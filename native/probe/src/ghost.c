#include "ghost.h"

#include <stdarg.h>
#include <stdio.h>
#include <string.h>

// ---- certificate names -------------------------------------------------------------------------------------------
static int lower(int c) { return c >= 'A' && c <= 'Z' ? c + 32 : c; }

static int same_text(const char *a, size_t an, const char *b, size_t bn) {
    if (an != bn) return 0;
    for (size_t i = 0; i < an; i++)
        if (lower((unsigned char)a[i]) != lower((unsigned char)b[i])) return 0;
    return 1;
}

int ghost_name_match(const char *pattern, const char *host) {
    if (!pattern || !host || !*pattern || !*host) return 0;
    size_t pn = strlen(pattern), hn = strlen(host);
    if (hn && host[hn - 1] == '.') hn--;
    if (pn && pattern[pn - 1] == '.') pn--;
    if (!pn || !hn) return 0;
    if (memchr(pattern, '*', pn) == NULL) return same_text(pattern, pn, host, hn);
    // a wildcard: "*." and at least two more labels, no other "*"
    if (pn < 3 || pattern[0] != '*' || pattern[1] != '.' || memchr(pattern + 1, '*', pn - 1)) return 0;
    const char *rest = pattern + 2;
    size_t rn = pn - 2;
    if (!memchr(rest, '.', rn)) return 0;
    const char *dot = memchr(host, '.', hn);
    if (!dot || dot == host) return 0;   // the wildcard stands for one label, and not an empty one
    size_t tail = hn - (size_t)(dot + 1 - host);
    return same_text(rest, rn, dot + 1, tail);
}

// ---- the HTTP exchange -------------------------------------------------------------------------------------------
enum { GD_HEAD = 0, GD_BODY, GD_UNTIL_CLOSE, GD_CHUNK_SIZE, GD_CHUNK_DATA, GD_CHUNK_END, GD_TRAILER };

typedef struct {
    char (*lines)[GHOST_LINE_MAX];
    int n, max;
} Out;

static void emit(Out *o, const char *fmt, ...) {
    if (o->n >= o->max) return;
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(o->lines[o->n++], GHOST_LINE_MAX, fmt, ap);
    va_end(ap);
}

// The value of a header of the head (lines after the first), trimmed; 1 when present.
static int header(const char *head, const char *name, char *out, size_t cap) {
    size_t nn = strlen(name);
    const char *p = strstr(head, "\r\n");
    while (p) {
        p += 2;
        const char *end = strstr(p, "\r\n");
        size_t len = end ? (size_t)(end - p) : strlen(p);
        if (len > nn && p[nn] == ':' && same_text(p, nn, name, nn)) {
            const char *v = p + nn + 1;
            size_t vn = len - nn - 1;
            while (vn && (*v == ' ' || *v == '\t')) v++, vn--;
            while (vn && (v[vn - 1] == ' ' || v[vn - 1] == '\t')) vn--;
            if (vn >= cap) vn = cap - 1;
            memcpy(out, v, vn);
            out[vn] = 0;
            return 1;
        }
        p = end;
    }
    return 0;
}

static int number(const char *s, uint64_t *out) {
    uint64_t v = 0;
    if (!*s) return 0;
    for (; *s; s++) {
        if (*s < '0' || *s > '9' || v > (UINT64_MAX - 9) / 10) return 0;
        v = v * 10 + (uint64_t)(*s - '0');
    }
    *out = v;
    return 1;
}

// A Range / Content-Range value is logged only when it is the byte arithmetic it should be.
static int plain_range(const char *s) {
    if (!*s || strlen(s) > 60) return 0;
    for (; *s; s++)
        if (!strchr("bytes =*/-0123456789", *s)) return 0;
    return 1;
}

static int chunked(const char *head) {
    char v[64];
    if (!header(head, "Transfer-Encoding", v, sizeof v)) return 0;
    for (char *p = v; *p; p++) *p = (char)lower((unsigned char)*p);
    return strstr(v, "chunked") != NULL;
}

