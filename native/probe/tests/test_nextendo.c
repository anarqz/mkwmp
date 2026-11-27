// Nextendo Network mode (docs/contracts/NEXTENDO_API.md): mkwmp.toml's backend / nextendo_ca, the session JSON, the
// name rules, the address cache, the account id and token, and the running session against a fake launcher (a
// loopback HTTP server in this process): fetch, token, hosts, the CA, and that the nx2 never reaches mkwmp.log.
//
//   $zig = "C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"; $s = "native\probe\src"
//   & $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o scratch\test_nextendo.exe `
//       native\probe\tests\test_nextendo.c $s\nextendo.c $s\uuid5.c $s\launcher.c $s\config.c $s\lobby_ca.c $s\log.c `
//       $s\plat_win.c $s\plat_net_win.c -lws2_32
//   scratch\test_nextendo.exe
#undef NDEBUG
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "../src/plat_net.h"
#include "../src/config.h"
#include "../src/lobby_ca.h"
#include "../src/log.h"
#include "../src/nextendo.h"

#define NX2 "nx2.eyJwaWQiOjEyM30.c2lnbmF0dXJl-_SECRET"
#define READY(extra)                                                                                              \
    "{\"state\":\"ready\",\"nexToken\":\"" NX2 "\",\"expiresAt\":4102444800000,\"pid\":123,\"username\":\"bia\","  \
    "\"friendCode\":\"SW-1111-2222-3333\",\"serverHost\":\"nex.nextendo.network\","                                \
    "\"natHost\":\"nat.nextendo.network\"" extra "}"

// ---- mkwmp.toml ---------------------------------------------------------------------------------------------------
static void test_config(void) {
    MkwmpConfig c;
    // an MKWMP file, as before: no backend key
    mkwmp_config_parse("server = \"10.0.0.5:9000\"\nname = \"Ana\"\nticket = \"seat-01\"\n", &c);
    assert(c.backend == BACKEND_MKWMP && c.voice_port == 8907 && !strcmp(c.ticket, "seat-01"));
    assert(!strcmp(c.nextendo_ca, "nextendo-ca.pem") && !strcmp(c.lobby_ca, "mkwmp-lobby-ca.pem"));
    mkwmp_config_parse("backend = \"mkwmp\"\nvoice_port = 9000\n", &c);
    assert(c.backend == BACKEND_MKWMP && c.voice_port == 9000);
    mkwmp_config_parse("backend = \"something\"\n", &c);   // unknown: an MKWMP server
    assert(c.backend == BACKEND_MKWMP);
    // the contract's file (section 3), its comments too
    mkwmp_config_parse("backend = \"nextendo\"                # absent or \"mkwmp\" = MKWMP server (unchanged behaviour)\n"
                       "nextendo_ca = \"nextendo-ca.pem\"     # beside mkwmp.toml: ca_pem from network-config, PEM\n", &c);
    assert(c.backend == BACKEND_NEXTENDO && !strcmp(c.nextendo_ca, "nextendo-ca.pem"));
    mkwmp_config_parse("backend = \"mkwmp\"   # \"nextendo\"\n", &c);
    assert(c.backend == BACKEND_MKWMP);
    mkwmp_config_parse("backend = nextendo\r\n", &c);   // unquoted, CRLF
    assert(c.backend == BACKEND_MKWMP);                 // (as every key: a CR stays in an unquoted value)
    mkwmp_config_parse("backend = \"nextendo\"\r\nname = \"bia\"\r\n", &c);
    assert(c.backend == BACKEND_NEXTENDO);
    mkwmp_config_parse("backend = \"nextendo\"\nname = \"bia\"\nlauncher = \"127.0.0.1:47820\"\n"
                       "launcher_token = \"ab12\"\nnextendo_ca = \"nx-ca.pem\"\nlauncher_exe = \"C:/x/mkwmp.exe\"\n", &c);
    assert(c.present && c.backend == BACKEND_NEXTENDO && !strcmp(c.name, "bia") && c.launcher_port == 47820);
    assert(!strcmp(c.nextendo_ca, "nx-ca.pem") && !strcmp(c.launcher_exe, "C:/x/mkwmp.exe"));
    assert(c.voice_port == 0 && !c.ticket[0]);   // no voice relay, no seat ticket
    // whatever the file says of the MKWMP-only keys, in any order
    mkwmp_config_parse("voice_port = 8907\nticket = \"mkw1.a.b\"\nbackend = \"nextendo\"\n", &c);
    assert(c.backend == BACKEND_NEXTENDO && c.voice_port == 0 && !c.ticket[0]);
    mkwmp_config_parse("backend = \"nextendo\"\nvoice_port = 8907\n", &c);
    assert(c.voice_port == 0 && !strcmp(c.nextendo_ca, "nextendo-ca.pem"));
}

