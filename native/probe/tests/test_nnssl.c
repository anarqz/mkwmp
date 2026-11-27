// The console TLS service the probe answers for the game's HTTP client (nnssl.c on plat_tls_win.c), driven the way
// the game's libcurl drives it: the overrides are caught as the runtime would get them and called with a Cpu whose
// registers hold the arguments (64-bit host pointers pass as guest pointers).
//
// Without arguments: the calls that need no server (ids, bad arguments, a handshake that cannot start, the table's
// limit). With a server (tests/tls_server.py and the certificates of tests/make_test_ca.ps1): a real handshake on a
// non-blocking socket, a ghost download or upload over it, the lines the log gets, and a certificate that must be
// refused.
//
//   $zig = "C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"; $s = "native\probe\src"
//   $out = "$env:TEMP\mkwmp-tests"; mkdir -Force $out
//   & $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o $out\test_nnssl.exe `
//       native\probe\tests\test_nnssl.c $s\nnssl.c $s\ghost.c $s\plat_tls_win.c $s\plat_win.c $s\plat_net_win.c `
//       -lws2_32 -lsecur32 -lcrypt32
//   & $out\test_nnssl.exe                                    # offline part
//
//   pwsh native\probe\tests\make_test_ca.ps1 $out\ca          # ca.pem, other-ca.pem, server.pem, server.key
//   $srv = Start-Process -PassThru -NoNewWindow .venv\Scripts\python.exe `
//       "-I native\probe\tests\tls_server.py 47931 $out\ca\server.pem $out\ca\server.key"
//   & $out\test_nnssl.exe 127.0.0.1 47931 ghost.npln.srv.test.invalid $out\ca\ca.pem get
//   & $out\test_nnssl.exe 127.0.0.1 47931 ghost.npln.srv.test.invalid $out\ca\ca.pem put
//   & $out\test_nnssl.exe 127.0.0.1 47931 ghost.npln.srv.test.invalid $out\ca\other-ca.pem untrusted   # wrong CA
//   & $out\test_nnssl.exe 127.0.0.1 47931 other.host.test.invalid $out\ca\ca.pem untrusted             # wrong name
//   Stop-Process $srv
#undef NDEBUG
#include "../src/plat_net.h"
#include "../src/lobby_ca.h"
#include "../src/nnssl.h"
#include "../src/rtapi.h"

#include <assert.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum { RES_OK = 0, RES_BAD_ARGUMENT = 0xe27b, RES_WOULD_BLOCK = 0x1987b, RES_CERT_REFUSED = 0x19e7b };
enum { GUEST_FD = 5, GHOST_BYTES = 300000 };

// ---- what nnssl.c takes from the rest of the probe ---------------------------------------------------------------
RtApi g_rt;
static struct { const char *name; GuestFn fn; } g_ov[64];
static int g_n_ov;
static char g_log[64][512];
static int g_n_log;
static PlatSock g_sock = PLAT_NO_SOCK;
static unsigned char g_ca[LOBBY_CA_MAX];
static size_t g_ca_len;

static void take_override(const char *name, GuestFn fn) {
    assert(g_n_ov < 64);
    g_ov[g_n_ov].name = name, g_ov[g_n_ov++].fn = fn;
}
static int no_modules(void) { return 0; }
void mkwmp_log(const char *fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    if (g_n_log < 64) vsnprintf(g_log[g_n_log++], sizeof g_log[0], fmt, ap);
    va_end(ap);
    printf("    log: %s\n", g_log[g_n_log - 1]);
}
void crumb_text(const char *line) { (void)line; }
PlatSock npln_guest_socket(int fd) { return fd == GUEST_FD ? g_sock : PLAT_NO_SOCK; }
int npln_curl_socket_overrides(void) { return 0; }
const unsigned char *lobby_ca(size_t *len) { *len = g_ca_len; return g_ca_len ? g_ca : NULL; }

static int logged(const char *text) {
    for (int i = 0; i < g_n_log; i++)
        if (strstr(g_log[i], text)) return 1;
    return 0;
}

// ---- calling an override as the game does ------------------------------------------------------------------------
static uint32_t call(const char *name, uint64_t a0, uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4) {
    for (int i = 0; i < g_n_ov; i++) {
        if (strcmp(g_ov[i].name, name)) continue;
        Cpu c;
        memset(&c, 0, sizeof c);
        c.x[0] = a0, c.x[1] = a1, c.x[2] = a2, c.x[3] = a3, c.x[4] = a4;
        g_ov[i].fn(&c);
        return (uint32_t)c.x[0];
    }
    fprintf(stderr, "not registered: %s\n", name);
    abort();
}
#define P(x) ((uint64_t)(uintptr_t)(x))

