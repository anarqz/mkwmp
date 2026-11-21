// The launcher's local API answered in the game (seatd.h): the seat server called directly with the mod's settings.
#include "seatd.h"

#include "config.h"
#include "log.h"
#include "notify.h"
#include "plat.h"
#include "seatapi.h"

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum { MUTES_MAX = SEATD_MUTES_MAX };

static PlatLock g_lock = PLAT_LOCK_INIT;   // one request at a time (launcher.c serialises too)
static SeatdConfig C;
static int g_started;
static int g_have_status;
static char g_sid[80], g_skey[160];          // the seat session
static SeatdMute g_mutes[MUTES_MAX];
static int g_nmutes;
enum { MUTE_KEYS = 3, PER_KEY = 22 };   // 64 of "id:name" (20 digits, 16 characters) in settings of <= 1024 bytes
_Static_assert(MUTE_KEYS * PER_KEY >= MUTES_MAX, "every mute fits the settings");

// The friends watcher's (g_flock: the long poll does not hold g_lock)
static PlatLock g_flock = PLAT_LOCK_INIT;
static char g_presence[160];                 // the friends routes' token
static SeatApiFriends g_friends;
static int g_have_friends;
static char g_devices[8192];                 // the voice engine's last device list (POST /local/v1/voice/devices)

// ---- texts of the notices (English, Brazilian Portuguese)
enum { N_ONLINE, N_PLAYING, N_WANTS, N_ACCEPTED, N_MORE, N_FRIENDS_ON, N_ONE_ON, N_COUNT };
static const char *const N_EN[N_COUNT] = {"%s is online", "%s is playing online", "%s wants to be your friend",
                                          "%s accepted your friend request", "and %d more", "%d friends online: %s",
                                          "%s is online"};
static const char *const N_PT[N_COUNT] = {"%s está online", "%s está jogando online", "%s quer ser seu amigo",
                                          "%s aceitou seu pedido de amizade", "e mais %d", "%d amigos online: %s",
                                          "%s está online"};
static const char *nt(int id) { return C.lang_pt ? N_PT[id] : N_EN[id]; }

// ---- mutes, kept in the mod's settings as "id:name,..." (lock held)
static void mutes_load(void) {
    g_nmutes = 0;
    if (!C.setting_get) return;
    for (int k = 1; k <= MUTE_KEYS; k++) {
        char key[16], list[1100];
        snprintf(key, sizeof key, "mutes.%d", k);
        if (!C.setting_get(key, list, sizeof list)) continue;
        for (char *t = list, *next; t && *t && g_nmutes < MUTES_MAX; t = next) {
            next = strchr(t, ',');
            if (next) *next++ = 0;
            char *colon = strchr(t, ':');
            if (colon) *colon++ = 0;
            unsigned long long id = strtoull(t, NULL, 10);
            if (!id) continue;
            g_mutes[g_nmutes].account = id;
            snprintf(g_mutes[g_nmutes++].name, sizeof g_mutes[0].name, "%s", colon && seatapi_name_ok(colon) ? colon : "");
        }
    }
}

static void mutes_save(void) {
    if (!C.setting_set) return;
    for (int k = 0; k < MUTE_KEYS; k++) {
        char key[16], list[PER_KEY * 40 + 1] = "";
        size_t n = 0;
        for (int i = k * PER_KEY; i < g_nmutes && i < (k + 1) * PER_KEY; i++)
            n += (size_t)snprintf(list + n, sizeof list - n, "%s%llu%s%s", n ? "," : "", g_mutes[i].account,
                                  g_mutes[i].name[0] ? ":" : "", g_mutes[i].name);
        snprintf(key, sizeof key, "mutes.%d", k + 1);
        C.setting_set(key, n ? list : NULL);
    }
}

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

// ---- the friends watcher: the server's long poll, a notice per change (notify.c: mkwmp.log only, nothing in the game)
static int find(const SeatApiFriend *list, int n, const char *name) {
    for (int i = 0; i < n; i++)
        if (!strcmp(list[i].name, name)) return i;
    return -1;
}

