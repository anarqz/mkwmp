// mkwmp voice: friend rooms and video/v1 end to end (VOICE_API.md §6, VIDEO_API.md), with no server: two copies of the
// built library (two players, each with its own network thread and socket) and a small relay in this file that does
// what the contract says a relay does: binds on op 1/2, knows by itself which friend room each player is in (the test
// says: 0 is a matchmade lobby) and who is whose mutual friend, answers state down ver 2 with the room and the
// listener's mutual friends bound in it, in seat order (ver 1 to a player not in room mode, and to everybody as an
// "old relay"), and forwards the video of a player with its camera bit on to the mutual friends in its room that
// subscribed to it. Voice chat is off in both players' settings (no audio device is opened) until the last part, which
// turns it on for one player to see the engine run in a friend room and stop outside one; a PC without an audio
// device skips that part and says so.
//
//   voice_room_test <the library> <a folder for its two copies>        (build.py --test runs it)
#include "mkwmp_voice.h"
#include "voice_wire.h"

#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#else
#include <arpa/inet.h>
#include <dlfcn.h>
#include <netinet/in.h>
#include <sys/select.h>
#include <sys/socket.h>
#include <unistd.h>
using SOCKET = int;
constexpr SOCKET INVALID_SOCKET = -1;
inline int closesocket(SOCKET s) { return close(s); }
#endif

#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <deque>
#include <fstream>
#include <map>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace wire = mkwmp::voice_wire;

static int failures = 0;
static void Check(bool ok, const char *what) {
    std::printf("%s %s\n", ok ? "ok  " : "FAIL", what);
    std::fflush(stdout);
    if (!ok) failures++;
}

static int64_t NowMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}
static void SleepMs(int ms) { std::this_thread::sleep_for(std::chrono::milliseconds(ms)); }
template <typename F> static bool Until(int ms, F done) {
    for (const int64_t end = NowMs() + ms; NowMs() < end; SleepMs(10))
        if (done()) return true;
    return done();
}

// ---- the library, twice

struct Player {
    const char *name = "";
    uint64_t account = 0;
    void (*init)(mkwmp_voice_log_fn) = nullptr;
    void (*connect)(const char *, int, const char *);
    void (*settings)(int, const char *, const char *, int, int);
    void (*set_account)(uint64_t);
    int (*status)(void);
    int (*room_api)(void);
    void (*room_mode)(int);

    void (*room_camera)(int);
    void (*video_subscribe)(const uint64_t *, int);
    int (*video_send)(const uint8_t *, int, uint32_t);
    void (*video_receiver)(mkwmp_voice_video_fn, void *);
    int (*room_state)(mkwmp_voice_room *);
    // what its receiver got
    std::mutex lock;
    std::vector<std::pair<uint64_t, std::vector<uint8_t>>> frames;
};

static void *Sym(void *lib, const char *name) {
#ifdef _WIN32
    return reinterpret_cast<void *>(GetProcAddress(static_cast<HMODULE>(lib), name));
#else
    return dlsym(lib, name);
#endif
}

static bool Load(Player &p, const std::string &library, const std::string &copy) {
    { // its own copy of the file: its own globals, thread and socket
        std::ifstream in(library, std::ios::binary);
        std::ofstream out(copy, std::ios::binary | std::ios::trunc);
        out << in.rdbuf();
        if (!in || !out) return false;
    }
#ifdef _WIN32
    void *lib = LoadLibraryA(copy.c_str());
#else
    void *lib = dlopen(copy.c_str(), RTLD_NOW | RTLD_LOCAL);
#endif
    if (!lib) return false;
#define SYM(f) *reinterpret_cast<void **>(&p.f) = Sym(lib, "mkwmp_voice_" #f)
    SYM(init); SYM(connect); SYM(settings); SYM(set_account); SYM(status); SYM(room_api); SYM(room_mode);
    SYM(room_camera); SYM(video_subscribe); SYM(video_send); SYM(video_receiver); SYM(room_state);
#undef SYM
    return p.init && p.connect && p.settings && p.set_account && p.status && p.room_api && p.room_mode && p.room_camera &&
           p.video_subscribe && p.video_send && p.video_receiver && p.room_state;
}

static void Received(uint64_t account, const uint8_t *jpeg, int len, void *user) {
    Player *p = static_cast<Player *>(user);
    std::lock_guard lock(p->lock);
    p->frames.push_back({account, std::vector<uint8_t>(jpeg, jpeg + len)});
}
static size_t Count(Player &p) {
    std::lock_guard lock(p.lock);
    return p.frames.size();
}
static void LibraryLog(const char *line) { std::printf("     | %s\n", line); std::fflush(stdout); }

