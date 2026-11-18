// The launcher's local API answered in the game (seatd.h): the seat server called directly with the mod's settings.
#include "seatd.h"

#include "config.h"
#include "log.h"
#include "plat.h"
#include "seatapi.h"

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum { MUTES_MAX = 64, FRIENDS_TTL_MS = 10000 };

static PlatLock g_lock = PLAT_LOCK_INIT;   // one request at a time (launcher.c serialises too)
static SeatdConfig C;
static int g_started;
static int g_have_status;
static char g_sid[80], g_skey[160];          // the seat session
static char g_presence[160];                 // the friends routes' token
static struct { unsigned long long account; char name[40]; } g_mutes[MUTES_MAX];
static int g_nmutes;

// A server message inside our JSON: no quotes, backslashes or control characters.
static const char *clean(const char *m) {
    static _Thread_local char out[200];
    size_t k = 0;
    for (size_t i = 0; m && m[i] && k + 1 < sizeof out; i++)
        out[k++] = (m[i] == '"' || m[i] == '\\' || (unsigned char)m[i] < 0x20) ? ' ' : m[i];
    out[k] = 0;
    return out;
}

static int reply(char *buf, size_t cap, const char **body, int status, const char *fmt, ...) __attribute__((format(printf, 5, 6)));
static int reply(char *buf, size_t cap, const char **body, int status, const char *fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, cap, fmt, ap);
    va_end(ap);
    *body = buf;
    return status;
}

void seatd_start(const SeatdConfig *cfg) {
    plat_lock(&g_lock);
    C = *cfg;
    g_started = 1;
    plat_unlock(&g_lock);
    mkwmp_log("seat: no launcher; the plugin signs in to %s as %s itself", C.server, C.name);
}

// The lobby, its CA and the voice port, once per game (lock held).
static void fetch_status(void) {
    if (g_have_status) return;
    SeatApiStatus s;
    SeatApiResult r = seatapi_status(C.server, &s);
    if (r.status != 200) {
        mkwmp_log("seat: status of %s: %d %s", C.server, r.status, r.message);
        return;
    }
    g_have_status = 1;
    mkwmp_config_set_voice_port(C.voice ? s.voice_port : 0);
    char path[1024];
    if (plat_home_path(path, sizeof path, "mkwmp-lobby-ca.pem")) {
        if (s.lobby_ca) {
            FILE *f = fopen(path, "wb");
            if (f) {
                fputs(s.lobby_ca, f);
                fclose(f);
            }
        } else {
            remove(path);   // the server has none: the built-in one
        }
    }
    mkwmp_log("seat: server %s: lobby %s, voice port %d, %d/%d seats taken, %d queued%s", C.server, s.lobby, s.voice_port,
              s.seated, s.seats_total, s.queued, s.lobby_ca ? ", its lobby CA saved" : "");
    free(s.lobby_ca);
}

// The answer seat.c understands for a view (seat_parse).
static int view_reply(const SeatApiView *v, char *buf, size_t cap, const char **body) {
    if (!strcmp(v->state, "seated") && v->ticket[0])
        return reply(buf, cap, body, 200, "{\"state\":\"seated\",\"ticket\":\"%s\",\"lobby\":\"%s\",\"name\":\"%s\"}",
                     v->ticket, v->ticket_lobby, C.name);
    return reply(buf, cap, body, 200, "{\"state\":\"queued\",\"position\":%d,\"seatsTotal\":%d}", v->position,
                 v->seats_total);
}

static int refused(const SeatApiResult *r, char *buf, size_t cap, const char **body) {
    const char *why = !strcmp(r->code, "PIN_WRONG")           ? "Wrong PIN: open Mods > MKWMP > Options and enter it again"
                      : !strcmp(r->code, "PROFILE_NOT_FOUND") ? "This name is not registered: register it in Mods > MKWMP > Options"
                      : !strcmp(r->code, "NAME_TAKEN")        ? "This name is already online (the MKWMP launcher, or another PC)"
                                                              : r->message;
    mkwmp_log("seat: refused: %s (%s)", r->code, r->message);
    return reply(buf, cap, body, 409, "{\"state\":\"refused\",\"message\":\"%s\"}", clean(why));
}

static int seat(char *buf, size_t cap, const char **body) {
    fetch_status();
    SeatApiView v;
    SeatApiResult r;
    if (g_sid[0]) {
        r = seatapi_poll(C.server, g_sid, g_skey, &v);   // the heartbeat
        if (r.status == 200) return view_reply(&v, buf, cap, body);
        if (r.status != 404) {
            mkwmp_log("seat: heartbeat: %d %s", r.status, r.message);
            return reply(buf, cap, body, 503, "{\"state\":\"unavailable\",\"message\":\"%s\"}", clean(r.message));
        }
        mkwmp_log("seat: session gone (server restarted or timed out): joining again");
        g_sid[0] = g_skey[0] = 0;
    }
    r = seatapi_join(C.server, C.name, C.pin, &v);
    if (r.status == 201 || r.status == 200) {
        snprintf(g_sid, sizeof g_sid, "%s", v.session_id);
        snprintf(g_skey, sizeof g_skey, "%s", v.session_key);
        mkwmp_log("seat: joined as %s: %s%s", C.name, v.state, v.ticket[0] ? " with a ticket" : "");
        return view_reply(&v, buf, cap, body);
    }
    if (r.status == 401 || r.status == 404 || (r.status == 409 && !strcmp(r.code, "NAME_TAKEN")) || r.status == 400)
        return refused(&r, buf, cap, body);
    mkwmp_log("seat: join: %d %s %s (retry after %d s)", r.status, r.code, r.message, r.retry_after);
    return reply(buf, cap, body, 503, "{\"state\":\"unavailable\",\"message\":\"%s\"}", clean(r.message));
}