// ---- GET /local/v1/nextendo/session -------------------------------------------------------------------------------
static void test_session(void) {
    static NxSession s;
    nextendo_session_parse(200, READY(",\"caFile\":\"nextendo-ca.pem\""), &s);
    assert(s.state == NX_READY && !strcmp(s.token, NX2) && s.expires_at == 4102444800000ull && s.pid == 123);
    assert(!strcmp(s.username, "bia") && !strcmp(s.friend_code, "SW-1111-2222-3333"));
    assert(!strcmp(s.server_host, "nex.nextendo.network") && !strcmp(s.nat_host, "nat.nextendo.network"));
    assert(!strcmp(s.ca_file, "nextendo-ca.pem"));
    nextendo_session_parse(200, READY(""), &s);   // caFile is optional
    assert(s.state == NX_READY && !s.ca_file[0]);
    nextendo_session_parse(200, "{ \"state\" : \"ready\" , \"nexToken\" : \"nx2.a\" , \"pid\" : 7 , \"username\" : \"z\" ,"
                                " \"serverHost\" : \"1.2.3.4\" , \"natHost\" : \"n.example\" }", &s);
    assert(s.state == NX_READY && s.pid == 7 && !s.expires_at && !strcmp(s.server_host, "1.2.3.4"));

    nextendo_session_parse(409, "{\"state\":\"refused\",\"reason\":\"elsewhere\",\"message\":\"Already online\"}", &s);
    assert(s.state == NX_REFUSED && !strcmp(s.reason, "elsewhere") && !strcmp(s.message, "Already online") && !s.token[0]);
    nextendo_session_parse(409, NULL, &s);
    assert(s.state == NX_REFUSED);
    nextendo_session_parse(503, "{\"state\":\"unavailable\",\"message\":\"down\"}", &s);
    assert(s.state == NX_UNAVAILABLE && !strcmp(s.message, "down"));
    nextendo_session_parse(401, "{\"state\":\"unauthorized\"}", &s);
    assert(s.state == NX_UNAUTHORIZED);
    nextendo_session_parse(0, NULL, &s);
    assert(s.state == NX_UNAVAILABLE);
    nextendo_session_parse(200, "<html>", &s);
    assert(s.state == NX_UNAVAILABLE);
    nextendo_session_parse(409, READY(""), &s);   // a body that says ready under a refusal is a refusal
    assert(s.state == NX_REFUSED && !s.token[0]);

    // a 200 that is not whole is not used, and keeps nothing of the token
    static const char *const kBroken[] = {
        "{\"state\":\"seated\",\"nexToken\":\"nx2.a\",\"pid\":1,\"username\":\"u\",\"serverHost\":\"a.b\",\"natHost\":\"c.d\"}",
        "{\"state\":\"ready\",\"pid\":1,\"username\":\"u\",\"serverHost\":\"a.b\",\"natHost\":\"c.d\"}",
        "{\"state\":\"ready\",\"nexToken\":\"\",\"pid\":1,\"username\":\"u\",\"serverHost\":\"a.b\",\"natHost\":\"c.d\"}",
        "{\"state\":\"ready\",\"nexToken\":\"nx2 a\",\"pid\":1,\"username\":\"u\",\"serverHost\":\"a.b\",\"natHost\":\"c.d\"}",
        "{\"state\":\"ready\",\"nexToken\":\"nx2\\\"a\",\"pid\":1,\"username\":\"u\",\"serverHost\":\"a.b\",\"natHost\":\"c.d\"}",
        "{\"state\":\"ready\",\"nexToken\":\"nx2.a\",\"pid\":0,\"username\":\"u\",\"serverHost\":\"a.b\",\"natHost\":\"c.d\"}",
        "{\"state\":\"ready\",\"nexToken\":\"nx2.a\",\"username\":\"u\",\"serverHost\":\"a.b\",\"natHost\":\"c.d\"}",
        "{\"state\":\"ready\",\"nexToken\":\"nx2.a\",\"pid\":1,\"username\":\"\",\"serverHost\":\"a.b\",\"natHost\":\"c.d\"}",
        "{\"state\":\"ready\",\"nexToken\":\"nx2.a\",\"pid\":1,\"username\":\"u\",\"natHost\":\"c.d\"}",
        "{\"state\":\"ready\",\"nexToken\":\"nx2.a\",\"pid\":1,\"username\":\"u\",\"serverHost\":\"a b\",\"natHost\":\"c.d\"}",
        "{\"state\":\"ready\",\"nexToken\":\"nx2.a\",\"pid\":1,\"username\":\"u\",\"serverHost\":\"a.b/x\",\"natHost\":\"c.d\"}",
        "{\"state\":\"ready\",\"nexToken\":\"nx2.a\",\"pid\":1,\"username\":\"u\",\"serverHost\":\"a.b\"}",
        "{\"state\":\"ready\",\"nexToken\":\"nx2.a\",\"pid\":1,\"username\":\"u\",\"serverHost\":\"a.b\",\"natHost\":\"c.d",   // cut
    };
    for (size_t i = 0; i < sizeof kBroken / sizeof kBroken[0]; i++) {
        nextendo_session_parse(200, kBroken[i], &s);
        if (s.state != NX_UNAVAILABLE || s.token[0]) printf("accepted: %s\n", kBroken[i]);
        assert(s.state == NX_UNAVAILABLE && !s.token[0]);
    }
    // a token longer than the buffer is refused, not cut
    static char big[NX_TOKEN_MAX + 400];
    int n = snprintf(big, sizeof big, "{\"state\":\"ready\",\"nexToken\":\"");
    memset(big + n, 'a', NX_TOKEN_MAX);
    snprintf(big + n + NX_TOKEN_MAX, sizeof big - (size_t)n - NX_TOKEN_MAX,
             "\",\"pid\":1,\"username\":\"u\",\"serverHost\":\"a.b\",\"natHost\":\"c.d\"}");
    nextendo_session_parse(200, big, &s);
    assert(s.state == NX_UNAVAILABLE && !s.token[0]);
    big[n + NX_TOKEN_MAX - 1] = '"';   // one shorter: fits
    memmove(big + n + NX_TOKEN_MAX, big + n + NX_TOKEN_MAX + 1, strlen(big + n + NX_TOKEN_MAX + 1) + 1);
    nextendo_session_parse(200, big, &s);
    assert(s.state == NX_READY && strlen(s.token) == NX_TOKEN_MAX - 1);
    assert(!strcmp(nextendo_state_name(NX_READY), "ready") && !strcmp(nextendo_state_name(NX_REFUSED), "refused"));
}

