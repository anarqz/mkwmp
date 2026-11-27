#include "plat_tls.h"   // plat_net.h first (nnssl.h)
#include "nnssl.h"

#include "breadcrumbs.h"
#include "ghost.h"
#include "lobby_ca.h"
#include "log.h"
#include "rtapi.h"

#include <stdarg.h>
#include <stdio.h>
#include <string.h>

// nn::Result values of the TLS service (module 123), as the game's HTTP client tells them apart: "would block" makes
// it call again, "certificate refused" becomes its peer-verification error, anything else a connect / send / receive
// error.
enum {
    RES_OK = 0,
    RES_BAD_ARGUMENT = 0xe27b,
    RES_WOULD_BLOCK = 0x1987b,
    RES_CERT_REFUSED = 0x19e7b,
    RES_FAILED = 0x1a27b,
    RES_CERT_UNKNOWN_CA = 0x25a7b,   // GetVerifyCertError's detail
};
enum { IO_BLOCKING = 1, IO_NONBLOCKING = 2 };
enum { ALPN_NONE = 0, ALPN_NEGOTIATED = 1 };
enum { NCONN = 16, BLOCKING_LIMIT_MS = 30000, HANDSHAKE_WAIT_MS = 1500 };

typedef struct {
    int used;
    uint64_t id;                 // what the game's connection object holds (never 0)
    int fd, io_mode, said_start, said_ready;
    unsigned verify;
    char host[256];
    unsigned char alpn[64];
    size_t alpn_len;
    PlatTls *tls;
    uint32_t last_verify_error;
    uint64_t started_ms;
    GhostTrace trace;
} Conn;

static Conn g_conn[NCONN];
static PlatLock g_lock = PLAT_LOCK_INIT;
static uint32_t g_serial;

static void mlog(const char *fmt, ...) {
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    int n = vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    mkwmp_log("%s", buf);
    crumb_text(buf);
    if (g_rt.log) g_rt.log("mkwmp: %s", buf);
}

// The connection a game object names: its first 8 bytes are the id nnsslConnectionCreate wrote there.
static Conn *conn_of(Cpu *c) {
    const uint64_t *obj = arg_p(c, 0);
    if (!obj) return NULL;
    uint64_t id = *obj;
    unsigned slot = (unsigned)(id & 0xff);
    if ((id >> 32) != 0x6d6b7373u || !slot || slot > NCONN) return NULL;
    Conn *k = &g_conn[slot - 1];
    return k->used && k->id == id ? k : NULL;
}

static void end_connection(Conn *k) {
    if (k->tls) {
        if (k->said_ready) {
            char line[GHOST_LINE_MAX];
            ghost_trace_end(&k->trace, line, sizeof line);
            mlog("%s", line);
        }
        plat_tls_free(k->tls);
    }
    plat_lock(&g_lock);
    memset(k, 0, sizeof *k);
    plat_unlock(&g_lock);
}

// ---- set-up calls ------------------------------------------------------------------------------------------------
static void ov_conn_create(Cpu *c) {
    uint64_t *obj = arg_p(c, 0);
    if (!obj || !arg_x(c, 1)) { ret_x(c, RES_BAD_ARGUMENT); return; }
    Conn *k = NULL;
    plat_lock(&g_lock);
    for (int i = 0; i < NCONN && !k; i++) {
        if (g_conn[i].used) continue;
        k = &g_conn[i];
        memset(k, 0, sizeof *k);
        k->used = 1;
        k->id = (0x6d6b7373ull << 32) | ((uint64_t)(++g_serial & 0xffffff) << 8) | (unsigned)(i + 1);
        k->fd = -1;
        k->io_mode = IO_BLOCKING;
    }
    plat_unlock(&g_lock);
    if (!k) {
        mlog("ghost: too many TLS connections at once (%d): this one is refused", NCONN);
        ret_x(c, RES_FAILED);
        return;
    }
    *obj = k->id;
    ret_x(c, RES_OK);
}

