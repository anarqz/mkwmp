// Previous races (roster.h, rosterhook.h): the member tree read from a fake memory image (order, bounds, the st
// filter, the local flag, the string capacities), the change hash, the match key, the JSON body; then the hooks
// against a fake game (a fake runtime: g_rt.call runs C stand-ins of the game's functions, g_rt.host_fn_at takes the
// hooks) and a fake launcher (launcher_request_json below keeps the bodies).
//
//   $zig = "C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"; $s = "native\probe\src"
//   & $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o scratch\test_roster.exe `
//       native\probe\tests\test_roster.c $s\roster.c $s\rosterhook.c $s\guesthook.c $s\gameaddrs.c $s\log.c $s\plat_win.c
//   scratch\test_roster.exe [folder for its mkwmp.log; default %TEMP%]
#undef NDEBUG
#include <assert.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include "../src/config.h"
#include "../src/guesthook.h"
#include "../src/launcher.h"
#include "../src/log.h"
#include "../src/plat.h"
#include "../src/roster.h"
#include "../src/rosterhook.h"

RtApi g_rt;

// The 1.8.0 layout (gameaddrs.c)
static const struct roster_layout L = {.root = 0x30, .count = 0x48, .left = 0, .right = 8, .key = 0x18, .local = 0x20,
                                       .ussid = 0x28, .uid = 0x50, .st = 0x874, .name = 0x878, .sid = 0x8c8,
                                       .lid = 0x8d0};
enum { NODE = 0x1258, UID_INLINE = 0x5c, NAME_INLINE = 0x884 };

static void w64(uint8_t *p, uint64_t v) { memcpy(p, &v, 8); }
static void w32(uint8_t *p, uint32_t v) { memcpy(p, &v, 4); }

// One member node at n, which the game would see at address `at`: its strings are in its own buffers.
static void node(uint8_t *n, uint64_t at, uint32_t ussid, uint64_t left, uint64_t right, uint32_t st, int local,
                 const char *uid, const char *name, uint64_t sid, uint8_t lid) {
    memset(n, 0, NODE);
    w64(n + L.left, left), w64(n + L.right, right);
    w32(n + L.key, ussid), w32(n + L.ussid, ussid);
    n[L.local] = (uint8_t)local;
    w64(n + L.uid, at + UID_INLINE), w32(n + L.uid + 8, 0x400);
    strcpy((char *)n + UID_INLINE, uid);
    w32(n + L.st, st);
    w64(n + L.name, at + NAME_INLINE), w32(n + L.name + 8, 0x40);
    strcpy((char *)n + NAME_INLINE, name);
    w64(n + L.sid, sid);
    n[L.lid] = lid;
}

// ---- the fake image: IMAGE bytes the "game" has at BASE; everything else is not readable
enum { IMAGE = 0x60000, NODES = 64 };
#define BASE 0x7100000000ull
#define CTX (BASE + 0x100)
#define N(i) (BASE + 0x1000 + (uint64_t)(i) * 0x1300)
static uint8_t g_image[IMAGE];
static int g_reads;

static const void *image_at(void *user, uint64_t addr, size_t n) {
    (void)user;
    g_reads++;
    if (addr < BASE || addr - BASE > IMAGE || n > IMAGE - (addr - BASE)) return NULL;
    return g_image + (addr - BASE);
}
static const RosterMem kImage = {image_at, NULL};
static uint8_t *img(uint64_t addr) { return g_image + (addr - BASE); }

static void tree(uint64_t root, uint32_t count) { w64(img(CTX) + L.root, root), w32(img(CTX) + L.count, count); }
static void member(int i, uint32_t ussid, int left, int right, uint32_t st, int local, const char *uid, const char *name) {
    node(img(N(i)), N(i), ussid, left < 0 ? 0 : N(left), right < 0 ? 0 : N(right), st, local, uid, name,
         0x1000u + ussid, (uint8_t)(local ? 1 : 0));
}

static Roster r, r2;

