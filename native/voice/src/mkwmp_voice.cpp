// mkwmp voice: the C ABI (mkwmp_voice.h) and the relay client (docs/contracts/VOICE_API.md §2, §5). One network
// thread: a UDP socket connected to the relay, control up on change and every 5 s, voice and state down handed to the
// engine (voice_audio.h). The engine runs only while the launcher's setting is on and the relay says this player is in
// a voice room; mutes and deafen are applied by the relay first and here again (the engine's second gate).
// Chat rooms and video (VOICE_API.md §6, VIDEO_API.md; mkwmp_voice.h's second part): in room mode the same thread says
// so on every control up, asks for the room and the members' pictures after it (op 5, op 6), keeps the relay's room
// (state down ver 2) and puts the members' frames together; the camera's own frames leave from the caller's thread.
#define MKWMP_VOICE_BUILD
#include "mkwmp_voice.h"

#include "voice_audio.h"
#include "voice_log.h"
#include "voice_video.h"
#include "voice_volume.h"
#include "voice_wire.h"

#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#else
#include <arpa/inet.h>
#include <fcntl.h>
#include <netdb.h>
#include <netinet/in.h>
#include <sys/select.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>
#endif

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <random>
#include <string>
#include <thread>
#include <vector>

#ifndef _WIN32
// POSIX sockets under Winsock's names: the relay client below is the same code on both.
using SOCKET = int;
constexpr SOCKET INVALID_SOCKET = -1;
inline int closesocket(SOCKET s) { return close(s); }
#endif

namespace wire = mkwmp::voice_wire;
namespace video = mkwmp::voice_video;
namespace audio = fi::audio;

