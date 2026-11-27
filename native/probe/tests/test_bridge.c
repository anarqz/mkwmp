// Real Winsock integration test for the gRPC wakeup descriptor, the socket bridge and (it #includes npln.c) the
// per-build logic of profile_dispatch.
//
//   $zig = "C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"; $s = "native\probe\src"
//   & $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o scratch\test_bridge.exe `
//       native\probe\tests\test_bridge.c native\probe\tests\stub_coff.c $s\log.c $s\breadcrumbs.c $s\config.c $s\notify.c `
//       $s\friends.c $s\lobby_ca.c $s\nextendo.c $s\seat.c $s\voice.c $s\guesthook.c $s\gameaddrs.c $s\nxfriends.c `
//       $s\kartrace.c $s\launcher.c $s\uuid5.c $s\plat_win.c $s\plat_net_win.c -lws2_32
//   scratch\test_bridge.exe
#undef NDEBUG
#include <assert.h>
#include <stdarg.h>
#include <stdio.h>
#include "../src/npln.c"
#include "../src/capture_cert.h"
#include "../src/config.h"
#include "../src/notify.h"

static void quiet_log(const char *fmt, ...) { (void)fmt; }
RtApi g_rt = {.log = quiet_log};
static int released, restored;
static int release_core(void) { released++; return 1; }
static void restore_core(int *away) { assert(*away == 1); restored++; *away = 0; }
// A fake SDK: clock_gettime (CLOCK_REALTIME always fails, CLOCK_MONOTONIC unless sdk_mono_broken), nn::os ticks.
enum { FN_CLOCK = 42, FN_TICK, FN_SPAN };
static int sdk_mono_broken, sdk_clock_calls;
static uint64_t clock_symbol(const char *name) {
    if (!strcmp(name, "clock_gettime")) return FN_CLOCK;
    if (!strcmp(name, "_ZN2nn2os13GetSystemTickEv")) return FN_TICK;
    if (!strcmp(name, "_ZN2nn2os17ConvertToTimeSpanENS0_4TickE")) return FN_SPAN;
    assert(!"unexpected symbol");
    return 0;
}
static uint64_t sdk_clock(Cpu *c, uint64_t fn, int n, ...) {
    (void)c;
    va_list args;
    va_start(args, n);
    uint64_t a0 = n > 0 ? va_arg(args, uint64_t) : 0;
    int64_t *out = n > 1 ? (int64_t *)(uintptr_t)va_arg(args, uint64_t) : NULL;
    va_end(args);
    if (fn == FN_TICK) { assert(n == 0); return 31250000ull * 7; }                    // 7 s of 31.25 MHz ticks
    if (fn == FN_SPAN) { assert(n == 1 && a0 == 31250000ull * 7); return 7000000123ull; }   // TimeSpan: ns
    assert(fn == FN_CLOCK && n == 2);
    sdk_clock_calls++;
    if (a0 == 0 || sdk_mono_broken) return UINT32_MAX;   // what 1.8.0's SDK returns: w0 = -1
    assert(a0 == 1);
    out[0] = 123;
    out[1] = 456;
    return 0;
}

static int dispatched;
static void count_dispatch(Cpu *c) { (void)c; dispatched++; }