static void notices(const SeatApiFriends *was, const SeatApiFriends *now) {
    char text[160];
    int said = 0, more = 0;
    for (int i = 0; i < now->nfriends; i++) {
        const SeatApiFriend *f = &now->friends[i];
        int at = find(was->friends, was->nfriends, f->name);
        const char *before = at >= 0 ? was->friends[at].state : "";
        int id = -1;
        if (at < 0 && find(was->outgoing, was->noutgoing, f->name) >= 0) id = N_ACCEPTED;
        else if (!strcmp(f->state, "playing") && strcmp(before, "playing")) id = N_PLAYING;
        else if (!strcmp(f->state, "online") && (!strcmp(before, "offline") || at < 0)) id = N_ONLINE;
        if (id < 0) continue;
        if (said++ < 3) {
            snprintf(text, sizeof text, nt(id), f->name);
            notify_push(id == N_ACCEPTED ? NOTIFY_SUCCESS : NOTIFY_INFO, text);
        } else {
            more++;
        }
    }
    for (int i = 0; i < now->nincoming; i++) {
        if (find(was->incoming, was->nincoming, now->incoming[i].name) >= 0) continue;
        if (said++ < 3) {
            snprintf(text, sizeof text, nt(N_WANTS), now->incoming[i].name);
            notify_push(NOTIFY_INFO, text);
        } else {
            more++;
        }
    }
    if (more) {
        snprintf(text, sizeof text, nt(N_MORE), more);
        notify_push(NOTIFY_INFO, text);
    }
}

// The first answer: who is online already, in one notice.
static void first_notice(const SeatApiFriends *now) {
    char names[120] = "";
    int on = 0;
    size_t n = 0;
    for (int i = 0; i < now->nfriends; i++) {
        if (!strcmp(now->friends[i].state, "offline")) continue;
        if (on++ < 3) n += (size_t)snprintf(names + n, sizeof names - n, "%s%s", n ? ", " : "", now->friends[i].name);
    }
    char text[200];
    if (on == 1) snprintf(text, sizeof text, nt(N_ONE_ON), names);
    else if (on > 1) snprintf(text, sizeof text, nt(N_FRIENDS_ON), on, names);
    if (on) notify_push(NOTIFY_INFO, text);
    for (int i = 0; i < now->nincoming && i < 3; i++) {
        snprintf(text, sizeof text, nt(N_WANTS), now->incoming[i].name);
        notify_push(NOTIFY_INFO, text);
    }
}

// A call with name and PIN answered 404 PROFILE_NOT_FOUND: the server purged the profile (30 days unused) and the
// player gets it back with the PIN saved in the mod's settings, as the launcher's presence_token() does. 1: restored,
// call again; 0: *r says why not (409 NAME_REGISTERED: someone else has the name now).
static int restored(SeatApiResult *r) {
    if (r->status != 404 || strcmp(r->code, "PROFILE_NOT_FOUND")) return 0;
    SeatApiResult q = seatapi_restore(C.server, C.name, C.pin);
    if (q.status == 201) {
        mkwmp_log("seat: profile %s was purged by the server: restored with the saved PIN", C.name);
        return 1;
    }
    mkwmp_log("seat: restoring profile %s: %d %s %s", C.name, q.status, q.code, q.message);
    if (q.status == 409 || q.status == 403 || q.status == 400) *r = q;
    return 0;
}

static void watcher(void *unused) {
    (void)unused;
    static SeatApiFriends now, was;
    char cursor[64] = "", next[64], token[160] = "";
    int have = 0, backoff = 5;
    for (;;) {
        if (!token[0]) {
            SeatApiResult r = seatapi_presence(C.server, C.name, C.pin, token, sizeof token);
            if (!token[0] && restored(&r)) r = seatapi_presence(C.server, C.name, C.pin, token, sizeof token);
            if (!token[0]) {
                mkwmp_log("friends: sign-in: %d %s", r.status, r.message);
                plat_sleep_ms((uint32_t)(r.retry_after > 0 ? r.retry_after : backoff) * 1000);
                if (backoff < 120) backoff *= 2;
                continue;
            }
            plat_lock(&g_flock);
            snprintf(g_presence, sizeof g_presence, "%s", token);
            plat_unlock(&g_flock);
            cursor[0] = 0;
            backoff = 5;
        }
        SeatApiResult r = seatapi_friends_wait(C.server, token, cursor, next, sizeof next, &now);
        if (r.status == 401) {   // expired, or another sign-in replaced it: once more
            token[0] = 0;
            continue;
        }
        if (r.status != 200) {
            mkwmp_log("friends: %d %s", r.status, r.message);
            plat_sleep_ms(15000);
            continue;
        }
        if (have) notices(&was, &now);
        else first_notice(&now);
        plat_lock(&g_flock);
        g_friends = now;
        g_have_friends = 1;
        plat_unlock(&g_flock);
        was = now;
        have = 1;
        if (!next[0] || !strcmp(next, cursor)) plat_sleep_ms(10000);   // a server without the long poll
        snprintf(cursor, sizeof cursor, "%s", next);
    }
}