// A frame the receiver can check: a JPEG's first bytes, its number, then bytes that follow from the number.
static std::vector<uint8_t> Picture(uint32_t number, size_t size) {
    std::vector<uint8_t> v(size);
    uint32_t x = number * 2654435761u + 12345;
    for (size_t i = 0; i < size; i++) { x = x * 1664525u + 1013904223u; v[i] = static_cast<uint8_t>(x >> 24); }
    v[0] = 0xff, v[1] = 0xd8;
    std::memcpy(v.data() + 2, &number, 4);
    return v;
}
static bool Right(const std::vector<uint8_t> &frame) {
    uint32_t number = 0;
    if (frame.size() < 6) return false;
    std::memcpy(&number, frame.data() + 2, 4);
    return frame == Picture(number, frame.size());
}

// ---- the relay

struct Relay {
    struct Client {
        sockaddr_in addr{};
        bool ghost = false; // a player the test made up: in the states, with no endpoint
        uint64_t account = 0;
        uint8_t seat = 0, self = 0;
        std::vector<uint64_t> pictures;
        // what the test asks about
        int controls = 0, plain_controls = 0, subscriptions = 0, leaves = 0, video_in = 0, video_refused = 0, voice_in = 0;
        uint8_t last_op = 0, last_self = 0;
        std::deque<std::pair<int64_t, size_t>> video_bytes;
        size_t worst_second = 0, worst_datagrams = 0;
    };
    std::mutex lock;
    std::map<uint16_t, Client> clients; // by source port (ghosts: 1, 2, ...)
    std::map<std::string, uint64_t> tickets;
    // what the relay knows by itself (the test sets it, under `lock`): each account's friend room (none or 0: a
    // matchmade lobby) and the pairs of mutual friends
    std::map<uint64_t, uint64_t> rooms;
    std::vector<std::pair<uint64_t, uint64_t>> friends;
    std::atomic<bool> old{false}, lossy{false}, stop{false}, voice{false};
    SOCKET s = INVALID_SOCKET;
    int port = 0;
    std::thread thread;
    int video_seen = 0, unread = 0; // unread: 0x3E94 datagrams that are no op 1, 2, 3 or 6
    std::vector<uint8_t> held; // lossy: one datagram kept back, sent after the next (a swap)

