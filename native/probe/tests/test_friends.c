// Friend list: uuid5 / account ids against python's uuid module, the launcher's JSON, and the
// nn::friends::Friend structs GetFriendList hands to the game.
//
// zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o test_friends.exe
//   tests/test_friends.c src/friends.c src/uuid5.c src/launcher.c src/seat.c src/config.c
//   src/notify.c src/log.c -lws2_32
#undef NDEBUG
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "../src/friends.h"
#include "../src/uuid5.h"

static void hex(const uint8_t *b, size_t n, char *out) {
    for (size_t i = 0; i < n; i++) sprintf(out + 2 * i, "%02x", b[i]);
}

static void test_sha1(void) {
    uint8_t d[20];
    char h[41];
    sha1("abc", 3, d); hex(d, 20, h);
    assert(!strcmp(h, "a9993e364706816aba3e25717850c26c9cd0d89d"));
    char a[119];
    memset(a, 'a', sizeof a);
    sha1(a, 56, d); hex(d, 20, h);    // padding spills into a second block
    assert(!strcmp(h, "c2db330f6083854c99d4b5bfb6e8f29f201be699"));
    sha1(a, 119, d); hex(d, 20, h);   // ends one byte short of two full blocks
    assert(!strcmp(h, "ee971065aaa017e0632a8ca6c77bb3bf8b1dfc56"));
}

static void test_account_id(void) {
    // python: uuid.uuid5(uuid.NAMESPACE_URL, "mkwmp-user/alex") -> 977abe88-bab4-5116-be21-cd34e2d8b27d
    uint8_t id[16];
    char h[33];
    uuid5(kUuidNamespaceUrl, "mkwmp-user/alex", 15, id); hex(id, 16, h);
    assert(!strcmp(h, "977abe88bab45116be21cd34e2d8b27d"));
    uuid5(kUuidNamespaceUrl, "https://example.com/", 20, id); hex(id, 16, h);
    assert(!strcmp(h, "dd2c1780811a529681c5178a0ef488bc"));
    // python: int.from_bytes(uuid5(...).bytes[:8], "big") & (2**63 - 1)
    assert(friends_account_id("alex") == 0x177abe88bab45116ull);   // top bit of 0x97... cleared
    assert(friends_account_id("Alex") == friends_account_id("ALEX"));
    assert(friends_account_id("ALEX") == 0x177abe88bab45116ull);
    assert(friends_account_id("Ana") == 0x2f0dcb36eef95e1aull);
    assert(friends_account_id("bia") == 0x1eb7a3b8a2d55e1cull);
}

static void test_parse(void) {
    FriendRoster r;
    assert(friends_parse(200, "{\"friends\":[{\"name\":\"Ana\"},{\"name\":\"Bia\"}]}", "Caio", &r));
    assert(r.count == 2 && !strcmp(r.e[0].name, "Ana") && !strcmp(r.e[1].name, "Bia"));
    assert(r.e[0].account_id == 0x2f0dcb36eef95e1aull && r.e[1].account_id == 0x1eb7a3b8a2d55e1cull);

    // Whitespace, unknown members, nested values and other value types are skipped.
    assert(friends_parse(200,
        " { \"v\" : 2, \"meta\" : {\"friends\":[1,2]}, \"friends\" : [ {\"online\":true,\"tags\":[\"a]\",{}],"
        "\"name\" : \"Ana\"} , 7, {\"name\":5}, {\"name\":\"Bia\"} ] }\r\n", NULL, &r));
    assert(r.count == 2 && !strcmp(r.e[0].name, "Ana") && !strcmp(r.e[1].name, "Bia"));

    // The caller, repeats (names compare without case) and invalid names are left out.
    assert(friends_parse(200,
        "{\"friends\":[{\"name\":\"ANA\"},{\"name\":\"ana\"},{\"name\":\"caio\"},{\"name\":\"\"},"
        "{\"name\":\"abcdefghijklmnopqrstu\"},{\"name\":\"a\\u0041\"},{\"name\":\"abcdefghijklmnopqrst\"}]}",
        "Caio", &r));
    assert(r.count == 2 && !strcmp(r.e[0].name, "ANA") && !strcmp(r.e[1].name, "abcdefghijklmnopqrst"));

    assert(friends_parse(200, "{\"friends\":[]}", "Caio", &r) && r.count == 0);

    // Anything but a complete 200 answer leaves the roster alone.
    r.count = 77;
    assert(!friends_parse(503, "{\"state\":\"unavailable\",\"message\":\"press PLAY\"}", NULL, &r));
    assert(!friends_parse(200, "{\"state\":\"seated\"}", NULL, &r));
    assert(!friends_parse(200, "{\"friends\":[{\"name\":\"Ana\"},{\"name\":\"Bi", NULL, &r));
    assert(!friends_parse(200, "{\"friends\":[{\"name\":\"Ana\"}", NULL, &r));
    assert(!friends_parse(200, "", NULL, &r) && !friends_parse(0, NULL, NULL, &r));
    assert(r.count == 77);

    // No more than the contract's 100.
    static char big[8192];
    int n = sprintf(big, "{\"friends\":[");
    for (int i = 0; i < 130; i++) n += sprintf(big + n, "%s{\"name\":\"player%d\"}", i ? "," : "", i);
    sprintf(big + n, "]}");
    assert(friends_parse(200, big, NULL, &r) && r.count == FRIENDS_MAX && !strcmp(r.e[99].name, "player99"));
}

