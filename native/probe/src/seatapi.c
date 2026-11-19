// The seat server's API called directly (seatapi.h; docs/contracts/SEAT_API.md).
#include "seatapi.h"

#include "plat_http.h"

#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static volatile int g_timeout_ms = 10000;

void seatapi_set_timeout_ms(int ms) { g_timeout_ms = ms > 0 ? ms : 10000; }

int seatapi_name_ok(const char *name) {
    size_t n = name ? strlen(name) : 0;
    if (n < 3 || n > SEATAPI_NAME_MAX) return 0;
    for (size_t i = 0; i < n; i++)
        if (!isalnum((unsigned char)name[i]) && name[i] != '_' && name[i] != '-') return 0;
    return 1;
}

int seatapi_pin_ok(const char *pin) {
    if (!pin || strlen(pin) != SEATAPI_PIN_LEN) return 0;
    for (int i = 0; i < SEATAPI_PIN_LEN; i++)
        if (!isdigit((unsigned char)pin[i])) return 0;
    return 1;
}

int seatapi_base(const char *setting, char *out, size_t cap) {
    const char *s = setting && *setting ? setting : SEATAPI_DEFAULT_SERVER;
    while (*s == ' ' || *s == '\t') s++;
    size_t n = strlen(s);
    while (n && (s[n - 1] == ' ' || s[n - 1] == '\t' || s[n - 1] == '/')) n--;
    if (!n) return 0;
    for (size_t i = 0; i < n; i++)
        if ((unsigned char)s[i] <= ' ' || s[i] == '"' || s[i] == '\\') return 0;
    int has_scheme = (n > 7 && !strncmp(s, "http://", 7)) || (n > 8 && !strncmp(s, "https://", 8));
    int w = snprintf(out, cap, "%s%.*s", has_scheme ? "" : "https://", (int)n, s);
    return w > 0 && (size_t)w < cap;
}

// ---- JSON
// The byte after "key": and its spaces, or NULL.
static const char *value_of(const char *body, const char *key) {
    if (!body) return NULL;
    char pat[64];
    snprintf(pat, sizeof pat, "\"%s\"", key);
    for (const char *p = strstr(body, pat); p; p = strstr(p + 1, pat)) {
        const char *q = p + strlen(pat);
        while (*q == ' ' || *q == '\n' || *q == '\r' || *q == '\t') q++;
        if (*q != ':') continue;   // the same text as a value, not a key
        q++;
        while (*q == ' ' || *q == '\n' || *q == '\r' || *q == '\t') q++;
        return q;
    }
    return NULL;
}

int seatapi_json_str(const char *body, const char *key, char *out, size_t cap) {
    const char *p = value_of(body, key);
    if (!p || *p != '"' || !cap) return 0;
    size_t k = 0;
    for (p++; *p && *p != '"'; p++) {
        char c = *p;
        if (c == '\\' && p[1]) {
            c = *++p;
            if (c == 'n') c = '\n';
            else if (c == 'r') c = '\r';
            else if (c == 't') c = '\t';
            else if (c == 'u' && isxdigit((unsigned char)p[1]) && isxdigit((unsigned char)p[2]) &&
                     isxdigit((unsigned char)p[3]) && isxdigit((unsigned char)p[4])) {
                char hex[5] = {p[1], p[2], p[3], p[4], 0};
                long v = strtol(hex, NULL, 16);
                c = v < 0x80 ? (char)v : '?';   // the server's values are ASCII
                p += 4;
            }
        }
        if (k + 1 < cap) out[k++] = c;
    }
    out[k] = 0;
    return *p == '"';
}

int seatapi_json_int(const char *body, const char *key, long long *out) {
    const char *p = value_of(body, key);
    if (!p || !(*p == '-' || isdigit((unsigned char)*p))) return 0;
    *out = strtoll(p, NULL, 10);
    return 1;
}