// ---- name rules ---------------------------------------------------------------------------------------------------
static void test_classify(void) {
    uint8_t ip[4] = {9, 9, 9, 9};
    // the NAT check
    assert(nextendo_classify("nncs2-lp1.n.n.srv.nintendo.net", ip) == NX_HOST_NAT);
    assert(nextendo_classify("NNCS2-LP1.N.N.SRV.NINTENDO.NET.", NULL) == NX_HOST_NAT);
    assert(nextendo_classify("nncs1-lp1.n.n.srv.nintendo.net", NULL) == NX_HOST_SERVER);   // the first check server
    assert(nextendo_classify("nncs2-lp1.other.nintendo.net", NULL) == NX_HOST_SERVER);
    assert(nextendo_classify("nncs2-lp1.n.n.srv.nintendo.com", NULL) == NX_HOST_SERVER);
    // the server: every other name of the three domains
    assert(nextendo_classify("t-e96531c6-lp1.lp1.t.npln.srv.nintendo.net", NULL) == NX_HOST_SERVER);
    assert(nextendo_classify("ghosts.npln.srv.nintendo.net", NULL) == NX_HOST_SERVER);
    assert(nextendo_classify("gamesync.npln.nintendo.net", NULL) == NX_HOST_SERVER);
    assert(nextendo_classify("e0d67c509fb203858ebcb2fe3f88c2aa.baas.nintendo.com", NULL) == NX_HOST_SERVER);
    assert(nextendo_classify("www.nintendo.co.jp", NULL) == NX_HOST_SERVER);
    assert(nextendo_classify("nintendo.net", NULL) == NX_HOST_SERVER && nextendo_classify("Nintendo.Com.", NULL) == NX_HOST_SERVER);
    // anything else: the machine's DNS (a gamesync host of Nextendo's own, say)
    assert(nextendo_classify("gs1.nextendo.network", NULL) == NX_HOST_OTHER);
    assert(nextendo_classify("nex.nextendo.network", NULL) == NX_HOST_OTHER);
    assert(nextendo_classify("notnintendo.net", NULL) == NX_HOST_OTHER);
    assert(nextendo_classify("nintendo.net.example.org", NULL) == NX_HOST_OTHER);
    assert(nextendo_classify("nintendo.co.uk", NULL) == NX_HOST_OTHER && nextendo_classify("x.nintendowifi.net", NULL) == NX_HOST_OTHER);
    assert(nextendo_classify("localhost", NULL) == NX_HOST_OTHER && nextendo_classify("", NULL) == NX_HOST_OTHER);
    assert(nextendo_classify(NULL, NULL) == NX_HOST_OTHER);
    assert(!memcmp(ip, "\x09\x09\x09\x09", 4));   // untouched so far
    // an address is itself
    assert(nextendo_classify("51.178.29.194", ip) == NX_HOST_LITERAL && !memcmp(ip, "\x33\xb2\x1d\xc2", 4));
    assert(nextendo_classify("127.0.0.1", ip) == NX_HOST_LITERAL && !memcmp(ip, "\x7f\0\0\1", 4));
    assert(nextendo_classify("255.255.255.255", NULL) == NX_HOST_LITERAL);
    assert(nextendo_classify("256.1.1.1", NULL) == NX_HOST_OTHER && nextendo_classify("1.2.3", NULL) == NX_HOST_OTHER);
    assert(nextendo_classify("1.2.3.4.5", NULL) == NX_HOST_OTHER && nextendo_classify("1.2.3.", NULL) == NX_HOST_OTHER);
    assert(nextendo_classify("0x7f.0.0.1", NULL) == NX_HOST_OTHER && nextendo_classify("1.2.3.0004", NULL) == NX_HOST_OTHER);
}