static void ov_conn_destroy(Cpu *c) {
    uint64_t *obj = arg_p(c, 0);
    Conn *k = conn_of(c);
    if (k) end_connection(k);
    if (obj) *obj = 0;
    ret_x(c, obj ? RES_OK : RES_BAD_ARGUMENT);
}

static void ov_conn_id(Cpu *c) {
    uint64_t *out = arg_p(c, 1);
    Conn *k = conn_of(c);
    if (!arg_x(c, 0) || !out) { ret_x(c, RES_BAD_ARGUMENT); return; }
    *out = k ? k->id : 0;
    ret_x(c, RES_OK);
}

static void ov_conn_set_fd(Cpu *c) {
    Conn *k = conn_of(c);
    if (k) k->fd = (int)arg_x(c, 1);
    ret_x(c, k ? RES_OK : RES_BAD_ARGUMENT);
}

static void ov_conn_set_host(Cpu *c) {
    Conn *k = conn_of(c);
    const char *name = arg_p(c, 1);
    size_t len = (uint32_t)arg_x(c, 2);
    if (!k || !name || !len || len >= sizeof k->host) { ret_x(c, RES_BAD_ARGUMENT); return; }
    memcpy(k->host, name, len);
    k->host[len] = 0;   // (a length that counts the terminator is fine: the name ends at its first NUL)
    ret_x(c, RES_OK);
}

static void ov_conn_set_verify(Cpu *c) {
    Conn *k = conn_of(c);
    if (k) k->verify = (unsigned)arg_x(c, 1);
    ret_x(c, k ? RES_OK : RES_BAD_ARGUMENT);
}

static void ov_conn_set_io(Cpu *c) {
    Conn *k = conn_of(c);
    if (k) k->io_mode = (int)arg_x(c, 1) == IO_NONBLOCKING ? IO_NONBLOCKING : IO_BLOCKING;
    ret_x(c, k ? RES_OK : RES_BAD_ARGUMENT);
}

static void ov_conn_set_alpn(Cpu *c) {
    Conn *k = conn_of(c);
    const unsigned char *list = arg_p(c, 1);
    size_t len = (uint32_t)arg_x(c, 2);
    if (!k || !list || !len || len > sizeof k->alpn) { ret_x(c, RES_BAD_ARGUMENT); return; }
    memcpy(k->alpn, list, len);
    k->alpn_len = len;
    ret_x(c, RES_OK);
}

// SetOption, SetSessionCacheMode, SetServerCertBuffer: nothing to keep (the socket stays the game's to close, there
// is no session cache, the certificate is checked here and not handed to the game)
static void ov_conn_accept(Cpu *c) { ret_x(c, conn_of(c) ? RES_OK : RES_BAD_ARGUMENT); }

static void ov_conn_get_alpn(Cpu *c) {
    Conn *k = conn_of(c);
    uint32_t *state = arg_p(c, 1), *out_len = arg_p(c, 3);
    unsigned char *out = arg_p(c, 2);
    size_t cap = (uint32_t)arg_x(c, 4);
    if (!k || !state || !out || !out_len) { ret_x(c, RES_BAD_ARGUMENT); return; }
    const char *chosen = k->tls ? plat_tls_alpn(k->tls) : "";
    size_t n = strlen(chosen);
    if (!n || n > cap) {
        *state = ALPN_NONE, *out_len = 0;
    } else {
        memcpy(out, chosen, n);
        *state = ALPN_NEGOTIATED, *out_len = (uint32_t)n;
    }
    ret_x(c, RES_OK);
}

static void ov_conn_cert_detail(Cpu *c) { ret_x(c, RES_FAILED); }   // the certificate is not handed to the game

static void ov_conn_verify_error(Cpu *c) {
    Conn *k = conn_of(c);
    uint32_t *out = arg_p(c, 1);
    if (!k || !out) { ret_x(c, RES_BAD_ARGUMENT); return; }
    *out = k->last_verify_error;
    ret_x(c, RES_OK);
}

