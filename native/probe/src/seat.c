#include "seat.h"
#include "config.h"
#include "log.h"
#include "notify.h"

#include <winsock2.h>
#include <ws2tcpip.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// ---- tiny JSON field readers (flat objects, no escapes in our values) ------
static int json_str(const char *body, const char *key, char *dst, size_t n) {
    char pat[64];
    snprintf(pat, sizeof pat, "\"%s\"", key);
    const char *p = strstr(body, pat);
    if (!p) return 0;
    p = strchr(p + strlen(pat), ':');
    if (!p) return 0;
    while (*++p == ' ') {}
    if (*p != '"') return 0;
    const char *e = strchr(++p, '"');
    if (!e) return 0;
    size_t len = (size_t)(e - p);
    if (len >= n) len = n - 1;
    memcpy(dst, p, len);
    dst[len] = 0;
    return 1;
}

static int json_int(const char *body, const char *key, int *out) {
    char pat[64];
    snprintf(pat, sizeof pat, "\"%s\"", key);
    const char *p = strstr(body, pat);
    if (!p) return 0;
    p = strchr(p + strlen(pat), ':');
    if (!p) return 0;
    *out = atoi(p + 1);
    return 1;
}

void seat_parse(int http_status, const char *body, SeatReply *out) {
    memset(out, 0, sizeof *out);
    char state[32] = "";
    if (body) json_str(body, "state", state, sizeof state);
    if (http_status == 200 && !strcmp(state, "seated") && json_str(body, "ticket", out->ticket, sizeof out->ticket)) {
        out->state = SEAT_SEATED;
        json_str(body, "lobby", out->lobby, sizeof out->lobby);
    } else if (http_status == 200 && !strcmp(state, "queued")) {
        out->state = SEAT_QUEUED;
        json_int(body, "position", &out->position);
        json_int(body, "seatsTotal", &out->total);
    } else if (http_status == 409) {
        out->state = SEAT_REFUSED;
        if (!body || !json_str(body, "message", out->message, sizeof out->message))
            strcpy(out->message, "seat refused");
    } else {
        out->state = SEAT_UNAVAILABLE;
    }
}

// ---- one HTTP/1.1 POST over loopback -----------------------------------------
// Returns the HTTP status (0 = connection failed); body copied into `body`.
static int http_post(const char *host, int port, const char *token, char *body, size_t cap) {
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);
    SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET) return 0;
    DWORD tmo = 5000;
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (const char *)&tmo, sizeof tmo);
    setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, (const char *)&tmo, sizeof tmo);
    struct sockaddr_in sa = {0};
    sa.sin_family = AF_INET;
    sa.sin_port = htons((unsigned short)port);
    sa.sin_addr.s_addr = inet_addr(host);
    if (connect(s, (struct sockaddr *)&sa, sizeof sa)) { closesocket(s); return 0; }
    char req[512];
    int n = snprintf(req, sizeof req,
                     "POST /local/v1/seat HTTP/1.1\r\nHost: %s:%d\r\nAuthorization: Bearer %s\r\n"
                     "Content-Length: 0\r\nConnection: close\r\n\r\n", host, port, token);
    if (send(s, req, n, 0) != n) { closesocket(s); return 0; }
    char resp[4096];
    int got = 0, r;
    while (got < (int)sizeof resp - 1 && (r = recv(s, resp + got, (int)sizeof resp - 1 - got, 0)) > 0) got += r;
    closesocket(s);
    resp[got] = 0;
    int status = 0;
    if (sscanf(resp, "HTTP/1.%*d %d", &status) != 1) return 0;
    const char *b = strstr(resp, "\r\n\r\n");
    b = b ? b + 4 : "";
    snprintf(body, cap, "%s", b);
    return status;
}

static int seat_post_once(SeatReply *out);
static SRWLOCK g_ticket_lock = SRWLOCK_INIT;
static char g_ticket[520];

int seat_ticket(char *dst, size_t n) {
    AcquireSRWLockShared(&g_ticket_lock);
    snprintf(dst, n, "%s", g_ticket);
    ReleaseSRWLockShared(&g_ticket_lock);
    return dst[0] != 0;
}

int seat_post(SeatReply *out) {
    int rc = seat_post_once(out);
    if (rc == 0 && out->state == SEAT_SEATED) {
        AcquireSRWLockExclusive(&g_ticket_lock);
        snprintf(g_ticket, sizeof g_ticket, "%s", out->ticket);   // renewed tickets replace the old
        ReleaseSRWLockExclusive(&g_ticket_lock);
    }
    return rc;
}

static int seat_post_once(SeatReply *out) {
    const MkwmpConfig *cfg = mkwmp_config();
    if (!cfg->launcher_port) return -1;
    char body[4096];
    int status = http_post(cfg->launcher_host, cfg->launcher_port, cfg->launcher_token, body, sizeof body);
    if (status == 0 || status == 401) {
        // The launcher may have restarted (new token, same port): re-read mkwmp.toml once.
        cfg = mkwmp_config_reload();
        status = cfg->launcher_port ? http_post(cfg->launcher_host, cfg->launcher_port, cfg->launcher_token,
                                                body, sizeof body) : 0;
    }
    seat_parse(status, status ? body : NULL, out);
    return 0;
}

SeatState seat_acquire(SeatReply *out, int max_wait_ms) {
    int waited = 0, last_pos = -1;
    for (;;) {
        if (seat_post(out) < 0) { out->state = SEAT_UNAVAILABLE; return out->state; }
        if (out->state == SEAT_SEATED) {
            mkwmp_log("seat: seated, lobby %s, ticket %u bytes", out->lobby[0] ? out->lobby : "(config)",
                      (unsigned)strlen(out->ticket));
            notify_push(NOTIFY_SUCCESS, "Connected to MKWMP");
            return out->state;
        }
        if (out->state == SEAT_REFUSED) {
            mkwmp_log("seat: refused: %s", out->message);
            notify_push(NOTIFY_ERROR, out->message);
            return out->state;
        }
        if (out->state == SEAT_UNAVAILABLE) {
            mkwmp_log("seat: launcher or seat server unavailable");
            notify_push(NOTIFY_WARN, "MKWMP launcher not reachable");
            return out->state;
        }
        if (out->position != last_pos) {
            char msg[96];
            snprintf(msg, sizeof msg, "Queue: %d of %d", out->position, out->total);
            mkwmp_log("seat: %s", msg);
            notify_push(NOTIFY_INFO, msg);
            last_pos = out->position;
        }
        if (waited >= max_wait_ms) return out->state;
        Sleep(2000);
        waited += 2000;
    }
}

static volatile LONG g_ka_started, g_ka_paused;

int seat_keepalive_pause(void) {
    return g_ka_started && !InterlockedExchange(&g_ka_paused, 1);
}
void seat_keepalive_resume(void) { InterlockedExchange(&g_ka_paused, 0); }
int seat_keepalive_paused(void) { return g_ka_started && g_ka_paused; }

static DWORD WINAPI keepalive(LPVOID unused) {
    (void)unused;
    for (;;) {
        Sleep(15000);
        if (g_ka_paused) continue;
        SeatReply r;
        if (seat_post(&r) == 0 && r.state != SEAT_SEATED)
            mkwmp_log("seat keepalive: state %d (position %d)", (int)r.state, r.position);
    }
    return 0;
}

void seat_keepalive_start(void) {
    InterlockedExchange(&g_ka_paused, 0);
    if (InterlockedExchange(&g_ka_started, 1)) return;
    HANDLE h = CreateThread(NULL, 0, keepalive, NULL, 0, NULL);
    if (h) CloseHandle(h);
}