// ---- the address cache --------------------------------------------------------------------------------------------
static int g_dns_calls, g_dns_fail;
static uint8_t g_dns_ip[4];
static int fake_dns(const char *host, uint8_t ip[4]) {
    (void)host;
    g_dns_calls++;
    if (g_dns_fail) return 0;
    memcpy(ip, g_dns_ip, 4);
    return 1;
}

static void test_cache(void) {
    NxHostCache c = {0};
    uint8_t ip[4] = {0};
    uint64_t t = 5000000;
    memcpy(g_dns_ip, "\x0a\0\0\1", 4);
    assert(!nextendo_cache_get(&c, "", t, fake_dns, ip) && !nextendo_cache_get(&c, NULL, t, fake_dns, ip));
    assert(nextendo_cache_get(&c, "nex.example", t, fake_dns, ip) && g_dns_calls == 1 && !memcmp(ip, "\x0a\0\0\1", 4));
    // kept for 10 minutes without asking
    memcpy(g_dns_ip, "\x0a\0\0\2", 4);
    assert(nextendo_cache_get(&c, "nex.example", t + NX_DNS_TTL_MS - 1, fake_dns, ip) && g_dns_calls == 1 && ip[3] == 1);
    // then asked again
    assert(nextendo_cache_get(&c, "nex.example", t + NX_DNS_TTL_MS, fake_dns, ip) && g_dns_calls == 2 && ip[3] == 2);
    t += NX_DNS_TTL_MS;
    // a failed lookup keeps the last good address, and is not repeated at once
    g_dns_fail = 1;
    assert(nextendo_cache_get(&c, "nex.example", t + NX_DNS_TTL_MS, fake_dns, ip) && g_dns_calls == 3 && ip[3] == 2);
    assert(nextendo_cache_get(&c, "nex.example", t + NX_DNS_TTL_MS + 1000, fake_dns, ip) && g_dns_calls == 3);
    assert(nextendo_cache_get(&c, "nex.example", t + NX_DNS_TTL_MS + NX_DNS_RETRY_MS, fake_dns, ip) && g_dns_calls == 4);
    g_dns_fail = 0;
    memcpy(g_dns_ip, "\x0a\0\0\3", 4);
    t += NX_DNS_TTL_MS + 2 * NX_DNS_RETRY_MS;
    assert(nextendo_cache_get(&c, "nex.example", t, fake_dns, ip) && g_dns_calls == 5 && ip[3] == 3);
    assert(nextendo_cache_get(&c, "nex.example", t + 60000, fake_dns, ip) && g_dns_calls == 5);   // fresh again
    // another host does not inherit the address
    g_dns_fail = 1;
    memset(ip, 0xee, 4);
    assert(!nextendo_cache_get(&c, "other.example", t, fake_dns, ip) && g_dns_calls == 6 && ip[0] == 0xee);
    assert(!nextendo_cache_get(&c, "other.example", t + 1000, fake_dns, ip) && g_dns_calls == 6);   // 5 s between tries
    g_dns_fail = 0;
    assert(nextendo_cache_get(&c, "other.example", t + NX_DNS_FIRST_RETRY_MS, fake_dns, ip) && g_dns_calls == 7 && ip[3] == 3);
}