static void wait_socket(void) {
    PlatPollFd p = {g_sock, PLAT_POLLIN | PLAT_POLLOUT, 0};
    plat_poll(&p, 1, 50);
    plat_sleep_ms(1);
}

// ---- offline -----------------------------------------------------------------------------------------------------
static void test_offline(void) {
    uint64_t conn[16] = {0}, ctx[16] = {1}, id = 7;
    uint32_t u = 9;
    int32_t n = 9;
    char buf[8];
    // every name the game's HTTP client imports from the TLS service and its backend calls
    static const char *const names[] = {
        "nnsslConnectionCreate", "nnsslConnectionDestroy", "nnsslConnectionGetConnectionId",
        "nnsslConnectionSetSocketDescriptor", "nnsslConnectionSetHostName", "nnsslConnectionSetVerifyOption",
        "nnsslConnectionSetIoMode", "nnsslConnectionSetNextAlpnProto", "nnsslConnectionGetNextAlpnProto",
        "nnsslConnectionSetOption", "nnsslConnectionSetSessionCacheMode", "nnsslConnectionSetServerCertBuffer",
        "nnsslConnectionDoHandshakeWithCertBuffer", "nnsslConnectionGetServerCertDetail",
        "nnsslConnectionGetVerifyCertError", "nnsslConnectionRead", "nnsslConnectionPeek", "nnsslConnectionWrite",
        "nnsslConnectionPending", "nnsslContextDestroy", "nnsslFinalize", "_ZN2nn3nsd9ResolveExEPNS0_4FqdnERKS1_"};
    for (size_t i = 0; i < sizeof names / sizeof *names; i++) {
        int found = 0;
        for (int k = 0; k < g_n_ov; k++) found += !strcmp(g_ov[k].name, names[i]);
        if (found != 1) fprintf(stderr, "%s registered %d times\n", names[i], found);
        assert(found == 1);
    }
    // an object that was never created is no connection
    assert(call("nnsslConnectionGetConnectionId", P(conn), P(&id), 0, 0, 0) == RES_OK && id == 0);
    assert(call("nnsslConnectionRead", P(conn), P(buf), P(&n), 8, 0) == RES_BAD_ARGUMENT);
    assert(call("nnsslConnectionSetSocketDescriptor", P(conn), 3, 0, 0, 0) == RES_BAD_ARGUMENT);
    assert(call("nnsslConnectionDoHandshakeWithCertBuffer", P(conn), P(&u), P(&u), 0, 0) == RES_BAD_ARGUMENT);
    assert(call("nnsslConnectionCreate", 0, P(ctx), 0, 0, 0) == RES_BAD_ARGUMENT);
    assert(call("nnsslConnectionCreate", P(conn), 0, 0, 0, 0) == RES_BAD_ARGUMENT);
    // created: an id, and the settings are taken
    assert(call("nnsslConnectionCreate", P(conn), P(ctx), 0, 0, 0) == RES_OK && conn[0] != 0);
    assert(call("nnsslConnectionGetConnectionId", P(conn), P(&id), 0, 0, 0) == RES_OK && id == conn[0]);
    assert(call("nnsslConnectionSetOption", P(conn), 0, 1, 0, 0) == RES_OK);
    assert(call("nnsslConnectionSetHostName", P(conn), P("a.test.invalid"), 14, 0, 0) == RES_OK);
    assert(call("nnsslConnectionSetHostName", P(conn), 0, 4, 0, 0) == RES_BAD_ARGUMENT);
    assert(call("nnsslConnectionSetVerifyOption", P(conn), 3, 0, 0, 0) == RES_OK);
    assert(call("nnsslConnectionSetSessionCacheMode", P(conn), 0, 0, 0, 0) == RES_OK);
    assert(call("nnsslConnectionSetIoMode", P(conn), 2, 0, 0, 0) == RES_OK);
    assert(call("nnsslConnectionSetNextAlpnProto", P(conn), P("\x08http/1.1"), 9, 0, 0) == RES_OK);
    // before a handshake: no protocol, nothing pending, no data
    uint32_t state = 5, alpn_len = 5;
    unsigned char alpn[50];
    assert(call("nnsslConnectionGetNextAlpnProto", P(conn), P(&state), P(alpn), P(&alpn_len), 50) == RES_OK &&
           state == 0 && alpn_len == 0);
    assert(call("nnsslConnectionPending", P(conn), P(&n), 0, 0, 0) == RES_OK && n == 0);
    n = 9;
    uint32_t r = call("nnsslConnectionRead", P(conn), P(buf), P(&n), 8, 0);
    assert(r != RES_OK && r != RES_WOULD_BLOCK && n == 0);
    // a handshake with no socket behind the descriptor fails, and is not "call again"
    assert(call("nnsslConnectionSetSocketDescriptor", P(conn), 99, 0, 0, 0) == RES_OK);
    r = call("nnsslConnectionDoHandshakeWithCertBuffer", P(conn), P(&u), P(&u), 0, 0);
    assert(r != RES_OK && r != RES_WOULD_BLOCK && r != RES_CERT_REFUSED && logged("cannot start"));
    assert(call("nnsslConnectionDestroy", P(conn), 0, 0, 0, 0) == RES_OK && conn[0] == 0);
    assert(call("nnsslConnectionGetConnectionId", P(conn), P(&id), 0, 0, 0) == RES_OK && id == 0);
    assert(call("nnsslConnectionDestroy", P(conn), 0, 0, 0, 0) == RES_OK);   // twice is harmless
    // the table: 16 at once, the 17th refused, and a freed place is used again with a new id
    uint64_t many[17][2];
    memset(many, 0, sizeof many);
    for (int i = 0; i < 16; i++) assert(call("nnsslConnectionCreate", P(many[i]), P(ctx), 0, 0, 0) == RES_OK);
    for (int i = 0; i < 16; i++)
        for (int k = 0; k < i; k++) assert(many[i][0] != many[k][0]);
    assert(call("nnsslConnectionCreate", P(many[16]), P(ctx), 0, 0, 0) != RES_OK && many[16][0] == 0);
    uint64_t old = many[3][0];
    assert(call("nnsslConnectionDestroy", P(many[3]), 0, 0, 0, 0) == RES_OK);
    assert(call("nnsslConnectionCreate", P(many[16]), P(ctx), 0, 0, 0) == RES_OK && many[16][0] != old);
    many[3][0] = old;   // a stale object does not reach the new connection
    assert(call("nnsslConnectionSetIoMode", P(many[3]), 2, 0, 0, 0) == RES_BAD_ARGUMENT);
    many[3][0] = 0;
    for (int i = 0; i < 17; i++) call("nnsslConnectionDestroy", P(many[i]), 0, 0, 0, 0);
    // the context and the library
    assert(call("nnsslContextDestroy", P(ctx), 0, 0, 0, 0) == RES_OK && ctx[0] == 0);
    assert(call("nnsslContextDestroy", 0, 0, 0, 0, 0) == RES_BAD_ARGUMENT);
    assert(call("nnsslFinalize", 0, 0, 0, 0, 0) == RES_OK);
    // the name service: a link's host comes back as it is, a "%" as the environment
    char fqdn_in[256] = "storage.npln.srv.test.invalid", fqdn_out[256] = "x";
    assert(call("_ZN2nn3nsd9ResolveExEPNS0_4FqdnERKS1_", P(fqdn_out), P(fqdn_in), 0, 0, 0) == 0 && !strcmp(fqdn_out, fqdn_in));
    snprintf(fqdn_in, sizeof fqdn_in, "a-%%.b.test.invalid");
    assert(call("_ZN2nn3nsd9ResolveExEPNS0_4FqdnERKS1_", P(fqdn_in), P(fqdn_in), 0, 0, 0) == 0 && !strcmp(fqdn_in, "a-lp1.b.test.invalid"));
    memset(fqdn_in, '%', 255), fqdn_in[255] = 0;   // never past the 256 bytes
    assert(call("_ZN2nn3nsd9ResolveExEPNS0_4FqdnERKS1_", P(fqdn_out), P(fqdn_in), 0, 0, 0) == 0 && strlen(fqdn_out) == 255);
    assert(call("_ZN2nn3nsd9ResolveExEPNS0_4FqdnERKS1_", 0, P(fqdn_in), 0, 0, 0) != 0);
    printf("offline: ok (%d overrides)\n", g_n_ov);
}

