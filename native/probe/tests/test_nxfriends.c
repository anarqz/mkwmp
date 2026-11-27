// Nextendo friends from the game's own server (nxfriends.h): the account ids of a copy of the game's friend users,
// the per-sign-in set, and the hook against a fake friend manager (a fake runtime: g_rt.call runs C stand-ins of the
// game's functions, g_rt.host_fn_at takes the hook).
//
//   $zig = "C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"; $s = "native\probe\src"
//   & $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o scratch\test_nxfriends.exe `
//       native\probe\tests\test_nxfriends.c $s\nxfriends.c $s\friends.c $s\uuid5.c $s\launcher.c $s\seat.c $s\config.c `
//       $s\notify.c $s\lobby_ca.c $s\guesthook.c $s\gameaddrs.c $s\log.c $s\plat_win.c $s\plat_net_win.c -lws2_32
//   scratch\test_nxfriends.exe [folder for its mkwmp.log; default %TEMP%]
#undef NDEBUG
#include <assert.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include "../src/friends.h"
#include "../src/guesthook.h"
#include "../src/log.h"
#include "../src/nxfriends.h"
#include "../src/plat.h"

RtApi g_rt;

// ---- a friend user as the game holds it: a 0x28-byte element
static void user(uint8_t *e, const char *name, uint64_t account, int long_form) {
    memset(e, 0, 0x28);
    size_t len = strlen(name);
    if (long_form) {
        uint64_t cap = 0x31;
        memcpy(e, &cap, 8);          // bit 0: long
        memcpy(e + 8, &len, 8);
    } else {
        e[0] = (uint8_t)(len << 1);
        memcpy(e + 1, name, len);
    }
    memcpy(e + 0x20, &account, 8);
}

static void test_accounts(void) {
    uint8_t v[6 * 0x28];
    user(v, "u-3", 0x30, 0);
    user(v + 0x28, "u-1", 0x10, 1);
    user(v + 0x50, "", 0x99, 0);        // no user: skipped, as the game skips it
    user(v + 0x78, "u-1b", 0x10, 0);    // a second user of the same account
    user(v + 0xa0, "u-0", 0, 0);        // no account
    user(v + 0xc8, "u-2", 0x20, 0);
    uint64_t ids[8];
    assert(nxfriends_accounts(v, sizeof v, 0x28, 0x20, ids, 8) == 3);
    assert(ids[0] == 0x10 && ids[1] == 0x20 && ids[2] == 0x30);
    assert(nxfriends_accounts(v, sizeof v, 0x28, 0x20, ids, 2) == 2 && ids[0] == 0x10 && ids[1] == 0x30);
    assert(nxfriends_accounts(v, 0, 0x28, 0x20, ids, 8) == 0);
    assert(nxfriends_accounts(v, sizeof v - 1, 0x28, 0x20, ids, 8) == -1);   // not whole elements
    assert(nxfriends_accounts(v, sizeof v, 0x28, 0x24, ids, 8) == -1);       // the id does not fit the element
    assert(nxfriends_accounts(v, sizeof v, 0, 0, ids, 8) == -1);

    static NxFriendSet s;
    int added = -1;
    assert(nxfriends_set(&s, ids, 0, &added) == 0 && added == 0 && s.count == 0);
    const uint64_t a[] = {0x10, 0x20}, b[] = {0x10, 0x20, 0x30}, d[] = {0x20, 0x40};
    assert(nxfriends_set(&s, a, 2, &added) == 1 && added == 2 && s.count == 2);
    assert(nxfriends_set(&s, a, 2, &added) == 0 && added == 0);
    assert(nxfriends_set(&s, b, 3, &added) == 1 && added == 1 && s.count == 3 && s.ids[2] == 0x30);
    assert(nxfriends_set(&s, a, 2, &added) == 1 && added == 0 && s.count == 2);    // one gone
    assert(nxfriends_set(&s, d, 2, &added) == 1 && added == 1 && s.ids[1] == 0x40);   // same size, another friend
    assert(nxfriends_set(&s, NULL, 0, &added) == 1 && added == 0 && s.count == 0);
}

// ---- the hook: a fake game ---------------------------------------------------------------------------------------
enum { FN_CHANGED = 0x1000, FN_TICK = 0x2000, FN_RELOAD = 0x3000, FN_REMAP = 0x4000, FN_REINIT = 0x5000,
       FN_COPY = 0x6000, FN_FREE = 0x7000, FN_DONE = 0x8000 };
static uint64_t g_vtable[24], g_op_vtable[8], g_op[2];
static struct { uint64_t vtable, client; uint8_t pad0[0x18]; uint64_t op; uint8_t pad[0x58]; uint32_t state, state2;
                uint8_t pad2[8]; int32_t count, cap; uint64_t items; } g_mgr;
