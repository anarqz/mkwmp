// plat_tls.h on Linux: OpenSSL, loaded at run time (libssl.so.3 with libcrypto.so.3, else the 1.1 pair), so the plugin
// links nothing new and still loads where neither is installed (plat_tls_new then fails and says so).
//
// OpenSSL never touches the socket: it reads and writes two memory BIOs, and the bytes are moved here with
// MSG_NOSIGNAL, so a connection the server dropped cannot raise SIGPIPE in the game. The trust store of the context
// holds the caller's CA and nothing else (no default paths are loaded).
// The functions and the few constants used are OpenSSL's stable ABI (1.1.0 and 3.x alike).
#include "plat_tls.h"

#include <dlfcn.h>
#include <errno.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum {
    SSL_ERROR_SSL_ = 1, SSL_ERROR_WANT_READ_ = 2, SSL_ERROR_WANT_WRITE_ = 3, SSL_ERROR_ZERO_RETURN_ = 6,
    SSL_CTRL_MODE_ = 33, SSL_CTRL_SET_TLSEXT_HOSTNAME_ = 55,
    SSL_MODE_PARTIAL_AND_MOVING = 1 | 2,   // ENABLE_PARTIAL_WRITE | ACCEPT_MOVING_WRITE_BUFFER
    SSL_VERIFY_PEER_ = 1,
    BIO_CTRL_PENDING_ = 10,
    RECORD_MAX = 16384,
    OUT_CAP = 20 * 1024,
    FLUSH_WAIT_MS = 200,
};

static struct {
    int loaded;
    const void *(*TLS_client_method)(void);
    void *(*SSL_CTX_new)(const void *);
    void (*SSL_CTX_free)(void *);
    void (*SSL_CTX_set_verify)(void *, int, void *);
    void *(*SSL_CTX_get_cert_store)(const void *);
    void *(*SSL_new)(void *);
    void (*SSL_free)(void *);
    void (*SSL_set_bio)(void *, void *, void *);
    void (*SSL_set_connect_state)(void *);
    long (*SSL_ctrl)(void *, int, long, void *);
    int (*SSL_set1_host)(void *, const char *);
    void *(*SSL_get0_param)(void *);
    int (*SSL_set_alpn_protos)(void *, const unsigned char *, unsigned);
    void (*SSL_get0_alpn_selected)(const void *, const unsigned char **, unsigned *);
    int (*SSL_do_handshake)(void *);
    int (*SSL_read)(void *, void *, int);
    int (*SSL_peek)(void *, void *, int);
    int (*SSL_write)(void *, const void *, int);
    int (*SSL_pending)(const void *);
    int (*SSL_get_error)(const void *, int);
    int (*SSL_shutdown)(void *);
    long (*SSL_get_verify_result)(const void *);
    const char *(*SSL_get_version)(const void *);
    void *(*BIO_new)(const void *);
    const void *(*BIO_s_mem)(void);
    int (*BIO_read)(void *, void *, int);
    int (*BIO_write)(void *, const void *, int);
    long (*BIO_ctrl)(void *, int, long, void *);
    void *(*d2i_X509)(void **, const unsigned char **, long);
    void (*X509_free)(void *);
    int (*X509_STORE_add_cert)(void *, void *);
    int (*X509_VERIFY_PARAM_set1_ip_asc)(void *, const char *);
    const char *(*X509_verify_cert_error_string)(long);
    void (*ERR_clear_error)(void);
} O;

static pthread_once_t once = PTHREAD_ONCE_INIT;

