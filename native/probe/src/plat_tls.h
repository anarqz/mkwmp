// A TLS client on a socket the caller owns, for the game's HTTPS transfers (nnssl.c: the console's TLS service, which
// mkw-recomp does not provide). Windows: Schannel (TLS 1.2), with the certificate checked by hand against the one CA
// given. Linux: OpenSSL (libssl.so.3 or .1.1), loaded at run time as plat_http_posix.c loads libcurl; without it
// plat_tls_new fails and says so.
//
// The certificate is always checked: the chain must end at `ca`, be within its dates, and name `host`. The system's
// trust store is never used. The socket may be non-blocking: every call then returns PLAT_TLS_WANT_* instead of
// waiting, and is called again when the socket is ready. One connection is used by one thread at a time.
#ifndef MKWMP_PLAT_TLS_H
#define MKWMP_PLAT_TLS_H

#include "plat_net.h"

#include <stddef.h>

typedef struct PlatTls PlatTls;

enum {
    PLAT_TLS_OK = 1,
    PLAT_TLS_CLOSED = 0,        // read: the peer ended the connection
    PLAT_TLS_FAIL = -1,         // plat_tls_error says why
    PLAT_TLS_WANT_READ = -2,    // call again when the socket is readable
    PLAT_TLS_WANT_WRITE = -3,   // ... writable
    PLAT_TLS_UNTRUSTED = -4,    // handshake: the server's certificate was refused (plat_tls_error says why)
};

// host: the name asked of the server (SNI) and checked in its certificate. ca: the one trusted CA, DER. alpn: the
// protocols to offer in wire form (each a length byte and the name: "\x08http/1.1"), or NULL. NULL when it cannot be
// set up: why goes into error (when not NULL).
PlatTls *plat_tls_new(PlatSock s, const char *host, const unsigned char *ca, size_t ca_len, const unsigned char *alpn,
                      size_t alpn_len, char *error, size_t error_cap);
// One step of the handshake: PLAT_TLS_OK when it is done (then read / write), else WANT_*, FAIL or UNTRUSTED.
int plat_tls_handshake(PlatTls *t);
// Up to n plaintext bytes: the count (> 0), PLAT_TLS_CLOSED, WANT_READ or FAIL. peek leaves them to be read again.
long plat_tls_read(PlatTls *t, void *buf, size_t n, int peek);
// Takes up to n bytes: how many (> 0; they are sent or queued), WANT_WRITE (none taken) or FAIL.
long plat_tls_write(PlatTls *t, const void *buf, size_t n);
// Plaintext bytes plat_tls_read would return without touching the socket.
size_t plat_tls_pending(PlatTls *t);
// The protocol the server chose (ALPN), "" when it chose none.
const char *plat_tls_alpn(PlatTls *t);
// "TLS 1.2" ... for the log, "" before the handshake is done.
const char *plat_tls_version(PlatTls *t);
const char *plat_tls_error(PlatTls *t);
// Says goodbye to the peer when it can without waiting, and frees. The socket stays open: the caller's.
void plat_tls_free(PlatTls *t);

#endif