static void request_head(GhostTrace *t, Out *o) {
    GhostDir *d = &t->tx;
    const char *sp = strchr(d->head, ' '), *line_end = strstr(d->head, "\r\n");
    size_t mn = sp ? (size_t)(sp - d->head) : 0;
    if (!sp || !line_end || sp > line_end || !mn || mn >= sizeof t->method) { d->lost = 1; return; }
    for (size_t i = 0; i < mn; i++)
        if (d->head[i] < 'A' || d->head[i] > 'Z') { d->lost = 1; return; }
    memcpy(t->method, d->head, mn);
    t->method[mn] = 0;
    const char *target = sp + 1, *target_end = memchr(target, ' ', (size_t)(line_end - target));
    if (!target_end) target_end = line_end;
    const char *q = memchr(target, '?', (size_t)(target_end - target));
    size_t path = (size_t)((q ? q : target_end) - target), query = q ? (size_t)(target_end - q - 1) : 0;

    char v[80], range[80] = "";
    uint64_t length = 0;
    int has_length = header(d->head, "Content-Length", v, sizeof v) && number(v, &length);
    int is_chunked = chunked(d->head);
    const char *range_name = "Content-Range";
    if (!(header(d->head, "Content-Range", range, sizeof range) && plain_range(range))) {
        range_name = "Range";
        if (!(header(d->head, "Range", range, sizeof range) && plain_range(range))) range[0] = 0;
    }
    t->upload = !strcmp(t->method, "PUT") || !strcmp(t->method, "POST") || !strcmp(t->method, "PATCH");
    t->requests++;
    char where[200], extra[100] = "";
    snprintf(where, sizeof where, "%s https://%s (path %zu chars, signed query %zu chars)", t->method, t->host, path, query);
    if (range[0]) snprintf(extra, sizeof extra, ", %s %s", range_name, range);
    if (t->upload && has_length && !length && !strncmp(range, "bytes */", 8))
        emit(o, "ghost: upload status request: %s%s", where, extra);
    else if (t->upload && is_chunked)
        emit(o, "ghost: upload request: %s, chunked%s", where, extra);
    else if (t->upload)
        emit(o, "ghost: upload request: %s, %llu bytes%s", where, (unsigned long long)length, extra);
    else if (!strcmp(t->method, "GET"))
        emit(o, "ghost: download request: %s%s", where, extra);
    else
        emit(o, "ghost: request: %s%s", where, extra);
    d->body = 0;
    if (is_chunked) d->state = GD_CHUNK_SIZE, d->num_len = 0;
    else if (has_length && length) d->state = GD_BODY, d->left = length;
    else d->state = GD_HEAD;
}

static void response_head(GhostTrace *t, Out *o) {
    GhostDir *d = &t->rx;
    int code = 0;
    if (strncmp(d->head, "HTTP/", 5) || sscanf(d->head, "HTTP/%*9s %3d", &code) != 1 || code < 100 || code > 599) {
        d->lost = 1;
        return;
    }
    if (code < 200 && code != 101) { d->state = GD_HEAD; return; }   // 100 Continue: the real answer follows
    t->responses++;
    char v[80], range[80] = "", extra[160] = "";
    uint64_t length = 0, stored = 0;
    int has_length = header(d->head, "Content-Length", v, sizeof v) && number(v, &length);
    int is_chunked = chunked(d->head);
    int no_body = !strcmp(t->method, "HEAD") || code == 204 || code == 304;
    size_t n = 0;
    if (!no_body && has_length) n += (size_t)snprintf(extra + n, sizeof extra - n, ", %llu bytes", (unsigned long long)length);
    else if (!no_body && is_chunked) n += (size_t)snprintf(extra + n, sizeof extra - n, ", chunked");
    if (header(d->head, "Range", range, sizeof range) && plain_range(range))
        n += (size_t)snprintf(extra + n, sizeof extra - n, ", Range %s", range);
    else if (header(d->head, "Content-Range", range, sizeof range) && plain_range(range))
        n += (size_t)snprintf(extra + n, sizeof extra - n, ", Content-Range %s", range);
    if (header(d->head, "x-goog-stored-content-length", v, sizeof v) && number(v, &stored))
        snprintf(extra + n, sizeof extra - n, ", stored %llu bytes", (unsigned long long)stored);
    const char *meaning = code == 308 ? " (part stored, more expected)" : code >= 400 ? " (refused)"
                        : code >= 300 ? " (redirect)" : "";
    emit(o, "ghost: %s answer: HTTP %d%s%s", t->upload ? "upload" : "download", code, meaning, extra);
    d->body = 0;
    if (no_body) d->state = GD_HEAD;
    else if (is_chunked) d->state = GD_CHUNK_SIZE, d->num_len = 0;
    else if (has_length) d->state = length ? GD_BODY : GD_HEAD, d->left = length;
    else d->state = GD_UNTIL_CLOSE;
}

static void body_done(GhostTrace *t, int sent, Out *o) {
    GhostDir *d = sent ? &t->tx : &t->rx;
    if (sent) emit(o, "ghost: upload body sent: %llu bytes", (unsigned long long)d->body);
    else if (!t->upload) emit(o, "ghost: download body received: %llu bytes", (unsigned long long)d->body);
    d->state = GD_HEAD;
}

