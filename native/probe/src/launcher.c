#include "launcher.h"
#include "config.h"

#include "plat_net.h"
#include <stdio.h>
#include <string.h>

// Returns the HTTP status (0 = no usable response).
static int request_once(const MkwmpConfig *cfg, const char *method, const char *path, const char *json,
                        char *buf, size_t cap, const char **body) {
    plat_net_init();
    PlatSock s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == PLAT_NO_SOCK) return 0;
    plat_sock_timeouts(s, 5000);
    struct sockaddr_in sa = {0};
    sa.sin_family = AF_INET;
    sa.sin_port = htons((unsigned short)cfg->launcher_port);
    sa.sin_addr.s_addr = inet_addr(cfg->launcher_host);
    if (connect(s, (struct sockaddr *)&sa, sizeof sa)) { plat_sock_close(s); return 0; }
    char req[512];
    size_t jlen = json ? strlen(json) : 0;
    int n = snprintf(req, sizeof req,
                     "%s %s HTTP/1.1\r\nHost: %s:%d\r\nAuthorization: Bearer %s\r\n%s"
                     "Content-Length: %u\r\nConnection: close\r\n\r\n",
                     method, path, cfg->launcher_host, cfg->launcher_port, cfg->launcher_token,
                     json ? "Content-Type: application/json\r\n" : "", (unsigned)jlen);
    if (n < 0 || n >= (int)sizeof req || send(s, req, n, 0) != n) { plat_sock_close(s); return 0; }
    for (size_t sent = 0; sent < jlen;) {
        int k = send(s, json + sent, (int)(jlen - sent), 0);
        if (k <= 0) { plat_sock_close(s); return 0; }
        sent += (size_t)k;
    }
    int got = 0, r;
    while (got < (int)cap - 1 && (r = recv(s, buf + got, (int)cap - 1 - got, 0)) > 0) got += r;
    plat_sock_close(s);
    buf[got] = 0;
    int status = 0;
    if (sscanf(buf, "HTTP/1.%*d %d", &status) != 1) return 0;
    const char *b = strstr(buf, "\r\n\r\n");
    *body = b ? b + 4 : buf + got;
    return status;
}

int launcher_request(const char *method, const char *path, char *buf, size_t cap, const char **body) {
    return launcher_request_json(method, path, NULL, buf, cap, body);
}

int launcher_request_json(const char *method, const char *path, const char *json, char *buf, size_t cap,
                          const char **body) {
    // One request at a time: the seat keepalive and the friends refresh run on their own
    // threads, and a retry re-reads the shared config.
    static PlatLock lock = PLAT_LOCK_INIT;
    if (cap == 0) return 0;
    buf[0] = 0;
    *body = buf;
    plat_lock(&lock);
    const MkwmpConfig *cfg = mkwmp_config();
    int status = -1;
    if (cfg->launcher_port) {
        status = request_once(cfg, method, path, json, buf, cap, body);
        if (status == 0 || status == 401) {
            // The launcher may have restarted (new token, same port): re-read mkwmp.toml once.
            cfg = mkwmp_config_reload();
            status = cfg->launcher_port ? request_once(cfg, method, path, json, buf, cap, body) : 0;
        }
    }
    plat_unlock(&lock);
    return status;
}