namespace {

std::atomic<mkwmp_voice_log_fn> g_log{nullptr};

struct Settings {
    bool enabled = true;
    std::string input, output;
    int input_gain = 100, output_gain = 100;
};

struct Shared {
    std::mutex lock;
    // what the probe asked for
    std::string host, ticket;
    int port = 0;
    uint32_t target_serial = 0; // bumped by connect/disconnect
    Settings settings;
    std::vector<uint64_t> mutes;
    bool mic_muted = false, deafened = false;
    uint32_t control_serial = 0; // bumped when control up must go now
    uint64_t account = 0;
    // what the relay said
    wire::StateDown state;
    bool subscribed = false;
    // chat rooms: what the probe asked for
    bool room_mode = false, camera_on = false;
    std::string room_key, room_ticket;
    std::vector<uint64_t> room_want, video_want;
    uint32_t room_serial = 0; // bumped when the room request or the video subscription must go now
    mkwmp_voice_video_fn video_fn = nullptr;
    void *video_user = nullptr;
    // and what the relay said: 0 no state yet, else the version of its last state (1: a relay without rooms)
    int relay = 0;
};
Shared g;

// The camera's frames leave from the probe's thread: their numbers and what was sent lately.
struct VideoOut {
    std::mutex lock;
    video::Pacer pacer;
    uint16_t frame = 0;
    uint32_t sent = 0, held = 0;
    int64_t said = 0;
};
VideoOut g_video_out;

std::atomic<bool> g_thread_started{false};
std::atomic<SOCKET> g_socket{INVALID_SOCKET};

void Logf(const char *format, ...) {
    mkwmp_voice_log_fn log = g_log.load();
    if (!log) return;
    char line[512];
    va_list ap;
    va_start(ap, format);
    vsnprintf(line, sizeof line, format, ap);
    va_end(ap);
    log(line);
}

uint32_t Random32() {
    static thread_local std::mt19937 rng{std::random_device{}()};
    return rng();
}

#ifdef _WIN32
int64_t NowMs() { return static_cast<int64_t>(GetTickCount64()); }
void SleepMs(int ms) { Sleep(static_cast<DWORD>(ms)); }
#else
int64_t NowMs() {
    timespec ts{};
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return static_cast<int64_t>(ts.tv_sec) * 1000 + ts.tv_nsec / 1000000;
}
void SleepMs(int ms) { std::this_thread::sleep_for(std::chrono::milliseconds(ms)); }
#endif

bool Muted(const std::vector<uint64_t> &mutes, uint64_t account) { return std::find(mutes.begin(), mutes.end(), account) != mutes.end(); }

// The engine's send hook (engine thread): one voice up, straight out of the connected socket.
void SendVoice(const uint8_t *opus, size_t size, uint8_t flags, uint16_t seq, uint32_t ts) {
    const SOCKET s = g_socket.load();
    if (s == INVALID_SOCKET) return;
    const uint32_t r = Random32();
    const uint8_t random[4] = {static_cast<uint8_t>(r), static_cast<uint8_t>(r >> 8), static_cast<uint8_t>(r >> 16), static_cast<uint8_t>(r >> 24)};
    const std::vector<uint8_t> d = wire::EncodeVoiceUp(static_cast<uint8_t>(flags & wire::kEndOfSpurt), seq, ts, opus, size, random);
    if (!d.empty()) send(s, reinterpret_cast<const char *>(d.data()), static_cast<int>(d.size()), 0);
}

SOCKET Open(const std::string &host, int port) {
    addrinfo hints{}, *res = nullptr;
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_DGRAM;
    hints.ai_protocol = IPPROTO_UDP;
    char service[16];
    snprintf(service, sizeof service, "%d", port);
    if (getaddrinfo(host.c_str(), service, &hints, &res) != 0 || !res) {
        Logf("voice: cannot resolve the voice relay %s:%d", host.c_str(), port);
        return INVALID_SOCKET;
    }
    SOCKET s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    // connected: send() goes to the relay and recv() takes only the relay's datagrams (the source check)
    if (s != INVALID_SOCKET && connect(s, res->ai_addr, static_cast<int>(res->ai_addrlen)) != 0) {
        closesocket(s);
        s = INVALID_SOCKET;
    }
    if (s != INVALID_SOCKET) {
#ifdef _WIN32
        u_long nb = 1;
        ioctlsocket(s, FIONBIO, &nb);
        // Windows: an ICMP port unreachable must not turn every later recv into WSAECONNRESET
        BOOL off = FALSE;
        DWORD ret = 0;
        WSAIoctl(s, _WSAIOW(IOC_VENDOR, 12) /* SIO_UDP_CONNRESET */, &off, sizeof off, nullptr, 0, &ret, nullptr, nullptr);
#else
        // Linux: an ICMP port unreachable fails one recv (ECONNREFUSED) and nothing after; the loop just reads again
        const int fl = fcntl(s, F_GETFL, 0);
        fcntl(s, F_SETFL, (fl < 0 ? 0 : fl) | O_NONBLOCK);
        fcntl(s, F_SETFD, FD_CLOEXEC);
#endif
        char addr[64] = {};
        inet_ntop(AF_INET, &reinterpret_cast<sockaddr_in *>(res->ai_addr)->sin_addr, addr, sizeof addr);
        Logf("voice: relay %s:%d (%s)", host.c_str(), port, addr);
    }
    freeaddrinfo(res);
    return s;
}

void RandomTxid(uint8_t txid[12]) {
    for (int i = 0; i < 12; i += 4) { const uint32_t r = Random32(); std::memcpy(txid + i, &r, 4); }
}

// Under g.lock. The self bits of a control up: room mode's two only in room mode (a client out of it never sets them).
uint8_t SelfBits() {
    uint8_t self = static_cast<uint8_t>((g.mic_muted ? wire::kSelfMicMuted : 0) | (g.deafened ? wire::kSelfDeafened : 0));
    if (g.room_mode) {
        self |= wire::kSelfRoomMode | (g.camera_on ? wire::kSelfCameraOn : 0);
        if (!g.settings.enabled) self |= wire::kSelfMicMuted | wire::kSelfDeafened; // in the room for the camera only
    }
    return self;
}

void SendControl(SOCKET s, uint8_t op, const std::string &ticket, uint8_t self, std::vector<uint64_t> mutes) {
    if (s == INVALID_SOCKET || ticket.empty()) return;
    if (mutes.size() > wire::kMaxMutes) mutes.resize(wire::kMaxMutes);
    uint8_t txid[12];
    RandomTxid(txid);
    const std::vector<uint8_t> d = self & wire::kSelfRoomMode ? wire::EncodeControlUpRoom(op, self, mutes, ticket, txid)
                                                              : wire::EncodeControlUp(op, self, mutes, ticket, txid);
    if (d.empty()) { Logf("voice: control up refused by the encoder (ticket %zu bytes)", ticket.size()); return; }
    send(s, reinterpret_cast<const char *>(d.data()), static_cast<int>(d.size()), 0);
}

// Room mode, after a control up and when the probe's wishes change: the room asked for (op 5) and the pictures wanted
// (op 6). `asked` is the sharing key of the request the relay holds: once the game has no key any more, or nobody is
// wanted, one request with an empty want list leaves that room.
void SendRoom(SOCKET s, const std::string &key, std::vector<uint64_t> want, const std::string &room_ticket, std::vector<uint64_t> pictures,
              std::string &asked) {
    if (s == INVALID_SOCKET) return;
    uint8_t txid[12];
    if (want.size() > wire::kMaxRoomFriends) want.resize(wire::kMaxRoomFriends);
    if (pictures.size() > wire::kMaxRoomFriends) pictures.resize(wire::kMaxRoomFriends);
    std::vector<uint8_t> d;
    if (!key.empty() && !want.empty() && !room_ticket.empty()) {
        RandomTxid(txid);
        d = wire::EncodeRoomRequest(key, want, room_ticket, txid);
        if (d.empty()) Logf("voice: room request refused by the encoder (key %zu, ticket %zu bytes)", key.size(), room_ticket.size());
        else asked = key;
    } else if (!asked.empty()) {
        RandomTxid(txid);
        d = wire::EncodeRoomRequest(asked, {}, room_ticket.size() <= wire::kMaxRoomTicket ? room_ticket : std::string(), txid);
        asked.clear();
    }
    if (!d.empty()) send(s, reinterpret_cast<const char *>(d.data()), static_cast<int>(d.size()), 0);
    RandomTxid(txid);
    d = wire::EncodeVideoSubscribe(pictures, txid);
    if (!d.empty()) send(s, reinterpret_cast<const char *>(d.data()), static_cast<int>(d.size()), 0);
}

// Under g.lock: is this account in the room the relay last gave?
bool RoomMember(uint64_t account) {
    for (const wire::Member &m : g.state.members)
        if (m.account == account) return true;
    return false;
}

// The network thread: lives for the process (the probe loads the DLL once).
void NetLoop() {
#ifdef _WIN32
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);
#endif
    SOCKET s = INVALID_SOCKET;
    uint32_t target_seen = ~0u, control_seen = ~0u;
    std::string ticket, room_asked;
    uint32_t room_seen = ~0u;
    bool joined = false; // a subscribe went out on this socket
    video::Reassembler pictures;
    video::Frame picture;
    int64_t last_control = 0, last_rx_state = 0, last_engine_try = -100000, last_open_try = -100000;
    bool engine_wanted_logged = false;
    Settings applied;
    bool applied_once = false;
    std::vector<uint8_t> buf(2048);
    for (;;) {
        // 1. the target relay
        std::string host;
        int port = 0;
        uint32_t serial;
        {
            std::lock_guard lock(g.lock);
            serial = g.target_serial;
            host = g.host;
            port = g.port;
            ticket = g.ticket;
        }
        if (serial != target_seen || (s == INVALID_SOCKET && !host.empty() && NowMs() - last_open_try > 5000)) {
            if (serial != target_seen && s != INVALID_SOCKET) {
                uint8_t self;
                { std::lock_guard lock(g.lock); self = SelfBits(); }
                if (host.empty()) SendControl(s, wire::kLeave, ticket, self, {});
                g_socket = INVALID_SOCKET;
                if (audio::Running()) audio::Stop();
                closesocket(s);
                s = INVALID_SOCKET;
                joined = false;
                room_asked.clear();
                pictures.Reset();
                std::lock_guard lock(g.lock);
                g.subscribed = false;
                g.state = {};
                g.relay = 0;
            }
            target_seen = serial;
            if (!host.empty() && port > 0) {
                last_open_try = NowMs();
                s = Open(host, port);
                g_socket = s;
                control_seen = ~0u; // subscribe at once
            }
        }

        // 2. control up: on change, and every 5 s
        uint8_t self = 0;
        std::vector<uint64_t> mutes;
        Settings want;
        bool subscribed, room_mode, room_v2;
        uint64_t me;
        {
            std::lock_guard lock(g.lock);
            self = SelfBits();
            mutes = g.mutes;
            want = g.settings;
            subscribed = g.subscribed;
            me = g.account;
            room_mode = g.room_mode;
            bool refresh = false;
            if (s != INVALID_SOCKET && (g.control_serial != control_seen || NowMs() - last_control >= 5000)) {
                const bool first = control_seen == ~0u;
                control_seen = g.control_serial;
                last_control = NowMs();
                // in room mode the relay is joined with voice off too: the camera's room is there
                if (want.enabled || room_mode) {
                    SendControl(s, first ? wire::kSubscribe : wire::kReplaceMutes, ticket, self, mutes);
                    joined = refresh = true;
                } else if (g.subscribed || joined) {
                    SendControl(s, wire::kLeave, ticket, self, {});
                    joined = false;
                    g.subscribed = false;
                    g.state = {};
                    g.relay = 0;
                }
            }
            if (!room_mode) {
                room_asked.clear();
                room_seen = g.room_serial;
            } else if (s != INVALID_SOCKET && joined && g.relay != 1 && (refresh || g.room_serial != room_seen)) {
                room_seen = g.room_serial; // a relay without rooms (state ver 1) is not sent what it cannot read
                SendRoom(s, g.room_key, g.room_want, g.room_ticket, g.video_want, room_asked);
            }
        }

        // 3. what arrived
        if (s != INVALID_SOCKET) {
            fd_set rd;
            FD_ZERO(&rd);
            FD_SET(s, &rd);
            timeval tv{0, 50 * 1000};
#ifdef _WIN32
            const int nfds = 0; // ignored by Winsock
#else
            const int nfds = s + 1;
#endif
            if (select(nfds, &rd, nullptr, nullptr, &tv) > 0) {
                for (int i = 0; i < 256; i++) {
                    const int n = recv(s, reinterpret_cast<char *>(buf.data()), static_cast<int>(buf.size()), 0);
                    if (n <= 0) break;
                    const uint8_t *d = buf.data();
                    wire::VoiceDown v;
                    wire::StateDown st;
                    wire::VideoChunk chunk;
                    if (wire::DecodeVoiceDown(d, static_cast<size_t>(n), v)) {
                        if (audio::Running()) audio::Receive(v.seat, v.seq, v.ts, v.flags, v.opus, v.size);
                    } else if (wire::DecodeVideoDown(d, static_cast<size_t>(n), chunk)) {
                        // a member's picture: whole frames only, of a seat the relay's state names as a member of the room
                        if (!room_mode || !pictures.Feed(chunk, NowMs(), picture)) continue;
                        uint64_t account = 0;
                        mkwmp_voice_video_fn fn = nullptr;
                        void *user = nullptr;
                        {
                            std::lock_guard lock(g.lock);
                            for (const wire::Seat &seat : g.state.seats)
                                if (seat.seat == picture.seat) account = seat.account;
                            if (!g.room_mode || g.relay != wire::kVersionRooms || !account || account == g.account || !RoomMember(account)) account = 0;
                            fn = g.video_fn;
                            user = g.video_user;
                        }
                        if (account && fn) fn(account, picture.bytes.data(), static_cast<int>(picture.bytes.size()), user);
                    } else if (wire::DecodeStateDown(d, static_cast<size_t>(n), st)) {
                        std::lock_guard lock(g.lock);
                        const bool room = (st.room & wire::kRoomVoice) != 0;
                        if (room != g.subscribed || st.seats.size() != g.state.seats.size())
                            Logf("voice: %s (%zu seats, bundle %u)", room ? "in a voice room" : "not in a voice room yet", st.seats.size(), st.bundle);
                        if (g.room_mode) {
                            // the members are the others: this player is never one of its own room's
                            std::erase_if(st.members, [](const wire::Member &m) { return !m.account || m.account == g.account; });
                            const bool rooms = st.version == wire::kVersionRooms;
                            if (st.version != g.relay)
                                Logf(rooms ? "voice: the relay speaks chat rooms (state ver 2)"
                                           : "voice: the relay speaks no chat rooms (state ver 1): no room is granted, no camera is sent or shown; voice "
                                             "stays the lobby's");
                            if (rooms && (st.version != g.relay || st.room_id != g.state.room_id || st.members.size() != g.state.members.size() ||
                                          st.fps != g.state.fps)) {
                                int cameras = 0;
                                for (const wire::Member &m : st.members) cameras += m.flags & wire::kMemberCamera;
                                if (st.room_id)
                                    Logf("voice: chat room %#llx: %zu other member(s), %d with a camera on, send divisor %u",
                                         static_cast<unsigned long long>(st.room_id), st.members.size(), cameras, st.fps);
                                else Logf("voice: in no chat room (the relay waits for a friend who asks for this player too)");
                            }
                            if (st.version != g.relay) g.room_serial++; // the relay bound this endpoint: the room request once more
                            if (st.room_id != g.state.room_id) pictures.Reset();
                            g.relay = st.version;
                        } else {
                            g.relay = 0;
                        }
                        g.subscribed = room;
                        g.state = std::move(st);
                        last_rx_state = NowMs();
                    }
                }
            }
        } else {
            SleepMs(50);
        }

        // 4. a relay gone quiet (it sends state at least every 1 s): out of the room after 10 s
        {
            std::lock_guard lock(g.lock);
            if ((g.subscribed || g.relay) && NowMs() - last_rx_state > 10000) {
                Logf("voice: no state from the relay for 10 s; voice paused");
                g.subscribed = false;
                g.state = {};
                g.relay = 0;
            }
            subscribed = g.subscribed;
            // a relay with rooms routes voice inside the chat room only: without a room nobody would hear this player
            room_v2 = g.room_mode && g.relay == wire::kVersionRooms;
            if (room_v2 && !g.state.room_id) subscribed = false;
        }

        // 5. the engine: settings, start/stop, gates
        if (!applied_once || want.input != applied.input || want.output != applied.output) audio::SetDevices(want.input, want.output);
        audio::SetMicGain(audio::VolumeGain(audio::ValidVolume(want.input_gain)));
        audio::SetVolume(audio::VolumeGain(audio::ValidVolume(want.output_gain)));
        applied = want;
        applied_once = true;
        const bool run = want.enabled && subscribed && s != INVALID_SOCKET;
        if (run && !audio::Running() && NowMs() - last_engine_try > 5000) {
            last_engine_try = NowMs();
            if (audio::Start(SendVoice)) Logf("voice: engine started%s", audio::ListenOnly() ? " (no microphone: listen only)" : "");
            else if (!engine_wanted_logged) { Logf("voice: engine did not start; retrying every 5 s"); engine_wanted_logged = true; }
        } else if (!run && audio::Running()) {
            audio::Stop();
            Logf("voice: engine stopped");
        }
        if (audio::Running()) {
            audio::SetTransmit(!(self & wire::kSelfMicMuted), false);
            std::lock_guard lock(g.lock);
            bool listed[wire::kMaxSeats] = {};
            for (const wire::Seat &seat : g.state.seats) {
                listed[seat.seat] = true;
                audio::SetSilenced(seat.seat, g.deafened || seat.account == me || Muted(g.mutes, seat.account) ||
                                                  (room_v2 && !RoomMember(seat.account))); // the relay routes; this is the second gate
            }
            for (uint8_t i = 0; i < wire::kMaxSeats; i++)
                if (!listed[i]) audio::SetSilenced(i, true);
            if (g.state.bundle == 3 || g.state.bundle == 6) audio::SetBundle(g.state.bundle);
        }
    }
}