static void load(void) {
    static const char *const pairs[][2] = {{"libssl.so.3", "libcrypto.so.3"}, {"libssl.so.1.1", "libcrypto.so.1.1"}};
    void *ssl = NULL, *crypto = NULL;
    for (size_t i = 0; i < sizeof pairs / sizeof *pairs && !ssl; i++) {
        crypto = dlopen(pairs[i][1], RTLD_NOW | RTLD_LOCAL);
        if (!crypto) continue;
        ssl = dlopen(pairs[i][0], RTLD_NOW | RTLD_LOCAL);
        if (!ssl) dlclose(crypto), crypto = NULL;
    }
    if (!ssl) return;
    int missing = 0;
#define SYM(lib, name) missing |= !(*(void **)&O.name = dlsym(lib, #name))
    SYM(ssl, TLS_client_method); SYM(ssl, SSL_CTX_new); SYM(ssl, SSL_CTX_free); SYM(ssl, SSL_CTX_set_verify);
    SYM(ssl, SSL_CTX_get_cert_store); SYM(ssl, SSL_new); SYM(ssl, SSL_free); SYM(ssl, SSL_set_bio);
    SYM(ssl, SSL_set_connect_state); SYM(ssl, SSL_ctrl); SYM(ssl, SSL_set1_host); SYM(ssl, SSL_get0_param);
    SYM(ssl, SSL_set_alpn_protos); SYM(ssl, SSL_get0_alpn_selected); SYM(ssl, SSL_do_handshake); SYM(ssl, SSL_read);
    SYM(ssl, SSL_peek); SYM(ssl, SSL_write); SYM(ssl, SSL_pending); SYM(ssl, SSL_get_error); SYM(ssl, SSL_shutdown);
    SYM(ssl, SSL_get_verify_result); SYM(ssl, SSL_get_version);
    SYM(crypto, BIO_new); SYM(crypto, BIO_s_mem); SYM(crypto, BIO_read); SYM(crypto, BIO_write); SYM(crypto, BIO_ctrl);
    SYM(crypto, d2i_X509); SYM(crypto, X509_free); SYM(crypto, X509_STORE_add_cert);
    SYM(crypto, X509_VERIFY_PARAM_set1_ip_asc); SYM(crypto, X509_verify_cert_error_string); SYM(crypto, ERR_clear_error);
#undef SYM
    O.loaded = !missing;
}

struct PlatTls {
    PlatSock s;
    void *ctx, *ssl, *rbio, *wbio;   // the BIOs belong to ssl
    int done, failed, closed;
    unsigned char out[OUT_CAP];      // taken from wbio, not yet sent
    size_t out_off, out_len;
    char alpn[40], version[16], error[200];
};

static int fail(PlatTls *t, int code, const char *fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(t->error, sizeof t->error, fmt, ap);
    va_end(ap);
    t->failed = code;
    return code;
}

// What OpenSSL wants sent, to the socket: 1 all sent, 0 the socket is full, -1 failed
static int flush(PlatTls *t) {
    for (;;) {
        while (t->out_off < t->out_len) {
            ssize_t r = send(t->s, t->out + t->out_off, t->out_len - t->out_off, MSG_NOSIGNAL);
            if (r > 0) t->out_off += (size_t)r;
            else if (errno == EAGAIN || errno == EWOULDBLOCK) return 0;
            else if (errno != EINTR) return -1;
        }
        t->out_off = t->out_len = 0;
        if (O.BIO_ctrl(t->wbio, BIO_CTRL_PENDING_, 0, NULL) <= 0) return 1;
        int n = O.BIO_read(t->wbio, t->out, sizeof t->out);
        if (n <= 0) return 1;
        t->out_len = (size_t)n;
    }
}

// From the socket to OpenSSL: 1 got some, 0 the peer closed, PLAT_TLS_WANT_READ, -1 failed
static int fill(PlatTls *t) {
    unsigned char buf[RECORD_MAX];
    for (;;) {
        ssize_t r = recv(t->s, buf, sizeof buf, 0);
        if (r > 0) return O.BIO_write(t->rbio, buf, (int)r) == (int)r ? 1 : -1;
        if (r == 0) return 0;
        if (errno == EAGAIN || errno == EWOULDBLOCK) return PLAT_TLS_WANT_READ;
        if (errno != EINTR) return -1;
    }
}