int seatapi_json_span(const char *body, const char *key, const char **begin, const char **end) {
    const char *p = value_of(body, key);
    if (!p || (*p != '[' && *p != '{')) return 0;
    int depth = 0, in_str = 0;
    for (const char *q = p; *q; q++) {
        if (in_str) {
            if (*q == '\\' && q[1]) q++;
            else if (*q == '"') in_str = 0;
        } else if (*q == '"') {
            in_str = 1;
        } else if (*q == '[' || *q == '{') {
            depth++;
        } else if ((*q == ']' || *q == '}') && --depth == 0) {
            *begin = p;
            *end = q + 1;
            return 1;
        }
    }
    return 0;
}

// ---- calls
static SeatApiResult call_t(const char *method, const char *base, const char *path, const char *bearer,
                            const char *json, char *out, size_t cap, int timeout_ms) {
    char url[600];
    snprintf(url, sizeof url, "%s%s", base, path);
    PlatHttpResult h = plat_http(method, url, bearer, json, out, cap, timeout_ms);
    SeatApiResult r = {0};
    r.status = h.status;
    r.retry_after = h.retry_after;
    if (!h.status) {
        snprintf(r.code, sizeof r.code, "OFFLINE");
        snprintf(r.message, sizeof r.message, "%s", plat_http_error()[0] ? plat_http_error() : "no answer");
    } else if (h.status >= 400) {
        const char *b, *e;
        char err[400] = "";
        if (seatapi_json_span(out, "error", &b, &e) && (size_t)(e - b) < sizeof err) memcpy(err, b, (size_t)(e - b));
        if (!seatapi_json_str(err, "code", r.code, sizeof r.code)) snprintf(r.code, sizeof r.code, "HTTP_%d", h.status);
        if (!seatapi_json_str(err, "message", r.message, sizeof r.message))
            snprintf(r.message, sizeof r.message, "HTTP %d", h.status);
    }
    return r;
}

static SeatApiResult call(const char *method, const char *base, const char *path, const char *bearer,
                          const char *json, char *out, size_t cap) {
    return call_t(method, base, path, bearer, json, out, cap, g_timeout_ms);
}

SeatApiResult seatapi_register(const char *base, const char *name, char pin[SEATAPI_PIN_LEN + 1]) {
    char json[64], out[1024];
    pin[0] = 0;
    if (!seatapi_name_ok(name)) {
        SeatApiResult r = {400, 0, "NAME_INVALID", "3-16 letters, digits, - or _"};
        return r;
    }
    snprintf(json, sizeof json, "{\"name\":\"%s\"}", name);
    SeatApiResult r = call("POST", base, "/seat/v1/profiles", NULL, json, out, sizeof out);
    if (r.status == 201 && !(seatapi_json_str(out, "pin", pin, SEATAPI_PIN_LEN + 1) && seatapi_pin_ok(pin))) {
        r.status = 0;
        snprintf(r.code, sizeof r.code, "BAD_ANSWER");
        snprintf(r.message, sizeof r.message, "the server's answer has no PIN");
    }
    return r;
}

SeatApiResult seatapi_restore(const char *base, const char *name, const char *pin) {
    char json[96], out[1024];
    if (!seatapi_name_ok(name) || !seatapi_pin_ok(pin)) {
        SeatApiResult r = {400, 0, "NAME_INVALID", "3-16 letters, digits, - or _, and a 6-digit PIN"};
        return r;
    }
    snprintf(json, sizeof json, "{\"name\":\"%s\",\"pin\":\"%s\"}", name, pin);
    return call("POST", base, "/seat/v1/profiles", NULL, json, out, sizeof out);
}

SeatApiResult seatapi_status(const char *base, SeatApiStatus *s) {
    enum { CAP = 24576 };   // lobbyCa is at most 16 KiB of PEM
    memset(s, 0, sizeof *s);
    char *out = malloc(CAP);
    if (!out) {
        SeatApiResult r = {0, 0, "NO_MEMORY", "out of memory"};
        return r;
    }
    SeatApiResult r = call("GET", base, "/seat/v1/status", NULL, NULL, out, CAP);
    if (r.status != 200) {
        free(out);
        return r;
    }
    long long v;
    seatapi_json_str(out, "lobbyEndpoint", s->lobby, sizeof s->lobby);
    if (seatapi_json_int(out, "voicePort", &v)) s->voice_port = (int)v;
    if (seatapi_json_int(out, "seatsTotal", &v)) s->seats_total = (int)v;
    if (seatapi_json_int(out, "seated", &v)) s->seated = (int)v;
    if (seatapi_json_int(out, "queued", &v)) s->queued = (int)v;
    char *ca = malloc(16384 + 1);
    if (ca && seatapi_json_str(out, "lobbyCa", ca, 16384 + 1) && strstr(ca, "-----BEGIN CERTIFICATE-----")) s->lobby_ca = ca;
    else free(ca);
    free(out);
    return r;
}