static void test_page(void) {
    static FriendRoster r;
    static char body[4096];
    int n = sprintf(body, "{\"friends\":[");
    for (int i = 0; i < 23; i++) n += sprintf(body + n, "%s{\"name\":\"P%d\"}", i ? "," : "", i);
    sprintf(body + n, "]}");
    assert(friends_parse(200, body, NULL, &r) && r.count == 23);

    // The game's loop: 10 at a time, advance by a full page, stop at a short one.
    const uint64_t owner[2] = {0x1111, 0x2222};
    static GFriend page[11];
    int total = 0, got;
    for (int offset = 0;; offset += got) {
        memset(page, 0xee, sizeof page);
        got = friends_page(&r, owner, offset, 10, page);
        for (int i = 0; i < got; i++) {
            const uint8_t *raw = (const uint8_t *)&page[i];
            char want[8];
            sprintf(want, "P%d", total + i);
            uint64_t id;
            memcpy(&id, raw + 0x10, 8);                         // Friend::GetAccountId
            assert(id == friends_account_id(want) && id == r.e[total + i].account_id);
            assert(!strcmp((const char *)raw + 0x18, want));    // Friend::GetNickname
            assert(raw[0x128] == 1);                            // Friend::IsValid
            assert(!memcmp(raw, owner, 16) && raw[0x120] == 0 && raw[0x121] == 0);
            static const uint8_t zero[0xe0];
            assert(!memcmp(raw + 0x40, zero, sizeof zero));     // presence: offline
        }
        assert(((const uint8_t *)&page[got])[0] == 0xee);       // nothing past the count
        total += got;
        if (got < 10) break;
    }
    assert(total == 23);
    assert(friends_page(&r, owner, 23, 10, page) == 0 && friends_page(&r, owner, 500, 10, page) == 0);
    assert(friends_page(&r, owner, -1, 10, page) == 0 && friends_page(&r, owner, 0, 0, page) == 0);
    assert(friends_page(&r, owner, 0, -3, page) == 0 && friends_page(&r, owner, 0, 10, NULL) == 0);
    assert(friends_page(&r, NULL, 20, 10, page) == 3 && page[0].owner[0] == 0);

    // A nickname holds 32 bytes; a longer name is cut between characters, not inside one.
    static FriendRoster wide;
    assert(friends_parse(200, "{\"friends\":[{\"name\":\"\xc3\xa1\xc3\xa9\xc3\xad\xc3\xb3\xc3\xba\xc3\xa1\xc3\xa9\xc3\xad"
                              "\xc3\xb3\xc3\xba\xc3\xa1\xc3\xa9\xc3\xad\xc3\xb3\xc3\xbax\xc3\xa1\xc3\xa9\"}]}", NULL, &wide));
    assert(wide.count == 1 && strlen(wide.e[0].name) == 35);
    assert(friends_page(&wide, owner, 0, 1, page) == 1);
    assert(strlen(page[0].nickname) == 31 && !memcmp(page[0].nickname, wide.e[0].name, 31));

    // With no launcher roster fetched, the cached list is empty.
    assert(friends_list(owner, 0, 10, page) == 0);
}

static void test_lobby_ca_decode(void);
int main(void) {
    test_sha1();
    test_lobby_ca_decode();
    test_account_id();
    test_parse();
    test_page();
    puts("friends: sha1, uuid5, account ids, roster parsing and Friend paging passed");
    return 0;
}

// ---- lobby_ca.c: PEM -> DER ---------------------------------------------------
#include <stdlib.h>
#include "../src/lobby_ca.h"
static void test_lobby_ca_decode(void) {
    // "MIIB" = 30 82 01 ..; a tiny fake cert body is enough for the decoder.
    const char pem[] = "junk\n-----BEGIN CERTIFICATE-----\nMIIBAAEC\nAwQ=\n-----END CERTIFICATE-----\n";
    unsigned char der[64];
    size_t n = lobby_ca_decode((const unsigned char *)pem, sizeof pem - 1, der, sizeof der);
    if (n != 8 || der[0] != 0x30 || der[1] != 0x82 || der[7] != 0x04) { printf("lobby_ca: pem decode failed (%u)\n", (unsigned)n); exit(1); }
    unsigned char raw[] = {0x30, 0x82, 0x00, 0x01, 0x05};
    if (lobby_ca_decode(raw, sizeof raw, der, sizeof der) != sizeof raw) { printf("lobby_ca: der passthrough failed\n"); exit(1); }
    if (lobby_ca_decode((const unsigned char *)"hello", 5, der, sizeof der) != 0) { printf("lobby_ca: junk accepted\n"); exit(1); }
    if (lobby_ca_decode((const unsigned char *)pem, sizeof pem - 1, der, 3) != 0) { printf("lobby_ca: overflow not refused\n"); exit(1); }
    printf("lobby_ca: decode passed\n");
}