PlatTls *plat_tls_new(PlatSock s, const char *host, const unsigned char *ca, size_t ca_len, const unsigned char *alpn,
                      size_t alpn_len, char *error, size_t error_cap) {
    const char *why = "bad arguments or out of memory";
    if (error && error_cap) error[0] = 0;
    pthread_once(&once, load);
    PlatTls *t = NULL;
    void *cert = NULL;
    if (!O.loaded) { why = "OpenSSL not found (libssl.so.3 or libssl.so.1.1)"; goto bad; }
    if (!host || !*host || !ca || !ca_len) goto bad;
    t = calloc(1, sizeof *t);
    if (!t) goto bad;
    t->s = s;
    O.ERR_clear_error();
    t->ctx = O.SSL_CTX_new(O.TLS_client_method());
    if (!t->ctx) { why = "OpenSSL gave no context"; goto bad; }
    const unsigned char *p = ca;
    cert = O.d2i_X509(NULL, &p, (long)ca_len);
    if (!cert || O.X509_STORE_add_cert(O.SSL_CTX_get_cert_store(t->ctx), cert) != 1) {
        why = "the trusted CA is not a certificate";
        goto bad;
    }
    O.SSL_CTX_set_verify(t->ctx, SSL_VERIFY_PEER_, NULL);
    t->ssl = O.SSL_new(t->ctx);
    void *rbio = O.BIO_new(O.BIO_s_mem()), *wbio = O.BIO_new(O.BIO_s_mem());
    if (!t->ssl || !rbio || !wbio) { why = "OpenSSL gave no connection"; goto bad; }
    O.SSL_set_bio(t->ssl, rbio, wbio);   // ssl owns them from here
    t->rbio = rbio, t->wbio = wbio;
    O.SSL_set_connect_state(t->ssl);
    O.SSL_ctrl(t->ssl, SSL_CTRL_MODE_, SSL_MODE_PARTIAL_AND_MOVING, NULL);
    struct in_addr literal;
    if (inet_pton(AF_INET, host, &literal) == 1) {
        if (O.X509_VERIFY_PARAM_set1_ip_asc(O.SSL_get0_param(t->ssl), host) != 1) goto bad;
    } else {
        if (O.SSL_set1_host(t->ssl, host) != 1) goto bad;
        O.SSL_ctrl(t->ssl, SSL_CTRL_SET_TLSEXT_HOSTNAME_, 0, (void *)(uintptr_t)host);
    }
    if (alpn && alpn_len && O.SSL_set_alpn_protos(t->ssl, alpn, (unsigned)alpn_len) != 0) goto bad;
    O.X509_free(cert);
    return t;
bad:
    if (error) snprintf(error, error_cap, "%s", why);
    if (cert) O.X509_free(cert);
    if (t) {
        if (t->ssl) O.SSL_free(t->ssl);
        if (t->ctx) O.SSL_CTX_free(t->ctx);
        free(t);
    }
    return NULL;
}

int plat_tls_handshake(PlatTls *t) {
    if (t->failed) return t->failed;
    if (t->done) return PLAT_TLS_OK;
    for (;;) {
        O.ERR_clear_error();
        int r = O.SSL_do_handshake(t->ssl);
        int err = r == 1 ? 0 : O.SSL_get_error(t->ssl, r);
        int f = flush(t);
        if (f < 0) return fail(t, PLAT_TLS_FAIL, "sending the handshake failed (errno %d)", errno);
        if (r == 1) {
            const unsigned char *proto = NULL;
            unsigned n = 0;
            O.SSL_get0_alpn_selected(t->ssl, &proto, &n);
            if (proto && n < sizeof t->alpn) memcpy(t->alpn, proto, n), t->alpn[n] = 0;
            const char *v = O.SSL_get_version(t->ssl);
            snprintf(t->version, sizeof t->version, "%s", !v ? "TLS" : !strcmp(v, "TLSv1.3") ? "TLS 1.3"
                     : !strcmp(v, "TLSv1.2") ? "TLS 1.2" : v);
            t->done = 1;
            return PLAT_TLS_OK;   // what is still queued goes out with the first read or write
        }
        if (err == SSL_ERROR_WANT_READ_) {
            if (!f) return PLAT_TLS_WANT_WRITE;
            int got = fill(t);
            if (got == PLAT_TLS_WANT_READ) return got;
            if (got == 0) return fail(t, PLAT_TLS_FAIL, "the server closed the connection during the handshake");
            if (got < 0) return fail(t, PLAT_TLS_FAIL, "receiving the handshake failed (errno %d)", errno);
            continue;
        }
        if (err == SSL_ERROR_WANT_WRITE_) {
            if (!f) return PLAT_TLS_WANT_WRITE;
            continue;
        }
        long verify = O.SSL_get_verify_result(t->ssl);
        if (verify != 0) return fail(t, PLAT_TLS_UNTRUSTED, "%s", O.X509_verify_cert_error_string(verify));
        return fail(t, PLAT_TLS_FAIL, "the TLS handshake failed (OpenSSL error %d)", err);
    }
}