static void test_walk(void) {
    assert(N(NODES) + 0x1300 <= BASE + IMAGE);
    memset(g_image, 0, sizeof g_image);
    // no context, an empty one
    assert(roster_walk(&kImage, &L, 0, &r) == 0 && r.count == 0);
    assert(roster_walk(&kImage, &L, CTX, &r) == 0);
    assert(roster_walk(&kImage, &L, BASE + IMAGE, &r) == -1);   // a context that is not there

    //        20
    //    10      30        15 is not present (st 1); 30 is this console's
    //      15  25
    member(0, 20, 1, 2, 2, 0, "uid-twenty", "Twenty");
    member(1, 10, -1, 3, 2, 0, "uid-ten", "Ten");
    member(2, 30, 4, -1, 2, 1, "uid-thirty", "Thirty");
    member(3, 15, -1, -1, 1, 0, "uid-fifteen", "Fifteen");
    member(4, 25, -1, -1, 2, 0, "", "");
    w64(img(N(4)) + L.sid, 0);
    tree(N(0), 5);
    assert(roster_walk(&kImage, &L, CTX, &r) == 4 && r.count == 4);
    assert(r.rows[0].ussid == 10 && r.rows[1].ussid == 20 && r.rows[2].ussid == 25 && r.rows[3].ussid == 30);
    assert(!strcmp(r.rows[0].uid, "uid-ten") && !strcmp(r.rows[0].name, "Ten") && !r.rows[0].local);
    assert(r.rows[0].sid == 0x100a && r.rows[0].lid == 0);
    assert(!strcmp(r.rows[2].uid, "") && !strcmp(r.rows[2].name, "") && r.rows[2].sid == 0);
    assert(r.rows[3].local == 1 && r.rows[3].lid == 1 && !strcmp(r.rows[3].name, "Thirty"));
    // st: only 2 is a member
    w32(img(N(3)) + L.st, 2), w32(img(N(0)) + L.st, 0), w32(img(N(2)) + L.st, 3);
    assert(roster_walk(&kImage, &L, CTX, &r) == 3);
    assert(r.rows[0].ussid == 10 && r.rows[1].ussid == 15 && r.rows[2].ussid == 25);
    w32(img(N(0)) + L.st, 2), w32(img(N(2)) + L.st, 2);

    // the count bounds the walk: no more nodes are visited than the context has
    tree(N(0), 2);
    assert(roster_walk(&kImage, &L, CTX, &r) == 2 && r.rows[1].ussid == 15);
    tree(N(0), 0);
    assert(roster_walk(&kImage, &L, CTX, &r) == 0);
    tree(0, 5);
    assert(roster_walk(&kImage, &L, CTX, &r) == 0);
    tree(N(0), ROSTER_NODES_MAX + 1);
    assert(roster_walk(&kImage, &L, CTX, &r) == -1);
    tree(N(0), 0x80000000u);
    assert(roster_walk(&kImage, &L, CTX, &r) == -1);
    // a count that is too large for the tree: the tree ends the walk, and the reads stay few
    tree(N(0), ROSTER_NODES_MAX);
    g_reads = 0;
    assert(roster_walk(&kImage, &L, CTX, &r) == 5 && g_reads < 32);

    // pointers that lead nowhere, or round in circles
    tree(N(0), 5);
    w64(img(N(2)) + L.left, BASE + IMAGE - 0x100);       // a node that is not all there
    assert(roster_walk(&kImage, &L, CTX, &r) == -1);
    w64(img(N(2)) + L.left, 0x1234);
    assert(roster_walk(&kImage, &L, CTX, &r) == -1);
    w64(img(N(2)) + L.left, ~0ull);
    assert(roster_walk(&kImage, &L, CTX, &r) == -1);
    w64(img(N(2)) + L.left, N(2));                       // its own left child
    assert(roster_walk(&kImage, &L, CTX, &r) == -1);
    w64(img(N(2)) + L.left, N(4));
    w64(img(N(4)) + L.right, N(0));                      // back to the root: the keys are no longer in order
    tree(N(0), 200);
    assert(roster_walk(&kImage, &L, CTX, &r) == -1);
    w64(img(N(4)) + L.right, 0);
    w32(img(N(4)) + L.ussid, 26);                        // key and ussid disagree
    assert(roster_walk(&kImage, &L, CTX, &r) == -1);
    w32(img(N(4)) + L.ussid, 25);
    assert(roster_walk(&kImage, &L, CTX, &r) == 5);

    // more members than a roster holds: the first ROSTER_MAX, in order (a chain to the right needs no depth)
    for (int i = 0; i < 40; i++) member(i, 100u + (unsigned)i, -1, i < 39 ? i + 1 : -1, 2, 0, "u", "n");
    tree(N(0), 40);
    assert(roster_walk(&kImage, &L, CTX, &r) == ROSTER_MAX && r.rows[0].ussid == 100 && r.rows[31].ussid == 131);
    // ... and a chain to the left deeper than any balanced tree: refused
    for (int i = 0; i < 60; i++) member(i, 1000u - (unsigned)i, i < 59 ? i + 1 : -1, -1, 2, 0, "u", "n");
    tree(N(0), 60);
    assert(roster_walk(&kImage, &L, CTX, &r) == -1);
}