void EnsureThread() {
    if (!g_thread_started.exchange(true)) std::thread(NetLoop).detach();
}

void JsonString(std::string &out, const std::string &s) {
    out += '"';
    for (unsigned char c : s) {
        if (c == '"' || c == '\\') { out += '\\'; out += static_cast<char>(c); }
        else if (c < 0x20) { char e[8]; snprintf(e, sizeof e, "\\u%04x", c); out += e; }
        else out += static_cast<char>(c);
    }
    out += '"';
}

} // namespace

namespace fi {
void Log(const char *format, ...) {
    mkwmp_voice_log_fn log = g_log.load();
    if (!log) return;
    char line[512];
    va_list ap;
    va_start(ap, format);
    vsnprintf(line, sizeof line, format, ap);
    va_end(ap);
    log(line);
}
} // namespace fi

extern "C" {

MKWMP_VOICE_API int mkwmp_voice_abi(void) { return MKWMP_VOICE_ABI; }

MKWMP_VOICE_API void mkwmp_voice_init(mkwmp_voice_log_fn log) {
    g_log = log;
    EnsureThread();
}

MKWMP_VOICE_API void mkwmp_voice_connect(const char *host, int port, const char *ticket) {
    std::lock_guard lock(g.lock);
    const std::string h = host ? host : "", t = ticket ? ticket : "";
    if (h == g.host && port == g.port) {
        if (t != g.ticket) { g.ticket = t; g.control_serial++; }
        return;
    }
    g.host = h;
    g.port = port;
    g.ticket = t;
    g.target_serial++;
}

MKWMP_VOICE_API void mkwmp_voice_disconnect(void) {
    std::lock_guard lock(g.lock);
    if (g.host.empty()) return;
    g.host.clear();
    g.port = 0;
    g.target_serial++;
}

MKWMP_VOICE_API void mkwmp_voice_settings(int enabled, const char *input_id, const char *output_id, int input_gain, int output_gain) {
    std::lock_guard lock(g.lock);
    Settings s{enabled != 0, input_id ? input_id : "", output_id ? output_id : "", input_gain, output_gain};
    if (s.enabled != g.settings.enabled) g.control_serial++;
    g.settings = std::move(s);
}

MKWMP_VOICE_API void mkwmp_voice_set_mutes(const uint64_t *accounts, int count) {
    std::vector<uint64_t> m;
    for (int i = 0; accounts && i < count; i++)
        if (accounts[i] && !Muted(m, accounts[i])) m.push_back(accounts[i]);
    std::sort(m.begin(), m.end());
    std::lock_guard lock(g.lock);
    if (m != g.mutes) { g.mutes = std::move(m); g.control_serial++; }
}

MKWMP_VOICE_API void mkwmp_voice_set_self(int mic_muted, int deafened) {
    std::lock_guard lock(g.lock);
    if ((mic_muted != 0) != g.mic_muted || (deafened != 0) != g.deafened) {
        g.mic_muted = mic_muted != 0;
        g.deafened = deafened != 0;
        g.control_serial++;
    }
}

MKWMP_VOICE_API void mkwmp_voice_set_account(uint64_t account) {
    std::lock_guard lock(g.lock);
    g.account = account;
}

MKWMP_VOICE_API int mkwmp_voice_devices_json(char *buf, int cap) {
    std::string out = "{";
    for (int capture = 1; capture >= 0; capture--) {
        out += capture ? "\"inputs\":[" : ",\"outputs\":[";
        bool first = true;
        for (const audio::Device &d : audio::ListDevices(capture != 0)) {
            if (d.id.empty()) continue; // "System default" is the launcher's own first entry
            if (!first) out += ',';
            first = false;
            out += "{\"id\":";
            JsonString(out, d.id);
            out += ",\"name\":";
            JsonString(out, d.name);
            out += '}';
        }
        out += ']';
    }
    out += '}';
    if (!buf || cap <= static_cast<int>(out.size())) return -1;
    std::memcpy(buf, out.c_str(), out.size() + 1);
    return static_cast<int>(out.size());
}

MKWMP_VOICE_API int mkwmp_voice_peers(mkwmp_voice_peer *out, int cap) {
    std::lock_guard lock(g.lock);
    int n = 0;
    for (const wire::Seat &s : g.state.seats) {
        if (n >= cap) break;
        const bool self = s.account == g.account;
        const bool muted = Muted(g.mutes, s.account);
        const bool talking = self ? audio::Sending() : (audio::Running() && !muted && audio::SeatTalking(s.seat));
        out[n++] = {s.account, s.seat, static_cast<uint8_t>(talking), static_cast<uint8_t>(muted), static_cast<uint8_t>(self)};
    }
    return n;
}

MKWMP_VOICE_API int mkwmp_voice_status(void) {
    int st = 0;
    { std::lock_guard lock(g.lock); if (g.subscribed) st |= MKWMP_VOICE_SUBSCRIBED; }
    if (audio::Running()) st |= MKWMP_VOICE_RUNNING;
    if (audio::Sending()) st |= MKWMP_VOICE_SENDING;
    if (audio::ListenOnly()) st |= MKWMP_VOICE_LISTEN_ONLY;
    return st;
}

// ---- chat rooms and video

MKWMP_VOICE_API int mkwmp_voice_room_api(void) { return MKWMP_VOICE_ROOM_API; }

MKWMP_VOICE_API void mkwmp_voice_room_mode(int on) {
    std::lock_guard lock(g.lock);
    if ((on != 0) == g.room_mode) return;
    g.room_mode = on != 0;
    g.control_serial++; // self bit 3 from the next control up on, or never again
    g.room_serial++;
    g.relay = 0;
    Logf("voice: room mode %s", on ? "on: voice and the camera are shared with the chat room only" : "off");
}

MKWMP_VOICE_API void mkwmp_voice_room_request(const char *sharing_key, const uint64_t *want, int count, const char *room_ticket) {
    std::string key = sharing_key ? sharing_key : "", ticket = room_ticket ? room_ticket : "";
    if (key.size() > wire::kMaxSharingKey) key.resize(wire::kMaxSharingKey);
    std::vector<uint64_t> w;
    for (int i = 0; want && i < count && w.size() < wire::kMaxRoomFriends; i++)
        if (want[i] && !Muted(w, want[i])) w.push_back(want[i]);
    std::lock_guard lock(g.lock);
    if (key == g.room_key && w == g.room_want && ticket == g.room_ticket) return;
    g.room_key = std::move(key);
    g.room_want = std::move(w);
    g.room_ticket = std::move(ticket);
    g.room_serial++;
}

MKWMP_VOICE_API void mkwmp_voice_room_camera(int on) {
    std::lock_guard lock(g.lock);
    if ((on != 0) == g.camera_on) return;
    g.camera_on = on != 0;
    if (g.room_mode) g.control_serial++;
}

MKWMP_VOICE_API void mkwmp_voice_video_subscribe(const uint64_t *accounts, int count) {
    std::vector<uint64_t> w;
    for (int i = 0; accounts && i < count && w.size() < wire::kMaxRoomFriends; i++)
        if (accounts[i] && !Muted(w, accounts[i])) w.push_back(accounts[i]);
    std::lock_guard lock(g.lock);
    if (w == g.video_want) return;
    g.video_want = std::move(w);
    g.room_serial++;
}

MKWMP_VOICE_API int mkwmp_voice_video_send(const uint8_t *jpeg, int len, uint32_t ts_ms) {
    const SOCKET s = g_socket.load();
    if (s == INVALID_SOCKET || !jpeg || len <= 0) return 0;
    int divisor;
    {
        std::lock_guard lock(g.lock); // the relay takes video only from a member of a room whose camera bit is on
        if (!g.room_mode || !g.camera_on || g.relay != wire::kVersionRooms || !g.state.room_id || g.state.members.empty()) return 0;
        divisor = g.state.fps;
    }
    std::lock_guard lock(g_video_out.lock);
    const std::vector<std::vector<uint8_t>> out =
        video::ChunkFrame(jpeg, static_cast<size_t>(len), g_video_out.frame, ts_ms, [](size_t) { return Random32(); });
    size_t bytes = 0;
    for (const std::vector<uint8_t> &d : out) bytes += d.size();
    const int64_t now = NowMs();
    const bool go = !out.empty() && g_video_out.pacer.Admit(now, bytes, out.size(), divisor);
    if (go) {
        g_video_out.frame++;
        g_video_out.sent++;
        for (const std::vector<uint8_t> &d : out) send(s, reinterpret_cast<const char *>(d.data()), static_cast<int>(d.size()), 0);
    } else {
        g_video_out.held++;
    }
    if (now - g_video_out.said > 30000) { // a line every half minute while the camera is sent
        Logf("voice: camera: %u frame(s) sent, %u held back (the sender's limits, or the relay's divisor %d); the last one %d bytes in %zu "
             "datagram(s)", g_video_out.sent, g_video_out.held, divisor, len, out.size());
        g_video_out.said = now;
        g_video_out.sent = g_video_out.held = 0;
    }
    return go ? 1 : 0;
}

MKWMP_VOICE_API void mkwmp_voice_video_receiver(mkwmp_voice_video_fn fn, void *user) {
    std::lock_guard lock(g.lock);
    g.video_fn = fn;
    g.video_user = user;
}

MKWMP_VOICE_API int mkwmp_voice_room_state(mkwmp_voice_room *out) {
    if (!out) return 0;
    std::memset(out, 0, sizeof *out);
    std::lock_guard lock(g.lock);
    if (!g.room_mode || !g.relay) return 0;
    out->relay = g.relay;
    out->fps_divisor = g.state.fps == 2 || g.state.fps == 4 ? g.state.fps : 1;
    if (g.relay != wire::kVersionRooms) return 1;
    out->room_id = g.state.room_id;
    for (const wire::Member &m : g.state.members) {
        if (out->count >= 4) break;
        out->member[out->count++] = {m.account, static_cast<uint8_t>((m.flags & wire::kMemberCamera) != 0),
                                     static_cast<uint8_t>((m.flags & wire::kMemberVoice) != 0)};
    }
    return 1;
}

} // extern "C"