// ---- with a server -----------------------------------------------------------------------------------------------
static void read_ca(const char *path) {
    static unsigned char pem[LOBBY_CA_MAX * 2];
    FILE *f = fopen(path, "rb");
    assert(f);
    size_t n = fread(pem, 1, sizeof pem - 1, f);
    fclose(f);
    // the first certificate's base64, as lobby_ca_decode does (not linked here: lobby_ca is this test's)
    pem[n] = 0;
    const char *b = strstr((char *)pem, "-----BEGIN CERTIFICATE-----"), *e = b ? strstr(b, "-----END") : NULL;
    assert(b && e);
    b += 27;
    unsigned acc = 0, bits = 0;
    for (; b < e; b++) {
        int v = *b >= 'A' && *b <= 'Z' ? *b - 'A' : *b >= 'a' && *b <= 'z' ? *b - 'a' + 26
              : *b >= '0' && *b <= '9' ? *b - '0' + 52 : *b == '+' ? 62 : *b == '/' ? 63 : -1;
        if (v < 0) continue;
        acc = (acc << 6) | (unsigned)v, bits += 6;
        if (bits >= 8) bits -= 8, g_ca[g_ca_len++] = (unsigned char)(acc >> bits);
    }
    assert(g_ca_len > 100 && g_ca[0] == 0x30);
}