static void feed(GhostTrace *t, int sent, const unsigned char *p, size_t n, Out *o) {
    GhostDir *d = sent ? &t->tx : &t->rx;
    while (n && !d->lost) {
        switch (d->state) {
        case GD_HEAD:
            if (d->head_len + 1 >= sizeof d->head) { d->lost = 1; break; }
            d->head[d->head_len++] = (char)*p++, n--;
            if (d->head_len >= 4 && !memcmp(d->head + d->head_len - 4, "\r\n\r\n", 4)) {
                d->head[d->head_len] = 0;
                if (memchr(d->head, 0, d->head_len)) d->lost = 1;   // not text: not HTTP
                else if (sent) request_head(t, o);
                else response_head(t, o);
                d->head_len = 0;
            }
            break;
        case GD_BODY:
        case GD_CHUNK_DATA: {
            size_t take = n < d->left ? n : (size_t)d->left;
            d->left -= take, d->body += take, p += take, n -= take;
            if (!d->left) {
                if (d->state == GD_BODY) body_done(t, sent, o);
                else d->state = GD_CHUNK_END, d->left = 2;
            }
            break;
        }
        case GD_UNTIL_CLOSE:
            d->body += n, n = 0;
            break;
        case GD_CHUNK_END:   // the CR LF after a chunk's data
            p++, n--;
            if (!--d->left) d->state = GD_CHUNK_SIZE, d->num_len = 0;
            break;
        case GD_CHUNK_SIZE: {
            char c = (char)*p++;
            n--;
            if (c == '\n') {
                uint64_t size = 0;
                size_t i = 0;
                for (; i < d->num_len; i++) {
                    int h = lower((unsigned char)d->num[i]);
                    int digit = h >= '0' && h <= '9' ? h - '0' : h >= 'a' && h <= 'f' ? h - 'a' + 10 : -1;
                    if (digit < 0) break;   // ";extension" or the CR
                    size = size * 16 + (uint64_t)digit;
                }
                if (!i) { d->lost = 1; break; }
                if (size) d->state = GD_CHUNK_DATA, d->left = size;
                else d->state = GD_TRAILER, d->num_len = 0;
            } else if (d->num_len < sizeof d->num) d->num[d->num_len++] = c;
            break;
        }
        case GD_TRAILER: {   // lines until an empty one
            char c = (char)*p++;
            n--;
            if (c == '\n') {
                if (d->num_len <= 1) body_done(t, sent, o);   // "" or "\r"
                d->num_len = 0;
            } else if (d->num_len < sizeof d->num) d->num_len++;
            break;
        }
        default:
            d->lost = 1;
        }
    }
}

void ghost_trace_start(GhostTrace *t, const char *host) {
    memset(t, 0, sizeof *t);
    snprintf(t->host, sizeof t->host, "%s", host && *host ? host : "?");
}

int ghost_trace_feed(GhostTrace *t, int sent, const void *data, size_t n, char lines[][GHOST_LINE_MAX], int max) {
    Out o = {lines, 0, max < 0 ? 0 : max};
    if (!data || !n) return 0;
    if (sent) t->sent += n;
    else t->received += n;
    int was_lost = sent ? t->tx.lost : t->rx.lost;
    feed(t, sent, data, n, &o);
    if (!was_lost && (sent ? t->tx.lost : t->rx.lost))
        emit(&o, "ghost: %s on the connection to %s is not HTTP/1.1 as this log can follow it: bytes are only counted "
                 "from here", sent ? "what the game sends" : "the answer", t->host);
    return o.n;
}

void ghost_trace_end(const GhostTrace *t, char *line, size_t cap) {
    char note[120] = "";
    if (t->rx.state == GD_BODY && t->rx.left)
        snprintf(note, sizeof note, " (answer cut short: %llu bytes missing)", (unsigned long long)t->rx.left);
    else if (t->tx.state == GD_BODY && t->tx.left)
        snprintf(note, sizeof note, " (upload cut short: %llu bytes not sent)", (unsigned long long)t->tx.left);
    else if (t->requests > t->responses)
        snprintf(note, sizeof note, " (no answer to the last request)");
    snprintf(line, cap, "ghost: connection to %s closed: %u request(s), %u answer(s), sent %llu bytes, received %llu bytes%s",
             t->host, t->requests, t->responses, (unsigned long long)t->sent, (unsigned long long)t->received, note);
}
