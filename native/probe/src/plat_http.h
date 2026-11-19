// HTTP(S) requests for the plugin build, which talks to the seat server itself when no MKWMP launcher runs
// (seatapi.c). Windows: WinHTTP (the system's TLS and proxy settings). Linux: libcurl, loaded at run time
// (libcurl.so.4; SteamOS and the desktop distributions ship it), so the plugin links nothing new.
#ifndef MKWMP_PLAT_HTTP_H
#define MKWMP_PLAT_HTTP_H

#include <stddef.h>

typedef struct {
    int status;        // the HTTP status; 0: no answer (offline, DNS, TLS, timeout, no HTTP library)
    int retry_after;   // the Retry-After header in seconds, 0 when absent
    size_t length;     // bytes of the body kept in out (cut at cap - 1)
} PlatHttpResult;

// One request. url: http:// or https://. bearer: the Authorization token, or NULL. json: a request body sent as
// application/json, or NULL for none. The answer's body goes into out, NUL-terminated. Blocks for at most timeout_ms.
// Any thread; requests on several threads run in parallel.
PlatHttpResult plat_http(const char *method, const char *url, const char *bearer, const char *json, char *out,
                         size_t cap, int timeout_ms);

// Why the last request on this thread got no answer (status 0), for the log: "libcurl not found", the OS error...
const char *plat_http_error(void);

#endif