// ---- waiting, for a connection the game left in blocking mode ----------------------------------------------------
static int wait_socket(Conn *k, int want, uint64_t until) {
    PlatSock s = npln_guest_socket(k->fd);
    uint64_t now = plat_ms();
    if (s == PLAT_NO_SOCK || now >= until) return 0;
    int away = 0;
    if (g_rt.core_away && g_rt.core_back) away = g_rt.core_away();
    PlatPollFd p = {s, (short)(want == PLAT_TLS_WANT_WRITE ? PLAT_POLLOUT : PLAT_POLLIN), 0};
    uint64_t left = until - now;
    int r = plat_poll(&p, 1, left > 250 ? 250 : (int)left);
    if (g_rt.core_back && away) g_rt.core_back(&away);
    return r >= 0;
}

static void alpn_text(const Conn *k, char *out, size_t cap) {
    size_t n = 0;
    out[0] = 0;
    for (size_t i = 0; i < k->alpn_len;) {
        size_t len = k->alpn[i++];
        if (!len || i + len > k->alpn_len || n + len + 2 >= cap) break;
        if (n) out[n++] = ' ';
        for (size_t j = 0; j < len; j++) {
            unsigned char ch = k->alpn[i + j];
            out[n++] = ch >= 0x21 && ch < 0x7f ? (char)ch : '?';
        }
        out[n] = 0;
        i += len;
    }
}

// ---- handshake ---------------------------------------------------------------------------------------------------
static void ov_conn_handshake(Cpu *c) {
    Conn *k = conn_of(c);
    uint32_t *cert_size = arg_p(c, 1), *cert_count = arg_p(c, 2);
    if (!k) { ret_x(c, RES_BAD_ARGUMENT); return; }
    if (cert_size) *cert_size = 0;
    if (cert_count) *cert_count = 0;
    if (!k->tls) {
        PlatSock s = npln_guest_socket(k->fd);
        size_t ca_len = 0;
        const unsigned char *ca = lobby_ca(&ca_len);
        char why[160] = "";
        if (s == PLAT_NO_SOCK || !k->host[0]) snprintf(why, sizeof why, "no socket or host name was set");
        else if (!ca || !ca_len) snprintf(why, sizeof why, "the session has no CA certificate");
        else k->tls = plat_tls_new(s, k->host, ca, ca_len, k->alpn_len ? k->alpn : NULL, k->alpn_len, why, sizeof why);
        if (!k->tls) {
            mlog("ghost: TLS with %s cannot start: %s", k->host[0] ? k->host : "(no host)", why);
            ret_x(c, RES_FAILED);
            return;
        }
        ghost_trace_start(&k->trace, k->host);
        k->started_ms = plat_ms();
    }
    if (!k->said_start) {
        char offered[80];
        alpn_text(k, offered, sizeof offered);
        k->said_start = 1;
        mlog("ghost: TLS handshake with %s started (offering %s)", k->host, offered[0] ? offered : "no ALPN");
    }
    // The game's HTTP client does not wait on the socket between two tries of a handshake (its TLS backend names it
    // none): it sleeps, up to a second each time. So a "non-blocking" handshake waits here for the server's next
    // message, for a bounded time (the guest core is given up meanwhile), and is usually done in one call.
    uint64_t until = plat_ms() + (k->io_mode == IO_NONBLOCKING ? HANDSHAKE_WAIT_MS : BLOCKING_LIMIT_MS);
    int r;
    while ((r = plat_tls_handshake(k->tls)) == PLAT_TLS_WANT_READ || r == PLAT_TLS_WANT_WRITE) {
        if (wait_socket(k, r, until)) continue;
        if (k->io_mode == IO_NONBLOCKING) { ret_x(c, RES_WOULD_BLOCK); return; }
        break;
    }
    if (r == PLAT_TLS_OK) {
        if (!k->said_ready) {
            const char *alpn = plat_tls_alpn(k->tls);
            k->said_ready = 1;
            mlog("ghost: TLS with %s ready after %llu ms (%s, ALPN %s, certificate checked against the session CA)",
                 k->host, (unsigned long long)(plat_ms() - k->started_ms), plat_tls_version(k->tls),
                 alpn[0] ? alpn : "none");
        }
        ret_x(c, RES_OK);
        return;
    }
    if (r == PLAT_TLS_UNTRUSTED) {
        k->last_verify_error = RES_CERT_UNKNOWN_CA;
        mlog("ghost: TLS with %s refused: %s", k->host, plat_tls_error(k->tls));
        ret_x(c, RES_CERT_REFUSED);
        return;
    }
    mlog("ghost: TLS with %s failed: %s", k->host,
         r == PLAT_TLS_FAIL ? plat_tls_error(k->tls) : "no answer in time");
    ret_x(c, RES_FAILED);
}