    bool Open() {
        s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        sockaddr_in a{};
        a.sin_family = AF_INET;
        a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        if (s == INVALID_SOCKET || bind(s, reinterpret_cast<sockaddr *>(&a), sizeof a) != 0) return false;
        socklen_t n = sizeof a;
        getsockname(s, reinterpret_cast<sockaddr *>(&a), &n);
        port = ntohs(a.sin_port);
        thread = std::thread([this] { Run(); });
        return true;
    }
    void Close() {
        stop = true;
        if (thread.joinable()) thread.join();
        closesocket(s);
    }
    Client *Of(uint64_t account) {
        for (auto &[port_, c] : clients)
            if (c.account == account) return &c;
        return nullptr;
    }
    static bool Holds(const std::vector<uint64_t> &v, uint64_t a) {
        for (uint64_t x : v)
            if (x == a) return true;
        return false;
    }
    uint64_t RoomOf(const Client &c) const {
        const auto it = rooms.find(c.account);
        return old || it == rooms.end() ? 0 : it->second;
    }
    bool Friends(uint64_t a, uint64_t b) const {
        for (const auto &[x, y] : friends)
            if ((x == a && y == b) || (x == b && y == a)) return true;
        return false;
    }
    // The contract's rule for the camera between two players: the same friend room, and mutual friends.
    bool Together(const Client &a, const Client &b) const { return RoomOf(a) && RoomOf(a) == RoomOf(b) && Friends(a.account, b.account); }
    void SendState() {
        std::vector<const Client *> seated; // in seat order, as the contract lists seats and members
        for (auto &[port_, c] : clients) seated.push_back(&c);
        for (size_t i = 0; i < seated.size(); i++)
            for (size_t k = i + 1; k < seated.size(); k++)
                if (seated[k]->seat < seated[i]->seat) std::swap(seated[i], seated[k]);
        for (auto &[port_, c] : clients) {
            if (c.ghost) continue;
            wire::StateDown st;
            st.room = voice ? wire::kRoomVoice : 0; // 0, "no voice in this lobby yet": the audio engine never starts, whatever the settings say
            for (const Client *o : seated) st.seats.push_back({o->seat, o->account, 0});
            if (!old && (c.self & wire::kSelfRoomMode)) {
                st.version = wire::kVersionRooms;
                st.room_id = RoomOf(c);
                for (const Client *o : seated)
                    if (o != &c && (o->self & wire::kSelfRoomMode) && Together(c, *o)) // (a v1 friend would be listed too: not played here)
                        st.members.push_back({o->account, static_cast<uint8_t>((o->self & wire::kSelfCameraOn ? wire::kMemberCamera : 0) | wire::kMemberBound)});
            }
            const uint8_t txid[12] = {};
            const auto d = wire::EncodeStateDown(st, txid);
            sendto(s, reinterpret_cast<const char *>(d.data()), static_cast<int>(d.size()), 0, reinterpret_cast<const sockaddr *>(&c.addr), sizeof c.addr);
        }
    }
    void Forward(const std::vector<uint8_t> &d, const Client &to) {
        if (to.ghost) return;
        if (lossy) {
            const int n = video_seen++;
            if (n % 5 == 2) return;                      // lost
            if (n % 5 == 0) { held = d; return; }        // kept back: it goes after the next one
            sendto(s, reinterpret_cast<const char *>(d.data()), static_cast<int>(d.size()), 0, reinterpret_cast<const sockaddr *>(&to.addr), sizeof to.addr);
            if (!held.empty()) {
                sendto(s, reinterpret_cast<const char *>(held.data()), static_cast<int>(held.size()), 0, reinterpret_cast<const sockaddr *>(&to.addr),
                       sizeof to.addr);
                held.clear();
            }
            return;
        }
        sendto(s, reinterpret_cast<const char *>(d.data()), static_cast<int>(d.size()), 0, reinterpret_cast<const sockaddr *>(&to.addr), sizeof to.addr);
    }
    void Run() {
        std::vector<uint8_t> buf(2048);
        int64_t last_state = 0;
        while (!stop) {
            fd_set rd;
            FD_ZERO(&rd);
            FD_SET(s, &rd);
            timeval tv{0, 20 * 1000};
            const bool ready = select(static_cast<int>(s) + 1, &rd, nullptr, nullptr, &tv) > 0;
            std::lock_guard guard(lock);
            if (ready) {
                sockaddr_in from{};
                socklen_t fl = sizeof from;
                const int n = recvfrom(s, reinterpret_cast<char *>(buf.data()), static_cast<int>(buf.size()), 0, reinterpret_cast<sockaddr *>(&from), &fl);
                const uint16_t port_ = ntohs(from.sin_port);
                wire::ControlUpMessage m;
                wire::VideoChunk v;
                if (n > 0 && wire::DecodeControlUp(buf.data(), static_cast<size_t>(n), m)) {
                    auto it = clients.find(port_);
                    if (m.op == wire::kSubscribe || m.op == wire::kReplaceMutes) {
                        if (!tickets.count(m.ticket)) continue;
                        if (it == clients.end()) {
                            it = clients.emplace(port_, Client{}).first;
                            it->second.addr = from;
                            it->second.account = tickets[m.ticket];
                            it->second.seat = static_cast<uint8_t>(clients.size() + 2); // seats 3, 4: not the map's order
                        }
                        Client &c = it->second;
                        c.controls++;
                        c.plain_controls += !(m.self & wire::kSelfRoomMode);
                        c.self = m.self;
                        if (!(m.self & wire::kSelfRoomMode)) c.pictures.clear(); // out of room mode: its subscriptions are dropped
                    } else if (it == clients.end()) {
                        continue; // only a bound endpoint is heard
                    } else if (m.op == wire::kLeave) {
                        it->second.leaves++;
                        it->second.self = m.self;
                        it->second.pictures.clear();
                        it->second.last_op = m.op, it->second.last_self = m.self;
                        SendState();
                        continue;
                    } else if (old) {
                        it->second.subscriptions += m.op == wire::kVideoSubscribe; // an old relay drops what it cannot read
                        continue;
                    } else if (m.op == wire::kVideoSubscribe) {
                        it->second.subscriptions++;
                        it->second.pictures = m.accounts;
                    }
                    it->second.last_op = m.op, it->second.last_self = m.self;
                    SendState();
                    last_state = NowMs();
                } else if (n > 0 && wire::DecodeVideoUp(buf.data(), static_cast<size_t>(n), v)) {
                    auto it = clients.find(port_);
                    if (it == clients.end()) continue;
                    Client &c = it->second;
                    c.video_in++;
                    const int64_t now = NowMs();
                    c.video_bytes.push_back({now, static_cast<size_t>(n)});
                    while (now - c.video_bytes.front().first >= 1000) c.video_bytes.pop_front();
                    size_t sum = 0;
                    for (auto &e : c.video_bytes) sum += e.second;
                    if (sum > c.worst_second) c.worst_second = sum;
                    if (c.video_bytes.size() > c.worst_datagrams) c.worst_datagrams = c.video_bytes.size();
                    // taken only from a room-mode player in a friend room with its camera bit on
                    const bool taken = (c.self & wire::kSelfRoomMode) && (c.self & wire::kSelfCameraOn) && RoomOf(c);
                    for (auto &[p2, o] : clients)
                        if (taken && &o != &c && (o.self & wire::kSelfRoomMode) && Together(c, o) && Holds(o.pictures, c.account))
                            Forward(wire::EncodeVideoDown(v.flags, v.frame, v.chunk, v.chunks, v.ts, v.codec, c.seat, v.data, v.size), o);
                    c.video_refused += !taken;
                } else if (n >= 20 && wire::Rd16(buf.data()) == wire::kVoiceKind) {
                    auto it = clients.find(port_);
                    if (it != clients.end()) it->second.voice_in++;
                } else if (n >= 20 && wire::Rd16(buf.data()) == wire::kControlKind) {
                    unread++;
                }
            }
            if (NowMs() - last_state >= 200) {
                SendState();
                last_state = NowMs();
            }
        }
    }
};