static void test_strings(void) {
    memset(g_image, 0, sizeof g_image);
    member(0, 7, -1, -1, 2, 1, "x", "y");
    tree(N(0), 1);
    uint8_t *n = img(N(0));
    // a name that fills its buffer without an end: capacity - 1 bytes, never the byte after the buffer
    memset(n + NAME_INLINE, 'N', 0x40);
    memset(n + NAME_INLINE + 0x40, 'Z', 8);
    assert(roster_walk(&kImage, &L, CTX, &r) == 1 && strlen(r.rows[0].name) == 0x3f && !strchr(r.rows[0].name, 'Z'));
    // a capacity larger than the game's: ours is the limit
    memset(n + UID_INLINE, 'U', 0x400);
    memset(n + UID_INLINE + 0x400, 'Z', 8);
    w32(n + L.uid + 8, 0x100000);
    assert(roster_walk(&kImage, &L, CTX, &r) == 1 && strlen(r.rows[0].uid) == 0x3ff && !strchr(r.rows[0].uid, 'Z'));
    // a smaller one is respected
    w32(n + L.uid + 8, 5), w32(n + L.name + 8, 1);
    assert(roster_walk(&kImage, &L, CTX, &r) == 1 && !strcmp(r.rows[0].uid, "UUUU") && !strcmp(r.rows[0].name, ""));
    // no capacity, no buffer, a buffer that is not there: an empty string, the row stays
    w32(n + L.uid + 8, 0), w32(n + L.name + 8, 0x40), w64(n + L.name, 0);
    assert(roster_walk(&kImage, &L, CTX, &r) == 1 && !r.rows[0].uid[0] && !r.rows[0].name[0] && r.rows[0].ussid == 7);
    w32(n + L.uid + 8, 0x400), w64(n + L.uid, BASE + IMAGE - 0x10), w64(n + L.name, 0x10);
    assert(roster_walk(&kImage, &L, CTX, &r) == 1 && !r.rows[0].uid[0] && !r.rows[0].name[0]);
    // a string elsewhere in memory
    strcpy((char *)img(BASE + 0x800), "elsewhere");
    w64(n + L.uid, BASE + 0x800);
    assert(roster_walk(&kImage, &L, CTX, &r) == 1 && !strcmp(r.rows[0].uid, "elsewhere"));
}

static void test_hash(void) {
    memset(g_image, 0, sizeof g_image);
    member(0, 20, 1, 2, 2, 0, "uid-b", "Bee");
    member(1, 10, -1, -1, 2, 1, "uid-a", "Ay");
    member(2, 30, -1, -1, 1, 0, "uid-c", "Sea");
    tree(N(0), 3);
    assert(roster_walk(&kImage, &L, CTX, &r) == 2);
    uint64_t h = roster_hash(&r);
    assert(roster_walk(&kImage, &L, CTX, &r2) == 2 && roster_hash(&r2) == h);
    r2.count = 0;
    uint64_t empty = roster_hash(&r2);
    assert(empty != h);
    // what is not {ussid, uid, name} does not count: the station, the local flag
    w64(img(N(0)) + L.sid, 0x999), img(N(0))[L.lid] = 3, img(N(1))[L.local] = 0;
    assert(roster_walk(&kImage, &L, CTX, &r2) == 2 && roster_hash(&r2) == h);
    // a rename, another uid, another ussid, a member more, a member less: each is a change
    strcpy((char *)img(N(0)) + NAME_INLINE, "Bea");
    assert(roster_walk(&kImage, &L, CTX, &r2) == 2 && roster_hash(&r2) != h);
    strcpy((char *)img(N(0)) + NAME_INLINE, "Bee");
    strcpy((char *)img(N(1)) + UID_INLINE, "uid-A");
    assert(roster_walk(&kImage, &L, CTX, &r2) == 2 && roster_hash(&r2) != h);
    strcpy((char *)img(N(1)) + UID_INLINE, "uid-a");
    assert(roster_walk(&kImage, &L, CTX, &r2) == 2 && roster_hash(&r2) == h);
    w32(img(N(1)) + L.key, 11), w32(img(N(1)) + L.ussid, 11);
    assert(roster_walk(&kImage, &L, CTX, &r2) == 2 && roster_hash(&r2) != h);
    w32(img(N(1)) + L.key, 10), w32(img(N(1)) + L.ussid, 10);
    w32(img(N(2)) + L.st, 2);
    assert(roster_walk(&kImage, &L, CTX, &r2) == 3 && roster_hash(&r2) != h);
    w32(img(N(2)) + L.st, 1), w32(img(N(1)) + L.st, 0);
    assert(roster_walk(&kImage, &L, CTX, &r2) == 1 && roster_hash(&r2) != h && roster_hash(&r2) != empty);
    // where one string ends and the next begins counts
    r2.count = 1, r2.rows[0].ussid = 1;
    strcpy(r2.rows[0].uid, "ab"), strcpy(r2.rows[0].name, "c");
    uint64_t a = roster_hash(&r2);
    strcpy(r2.rows[0].uid, "a"), strcpy(r2.rows[0].name, "bc");
    assert(roster_hash(&r2) != a);
}