// ---- data --------------------------------------------------------------------------------------------------------
static void trace(Conn *k, int sent, const void *data, size_t n) {
    char lines[GHOST_LINES][GHOST_LINE_MAX];
    int count = ghost_trace_feed(&k->trace, sent, data, n, lines, GHOST_LINES);
    for (int i = 0; i < count; i++) mlog("%s", lines[i]);
}

static void transfer(Cpu *c, int writing, int peek) {
    Conn *k = conn_of(c);
    void *buf = arg_p(c, 1);
    int32_t *done = arg_p(c, 2);
    size_t size = (uint32_t)arg_x(c, 3);
    if (!k || !buf || !done) { ret_x(c, RES_BAD_ARGUMENT); return; }
    *done = 0;
    if (!k->tls || !k->said_ready) { ret_x(c, RES_FAILED); return; }
    if (!size) { ret_x(c, RES_OK); return; }
    uint64_t until = plat_ms() + BLOCKING_LIMIT_MS;
    long r;
    for (;;) {
        r = writing ? plat_tls_write(k->tls, buf, size) : plat_tls_read(k->tls, buf, size, peek);
        if (r != PLAT_TLS_WANT_READ && r != PLAT_TLS_WANT_WRITE) break;
        if (k->io_mode == IO_NONBLOCKING) { ret_x(c, RES_WOULD_BLOCK); return; }
        if (!wait_socket(k, (int)r, until)) { r = PLAT_TLS_FAIL; break; }
    }
    if (r > 0) {
        *done = (int32_t)r;
        if (!peek) trace(k, writing, buf, (size_t)r);
        ret_x(c, RES_OK);
        return;
    }
    if (r == PLAT_TLS_CLOSED && !writing) { ret_x(c, RES_OK); return; }   // 0 bytes: the end, as recv's 0
    static volatile int32_t said;
    if (plat_inc(&said) <= 20)
        mlog("ghost: %s %s failed: %s", writing ? "sending to" : "receiving from", k->host,
             plat_tls_error(k->tls)[0] ? plat_tls_error(k->tls) : "no progress in time");
    ret_x(c, RES_FAILED);
}

static void ov_conn_read(Cpu *c) { transfer(c, 0, 0); }
static void ov_conn_peek(Cpu *c) { transfer(c, 0, 1); }
static void ov_conn_write(Cpu *c) { transfer(c, 1, 0); }

static void ov_conn_pending(Cpu *c) {
    Conn *k = conn_of(c);
    int32_t *out = arg_p(c, 1);
    if (!k || !out) { ret_x(c, RES_BAD_ARGUMENT); return; }
    size_t n = k->tls && k->said_ready ? plat_tls_pending(k->tls) : 0;
    *out = n > 0x7fffffff ? 0x7fffffff : (int32_t)n;
    ret_x(c, RES_OK);
}