void seatd_start(const SeatdConfig *cfg) {
    static int watching;
    plat_lock(&g_lock);
    C = *cfg;
    g_started = 1;
    mutes_load();
    plat_unlock(&g_lock);
    mkwmp_log("seat: no launcher; the plugin signs in to %s as %s itself (%d muted player(s) kept)", C.server, C.name,
              g_nmutes);
    if (cfg->watch_friends && !watching) watching = plat_thread_start(watcher, NULL);
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
    mkwmp_config_set_voice_port(s.voice_port);   // voice on/off is the settings' (voice_get), live
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
        return reply(buf, cap, body, 200, "{\"state\":\"seated\",\"ticket\":\"%s\",\"lobby\":\"%s\",\"name\":\"%s\",\"room\":%s}",
                     v->ticket, v->ticket_lobby, C.name, v->room[0] ? v->room : "null");
    return reply(buf, cap, body, 200, "{\"state\":\"queued\",\"position\":%d,\"seatsTotal\":%d}", v->position,
                 v->seats_total);
}

static int refused(const SeatApiResult *r, char *buf, size_t cap, const char **body) {
    const char *why = !strcmp(r->code, "PIN_WRONG")           ? "Wrong PIN: open Mods > MKWMP > Options and enter it again"
                      : !strcmp(r->code, "PROFILE_NOT_FOUND") ? "This name is not registered: register it in Mods > MKWMP > Options"
                      : !strcmp(r->code, "NAME_TAKEN")        ? "This name is already online (the MKWMP launcher, or another PC)"
                      : !strcmp(r->code, "NAME_REGISTERED")   ? "Your name expired and someone else took it: register another in Mods > MKWMP > Options"
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
            mkwmp_event("seat: heartbeat: %d %s", r.status, r.message);
            return reply(buf, cap, body, 503, "{\"state\":\"unavailable\",\"message\":\"%s\"}", clean(r.message));
        }
        mkwmp_event("seat: session gone (server restarted or timed out): joining again");
        g_sid[0] = g_skey[0] = 0;
    }
    r = seatapi_join(C.server, C.name, C.pin, &v);
    if (restored(&r)) r = seatapi_join(C.server, C.name, C.pin, &v);
    if (r.status == 201 || r.status == 200) {
        snprintf(g_sid, sizeof g_sid, "%s", v.session_id);
        snprintf(g_skey, sizeof g_skey, "%s", v.session_key);
        mkwmp_event("seat: joined as %s: %s%s", C.name, v.state, v.ticket[0] ? " with a ticket" : "");
        return view_reply(&v, buf, cap, body);
    }
    if (r.status == 401 || r.status == 403 || r.status == 404 || r.status == 400 ||
        (r.status == 409 && (!strcmp(r.code, "NAME_TAKEN") || !strcmp(r.code, "NAME_REGISTERED"))))
        return refused(&r, buf, cap, body);
    mkwmp_event("seat: join: %d %s %s (retry after %d s)", r.status, r.code, r.message, r.retry_after);
    return reply(buf, cap, body, 503, "{\"state\":\"unavailable\",\"message\":\"%s\"}", clean(r.message));
}