static uint8_t g_client[0x100], g_users[4 * 0x28];
static size_t g_users_bytes;
static uint8_t g_entry[2][0x400];
static uint64_t g_entries[2];
static GuestFn g_hook;
static int ticks, copies, frees, changed_calls, reinits, reinit_at, changed_answer, done_calls, op_done;
static char g_log[8192];

static uint64_t fake_call(Cpu *c, uint64_t fn, int n, ...) {
    va_list ap;
    va_start(ap, n);
    for (int i = 0; i < n; i++) c->x[i] = va_arg(ap, uint64_t);
    va_end(ap);
    if (fn == FN_TICK) {
        assert(c->x[0] == (uint64_t)(uintptr_t)&g_mgr && c->x[1] == 0x1111 && c->x[8] == 0x8888);   // as it was called
        if (g_mgr.op && g_mgr.state == 0 && op_done) g_mgr.state = 1;   // the game takes the server's answer in
        ticks++;
        c->x[0] = 0x7777;
    } else if (fn == FN_DONE) {
        assert(c->x[0] == (uint64_t)(uintptr_t)g_op);
        done_calls++;
        c->x[0] = (uint64_t)op_done;
    } else if (fn == FN_COPY) {
        assert(c->x[0] == (uint64_t)(uintptr_t)g_client);
        uint64_t *vec = (uint64_t *)(uintptr_t)c->x[8];
        vec[0] = g_users_bytes ? (uint64_t)(uintptr_t)g_users : 0;
        vec[1] = vec[0] + g_users_bytes;
        vec[2] = vec[1];
        vec[3] = 0xa110c;
        copies++;
    } else if (fn == FN_FREE) {
        uint64_t **pp = (uint64_t **)(uintptr_t)c->x[0];
        assert((*pp)[3] == 0xa110c);   // the vector the copy filled
        frees++;
    } else if (fn == FN_CHANGED) {
        assert(c->x[0] == (uint64_t)(uintptr_t)&g_mgr);
        changed_calls++;
        c->x[0] = (uint64_t)changed_answer;
    } else if (fn == FN_REINIT) {
        assert(c->x[0] == (uint64_t)(uintptr_t)&g_mgr);
        reinits++;
        reinit_at = ticks;
        c->x[0] = 1;
    } else {
        assert(!"unexpected guest call");
    }
    return c->x[0];
}
static int fake_host_fn_at(uint64_t addr, GuestFn fn) { (void)addr; g_hook = fn; return 1; }

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

static void tick(void) {
    Cpu c;
    memset(&c, 0, sizeof c);
    c.x[0] = (uint64_t)(uintptr_t)&g_mgr;
    c.x[1] = 0x1111, c.x[8] = 0x8888;
    g_hook(&c);
    assert(c.x[0] == 0x7777);   // the update's own result
    plat_sleep_ms(1020);        // the hook looks once a second
}

