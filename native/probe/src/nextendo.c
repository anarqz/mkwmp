#include "nextendo.h"

#include "plat_net.h"   // before plat.h: Winsock comes before windows.h

#include "launcher.h"
#include "log.h"
#include "uuid5.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// ---- the launcher's JSON (a flat object) --------------------------------------------------------------------------
// The value after `"key":`, or NULL. (A quote inside a string value is escaped, so `"key"` only matches a key.)
static const char *field(const char *json, const char *key) {
    char pat[40];
    snprintf(pat, sizeof pat, "\"%s\"", key);
    for (const char *p = strstr(json, pat); p; p = strstr(p + 1, pat)) {
        const char *q = p + strlen(pat);
        while (*q == ' ' || *q == '\t' || *q == '\r' || *q == '\n') q++;
        if (*q != ':') continue;
        q++;
        while (*q == ' ' || *q == '\t' || *q == '\r' || *q == '\n') q++;
        return q;
    }
    return NULL;
}

// A string value into dst. strict: 1 only when it is there, closed, fits whole and has no escape (tokens, hosts,
// names: a cut or altered one must not be used). Otherwise as much as fits, escapes kept raw (messages for the log).
static int str_field(const char *json, const char *key, char *dst, size_t n, int strict) {
    dst[0] = 0;
    const char *p = field(json, key);
    if (!p || *p != '"') return 0;
    size_t k = 0;
    for (p++; *p && *p != '"'; p++) {
        if (*p == '\\') {
            if (strict) { dst[0] = 0; return 0; }
            if (p[1]) p++;
        }
        if (k + 1 < n) dst[k++] = *p;
        else if (strict) { dst[0] = 0; return 0; }
    }
    dst[k] = 0;
    if (*p != '"' && strict) { dst[0] = 0; return 0; }
    return 1;
}

// An unsigned number (also one in quotes). 0 when it is not there.
static uint64_t u64_field(const char *json, const char *key) {
    const char *p = field(json, key);
    if (p && *p == '"') p++;
    return p && *p >= '0' && *p <= '9' ? strtoull(p, NULL, 10) : 0;
}

static int token_ok(const char *t) {
    if (!t[0]) return 0;
    for (; *t; t++)
        if (*t <= 0x20 || *t >= 0x7f || *t == '"' || *t == '\\') return 0;   // it goes into a JSON string as it is
    return 1;
}

static int host_ok(const char *h) {
    if (!h[0]) return 0;
    for (; *h; h++)
        if (!((*h >= 'a' && *h <= 'z') || (*h >= 'A' && *h <= 'Z') || (*h >= '0' && *h <= '9') || *h == '.' || *h == '-'))
            return 0;
    return 1;
}

static int name_ok(const char *s) {
    if (!s[0]) return 0;
    for (; *s; s++)
        if ((unsigned char)*s < 0x20 || *s == 0x7f) return 0;
    return 1;
}

void nextendo_session_parse(int http_status, const char *body, NxSession *out) {
    memset(out, 0, sizeof *out);
    out->state = NX_UNAVAILABLE;
    char state[24] = "";
    if (body) {
        str_field(body, "state", state, sizeof state, 0);
        str_field(body, "reason", out->reason, sizeof out->reason, 0);
        str_field(body, "message", out->message, sizeof out->message, 0);
    }
    if (http_status == 401) { out->state = NX_UNAUTHORIZED; return; }
    if (http_status == 409) { out->state = NX_REFUSED; return; }
    if (http_status != 200 || !body || strcmp(state, "ready")) return;
    out->expires_at = u64_field(body, "expiresAt");
    out->pid = u64_field(body, "pid");
    str_field(body, "friendCode", out->friend_code, sizeof out->friend_code, 0);
    str_field(body, "caFile", out->ca_file, sizeof out->ca_file, 1);
    if (str_field(body, "nexToken", out->token, sizeof out->token, 1) && token_ok(out->token) && out->pid &&
        str_field(body, "username", out->username, sizeof out->username, 1) && name_ok(out->username) &&
        str_field(body, "serverHost", out->server_host, sizeof out->server_host, 1) && host_ok(out->server_host) &&
        str_field(body, "natHost", out->nat_host, sizeof out->nat_host, 1) && host_ok(out->nat_host)) {
        out->state = NX_READY;
        return;
    }
    memset(out->token, 0, sizeof out->token);   // not whole: nothing of it is used
    snprintf(out->reason, sizeof out->reason, "incomplete");
}