// The game's friend list: the watcher's last answer, else one fetch of our own.
static int friends(char *buf, size_t cap, const char **body) {
    static SeatApiFriends f;
    plat_lock(&g_flock);
    int have = g_have_friends;
    if (have) f = g_friends;
    plat_unlock(&g_flock);
    if (!have) {
        char token[160] = "";
        SeatApiResult r = seatapi_presence(C.server, C.name, C.pin, token, sizeof token);
        if (!token[0] && restored(&r)) r = seatapi_presence(C.server, C.name, C.pin, token, sizeof token);
        if (token[0]) r = seatapi_friends(C.server, token, &f);
        if (r.status != 200) return reply(buf, cap, body, 503, "{\"state\":\"unavailable\",\"message\":\"%s\"}", clean(r.message));
        if (!C.watch_friends) seatapi_presence_end(C.server, token);
        else {
            plat_lock(&g_flock);
            if (!g_presence[0]) snprintf(g_presence, sizeof g_presence, "%s", token);
            plat_unlock(&g_flock);
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

// The voice settings: the mod's (its in-game Voice chat tab and options page), read at each poll so a change applies
// at once. Device ids go into JSON: quotes and backslashes are not kept.
static void setting(const char *key, const char *fallback, char *out, size_t cap) {
    if (!C.setting_get || !C.setting_get(key, out, cap)) snprintf(out, cap, "%s", fallback);
    for (char *q = out; *q; q++)
        if (*q == '"' || *q == '\\' || (unsigned char)*q < 0x20) *q = ' ';
}

static int gain(const char *key) {
    char v[16];
    setting(key, "100", v, sizeof v);
    int g = atoi(v);
    return g < 1 ? 1 : g > 100 ? 100 : g;
}

static int voice_get(char *buf, size_t cap, const char **body) {
    char on[8], input[512], output[512];
    setting("voice", C.voice ? "1" : "0", on, sizeof on);
    setting("voice_input", "", input, sizeof input);
    setting("voice_output", "", output, sizeof output);
    size_t n = (size_t)snprintf(buf, cap, "{\"enabled\":%s,\"input\":\"%s\",\"output\":\"%s\",\"inputGain\":%d,"
                                          "\"outputGain\":%d,\"mutes\":[", strcmp(on, "0") ? "true" : "false", input,
                                output, gain("voice_in_gain"), gain("voice_out_gain"));
    for (int i = 0; i < g_nmutes && n + 80 < cap; i++)
        n += (size_t)snprintf(buf + n, cap - n, "%s{\"account\":\"%llu\",\"name\":\"%s\"}", i ? "," : "",
                              g_mutes[i].account, g_mutes[i].name);
    snprintf(buf + n, cap - n, "]}");
    *body = buf;
    return 200;
}

// {"account":"123","name":"x","muted":true} (LAUNCHER_API.md's POST /local/v1/voice/mute; the mod itself no longer
// posts it since it stopped drawing a mute menu in the game): kept across games
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
        snprintf(g_mutes[g_nmutes].name, sizeof g_mutes[0].name, "%s", seatapi_name_ok(name) ? name : "");
        g_nmutes++;
    } else if (!muted && at >= 0) {
        g_mutes[at] = g_mutes[--g_nmutes];
    } else {
        return reply(buf, cap, body, 204, "");   // no change
    }
    mutes_save();
    return reply(buf, cap, body, 204, "");
}

static int voice_devices(const char *json, char *buf, size_t cap, const char **body) {
    plat_lock(&g_flock);
    snprintf(g_devices, sizeof g_devices, "%s", json ? json : "");
    plat_unlock(&g_flock);
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
    else if (!strcmp(path, "/local/v1/voice/devices")) st = voice_devices(json, buf, cap, body);
    else st = reply(buf, cap, body, 404, "{}");   // /local/v1/room is gone: the runtime's number pad asks
    plat_unlock(&g_lock);
    return st;
}

int seatd_devices_json(char *out, size_t cap) {
    plat_lock(&g_flock);
    snprintf(out, cap, "%s", g_devices);
    plat_unlock(&g_flock);
    return out[0] != 0;
}

int seatd_mutes(SeatdMute *out, int max) {
    plat_lock(&g_lock);
    int n = g_nmutes < max ? g_nmutes : max;
    memcpy(out, g_mutes, (size_t)n * sizeof *out);
    plat_unlock(&g_lock);
    return n;
}

void seatd_unmute(unsigned long long account) {
    plat_lock(&g_lock);
    for (int i = 0; i < g_nmutes; i++)
        if (g_mutes[i].account == account) {
            g_mutes[i] = g_mutes[--g_nmutes];
            mutes_save();
            break;
        }
    plat_unlock(&g_lock);
}

void seatd_stop(void) {
    plat_lock(&g_lock);
    plat_lock(&g_flock);
    if (g_sid[0] || g_presence[0]) {
        seatapi_set_timeout_ms(1500);   // the process ends right after: a slow server must not hold it
        if (g_sid[0]) {
            SeatApiResult r = seatapi_leave(C.server, g_sid, g_skey);
            mkwmp_event("seat: given back (%d)", r.status);
        }
        if (g_presence[0]) seatapi_presence_end(C.server, g_presence);
        seatapi_set_timeout_ms(0);
    }
    g_sid[0] = g_presence[0] = 0;
    plat_unlock(&g_flock);
    plat_unlock(&g_lock);
}