static bool State(Player &p, mkwmp_voice_room &r) { return p.room_state(&r) != 0; }

int main(int argc, char **argv) {
    if (argc < 3) {
        std::printf("usage: voice_room_test <the voice library> <a folder for its two copies>\n");
        return 2;
    }
#ifdef _WIN32
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);
    const char *suffix = ".dll";
#else
    const char *suffix = ".so";
#endif
    const uint64_t kAna = 0x0123456789abcdefull, kBia = 0x0fedcba987654321ull, kRoom = 0x7700000000000001ull;
    Player ana, bia;
    ana.name = "Ana", ana.account = kAna;
    bia.name = "Bia", bia.account = kBia;
    if (!Load(ana, argv[1], std::string(argv[2]) + "/voice-room-test-a" + suffix) || !Load(bia, argv[1], std::string(argv[2]) + "/voice-room-test-b" + suffix)) {
        std::printf("FAIL the library %s did not load twice, or lacks the room API\n", argv[1]);
        return 1;
    }
    Check(MKWMP_VOICE_ROOM_API == 2 && ana.room_api() == MKWMP_VOICE_ROOM_API && bia.room_api() == MKWMP_VOICE_ROOM_API, "the library has room API 2");

    Relay relay;
    relay.tickets = {{"mkw1.ana", kAna}, {"mkw1.bia", kBia}};
    if (!relay.Open()) {
        std::printf("FAIL no relay socket\n");
        return 1;
    }
    auto room = [&](uint64_t account, uint64_t id) { std::lock_guard lock(relay.lock); relay.rooms[account] = id; };
    auto befriend = [&](uint64_t a, uint64_t b, bool on) {
        std::lock_guard lock(relay.lock);
        std::erase_if(relay.friends, [&](const auto &f) { return (f.first == a && f.second == b) || (f.first == b && f.second == a); });
        if (on) relay.friends.push_back({a, b});
    };
    for (Player *p : {&ana, &bia}) {
        p->init(LibraryLog);
        p->settings(0, "", "", 100, 100); // voice chat off: the relay is joined for the camera, no audio device is opened
        p->set_account(p->account);
        p->video_receiver(Received, p);
        p->room_mode(1);
    }
    mkwmp_voice_room ra{}, rb{};
    Check(!State(ana, ra) && ra.relay == 0 && ra.count == 0, "before the relay answered: no room state");
    Check(!ana.video_send(Picture(0, 3000).data(), 3000, 0), "no relay: no frame leaves");

    // ---- a matchmade lobby: the relay speaks rooms and says "no room", even to two mutual friends
    befriend(kAna, kBia, true);
    ana.room_camera(1);
    ana.connect("127.0.0.1", relay.port, "mkw1.ana");
    bia.connect("127.0.0.1", relay.port, "mkw1.bia");
    Check(Until(3000, [&] { return State(ana, ra) && State(bia, rb) && ra.relay == 2 && rb.relay == 2; }) && !ra.room_id && !rb.room_id && !ra.count && !rb.count,
          "a matchmade lobby: the relay speaks rooms, room_id 0 and nobody listed, friends or not");
    Check(!ana.video_send(Picture(1, 3000).data(), 3000, 0), "in no friend room: no frame leaves");

    // ---- a friend room: the camera is between mutual friends only
    befriend(kAna, kBia, false);
    room(kAna, kRoom);
    room(kBia, kRoom);
    Check(Until(3000, [&] { return State(ana, ra) && State(bia, rb) && ra.room_id == kRoom && rb.room_id == kRoom; }) && !ra.count && !rb.count,
          "the same friend room, not friends of each other: the room, and no one to share a camera with");
    Check(!ana.video_send(Picture(1, 3000).data(), 3000, 0), "no mutual friend in the room: no frame leaves");
    befriend(kAna, kBia, true);
    Check(Until(3000, [&] { return State(ana, ra) && State(bia, rb) && ra.count == 1 && rb.count == 1; }) && ra.room_id == rb.room_id &&
              ra.member[0].account == kBia && rb.member[0].account == kAna && ra.member[0].bound && rb.member[0].bound,
          "mutual friends in the room: each one's list holds the other, never itself");
    Check(Until(2000, [&] { return State(bia, rb) && rb.member[0].camera == 1; }) && State(ana, ra) && ra.member[0].camera == 0 && ra.fps_divisor == 1,
          "the camera flags: Ana's on, Bia's off; send divisor 1");
    Check(!(ana.status() & MKWMP_VOICE_RUNNING) && !(bia.status() & MKWMP_VOICE_RUNNING), "voice chat off in the settings: no audio engine");
    {
        std::lock_guard lock(relay.lock);
        Relay::Client *a = relay.Of(kAna), *b = relay.Of(kBia);
        Check(a && b && a->plain_controls == 0 && b->plain_controls == 0 && a->controls > 0 && b->controls > 0,
              "every op 1/2 of a player in room mode carries self bit 3");
        Check(a && b && (a->self & wire::kSelfCameraOn) && !(b->self & wire::kSelfCameraOn) && (a->self & 3) == 3,
              "self: bit 2 is the camera; with voice off the player is muted and deafened");
        Check(a && b && a->subscriptions >= 1 && b->subscriptions >= 1 && relay.unread == 0,
              "op 6 followed the subscribe, and nothing else was asked of the relay (no op 5)");
    }

    // ---- Ana's camera to Bia
    for (uint32_t i = 0; i < 3; i++) ana.video_send(Picture(i, 5000).data(), 5000, i);
    SleepMs(300);
    {
        std::lock_guard lock(relay.lock);
        Check(Count(bia) == 0 && relay.Of(kAna)->video_in == 15 && relay.Of(kAna)->video_refused == 0,
              "Bia did not subscribe to Ana's pictures: the relay takes them and none comes");
    }
    bia.video_subscribe(&kAna, 1);
    Check(Until(2000, [&] { std::lock_guard lock(relay.lock); return relay.Of(kBia)->pictures == std::vector<uint64_t>{kAna}; }),
          "op 6 reached the relay when the subscription changed");
    SleepMs(2100); // the three frames above are out of the pacer's two seconds
    int sent = 0;
    for (uint32_t i = 100; i < 120; i++) {
        sent += ana.video_send(Picture(i, 6000).data(), 6000, i * 100);
        SleepMs(100);
    }
    Check(sent == 20, "twenty frames of 6000 bytes at ten a second: all sent");
    Check(Until(1000, [&] { return Count(bia) >= 20; }) && Count(bia) == 20, "Bia received the twenty");
    {
        std::lock_guard lock(bia.lock);
        bool right = true, ordered = true;
        uint32_t last = 0;
        for (auto &[account, frame] : bia.frames) {
            uint32_t number = 0;
            std::memcpy(&number, frame.data() + 2, 4);
            right &= account == kAna && frame.size() == 6000 && Right(frame);
            ordered &= number > last;
            last = number;
        }
        Check(right && ordered, "each one whole, byte for byte, from Ana's account (the seat of the relay's state), in order");
        bia.frames.clear();
    }
    Check(Count(ana) == 0, "nothing comes back to the sender");

    // ---- Bia's camera: off, then on
    Check(!bia.video_send(Picture(1, 3000).data(), 3000, 0), "a player with the camera bit off sends nothing");
    bia.room_camera(1);
    Check(Until(2000, [&] { return State(ana, ra) && ra.count == 1 && ra.member[0].camera == 1; }), "Bia's camera on: Ana's room says so");
    ana.video_subscribe(&kBia, 1);
    Check(Until(2000, [&] {
              bia.video_send(Picture(7, 2000).data(), 2000, 7);
              SleepMs(120);
              return Count(ana) > 0;
          }),
          "and Ana, subscribed, gets Bia's frames");

    // ---- a full friend room: more mutual friends than faces. The library hands every one over, in seat order (the
    // probe picks four: chatroom_select), and never asks the relay for more than four pictures
    const uint64_t kGhost = 0x5000;
    {
        std::lock_guard lock(relay.lock);
        for (uint16_t i = 1; i <= 7; i++) { // seats 11 down to 5: not the map's order; cameras on for the odd ones
            Relay::Client c;
            c.ghost = true;
            c.account = kGhost + i;
            c.seat = static_cast<uint8_t>(12 - i);
            c.self = static_cast<uint8_t>(wire::kSelfRoomMode | (i & 1 ? wire::kSelfCameraOn : 0));
            relay.clients.emplace(i, c);
            relay.rooms[c.account] = i == 7 ? 0 : kRoom;              // the seventh races elsewhere
            if (i != 6) relay.friends.push_back({kAna, c.account});   // the sixth is in the room and no friend of Ana's
        }
    }
    Check(Until(2000, [&] { return State(ana, ra) && ra.count == 6; }), "six mutual friends in the room (of the eight other players): all six listed");
    {
        bool order = ra.member[0].account == kBia; // seat 4, then the ghosts by seat: 5 (the seventh, elsewhere), 6 (the sixth, no friend), 7..11
        for (int i = 1; i < 6; i++) order &= ra.member[i].account == kGhost + static_cast<uint64_t>(6 - i) && ra.member[i].camera == ((6 - i) & 1);
        Check(order && ra.member[0].camera == 1, "in seat order, each with its camera flag; a stranger in the room and a friend elsewhere are not listed");
    }
    Check(State(bia, rb) && rb.count == 1 && rb.member[0].account == kAna, "Bia's list is her own mutual friends: Ana alone");
    {
        const uint64_t six[6] = {kGhost + 5, kGhost + 3, kGhost + 1, kBia, kGhost + 4, kGhost + 2};
        ana.video_subscribe(six, 6);
        Check(Until(2000, [&] {
                  std::lock_guard lock(relay.lock);
                  return relay.Of(kAna)->pictures == std::vector<uint64_t>{kGhost + 5, kGhost + 3, kGhost + 1, kBia};
              }),
              "op 6 carries the first four asked for: a longer list never leaves (the relay would drop it whole)");
        std::lock_guard lock(relay.lock);
        Check(relay.unread == 0, "and the relay read every control up");
        for (uint16_t i = 1; i <= 7; i++) {
            relay.rooms.erase(kGhost + i);
            relay.clients.erase(i);
        }
        std::erase_if(relay.friends, [&](const auto &f) { return f.second != kBia; });
    }
    ana.video_subscribe(&kBia, 1);
    Check(Until(2000, [&] { return State(ana, ra) && ra.count == 1 && ra.member[0].account == kBia; }), "the others left: Bia alone again");

    // ---- loss and reordering on the way: whole frames or nothing
    SleepMs(2100);
    { std::lock_guard lock(bia.lock); bia.frames.clear(); }
    relay.lossy = true;
    sent = 0;
    for (uint32_t i = 200; i < 230; i++) {
        sent += ana.video_send(Picture(i, 4000).data(), 4000, i * 100); // 4 datagrams each; of every 5, one is lost and two swap
        SleepMs(100);
    }
    SleepMs(300);
    relay.lossy = false;
    {
        std::lock_guard lock(bia.lock);
        bool right = true;
        for (auto &[account, frame] : bia.frames) right &= account == kAna && frame.size() == 4000 && Right(frame);
        std::printf("     lossy relay: %d sent, %zu arrived whole\n", sent, bia.frames.size());
        Check(sent == 30 && right && !bia.frames.empty() && bia.frames.size() < 30, "a lossy, reordering relay: fewer frames, every one whole and right");
        bia.frames.clear();
    }

    // ---- the sender's limits: a flood from the camera never passes 64 KiB or 400 datagrams in a second
    SleepMs(2100);
    {
        std::lock_guard lock(relay.lock);
        relay.Of(kAna)->worst_second = relay.Of(kAna)->worst_datagrams = 0;
        relay.Of(kAna)->video_bytes.clear();
    }
    sent = 0;
    const int64_t flood_end = NowMs() + 2500;
    int tried = 0;
    for (uint32_t i = 1000; NowMs() < flood_end; i++, tried++) {
        sent += ana.video_send(Picture(i, 6000).data(), 6000, i);
        if (i % 16 == 0) SleepMs(1);
    }
    SleepMs(200);
    {
        std::lock_guard lock(relay.lock);
        Relay::Client *a = relay.Of(kAna);
        std::printf("     flood: %d tried, %d sent in 2.5 s; the relay's worst second: %zu bytes, %zu datagrams\n", tried, sent, a->worst_second, a->worst_datagrams);
        // By the sender's clock no second holds over 64 KiB (voice_video_test proves that of the pacer). The relay's
        // clock is another: what left at a second's two edges (at most a burst each, three tenths of a second's bytes)
        // may fall into one of its seconds. Over the whole flood (2.5 s: three seconds begun) the rate is the sender's.
        Check(tried > 200 && sent >= 10 && a->worst_second <= 64 * 1024 * 13 / 10 && a->worst_datagrams <= 400 && a->video_refused == 0 &&
                  static_cast<size_t>(sent) * 6144 <= 3 * 64 * 1024,
              "a flood of frames: 64 KiB a second leave (the relay's second: at most a burst over), far under 400 datagrams");
    }

    // ---- the refresh: op 1/2, then op 6 again, every 5 s
    int before_subscriptions, before_controls;
    {
        std::lock_guard lock(relay.lock);
        Relay::Client *b = relay.Of(kBia);
        before_subscriptions = b->subscriptions, before_controls = b->controls;
    }
    SleepMs(5300);
    {
        std::lock_guard lock(relay.lock);
        Relay::Client *b = relay.Of(kBia);
        Check(b->controls > before_controls && b->subscriptions > before_subscriptions && b->plain_controls == 0 && relay.unread == 0,
              "the 5 s refresh re-sends op 1/2 (bit 3 set) and the video subscription, and nothing else");
    }

    // ---- Bia leaves the friend room for a matchmade race: no room for her, nobody to share with for Ana
    room(kBia, 0);
    Check(Until(2000, [&] { return State(ana, ra) && State(bia, rb) && !rb.room_id && !rb.count && !ra.count; }) && ra.room_id == kRoom,
          "Bia left the friend room: she is in none, and Ana's has no friend in it");
    Check(ra.relay == 2 && !ana.video_send(Picture(1, 3000).data(), 3000, 0) && !bia.video_send(Picture(1, 3000).data(), 3000, 0),
          "no frame leaves: from the room without a friend, or from no room");
    room(kBia, kRoom);
    Check(Until(2000, [&] { return State(ana, ra) && State(bia, rb) && ra.count == 1 && rb.count == 1 && rb.room_id == kRoom; }),
          "back in the friend room: together again, without the players doing anything");

    // ---- a relay that does not speak rooms: ver 1 states, op 6 unread
    relay.old = true;
    Check(Until(2000, [&] { return State(ana, ra) && State(bia, rb) && ra.relay == 1 && rb.relay == 1; }) && !ra.room_id && !ra.count && !rb.count,
          "an old relay (state ver 1): no friend room, nobody listed");
    Check(!ana.video_send(Picture(1, 3000).data(), 3000, 0), "an old relay: no frame leaves");
    SleepMs(300);
    {
        std::lock_guard lock(relay.lock);
        before_subscriptions = relay.Of(kAna)->subscriptions;
        before_controls = relay.Of(kAna)->controls;
    }
    SleepMs(5300);
    {
        std::lock_guard lock(relay.lock);
        Relay::Client *a = relay.Of(kAna);
        Check(a->controls > before_controls && a->subscriptions == before_subscriptions && a->plain_controls == 0,
              "an old relay is refreshed (bit 3 still set) and no longer sent op 6");
    }
    relay.old = false;
    Check(Until(7000, [&] { return State(ana, ra) && State(bia, rb) && ra.relay == 2 && ra.count == 1 && rb.count == 1; }),
          "the relay speaks rooms again: the friend room is back without the players doing anything");

    // ---- voice: the whole friend room's, and none outside one. Ana turns voice chat on; the relay says "voice on" in
    // every state from here. The engine opens the PC's audio devices: without any, this part is skipped.
    relay.voice = true;
    ana.settings(1, "", "", 100, 100);
    if (!Until(9000, [&] { return (ana.status() & MKWMP_VOICE_RUNNING) != 0; })) {
        std::printf("skip the audio engine did not start (no audio device on this PC?): the voice checks are skipped\n");
    } else {
        Check(true, "in a friend room with voice chat on: the audio engine runs");
        befriend(kAna, kBia, false);
        Check(Until(2000, [&] { return State(ana, ra) && ra.room_id == kRoom && ra.count == 0; }), "Ana has no mutual friend in the room any more");
        SleepMs(700);
        Check((ana.status() & MKWMP_VOICE_RUNNING) != 0, "voice is the whole room's: it runs with no friend in the room (only the camera is between friends)");
        befriend(kAna, kBia, true);
        room(kAna, 0); // a matchmade race
        Check(Until(3000, [&] { return !(ana.status() & MKWMP_VOICE_RUNNING); }) && State(ana, ra) && ra.relay == 2 && !ra.room_id,
              "room_id 0 (a matchmade lobby): the audio engine stops, the microphone is closed and nothing is played");
        SleepMs(300);
        { std::lock_guard lock(relay.lock); relay.Of(kAna)->voice_in = 0; }
        SleepMs(1500);
        {
            std::lock_guard lock(relay.lock);
            Check(relay.Of(kAna)->voice_in == 0 && !(ana.status() & MKWMP_VOICE_RUNNING), "and no voice datagram leaves while in no friend room");
        }
        room(kAna, kRoom);
        Check(Until(9000, [&] { return (ana.status() & MKWMP_VOICE_RUNNING) != 0; }), "in the friend room again: voice is back");
        room(kAna, 0);
        Check(Until(3000, [&] { return !(ana.status() & MKWMP_VOICE_RUNNING); }), "out again: stopped again");
        relay.old = true; // a relay without friend rooms cannot say where the player is: voice/v1, as that relay routes it
        Check(Until(9000, [&] { return (ana.status() & MKWMP_VOICE_RUNNING) != 0; }) && State(ana, ra) && ra.relay == 1,
              "an old relay (state ver 1): voice/v1 as before, whatever the room");
        relay.old = false;
        room(kAna, kRoom);
        Check(Until(7000, [&] { return State(ana, ra) && ra.relay == 2 && ra.room_id == kRoom; }), "the relay speaks rooms again, Ana in the friend room");
    }
    room(kAna, kRoom);
    ana.settings(0, "", "", 100, 100);
    relay.voice = false;
    Check(Until(3000, [&] { return !(ana.status() & MKWMP_VOICE_RUNNING); }), "voice chat off in the settings: no audio engine");

    // ---- out of room mode: voice/v1 as before, bits 2 and 3 never set
    ana.room_mode(0);
    Check(Until(2000, [&] { std::lock_guard lock(relay.lock); return relay.Of(kAna)->leaves > 0; }), "room mode off with voice off: the player leaves the relay");
    {
        std::lock_guard lock(relay.lock);
        Check(relay.Of(kAna)->last_op == wire::kLeave && relay.Of(kAna)->last_self <= 3, "and its last control up has neither bit 2 nor bit 3");
    }
    Check(!State(ana, ra) && ra.relay == 0 && !ana.video_send(Picture(1, 3000).data(), 3000, 0), "out of room mode: no room state, no frame leaves");
    ana.settings(1, "", "", 100, 100); // voice on, room mode off: what a released client sends
    Check(Until(3000, [&] { std::lock_guard lock(relay.lock); return relay.Of(kAna)->plain_controls > 0; }), "voice/v1 control up: self below 4");
    {
        std::lock_guard lock(relay.lock);
        Check(relay.Of(kAna)->last_self <= 3, "a client out of room mode never sets bits 2 and 3");
    }
    ana.settings(0, "", "", 100, 100);
    Check(Until(2000, [&] { return State(bia, rb) && rb.room_id == kRoom && !rb.count; }),
          "Bia, still in room mode: in the friend room, with no friend in room mode to share a camera with");
    SleepMs(300);

    relay.Close();
    std::printf("voice_room_test: %s\n", failures ? "FAIL" : "PASS");
    std::fflush(stdout);
#ifdef _WIN32
    ExitProcess(failures ? 1 : 0); // the libraries' network threads never end
#else
    _exit(failures ? 1 : 0);
#endif
}