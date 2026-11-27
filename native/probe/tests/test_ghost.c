// Ghost transfers, the pure parts (ghost.h): certificate names, and the log of an HTTP/1.1 exchange: a download, an
// upload in parts with its status request, bytes arriving in any split, a chunked answer, and that nothing of a link
// but its host ever reaches a line.
//
//   $zig = "C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"; $out = "$env:TEMP\mkwmp-tests"; mkdir -Force $out
//   & $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o $out\test_ghost.exe `
//       native\probe\tests\test_ghost.c native\probe\src\ghost.c
//   & $out\test_ghost.exe
#undef NDEBUG
#include <assert.h>
#include <stdio.h>
#include <string.h>

#include "../src/ghost.h"

#define HOST "storage.npln.srv.example.net"
#define SECRET_PATH "/tenants/t-1/users/0123456789abcdef/publicItems/ghost-7"
#define SECRET_QUERY "X-Sig=SECRETSIGNATURE&Expires=99"

static char g_all[32][GHOST_LINE_MAX];
static int g_n;

static void feed(GhostTrace *t, int sent, const char *data, size_t n) {
    char lines[GHOST_LINES][GHOST_LINE_MAX];
    int k = ghost_trace_feed(t, sent, data, n, lines, GHOST_LINES);
    assert(k >= 0 && k <= GHOST_LINES);
    for (int i = 0; i < k; i++) {
        assert(g_n < 32);
        snprintf(g_all[g_n++], GHOST_LINE_MAX, "%s", lines[i]);
        // never the document's path, never the signature
        assert(!strstr(lines[i], "publicItems") && !strstr(lines[i], "SECRET") && !strstr(lines[i], "0123456789abcdef"));
        assert(!strncmp(lines[i], "ghost: ", 7));
    }
}
static void feed_s(GhostTrace *t, int sent, const char *s) { feed(t, sent, s, strlen(s)); }
// the same bytes one at a time: a head may arrive in any split
static void feed_bytes(GhostTrace *t, int sent, const char *s) {
    for (size_t i = 0; s[i]; i++) feed(t, sent, s + i, 1);
}
static int has(const char *text) {
    for (int i = 0; i < g_n; i++)
        if (strstr(g_all[i], text)) return 1;
    return 0;
}

static void test_names(void) {
    assert(ghost_name_match("a.example.net", "a.example.net"));
    assert(ghost_name_match("A.Example.NET", "a.example.net."));
    assert(!ghost_name_match("a.example.net", "b.example.net"));
    assert(ghost_name_match("*.npln.srv.example.net", "storage.npln.srv.example.net"));
    assert(ghost_name_match("*.NPLN.srv.example.net", "Storage.npln.SRV.example.net"));
    assert(!ghost_name_match("*.npln.srv.example.net", "npln.srv.example.net"));        // one label, not none
    assert(!ghost_name_match("*.npln.srv.example.net", "a.b.npln.srv.example.net"));    // ... and not two
    assert(!ghost_name_match("*.npln.srv.example.net", ".npln.srv.example.net"));
    assert(!ghost_name_match("*.net", "example.net"));                                  // too wide
    assert(!ghost_name_match("*", "example"));
    assert(!ghost_name_match("st*.example.net", "storage.example.net"));                // only a whole label
    assert(!ghost_name_match("*.*.example.net", "a.b.example.net"));
    assert(!ghost_name_match("", "a") && !ghost_name_match("a", "") && !ghost_name_match(NULL, "a") &&
           !ghost_name_match("a", NULL));
    assert(!ghost_name_match("example.net", "example.net.evil.org"));
    assert(!ghost_name_match("*.example.net", "evil.org"));
    printf("certificate names: ok\n");
}