const char *nextendo_state_name(NxState s) {
    return s == NX_READY ? "ready" : s == NX_REFUSED ? "refused" : s == NX_UNAUTHORIZED ? "unauthorized" : "unavailable";
}

// ---- name resolution rules ----------------------------------------------------------------------------------------
// Four decimal parts of 1 to 3 digits, each at most 255, nothing else.
static int ipv4_literal(const char *s, uint8_t ip[4]) {
    uint8_t v[4];
    for (int i = 0; i < 4; i++) {
        unsigned part = 0;
        int digits = 0;
        for (; *s >= '0' && *s <= '9' && digits < 4; s++, digits++) part = part * 10 + (unsigned)(*s - '0');
        if (digits < 1 || digits > 3 || part > 255) return 0;
        if (*s++ != (i < 3 ? '.' : 0)) return 0;
        v[i] = (uint8_t)part;
    }
    if (ip) memcpy(ip, v, 4);
    return 1;
}

static int has_suffix(const char *s, size_t n, const char *suffix) {
    size_t k = strlen(suffix);
    return n >= k && !memcmp(s + n - k, suffix, k);
}

NxHostClass nextendo_classify(const char *name, uint8_t ip[4]) {
    char low[256];
    size_t n = name ? strlen(name) : 0;
    if (!n || n >= sizeof low) return NX_HOST_OTHER;
    for (size_t i = 0; i < n; i++) low[i] = name[i] >= 'A' && name[i] <= 'Z' ? (char)(name[i] + 32) : name[i];
    if (low[n - 1] == '.') n--;   // a fully qualified name
    low[n] = 0;
    if (ipv4_literal(low, ip)) return NX_HOST_LITERAL;
    static const char *const kDomains[] = {"nintendo.net", "nintendo.com", "nintendo.co.jp"};
    for (size_t d = 0; d < sizeof kDomains / sizeof kDomains[0]; d++) {
        size_t k = strlen(kDomains[d]);
        // the domain itself or a name under it: "notnintendo.net" is somebody else's
        if (!has_suffix(low, n, kDomains[d]) || (n > k && low[n - k - 1] != '.')) continue;
        if (!strncmp(low, "nncs2-", 6) && has_suffix(low, n, ".n.n.srv.nintendo.net")) return NX_HOST_NAT;
        return NX_HOST_SERVER;
    }
    return NX_HOST_OTHER;
}

int nextendo_cache_get(NxHostCache *c, const char *host, uint64_t now_ms, NxResolveFn resolve, uint8_t ip[4]) {
    if (!host || !host[0] || strlen(host) >= sizeof c->host) return 0;
    if (strcmp(c->host, host)) {   // another host: the old address is not its last good one
        memset(c, 0, sizeof *c);
        strcpy(c->host, host);
    }
    int due = !c->have || now_ms - c->good_ms >= NX_DNS_TTL_MS;
    int backoff = c->tried && now_ms - c->tried_ms < (uint64_t)(c->have ? NX_DNS_RETRY_MS : NX_DNS_FIRST_RETRY_MS);
    if (due && !backoff) {
        uint8_t fresh[4];
        if (resolve && resolve(host, fresh)) {
            memcpy(c->ip, fresh, 4);
            c->have = 1;
            c->good_ms = now_ms;
            c->tried = 0;
        } else {
            c->tried = 1;
            c->tried_ms = now_ms;
        }
    }
    if (c->have) memcpy(ip, c->ip, 4);
    return c->have;
}

