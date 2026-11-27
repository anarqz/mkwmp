#include "nxfriends.h"

#include "friends.h"
#include "guesthook.h"
#include "log.h"
#include "plat.h"

#include <string.h>

_Static_assert(sizeof((NxFriendSet *)0)->ids / sizeof(uint64_t) == FRIENDS_MAX, "one set fills the friend list");

// ---- pure parts ------------------------------------------------------------------------------------------------
int nxfriends_accounts(const uint8_t *users, size_t bytes, size_t elem, size_t account_at, uint64_t *out, int cap) {
    if (elem < 16 || account_at + 8 > elem || bytes % elem) return -1;
    int n = 0;
    for (size_t off = 0; off < bytes; off += elem) {
        const uint8_t *e = users + off;
        uint64_t len = e[0] >> 1, id;
        if (e[0] & 1) memcpy(&len, e + 8, 8);
        memcpy(&id, e + account_at, 8);
        if (!len || !id) continue;
        int at = 0;
        while (at < n && out[at] < id) at++;
        if ((at < n && out[at] == id) || n >= cap) continue;
        memmove(out + at + 1, out + at, (size_t)(n - at) * sizeof out[0]);
        out[at] = id;
        n++;
    }
    return n;
}

int nxfriends_set(NxFriendSet *s, const uint64_t *ids, int n, int *added) {
    int cap = (int)(sizeof s->ids / sizeof s->ids[0]), fresh = 0;
    if (n < 0) n = 0;
    if (n > cap) n = cap;
    for (int i = 0; i < n; i++) {
        int had = 0;
        for (int k = 0; k < s->count && !had; k++) had = s->ids[k] == ids[i];
        fresh += !had;
    }
    if (added) *added = fresh;
    if (!fresh && n == s->count) return 0;   // nothing new and nothing gone: the same set
    if (n) memcpy(s->ids, ids, (size_t)n * sizeof ids[0]);
    s->count = n;
    return 1;
}

// ---- the hook --------------------------------------------------------------------------------------------------
enum { PERIOD_MS = 1000, AGAIN_MS = 10000, MAX_USERS = 4096, MAX_ITEMS = 1024 };

static const struct game_addrs *g_a;
static uint64_t g_original;
static int g_every;        // MKWMP_NX_FRIENDS=2
// Only the game's thread that runs the manager's update touches what follows.
static NxFriendSet g_set;
static uint64_t g_mgr;     // the manager whose friend client answered (this sign-in); 0: none
static int g_armed;        // the first new accounts of this sign-in are still to be handed to the game
static int g_pending;      // new accounts the game has not been asked to read
static uint64_t g_asked;
static int g_items = -1, g_matched = -1;

static int readable(uint64_t addr, size_t n) { return addr && plat_mem_readable((const void *)(uintptr_t)addr) >= n; }
static uint64_t rd64(uint64_t addr) { uint64_t v; memcpy(&v, (const void *)(uintptr_t)addr, 8); return v; }
static uint32_t rd32(uint64_t addr) { uint32_t v; memcpy(&v, (const void *)(uintptr_t)addr, 4); return v; }

// 1 when mgr is a friend manager of the row: its table holds the row's functions in two slots no hook changes.
// Asked at every update, so an object that passed is remembered (the game's managers live as long as it runs).
static int is_manager(const struct game_addrs *a, uint64_t mgr) {
    static uint64_t known[4];
    static int n;
    for (int i = 0; i < n; i++) if (known[i] == mgr) return 1;
    if (!readable(mgr, (size_t)a->fr_items + 8)) return 0;
    uint64_t table = rd64(mgr);
    int top = a->fr_slot_reload > a->fr_slot_changed ? a->fr_slot_reload : a->fr_slot_changed;
    if (!readable(table, 8u * (size_t)(top + 1)) || rd64(table + 8ull * a->fr_slot_changed) != a->fr_changed ||
        rd64(table + 8ull * a->fr_slot_reload) != a->fr_reload)
        return 0;
    if (n < 4) known[n++] = mgr;
    return 1;
}