static void test_download(void) {
    GhostTrace t;
    ghost_trace_start(&t, HOST);
    g_n = 0;
    feed_s(&t, 1, "GET " SECRET_PATH "?" SECRET_QUERY " HTTP/1.1\r\nHost: " HOST "\r\nAccept: */*\r\n\r\n");
    assert(g_n == 1);
    char want[GHOST_LINE_MAX];
    snprintf(want, sizeof want, "ghost: download request: GET https://" HOST " (path %zu chars, signed query %zu chars)",
             strlen(SECRET_PATH), strlen(SECRET_QUERY));
    assert(!strcmp(g_all[0], want));
    // the answer's head and the first body bytes in one read, the rest later
    feed_s(&t, 0, "HTTP/1.1 200 OK\r\ncontent-length: 10\r\nContent-Type: application/octet-stream\r\n\r\n01234");
    assert(g_n == 2 && !strcmp(g_all[1], "ghost: download answer: HTTP 200, 10 bytes"));
    feed_s(&t, 0, "5678");
    assert(g_n == 2);
    feed_s(&t, 0, "9");
    assert(g_n == 3 && !strcmp(g_all[2], "ghost: download body received: 10 bytes"));
    char line[GHOST_LINE_MAX];
    ghost_trace_end(&t, line, sizeof line);
    assert(strstr(line, "closed: 1 request(s), 1 answer(s)") && !strstr(line, "cut short") && !strstr(line, "no answer"));
    assert(t.requests == 1 && t.responses == 1 && t.received > 10);

    // a second request on the same connection, refused; then one that is cut short
    feed_s(&t, 1, "GET /x?y HTTP/1.1\r\nHost: h\r\n\r\n");
    feed_s(&t, 0, "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n");
    assert(has("ghost: download answer: HTTP 403 (refused), 0 bytes"));
    feed_s(&t, 1, "GET /x?y HTTP/1.1\r\nHost: h\r\n\r\n");
    feed_s(&t, 0, "HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\nabc");
    ghost_trace_end(&t, line, sizeof line);
    assert(strstr(line, "(answer cut short: 97 bytes missing)"));
    printf("download: ok\n");
}

static void test_upload(void) {
    GhostTrace t;
    ghost_trace_start(&t, HOST);
    g_n = 0;
    // the status request of a resumable upload, byte by byte
    feed_bytes(&t, 1, "PUT " SECRET_PATH "?" SECRET_QUERY " HTTP/1.1\r\nHost: " HOST "\r\nContent-Length: 0\r\n"
                      "Content-Range: bytes */300\r\n\r\n");
    assert(g_n == 1 && strstr(g_all[0], "ghost: upload status request: PUT https://" HOST " (path ") &&
           strstr(g_all[0], ", Content-Range bytes */300"));
    feed_bytes(&t, 0, "HTTP/1.1 308 Resume Incomplete\r\nRange: bytes=0-99\r\nContent-Length: 0\r\n\r\n");
    assert(g_n == 2 && !strcmp(g_all[1], "ghost: upload answer: HTTP 308 (part stored, more expected), 0 bytes, Range bytes=0-99"));
    // the rest of the data
    feed_s(&t, 1, "PUT " SECRET_PATH "?" SECRET_QUERY " HTTP/1.1\r\nContent-Range: bytes 100-299/300\r\ncontent-length: 200\r\n\r\n");
    assert(g_n == 3 && strstr(g_all[2], "ghost: upload request: PUT https://" HOST) &&
           strstr(g_all[2], ", 200 bytes, Content-Range bytes 100-299/300"));
    char body[200];
    memset(body, 'g', sizeof body);
    feed(&t, 1, body, 150);
    assert(g_n == 3);
    feed(&t, 1, body, 50);
    assert(g_n == 4 && !strcmp(g_all[3], "ghost: upload body sent: 200 bytes"));
    feed_s(&t, 0, "HTTP/1.1 100 Continue\r\n\r\n");   // not an answer
    assert(g_n == 4 && t.responses == 1);
    feed_s(&t, 0, "HTTP/1.1 200 OK\r\nx-goog-stored-content-length: 300\r\nContent-Length: 2\r\n\r\n{}");
    assert(g_n == 5 && !strcmp(g_all[4], "ghost: upload answer: HTTP 200, 2 bytes, stored 300 bytes"));
    char line[GHOST_LINE_MAX];
    ghost_trace_end(&t, line, sizeof line);
    assert(strstr(line, "closed: 2 request(s), 2 answer(s)") && !strstr(line, "cut short"));

    // an upload whose answer never comes, and one that stops half way
    feed_s(&t, 1, "PUT /p?q HTTP/1.1\r\nContent-Length: 10\r\n\r\n0123");
    ghost_trace_end(&t, line, sizeof line);
    assert(strstr(line, "(upload cut short: 6 bytes not sent)"));
    feed_s(&t, 1, "456789");
    ghost_trace_end(&t, line, sizeof line);
    assert(strstr(line, "(no answer to the last request)"));
    printf("upload: ok\n");
}

