// mkwmp voice: the C ABI (mkwmp_voice.h) and the relay client (docs/contracts/VOICE_API.md §2, §5). One network
// thread: a UDP socket connected to the relay, control up on change and every 5 s, voice and state down handed to the
// engine (voice_audio.h). The engine runs only while the launcher's setting is on and the relay says this player is in
// a voice room; mutes and deafen are applied by the relay first and here again (the engine's second gate).
#define MKWMP_VOICE_BUILD
#include "mkwmp_voice.h"

#include "voice_audio.h"
#include "voice_log.h"
#include "voice_volume.h"
#include "voice_wire.h"

#include <winsock2.h>
#include <ws2tcpip.h>

#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <random>
#include <string>
#include <thread>
#include <vector>

namespace wire = mkwmp::voice_wire;
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
};
Shared g;

std::atomic<bool> g_thread_started{false};
std::atomic<SOCKET> g_socket{INVALID_SOCKET};
std::atomic<uint32_t> g_rand{0};

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

int64_t NowMs() { return static_cast<int64_t>(GetTickCount64()); }

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
        u_long nb = 1;
        ioctlsocket(s, FIONBIO, &nb);
        // Windows: an ICMP port unreachable must not turn every later recv into WSAECONNRESET
        BOOL off = FALSE;
        DWORD ret = 0;
        WSAIoctl(s, _WSAIOW(IOC_VENDOR, 12) /* SIO_UDP_CONNRESET */, &off, sizeof off, nullptr, 0, &ret, nullptr, nullptr);
        char addr[64] = {};
        inet_ntop(AF_INET, &reinterpret_cast<sockaddr_in *>(res->ai_addr)->sin_addr, addr, sizeof addr);
        Logf("voice: relay %s:%d (%s)", host.c_str(), port, addr);
    }
    freeaddrinfo(res);
    return s;
}

void SendControl(SOCKET s, uint8_t op, const std::string &ticket, uint8_t self, std::vector<uint64_t> mutes) {
    if (s == INVALID_SOCKET || ticket.empty()) return;
    if (mutes.size() > wire::kMaxMutes) mutes.resize(wire::kMaxMutes);
    uint8_t txid[12];
    for (int i = 0; i < 12; i += 4) { const uint32_t r = Random32(); std::memcpy(txid + i, &r, 4); }
    const std::vector<uint8_t> d = wire::EncodeControlUp(op, self, mutes, ticket, txid);
    if (d.empty()) { Logf("voice: control up refused by the encoder (ticket %zu bytes)", ticket.size()); return; }
    send(s, reinterpret_cast<const char *>(d.data()), static_cast<int>(d.size()), 0);
}

// The network thread: lives for the process (the probe loads the DLL once).
void NetLoop() {
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);
    SOCKET s = INVALID_SOCKET;
    uint32_t target_seen = ~0u, control_seen = ~0u;
    std::string ticket;
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
                { std::lock_guard lock(g.lock); self = static_cast<uint8_t>((g.mic_muted ? 1 : 0) | (g.deafened ? 2 : 0)); }
                if (host.empty()) SendControl(s, wire::kLeave, ticket, self, {});
                g_socket = INVALID_SOCKET;
                if (audio::Running()) audio::Stop();
                closesocket(s);
                s = INVALID_SOCKET;
                std::lock_guard lock(g.lock);
                g.subscribed = false;
                g.state = {};
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
        bool subscribed;
        uint64_t me;
        {
            std::lock_guard lock(g.lock);
            self = static_cast<uint8_t>((g.mic_muted ? wire::kSelfMicMuted : 0) | (g.deafened ? wire::kSelfDeafened : 0));
            mutes = g.mutes;
            want = g.settings;
            subscribed = g.subscribed;
            me = g.account;
            if (s != INVALID_SOCKET && (g.control_serial != control_seen || NowMs() - last_control >= 5000)) {
                const bool first = control_seen == ~0u;
                control_seen = g.control_serial;
                last_control = NowMs();
                if (want.enabled) SendControl(s, first ? wire::kSubscribe : wire::kReplaceMutes, ticket, self, mutes);
                else if (g.subscribed) { SendControl(s, wire::kLeave, ticket, self, {}); g.subscribed = false; g.state = {}; }
            }
        }

        // 3. what arrived
        if (s != INVALID_SOCKET) {
            fd_set rd;
            FD_ZERO(&rd);
            FD_SET(s, &rd);
            timeval tv{0, 50 * 1000};
            if (select(0, &rd, nullptr, nullptr, &tv) > 0) {
                for (int i = 0; i < 256; i++) {
                    const int n = recv(s, reinterpret_cast<char *>(buf.data()), static_cast<int>(buf.size()), 0);
                    if (n <= 0) break;
                    const uint8_t *d = buf.data();
                    wire::VoiceDown v;
                    wire::StateDown st;
                    if (wire::DecodeVoiceDown(d, static_cast<size_t>(n), v)) {
                        if (audio::Running()) audio::Receive(v.seat, v.seq, v.ts, v.flags, v.opus, v.size);
                    } else if (wire::DecodeStateDown(d, static_cast<size_t>(n), st)) {
                        std::lock_guard lock(g.lock);
                        if (!g.subscribed) Logf("voice: in a voice room (%zu seats, bundle %u)", st.seats.size(), st.bundle);
                        g.subscribed = (st.room & wire::kRoomVoice) != 0;
                        g.state = std::move(st);
                        last_rx_state = NowMs();
                    }
                }
            }
        } else {
            Sleep(50);
        }

        // 4. a relay gone quiet (it sends state at least every 1 s): out of the room after 10 s
        {
            std::lock_guard lock(g.lock);
            if (g.subscribed && NowMs() - last_rx_state > 10000) {
                Logf("voice: no state from the relay for 10 s; voice paused");
                g.subscribed = false;
                g.state = {};
            }
            subscribed = g.subscribed;
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
                audio::SetSilenced(seat.seat, g.deafened || seat.account == me || Muted(g.mutes, seat.account));
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

} // extern "C"