// The accounts the server named, from the game's own copy of them (taken under the friend client's lock and freed
// again, both by the game's functions). -1: the copy does not look as expected (nothing is used of it).
static int server_accounts(Cpu *c, const struct game_addrs *a, uint64_t client, uint64_t *ids, int cap) {
    uint64_t vec[4] = {0, 0, 0, 0}, *vecp = vec;   // begin, end, capacity, allocator
    c->x[8] = (uint64_t)(uintptr_t)vec;            // the result register of the call
    guest_call(c, a->fr_users_copy, 1, client, 0, 0, 0);
    int n = -1;
    uint64_t bytes = vec[1] - vec[0];
    if (!vec[0] && !vec[1]) n = 0;
    else if (vec[1] >= vec[0] && bytes <= (uint64_t)MAX_USERS * a->fr_user_size && (!bytes || readable(vec[0], bytes)))
        n = nxfriends_accounts((const uint8_t *)(uintptr_t)vec[0], bytes, a->fr_user_size, a->fr_user_account, ids, cap);
    guest_call(c, a->fr_users_free, 1, (uint64_t)(uintptr_t)&vecp, 0, 0, 0);
    return n;
}

// How many console friends the manager holds, and how many of them the game attached a server user to.
static void log_matched(const struct game_addrs *a, uint64_t mgr) {
    int32_t count = (int32_t)rd32(mgr + a->fr_count);
    uint64_t items = rd64(mgr + a->fr_items);
    if (count < 0 || count > MAX_ITEMS || (count && !readable(items, 8u * (size_t)count))) return;
    int matched = 0;
    uint64_t lo = 0, hi = 0, need = (uint64_t)a->fr_mapped + 1;   // the last readable stretch found (entries are pooled)
    for (int i = 0; i < count; i++) {
        uint64_t e = rd64(items + 8ull * (unsigned)i);
        if (e < lo || e + need > hi) {
            size_t len = e ? plat_mem_readable((const void *)(uintptr_t)e) : 0;
            if (len < need) continue;
            lo = e, hi = e + len;
        }
        matched += *(const uint8_t *)(uintptr_t)(e + a->fr_mapped) == 1;
    }
    if (count == g_items && matched == g_matched) return;
    g_items = count, g_matched = matched;
    mkwmp_log("friends: the game's friend list has %d, %d matched to a player on Nextendo's server", count, matched);
}

// At most once a second per manager object (the game may hold more than one; each has its own turn).
static int due(uint64_t mgr, uint64_t now) {
    static struct { uint64_t mgr, next; } seen[4];
    int at = 0;
    for (int i = 0; i < 4; i++) {
        if (seen[i].mgr == mgr) { at = i; break; }
        if (!seen[i].mgr || seen[i].next < seen[at].next) at = i;   // a free place, else the longest idle one
    }
    if (seen[at].mgr == mgr && now < seen[at].next) return 0;
    seen[at].mgr = mgr;
    seen[at].next = now + PERIOD_MS;
    return 1;
}

static void forget(void) {
    g_set.count = 0;
    friends_set_server(NULL, 0);
    g_mgr = 0;
    g_armed = g_pending = 0;
    g_items = g_matched = -1;
}

static void signed_in(uint64_t mgr) {
    if (g_mgr) return;
    g_mgr = mgr;
    g_armed = 1;
    mkwmp_log("friends: the game's friend service is up on Nextendo: reading the friend accounts its server named");
}

// Read the server's accounts and, when the first ones of the sign-in are new (or, with MKWMP_NX_FRIENDS=2, any), have
// the game take them: its own routine for a console friend list that changed, "is there a friend I do not have?" (it
// reads GetFriendList, which now has the server's accounts), and if so "start over for the signed-in user": the list
// is read again at the manager's next update and the server's friends are attached to it.
static void take(Cpu *c, const struct game_addrs *a, uint64_t mgr, uint64_t client, uint64_t now) {
    uint64_t ids[FRIENDS_MAX];
    int n = server_accounts(c, a, client, ids, FRIENDS_MAX), added = 0;
    if (n < 0) {
        static int said;
        if (!said++) mkwmp_log("friends: the game's copy of its server friends is not as expected: left alone");
        return;
    }
    if (nxfriends_set(&g_set, ids, n, &added)) {
        friends_set_server(g_set.ids, g_set.count);
        mkwmp_log("friends: Nextendo's server names %d friend account(s), %d new", g_set.count, added);
        g_pending |= added > 0;
    }
    if (g_pending && (g_armed || (g_every && now - g_asked >= AGAIN_MS))) {
        g_pending = g_armed = 0;
        g_asked = now;
        int changed = (int)(guest_call(c, a->fr_changed, 1, mgr, 0, 0, 0) & 1);
        int again = changed && (guest_call(c, a->fr_reinit, 1, mgr, 0, 0, 0) & 1);
        mkwmp_log(again ? "friends: the game reads its friend list again (it now holds the server's friends)"
                  : changed ? "friends: the game did not take a new read of its friend list (its next own read has them)"
                  : "friends: the game needs no new read of its friend list (it has them, or has not read it yet)");
    } else if (g_pending && !g_every) {
        g_pending = 0;
        mkwmp_log("friends: new friend accounts after the first ones: in the list at the game's next own read of it");
    }
}

