#include "rosterhook.h"

#include "config.h"
#include "guesthook.h"
#include "launcher.h"
#include "log.h"
#include "plat.h"
#include "roster.h"

#include <stdio.h>
#include <string.h>

enum {
    WALK_MS = 200,       // the tree is read this often, not at every frame (and at once when a match starts)
    CHANGE_MS = 1000,    // "change" at most once a second: the latest set wins
    QUEUE = 4,
    MISSING_MAX = 3,     // this many 404s: a launcher without Previous races, nothing more is sent
};
enum { EV_CHANGE, EV_MATCH, EV_LEFT };

static const struct game_addrs *g_a;
static uint64_t g_orig_update, g_orig_match[2];

// ---- guest memory, with the last readable stretches remembered for one read of the tree (plat_mem_readable is a
// VirtualQuery, or a scan of /proc/self/maps: too dear for every node)
typedef struct { uint64_t lo[4], hi[4]; unsigned next; } Seen;

static const void *mem_at(void *user, uint64_t addr, size_t n) {
    Seen *s = user;
    if (!addr || addr + n < addr) return NULL;
    for (int i = 0; i < 4; i++)
        if (addr >= s->lo[i] && addr + n <= s->hi[i]) return (const void *)(uintptr_t)addr;
    size_t len = plat_mem_readable((const void *)(uintptr_t)addr);
    if (len < n) return NULL;
    s->lo[s->next & 3] = addr, s->hi[s->next & 3] = addr + len;
    s->next++;
    return (const void *)(uintptr_t)addr;
}

static uint64_t peek(Seen *s, uint64_t addr, size_t n) {   // a u32 or u64; 0 when it cannot be read
    uint64_t v = 0;
    const void *p = mem_at(s, addr, n);
    if (p) memcpy(&v, p, n);
    return v;
}

// ---- the sender: its own thread, so the game's never waits for the launcher --------------------------------------
typedef struct {
    int event;
    char key[ROSTER_KEY_CAP];
    Roster r;
} Item;

static Item g_queue[QUEUE];
static int g_head, g_count;
static PlatLock g_lock = PLAT_LOCK_INIT;
static PlatEvent *g_wake;
static volatile int32_t g_off;   // nobody to tell: the game's thread stops copying

static void copy_roster(Roster *to, const Roster *from) {
    to->count = from ? from->count : 0;
    if (to->count) memcpy(to->rows, from->rows, (size_t)to->count * sizeof from->rows[0]);
}

static void push(int event, const char *key, const Roster *r) {
    if (plat_load(&g_off) || !g_wake) return;
    plat_lock(&g_lock);
    Item *tail = g_count ? &g_queue[(g_head + g_count - 1) % QUEUE] : NULL, *it;
    if (tail && ((event == EV_CHANGE && tail->event == EV_CHANGE) || g_count == QUEUE)) it = tail;   // the latest wins
    else it = &g_queue[(g_head + g_count++) % QUEUE];
    it->event = event;
    snprintf(it->key, sizeof it->key, "%s", key ? key : "");
    copy_roster(&it->r, r);
    plat_unlock(&g_lock);
    plat_event_set(g_wake);
}

static void sender(void *arg) {
    (void)arg;
    static const char *const kEvent[] = {"change", "match", "left"};
    static Item it;
    static char json[ROSTER_JSON_MAX];
    uint64_t change_at = 0;
    int last = -2, missing = 0;
    for (;;) {
        uint32_t wait = PLAT_FOREVER;
        int have = 0;
        plat_lock(&g_lock);
        if (g_count) {
            const Item *head = &g_queue[g_head];
            uint64_t now = plat_ms();
            if (head->event == EV_CHANGE && now < change_at) wait = (uint32_t)(change_at - now);
            else {
                it.event = head->event;
                memcpy(it.key, head->key, sizeof it.key);
                copy_roster(&it.r, &head->r);
                g_head = (g_head + 1) % QUEUE, g_count--;
                have = 1;
            }
        }
        plat_unlock(&g_lock);
        if (!have) { plat_event_wait(g_wake, wait); continue; }
        if (plat_load(&g_off)) continue;
        if (mkwmp_config()->direct) {   // the mod alone: its own seat client answers /local/v1, and keeps no races
            plat_store(&g_off, 1);
            mkwmp_log("roster: no MKWMP launcher runs this game: its lobbies are not kept (Previous races)");
            continue;
        }
        int len = roster_json(kEvent[it.event], it.event == EV_MATCH ? it.key : NULL, &it.r, json, sizeof json);
        if (len < 0) continue;
        char buf[512];
        const char *body;
        int st = launcher_request_json("POST", "/local/v1/roster", json, buf, sizeof buf, &body);
        if (it.event == EV_CHANGE) change_at = plat_ms() + CHANGE_MS;
        missing = st == 404 ? missing + 1 : 0;
        if (st == -1 || missing >= MISSING_MAX) {
            plat_store(&g_off, 1);
            mkwmp_log(st == -1 ? "roster: no launcher to tell: the lobbies of this session are not kept (Previous races)"
                      : "roster: this launcher has no Previous races (HTTP 404): nothing more is sent this session");
        } else if (st != last) {
            if (st == 204 || st == 200) mkwmp_log("roster: the launcher keeps this session's lobbies (Previous races)");
            else mkwmp_log("roster: the launcher did not take the lobby (HTTP %d; 0: not reachable)", st);
        }
        last = st;
    }
}

