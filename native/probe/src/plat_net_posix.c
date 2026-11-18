// plat_net.h on Linux (and other POSIX systems).
#define _GNU_SOURCE
#include "plat_net.h"

#include <errno.h>
#include <fcntl.h>
#include <sys/time.h>
#include <sys/uio.h>
#include <unistd.h>

void plat_net_init(void) {}

void plat_sock_close(PlatSock s) { close(s); }

int plat_sock_nonblock(PlatSock s) {
    int fl = fcntl(s, F_GETFL);
    return fl < 0 || fcntl(s, F_SETFL, fl | O_NONBLOCK) < 0;
}

void plat_sock_timeouts(PlatSock s, uint32_t ms) {
    struct timeval tv = {(time_t)(ms / 1000u), (suseconds_t)(ms % 1000u) * 1000};
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof tv);
    setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof tv);
}

void plat_sock_shutdown(PlatSock s) { shutdown(s, SHUT_RDWR); }

int plat_poll(PlatPollFd *fds, int n, int timeout_ms) { return poll(fds, (nfds_t)n, timeout_ms); }

int plat_net_error(void) {
    switch (errno) {
    case EAGAIN:
#if EWOULDBLOCK != EAGAIN
    case EWOULDBLOCK:
#endif
        return PLAT_NET_WOULDBLOCK;
    case EINPROGRESS: return PLAT_NET_INPROGRESS;
    case EALREADY: return PLAT_NET_ALREADY;
    case EISCONN: return PLAT_NET_ISCONN;
    case EINTR: return PLAT_NET_INTR;
    case ECONNREFUSED:
    case ECONNRESET: return PLAT_NET_CONNRESET;
    default: return PLAT_NET_OTHER;
    }
}

enum { MAX_IOV = 1024 };

static long msg(PlatSock s, PlatIov *v, int n, int reading) {
    struct iovec io[MAX_IOV];
    if (n < 0 || n > MAX_IOV) return -1;
    for (int i = 0; i < n; i++) io[i].iov_base = v[i].base, io[i].iov_len = v[i].len;
    struct msghdr m = {.msg_iov = io, .msg_iovlen = (size_t)n};
    ssize_t r = reading ? recvmsg(s, &m, 0) : sendmsg(s, &m, MSG_NOSIGNAL);
    return (long)r;
}

long plat_sock_sendv(PlatSock s, const PlatIov *v, int n) { return msg(s, (PlatIov *)v, n, 0); }
long plat_sock_recvv(PlatSock s, PlatIov *v, int n) { return msg(s, v, n, 1); }