// ---- the account id and the token ---------------------------------------------------------------------------------
static int unb64(char ch) {
    if (ch >= 'A' && ch <= 'Z') return ch - 'A';
    if (ch >= 'a' && ch <= 'z') return ch - 'a' + 26;
    if (ch >= '0' && ch <= '9') return ch - '0' + 52;
    return ch == '-' ? 62 : ch == '_' ? 63 : -1;
}
// base64url without padding -> bytes; -1 on a character outside the alphabet
static int b64url_decode(const char *in, size_t n, uint8_t *out, size_t cap) {
    size_t k = 0;
    unsigned acc = 0;
    int bits = 0;
    for (size_t i = 0; i < n; i++) {
        int v = unb64(in[i]);
        if (v < 0) return -1;
        acc = acc << 6 | (unsigned)v;
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            if (k >= cap) return -1;
            out[k++] = (uint8_t)(acc >> bits);
        }
    }
    return (int)k;
}

// The three parts of a token, decoded. 1 when it is header.claims.signature and nothing else.
static int split_jwt(const char *jwt, char *header, char *claims, size_t cap, uint8_t sig[64], int *sig_len) {
    const char *d1 = strchr(jwt, '.'), *d2 = d1 ? strchr(d1 + 1, '.') : NULL;
    if (!d2 || strchr(d2 + 1, '.') || strchr(jwt, '=')) return 0;
    int h = b64url_decode(jwt, (size_t)(d1 - jwt), (uint8_t *)header, cap - 1);
    int c = b64url_decode(d1 + 1, (size_t)(d2 - d1 - 1), (uint8_t *)claims, cap - 1);
    *sig_len = b64url_decode(d2 + 1, strlen(d2 + 1), sig, 64);
    if (h < 0 || c < 0) return 0;
    header[h] = 0;
    claims[c] = 0;
    return 1;
}