// ---- the game's side ---------------------------------------------------------------------------------------------
// Only the thread that runs the controller's update touches these.
static Roster g_cur;
static uint64_t g_empty_hash, g_last_hash, g_next_walk;
static int32_t g_seen_left;
// Shared with the threads of roster_online / roster_left and of the referee request.
static volatile int32_t g_open;    // the launcher was told of a lobby since the last "left"
static volatile int32_t g_hold;    // left online: quiet until the tree has emptied or the player is online again
static volatile int32_t g_left;    // counts roster_left calls
static volatile int32_t g_match;   // a match started: g_match_key is to be sent with the roster
static PlatLock g_match_lock = PLAT_LOCK_INIT;
static char g_match_key[ROSTER_KEY_CAP], g_last_key[ROSTER_KEY_CAP];

// The online context the game itself would use: its getter's two globals (LOBBY-ROSTER-1.8.0.md §3). 0: none.
static uint64_t context(const struct game_addrs *a, Seen *s) {
    uint64_t ctrl = peek(s, a->ro_ctrl_g, 8), mgr = peek(s, a->ro_mgr_g, 8);
    uint64_t sessions = mgr ? peek(s, mgr + a->ro_mgr, 8) : 0;
    if (!ctrl || !sessions || !mem_at(s, sessions + a->ro_cur, 4)) return 0;
    uint32_t slot = (uint32_t)peek(s, sessions + a->ro_cur, 4);
    return slot < 2 ? peek(s, ctrl + a->ro_ctx + 8ull * slot, 8) : 0;
}

static int locals(const Roster *r) {
    int n = 0;
    for (int i = 0; i < r->count; i++) n += r->rows[i].local;
    return n;
}

static void after_update(const struct game_addrs *a) {
    if (plat_load(&g_off)) return;
    uint64_t now = plat_ms();
    int match = plat_load(&g_match) != 0;
    if (!match && now < g_next_walk) return;
    g_next_walk = now + WALK_MS;
    Seen seen = {0};
    RosterMem mem = {mem_at, &seen};
    int n = roster_walk(&mem, &a->rl, context(a, &seen), &g_cur);
    char key[ROSTER_KEY_CAP] = "";
    if (match) {
        plat_lock(&g_match_lock);
        memcpy(key, g_match_key, sizeof key);
        plat_store(&g_match, 0);
        plat_unlock(&g_match_lock);
    }
    if (n < 0) {
        static int said;
        if (said < 3 && ++said) mkwmp_log("roster: the lobby's member tree is not as expected: not read this time");
        if (match) mkwmp_log("roster: a match starts, but its members could not be read: not sent");
        return;
    }
    int32_t left = plat_load(&g_left);
    if (left != g_seen_left) g_seen_left = left, g_last_hash = g_empty_hash;   // what comes after "left" is new
    uint64_t hash = roster_hash(&g_cur);
    if (match) {   // once per match, whatever was sent before: the members are complete here
        plat_store(&g_hold, 0);
        plat_store(&g_open, 1);
        g_last_hash = hash;
        push(EV_MATCH, key, &g_cur);
        mkwmp_event("roster: a match starts with %d member(s), %d on this console", n, locals(&g_cur));
        return;
    }
    if (plat_load(&g_hold)) {
        if (n) return;
        plat_store(&g_hold, 0);
    }
    if (hash == g_last_hash) return;
    g_last_hash = hash;
    if (!n && !plat_load(&g_open)) return;   // offline, or between lobbies before the first one: nothing to tell
    plat_store(&g_open, 1);
    push(EV_CHANGE, NULL, &g_cur);
    mkwmp_log("roster: the lobby has %d member(s), %d on this console", n, locals(&g_cur));
}