// ---- the account --------------------------------------------------------------------------------------------------
uint64_t nextendo_account_id(uint64_t pid) {
    // "NX" over the pid: one id per pid (pids are 32 bits today; 48 fit), never 0, positive, and not an id an
    // MKWMP server would derive from a name by chance
    return 0x4e58ull << 48 | (pid & 0xffffffffffffull);
}

static size_t b64url(const uint8_t *in, size_t n, char *out) {
    static const char kB64[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    size_t k = 0;
    for (size_t i = 0; i < n; i += 3) {
        uint32_t v = (uint32_t)in[i] << 16 | (i + 1 < n ? (uint32_t)in[i + 1] << 8 : 0) | (i + 2 < n ? in[i + 2] : 0);
        out[k++] = kB64[v >> 18];
        out[k++] = kB64[(v >> 12) & 63];
        if (i + 1 < n) out[k++] = kB64[(v >> 6) & 63];
        if (i + 2 < n) out[k++] = kB64[v & 63];
    }
    return k;
}
static size_t b64url_len(size_t n) { return n / 3 * 4 + (n % 3 ? n % 3 + 1 : 0); }

// 16 hex characters that are the same for every token of one pid (a console's "sub" and device id do not change)
static void pid_hex(const char *what, uint64_t pid, char out[17]) {
    char key[48];
    int n = snprintf(key, sizeof key, "nextendo-%s/%llu", what, (unsigned long long)pid);
    uint8_t u[16];
    uuid5(kUuidNamespaceUrl, key, (size_t)n, u);
    snprintf(out, 17, "%02x%02x%02x%02x%02x%02x%02x%02x", u[0], u[1], u[2], u[3], u[4], u[5], u[6], u[7]);
}

size_t nextendo_jwt(const NxJwt *in, char *out, size_t cap) {
    static const char kHeader[] = "{\"alg\":\"RS256\",\"typ\":\"JWT\"}";
    if (!in->nex_token || !token_ok(in->nex_token) || strlen(in->nex_token) >= NX_TOKEN_MAX) return 0;
    char sub[17], di[17], jti[37];
    pid_hex("sub", in->pid, sub);
    pid_hex("di", in->pid, di);
    uint8_t j[16];
    memcpy(j, in->jti, 16);
    j[6] = (uint8_t)((j[6] & 0x0f) | 0x40);   // version 4
    j[8] = (uint8_t)((j[8] & 0x3f) | 0x80);   // RFC 4122 variant
    snprintf(jti, sizeof jti, "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x", j[0], j[1], j[2], j[3],
             j[4], j[5], j[6], j[7], j[8], j[9], j[10], j[11], j[12], j[13], j[14], j[15]);
    // Nextendo's reference claims (wiki.nextendo.network/developers, "Game login"). The server reads nnex and tv; the
    // rest is there so the token looks like the console's. No app_id: the reference has it only for a few other titles.
    char claims[NX_TOKEN_MAX + 512];
    int n = snprintf(claims, sizeof claims,
                     "{\"iss\":\"https://e0d67c509fb203858ebcb2fe3f88c2aa.baas.nintendo.com\",\"aud\":\"ed9e2f05d286f7b8\","
                     "\"typ\":\"id_token\",\"sub\":\"%s\",\"jti\":\"%s\",\"di\":\"%s\",\"bs:did\":\"%s\","
                     "\"sn\":\"XAW10000000000\",\"hm\":true,\"tv\":\"1.8.0\",\"iat\":%lld,\"exp\":%lld,\"nnex\":\"%s\"}",
                     sub, jti, di, di, (long long)in->iat, (long long)in->iat + 3 * 60 * 60, in->nex_token);
    size_t total = 0;
    if (n > 0 && (size_t)n < sizeof claims) {
        size_t need = b64url_len(sizeof kHeader - 1) + 1 + b64url_len((size_t)n) + 1 + b64url_len(sizeof in->sig);
        if (need < cap) {
            total = b64url((const uint8_t *)kHeader, sizeof kHeader - 1, out);
            out[total++] = '.';
            total += b64url((const uint8_t *)claims, (size_t)n, out + total);
            out[total++] = '.';
            total += b64url(in->sig, sizeof in->sig, out + total);
            out[total] = 0;
        }
    }
    memset(claims, 0, sizeof claims);   // the nx2 was in it
    return total;
}

// ---- the running session ------------------------------------------------------------------------------------------
static PlatLock g_lock = PLAT_LOCK_INIT;
static NxSession g_session;   // state NX_READY when g_have
static int g_have;

static uint64_t unix_ms(void) {
    int64_t t[2];
    plat_realtime(t);
    return (uint64_t)t[0] * 1000u + (uint64_t)t[1] / 1000000u;
}

NxState nextendo_fetch(void) {
    static NxSession s;                    // too big for a guest thread's stack with the reply beside it
    static char buf[NX_TOKEN_MAX + 2048];
    static PlatLock fetch_lock = PLAT_LOCK_INIT;
    plat_lock(&fetch_lock);
    const char *body;
    int status = launcher_request("GET", "/local/v1/nextendo/session", buf, sizeof buf, &body);
    nextendo_session_parse(status > 0 ? status : 0, status > 0 ? body : NULL, &s);
    memset(buf, 0, sizeof buf);
    uint64_t now = unix_ms();
    if (s.state == NX_READY && s.expires_at && s.expires_at <= now) {
        s.state = NX_UNAVAILABLE;
        snprintf(s.reason, sizeof s.reason, "expired");
    }
    NxState state = s.state;
    plat_lock(&g_lock);
    int kept = 0;
    if (state == NX_READY) {
        g_session = s;
        g_have = 1;
    } else if (state == NX_UNAVAILABLE && g_have && (!g_session.expires_at || g_session.expires_at > now)) {
        kept = 1;   // a hiccup of the launcher or of Nextendo: the token in hand is still good
    } else {
        memset(&g_session, 0, sizeof g_session);
        g_have = 0;
    }
    plat_unlock(&g_lock);
    if (state == NX_READY) {
        long long left = s.expires_at ? (long long)((s.expires_at - now) / 60000) : -1;
        mkwmp_event("nextendo: session ready (token %u bytes, %lld min left, server %s, nat %s)",
                    (unsigned)strlen(s.token), left, s.server_host, s.nat_host);
    } else if (status < 0) {
        mkwmp_event("nextendo: no session: mkwmp.toml names no launcher");
    } else {
        mkwmp_event("nextendo: no session: %s (HTTP %d%s%s%s%s)%s", nextendo_state_name(state), status,
                    s.reason[0] ? ", " : "", s.reason, s.message[0] ? ": " : "", s.message,
                    kept ? "; the one in hand stays" : "");
    }
    memset(&s, 0, sizeof s);
    plat_unlock(&fetch_lock);
    return state;
}

int nextendo_have(void) {
    plat_lock_shared(&g_lock);
    int have = g_have;
    plat_unlock_shared(&g_lock);
    return have;
}

uint64_t nextendo_pid(void) {
    plat_lock_shared(&g_lock);
    uint64_t pid = g_have ? g_session.pid : 0;
    plat_unlock_shared(&g_lock);
    return pid;
}

int nextendo_username(char *out, size_t cap) {
    plat_lock_shared(&g_lock);
    int have = g_have;
    if (have) snprintf(out, cap, "%s", g_session.username);
    plat_unlock_shared(&g_lock);
    return have;
}

int nextendo_hosts(char *server, char *nat, char *ca_file, size_t cap) {
    plat_lock_shared(&g_lock);
    int have = g_have;
    if (have && server) snprintf(server, cap, "%s", g_session.server_host);
    if (have && nat) snprintf(nat, cap, "%s", g_session.nat_host);
    if (have && ca_file) snprintf(ca_file, cap, "%s", g_session.ca_file);
    plat_unlock_shared(&g_lock);
    return have;
}

// Bytes nobody can predict cheaply; they identify a token (jti) and fill a signature nobody checks. Not a key.
static void scramble(uint8_t *out, size_t n) {
    static volatile uint64_t counter;
    for (size_t at = 0; at < n; at += 20) {
        struct { int64_t real[2], mono[2]; uint64_t count, ms; unsigned long tid, process; const void *stack; } seed;
        memset(&seed, 0, sizeof seed);
        plat_realtime(seed.real);
        plat_monotonic(seed.mono);
        seed.count = plat_inc(&counter);
        seed.ms = plat_ms();
        seed.tid = plat_thread_id();
        seed.process = plat_process_id();
        seed.stack = &seed;
        uint8_t d[20];
        sha1(&seed, sizeof seed, d);
        memcpy(out + at, d, n - at < 20 ? n - at : 20);
    }
}

size_t nextendo_token(char *out, size_t cap) {
    static char token[NX_TOKEN_MAX];
    static PlatLock token_lock = PLAT_LOCK_INIT;
    NxJwt j = {.nex_token = token};
    plat_lock(&token_lock);
    plat_lock_shared(&g_lock);
    int have = g_have;
    if (have) {
        memcpy(token, g_session.token, sizeof token);
        j.pid = g_session.pid;
    }
    plat_unlock_shared(&g_lock);
    size_t len = 0;
    if (have) {
        j.iat = (int64_t)(unix_ms() / 1000);
        scramble(j.jti, sizeof j.jti);
        scramble(j.sig, sizeof j.sig);
        len = nextendo_jwt(&j, out, cap);
    }
    memset(token, 0, sizeof token);
    plat_unlock(&token_lock);
    return len;
}

static int dns_ipv4(const char *host, uint8_t ip[4]) {
    plat_net_init();
    struct addrinfo hints = {0}, *res = NULL;
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_STREAM;
    if (getaddrinfo(host, NULL, &hints, &res) || !res) return 0;
    memcpy(ip, &((struct sockaddr_in *)res->ai_addr)->sin_addr, 4);
    freeaddrinfo(res);
    return 1;
}

int nextendo_host(NxHostClass which, uint8_t ip[4]) {
    static NxHostCache cache[2];
    static PlatLock dns_lock = PLAT_LOCK_INIT;
    char host[128];
    int nat = which == NX_HOST_NAT;
    if (!nextendo_hosts(nat ? NULL : host, nat ? host : NULL, NULL, sizeof host)) return 0;
    if (nextendo_classify(host, ip) == NX_HOST_LITERAL) return 1;   // the launcher gave an address
    plat_lock(&dns_lock);
    NxHostCache *c = &cache[nat];
    uint64_t now = plat_ms();
    int had = c->have && !strcmp(c->host, host);
    uint64_t was = had ? c->good_ms : 0, tried_was = had && c->tried ? c->tried_ms : 0;
    int ok = nextendo_cache_get(c, host, now, dns_ipv4, ip);
    int fresh = ok && (!had || c->good_ms != was), stale = c->tried && c->tried_ms == now && tried_was != now;
    plat_unlock(&dns_lock);
    if (fresh) mkwmp_log("nextendo: %s is %u.%u.%u.%u", host, ip[0], ip[1], ip[2], ip[3]);
    else if (stale) mkwmp_log("nextendo: cannot resolve %s%s", host, ok ? ": keeping its last address" : "");
    return ok;
}

int nextendo_resolve(const char *name, uint8_t ip[4]) {
    switch (nextendo_classify(name, ip)) {
    case NX_HOST_LITERAL: return 1;
    case NX_HOST_NAT: return nextendo_host(NX_HOST_NAT, ip);
    case NX_HOST_SERVER: return nextendo_host(NX_HOST_SERVER, ip);
    default: return name && name[0] && dns_ipv4(name, ip);
    }
}