static void test_account(void) {
    assert(nextendo_account_id(123) == 0x4e5800000000007bull);
    assert(nextendo_account_id(123) == nextendo_account_id(123) && nextendo_account_id(123) != nextendo_account_id(124));
    assert(nextendo_account_id(0) != 0 && nextendo_account_id(0xffffffffull) == 0x4e58000000000000ull + 0xffffffffull);
    assert(!(nextendo_account_id(0xffffffffffffffffull) >> 63));   // positive, as the ids of names are
    for (uint64_t a = 1; a < 2000; a++) assert(nextendo_account_id(a) != nextendo_account_id(a + 1) && nextendo_account_id(a));

    NxJwt in = {.nex_token = NX2, .pid = 123, .iat = 1795000000};
    for (int i = 0; i < 16; i++) in.jti[i] = (uint8_t)(0xf0 + i);
    for (int i = 0; i < 64; i++) in.sig[i] = (uint8_t)(i * 3 + 250);
    static char jwt[8192], header[256], claims[4096];
    uint8_t sig[64];
    int sig_len;
    size_t len = nextendo_jwt(&in, jwt, sizeof jwt);
    assert(len && len == strlen(jwt));
    // the header's encoding is fixed: {"alg":"RS256","typ":"JWT"}
    assert(!strncmp(jwt, "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.", 37));
    assert(split_jwt(jwt, header, claims, sizeof claims, sig, &sig_len));
    assert(!strcmp(header, "{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
    assert(sig_len == 64 && !memcmp(sig, in.sig, 64));
    assert(strlen(strrchr(jwt, '.') + 1) == 86);   // 64 bytes, no padding
    assert(claims[0] == '{' && claims[strlen(claims) - 1] == '}');
    assert(strstr(claims, "\"iss\":\"https://e0d67c509fb203858ebcb2fe3f88c2aa.baas.nintendo.com\""));
    assert(strstr(claims, "\"aud\":\"ed9e2f05d286f7b8\"") && strstr(claims, "\"typ\":\"id_token\""));
    assert(strstr(claims, "\"sn\":\"XAW10000000000\"") && strstr(claims, "\"hm\":true") && strstr(claims, "\"tv\":\"1.8.0\""));
    assert(strstr(claims, "\"iat\":1795000000,") && strstr(claims, "\"exp\":1795010800,"));   // 3 h
    assert(strstr(claims, "\"jti\":\"f0f1f2f3-f4f5-46f7-b8f9-fafbfcfdfeff\""));               // version 4, variant 10
    assert(strstr(claims, "\"nnex\":\"" NX2 "\"}"));
    const char *sub = strstr(claims, "\"sub\":\""), *di = strstr(claims, "\"di\":\""), *did = strstr(claims, "\"bs:did\":\"");
    assert(sub && di && did && sub[7 + 16] == '"' && di[6 + 16] == '"' && !memcmp(di + 6, did + 10, 16));
    assert(memcmp(sub + 7, di + 6, 16));
    for (int i = 0; i < 16; i++) assert(strchr("0123456789abcdef", sub[7 + i]) && strchr("0123456789abcdef", di[6 + i]));

    // sub and di belong to the pid; jti and the signature to the token
    static char again[8192], other[8192], claims2[4096];
    in.jti[0] ^= 1;
    assert(nextendo_jwt(&in, again, sizeof again) && strcmp(again, jwt));
    assert(split_jwt(again, header, claims2, sizeof claims2, sig, &sig_len));
    assert(!memcmp(strstr(claims2, "\"sub\":\""), sub, 7 + 16) && !memcmp(strstr(claims2, "\"di\":\""), di, 6 + 16));
    in.pid = 124;
    assert(nextendo_jwt(&in, other, sizeof other) && split_jwt(other, header, claims2, sizeof claims2, sig, &sig_len));
    assert(memcmp(strstr(claims2, "\"sub\":\""), sub, 7 + 16) && memcmp(strstr(claims2, "\"di\":\""), di, 6 + 16));

    // a buffer one byte short gets nothing; exactly enough gets it
    static char tight[8192];
    assert(nextendo_jwt(&in, tight, len) == 0);
    assert(nextendo_jwt(&in, tight, len + 1) == len);
    // a token that would break out of its JSON string is refused
    in.nex_token = "nx2\",\"hm\":false,\"x\":\"";
    assert(nextendo_jwt(&in, tight, sizeof tight) == 0);
    in.nex_token = "";
    assert(nextendo_jwt(&in, tight, sizeof tight) == 0);
    in.nex_token = NULL;
    assert(nextendo_jwt(&in, tight, sizeof tight) == 0);
}

// ---- the running session, against a fake launcher -----------------------------------------------------------------
static SOCKET g_listener;
static volatile int g_mode, g_requests, g_bad_auth, g_bad_route;   // mode: an HTTP status
static const char *volatile g_ready_body = READY(",\"caFile\":\"other-ca.pem\"");

static DWORD WINAPI fake_launcher(void *unused) {
    (void)unused;
    for (;;) {
        SOCKET s = accept(g_listener, NULL, NULL);
        if (s == INVALID_SOCKET) return 0;
        char req[2048];
        int got = 0, r;
        while (got < (int)sizeof req - 1 && (r = recv(s, req + got, (int)sizeof req - 1 - got, 0)) > 0) {
            got += r;
            req[got] = 0;
            if (strstr(req, "\r\n\r\n")) break;
        }
        req[got] = 0;
        g_requests++;
        if (strncmp(req, "GET /local/v1/nextendo/session HTTP/1.1\r\n", 41)) g_bad_route++;
        if (!strstr(req, "\r\nAuthorization: Bearer tok123\r\n")) g_bad_auth++;
        int mode = g_mode;
        const char *body = mode == 200 ? g_ready_body
                         : mode == 409 ? "{\"state\":\"refused\",\"reason\":\"elsewhere\",\"message\":\"Already online\"}"
                         : mode == 503 ? "{\"state\":\"unavailable\",\"message\":\"down\"}" : "{\"state\":\"unauthorized\"}";
        char head[160];
        int n = snprintf(head, sizeof head, "HTTP/1.1 %d X\r\nContent-Type: application/json\r\nContent-Length: %u\r\n"
                                            "Connection: close\r\n\r\n", mode, (unsigned)strlen(body));
        send(s, head, n, 0);
        send(s, body, (int)strlen(body), 0);
        closesocket(s);
    }
}

static void write_file(const char *dir, const char *name, const char *text) {
    char path[600];
    snprintf(path, sizeof path, "%s\\%s", dir, name);
    FILE *f = fopen(path, "wb");
    assert(f);
    fputs(text, f);
    fclose(f);
}

static void test_running(void) {
    char tmp[300], dir[400];
    assert(GetTempPathA(sizeof tmp, tmp));
    snprintf(dir, sizeof dir, "%smkwmp-nextendo-test-%lu", tmp, (unsigned long)GetCurrentProcessId());
    assert(CreateDirectoryA(dir, NULL) || GetLastError() == ERROR_ALREADY_EXISTS);
    plat_set_home(dir);
    log_init();

    WSADATA wsa;
    assert(!WSAStartup(MAKEWORD(2, 2), &wsa));
    g_listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    struct sockaddr_in a = {0};
    a.sin_family = AF_INET;
    a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    int al = sizeof a;
    assert(!bind(g_listener, (struct sockaddr *)&a, sizeof a) && !getsockname(g_listener, (struct sockaddr *)&a, &al));
    assert(!listen(g_listener, 8));
    assert(CreateThread(NULL, 0, fake_launcher, NULL, 0, NULL));

    char toml[400];
    snprintf(toml, sizeof toml, "backend = \"nextendo\"\nname = \"bia\"\nlauncher = \"127.0.0.1:%u\"\n"
                                "launcher_token = \"tok123\"\nnextendo_ca = \"nextendo-ca.pem\"\n", (unsigned)ntohs(a.sin_port));
    write_file(dir, "mkwmp.toml", toml);
    const MkwmpConfig *cfg = mkwmp_config();
    assert(cfg->present && cfg->backend == BACKEND_NEXTENDO && mkwmp_config_nextendo() && cfg->voice_port == 0);

    // the CA: Nextendo's file or nothing, never the built-in capture certificate
    size_t ca_len = 99;
    assert(!lobby_ca(&ca_len) && ca_len == 0);
    static const char kPem[] = "-----BEGIN CERTIFICATE-----\nMIIBAAEC\nAwQ=\n-----END CERTIFICATE-----\n";
    write_file(dir, "nextendo-ca.pem", kPem);
    const unsigned char *ca = lobby_ca(&ca_len);   // asked again: the launcher wrote it meanwhile
    assert(ca && ca_len == 8 && ca[0] == 0x30 && ca[7] == 0x04);
    lobby_ca_use_file("other-ca.pem");             // the session's caFile, not there
    assert(!lobby_ca(&ca_len) && ca_len == 0);
    lobby_ca_use_file("..\\nextendo-ca.pem");      // not a plain name
    assert(!lobby_ca(&ca_len));
    lobby_ca_use_file("nextendo-ca.pem");
    assert(lobby_ca(&ca_len) && ca_len == 8);

    // nothing before the first session
    static char jwt[8192], name[64], server[128], nat[128], file[128];
    uint8_t ip[4];
    assert(!nextendo_have() && !nextendo_pid() && !nextendo_username(name, sizeof name));
    assert(!nextendo_token(jwt, sizeof jwt) && !nextendo_host(NX_HOST_SERVER, ip));
    assert(!nextendo_resolve("t-e96531c6-lp1.lp1.t.npln.srv.nintendo.net", ip));   // not to DNS, not anywhere
    assert(nextendo_resolve("203.0.113.9", ip) && !memcmp(ip, "\xcb\x00\x71\x09", 4));

    // refused, unavailable, unauthorized: no session
    g_mode = 409;
    assert(nextendo_fetch() == NX_REFUSED && !nextendo_have() && g_requests == 1);
    g_mode = 503;
    assert(nextendo_fetch() == NX_UNAVAILABLE && !nextendo_have() && g_requests == 2);
    g_mode = 401;   // launcher.c reads mkwmp.toml again and asks once more
    assert(nextendo_fetch() == NX_UNAUTHORIZED && !nextendo_have() && g_requests == 4);

    // ready
    g_mode = 200;
    assert(nextendo_fetch() == NX_READY && nextendo_have() && g_requests == 5 && !g_bad_auth && !g_bad_route);
    assert(nextendo_pid() == 123 && nextendo_username(name, sizeof name) && !strcmp(name, "bia"));
    assert(nextendo_hosts(server, nat, file, sizeof server) && !strcmp(server, "nex.nextendo.network"));
    assert(!strcmp(nat, "nat.nextendo.network") && !strcmp(file, "other-ca.pem"));
    size_t len = nextendo_token(jwt, sizeof jwt);
    static char header[256], claims[4096], jwt2[8192];
    uint8_t sig[64];
    int sig_len;
    assert(len && split_jwt(jwt, header, claims, sizeof claims, sig, &sig_len) && sig_len == 64);
    assert(strstr(claims, "\"nnex\":\"" NX2 "\"}") && strstr(claims, "\"tv\":\"1.8.0\""));
    const char *iat = strstr(claims, "\"iat\":");
    assert(iat && strtoll(iat + 6, NULL, 10) > 1790000000);
    assert(nextendo_token(jwt2, sizeof jwt2) == len && strcmp(jwt, jwt2));   // a new jti and signature each time
    assert(!nextendo_token(jwt2, 100));

    // a hiccup keeps the session in hand; a refusal drops it
    g_mode = 503;
    assert(nextendo_fetch() == NX_UNAVAILABLE && nextendo_have() && nextendo_token(jwt2, sizeof jwt2) == len);
    g_mode = 409;
    assert(nextendo_fetch() == NX_REFUSED && !nextendo_have() && !nextendo_token(jwt2, sizeof jwt2) && !nextendo_pid());

    // hosts given as addresses need no DNS: the names of the console maker's domains go to them
    g_ready_body = "{\"state\":\"ready\",\"nexToken\":\"" NX2 "\",\"expiresAt\":4102444800000,\"pid\":123,"
                   "\"username\":\"bia\",\"serverHost\":\"198.51.100.7\",\"natHost\":\"198.51.100.8\"}";
    g_mode = 200;
    assert(nextendo_fetch() == NX_READY);
    assert(nextendo_host(NX_HOST_SERVER, ip) && !memcmp(ip, "\xc6\x33\x64\x07", 4));
    assert(nextendo_host(NX_HOST_NAT, ip) && !memcmp(ip, "\xc6\x33\x64\x08", 4));
    assert(nextendo_resolve("t-e96531c6-lp1.lp1.t.npln.srv.nintendo.net", ip) && ip[3] == 7);
    assert(nextendo_resolve("nncs1-lp1.n.n.srv.nintendo.net", ip) && ip[3] == 7);
    assert(nextendo_resolve("nncs2-lp1.n.n.srv.nintendo.net", ip) && ip[3] == 8);
    assert(nextendo_resolve("192.0.2.44", ip) && !memcmp(ip, "\xc0\x00\x02\x2c", 4));   // gamesync by address
    assert(nextendo_resolve("localhost", ip) && ip[0] == 127);                           // any other name: real DNS
    assert(!nextendo_resolve("", ip) && !nextendo_resolve(NULL, ip));
    // a session that has already expired is not one
    g_ready_body = "{\"state\":\"ready\",\"nexToken\":\"" NX2 "\",\"expiresAt\":1000,\"pid\":123,"
                   "\"username\":\"bia\",\"serverHost\":\"198.51.100.7\",\"natHost\":\"198.51.100.8\"}";
    assert(nextendo_fetch() == NX_UNAVAILABLE && nextendo_have());   // (the good one in hand stays)

    // mkwmp.log: the session is named, the token and the account are not
    char path[600];
    snprintf(path, sizeof path, "%s\\mkwmp.log", dir);
    FILE *f = fopen(path, "rb");
    assert(f);
    static char text[1 << 16];
    text[fread(text, 1, sizeof text - 1, f)] = 0;
    fclose(f);
    assert(strstr(text, "nextendo: session ready (token 40 bytes") && strstr(text, "server nex.nextendo.network"));
    assert(strstr(text, "nextendo: no session: refused (HTTP 409, elsewhere: Already online)"));
    assert(strstr(text, "the one in hand stays"));
    assert(!strstr(text, "nx2") && !strstr(text, "SECRET") && !strstr(text, "eyJ"));
    assert(!strstr(text, "bia") && !strstr(text, "123,") && !strstr(text, "SW-"));
    closesocket(g_listener);
    snprintf(path, sizeof path, "%s\\mkwmp.toml", dir);
    remove(path);
    snprintf(path, sizeof path, "%s\\nextendo-ca.pem", dir);
    remove(path);   // (mkwmp.log is still open: it stays in the temp folder)
    printf("  (session log, %u bytes)\n%s", (unsigned)strlen(text), text);
}

int main(void) {
    assert(strlen(NX2) == 40);
    test_config();
    test_session();
    test_classify();
    test_cache();
    test_account();
    test_running();
    puts("nextendo: config, session JSON, name rules, address cache, account id, token and the running session passed");
    return 0;
}