static void parse_view(const char *out, SeatApiView *v) {
    long long n;
    seatapi_json_str(out, "sessionId", v->session_id, sizeof v->session_id);
    seatapi_json_str(out, "sessionKey", v->session_key, sizeof v->session_key);
    seatapi_json_str(out, "state", v->state, sizeof v->state);
    if (seatapi_json_int(out, "position", &n)) v->position = (int)n;
    if (seatapi_json_int(out, "seatsTotal", &n)) v->seats_total = (int)n;
    if (seatapi_json_int(out, "nextPollMs", &n)) v->next_poll_ms = (int)n;
    const char *b, *e;
    char ticket[900] = "";
    if (seatapi_json_span(out, "ticket", &b, &e) && (size_t)(e - b) < sizeof ticket) {
        memcpy(ticket, b, (size_t)(e - b));
        seatapi_json_str(ticket, "token", v->ticket, sizeof v->ticket);
        seatapi_json_str(ticket, "lobby", v->ticket_lobby, sizeof v->ticket_lobby);
    }
}

SeatApiResult seatapi_join(const char *base, const char *name, const char *pin, SeatApiView *v) {
    char json[96], out[2048];
    memset(v, 0, sizeof *v);
    if (!seatapi_name_ok(name) || !seatapi_pin_ok(pin)) {
        SeatApiResult r = {400, 0, "NAME_INVALID", "the name or the PIN is not valid"};
        return r;
    }
    snprintf(json, sizeof json, "{\"name\":\"%s\",\"pin\":\"%s\"}", name, pin);
    SeatApiResult r = call("POST", base, "/seat/v1/sessions", NULL, json, out, sizeof out);
    if (r.status == 201 || r.status == 200) parse_view(out, v);
    return r;
}

SeatApiResult seatapi_poll(const char *base, const char *session_id, const char *session_key, SeatApiView *v) {
    char path[160], out[2048];
    memset(v, 0, sizeof *v);
    snprintf(path, sizeof path, "/seat/v1/sessions/%s", session_id);
    SeatApiResult r = call("GET", base, path, session_key, NULL, out, sizeof out);
    if (r.status == 200) parse_view(out, v);
    return r;
}

SeatApiResult seatapi_leave(const char *base, const char *session_id, const char *session_key) {
    char path[160], out[256];
    snprintf(path, sizeof path, "/seat/v1/sessions/%s", session_id);
    return call("DELETE", base, path, session_key, NULL, out, sizeof out);
}

SeatApiResult seatapi_presence(const char *base, const char *name, const char *pin, char *token, size_t cap) {
    char json[96], out[1024];
    if (cap) token[0] = 0;
    if (!seatapi_name_ok(name) || !seatapi_pin_ok(pin)) {
        SeatApiResult r = {400, 0, "NAME_INVALID", "the name or the PIN is not valid"};
        return r;
    }
    snprintf(json, sizeof json, "{\"name\":\"%s\",\"pin\":\"%s\"}", name, pin);
    SeatApiResult r = call("POST", base, "/seat/v1/presence", NULL, json, out, sizeof out);
    if (r.status == 200 || r.status == 201) seatapi_json_str(out, "token", token, cap);
    return r;
}

SeatApiResult seatapi_presence_end(const char *base, const char *token) {
    char out[256];
    return call("DELETE", base, "/seat/v1/presence", token, NULL, out, sizeof out);
}

