#include "launcher.h"
#include "config.h"

#include <winsock2.h>
#include <ws2tcpip.h>
#include <stdio.h>
#include <string.h>

// Returns the HTTP status (0 = no usable response).
static int request_once(const MkwmpConfig *cfg, const char *method, const char *path,
                        char *buf, size_t cap, const char **body) {
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);
    SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET) return 0;
    DWORD tmo = 5000;
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (const char *)&tmo, sizeof tmo);
    setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, (const char *)&tmo, sizeof tmo);
    struct sockaddr_in sa = {0};
    sa.sin_family = AF_INET;
    sa.sin_port = htons((unsigned short)cfg->launcher_port);
    sa.sin_addr.s_addr = inet_addr(cfg->launcher_host);
    if (connect(s, (struct sockaddr *)&sa, sizeof sa)) { closesocket(s); return 0; }
    char req[512];
    int n = snprintf(req, sizeof req,
                     "%s %s HTTP/1.1\r\nHost: %s:%d\r\nAuthorization: Bearer %s\r\n"
                     "Content-Length: 0\r\nConnection: close\r\n\r\n",
                     method, path, cfg->launcher_host, cfg->launcher_port, cfg->launcher_token);
    if (n < 0 || n >= (int)sizeof req || send(s, req, n, 0) != n) { closesocket(s); return 0; }
    int got = 0, r;
    while (got < (int)cap - 1 && (r = recv(s, buf + got, (int)cap - 1 - got, 0)) > 0) got += r;
    closesocket(s);
    buf[got] = 0;
    int status = 0;
    if (sscanf(buf, "HTTP/1.%*d %d", &status) != 1) return 0;
    const char *b = strstr(buf, "\r\n\r\n");
    *body = b ? b + 4 : buf + got;
    return status;
}

int launcher_request(const char *method, const char *path, char *buf, size_t cap, const char **body) {
    // One request at a time: the seat keepalive and the friends refresh run on their own
    // threads, and a retry re-reads the shared config.
    static SRWLOCK lock = SRWLOCK_INIT;
    if (cap == 0) return 0;
    buf[0] = 0;
    *body = buf;
    AcquireSRWLockExclusive(&lock);
    const MkwmpConfig *cfg = mkwmp_config();
    int status = -1;
    if (cfg->launcher_port) {
        status = request_once(cfg, method, path, buf, cap, body);
        if (status == 0 || status == 401) {
            // The launcher may have restarted (new token, same port): re-read mkwmp.toml once.
            cfg = mkwmp_config_reload();
            status = cfg->launcher_port ? request_once(cfg, method, path, buf, cap, body) : 0;
        }
    }
    ReleaseSRWLockExclusive(&lock);
    return status;
}