static void test_key(void) {
    assert(roster_key_ok("12_3_0_1_a1b2c3d4") && roster_key_ok("4294967295_-1_-2147483648_0_0"));
    assert(roster_key_ok("0_0_0_0_0") && roster_key_ok("7_1_2_3_DEADBEEF"));
    assert(!roster_key_ok("") && !roster_key_ok("12_3_0_1") && !roster_key_ok("12_3_0_1_") && !roster_key_ok("_3_0_1_a"));
    assert(!roster_key_ok("12_3_0_1_a_") && !roster_key_ok("12_3_0_1_g") && !roster_key_ok("-12_3_0_1_a"));
    assert(!roster_key_ok("12_3_0_1_a b") && !roster_key_ok("12_3_0_1_a\n") && !roster_key_ok("12__0_1_a"));
    assert(!roster_key_ok("12345678901_3_0_1_a") && !roster_key_ok("1_a_0_1_a") && !roster_key_ok("1_2_3_4_5_6"));

    char key[ROSTER_KEY_CAP];
    memset(g_image, 0, sizeof g_image);
    uint64_t at = BASE + 0x2000 + 0xb8;
    // a string object: pointer, capacity, the characters after them
    w64(img(at), at + 0xc), w32(img(at) + 8, 0x40);
    strcpy((char *)img(at) + 0xc, "55_1_2_3_abc");
    assert(roster_match_key(&kImage, at, key) == 1 && !strcmp(key, "55_1_2_3_abc"));
    // ... whose text is not a key, has no end, or is not there
    strcpy((char *)img(at) + 0xc, "hello");
    assert(roster_match_key(&kImage, at, key) == 0 && !key[0]);
    memset(img(at) + 0xc, '1', 0x80);
    assert(roster_match_key(&kImage, at, key) == 0 && !key[0]);
    w64(img(at), 0x10);
    assert(roster_match_key(&kImage, at, key) == 0);
    // the characters themselves
    memset(img(at), 0, 0x100);
    strcpy((char *)img(at), "9_-1_0_24_1f");
    assert(roster_match_key(&kImage, at, key) == 1 && !strcmp(key, "9_-1_0_24_1f"));
    assert(roster_match_key(&kImage, 0, key) == 0 && roster_match_key(&kImage, BASE + IMAGE - 4, key) == 0);
}