static void hook_update(Cpu *c) {
    const struct game_addrs *a = g_a;
    uint64_t controller = c->x[0];
    guesthook_original(c, g_orig_update);
    if (!a) return;
    static int said;
    if (!said) {
        said = 1;
        Seen seen = {0};
        mkwmp_log("roster: the online race controller's update runs: the lobby's members are read after it%s",
                  controller == peek(&seen, a->ro_ctrl_g, 8) ? "" : " (not the game's global controller)");
    }
    after_update(a);   // reads only: the registers stay as the game's function left them
}

// The referee request went out: the match's key is in the node. The roster itself is read by the next update, on the
// thread that owns the tree.
static void match_sent(const struct game_addrs *a, uint64_t node) {
    Seen seen = {0};
    RosterMem mem = {mem_at, &seen};
    char key[ROSTER_KEY_CAP];
    if (!node || !roster_match_key(&mem, node + a->ro_match_key, key)) {
        static volatile int32_t said;
        if (plat_inc(&said) <= 3) mkwmp_log("roster: a match starts, but its key is not where expected: no match marker");
        return;
    }
    plat_lock(&g_match_lock);
    int fresh = strcmp(key, g_last_key) != 0;
    if (fresh) {
        memcpy(g_last_key, key, sizeof key);
        memcpy(g_match_key, key, sizeof key);
        plat_store(&g_match, 1);
    }
    plat_unlock(&g_match_lock);
}

static void hook_match(Cpu *c, int which) {
    const struct game_addrs *a = g_a;
    uint64_t node = c->x[0];
    guesthook_original(c, g_orig_match[which]);
    if (a && !plat_load(&g_off)) match_sent(a, node);
}
static void hook_match_0(Cpu *c) { hook_match(c, 0); }
static void hook_match_1(Cpu *c) { hook_match(c, 1); }

void roster_online(void) { plat_store(&g_hold, 0); }

void roster_left(void) {
    if (!g_a) return;
    plat_store(&g_hold, 1);
    plat_inc(&g_left);
    if (!plat_xchg(&g_open, 0)) return;
    push(EV_LEFT, NULL, NULL);
    mkwmp_event("roster: left online: the launcher closes this session's lobbies");
}

int roster_install(const struct game_addrs *a) {
    static int tried;
    if (tried) return g_a != NULL;
    tried = 1;
    char v[8] = "";
    if (plat_getenv("MKWMP_NO_ROSTER", v, sizeof v) && !strcmp(v, "1")) {
        mkwmp_log("roster: off (MKWMP_NO_ROSTER=1): the launcher's Previous races get no lobbies from this game");
        return 0;
    }
    if (!a || !a->ro_update) {
        mkwmp_log("roster: no addresses of the lobby's member tree for this build: Previous races stay empty");
        return 0;
    }
    if (!g_rt.call || (!g_rt.host_fn_at && !g_rt.call_original) || !game_guard_roster(a)) {
        mkwmp_log("roster: guard mismatch, the lobby's member tree is NOT read: Previous races stay empty");
        return 0;
    }
    g_cur.count = 0;
    g_empty_hash = g_last_hash = roster_hash(&g_cur);
    g_wake = plat_event_new(0);
    if (!g_wake) {
        mkwmp_log("roster: no event for the sender: Previous races stay empty");
        return 0;
    }
    g_a = a;   // before the hooks go in: they may run at once
    if (!guesthook_vtable(a->ro_vtable, a->ro_slot, hook_update, &g_orig_update)) {
        g_a = NULL;
        mkwmp_log("roster: the online race controller could not be hooked: Previous races stay empty");
        return 0;
    }
    // The match marker. With a table of host functions every slot that holds a sender is changed; an override by
    // address covers them all.
    static const GuestFn kMatch[2] = {hook_match_0, hook_match_1};
    int ok = 0, want = 0;
    for (int f = 0; f < 2; f++)
        for (int i = 0; i < 6 && a->ro_match_slots[f][i]; i++) {
            if (i && !g_rt.host_fn_at) break;
            want++;
            ok += guesthook_vtable(a->ro_match_slots[f][i], 0, kMatch[f], &g_orig_match[f]);
        }
    if (!plat_thread_start(sender, NULL)) {
        plat_store(&g_off, 1);   // the hooks stay, and do nothing
        mkwmp_log("roster: no thread for the sender: Previous races stay empty");
        return 0;
    }
    mkwmp_log("roster: the lobby's members are read after the online race controller's update (match marker: %d of %d "
              "hooks) and sent to the launcher (Previous races)", ok, want);
    return 1;
}