long plat_tls_read(PlatTls *t, void *buf, size_t n, int peek) {
    if (t->failed || !t->done || !n) return PLAT_TLS_FAIL;
    if (flush(t) < 0) return fail(t, PLAT_TLS_FAIL, "sending failed (errno %d)", errno);
    int want = n > 0x40000000 ? 0x40000000 : (int)n;
    for (;;) {
        O.ERR_clear_error();
        int r = peek ? O.SSL_peek(t->ssl, buf, want) : O.SSL_read(t->ssl, buf, want);
        if (r > 0) return r;
        int err = O.SSL_get_error(t->ssl, r);
        if (flush(t) < 0) return fail(t, PLAT_TLS_FAIL, "sending failed (errno %d)", errno);
        if (err == SSL_ERROR_ZERO_RETURN_ || t->closed) return PLAT_TLS_CLOSED;
        if (err != SSL_ERROR_WANT_READ_) return fail(t, PLAT_TLS_FAIL, "reading failed (OpenSSL error %d)", err);
        int got = fill(t);
        if (got == PLAT_TLS_WANT_READ) return got;
        if (got < 0) return fail(t, PLAT_TLS_FAIL, "receiving failed (errno %d)", errno);
        if (got == 0) t->closed = 1;   // closed without a goodbye: one more read for what OpenSSL still holds
    }
}

long plat_tls_write(PlatTls *t, const void *buf, size_t n) {
    if (t->failed || !t->done || !n) return PLAT_TLS_FAIL;
    int f = flush(t);
    if (f < 0) return fail(t, PLAT_TLS_FAIL, "sending failed (errno %d)", errno);
    if (!f) return PLAT_TLS_WANT_WRITE;
    O.ERR_clear_error();
    int r = O.SSL_write(t->ssl, buf, n > RECORD_MAX ? RECORD_MAX : (int)n);
    if (r <= 0) return fail(t, PLAT_TLS_FAIL, "writing failed (OpenSSL error %d)", O.SSL_get_error(t->ssl, r));
    // as plat_tls_win.c: the record is taken; a short wait so the end of a request is not left queued
    uint64_t until = plat_ms() + FLUSH_WAIT_MS;
    while ((f = flush(t)) == 0 && plat_ms() < until) {
        PlatPollFd p = {t->s, PLAT_POLLOUT, 0};
        plat_poll(&p, 1, 20);
    }
    if (f < 0) return fail(t, PLAT_TLS_FAIL, "sending failed (errno %d)", errno);
    return r;
}

size_t plat_tls_pending(PlatTls *t) {
    int n = t->ssl ? O.SSL_pending(t->ssl) : 0;
    return n > 0 ? (size_t)n : 0;
}
const char *plat_tls_alpn(PlatTls *t) { return t->alpn; }
const char *plat_tls_version(PlatTls *t) { return t->version; }
const char *plat_tls_error(PlatTls *t) { return t->error; }

void plat_tls_free(PlatTls *t) {
    if (!t) return;
    if (t->done && !t->failed && !t->closed) {
        O.ERR_clear_error();
        O.SSL_shutdown(t->ssl);   // queues the goodbye
        flush(t);                 // best effort: no waiting
    }
    O.SSL_free(t->ssl);
    O.SSL_CTX_free(t->ctx);
    free(t);
}