// Before the manager's update: when its start operation has just finished, this update takes the server's first
// answer in and attaches the server's friends to the console's. The accounts are handed over first, so that happens
// once, with them in the list.
static void before_update(Cpu *c, const struct game_addrs *a, uint64_t mgr) {
    if ((g_mgr && mgr != g_mgr) || !is_manager(a, mgr)) return;
    uint64_t client = rd64(mgr + a->fr_client), op = rd64(mgr + a->fr_op);
    if (!client || !op || rd32(mgr + a->fr_state) != 0) return;
    static uint64_t checked_op, done_fn;   // one operation is asked at every update until it has finished
    if (op != checked_op) {
        uint64_t table = readable(op, 8) ? rd64(op) : 0;
        if (!readable(table, (size_t)a->fr_op_done + 8)) return;
        checked_op = op, done_fn = rd64(table + a->fr_op_done);
    }
    if (!done_fn || !(guest_call(c, done_fn, 1, op, 0, 0, 0) & 1)) return;   // still on its way
    checked_op = 0;   // finished: the next operation is looked at anew
    signed_in(mgr);
    take(c, a, mgr, client, plat_ms());
}

// After it, once a second: sign-out, accounts the server named later, and what the game made of them.
static void after_update(Cpu *c, const struct game_addrs *a, uint64_t mgr) {
    if (g_mgr && mgr != g_mgr) return;   // one manager is signed in: the others have nothing to tell
    uint64_t now = plat_ms();
    if (!due(mgr, now) || !is_manager(a, mgr)) return;
    uint64_t client = rd64(mgr + a->fr_client);
    if (!client) {   // signed out: the next sign-in starts with nothing
        if (!g_mgr) return;
        mkwmp_log("friends: the game left Nextendo's friend service: its %d friend account(s) are forgotten", g_set.count);
        forget();
        return;
    }
    if (rd32(mgr + a->fr_state) != 1) return;   // the server's first answer is not in yet (or its stream ended)
    signed_in(mgr);
    take(c, a, mgr, client, now);
    log_matched(a, mgr);
}

static void hook_friend_update(Cpu *c) {
    const struct game_addrs *a = g_a;
    uint64_t mgr = c->x[0], saved[19];
    if (a) {
        memcpy(saved, c->x, sizeof saved);   // x0..x18: what a call may change
        before_update(c, a, mgr);
        memcpy(c->x, saved, sizeof saved);
    }
    guesthook_original(c, g_original);
    uint64_t ret = c->x[0];
    if (a) after_update(c, a, mgr);
    c->x[0] = ret;
}

int nxfriends_install(const struct game_addrs *a) {
    static int tried;
    if (tried) return g_a != NULL;
    tried = 1;
    char v[8] = "";
    if (plat_getenv("MKWMP_NX_FRIENDS", v, sizeof v) && !strcmp(v, "0")) {
        mkwmp_log("friends: off (MKWMP_NX_FRIENDS=0): only friends the launcher has an id for are in the game's list");
        return 0;
    }
    g_every = !strcmp(v, "2");
    if (!a || !a->fr_tick) {
        mkwmp_log("friends: no addresses of the game's friend manager for this build: only friends the launcher has an "
                  "id for are in the game's list");
        return 0;
    }
    if (!g_rt.call || !game_guard_friends(a)) {
        mkwmp_log("friends: guard mismatch, the game's friend manager is NOT hooked: only friends the launcher has an id "
                  "for are in the game's list");
        return 0;
    }
    g_a = a;   // before the hook goes in: it may run at once
    // With a table of host functions each class table gets its slot changed; an override by address covers all three.
    int ok = 0, want = g_rt.host_fn_at ? 3 : 1;
    for (int i = 0; i < want; i++)
        if (a->fr_vtables[i]) ok += guesthook_vtable(a->fr_vtables[i], a->fr_slot_tick, hook_friend_update, &g_original);
    if (!ok) {
        g_a = NULL;
        mkwmp_log("friends: the game's friend manager could not be hooked: only friends the launcher has an id for are in "
                  "the game's list");
        return 0;
    }
    mkwmp_log("friends: the game's friend manager is hooked (%d of %d): the friends Nextendo's server names go into the "
              "game's friend list%s", ok, want, g_every ? " (a new read at every new account)" : "");
    return 1;
}