static void test_odd(void) {
    GhostTrace t;
    char line[GHOST_LINE_MAX];
    // a chunked answer: two chunks, then the next answer is still understood
    ghost_trace_start(&t, HOST);
    g_n = 0;
    feed_s(&t, 1, "GET /a HTTP/1.1\r\n\r\n");
    assert(strstr(g_all[0], "(path 2 chars, signed query 0 chars)"));
    feed_bytes(&t, 0, "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\nA;x=1\r\n0123456789\r\n0\r\n\r\n");
    assert(has("ghost: download answer: HTTP 200, chunked") && has("ghost: download body received: 15 bytes"));
    feed_s(&t, 1, "HEAD /a HTTP/1.1\r\n\r\n");
    feed_s(&t, 0, "HTTP/1.1 200 OK\r\nContent-Length: 99\r\n\r\n");   // a HEAD's answer has no body
    feed_s(&t, 1, "GET /a HTTP/1.1\r\n\r\n");
    feed_s(&t, 0, "HTTP/1.1 204 No Content\r\n\r\n");
    assert(t.requests == 3 && t.responses == 3 && !t.rx.lost && !t.tx.lost);

    // a header that is not the arithmetic it should be is not logged
    ghost_trace_start(&t, HOST);
    g_n = 0;
    feed_s(&t, 1, "GET /a HTTP/1.1\r\nRange: SECRET-not-a-range\r\n\r\n");
    assert(g_n == 1 && !strstr(g_all[0], "Range"));

    // not HTTP (HTTP/2's preface): said once, then only counted
    ghost_trace_start(&t, HOST);
    g_n = 0;
    feed_s(&t, 1, "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");
    feed(&t, 1, "\0\0\x12\x04\0\0\0\0\0", 9);
    feed(&t, 0, "\0\0\x06\x04\0\0\0\0\0junk\r\n\r\n", 17);
    feed(&t, 0, "more", 4);
    assert(t.rx.lost && has("is not HTTP/1.1"));
    int lost_lines = 0;
    for (int i = 0; i < g_n; i++) lost_lines += strstr(g_all[i], "is not HTTP/1.1") != NULL;
    assert(lost_lines <= 2);   // once per direction at most
    ghost_trace_end(&t, line, sizeof line);
    assert(strstr(line, "received 21 bytes"));

    // a head that never ends does not overflow anything
    ghost_trace_start(&t, HOST);
    g_n = 0;
    char big[GHOST_HEAD_MAX + 500];
    memset(big, 'a', sizeof big);
    memcpy(big, "GET /", 5);
    feed(&t, 1, big, sizeof big);
    assert(t.tx.lost && t.sent == sizeof big && t.requests == 0);

    // no host given, and no room for lines
    ghost_trace_start(&t, NULL);
    assert(!strcmp(t.host, "?"));
    assert(ghost_trace_feed(&t, 1, "GET / HTTP/1.1\r\n\r\n", 18, NULL, 0) == 0 && t.requests == 1);
    assert(ghost_trace_feed(&t, 1, NULL, 5, NULL, 0) == 0 && ghost_trace_feed(&t, 1, "x", 0, NULL, 0) == 0);
    printf("odd inputs: ok\n");
}

int main(void) {
    test_names();
    test_download();
    test_upload();
    test_odd();
    printf("test_ghost: all passed\n");
    return 0;
}