static void test_hook(void) {
    assert(offsetof(__typeof__(g_mgr), op) == 0x28 && offsetof(__typeof__(g_mgr), state) == 0x88);
    assert(offsetof(__typeof__(g_mgr), count) == 0x98 && offsetof(__typeof__(g_mgr), items) == 0xa0);
    static struct game_addrs a;
    a.fr_vtables[0] = (uint64_t)(uintptr_t)g_vtable;
    a.fr_slot_changed = 9, a.fr_slot_tick = 11, a.fr_slot_reload = 15, a.fr_slot_remap = 19;
    a.fr_changed = FN_CHANGED, a.fr_tick = FN_TICK, a.fr_reload = FN_RELOAD, a.fr_remap = FN_REMAP;
    a.fr_reinit = FN_REINIT, a.fr_users_copy = FN_COPY, a.fr_users_free = FN_FREE;
    static uint32_t word = 0x12345678;
    a.fr_guard[0].at = (uint64_t)(uintptr_t)&word, a.fr_guard[0].word = 0x12345678;
    a.fr_client = 8, a.fr_op = 0x28, a.fr_op_done = 0x20, a.fr_state = 0x88, a.fr_count = 0x98, a.fr_items = 0xa0;
    a.fr_mapped = 0x3d0, a.fr_user_size = 0x28, a.fr_user_account = 0x20;
    g_vtable[9] = FN_CHANGED, g_vtable[11] = FN_TICK, g_vtable[15] = FN_RELOAD, g_vtable[19] = FN_REMAP;
    g_mgr.vtable = (uint64_t)(uintptr_t)g_vtable;
    g_op_vtable[4] = FN_DONE;
    g_op[0] = (uint64_t)(uintptr_t)g_op_vtable;
    g_entries[0] = (uint64_t)(uintptr_t)g_entry[0], g_entries[1] = (uint64_t)(uintptr_t)g_entry[1];
    g_mgr.items = (uint64_t)(uintptr_t)g_entries;

    // a guard that does not hold: no hook
    g_rt.call = fake_call;
    g_rt.host_fn_at = fake_host_fn_at;
    static struct game_addrs bad;
    bad = a;
    bad.fr_guard[0].word = 1;
    assert(!game_guard_friends(&bad) && game_guard_friends(&a));
    assert(nxfriends_install(&a) == 1 && g_hook);
    assert(g_vtable[11] != FN_TICK);   // the slot is the hook's now; the manager is still known by its other slots

    const uint64_t owner[2] = {1, 2};
    static GFriend page[10];
    // ---- a sign-in whose first answer names two accounts
    // signed out; then the friend client exists and its start is on its way: asked, nothing read
    tick();
    assert(done_calls == 0);
    g_mgr.client = (uint64_t)(uintptr_t)g_client, g_mgr.op = (uint64_t)(uintptr_t)g_op;
    tick();
    assert(ticks == 2 && done_calls == 1 && copies == 0 && friends_list(owner, 0, 10, page) == 0);
    // the start finished: the accounts are handed over before the game's update takes the answer in, once
    user(g_users, "u-2", 0xbb, 0);
    user(g_users + 0x28, "u-1", 0xaa, 1);
    g_users_bytes = 0x50;
    op_done = changed_answer = 1;
    tick();
    assert(ticks == 3 && changed_calls == 1 && reinits == 1 && reinit_at == 2);   // before the third update ran
    assert(g_mgr.state == 1 && copies == 2 && frees == 2);                         // read before, and looked at after
    assert(friends_list(owner, 0, 10, page) == 2 && page[0].account_id == 0xaa && page[1].account_id == 0xbb);
    // the game read and attached them
    g_mgr.count = 2;
    g_entry[0][0x3d0] = 1;
    tick();
    assert(changed_calls == 1 && reinits == 1 && done_calls == 2);   // started: the operation is not asked any more
    assert(logged("friends: the game's friend list has 2, 1 matched"));
    // a third account later: served, but the game is not pushed again (its next own read takes it)
    user(g_users + 0x50, "u-3", 0xcc, 0);
    g_users_bytes = 0x78;
    tick();
    assert(changed_calls == 1 && reinits == 1 && friends_list(owner, 0, 10, page) == 3);
    // a copy that is not whole elements: left alone, the set stays
    g_users_bytes = 0x77;
    tick();
    assert(frees == copies && friends_list(owner, 0, 10, page) == 3);
    // signed out: forgotten
    g_mgr.client = 0, g_mgr.op = 0, g_mgr.state = 0;
    tick();
    assert(friends_list(owner, 0, 10, page) == 0);

    // ---- a sign-in whose first answer is empty, and the accounts come later: handed over then
    g_mgr.client = (uint64_t)(uintptr_t)g_client, g_mgr.state = 1, g_users_bytes = 0;
    tick();
    assert(changed_calls == 1 && friends_list(owner, 0, 10, page) == 0);
    g_users_bytes = 0x28;
    tick();
    assert(changed_calls == 2 && reinits == 2 && friends_list(owner, 0, 10, page) == 1 && page[0].account_id == 0xbb);
    g_mgr.client = 0, g_mgr.state = 0;
    tick();

    // ---- the game has not read its list yet (or has them): asked, no start-over
    g_mgr.client = (uint64_t)(uintptr_t)g_client, g_mgr.state = 1;
    changed_answer = 0;
    tick();
    assert(changed_calls == 3 && reinits == 2 && friends_list(owner, 0, 10, page) == 1);
    assert(frees == copies);
    assert(logged("Nextendo's server names 2 friend account(s), 2 new"));
    assert(logged("the game reads its friend list again"));
    assert(logged("the game needs no new read of its friend list"));
    assert(!strstr(g_log, "0xaa") && !strstr(g_log, "00000000000000aa") && !strstr(g_log, "u-1"));   // no id, no user
}


int main(int argc, char **argv) {
    test_accounts();
    char dir[512];
    assert(argc > 1 || plat_getenv("TEMP", dir, sizeof dir));
    plat_set_home(argc > 1 ? argv[1] : dir);   // where this run's mkwmp.log goes
    log_init();
    test_hook();
    puts("nxfriends: accounts, the set and the friend manager hook passed");
    return 0;
}
