// Real Winsock integration test for the gRPC wakeup descriptor.
#undef NDEBUG
#include <assert.h>
#include <stdarg.h>
#include <stdio.h>
#include "../src/npln.c"
#include "../src/config.h"
#include "../src/notify.h"

static void quiet_log(const char *fmt, ...) { (void)fmt; }
Runtime g_rt = {.rt_log = quiet_log};
Delegates g_dele;
static int released, restored;
static int release_core(void) { released++; return 1; }
static void restore_core(int *away) { assert(*away == 1); restored++; *away = 0; }
static uint64_t clock_symbol(const char *name) {
    assert(!strcmp(name, "clock_gettime"));
    return 42;
}
static uint64_t sdk_clock(Cpu *c, uint64_t fn, int n, ...) {
    (void)c;
    assert(fn == 42 && n == 2);
    va_list args;
    va_start(args, n);
    uint64_t id = va_arg(args, uint64_t);
    int64_t *out = (int64_t *)(uintptr_t)va_arg(args, uint64_t);
    va_end(args);
    if (id == 0) return UINT32_MAX;
    assert(id == 1);
    out[0] = 123;
    out[1] = 456;
    return 0;
}

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
    g_dele.core_away = release_core;
    g_dele.core_back = restore_core;
    Cpu poll_cpu = {0}; poll_cpu.thread = (void *)1; poll_cpu.x[2] = 1;
    ov_sock_poll(&poll_cpu);
    assert(poll_cpu.x[0] == 0 && released == 1 && restored == 1);
    struct { int32_t fd; int16_t events, revents; } invalid_poll = {NFD, G_POLLIN, 0};
    poll_cpu.x[0] = (uintptr_t)&invalid_poll; poll_cpu.x[1] = 1; poll_cpu.x[2] = 100;
    ov_sock_poll(&poll_cpu);
    assert(poll_cpu.x[0] == 1 && invalid_poll.revents == G_POLLNVAL);
    assert(released == 1 && restored == 1);
    g_dele.core_away = NULL; g_dele.core_back = NULL;
    int64_t clock[2] = {-1, -1};
    assert(call2(ov_clock_gettime, 0, (uintptr_t)clock) == 0);
    assert(clock[0] > 1700000000 && clock[1] >= 0 && clock[1] < 1000000000);
    assert(call2(ov_clock_gettime, 1, (uintptr_t)clock) == 0);
    int64_t previous = clock[0] * 1000000000ll + clock[1];
    assert(call2(ov_clock_gettime, 1, (uintptr_t)clock) == 0);
    assert(clock[1] >= 0 && clock[1] < 1000000000);
    assert(clock[0] * 1000000000ll + clock[1] >= previous);
    assert(call2(ov_clock_gettime, 999, (uintptr_t)clock) == UINT64_MAX && g_errno == G_EINVAL);
    g_rt.ldr_symbol = clock_symbol;
    g_rt.rt_call = sdk_clock;
    assert(call2(ov_clock_gettime, 1, (uintptr_t)clock) == 0 && clock[0] == 123 && clock[1] == 456);
    assert(call2(ov_clock_gettime, 0, (uintptr_t)clock) == 0 && clock[0] > 1700000000);
    g_rt.ldr_symbol = NULL;
    g_rt.rt_call = NULL;
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
    call2(ov_sock_freeaddrinfo, (uintptr_t)ai, 0);
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
    call2(ov_sock_close, from_fd, 0); call2(ov_sock_close, to_fd, 0);
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

    puts("bridge: eventfd, addrinfo ABI, TCP, loopback peer UDP, certificate, config and notify passed");
    return 0;
}