static void test_json(void) {
    static char out[ROSTER_JSON_MAX], esc[64];
    // escapes: quotes, backslashes, control characters; UTF-8 kept; what is not UTF-8 becomes '?'
    assert(roster_json_escape("a\"b\\c", esc, sizeof esc) == 7 && !strcmp(esc, "a\\\"b\\\\c"));
    assert(roster_json_escape("\n\t\x01\x1f\x7f ", esc, sizeof esc) == 31 &&
           !strcmp(esc, "\\u000a\\u0009\\u0001\\u001f\\u007f "));
    assert(roster_json_escape("\xc3\xa9\xe3\x83\x9e\xf0\x9f\x8f\x81", esc, sizeof esc) == 9 &&
           !strcmp(esc, "\xc3\xa9\xe3\x83\x9e\xf0\x9f\x8f\x81"));
    assert(roster_json_escape("a\xff" "b\x80" "c", esc, sizeof esc) == 5 && !strcmp(esc, "a?b?c"));
    assert(roster_json_escape("\xc3", esc, sizeof esc) == 1 && !strcmp(esc, "?"));              // cut short
    assert(roster_json_escape("x\xe3\x83", esc, sizeof esc) == 3 && !strcmp(esc, "x??"));
    assert(roster_json_escape("\xf0\x9f\x8f", esc, sizeof esc) == 3 && !strcmp(esc, "???"));
    assert(roster_json_escape("\xc0\xaf", esc, sizeof esc) == 2 && !strcmp(esc, "??"));          // overlong '/'
    assert(roster_json_escape("\xe0\x80\xaf", esc, sizeof esc) == 3 && !strcmp(esc, "???"));
    assert(roster_json_escape("\xed\xa0\x80", esc, sizeof esc) == 3 && !strcmp(esc, "???"));     // a surrogate
    assert(roster_json_escape("\xf4\x90\x80\x80", esc, sizeof esc) == 4 && !strcmp(esc, "????")); // past U+10FFFF
    assert(roster_json_escape("\xf5\x80\x80\x80", esc, sizeof esc) == 4);
    assert(roster_json_escape("", esc, sizeof esc) == 0 && !esc[0]);
    assert(roster_json_escape("abc", esc, 3) == -1 && roster_json_escape("", esc, 0) == -1);
    assert(roster_json_escape("", esc, 1) == 0);

    // the body
    assert(roster_json("left", NULL, NULL, out, sizeof out) == 42);
    assert(!strcmp(out, "{\"event\":\"left\",\"match\":null,\"members\":[]}"));
    memset(&r, 0, sizeof r);
    r.count = 2;
    r.rows[0].ussid = 12, r.rows[0].local = 1, r.rows[0].lid = 0, r.rows[0].sid = 0x1122334455667788ull;
    strcpy(r.rows[0].uid, "0f8fad5b-d9cb-469f-a165-70867728950e"), strcpy(r.rows[0].name, "Ann \"A\" \\ \xc3\xa9\n");
    r.rows[1].ussid = 4000000000u, r.rows[1].lid = 1;
    strcpy(r.rows[1].name, "\xff");
    int n = roster_json("change", NULL, &r, out, sizeof out);
    const char *want =
        "{\"event\":\"change\",\"match\":null,\"members\":["
        "{\"ussid\":12,\"uid\":\"0f8fad5b-d9cb-469f-a165-70867728950e\",\"name\":\"Ann \\\"A\\\" \\\\ \xc3\xa9\\u000a\","
        "\"local\":true,\"lid\":0,\"sid\":\"1122334455667788\"},"
        "{\"ussid\":4000000000,\"uid\":\"\",\"name\":\"?\",\"local\":false,\"lid\":1,\"sid\":null}]}";
    assert(n == (int)strlen(want) && !strcmp(out, want));
    r.rows[1].sid = 0xab;
    n = roster_json("match", "12_3_0_1_a1b2c3d4", &r, out, sizeof out);
    assert(n > 0 && strstr(out, "{\"event\":\"match\",\"match\":\"12_3_0_1_a1b2c3d4\",\"members\":[{"));
    assert(strstr(out, "\"sid\":\"00000000000000ab\"}]}"));
    // too small a buffer: refused, at every size below the body's
    static char small[512];
    for (size_t cap = 0; cap <= (size_t)n; cap++) assert(roster_json("match", "12_3_0_1_a1b2c3d4", &r, small, cap) == -1);
    assert(roster_json("match", "12_3_0_1_a1b2c3d4", &r, small, (size_t)n + 8) == n && !strcmp(small, out));
    // the largest body there is fits ROSTER_JSON_MAX: every byte of every string a control character
    r.count = ROSTER_MAX;
    for (int i = 0; i < ROSTER_MAX; i++) {
        RosterRow *row = &r.rows[i];
        row->ussid = 0xffffffffu, row->local = 0, row->lid = 255, row->sid = ~0ull;
        memset(row->uid, 1, sizeof row->uid - 1), row->uid[sizeof row->uid - 1] = 0;
        memset(row->name, 1, sizeof row->name - 1), row->name[sizeof row->name - 1] = 0;
    }
    n = roster_json("change", NULL, &r, out, sizeof out);
    assert(n > ROSTER_MAX * 6 * (0x3ff + 0x3f) && n < ROSTER_JSON_MAX && (int)strlen(out) == n);
}

// ---- the hooks: a fake game and a fake launcher ------------------------------------------------------------------
enum { FN_UPDATE = 0x1000, FN_MATCH0 = 0x2000, FN_MATCH1 = 0x3000, BODIES = 24 };
static uint64_t g_vtable[40], g_slot0[2], g_slot1, g_ctrl_g, g_mgr_g;
static uint8_t g_ctrl[0x100], g_mgr[0x100], g_sessions[0x1400], g_ctx[0x100], g_nodes[4][NODE], g_match_node[0x200];
static struct { uint64_t addr; GuestFn fn; } g_hooks[8];
static int g_hook_count, g_updates, g_matches, g_join_in_update;
static MkwmpConfig g_cfg;
static PlatLock g_body_lock = PLAT_LOCK_INIT;
static char g_bodies[BODIES][2048];
static uint64_t g_body_ms[BODIES];
static volatile int32_t g_body_count, g_status = 204;
static char g_log[16384];

const MkwmpConfig *mkwmp_config(void) { return &g_cfg; }

int launcher_request_json(const char *method, const char *path, const char *json, char *buf, size_t cap,
                          const char **body) {
    assert(!strcmp(method, "POST") && !strcmp(path, "/local/v1/roster") && json && cap);
    plat_lock(&g_body_lock);
    int at = g_body_count;
    assert(at < BODIES && strlen(json) < sizeof g_bodies[0]);
    strcpy(g_bodies[at], json);
    g_body_ms[at] = plat_ms();
    plat_store(&g_body_count, at + 1);
    plat_unlock(&g_body_lock);
    buf[0] = 0;
    *body = buf;
    return plat_load(&g_status);
}

