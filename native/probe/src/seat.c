#include "seat.h"
#include "launcher.h"
#include "log.h"
#include "notify.h"
#include "config.h"


#include "plat.h"
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

// The seat reply's optional "room" object: the room as the lobby numbers it,
//   "room": {"lobby": "<lobby uuid>", "ussid": 3, "ussids": [3, 5, 12]}     (or "room": null: in none)
// which the game process cannot read itself (Gamesync is TLS inside the guest). Handed to the sink (probe.c: the
// crash record's breadcrumbs). A reply without the key says nothing and changes nothing.
static SeatRoomSink g_room_sink;

void seat_set_room_sink(SeatRoomSink sink) { g_room_sink = sink; }

void seat_parse_room(const char *body) {
    SeatRoomSink sink = g_room_sink;
    const char *p = body ? strstr(body, "\"room\"") : NULL;
    if (!sink || !p) return;
    p += 6;
    while (*p == ' ' || *p == ':') p++;
    if (*p != '{') { sink(NULL, 0, NULL, 0); return; }
    const char *end = strchr(p, '}');
    if (!end || end - p >= 1024) return;
    char object[1024];
    memcpy(object, p, (size_t)(end - p) + 1);
    object[end - p + 1] = 0;
    char lobby[40] = "";
    int ussid = 0, n = 0;
    uint32_t ussids[24];
    if (!json_str(object, "lobby", lobby, sizeof lobby) || !json_int(object, "ussid", &ussid) || ussid <= 0) return;
    const char *list = strstr(object, "\"ussids\"");
    list = list ? strchr(list, '[') : NULL;
    for (const char *q = list ? list + 1 : NULL; q && *q && *q != ']' && n < 24;) {
        while (*q == ' ' || *q == ',') q++;
        if (*q < '0' || *q > '9') break;
        ussids[n++] = (uint32_t)strtoul(q, (char **)&q, 10);
    }
    sink(lobby, (uint32_t)ussid, ussids, n);
}

static int seat_post_once(SeatReply *out);static PlatLock g_ticket_lock = PLAT_LOCK_INIT;
static char g_ticket[520];

int seat_ticket(char *dst, size_t n) {
    plat_lock_shared(&g_ticket_lock);
    snprintf(dst, n, "%s", g_ticket);
    plat_unlock_shared(&g_ticket_lock);
    return dst[0] != 0;
}

int seat_post(SeatReply *out) {
    int rc = seat_post_once(out);
    if (rc == 0 && out->state == SEAT_SEATED) {
        plat_lock(&g_ticket_lock);
        snprintf(g_ticket, sizeof g_ticket, "%s", out->ticket);   // renewed tickets replace the old
        plat_unlock(&g_ticket_lock);
    }
    return rc;
}

static int seat_post_once(SeatReply *out) {
    char buf[4096];
    const char *body;
    int status = launcher_request("POST", "/local/v1/seat", buf, sizeof buf, &body);
    if (status < 0) return -1;
    seat_parse(status, status ? body : NULL, out);
    if (status == 200) seat_parse_room(body);
    return 0;
}

// The seat's notices (the launcher's tray said the same): each once per change of state, so the game's Online Play
// after a seat taken at start (seat_take_early) does not say "connected" again.
static volatile int32_t g_said;   // the SeatState last told the player, + 1 (0: none yet)

static int tell(SeatState st) { return plat_xchg(&g_said, (int32_t)st + 1) != (int32_t)st + 1; }

SeatState seat_acquire(SeatReply *out, int max_wait_ms) {
    int waited = 0, last_pos = -1;
    for (;;) {
        if (seat_post(out) < 0) { out->state = SEAT_UNAVAILABLE; return out->state; }
        if (out->state == SEAT_SEATED) {
            mkwmp_event("seat: seated, lobby %s, ticket %u bytes", out->lobby[0] ? out->lobby : "(config)",
                      (unsigned)strlen(out->ticket));
            if (tell(SEAT_SEATED)) notify_push(NOTIFY_SUCCESS, "Connected to MKWMP: you have a seat");
            return out->state;
        }
        if (out->state == SEAT_REFUSED) {
            mkwmp_event("seat: refused: %s", out->message);
            if (tell(SEAT_REFUSED)) notify_push(NOTIFY_ERROR, out->message);
            return out->state;
        }
        if (out->state == SEAT_UNAVAILABLE) {
            int direct = mkwmp_config()->direct;
            mkwmp_event("seat: %s unavailable", direct ? "seat server" : "launcher or seat server");
            if (tell(SEAT_UNAVAILABLE))
                notify_push(NOTIFY_WARN, direct ? "MKWMP server not reachable: playing offline"
                                                : "MKWMP launcher not reachable");
            return out->state;
        }
        if (out->position != last_pos) {
            char msg[128];
            snprintf(msg, sizeof msg, "All seats are taken: you are #%d in line (%d seats)", out->position, out->total);
            mkwmp_event("seat: queued %d of %d", out->position, out->total);
            plat_xchg(&g_said, (int32_t)SEAT_QUEUED + 1);
            notify_push(NOTIFY_INFO, msg);
            last_pos = out->position;
        }
        if (waited >= max_wait_ms) return out->state;
        plat_sleep_ms(2000);
        waited += 2000;
    }
}

static volatile int32_t g_ka_started, g_ka_paused;

int seat_keepalive_pause(void) {
    return g_ka_started && !plat_xchg(&g_ka_paused, 1);
}
void seat_keepalive_resume(void) { plat_xchg(&g_ka_paused, 0); }
int seat_keepalive_paused(void) { return g_ka_started && g_ka_paused; }

static void keepalive(void *unused) {
    (void)unused;
    for (;;) {
        plat_sleep_ms(15000);
        if (g_ka_paused) continue;
        SeatReply r;
        if (seat_post(&r) == 0 && r.state != SEAT_SEATED)
            mkwmp_event("seat keepalive: state %d (position %d)", (int)r.state, r.position);
    }
}

void seat_keepalive_start(void) {
    plat_xchg(&g_ka_paused, 0);
    if (plat_xchg(&g_ka_started, 1)) return;
    plat_thread_start(keepalive, NULL);
}

// The plugin without a launcher (seat mode "start", as the launcher's default): a seat taken when the game starts,
// not at Online Play, so the queue is waited out in the menus. A thread: the game goes on meanwhile.
static void take_early(void *unused) {
    (void)unused;
    notify_push(NOTIFY_INFO, "Connecting to MKWMP...");
    SeatReply r;
    if (seat_acquire(&r, 60 * 60 * 1000) == SEAT_SEATED) seat_keepalive_start();
}

void seat_take_early(void) {
    static volatile int32_t started;
    if (!plat_xchg(&started, 1)) plat_thread_start(take_early, NULL);
}
