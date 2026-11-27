// plat_tls.h on Windows: Schannel through SSPI (secur32), TLS 1.2, on a socket that may be non-blocking.
//
// Schannel is told not to validate the server (SCH_CRED_MANUAL_CRED_VALIDATION): the chain is checked here against the
// one CA the caller trusts, with crypt32 only for parsing and signatures. The system's root store plays no part, so a
// certificate a public CA issued for the same name is refused, and the caller's private CA needs no installing.
#define SECURITY_WIN32
#include "plat_tls.h"

#include "ghost.h"

#include <windows.h>
#include <wincrypt.h>
#include <schannel.h>
#include <security.h>

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifndef SECBUFFER_APPLICATION_PROTOCOLS
#define SECBUFFER_APPLICATION_PROTOCOLS 18
#endif
#ifndef SECPKG_ATTR_APPLICATION_PROTOCOL
#define SECPKG_ATTR_APPLICATION_PROTOCOL 35
#endif
#ifndef SCH_USE_STRONG_CRYPTO
#define SCH_USE_STRONG_CRYPTO 0x00400000
#endif
#ifndef SP_PROT_TLS1_2_CLIENT
#define SP_PROT_TLS1_2_CLIENT 0x00000800
#endif
#ifndef SP_PROT_TLS1_3_CLIENT
#define SP_PROT_TLS1_3_CLIENT 0x00002000
#endif

// SecPkgContext_ApplicationProtocol (sspi.h of Windows 8.1 and later), spelled out for older headers
typedef struct {
    int status;   // 1: the server chose a protocol
    int ext;
    unsigned char size;
    unsigned char id[255];
} AlpnResult;

enum {
    IN_CAP = 48 * 1024,         // two full records and change: what the socket gave and Schannel has not taken yet
    CHAIN_DEPTH = 6,
    FLUSH_WAIT_MS = 200,
    HS_FLAGS = ISC_REQ_SEQUENCE_DETECT | ISC_REQ_REPLAY_DETECT | ISC_REQ_CONFIDENTIALITY | ISC_REQ_ALLOCATE_MEMORY |
               ISC_REQ_STREAM | ISC_REQ_MANUAL_CRED_VALIDATION | ISC_REQ_USE_SUPPLIED_CREDS,
};