static uint64_t host(const void *p) { return (uint64_t)(uintptr_t)p; }

static void set_members(int count) {   // 0: nobody; 1: 9; 2: 5 (local) and 9; 3: 5, 9 and 12
    w64(g_ctx + L.root, count ? host(g_nodes[1]) : 0), w32(g_ctx + L.count, (uint32_t)count);
    w64(g_nodes[1] + L.left, count > 1 ? host(g_nodes[0]) : 0);
    w64(g_nodes[1] + L.right, count > 2 ? host(g_nodes[2]) : 0);
}

static uint64_t fake_call(Cpu *c, uint64_t fn, int n, ...) {
    assert(n == 0);   // the hooks only ever run the function they stand before, with its own registers
    if (fn == FN_UPDATE) {
        assert(c->x[0] == host(g_ctrl) && c->x[1] == 0x1111);
        g_updates++;
        if (g_join_in_update) set_members(g_join_in_update), g_join_in_update = 0;   // the update fills the tree
        c->x[0] = 0x7777;
    } else if (fn == FN_MATCH0 || fn == FN_MATCH1) {
        assert(c->x[0] == host(g_match_node));
        g_matches++;
        c->x[0] = 0x5555;
    } else {
        assert(!"unexpected guest call");
    }
    return c->x[0];
}

static int fake_host_fn_at(uint64_t addr, GuestFn fn) {
    assert(g_hook_count < 8);
    g_hooks[g_hook_count].addr = addr, g_hooks[g_hook_count].fn = fn;
    g_hook_count++;
    return 1;
}

static void run(uint64_t target, uint64_t x0, uint64_t want) {
    Cpu c;
    memset(&c, 0, sizeof c);
    c.x[0] = x0, c.x[1] = 0x1111;
    for (int i = 0; i < g_hook_count; i++)
        if (g_hooks[i].addr == target) {
            g_hooks[i].fn(&c);
            assert(c.x[0] == want && c.x[1] == 0x1111);   // the game's own result, its registers
            return;
        }
    assert(!"no hook at the slot's address");
}
static void update(void) { run(g_vtable[38], host(g_ctrl), 0x7777); }
static void update_later(void) { plat_sleep_ms(230); update(); }   // past the hook's pause between two reads

static int bodies(int want, uint32_t ms) {   // waits for `want` bodies; returns how many there are
    for (uint64_t until = plat_ms() + ms; plat_load(&g_body_count) < want && plat_ms() < until;) plat_sleep_ms(5);
    return plat_load(&g_body_count);
}

static int logged(const char *text) {
    char path[512];
    assert(plat_home_path(path, sizeof path, "mkwmp.log"));
    FILE *f = fopen(path, "rb");
    if (!f) return 0;
    size_t n = fread(g_log, 1, sizeof g_log - 1, f);
    fclose(f);
    g_log[n] = 0;
    return strstr(g_log, text) != NULL;
}