static int friends(char *buf, size_t cap, const char **body) {
    static SeatApiFriends f;
    static uint64_t at;
    static int have;
    uint64_t now = plat_ms();
    if (!have || now - at > FRIENDS_TTL_MS) {
        SeatApiResult r = {0};
        for (int attempt = 0; attempt < 2; attempt++) {
            if (!g_presence[0]) {
                r = seatapi_presence(C.server, C.name, C.pin, g_presence, sizeof g_presence);
                if (!g_presence[0]) break;
            }
            r = seatapi_friends(C.server, g_presence, &f);
            if (r.status != 401) break;
            g_presence[0] = 0;   // expired: sign in again, once
        }
        if (r.status != 200) {
            if (!have) return reply(buf, cap, body, 503, "{\"state\":\"unavailable\",\"message\":\"%s\"}", clean(r.message));
        } else {
            have = 1;
            at = now;
        }
    }
    size_t n = (size_t)snprintf(buf, cap, "{\"friends\":[");
    for (int i = 0; i < f.nfriends && n + 64 < cap; i++)
        n += (size_t)snprintf(buf + n, cap - n, "%s{\"name\":\"%s\",\"online\":%s}", i ? "," : "", f.friends[i].name,
                              strcmp(f.friends[i].state, "offline") ? "true" : "false");
    snprintf(buf + n, cap - n, "]}");
    *body = buf;
    return 200;
}

static int voice_get(char *buf, size_t cap, const char **body) {
    size_t n = (size_t)snprintf(buf, cap, "{\"enabled\":%s,\"input\":\"\",\"output\":\"\",\"inputGain\":100,"
                                          "\"outputGain\":100,\"mutes\":[", C.voice ? "true" : "false");
    for (int i = 0; i < g_nmutes && n + 80 < cap; i++)
        n += (size_t)snprintf(buf + n, cap - n, "%s{\"account\":\"%llu\",\"name\":\"%s\"}", i ? "," : "",
                              g_mutes[i].account, g_mutes[i].name);
    snprintf(buf + n, cap - n, "]}");
    *body = buf;
    return 200;
}

// {"account":"123","name":"x","muted":true}: kept for this game (the launcher keeps them across games)
static int voice_mute(const char *json, char *buf, size_t cap, const char **body) {
    char num[32] = "", name[40] = "";
    seatapi_json_str(json, "account", num, sizeof num);
    seatapi_json_str(json, "name", name, sizeof name);
    unsigned long long account = strtoull(num, NULL, 10);
    int muted = json && strstr(json, "\"muted\":true");
    if (!account) return reply(buf, cap, body, 400, "{}");
    int at = -1;
    for (int i = 0; i < g_nmutes; i++)
        if (g_mutes[i].account == account) at = i;
    if (muted && at < 0 && g_nmutes < MUTES_MAX) {
        g_mutes[g_nmutes].account = account;
        for (size_t i = 0, k = 0; name[i] && k + 1 < sizeof g_mutes[0].name; i++)   // shown back in JSON: no quotes
            if (name[i] != '"' && name[i] != '\\') g_mutes[g_nmutes].name[k++] = name[i];
        g_nmutes++;
    } else if (!muted && at >= 0) {
        g_mutes[at] = g_mutes[--g_nmutes];
    }
    return reply(buf, cap, body, 204, "");
}

int seatd_request(const char *method, const char *path, const char *json, char *buf, size_t cap, const char **body) {
    if (!cap) return 0;
    buf[0] = 0;
    *body = buf;
    plat_lock(&g_lock);
    int st;
    if (!g_started) st = 0;
    else if (!strcmp(path, "/local/v1/seat") && !strcmp(method, "POST")) st = seat(buf, cap, body);
    else if (!strcmp(path, "/local/v1/friends")) st = friends(buf, cap, body);
    else if (!strcmp(path, "/local/v1/voice") && !strcmp(method, "GET")) st = voice_get(buf, cap, body);
    else if (!strcmp(path, "/local/v1/voice/mute")) st = voice_mute(json, buf, cap, body);
    else if (!strcmp(path, "/local/v1/voice/devices")) st = reply(buf, cap, body, 204, "");
    else st = reply(buf, cap, body, 404, "{}");   // /local/v1/room is gone: the runtime's number pad asks
    plat_unlock(&g_lock);
    return st;
}

void seatd_stop(void) {
    plat_lock(&g_lock);
    if (g_sid[0]) seatapi_leave(C.server, g_sid, g_skey);
    if (g_presence[0]) seatapi_presence_end(C.server, g_presence);
    g_sid[0] = g_presence[0] = 0;
    plat_unlock(&g_lock);
}