// ---- context and library -----------------------------------------------------------------------------------------
// The context was "created" by npln.c's nn::ssl::Context::Create (an id of 1 in its first 8 bytes): nothing to free.
static void ov_ctx_destroy(Cpu *c) {
    uint64_t *ctx = arg_p(c, 0);
    if (ctx) *ctx = 0;
    ret_x(c, ctx ? RES_OK : RES_BAD_ARGUMENT);
}
static void ov_finalize(Cpu *c) { ret_x(c, RES_OK); }

// nn::nsd::ResolveEx(Fqdn *out, const Fqdn &in), both 256 bytes: the HTTP client passes every link's host through it
// and rebuilds the link with the answer. The console gives the name back with its environment put in for a "%"
// ("lp1", as npln.c's GetEnvironmentIdentifier says); so does this. (The runtime's own handler is for a console
// without a network.)
static void ov_nsd_resolve(Cpu *c) {
    char *out = arg_p(c, 0);
    const char *in = arg_p(c, 1);
    if (!out || !in) { ret_x(c, RES_BAD_ARGUMENT); return; }
    char name[256];
    size_t n = 0;
    for (size_t i = 0; i < 255 && in[i] && n < 255; i++) {
        if (in[i] != '%') { name[n++] = in[i]; continue; }
        for (const char *env = "lp1"; *env && n < 255; env++) name[n++] = *env;
    }
    name[n] = 0;
    memcpy(out, name, n + 1);   // (out may be in)
    ret_x(c, 0);
}

static const struct { const char *name; GuestFn fn; } kOverrides[] = {
    {"nnsslConnectionCreate", ov_conn_create},
    {"nnsslConnectionDestroy", ov_conn_destroy},
    {"nnsslConnectionGetConnectionId", ov_conn_id},
    {"nnsslConnectionSetSocketDescriptor", ov_conn_set_fd},
    {"nnsslConnectionSetHostName", ov_conn_set_host},
    {"nnsslConnectionSetVerifyOption", ov_conn_set_verify},
    {"nnsslConnectionSetIoMode", ov_conn_set_io},
    {"nnsslConnectionSetNextAlpnProto", ov_conn_set_alpn},
    {"nnsslConnectionGetNextAlpnProto", ov_conn_get_alpn},
    {"nnsslConnectionSetOption", ov_conn_accept},
    {"nnsslConnectionSetSessionCacheMode", ov_conn_accept},
    {"nnsslConnectionSetServerCertBuffer", ov_conn_accept},
    {"nnsslConnectionDoHandshakeWithCertBuffer", ov_conn_handshake},
    {"nnsslConnectionGetServerCertDetail", ov_conn_cert_detail},
    {"nnsslConnectionGetVerifyCertError", ov_conn_verify_error},
    {"nnsslConnectionRead", ov_conn_read},
    {"nnsslConnectionPeek", ov_conn_peek},
    {"nnsslConnectionWrite", ov_conn_write},
    {"nnsslConnectionPending", ov_conn_pending},
    {"nnsslContextDestroy", ov_ctx_destroy},
    {"nnsslFinalize", ov_finalize},
    {"_ZN2nn3nsd9ResolveExEPNS0_4FqdnERKS1_", ov_nsd_resolve},
};

int nnssl_register(void) {
    char off[8];
    if (plat_getenv("MKWMP_NO_GHOST", off, sizeof off) && !strcmp(off, "1")) {
        mlog("ghost transfers: off (MKWMP_NO_GHOST=1): ghost uploads and downloads will fail in the game");
        return 0;
    }
    if (!g_rt.override || !g_rt.guest_modules || g_rt.guest_modules() != 0) {
        mlog("ghost transfers: off (the game is already loaded: too late to register)");
        return 0;
    }
    int n = (int)(sizeof kOverrides / sizeof kOverrides[0]);
    for (int i = 0; i < n; i++) g_rt.override(kOverrides[i].name, kOverrides[i].fn);
    n += npln_curl_socket_overrides();
    mlog("ghost transfers: on (%d overrides: the TLS service and socket calls of the game's HTTP client)", n);
    return n;
}