// The objects of an array span, each with a "name": into list (state from key state_key, when given).
static int read_list(const char *b, const char *e, const char *state_key, SeatApiFriend *list, int max) {
    int n = 0;
    for (const char *p = b; p && p < e && n < max;) {
        const char *o = strchr(p, '{');
        if (!o || o >= e) break;
        const char *c = strchr(o, '}');
        if (!c || c > e) break;
        char obj[200];
        size_t len = (size_t)(c - o + 1);
        if (len < sizeof obj) {
            memcpy(obj, o, len);
            obj[len] = 0;
            SeatApiFriend f = {{0}, {0}};
            if (seatapi_json_str(obj, "name", f.name, sizeof f.name) && seatapi_name_ok(f.name)) {
                if (state_key) seatapi_json_str(obj, state_key, f.state, sizeof f.state);
                list[n++] = f;
            }
        }
        p = c + 1;
    }
    return n;
}

SeatApiResult seatapi_friends(const char *base, const char *token, SeatApiFriends *f) {
    return seatapi_friends_wait(base, token, NULL, NULL, 0, f);
}

SeatApiResult seatapi_friends_wait(const char *base, const char *token, const char *since, char *cursor,
                                   size_t cursor_cap, SeatApiFriends *f) {
    char out[32768];   // 100 friends and 100 requests
    char path[160];
    memset(f, 0, sizeof *f);
    int wait = since && *since;
    if (wait) snprintf(path, sizeof path, "/seat/v1/friends?since=%s", since);
    else snprintf(path, sizeof path, "/seat/v1/friends");
    // the server holds a request with since up to 25 s
    SeatApiResult r = call_t("GET", base, path, token, NULL, out, sizeof out, wait ? 35000 : g_timeout_ms);
    if (r.status != 200) return r;
    if (cursor && cursor_cap) {
        long long c;
        if (!seatapi_json_str(out, "cursor", cursor, cursor_cap)) {
            if (seatapi_json_int(out, "cursor", &c)) snprintf(cursor, cursor_cap, "%lld", c);
            else cursor[0] = 0;
        }
        for (char *q = cursor; *q; q++)   // goes into a URL: only what a cursor can hold
            if (!((*q >= '0' && *q <= '9') || (*q >= 'a' && *q <= 'z') || (*q >= 'A' && *q <= 'Z') || *q == '-' ||
                  *q == '_' || *q == '.'))
                *q = '_';
    }
    const char *b, *e;
    if (seatapi_json_span(out, "friends", &b, &e)) f->nfriends = read_list(b, e, "state", f->friends, SEATAPI_FRIENDS_MAX);
    if (seatapi_json_span(out, "incoming", &b, &e)) f->nincoming = read_list(b, e, NULL, f->incoming, SEATAPI_REQUESTS_MAX);
    if (seatapi_json_span(out, "outgoing", &b, &e)) f->noutgoing = read_list(b, e, NULL, f->outgoing, SEATAPI_REQUESTS_MAX);
    return r;
}

SeatApiResult seatapi_friend_request(const char *base, const char *token, const char *name) {
    char json[64], out[512];
    if (!seatapi_name_ok(name)) {
        SeatApiResult r = {400, 0, "NAME_INVALID", "3-16 letters, digits, - or _"};
        return r;
    }
    snprintf(json, sizeof json, "{\"name\":\"%s\"}", name);
    return call("POST", base, "/seat/v1/friends", token, json, out, sizeof out);
}

SeatApiResult seatapi_friend_accept(const char *base, const char *token, const char *name) {
    char path[96], out[512];
    if (!seatapi_name_ok(name)) {
        SeatApiResult r = {400, 0, "NAME_INVALID", ""};
        return r;
    }
    snprintf(path, sizeof path, "/seat/v1/friends/%s/accept", name);
    return call("POST", base, path, token, NULL, out, sizeof out);
}

SeatApiResult seatapi_friend_remove(const char *base, const char *token, const char *name) {
    char path[96], out[512];
    if (!seatapi_name_ok(name)) {
        SeatApiResult r = {400, 0, "NAME_INVALID", ""};
        return r;
    }
    snprintf(path, sizeof path, "/seat/v1/friends/%s", name);
    return call("DELETE", base, path, token, NULL, out, sizeof out);
}