static void transfer_all(uint64_t *conn, int writing, char *data, size_t n) {
    size_t done = 0;
    int spins = 0;
    while (done < n) {
        int32_t step = -1;
        uint32_t r = call(writing ? "nnsslConnectionWrite" : "nnsslConnectionRead", P(conn), P(data + done), P(&step),
                          n - done, 0);
        if (r == RES_WOULD_BLOCK) { assert(step == 0 && ++spins < 20000); wait_socket(); continue; }
        assert(r == RES_OK && step > 0);
        done += (size_t)step;
    }
}

static void test_live(const char *ip, int port, const char *host, const char *mode) {
    struct sockaddr_in to = {0};
    to.sin_family = AF_INET;
    to.sin_port = htons((unsigned short)port);
    to.sin_addr.s_addr = inet_addr(ip);
    g_sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    assert(g_sock != PLAT_NO_SOCK && !connect(g_sock, (struct sockaddr *)&to, sizeof to));
    plat_sock_nonblock(g_sock);   // as every socket the probe gives the game

    uint64_t conn[16] = {0}, ctx[16] = {1};
    uint32_t cert_size = 1, cert_count = 1;
    g_n_log = 0;
    assert(call("nnsslConnectionCreate", P(conn), P(ctx), 0, 0, 0) == RES_OK);
    assert(call("nnsslConnectionSetOption", P(conn), 0, 1, 0, 0) == RES_OK);
    assert(call("nnsslConnectionSetSocketDescriptor", P(conn), GUEST_FD, 0, 0, 0) == RES_OK);
    assert(call("nnsslConnectionSetHostName", P(conn), P(host), strlen(host), 0, 0) == RES_OK);
    assert(call("nnsslConnectionSetVerifyOption", P(conn), 3, 0, 0, 0) == RES_OK);
    assert(call("nnsslConnectionSetNextAlpnProto", P(conn), P("\x08http/1.1"), 9, 0, 0) == RES_OK);
    assert(call("nnsslConnectionSetIoMode", P(conn), 2, 0, 0, 0) == RES_OK);
    uint32_t r;
    int calls = 0;
    while ((r = call("nnsslConnectionDoHandshakeWithCertBuffer", P(conn), P(&cert_size), P(&cert_count), 0, 0)) ==
           RES_WOULD_BLOCK) {
        assert(++calls < 2000);
        wait_socket();
    }
    printf("  handshake: result %#x after %d would-block answers\n", r, calls);
    if (!strcmp(mode, "untrusted")) {
        uint32_t detail = 0;
        assert(r == RES_CERT_REFUSED && logged("refused: "));
        assert(call("nnsslConnectionGetVerifyCertError", P(conn), P(&detail), 0, 0, 0) == RES_OK && detail != 0);
        int32_t n = 0;
        char b[4];
        assert(call("nnsslConnectionWrite", P(conn), P("GET"), P(&n), 3, 0) != RES_OK && n == 0);   // nothing is sent
        assert(call("nnsslConnectionRead", P(conn), P(b), P(&n), 4, 0) != RES_OK);
        assert(call("nnsslConnectionDestroy", P(conn), 0, 0, 0, 0) == RES_OK);
        assert(!logged("ready after"));
        printf("untrusted: ok (the certificate was refused)\n");
        return;
    }
    assert(r == RES_OK && cert_size == 0 && cert_count == 0 && logged("ready after"));
    uint32_t state = 0, alpn_len = 0;
    unsigned char alpn[50] = {0};
    assert(call("nnsslConnectionGetNextAlpnProto", P(conn), P(&state), P(alpn), P(&alpn_len), 50) == RES_OK);
    if (!strcmp(mode, "handshake")) {   // any server: the certificate chain and the name only, nothing is sent
        printf("  ALPN state %u, %u bytes\n", state, alpn_len);
        assert(call("nnsslConnectionDestroy", P(conn), 0, 0, 0, 0) == RES_OK);
        plat_sock_close(g_sock);
        printf("handshake: ok\n");
        return;
    }
    assert(state == 1 && alpn_len == 8 && !memcmp(alpn, "http/1.1", 8));
    int32_t n = 5;
    char one;
    assert(call("nnsslConnectionPeek", P(conn), P(&one), P(&n), 1, 0) == RES_WOULD_BLOCK && n == 0);   // idle

    static char body[GHOST_BYTES], head[1024], answer[GHOST_BYTES + 2048];
    for (size_t i = 0; i < sizeof body; i++) body[i] = (char)(i % 251);
    size_t want_body = 0;
    if (!strcmp(mode, "get")) {
        snprintf(head, sizeof head, "GET /ghost/doc-17?sig=SECRETSIG HTTP/1.1\r\nHost: %s\r\nAccept: */*\r\n\r\n", host);
        transfer_all(conn, 1, head, strlen(head));
        want_body = GHOST_BYTES;
    } else {
        snprintf(head, sizeof head, "PUT /ghost/doc-17?sig=SECRETSIG HTTP/1.1\r\nHost: %s\r\nContent-Length: %d\r\n"
                 "Content-Range: bytes 0-%d/%d\r\n\r\n", host, GHOST_BYTES, GHOST_BYTES - 1, GHOST_BYTES);
        transfer_all(conn, 1, head, strlen(head));
        transfer_all(conn, 1, body, sizeof body);
        want_body = 2;   // "{}"
    }
    // the answer: its head, then Content-Length bytes
    size_t got = 0;
    char *end = NULL;
    while (!end) {
        assert(got < sizeof answer - 1);
        transfer_all(conn, 0, answer + got, 1);
        answer[++got] = 0;
        end = strstr(answer, "\r\n\r\n");
    }
    assert(!strncmp(answer, "HTTP/1.1 200", 12));
    size_t head_len = got;
    transfer_all(conn, 0, answer + got, want_body);
    got += want_body;
    if (!strcmp(mode, "get")) {
        assert(!memcmp(answer + head_len, body, GHOST_BYTES));
        assert(logged("ghost: download request: GET https://") && logged("signed query 13 chars"));
        char line[80];
        snprintf(line, sizeof line, "ghost: download answer: HTTP 200, %d bytes", GHOST_BYTES);
        assert(logged(line));
        snprintf(line, sizeof line, "ghost: download body received: %d bytes", GHOST_BYTES);
        assert(logged(line));
    } else {
        assert(!memcmp(answer + head_len, "{}", 2));
        char line[120];
        snprintf(line, sizeof line, ", %d bytes, Content-Range bytes 0-%d/%d", GHOST_BYTES, GHOST_BYTES - 1, GHOST_BYTES);
        assert(logged("ghost: upload request: PUT https://") && logged(line));
        snprintf(line, sizeof line, "ghost: upload body sent: %d bytes", GHOST_BYTES);
        assert(logged(line) && logged("ghost: upload answer: HTTP 200, 2 bytes"));
    }
    assert(call("nnsslConnectionPending", P(conn), P(&n), 0, 0, 0) == RES_OK && n == 0);
    assert(call("nnsslConnectionDestroy", P(conn), 0, 0, 0, 0) == RES_OK && conn[0] == 0);
    assert(logged("closed: 1 request(s), 1 answer(s)"));
    for (int i = 0; i < g_n_log; i++) assert(!strstr(g_log[i], "SECRET") && !strstr(g_log[i], "doc-17"));
    plat_sock_close(g_sock);
    printf("%s: ok (%d bytes of ghost data)\n", mode, GHOST_BYTES);
}

int main(int argc, char **argv) {
    setvbuf(stdout, NULL, _IONBF, 0);   // the log lines of a failed assert too
    plat_net_init();
    g_rt.override = take_override;
    g_rt.guest_modules = no_modules;
    int n = nnssl_register();
    assert(n == g_n_ov && n == 22 && logged("ghost transfers: on"));
    if (argc == 1) {
        test_offline();
        printf("test_nnssl: offline part passed (the live part needs a server: see this file's header)\n");
        return 0;
    }
    if (argc != 6) { fprintf(stderr, "usage: %s [ip port host ca.pem get|put|untrusted|handshake]\n", argv[0]); return 2; }
    read_ca(argv[4]);
    test_live(argv[1], atoi(argv[2]), argv[3], argv[5]);
    printf("test_nnssl: passed\n");
    return 0;
}