static uint64_t call2(GuestFn fn, uint64_t a, uint64_t b) {
    Cpu c = {0}; c.x[0] = a; c.x[1] = b;
    fn(&c); return c.x[0];
}
static int readable(int fd) {
    struct { int32_t fd; int16_t events, revents; } p = {fd, G_POLLIN, 0};
    Cpu c = {0}; c.x[0] = (uintptr_t)&p; c.x[1] = 1; c.x[2] = 100;
    ov_sock_poll(&c);
    assert(c.x[0] == (uint64_t)((p.revents & G_POLLIN) != 0));
    return (p.revents & G_POLLIN) != 0;
}
int main(void) {
    g_rt.core_away = release_core;
    g_rt.core_back = restore_core;
    Cpu poll_cpu = {0}; poll_cpu.thread = (void *)1; poll_cpu.x[2] = 1;
    ov_sock_poll(&poll_cpu);
    assert(poll_cpu.x[0] == 0 && released == 1 && restored == 1);
    struct { int32_t fd; int16_t events, revents; } invalid_poll = {NFD, G_POLLIN, 0};
    poll_cpu.x[0] = (uintptr_t)&invalid_poll; poll_cpu.x[1] = 1; poll_cpu.x[2] = 100;
    ov_sock_poll(&poll_cpu);
    assert(poll_cpu.x[0] == 1 && invalid_poll.revents == G_POLLNVAL);
    assert(released == 1 && restored == 1);
    g_rt.core_away = NULL; g_rt.core_back = NULL;
    int64_t clock[2] = {-1, -1};
    assert(call2(ov_clock_gettime, 0, (uintptr_t)clock) == 0);
    assert(clock[0] > 1700000000 && clock[1] >= 0 && clock[1] < 1000000000);
    assert(call2(ov_clock_gettime, 1, (uintptr_t)clock) == 0);
    int64_t previous = clock[0] * 1000000000ll + clock[1];
    assert(call2(ov_clock_gettime, 1, (uintptr_t)clock) == 0);
    assert(clock[1] >= 0 && clock[1] < 1000000000);
    assert(clock[0] * 1000000000ll + clock[1] >= previous);
    assert(call2(ov_clock_gettime, 999, (uintptr_t)clock) == UINT64_MAX && g_errno == G_EINVAL);
    g_rt.guest_symbol = clock_symbol;
    g_rt.call = sdk_clock;
    assert(call2(ov_clock_gettime, 1, (uintptr_t)clock) == 0 && clock[0] == 123 && clock[1] == 456);
    assert(call2(ov_clock_gettime, 0, (uintptr_t)clock) == 0 && clock[0] > 1700000000);
    // Game 1.8.0: the SDK's monotonic clock fails too. No repair without a known build (main is not mapped here), so
    // the override computes the SDK's tick clock itself; CLOCK_REALTIME stays the host's.
    sdk_mono_broken = 1;
    sdk_clock_calls = 0;
    assert(call2(ov_clock_gettime, 1, (uintptr_t)clock) == 0 && clock[0] == 7 && clock[1] == 123);
    assert(sdk_clock_calls == 1);
    assert(call2(ov_clock_gettime, 0, (uintptr_t)clock) == 0 && clock[0] > 1700000000);
    sdk_mono_broken = 0;
    assert(call2(ov_clock_gettime, 1, (uintptr_t)clock) == 0 && clock[0] == 123 && clock[1] == 456);
    {
        // The repair: the monotonic source slot gets the tick source back, once, and only past the guard.
        static const uint32_t tick_code[8] = {1, 2, 3, 4, 5, 6, 7, 8};
        uint64_t slot = 0x70049cd4e0ull;
        struct game_addrs row = {.sdk_mono_source_slot = (uintptr_t)&slot, .sdk_tick_source = (uintptr_t)tick_code,
                                 .sdk_tick_source_words = {1, 2, 3, 4, 5, 6, 7, 8}};
        struct game_addrs wrong = row, none = row;
        wrong.sdk_tick_source_words[7] = 9;
        none.sdk_mono_source_slot = 0;
        assert(!sdk_monotonic_repair_row(NULL) && !sdk_monotonic_repair_row(&none));
        assert(!sdk_monotonic_repair_row(&wrong) && slot == 0x70049cd4e0ull);
        assert(sdk_monotonic_repair_row(&row) && slot == (uintptr_t)tick_code);
        assert(!sdk_monotonic_repair_row(&row) && slot == (uintptr_t)tick_code);   // nothing left to repair
        assert(!sdk_monotonic_repair());                                           // unknown build
        // 1.8.0's row: both addresses in the sdk module; 1.0.0 needs no repair
        const struct game_addrs *r180 = game_addrs_for(GAME_180), *r100 = game_addrs_for(GAME_100);
        assert(r180->sdk_mono_source_slot == 0x7005240020ull && r180->sdk_tick_source == 0x70044af300ull);
        assert(!r100->sdk_mono_source_slot && !sdk_monotonic_repair_row(r100));
    }
    {
        // A realtime deadline as the same distance on the monotonic clock.
        int64_t out[2];
        deadline_on_clock((int64_t[]){1795002543, 634034300}, (int64_t[]){1795002543, 134034300},
                          (int64_t[]){7, 900000000}, out);
        assert(out[0] == 8 && out[1] == 400000000);                               // 0.5 s left, carried
        deadline_on_clock((int64_t[]){1795002545, 100}, (int64_t[]){1795002543, 999999900}, (int64_t[]){7, 0}, out);
        assert(out[0] == 8 && out[1] == 200);                                     // 1.0000002 s, borrowed
        deadline_on_clock((int64_t[]){1795002540, 0}, (int64_t[]){1795002543, 0}, (int64_t[]){7, 5}, out);
        assert(out[0] == 7 && out[1] == 5);                                       // already past: now
        deadline_on_clock((int64_t[]){INT64_MAX, 999999999}, (int64_t[]){0, 0}, (int64_t[]){7, 999999999}, out);
        assert(out[0] > 9000000000ll && out[1] >= 0 && out[1] < 1000000000);      // clamped, still a timespec
    }
    g_rt.guest_symbol = NULL;
    g_rt.call = NULL;
    int fd = (int)call2(ov_eventfd, 0, 6);
    assert(fd >= 0 && !readable(fd));
    uint64_t value = 123;
    assert(call2(ov_eventfd_read, fd, (uintptr_t)&value) == UINT64_MAX);
    assert(g_errno == G_EAGAIN && value == 123);
    assert(call2(ov_eventfd_write, fd, 2) == 0);
    assert(call2(ov_eventfd_write, fd, 3) == 0 && readable(fd));
    assert(call2(ov_eventfd_read, fd, (uintptr_t)&value) == 0 && value == 5);
    assert(!readable(fd));
    assert(call2(ov_eventfd_write, fd, UINT64_MAX) == UINT64_MAX && g_errno == G_EINVAL);
    assert(call2(ov_eventfd_write, fd, UINT64_MAX - 1) == 0);
    assert(call2(ov_eventfd_write, fd, 1) == UINT64_MAX && g_errno == G_EAGAIN);
    assert(call2(ov_eventfd_read, fd, (uintptr_t)&value) == 0 && value == UINT64_MAX - 1);
    assert(call2(ov_sock_close, fd, 0) == 0);
    assert(call2(ov_eventfd_read, fd, (uintptr_t)&value) == UINT64_MAX && g_errno == G_EINVAL);
    int sem = (int)call2(ov_eventfd, 2, 5);
    assert(sem == fd && readable(sem));
    assert(call2(ov_eventfd_read, sem, (uintptr_t)&value) == 0 && value == 1 && readable(sem));
    assert(call2(ov_eventfd_read, sem, (uintptr_t)&value) == 0 && value == 1 && !readable(sem));
    assert(call2(ov_sock_close, sem, 0) == 0);
    Cpu dns = {0};
    GAddrInfo *ai = NULL;
    dns.x[0] = (uintptr_t)"capture.local"; dns.x[3] = (uintptr_t)&ai;
    ov_sock_getaddrinfo(&dns);
    assert(dns.x[0] == 0 && ai && ai->ai_family == 2);
    assert(*(void **)((uint8_t *)ai + 0x20) == ai->ai_addr);
    assert(((uint8_t *)ai->ai_addr)[1] == 2);
    call2(ov_sock_freeaddrinfo, (uintptr_t)ai, 0);
    dns.x[0] = (uintptr_t)"capture.local";
    ov_sock_getaddrinfo_cpp(&dns);
    assert(dns.x[0] == 0 && ai && ai->ai_family == 2);
    assert(ai->ai_canonname && (uint8_t)ai->ai_canonname[1] == 2 && !ai->ai_addr && !ai->ai_next);
    // The typed result is a ring entry (nothing to free: FreeAddrInfo(AddrInfo *) stays the runtime's no-op), and
    // the next lookup does not overwrite it.
    GAddrInfo *typed_first = ai;
    assert(ai == &g_typed_ai[0].ai && ai->ai_canonname == (char *)g_typed_ai[0].sa);
    dns.x[0] = (uintptr_t)"capture.local";
    ov_sock_getaddrinfo_cpp(&dns);
    assert(dns.x[0] == 0 && ai == &g_typed_ai[1].ai && typed_first->ai_family == 2);
    {
        // On an MKWMP server every name is the lobby (here the default 127.0.0.1:8900): the console maker's names, a
        // gamesync name, an address. Port 443 and no port are the lobby's; any other port is kept.
        assert(!g_nextendo && g_server.sin_addr.s_addr == htonl(INADDR_LOOPBACK));
        static const char *const kNames[] = {"t-e96531c6-lp1.lp1.t.npln.srv.nintendo.net", "gamesync.npln.nintendo.net",
                                             "nncs2-lp1.n.n.srv.nintendo.net", "example.org", "203.0.113.7"};
        static const struct { const char *service; unsigned port; } kPorts[] = {{NULL, 0}, {"443", 0}, {"8906", 8906}, {"https", 0}};
        for (size_t i = 0; i < sizeof kNames / sizeof kNames[0]; i++)
            for (size_t k = 0; k < sizeof kPorts / sizeof kPorts[0]; k++)
                for (int typed = 0; typed < 2; typed++) {
                    Cpu q = {0};
                    GAddrInfo *r = NULL;
                    q.x[0] = (uintptr_t)kNames[i]; q.x[1] = (uintptr_t)kPorts[k].service; q.x[3] = (uintptr_t)&r;
                    (typed ? ov_sock_getaddrinfo_cpp : ov_sock_getaddrinfo)(&q);
                    assert(q.x[0] == 0 && r);
                    const uint8_t *sa = typed ? (const uint8_t *)r->ai_canonname : r->ai_addr;
                    uint16_t want = kPorts[k].port ? htons((uint16_t)kPorts[k].port) : g_server.sin_port;
                    assert(sa[0] == 16 && sa[1] == 2 && !memcmp(sa + 2, &want, 2) && !memcmp(sa + 4, "\x7f\0\0\1", 4));
                    if (!typed) call2(ov_sock_freeaddrinfo, (uintptr_t)r, 0);
                }
    }
    Cpu sock = {0}; ov_sock_socket(&sock); fd = (int)sock.x[0];
    int32_t option = 1; uint32_t length = 4;
    sock.x[0] = fd; sock.x[1] = 6; sock.x[2] = 1;
    sock.x[3] = (uintptr_t)&option; sock.x[4] = 4;
    ov_sock_setsockopt(&sock); assert(sock.x[0] == 0);
    option = 0; sock.x[0] = fd; sock.x[4] = (uintptr_t)&length;
    ov_sock_getsockopt(&sock); assert(sock.x[0] == 0 && option == 1 && length == 4);
    call2(ov_sock_close, fd, 0);
    SOCKET listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    struct sockaddr_in address = {0}; address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    assert(bind(listener, (struct sockaddr *)&address, sizeof address) == 0);
    int address_size = sizeof address;
    assert(getsockname(listener, (struct sockaddr *)&address, &address_size) == 0);
    assert(listen(listener, 1) == 0);
    SOCKET client = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    assert(connect(client, (struct sockaddr *)&address, sizeof address) == 0);
    SOCKET peer = accept(listener, NULL, NULL); assert(peer != INVALID_SOCKET);
    fd = fd_new(client);
    GIoVec vectors[] = {{"ab", 2}, {"cd", 2}};
    GMsgHdr header = {0}; header.iov = vectors; header.iovlen = 2;
    assert(call2(ov_sock_sendmsg, fd, (uintptr_t)&header) == 4);
    char echoed[4]; assert(recv(peer, echoed, 4, MSG_WAITALL) == 4);
    assert(memcmp(echoed, "abcd", 4) == 0);
    assert(send(peer, "wxyz", 4, 0) == 4 && readable(fd));
    char first[2], second[2]; vectors[0].base = first; vectors[1].base = second;
    assert(call2(ov_sock_recvmsg, fd, (uintptr_t)&header) == 4);
    assert(memcmp(first, "wx", 2) == 0 && memcmp(second, "yz", 2) == 0);
    call2(ov_sock_close, fd, 0); closesocket(peer); closesocket(listener);
    Cpu udp1 = {0}, udp2 = {0};
    udp1.x[0] = udp2.x[0] = 2;
    udp1.x[1] = udp2.x[1] = 2;
    ov_pia_socket(&udp1); ov_pia_socket(&udp2);
    int from_fd = (int)udp1.x[0], to_fd = (int)udp2.x[0];
    assert(from_fd >= 0 && to_fd >= 0 && from_fd != to_fd);
    ov_pia_bind(&udp1); ov_pia_bind(&udp2);
    assert(udp1.x[0] == 0 && udp2.x[0] == 0);
    address_size = sizeof address;
    assert(getsockname(fd_get(to_fd), (struct sockaddr *)&address, &address_size) == 0);
    uint8_t guest_peer[16] = {16, 2, 0, 0, 127, 0, 0, 1};
    memcpy(guest_peer + 2, &address.sin_port, 2);
    Cpu datagram = {0};
    datagram.x[0] = from_fd; datagram.x[1] = (uintptr_t)"peer"; datagram.x[2] = 4;
    datagram.x[4] = (uintptr_t)guest_peer; datagram.x[5] = sizeof guest_peer;
    ov_pia_sendto(&datagram);
    assert(datagram.x[0] == 4 && readable(to_fd));
    uint8_t guest_from[16]; uint32_t from_len = sizeof guest_from;
    datagram.x[0] = to_fd; datagram.x[1] = (uintptr_t)echoed; datagram.x[2] = sizeof echoed;
    datagram.x[4] = (uintptr_t)guest_from; datagram.x[5] = (uintptr_t)&from_len;
    ov_pia_recvfrom(&datagram);
    assert(datagram.x[0] == 4 && memcmp(echoed, "peer", 4) == 0);
    assert(from_len == 16 && memcmp(guest_from + 4, guest_peer + 4, 4) == 0);
    // A non-loopback peer (advertised via signaling) is redirected to loopback, same port.
    guest_peer[4] = 192;
    datagram.x[0] = from_fd; datagram.x[1] = (uintptr_t)"peer"; datagram.x[2] = 4;
    datagram.x[4] = (uintptr_t)guest_peer; datagram.x[5] = sizeof guest_peer;
    ov_pia_sendto(&datagram);
    assert(datagram.x[0] == 4 && readable(to_fd));
    from_len = sizeof guest_from;
    datagram.x[0] = to_fd; datagram.x[1] = (uintptr_t)echoed; datagram.x[2] = sizeof echoed;
    datagram.x[4] = (uintptr_t)guest_from; datagram.x[5] = (uintptr_t)&from_len;
    ov_pia_recvfrom(&datagram);
    assert(datagram.x[0] == 4);
    // Port 0 is still rejected.
    guest_peer[2] = guest_peer[3] = 0;
    datagram.x[0] = from_fd; datagram.x[1] = (uintptr_t)"peer"; datagram.x[2] = 4;
    datagram.x[4] = (uintptr_t)guest_peer; datagram.x[5] = sizeof guest_peer;
    ov_pia_sendto(&datagram);
    assert(datagram.x[0] == UINT64_MAX && g_errno == G_EINVAL);
    // nn::socket::Recv (game 1.8.0): a datagram without its address; EAGAIN when empty; EINVAL for a foreign fd.
    guest_peer[4] = 127;
    memcpy(guest_peer + 2, &address.sin_port, 2);
    datagram.x[0] = from_fd; datagram.x[1] = (uintptr_t)"ping"; datagram.x[2] = 4;
    datagram.x[4] = (uintptr_t)guest_peer; datagram.x[5] = sizeof guest_peer;
    ov_pia_sendto(&datagram);
    assert(datagram.x[0] == 4 && readable(to_fd));
    Cpu rx = {0};
    rx.x[0] = to_fd; rx.x[1] = (uintptr_t)echoed; rx.x[2] = sizeof echoed;
    ov_pia_recv(&rx);
    assert(rx.x[0] == 4 && memcmp(echoed, "ping", 4) == 0);
    rx.x[0] = to_fd; rx.x[1] = (uintptr_t)echoed; rx.x[2] = sizeof echoed;
    ov_pia_recv(&rx);
    assert(rx.x[0] == UINT64_MAX && g_errno == G_EAGAIN);
    rx.x[0] = NFD; rx.x[1] = (uintptr_t)echoed; rx.x[2] = sizeof echoed;
    ov_pia_recv(&rx);
    assert(rx.x[0] == UINT64_MAX && g_errno == G_EINVAL);
    call2(ov_sock_close, from_fd, 0); call2(ov_sock_close, to_fd, 0);
    // Pia 7.3's ping socket, Socket(AF_INET, SOCK_RAW, IPPROTO_ICMP): refused with EPROTONOSUPPORT, no descriptor.
    // Any other non-datagram socket keeps EINVAL.
    Cpu raw = {0};
    raw.x[0] = 2; raw.x[1] = 3; raw.x[2] = 1;
    g_errno = 0;
    ov_pia_socket(&raw);
    assert(raw.x[0] == UINT64_MAX && g_errno == G_EPROTONOSUPPORT);
    raw.x[0] = 2; raw.x[1] = 1; raw.x[2] = 6;
    ov_pia_socket(&raw);
    assert(raw.x[0] == UINT64_MAX && g_errno == G_EINVAL);
    for (int i = 0; i < NFD; i++) assert(!g_used[i]);
    // GetCurrentIpConfigInfo(InAddr * x5), the overload Pia 7.3 calls: loopback mode answers 127.0.0.1 / 255.0.0.0.
    // (No .ovr section in this test exe, so there is no stock handler to stay dormant with.)
    uint8_t ip[5][4];
    memset(ip, 0xee, sizeof ip);
    Cpu ipc = {0};
    for (int i = 0; i < 5; i++) ipc.x[i] = (uintptr_t)ip[i];
    ov_nifm_ip_config_inaddr(&ipc);
    assert(ipc.x[0] == 0 && !memcmp(ip[0], "\x7f\0\0\1", 4) && !memcmp(ip[1], "\xff\0\0\0", 4) && !ip[2][0]);
    // No game is mapped here: no table row, so no gRPC call-site gate, no stock-only module, nothing to match.
    assert(game_build() == GAME_UNKNOWN && game_addrs() == NULL);
    assert(grpc_timedwait_gate() == 0 && grpc_timedwait_gate() == 0);
    assert(!stock_only_caller(0x7003800010ull) && !stock_only_caller(0x7001000000ull));
    assert(!at(0, A.errsvc_post) && !at(0, A.ctl_next_lr) && !is_pia_join_step(0) && !in_trace_range(0x7001300000ull));
    assert(watch_addr(W_COMM_ERROR) == 0 && watch_addr(W_START_P2P) == 0);
    // profile_dispatch works from the installed row A. The referee-timeout skip (MKWMP_SKIP_REFEREE_TIMEOUT=1):
    const struct game_addrs *r100 = game_addrs_for(GAME_100), *r180 = game_addrs_for(GAME_180);
    original_dispatch = count_dispatch;
    g_skip_referee_timeout = 1;
    uint32_t error_info[6] = {0x606314c9};
    Cpu pd = {0};
    pd.pc = r100->errsvc_post; pd.x[1] = (uintptr_t)error_info;
    profile_dispatch(&pd);
    assert(dispatched == 1);                       // no row installed: nothing matches, the call goes through
    A = *r100;
    profile_dispatch(&pd);
    assert(dispatched == 1);                       // 1.0.0: dropped at its error-service post
    error_info[0] = 0x1234;
    profile_dispatch(&pd);
    assert(dispatched == 2);                       // another id is posted
    A = *r180;
    error_info[0] = 0x606314c9;
    profile_dispatch(&pd);
    assert(dispatched == 3);                       // 1.8.0: the 1.0.0 address is unrelated code
    pd.pc = r180->errsvc_post;
    profile_dispatch(&pd);
    assert(dispatched == 3);                       // ... its own post is the one that is dropped
    A.errsvc_post = 0;                             // what a failed error-service guard leaves
    profile_dispatch(&pd);
    assert(dispatched == 4);
    g_skip_referee_timeout = 0;
    // MKWMP_NO_IDLE_KICK: raises the move counter on 1.0.0's kart watchdog; refused on 1.8.0 (no entries, flag off).
    struct { uint8_t pad[0x3c]; int32_t player; } kart_info = {.player = 3};
    uint8_t ctrl[0x200] = {0};
    const uint8_t *kart_info_p = (const uint8_t *)&kart_info;
    uint32_t kart_state = 5, kart_frames = 3000;
    int32_t kart_moves = 2;
    memcpy(ctrl + r100->kart_info, &kart_info_p, 8);
    memcpy(ctrl + r100->kart_state, &kart_state, 4);
    memcpy(ctrl + r100->kart_frames, &kart_frames, 4);
    memcpy(ctrl + r100->kart_moves, &kart_moves, 4);
    g_trace_kart = 0; g_no_idle_kick = 1;
    Cpu kd = {0};
    kd.x[0] = (uintptr_t)ctrl;
    A = *r180;
    kd.pc = r100->kart_entry[1];
    profile_dispatch(&kd);
    kd.pc = 0;                                     // a zero entry never matches, even a zero pc
    profile_dispatch(&kd);
    memcpy(&kart_moves, ctrl + r100->kart_moves, 4);
    assert(kart_moves == 2 && dispatched == 6);
    A = *r100;
    kd.pc = r100->kart_entry[1];
    profile_dispatch(&kd);
    memcpy(&kart_moves, ctrl + r100->kart_moves, 4);
    assert(kart_moves == 20 && dispatched == 7);
    g_no_idle_kick = 0;
    memset(&A, 0, sizeof A);
    {
        // nnsocketConnect. MKWMP server: always the lobby's address; port 443 becomes the lobby's port, any other
        // explicit port is kept. Nextendo Network: the address and port the game asks for, as they are.
        SOCKET lis = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        struct sockaddr_in la = {0}; la.sin_family = AF_INET; la.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        int ll = sizeof la;
        assert(!bind(lis, (struct sockaddr *)&la, sizeof la) && !getsockname(lis, (struct sockaddr *)&la, &ll) && !listen(lis, 4));
        uint16_t lobby_port = g_server.sin_port, https = htons(443), bogus = htons(1);
        uint8_t want[16] = {16, 2, 0, 0, 203, 0, 113, 7};   // console sockaddr_in: 203.0.113.7
        struct { const uint16_t *server_port, *guest_port; } mkwmp[] = {{&la.sin_port, &https}, {&bogus, &la.sin_port}};
        for (int i = 0; i < 2; i++) {
            g_server.sin_port = *mkwmp[i].server_port;
            memcpy(want + 2, mkwmp[i].guest_port, 2);
            Cpu k = {0}; ov_sock_socket(&k); int cfd = (int)k.x[0];
            k.x[0] = cfd; k.x[1] = (uintptr_t)want; k.x[2] = sizeof want;
            ov_sock_connect(&k);
            assert(k.x[0] == 0 || (k.x[0] == UINT64_MAX && g_errno == G_EINPROGRESS));
            SOCKET got = accept(lis, NULL, NULL);   // 203.0.113.7 was never tried: the lobby's loopback answered
            assert(got != INVALID_SOCKET);
            closesocket(got); call2(ov_sock_close, cfd, 0);
        }
        g_nextendo = 1;
        g_server.sin_port = bogus;
        uint8_t ask[16] = {16, 2, 0, 0, 127, 0, 0, 1};
        memcpy(ask + 2, &la.sin_port, 2);
        Cpu k = {0}; ov_sock_socket(&k); int cfd = (int)k.x[0];
        k.x[0] = cfd; k.x[1] = (uintptr_t)ask; k.x[2] = sizeof ask;
        ov_sock_connect(&k);
        assert(k.x[0] == 0 || (k.x[0] == UINT64_MAX && g_errno == G_EINPROGRESS));
        SOCKET got = accept(lis, NULL, NULL);
        assert(got != INVALID_SOCKET);
        closesocket(got); call2(ov_sock_close, cfd, 0);
        // no port, no address, no sockaddr: refused, never the MKWMP lobby instead
        uint8_t none[3][16] = {{16, 2, 0, 0, 127, 0, 0, 1}, {16, 2, 0x1f, 0x90, 0, 0, 0, 0}, {16, 10, 0x1f, 0x90, 127, 0, 0, 1}};
        for (int i = 0; i < 4; i++) {
            ov_sock_socket(&k); cfd = (int)k.x[0];
            k.x[0] = cfd; k.x[1] = i < 3 ? (uintptr_t)none[i] : 0; k.x[2] = 16;
            g_errno = 0;
            ov_sock_connect(&k);
            assert(k.x[0] == UINT64_MAX && g_errno == G_EINVAL);
            call2(ov_sock_close, cfd, 0);
        }
        // Name resolution on Nextendo. An address the game session gives (gamesync) is itself, with the port asked
        // for; 443 stays 443. The console maker's names need the session's serverHost: without a session they fail
        // (never the MKWMP lobby, never the real owner of the name).
        static const struct { const char *service; unsigned port; } kPorts[] = {{"8906", 8906}, {"443", 443}, {NULL, 0}, {"https", 443}};
        for (size_t p = 0; p < sizeof kPorts / sizeof kPorts[0]; p++)
            for (int typed = 0; typed < 2; typed++) {
                Cpu q = {0};
                GAddrInfo *r = NULL;
                q.x[0] = (uintptr_t)"203.0.113.7"; q.x[1] = (uintptr_t)kPorts[p].service; q.x[3] = (uintptr_t)&r;
                (typed ? ov_sock_getaddrinfo_cpp : ov_sock_getaddrinfo)(&q);
                assert(q.x[0] == 0 && r);
                const uint8_t *sa = typed ? (const uint8_t *)r->ai_canonname : r->ai_addr;
                uint16_t port = htons((uint16_t)kPorts[p].port);
                assert(sa[0] == 16 && sa[1] == 2 && !memcmp(sa + 2, &port, 2) && !memcmp(sa + 4, "\xcb\x00\x71\x07", 4));
                if (!typed) call2(ov_sock_freeaddrinfo, (uintptr_t)r, 0);
            }
        static const char *const kNeedSession[] = {"t-e96531c6-lp1.lp1.t.npln.srv.nintendo.net", "nncs2-lp1.n.n.srv.nintendo.net",
                                                   "nncs1-lp1.n.n.srv.nintendo.net"};
        for (size_t i = 0; i < sizeof kNeedSession / sizeof kNeedSession[0]; i++) {
            Cpu q = {0};
            GAddrInfo *r = (GAddrInfo *)(uintptr_t)1;
            q.x[0] = (uintptr_t)kNeedSession[i]; q.x[1] = (uintptr_t)"443"; q.x[3] = (uintptr_t)&r;
            ov_sock_getaddrinfo(&q);
            assert(q.x[0] == UINT64_MAX && !r);
        }
        // The game's HTTP client (ghost data, nnssl.h) on a socket connected as above: the two ends' addresses, errno
        // through the socket library, plain send and recv (with MSG_PEEK, and the peer's end as 0), its resolver call.
        {
            Cpu h = {0}; ov_sock_socket(&h); int hfd = (int)h.x[0];
            h.x[0] = hfd; h.x[1] = (uintptr_t)ask; h.x[2] = sizeof ask;
            ov_sock_connect(&h);
            SOCKET peer = accept(lis, NULL, NULL);
            assert(peer != INVALID_SOCKET);
            uint8_t name[16]; uint32_t nl = sizeof name;
            Cpu a = {0}; a.x[0] = hfd; a.x[1] = (uintptr_t)name; a.x[2] = (uintptr_t)&nl;
            ov_sock_peername(&a);
            assert(a.x[0] == 0 && nl == 16 && name[0] == 16 && name[1] == 2 && !memcmp(name + 2, &la.sin_port, 2) &&
                   !memcmp(name + 4, "\x7f\0\0\1", 4));
            memset(name, 0, sizeof name);
            a.x[0] = hfd; a.x[1] = (uintptr_t)name; a.x[2] = (uintptr_t)&nl;
            ov_sock_sockname(&a);
            assert(a.x[0] == 0 && nl == 16 && name[1] == 2 && (name[2] | name[3]) && !memcmp(name + 4, "\x7f\0\0\1", 4));
            nl = 8;   // no room for an address
            a.x[0] = hfd; a.x[1] = (uintptr_t)name; a.x[2] = (uintptr_t)&nl;
            ov_sock_peername(&a);
            assert(a.x[0] == UINT64_MAX && g_errno == G_EINVAL);
            char in[8];
            Cpu r = {0}; r.x[0] = hfd; r.x[1] = (uintptr_t)in; r.x[2] = sizeof in;
            ov_sock_recv(&r);   // nothing yet
            assert(r.x[0] == UINT64_MAX && call2(ov_sock_get_errno, 0, 0) == G_EAGAIN);
            call2(ov_sock_set_errno, 0, 0);
            assert(call2(ov_sock_get_errno, 0, 0) == 0 && g_errno == 0);
            Cpu s = {0}; s.x[0] = hfd; s.x[1] = (uintptr_t)"ghost"; s.x[2] = 5;
            ov_sock_send(&s);
            assert(s.x[0] == 5 && recv(peer, in, sizeof in, 0) == 5 && !memcmp(in, "ghost", 5));
            assert(send(peer, "data", 4, 0) == 4 && readable(hfd));
            memset(in, 0, sizeof in);
            r.x[0] = hfd; r.x[1] = (uintptr_t)in; r.x[2] = sizeof in; r.x[3] = 2;   // MSG_PEEK
            ov_sock_recv(&r);
            assert(r.x[0] == 4 && !memcmp(in, "data", 4));
            r.x[0] = hfd; r.x[1] = (uintptr_t)in; r.x[2] = 2; r.x[3] = 0;
            ov_sock_recv(&r);
            assert(r.x[0] == 2);
            r.x[0] = hfd; r.x[1] = (uintptr_t)in; r.x[2] = sizeof in; r.x[3] = 0;
            ov_sock_recv(&r);
            assert(r.x[0] == 2 && !memcmp(in, "ta", 2));
            closesocket(peer);
            assert(readable(hfd));
            r.x[0] = hfd; r.x[1] = (uintptr_t)in; r.x[2] = sizeof in; r.x[3] = 0;
            ov_sock_recv(&r);
            assert(r.x[0] == 0);   // the server closed: the end
            call2(ov_sock_close, hfd, 0);
            r.x[0] = hfd; r.x[1] = (uintptr_t)in; r.x[2] = sizeof in;
            ov_sock_recv(&r);      // not a descriptor of ours any more
            assert(r.x[0] == UINT64_MAX && g_errno == G_EINVAL);
            s.x[0] = hfd; s.x[1] = (uintptr_t)"x"; s.x[2] = 1;
            ov_sock_send(&s);
            assert(s.x[0] == UINT64_MAX && g_errno == G_EINVAL);
            Cpu q = {0};
            GAddrInfo *res = NULL;
            q.x[0] = (uintptr_t)"203.0.113.7"; q.x[1] = (uintptr_t)"443"; q.x[3] = (uintptr_t)&res;
            ov_sock_getaddrinfo_options(&q);
            const uint8_t *sa = res ? res->ai_addr : NULL;
            assert(q.x[0] == 0 && sa && res->ai_family == 2 && res->ai_addrlen == 16 && !res->ai_next &&
                   !memcmp(sa, "\x10\x02\x01\xbb\xcb\x00\x71\x07", 8));
            call2(ov_sock_freeaddrinfo, (uintptr_t)res, 0);
            uint8_t option[32];
            memset(option, 0xff, sizeof option);
            assert(call2(ov_resolver_get_option, (uintptr_t)option, 0x2711) == 0 && !memcmp(option + 16, "\0\0\0\0", 4));
            assert(call2(ov_resolver_set_option, (uintptr_t)option, 0) == 0);
            assert(npln_guest_socket(hfd) == PLAT_NO_SOCK);
        }
        // Pia on Nextendo: a NAT probe (16 bytes, type 0) goes to the address Pia names, not to an MKWMP responder.
        Cpu u1 = {0}, u2 = {0};
        u1.x[0] = u2.x[0] = 2; u1.x[1] = u2.x[1] = 2;
        ov_pia_socket(&u1); ov_pia_socket(&u2);
        int tx = (int)u1.x[0], rx_fd = (int)u2.x[0];
        ov_pia_bind(&u1); ov_pia_bind(&u2);
        struct sockaddr_in ua; int ul = sizeof ua;
        assert(!getsockname(fd_get(rx_fd), (struct sockaddr *)&ua, &ul));
        uint8_t nat[16] = {16, 2, 0, 0, 127, 0, 0, 1};
        memcpy(nat + 2, &ua.sin_port, 2);
        static const char probe[16] = {0};
        Cpu d = {0};
        d.x[0] = tx; d.x[1] = (uintptr_t)probe; d.x[2] = sizeof probe; d.x[4] = (uintptr_t)nat; d.x[5] = sizeof nat;
        ov_pia_sendto(&d);
        assert(d.x[0] == 16 && readable(rx_fd));
        nat[2] = nat[3] = 0;   // no port: rejected
        d.x[0] = tx; d.x[1] = (uintptr_t)probe; d.x[2] = sizeof probe; d.x[4] = (uintptr_t)nat; d.x[5] = sizeof nat;
        ov_pia_sendto(&d);
        assert(d.x[0] == UINT64_MAX && g_errno == G_EINVAL);
        call2(ov_sock_close, tx, 0); call2(ov_sock_close, rx_fd, 0);
        // The account before any session: no token (nothing is asked of a launcher while dormant), a stand-in id.
        char tok[64] = "untouched"; uint64_t tok_len = 99;
        Cpu t = {0}; t.x[0] = (uintptr_t)&tok_len; t.x[1] = (uintptr_t)tok; t.x[2] = sizeof tok;
        ov_nsa_load_token(&t);
        assert(t.x[0] == 0 && tok_len == 0 && !strcmp(tok, "untouched"));
        uint64_t account = 0;
        Cpu n = {0}; n.x[0] = (uintptr_t)&account;
        ov_nsa_get_id(&n);
        assert(n.x[0] == 0 && account == FAKE_NSA_ID);
        g_nextendo = 0;
        g_server.sin_port = lobby_port;
        closesocket(lis);
        for (int i = 0; i < NFD; i++) assert(!g_used[i]);
    }
    uint8_t cert_buffer[2048]; GCertificateInfo *info = NULL;
    Cpu cert = {0}; cert.x[0] = (uintptr_t)&info; cert.x[1] = (uintptr_t)cert_buffer;
    cert.x[2] = sizeof cert_buffer;
    ov_ssl_certificates(&cert);
    assert(cert.x[0] == 0 && info == (GCertificateInfo *)cert_buffer);
    assert(info->size == sizeof kCaptureCert && info->data[0] == 0x30);
    WSACleanup();

    // Client config parser.
    MkwmpConfig cc;
    mkwmp_config_parse("server = \"10.0.0.5:9000\"\nname = \"Ana\"\nticket = \"seat-01\"\nslot = 2\n", &cc);
    assert(cc.present && !strcmp(cc.host, "10.0.0.5") && cc.port == 9000);
    assert(!strcmp(cc.name, "Ana") && cc.slot == 2 && !strcmp(cc.ticket, "seat-01"));
    mkwmp_config_parse("# comment\n[section]\nname = \"Bia\"\n", &cc);  // defaults + comments/sections
    assert(!strcmp(cc.host, "127.0.0.1") && cc.port == 8900 && cc.slot == 1);
    assert(!strcmp(cc.name, "Bia"));
    assert(cc.launcher_port == 0);
    mkwmp_config_parse("server = \"mkwlb.anarqz.dev:8905\"\nlauncher = \"127.0.0.1:47820\"\n"
                       "launcher_token = \"ab12\"\n", &cc);
    assert(!strcmp(cc.host, "mkwlb.anarqz.dev") && cc.port == 8905);
    assert(!strcmp(cc.launcher_host, "127.0.0.1") && cc.launcher_port == 47820 && !strcmp(cc.launcher_token, "ab12"));

    // Local seat API replies (launcher contract).
    SeatReply sr;
    seat_parse(200, "{\"state\":\"seated\",\"ticket\":\"mkw1.p.s\",\"lobby\":\"216.128.169.210:8905\",\"name\":\"Ana\"}", &sr);
    assert(sr.state == SEAT_SEATED && !strcmp(sr.ticket, "mkw1.p.s") && !strcmp(sr.lobby, "216.128.169.210:8905"));
    seat_parse(200, "{\"state\": \"queued\", \"position\": 3, \"seatsTotal\": 50}", &sr);
    assert(sr.state == SEAT_QUEUED && sr.position == 3 && sr.total == 50);
    seat_parse(409, "{\"state\":\"refused\",\"message\":\"NAME_TAKEN\"}", &sr);
    assert(sr.state == SEAT_REFUSED && !strcmp(sr.message, "NAME_TAKEN"));
    seat_parse(503, "", &sr);
    assert(sr.state == SEAT_UNAVAILABLE);
    seat_parse(0, NULL, &sr);
    assert(sr.state == SEAT_UNAVAILABLE);

    // The seated reply's "room" (launcher 1.4.0), as the launcher writes it, into the crash record's breadcrumbs.
    const char *seated_room = "{\"state\":\"seated\",\"ticket\":\"mkw1.p.s\",\"lobby\":\"216.128.169.210:8905\",\"name\":\"Ana\","
                              "\"room\":{\"lobby\":\"0b4e7a10-1111-4222-8333-444455556666\",\"ussid\":12,\"ussids\":[3,12,16],"
                              "\"at\":1763647200123}}";
    seat_parse(200, seated_room, &sr);   // the top-level lobby, not the room's
    assert(sr.state == SEAT_SEATED && !strcmp(sr.lobby, "216.128.169.210:8905"));
    seat_set_room_sink(crumb_room);
    seat_parse_room(seated_room);
    CrumbRoom cr;
    assert(crumb_room_get(&cr) && !strcmp(cr.lobby, "0b4e7a10-1111-4222-8333-444455556666") && cr.ussid == 12);
    assert(cr.n == 3 && cr.ussids[0] == 3 && cr.ussids[2] == 16 && cr.at == 1763647200123ull);
    assert(!crumb_room_host(&cr));   // the host is ussids[0], 3
    seat_parse_room("{\"state\":\"seated\",\"room\":{\"lobby\":\"0b4e7a10-1111-4222-8333-444455556666\",\"ussid\":3,"
                    "\"ussids\":[3,12,16],\"at\":1763647260000}}");
    assert(crumb_room_get(&cr) && cr.ussid == 3 && crumb_room_host(&cr) && cr.at == 1763647260000ull);
    seat_parse_room("{\"state\":\"seated\",\"ticket\":\"t\"}");   // no key (an older launcher): nothing changes
    assert(crumb_room_get(&cr) && cr.ussid == 3);
    seat_parse_room("{\"state\":\"seated\",\"room\":{\"lobby\":\"aa\",\"ussid\":5,\"ussids\":[5]}}");   // without "at"
    assert(crumb_room_get(&cr) && cr.ussid == 5 && cr.at == 0 && crumb_room_host(&cr));
    seat_parse_room("{\"state\":\"seated\",\"room\":null}");   // in no room
    assert(!crumb_room_get(&cr));
    seat_set_room_sink(NULL);

    // Notification queue: FIFO, drain, and oldest-dropped-on-overflow.
    Notification nbuf[32];
    notify_push(NOTIFY_SUCCESS, "Connected to MKWMP");
    notify_push(NOTIFY_INFO, "Waiting to join, 4th in queue");
    assert(notify_drain(nbuf, 32) == 2);
    assert(nbuf[0].level == NOTIFY_SUCCESS && !strcmp(nbuf[0].text, "Connected to MKWMP"));
    assert(nbuf[1].id == nbuf[0].id + 1 && !strcmp(nbuf[1].text, "Waiting to join, 4th in queue"));
    assert(notify_drain(nbuf, 32) == 0);
    for (int i = 0; i < 20; i++) notify_push(NOTIFY_INFO, "x");   // overflow 16-slot ring
    assert(notify_drain(nbuf, 32) == 16);
    assert(nbuf[0].id == 7);  // first 4 of 20 dropped (ids 3..6)

    // GetFriendList before any launcher roster: success with zero friends, buffer untouched.
    int32_t friend_count = -1;
    GFriend friend_buf[2];
    memset(friend_buf, 0xee, sizeof friend_buf);
    uint64_t friend_owner[2] = {1, 2};
    Cpu fc = {0};
    fc.x[0] = (uintptr_t)&friend_count; fc.x[1] = (uintptr_t)friend_buf; fc.x[2] = (uintptr_t)friend_owner;
    fc.x[3] = 0; fc.x[4] = 2;
    ov_friend_list(&fc);
    assert(fc.x[0] == 0 && friend_count == 0 && friend_buf[0].is_valid == 0xee);

    puts("bridge: eventfd, addrinfo ABI, TCP, loopback peer UDP, certificate, config and notify passed");
    return 0;
}
