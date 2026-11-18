// plat_net.h on Windows (Winsock 2).
#include "plat_net.h"

void plat_net_init(void) {
    static volatile int32_t started;
    if (plat_load(&started)) return;
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);   // reference-counted: a second caller racing here costs nothing
    plat_store(&started, 1);
}

void plat_sock_close(PlatSock s) { closesocket(s); }

int plat_sock_nonblock(PlatSock s) {
    u_long nb = 1;
    return ioctlsocket(s, FIONBIO, &nb);
}

void plat_sock_timeouts(PlatSock s, uint32_t ms) {
    DWORD tmo = ms;
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (const char *)&tmo, sizeof tmo);
    setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, (const char *)&tmo, sizeof tmo);
}

void plat_sock_shutdown(PlatSock s) { shutdown(s, SD_BOTH); }

int plat_poll(PlatPollFd *fds, int n, int timeout_ms) { return WSAPoll(fds, (ULONG)n, timeout_ms); }

int plat_net_error(void) {
    switch (WSAGetLastError()) {
    case WSAEWOULDBLOCK: return PLAT_NET_WOULDBLOCK;
    case WSAEINPROGRESS: return PLAT_NET_INPROGRESS;
    case WSAEALREADY: return PLAT_NET_ALREADY;
    case WSAEISCONN: return PLAT_NET_ISCONN;
    case WSAEINTR: return PLAT_NET_INTR;
    case WSAECONNRESET: return PLAT_NET_CONNRESET;
    default: return PLAT_NET_OTHER;
    }
}

enum { MAX_IOV = 1024 };

long plat_sock_sendv(PlatSock s, const PlatIov *v, int n) {
    WSABUF b[MAX_IOV];
    if (n < 0 || n > MAX_IOV) return -1;
    for (int i = 0; i < n; i++) b[i].buf = v[i].base, b[i].len = (ULONG)v[i].len;
    DWORD sent = 0;
    return WSASend(s, b, (DWORD)n, &sent, 0, NULL, NULL) ? -1 : (long)sent;
}

long plat_sock_recvv(PlatSock s, PlatIov *v, int n) {
    WSABUF b[MAX_IOV];
    if (n < 0 || n > MAX_IOV) return -1;
    for (int i = 0; i < n; i++) b[i].buf = v[i].base, b[i].len = (ULONG)v[i].len;
    DWORD got = 0, flags = 0;
    return WSARecv(s, b, (DWORD)n, &got, &flags, NULL, NULL) ? -1 : (long)got;
}
