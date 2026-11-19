// plat_http.h on Linux: libcurl, loaded at run time (dlopen), so the plugin needs no build-time dependency and still
// loads where libcurl is missing (every request then answers status 0 with "libcurl not found").
// The curl_easy option and info numbers are libcurl's stable ABI (curl/curl.h: CURLOPT_* = type base + number).
#include "plat_http.h"

#include <dlfcn.h>
#include <pthread.h>
#include <stdio.h>
#include <string.h>
#include <strings.h>
#include <stdlib.h>

enum {
    OPT_WRITEDATA = 10001, OPT_URL = 10002, OPT_POSTFIELDS = 10015, OPT_USERAGENT = 10018, OPT_HTTPHEADER = 10023,
    OPT_HEADERDATA = 10029, OPT_CUSTOMREQUEST = 10036, OPT_WRITEFUNCTION = 20011, OPT_HEADERFUNCTION = 20079,
    OPT_POSTFIELDSIZE = 60, OPT_NOSIGNAL = 99, OPT_TIMEOUT_MS = 155, OPT_CONNECTTIMEOUT_MS = 156,
    INFO_RESPONSE_CODE = 0x200002,
    GLOBAL_DEFAULT = 3,
};

static struct {
    int loaded;
    void *(*easy_init)(void);
    int (*easy_setopt)(void *, int, ...);
    int (*easy_perform)(void *);
    int (*easy_getinfo)(void *, int, ...);
    void (*easy_cleanup)(void *);
    const char *(*easy_strerror)(int);
    void *(*slist_append)(void *, const char *);
    void (*slist_free_all)(void *);
    int (*global_init)(long);
} C;

static pthread_once_t once = PTHREAD_ONCE_INIT;
static _Thread_local char g_error[160];

const char *plat_http_error(void) { return g_error; }

static void load(void) {
    static const char *const names[] = {"libcurl.so.4", "libcurl-gnutls.so.4", "libcurl.so"};
    void *lib = NULL;
    for (size_t i = 0; i < sizeof names / sizeof *names && !lib; i++) lib = dlopen(names[i], RTLD_NOW | RTLD_LOCAL);
    if (!lib) return;
#define SYM(field, name) *(void **)&C.field = dlsym(lib, name)
    SYM(easy_init, "curl_easy_init");
    SYM(easy_setopt, "curl_easy_setopt");
    SYM(easy_perform, "curl_easy_perform");
    SYM(easy_getinfo, "curl_easy_getinfo");
    SYM(easy_cleanup, "curl_easy_cleanup");
    SYM(easy_strerror, "curl_easy_strerror");
    SYM(slist_append, "curl_slist_append");
    SYM(slist_free_all, "curl_slist_free_all");
    SYM(global_init, "curl_global_init");
#undef SYM
    if (!C.easy_init || !C.easy_setopt || !C.easy_perform || !C.easy_getinfo || !C.easy_cleanup || !C.slist_append ||
        !C.slist_free_all || !C.global_init)
        return;
    C.global_init(GLOBAL_DEFAULT);   // once, before any thread uses curl
    C.loaded = 1;
}

typedef struct {
    char *out;
    size_t cap, got;
    int retry_after;
} Sink;

static size_t on_body(char *data, size_t size, size_t n, void *user) {
    Sink *s = user;
    size_t len = size * n;
    if (s->cap && s->got < s->cap - 1) {
        size_t k = len < s->cap - 1 - s->got ? len : s->cap - 1 - s->got;
        memcpy(s->out + s->got, data, k);
        s->got += k;
    }
    return len;
}

static size_t on_header(char *data, size_t size, size_t n, void *user) {
    Sink *s = user;
    size_t len = size * n;
    if (len > 12 && !strncasecmp(data, "Retry-After:", 12)) s->retry_after = atoi(data + 12);
    return len;
}

PlatHttpResult plat_http(const char *method, const char *url, const char *bearer, const char *json, char *out,
                         size_t cap, int timeout_ms) {
    PlatHttpResult r = {0};
    if (cap) out[0] = 0;
    g_error[0] = 0;
    pthread_once(&once, load);
    if (!C.loaded) {
        snprintf(g_error, sizeof g_error, "libcurl not found");
        return r;
    }
    void *h = C.easy_init();
    if (!h) {
        snprintf(g_error, sizeof g_error, "curl_easy_init failed");
        return r;
    }
    Sink sink = {out, cap, 0, 0};
    void *headers = NULL;
    if (json) headers = C.slist_append(headers, "Content-Type: application/json");
    if (bearer && *bearer) {
        char b[320];
        snprintf(b, sizeof b, "Authorization: Bearer %s", bearer);
        headers = C.slist_append(headers, b);
    }
    C.easy_setopt(h, OPT_URL, url);
    C.easy_setopt(h, OPT_CUSTOMREQUEST, method);
    C.easy_setopt(h, OPT_USERAGENT, "mkwmp");
    C.easy_setopt(h, OPT_NOSIGNAL, 1L);
    C.easy_setopt(h, OPT_TIMEOUT_MS, (long)timeout_ms);
    C.easy_setopt(h, OPT_CONNECTTIMEOUT_MS, (long)timeout_ms);
    C.easy_setopt(h, OPT_WRITEFUNCTION, on_body);
    C.easy_setopt(h, OPT_WRITEDATA, &sink);
    C.easy_setopt(h, OPT_HEADERFUNCTION, on_header);
    C.easy_setopt(h, OPT_HEADERDATA, &sink);
    if (headers) C.easy_setopt(h, OPT_HTTPHEADER, headers);
    if (json || !strcmp(method, "POST")) {   // a POST without a body still says Content-Length: 0
        C.easy_setopt(h, OPT_POSTFIELDS, json ? json : "");
        C.easy_setopt(h, OPT_POSTFIELDSIZE, (long)(json ? strlen(json) : 0));
    }
    int e = C.easy_perform(h);
    if (e == 0) {
        long status = 0;
        C.easy_getinfo(h, INFO_RESPONSE_CODE, &status);
        r.status = (int)status;
        r.retry_after = sink.retry_after;
        r.length = sink.got;
    } else {
        snprintf(g_error, sizeof g_error, "%s", C.easy_strerror ? C.easy_strerror(e) : "curl error");
    }
    if (cap) out[sink.got < cap ? sink.got : cap - 1] = 0;
    if (headers) C.slist_free_all(headers);
    C.easy_cleanup(h);
    return r;
}