static void test_hook(void) {
    static struct game_addrs a;
    a.ro_vtable = host(g_vtable), a.ro_slot = 38, a.ro_update = FN_UPDATE;
    a.ro_ctrl_g = host(&g_ctrl_g), a.ro_mgr_g = host(&g_mgr_g);
    a.ro_ctx = 0xa0, a.ro_mgr = 0x80, a.ro_cur = 0x12d0, a.ro_match_key = 0xb8;
    a.rl = L;
    a.ro_match[0] = FN_MATCH0, a.ro_match[1] = FN_MATCH1;
    a.ro_match_slots[0][0] = host(&g_slot0[0]), a.ro_match_slots[0][1] = host(&g_slot0[1]);
    a.ro_match_slots[1][0] = host(&g_slot1);
    static uint32_t word = 0x12345678;
    a.ro_guard[0].at = host(&word), a.ro_guard[0].word = 0x12345678;
    g_vtable[38] = FN_UPDATE, g_slot0[0] = g_slot0[1] = FN_MATCH0, g_slot1 = FN_MATCH1;
    // the game: controller -> two contexts (the second is the current one), session manager -> current slot
    g_ctrl_g = host(g_ctrl), g_mgr_g = host(g_mgr);
    w64(g_mgr + 0x80, host(g_sessions));
    w32(g_sessions + 0x12d0, 1);
    w64(g_ctrl + 0xa0 + 8, host(g_ctx));
    node(g_nodes[0], host(g_nodes[0]), 5, 0, 0, 2, 1, "uid-me-0001", "Me", 0x5151515151510001ull, 0);
    node(g_nodes[1], host(g_nodes[1]), 9, 0, 0, 2, 0, "uid-you-0002", "Yolanda \"2\"", 0x5151515151510002ull, 0);
    node(g_nodes[2], host(g_nodes[2]), 12, 0, 0, 2, 0, "uid-she-0003", "Sheila", 0, 1);
    set_members(0);

    // guards: each kind that does not hold keeps the hooks out
    g_rt.call = fake_call;
    g_rt.host_fn_at = fake_host_fn_at;
    static struct game_addrs bad;
    assert(game_guard_roster(&a));
    bad = a, bad.ro_guard[0].word = 1;
    assert(!game_guard_roster(&bad));
    bad = a, bad.ro_update = FN_UPDATE + 4;
    assert(!game_guard_roster(&bad));
    bad = a, bad.ro_match[1] = FN_MATCH0;
    assert(!game_guard_roster(&bad));
    bad = a, bad.ro_guard[0].at = 0;
    assert(!game_guard_roster(&bad) && !game_guard_roster(NULL));
    assert(g_hook_count == 0 && g_vtable[38] == FN_UPDATE);

    roster_left();   // before anything is installed: nothing
    assert(roster_install(&a) == 1 && g_hook_count == 4);
    assert(g_vtable[38] != FN_UPDATE && g_slot0[0] != FN_MATCH0 && g_slot0[1] != FN_MATCH0 && g_slot1 != FN_MATCH1);
    assert(logged("roster: the lobby's members are read after the online race controller's update (match marker: 3 of 3"));

    // offline: the update runs, nothing is told; leaving online tells nothing either
    update();
    update_later();
    roster_left();
    update_later();
    assert(g_updates == 3 && bodies(1, 300) == 0);
    assert(logged("roster: the online race controller's update runs") && !strstr(g_log, "not the game's global"));

    // a lobby: the game's update fills the tree, and the read after it sees the two members
    roster_online();
    g_join_in_update = 2;
    update_later();
    assert(bodies(1, 2000) == 1);
    assert(!strcmp(g_bodies[0], "{\"event\":\"change\",\"match\":null,\"members\":["
                   "{\"ussid\":5,\"uid\":\"uid-me-0001\",\"name\":\"Me\",\"local\":true,\"lid\":0,"
                   "\"sid\":\"5151515151510001\"},"
                   "{\"ussid\":9,\"uid\":\"uid-you-0002\",\"name\":\"Yolanda \\\"2\\\"\",\"local\":false,\"lid\":0,"
                   "\"sid\":\"5151515151510002\"}]}"));
    // the same set again, and a field that is not part of it: nothing
    update_later();
    w64(g_nodes[1] + L.sid, 0x5151515151510009ull);
    update_later();
    assert(bodies(2, 300) == 1);
    // a read is not made at every frame: a rename is seen at the next one
    update_later();
    strcpy((char *)g_nodes[1] + NAME_INLINE, "Renamed");
    update();
    update();
    assert(bodies(2, 100) == 1);
    update_later();
    assert(bodies(2, 2000) == 2 && strstr(g_bodies[1], "\"name\":\"Renamed\"") && !strstr(g_bodies[1], "\"ussid\":12,"));
    // two more changes within the second: one "change", with the latest set, a second after the last one
    set_members(3);
    update_later();
    strcpy((char *)g_nodes[1] + NAME_INLINE, "Renamed2");
    update_later();
    assert(bodies(3, 2000) == 3 && bodies(4, 400) == 3);
    assert(g_body_ms[2] - g_body_ms[1] >= 900);
    assert(strstr(g_bodies[2], "\"event\":\"change\"") && strstr(g_bodies[2], "\"name\":\"Renamed2\"") &&
           strstr(g_bodies[2], "\"sid\":\"5151515151510009\"") &&
           strstr(g_bodies[2], "{\"ussid\":12,\"uid\":\"uid-she-0003\",\"name\":\"Sheila\",\"local\":false,\"lid\":1,\"sid\":null}]}"));

    // a match starts: its key is taken when the referee request went out, the members at the next update, at once
    w64(g_match_node + 0xb8, host(g_match_node) + 0xc4), w32(g_match_node + 0xc0, 0x40);
    strcpy((char *)g_match_node + 0xc4, "777_1_0_3_beef");
    run(g_slot0[1], host(g_match_node), 0x5555);
    assert(g_matches == 1 && bodies(4, 100) == 3);
    update();
    assert(bodies(4, 2000) == 4 && g_body_ms[3] - g_body_ms[2] < 900);   // not held back as a "change" is
    assert(strstr(g_bodies[3], "{\"event\":\"match\",\"match\":\"777_1_0_3_beef\",\"members\":[{\"ussid\":5,") &&
           strstr(g_bodies[3], "\"ussid\":12,"));
    // the same match again (another node of it): once
    run(g_slot0[0], host(g_match_node), 0x5555);
    update();
    // the other sender, the next match; a node whose key is not there
    strcpy((char *)g_match_node + 0xc4, "777_2_0_3_beef");
    run(g_slot1, host(g_match_node), 0x5555);
    update();
    assert(bodies(5, 2000) == 5 && strstr(g_bodies[4], "\"match\":\"777_2_0_3_beef\""));
    strcpy((char *)g_match_node + 0xc4, "no key here");
    run(g_slot1, host(g_match_node), 0x5555);
    update_later();
    assert(g_matches == 4 && bodies(6, 300) == 5);
    assert(logged("roster: a match starts, but its key is not where expected"));

    // a tree that is not as expected: nothing is sent, and nothing is forgotten
    w32(g_nodes[0] + L.ussid, 6);
    update_later();
    w32(g_nodes[0] + L.ussid, 5);
    update_later();
    assert(bodies(6, 300) == 5 && logged("roster: the lobby's member tree is not as expected"));

    // the launcher is away: said once, and the next lobby is told again
    plat_store(&g_status, 0);
    set_members(2);
    update_later();
    assert(bodies(6, 2000) == 6);
    plat_store(&g_status, 204);

    // leaving online: "left", once; the members still in the tree are not told again
    roster_left();
    roster_left();
    assert(bodies(7, 2000) == 7 && !strcmp(g_bodies[6], "{\"event\":\"left\",\"match\":null,\"members\":[]}"));
    update_later();
    set_members(3);
    update_later();
    assert(bodies(8, 300) == 7);
    // the tree empties (nothing to tell: no lobby was told since "left"), then the next lobby: the same members are new
    set_members(0);
    update_later();
    assert(bodies(8, 300) == 7);
    set_members(2);
    update_later();
    assert(bodies(8, 2000) == 8 && strstr(g_bodies[7], "\"event\":\"change\"") && strstr(g_bodies[7], "\"ussid\":9,"));
    // leaving the lobby while online: an empty "change"
    set_members(0);
    update_later();
    assert(bodies(9, 2000) == 9 && !strcmp(g_bodies[8], "{\"event\":\"change\",\"match\":null,\"members\":[]}"));
    // left, and online again before the tree was seen empty: the lobby that comes is told
    set_members(1);
    update_later();
    assert(bodies(10, 2000) == 10);
    roster_left();
    assert(bodies(11, 2000) == 11 && strstr(g_bodies[10], "\"event\":\"left\""));
    update_later();
    roster_online();
    update_later();
    assert(bodies(12, 2000) == 12 && strstr(g_bodies[11], "\"event\":\"change\"") && strstr(g_bodies[11], "\"ussid\":9,"));
    // no current context (slot 2 and up): no lobby
    w32(g_sessions + 0x12d0, 0xffffffffu);
    update_later();
    assert(bodies(13, 2000) == 13 && strstr(g_bodies[12], "\"members\":[]}"));

    // a launcher without Previous races: three 404s, and nothing more is asked
    w32(g_sessions + 0x12d0, 1);
    plat_store(&g_status, 404);
    for (int i = 0; i < 4; i++) {
        set_members(1 + (i & 1));
        update_later();
        plat_sleep_ms(1100);
    }
    assert(bodies(17, 300) == 16 && logged("roster: this launcher has no Previous races"));

    // the log: counts, never an id, a name, a station or a key
    assert(logged("roster: the lobby has 2 member(s), 1 on this console"));
    assert(logged("roster: a match starts with 3 member(s), 1 on this console"));
    assert(logged("roster: left online") && logged("roster: the launcher keeps this session's lobbies"));
    assert(logged("roster: the launcher did not take the lobby (HTTP 0"));
    assert(!strstr(g_log, "uid-") && !strstr(g_log, "Renamed") && !strstr(g_log, "Yolanda") && !strstr(g_log, "Sheila"));
    assert(!strstr(g_log, "777_") && !strstr(g_log, "51515151"));
}
int main(int argc, char **argv) {
    test_walk();
    test_strings();
    test_hash();
    test_key();
    test_json();
    char dir[512];
    assert(argc > 1 || plat_getenv("TEMP", dir, sizeof dir));
    plat_set_home(argc > 1 ? argv[1] : dir);   // where this run's mkwmp.log goes
    log_init();
    test_hook();
    puts("roster: the member tree, the hash, the match key, the JSON body and the hooks passed");
    return 0;
}