struct PlatTls {
    PlatSock s;
    CredHandle cred;
    CtxtHandle ctx;
    int have_cred, have_ctx, tokens_done, done, failed, closed, need_more, cred_asks;
    SecPkgContext_StreamSizes sizes;
    unsigned char *in;                       // from the socket, not yet taken by Schannel
    size_t in_len;
    unsigned char *plain;                    // decrypted, not yet read
    size_t plain_off, plain_len, plain_cap;
    unsigned char *out;                      // for the socket, not yet sent
    size_t out_off, out_len, out_cap;
    wchar_t whost[256];
    char host[256];
    unsigned char *ca;
    size_t ca_len;
    unsigned char alpn_in[96];
    size_t alpn_in_len;
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

// ---- the socket ----------------------------------------------------------------------------------------------------
// 1 all sent, 0 some is still queued (the socket is full), -1 failed
static int flush(PlatTls *t) {
    while (t->out_off < t->out_len) {
        int r = send(t->s, (const char *)t->out + t->out_off, (int)(t->out_len - t->out_off), 0);
        if (r > 0) t->out_off += (size_t)r;
        else if (plat_net_error() == PLAT_NET_WOULDBLOCK) return 0;
        else return -1;
    }
    t->out_off = t->out_len = 0;
    return 1;
}

static int out_room(PlatTls *t, size_t more) {
    if (t->out_len + more <= t->out_cap) return 1;
    size_t cap = t->out_len + more + 4096;
    unsigned char *p = realloc(t->out, cap);
    if (!p) return 0;
    t->out = p, t->out_cap = cap;
    return 1;
}

static int queue(PlatTls *t, const void *p, size_t n) {
    if (!out_room(t, n)) return 0;
    memcpy(t->out + t->out_len, p, n);
    t->out_len += n;
    return 1;
}

// More bytes from the socket: 1 got some, 0 the peer closed, PLAT_TLS_WANT_READ, -1 failed
static int fill(PlatTls *t) {
    if (t->in_len >= IN_CAP) return -1;
    int r = recv(t->s, (char *)t->in + t->in_len, (int)(IN_CAP - t->in_len), 0);
    if (r > 0) { t->in_len += (size_t)r; return 1; }
    if (r == 0) return 0;
    return plat_net_error() == PLAT_NET_WOULDBLOCK ? PLAT_TLS_WANT_READ : -1;
}

// ---- the server's certificate ------------------------------------------------------------------------------------
static int is_ca(PCCERT_CONTEXT c) {
    PCERT_EXTENSION e = CertFindExtension(szOID_BASIC_CONSTRAINTS2, c->pCertInfo->cExtension, c->pCertInfo->rgExtension);
    CERT_BASIC_CONSTRAINTS2_INFO info;
    DWORD size = sizeof info;
    return e && CryptDecodeObjectEx(X509_ASN_ENCODING, X509_BASIC_CONSTRAINTS2, e->Value.pbData, e->Value.cbData, 0, NULL,
                                    &info, &size) && info.fCA;
}

static int signed_by(PCCERT_CONTEXT subject, PCCERT_CONTEXT issuer) {
    return CertCompareCertificateName(X509_ASN_ENCODING, &subject->pCertInfo->Issuer, &issuer->pCertInfo->Subject) &&
           CryptVerifyCertificateSignatureEx(0, X509_ASN_ENCODING, CRYPT_VERIFY_CERT_SIGN_SUBJECT_CERT, (void *)subject,
                                             CRYPT_VERIFY_CERT_SIGN_ISSUER_CERT, (void *)issuer, 0, NULL);
}

static int same_cert(PCCERT_CONTEXT a, PCCERT_CONTEXT b) {
    return a->cbCertEncoded == b->cbCertEncoded && !memcmp(a->pbCertEncoded, b->pbCertEncoded, a->cbCertEncoded);
}

// 1 when the certificate names host: a DNS name of its subjectAltName (or the address, for an IP literal), else,
// only when it has no subjectAltName at all, its common name.
static int names_host(PCCERT_CONTEXT leaf, const char *host) {
    unsigned long ip = inet_addr(host);
    PCERT_EXTENSION e = CertFindExtension(szOID_SUBJECT_ALT_NAME2, leaf->pCertInfo->cExtension, leaf->pCertInfo->rgExtension);
    if (e) {
        CERT_ALT_NAME_INFO *names = NULL;
        DWORD size = 0;
        int ok = 0;
        if (!CryptDecodeObjectEx(X509_ASN_ENCODING, X509_ALTERNATE_NAME, e->Value.pbData, e->Value.cbData,
                                 CRYPT_DECODE_ALLOC_FLAG, NULL, &names, &size) || !names)
            return 0;
        for (DWORD i = 0; i < names->cAltEntry && !ok; i++) {
            const CERT_ALT_NAME_ENTRY *n = &names->rgAltEntry[i];
            if (n->dwAltNameChoice == CERT_ALT_NAME_DNS_NAME && n->pwszDNSName && ip == INADDR_NONE) {
                char name[256];
                size_t k = 0;
                for (; n->pwszDNSName[k] && k + 1 < sizeof name; k++) {
                    if (n->pwszDNSName[k] > 0x7e) break;   // not a plain host name
                    name[k] = (char)n->pwszDNSName[k];
                }
                name[k] = 0;
                ok = !n->pwszDNSName[k] && ghost_name_match(name, host);
            } else if (n->dwAltNameChoice == CERT_ALT_NAME_IP_ADDRESS && ip != INADDR_NONE) {
                ok = n->IPAddress.cbData == 4 && !memcmp(n->IPAddress.pbData, &ip, 4);
            }
        }
        LocalFree(names);
        return ok;
    }
    char cn[256];
    if (ip != INADDR_NONE || CertGetNameStringA(leaf, CERT_NAME_ATTR_TYPE, 0, (void *)szOID_COMMON_NAME, cn, sizeof cn) <= 1)
        return 0;
    return ghost_name_match(cn, host);
}

// The chain from the server's certificate, through the certificates it sent, to the caller's CA.
static int verify_peer(PlatTls *t) {
    PCCERT_CONTEXT leaf = NULL;
    if (QueryContextAttributesW(&t->ctx, SECPKG_ATTR_REMOTE_CERT_CONTEXT, &leaf) != SEC_E_OK || !leaf)
        return fail(t, PLAT_TLS_UNTRUSTED, "the server sent no certificate");
    PCCERT_CONTEXT ca = CertCreateCertificateContext(X509_ASN_ENCODING, t->ca, (DWORD)t->ca_len);
    if (!ca) {
        CertFreeCertificateContext(leaf);
        return fail(t, PLAT_TLS_UNTRUSTED, "the trusted CA is not a certificate");
    }
    const char *why = NULL;
    PCCERT_CONTEXT cur = CertDuplicateCertificateContext(leaf);
    for (int depth = 0; !why; depth++) {
        if (CertVerifyTimeValidity(NULL, cur->pCertInfo)) { why = "a certificate of the chain is outside its dates"; break; }
        if (depth > 0 && !is_ca(cur)) { why = "the chain passes through a certificate that is not a CA"; break; }
        if (same_cert(cur, ca)) break;   // the trusted certificate itself
        if (signed_by(cur, ca)) {
            if (CertVerifyTimeValidity(NULL, ca->pCertInfo)) why = "the trusted CA is outside its dates";
            break;
        }
        if (depth + 1 >= CHAIN_DEPTH) { why = "the chain is too long"; break; }
        DWORD flags = CERT_STORE_SIGNATURE_FLAG;
        PCCERT_CONTEXT issuer = CertGetIssuerCertificateFromStore(leaf->hCertStore, cur, NULL, &flags);
        if (!issuer || (flags & CERT_STORE_SIGNATURE_FLAG) || same_cert(issuer, cur)) {
            if (issuer) CertFreeCertificateContext(issuer);
            why = "the certificate was not issued by the trusted CA";
            break;
        }
        CertFreeCertificateContext(cur);
        cur = issuer;
    }
    if (!why && !names_host(leaf, t->host)) why = "the certificate is for another host name";
    CertFreeCertificateContext(cur);
    CertFreeCertificateContext(ca);
    CertFreeCertificateContext(leaf);
    return why ? fail(t, PLAT_TLS_UNTRUSTED, "%s", why) : PLAT_TLS_OK;
}

// ---- set-up --------------------------------------------------------------------------------------------------------
PlatTls *plat_tls_new(PlatSock s, const char *host, const unsigned char *ca, size_t ca_len, const unsigned char *alpn,
                      size_t alpn_len, char *error, size_t error_cap) {
    if (error && error_cap) error[0] = 0;
    PlatTls *t = calloc(1, sizeof *t);
    if (t) t->in = malloc(IN_CAP), t->ca = malloc(ca_len ? ca_len : 1);
    if (!t || !t->in || !t->ca || !host || !*host || strlen(host) >= sizeof t->host || !ca || !ca_len ||
        alpn_len > sizeof t->alpn_in - 10) {
        if (error) snprintf(error, error_cap, "bad arguments or out of memory");
        if (t) free(t->in), free(t->ca);
        free(t);
        return NULL;
    }
    t->s = s;
    snprintf(t->host, sizeof t->host, "%s", host);
    for (size_t i = 0; host[i]; i++) t->whost[i] = (wchar_t)(unsigned char)host[i];
    memcpy(t->ca, ca, ca_len);
    t->ca_len = ca_len;
    if (alpn && alpn_len) {
        // SEC_APPLICATION_PROTOCOLS: the size of what follows, then one list: ALPN (2), its size, the wire form
        unsigned ext = 2, lists = (unsigned)(sizeof ext + sizeof(unsigned short) + alpn_len);
        unsigned short n = (unsigned short)alpn_len;
        memcpy(t->alpn_in, &lists, 4);
        memcpy(t->alpn_in + 4, &ext, 4);
        memcpy(t->alpn_in + 8, &n, 2);
        memcpy(t->alpn_in + 10, alpn, alpn_len);
        t->alpn_in_len = 10 + alpn_len;
    }
    SCHANNEL_CRED sc;
    memset(&sc, 0, sizeof sc);
    sc.dwVersion = SCHANNEL_CRED_VERSION;
    sc.grbitEnabledProtocols = SP_PROT_TLS1_2_CLIENT;
    sc.dwFlags = SCH_CRED_MANUAL_CRED_VALIDATION | SCH_CRED_NO_DEFAULT_CREDS | SCH_USE_STRONG_CRYPTO;
    SECURITY_STATUS st = AcquireCredentialsHandleW(NULL, (wchar_t *)UNISP_NAME_W, SECPKG_CRED_OUTBOUND, NULL, &sc, NULL, NULL,
                                                   &t->cred, NULL);
    if (st != SEC_E_OK) {
        if (error) snprintf(error, error_cap, "no TLS credentials (Schannel status %#lx)", (unsigned long)st);
        free(t->in), free(t->ca), free(t);
        return NULL;
    }
    t->have_cred = 1;
    return t;
}

static int finish(PlatTls *t) {
    if (QueryContextAttributesW(&t->ctx, SECPKG_ATTR_STREAM_SIZES, &t->sizes) != SEC_E_OK)
        return fail(t, PLAT_TLS_FAIL, "no record sizes from Schannel");
    int v = verify_peer(t);
    if (v != PLAT_TLS_OK) return v;
    AlpnResult chosen;
    memset(&chosen, 0, sizeof chosen);
    if (QueryContextAttributesW(&t->ctx, SECPKG_ATTR_APPLICATION_PROTOCOL, &chosen) == SEC_E_OK && chosen.status == 1 &&
        chosen.size < sizeof t->alpn) {
        memcpy(t->alpn, chosen.id, chosen.size);
        t->alpn[chosen.size] = 0;
    }
    SecPkgContext_ConnectionInfo info;
    memset(&info, 0, sizeof info);
    if (QueryContextAttributesW(&t->ctx, SECPKG_ATTR_CONNECTION_INFO, &info) == SEC_E_OK)
        snprintf(t->version, sizeof t->version, "%s", info.dwProtocol & SP_PROT_TLS1_3_CLIENT ? "TLS 1.3"
                 : info.dwProtocol & SP_PROT_TLS1_2_CLIENT ? "TLS 1.2" : "TLS");
    t->done = 1;
    return PLAT_TLS_OK;
}

int plat_tls_handshake(PlatTls *t) {
    if (t->failed) return t->failed;
    if (t->done) return PLAT_TLS_OK;
    for (;;) {
        int f = flush(t);
        if (f < 0) return fail(t, PLAT_TLS_FAIL, "sending the handshake failed (socket error %d)", WSAGetLastError());
        if (!f) return PLAT_TLS_WANT_WRITE;
        if (t->tokens_done) return finish(t);
        SecBuffer ob = {0, SECBUFFER_TOKEN, NULL};
        SecBufferDesc od = {SECBUFFER_VERSION, 1, &ob};
        DWORD attrs = 0;
        SECURITY_STATUS st;
        if (!t->have_ctx) {
            SecBuffer ib = {(unsigned long)t->alpn_in_len, SECBUFFER_APPLICATION_PROTOCOLS, t->alpn_in};
            SecBufferDesc id = {SECBUFFER_VERSION, 1, &ib};
            st = InitializeSecurityContextW(&t->cred, NULL, t->whost, HS_FLAGS, 0, 0, t->alpn_in_len ? &id : NULL, 0,
                                            &t->ctx, &od, &attrs, NULL);
            t->have_ctx = st == SEC_I_CONTINUE_NEEDED || st == SEC_E_OK;
        } else {
            if (t->need_more || !t->in_len) {
                int r = fill(t);
                if (r == PLAT_TLS_WANT_READ) return r;
                if (r == 0) return fail(t, PLAT_TLS_FAIL, "the server closed the connection during the handshake");
                if (r < 0) return fail(t, PLAT_TLS_FAIL, "receiving the handshake failed (socket error %d)", WSAGetLastError());
                t->need_more = 0;
            }
            SecBuffer ib[2] = {{(unsigned long)t->in_len, SECBUFFER_TOKEN, t->in}, {0, SECBUFFER_EMPTY, NULL}};
            SecBufferDesc id = {SECBUFFER_VERSION, 2, ib};
            st = InitializeSecurityContextW(&t->cred, &t->ctx, t->whost, HS_FLAGS, 0, 0, &id, 0, NULL, &od, &attrs, NULL);
            if (st == SEC_E_INCOMPLETE_MESSAGE) {
                if (t->in_len >= IN_CAP) return fail(t, PLAT_TLS_FAIL, "a handshake message does not fit");
                t->need_more = 1;
                continue;
            }
            if (st == SEC_E_OK || st == SEC_I_CONTINUE_NEEDED) {
                if (ib[1].BufferType == SECBUFFER_EXTRA && ib[1].cbBuffer && ib[1].cbBuffer <= t->in_len) {
                    memmove(t->in, t->in + t->in_len - ib[1].cbBuffer, ib[1].cbBuffer);
                    t->in_len = ib[1].cbBuffer;
                } else {
                    t->in_len = 0;
                }
            }
        }
        if (ob.pvBuffer) {
            int queued = !ob.cbBuffer || queue(t, ob.pvBuffer, ob.cbBuffer);
            FreeContextBuffer(ob.pvBuffer);
            if (!queued) return fail(t, PLAT_TLS_FAIL, "out of memory");
        }
        if (st == SEC_I_CONTINUE_NEEDED) continue;
        if (st == SEC_E_OK) { t->tokens_done = 1; continue; }   // its last message goes out, then the certificate
        if (st == SEC_I_INCOMPLETE_CREDENTIALS && t->cred_asks++ < 2) continue;   // a client certificate: there is none
        return fail(t, PLAT_TLS_FAIL, "the TLS handshake failed (Schannel status %#lx)", (unsigned long)st);
    }
}

// ---- data ----------------------------------------------------------------------------------------------------------
static int plain_add(PlatTls *t, const void *p, size_t n) {
    if (t->plain_off == t->plain_len) t->plain_off = t->plain_len = 0;
    if (t->plain_len + n > t->plain_cap) {
        size_t cap = t->plain_len + n + 4096;
        unsigned char *q = realloc(t->plain, cap);
        if (!q) return 0;
        t->plain = q, t->plain_cap = cap;
    }
    memcpy(t->plain + t->plain_len, p, n);
    t->plain_len += n;
    return 1;
}

long plat_tls_read(PlatTls *t, void *buf, size_t n, int peek) {
    if (t->failed || !t->done) return PLAT_TLS_FAIL;
    if (!n) return PLAT_TLS_FAIL;
    if (flush(t) < 0) return fail(t, PLAT_TLS_FAIL, "sending failed (socket error %d)", WSAGetLastError());
    for (;;) {
        if (t->plain_len > t->plain_off) {
            size_t take = t->plain_len - t->plain_off;
            if (take > n) take = n;
            if (take > 0x40000000) take = 0x40000000;
            memcpy(buf, t->plain + t->plain_off, take);
            if (!peek) t->plain_off += take;
            return (long)take;
        }
        if (t->closed) return PLAT_TLS_CLOSED;
        if (t->in_len && !t->need_more) {
            SecBuffer b[4] = {{(unsigned long)t->in_len, SECBUFFER_DATA, t->in}, {0, SECBUFFER_EMPTY, NULL},
                              {0, SECBUFFER_EMPTY, NULL}, {0, SECBUFFER_EMPTY, NULL}};
            SecBufferDesc d = {SECBUFFER_VERSION, 4, b};
            SECURITY_STATUS st = DecryptMessage(&t->ctx, &d, 0, NULL);
            if (st == SEC_E_OK || st == SEC_I_CONTEXT_EXPIRED || st == SEC_I_RENEGOTIATE) {
                SecBuffer *data = NULL, *extra = NULL;
                for (int i = 0; i < 4; i++) {
                    if (b[i].BufferType == SECBUFFER_DATA && !data) data = &b[i];
                    if (b[i].BufferType == SECBUFFER_EXTRA && !extra) extra = &b[i];
                }
                if (st == SEC_E_OK && data && data->cbBuffer && !plain_add(t, data->pvBuffer, data->cbBuffer))
                    return fail(t, PLAT_TLS_FAIL, "out of memory");
                if (extra && extra->cbBuffer && extra->cbBuffer <= t->in_len) {
                    memmove(t->in, extra->pvBuffer, extra->cbBuffer);
                    t->in_len = extra->cbBuffer;
                } else {
                    t->in_len = 0;
                }
                if (st == SEC_I_CONTEXT_EXPIRED) t->closed = 1;   // the server's goodbye
                if (st == SEC_I_RENEGOTIATE) return fail(t, PLAT_TLS_FAIL, "the server asked for a new handshake");
                continue;
            }
            if (st != SEC_E_INCOMPLETE_MESSAGE)
                return fail(t, PLAT_TLS_FAIL, "a record could not be decrypted (Schannel status %#lx)", (unsigned long)st);
            if (t->in_len >= IN_CAP) return fail(t, PLAT_TLS_FAIL, "a record does not fit");
            t->need_more = 1;
        }
        int r = fill(t);
        if (r == PLAT_TLS_WANT_READ) return r;
        if (r < 0) return fail(t, PLAT_TLS_FAIL, "receiving failed (socket error %d)", WSAGetLastError());
        if (r == 0) { t->closed = 1; continue; }   // closed without a goodbye: the caller sees the end
        t->need_more = 0;
    }
}

long plat_tls_write(PlatTls *t, const void *buf, size_t n) {
    if (t->failed || !t->done || !n) return PLAT_TLS_FAIL;
    int f = flush(t);
    if (f < 0) return fail(t, PLAT_TLS_FAIL, "sending failed (socket error %d)", WSAGetLastError());
    if (!f) return PLAT_TLS_WANT_WRITE;
    size_t take = n < t->sizes.cbMaximumMessage ? n : t->sizes.cbMaximumMessage;
    if (!out_room(t, t->sizes.cbHeader + take + t->sizes.cbTrailer)) return fail(t, PLAT_TLS_FAIL, "out of memory");
    memcpy(t->out + t->sizes.cbHeader, buf, take);
    SecBuffer b[4] = {{t->sizes.cbHeader, SECBUFFER_STREAM_HEADER, t->out},
                      {(unsigned long)take, SECBUFFER_DATA, t->out + t->sizes.cbHeader},
                      {t->sizes.cbTrailer, SECBUFFER_STREAM_TRAILER, t->out + t->sizes.cbHeader + take},
                      {0, SECBUFFER_EMPTY, NULL}};
    SecBufferDesc d = {SECBUFFER_VERSION, 4, b};
    SECURITY_STATUS st = EncryptMessage(&t->ctx, 0, &d, 0);
    if (st != SEC_E_OK) return fail(t, PLAT_TLS_FAIL, "a record could not be encrypted (Schannel status %#lx)", (unsigned long)st);
    t->out_off = 0;
    t->out_len = (size_t)b[0].cbBuffer + b[1].cbBuffer + b[2].cbBuffer;
    // The record is the caller's bytes from here on. What the socket does not take now goes out with the next call;
    // a short wait first, so that the end of a request is not left queued while the caller waits for the answer.
    uint64_t until = plat_ms() + FLUSH_WAIT_MS;
    while ((f = flush(t)) == 0 && plat_ms() < until) {
        PlatPollFd p = {t->s, PLAT_POLLOUT, 0};
        plat_poll(&p, 1, 20);
    }
    if (f < 0) return fail(t, PLAT_TLS_FAIL, "sending failed (socket error %d)", WSAGetLastError());
    return (long)take;
}

size_t plat_tls_pending(PlatTls *t) { return t->plain_len - t->plain_off; }
const char *plat_tls_alpn(PlatTls *t) { return t->alpn; }
const char *plat_tls_version(PlatTls *t) { return t->version; }
const char *plat_tls_error(PlatTls *t) { return t->error; }

void plat_tls_free(PlatTls *t) {
    if (!t) return;
    if (t->have_ctx) {
        if (t->done && !t->failed && !t->closed && flush(t) == 1) {
            DWORD type = SCHANNEL_SHUTDOWN;
            SecBuffer cb = {sizeof type, SECBUFFER_TOKEN, &type};
            SecBufferDesc cd = {SECBUFFER_VERSION, 1, &cb};
            if (ApplyControlToken(&t->ctx, &cd) == SEC_E_OK) {
                SecBuffer ob = {0, SECBUFFER_TOKEN, NULL};
                SecBufferDesc od = {SECBUFFER_VERSION, 1, &ob};
                DWORD attrs = 0;
                InitializeSecurityContextW(&t->cred, &t->ctx, t->whost, HS_FLAGS, 0, 0, NULL, 0, NULL, &od, &attrs, NULL);
                if (ob.pvBuffer) {
                    if (ob.cbBuffer) send(t->s, ob.pvBuffer, (int)ob.cbBuffer, 0);   // best effort: no waiting
                    FreeContextBuffer(ob.pvBuffer);
                }
            }
        }
        DeleteSecurityContext(&t->ctx);
    }
    if (t->have_cred) FreeCredentialsHandle(&t->cred);
    free(t->in), free(t->plain), free(t->out), free(t->ca);
    free(t);
}
