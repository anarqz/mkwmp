#include "friends.h"
#include "config.h"
#include "launcher.h"
#include "log.h"
#include "seat.h"
#include "uuid5.h"


#include "plat.h"
#include <stdio.h>
#include <string.h>

_Static_assert(sizeof(GFriend) == 0x200, "nn::friends::Friend size");
_Static_assert(offsetof(GFriend, account_id) == 0x10, "Friend::GetAccountId");
_Static_assert(offsetof(GFriend, nickname) == 0x18, "Friend::GetNickname");
_Static_assert(offsetof(GFriend, presence) == 0x40, "Friend::GetPresence");
_Static_assert(offsetof(GFriend, is_favorite) == 0x120, "Friend::IsFavorite");
_Static_assert(offsetof(GFriend, is_valid) == 0x128, "Friend::IsValid");

// ---- account id ----------------------------------------------------------------
uint64_t friends_account_id(const char *name) {
    char key[11 + FRIEND_NAME_SIZE] = "mkwmp-user/";
    size_t n = 11;
    for (; *name && n < sizeof key; name++)
        key[n++] = *name >= 'A' && *name <= 'Z' ? (char)(*name + 32) : *name;
    uint8_t id[16];
    uuid5(kUuidNamespaceUrl, key, n, id);
    uint64_t v = 0;
    for (int i = 0; i < 8; i++) v = v << 8 | id[i];
    v &= 0x7fffffffffffffffull;
    return v ? v : 1;
}

// ---- the launcher's JSON ---------------------------------------------------------
static const char *skip_ws(const char *p) {
    while (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n') p++;
    return p;
}

// p is at a string's opening quote. Returns the position after its closing quote, or NULL if
// it never closes. With dst, the text is copied and *ok tells whether it was taken whole:
// it fits in n bytes and uses no escape other than \" \\ \/ (names never need more).
static const char *json_string(const char *p, char *dst, size_t n, int *ok) {
    size_t len = 0;
    int whole = 1;
    for (p++; *p != '"'; p++) {
        char ch = *p;
        if (!ch) return NULL;
        if (ch == '\\') {
            ch = *++p;
            if (!ch) return NULL;
            if (ch != '"' && ch != '\\' && ch != '/') whole = 0;
        }
        if (dst && len + 1 < n) dst[len++] = ch;
        else whole = 0;
    }
    if (dst) { dst[len] = 0; *ok = whole; }
    return p + 1;
}

// Skip one JSON value of any kind. Returns NULL if it is cut short.
static const char *json_skip(const char *p) {
    if (*p == '"') return json_string(p, NULL, 0, NULL);
    if (*p != '{' && *p != '[') {
        while (*p && *p != ',' && *p != '}' && *p != ']') p++;
        return p;
    }
    for (int depth = 0;;) {
        if (!*p) return NULL;
        if (*p == '"') {
            if (!(p = json_string(p, NULL, 0, NULL))) return NULL;
            continue;
        }
        if (*p == '{' || *p == '[') depth++;
        else if ((*p == '}' || *p == ']') && --depth == 0) return p + 1;
        p++;
    }
}

// Walk the members of the object at p ('{'). For each one `member` gets the key and the
// position of the value and returns the position after the value (NULL = malformed).
typedef const char *(*MemberFn)(const char *key, const char *value, void *ctx);
static const char *json_object(const char *p, MemberFn member, void *ctx) {
    p = skip_ws(p + 1);
    if (*p == '}') return p + 1;
    for (;;) {
        char key[16];
        int ok;
        if (*p != '"' || !(p = json_string(p, key, sizeof key, &ok))) return NULL;
        p = skip_ws(p);
        if (*p++ != ':') return NULL;
        if (!(p = member(ok ? key : "", skip_ws(p), ctx))) return NULL;
        p = skip_ws(p);
        if (*p == '}') return p + 1;
        if (*p++ != ',') return NULL;
        p = skip_ws(p);
    }
}

// 1..FRIEND_NAME_CHARS characters, no control characters.
static int valid_name(const char *name) {
    int chars = 0;
    for (const unsigned char *p = (const unsigned char *)name; *p; p++) {
        if (*p < 0x20 || *p == 0x7f) return 0;
        chars += (*p & 0xc0) != 0x80;   // count UTF-8 lead bytes
    }
    return chars >= 1 && chars <= FRIEND_NAME_CHARS;
}

typedef struct {
    FriendRoster *roster;
    uint64_t self_id;   // 0 = nobody to exclude
    int need_account;   // 1: only entries that carry their own "account" (Nextendo: no id can be derived from a name);
                        // 2: only entries with a name and no "account" (kept by name, account_id 0)
    int seen;           // a "friends" array was read
} ParseCtx;

// One entry of the array as read: its name, and its "account" when it has one.
typedef struct {
    char name[FRIEND_NAME_SIZE];
    uint64_t account;
    int has_account;   // 0 no "account" member, 1 a good one, -1 one that is not an id
} Entry;

int friends_account_hex(const char *s, uint64_t *out) {
    uint64_t v = 0;
    int n = 0;
    for (; *s; s++, n++) {
        int d = *s >= '0' && *s <= '9' ? *s - '0' : *s >= 'a' && *s <= 'f' ? *s - 'a' + 10 : *s >= 'A' && *s <= 'F' ? *s - 'A' + 10 : -1;
        if (d < 0 || n >= 16) return 0;
        v = v << 4 | (uint64_t)d;
    }
    if (!n || !v) return 0;
    *out = v;
    return 1;
}

static void roster_add(ParseCtx *cx, const Entry *en) {
    FriendRoster *r = cx->roster;
    const char *name = en->name;
    uint64_t id;
    if (r->count >= FRIENDS_MAX || en->has_account < 0) return;
    if (cx->need_account == 2) {
        if (en->has_account || !valid_name(name)) return;
        for (int i = 0; i < r->count; i++) if (!strcmp(r->e[i].name, name)) return;
        r->e[r->count].account_id = 0;
        memcpy(r->e[r->count].name, name, strlen(name) + 1);
        r->count++;
        return;
    }
    if (en->has_account) {
        id = en->account;                         // the launcher knows the id: used as it is
        if (!valid_name(name)) name = "Player";   // the id is what the game matches; the name is only shown
    } else {
        if (cx->need_account || !valid_name(name)) return;
        id = friends_account_id(name);
    }
    if (id == cx->self_id) return;
    for (int i = 0; i < r->count; i++) if (r->e[i].account_id == id) return;
    r->e[r->count].account_id = id;
    memcpy(r->e[r->count].name, name, strlen(name) + 1);
    r->count++;
}

static const char *friend_member(const char *key, const char *value, void *ctx) {
    Entry *en = ctx;
    int ok;
    if (!strcmp(key, "account")) {
        char hex[20];
        en->has_account = -1;
        if (*value != '"') return json_skip(value);
        value = json_string(value, hex, sizeof hex, &ok);
        if (value && ok && friends_account_hex(hex, &en->account)) en->has_account = 1;
        return value;
    }
    if (strcmp(key, "name") || *value != '"') return json_skip(value);
    value = json_string(value, en->name, FRIEND_NAME_SIZE, &ok);
    if (!ok) en->name[0] = 0;
    return value;
}

static const char *root_member(const char *key, const char *value, void *ctx) {
    ParseCtx *cx = ctx;
    if (strcmp(key, "friends") || *value != '[') return json_skip(value);
    cx->seen = 1;
    const char *p = skip_ws(value + 1);
    if (*p == ']') return p + 1;
    for (;;) {
        if (*p == '{') {
            Entry en;
            en.name[0] = 0;
            en.account = 0;
            en.has_account = 0;
            if (!(p = json_object(p, friend_member, &en))) return NULL;
            roster_add(cx, &en);
        } else if (!(p = json_skip(p))) {
            return NULL;
        }
        p = skip_ws(p);
        if (*p == ']') return p + 1;
        if (*p++ != ',') return NULL;
        p = skip_ws(p);
    }
}

static int parse(int http_status, const char *body, uint64_t self_id, int need_account, FriendRoster *out) {
    if (http_status != 200 || !body) return 0;
    FriendRoster roster;
    roster.count = 0;
    ParseCtx cx = {&roster, self_id, need_account, 0};
    const char *p = skip_ws(body);
    if (*p != '{' || !json_object(p, root_member, &cx) || !cx.seen) return 0;
    memcpy(out, &roster, sizeof roster);
    return 1;
}

int friends_parse(int http_status, const char *body, const char *self, FriendRoster *out) {
    return parse(http_status, body, self && self[0] ? friends_account_id(self) : 0, 0, out);
}

int friends_parse_accounts(int http_status, const char *body, FriendRoster *out) {
    return parse(http_status, body, 0, 1, out);
}

int friends_parse_names(int http_status, const char *body, FriendRoster *out) {
    return parse(http_status, body, 0, 2, out);
}

int friends_merge(const FriendRoster *known, const FriendRoster *names, const uint64_t *ids, int n, FriendRoster *out) {
    FriendRoster r = *known;
    if (r.count > FRIENDS_MAX) r.count = FRIENDS_MAX;
    if (r.count < 0) r.count = 0;
    int base = r.count;
    for (int i = 0; i < n && r.count < FRIENDS_MAX; i++) {
        int have = !ids[i];
        for (int k = 0; k < r.count && !have; k++) have = r.e[k].account_id == ids[i];
        if (have) continue;
        r.e[r.count].account_id = ids[i];
        snprintf(r.e[r.count].name, sizeof r.e[r.count].name, "Friend %d", r.count - base + 1);
        r.count++;
    }
    int added = r.count - base;
    if (added == 1 && names && names->count == 1)
        memcpy(r.e[base].name, names->e[0].name, sizeof r.e[base].name);
    memcpy(out, &r, sizeof r);
    return added;
}

// ---- nn::friends::Friend -----------------------------------------------------------
// Copy as much of src as fits in n bytes without splitting a UTF-8 sequence.
static void copy_utf8(char *dst, size_t n, const char *src) {
    size_t len = strlen(src);
    if (len >= n) {
        len = n - 1;
        while (len > 0 && ((unsigned char)src[len] & 0xc0) == 0x80) len--;
    }
    memcpy(dst, src, len);
    dst[len] = 0;
}

int friends_page(const FriendRoster *roster, const uint64_t owner[2], int offset, int count, GFriend *out) {
    int n = 0;
    if (offset < 0 || !out) return 0;
    for (int i = offset; i < roster->count && n < count; i++, n++) {
        GFriend *f = &out[n];
        memset(f, 0, sizeof *f);
        if (owner) memcpy(f->owner, owner, sizeof f->owner);
        f->account_id = roster->e[i].account_id;
        copy_utf8(f->nickname, sizeof f->nickname, roster->e[i].name);
        f->is_valid = 1;
    }
    return n;
}

// ---- cached roster, refreshed in the background ------------------------------------
enum { REFRESH_MS = 15000 };

static PlatLock g_lock = PLAT_LOCK_INIT;
static FriendRoster g_latest;    // last good launcher answer
static FriendRoster g_serving;   // the snapshot the game is paging through
static FriendRoster g_names;     // Nextendo: the launcher's entries without an id (names only)
static uint64_t g_server_ids[FRIENDS_MAX];   // Nextendo: the accounts the game's server named (friends_set_server)
static int g_server_n;
static PlatEvent *g_wake;          // auto-reset: the game went online
static PlatEvent *g_answered;      // manual-reset: the refresher is up to date since friends_online

int friends_list(const uint64_t owner[2], int offset, int count, GFriend *out) {
    plat_lock(&g_lock);
    if (offset <= 0) {
        if (g_server_n) friends_merge(&g_latest, &g_names, g_server_ids, g_server_n, &g_serving);
        else g_serving = g_latest;
    }
    int n = friends_page(&g_serving, owner, offset, count, out);
    plat_unlock(&g_lock);
    return n;
}

void friends_set_server(const uint64_t *ids, int n) {
    if (n < 0 || !ids) n = 0;
    if (n > FRIENDS_MAX) n = FRIENDS_MAX;
    plat_lock(&g_lock);
    if (n) memcpy(g_server_ids, ids, (size_t)n * sizeof ids[0]);
    g_server_n = n;
    plat_unlock(&g_lock);
}

int friends_name_of(uint64_t account, char *out, size_t cap) {
    int found = 0;
    plat_lock(&g_lock);
    for (int i = 0; i < g_latest.count && !found; i++)
        if (g_latest.e[i].account_id == account) snprintf(out, cap, "%s", g_latest.e[i].name), found = 1;
    plat_unlock(&g_lock);
    return found;
}

int friends_accounts(uint64_t *out, int cap) {
    int n = 0;
    plat_lock(&g_lock);
    for (int i = 0; i < g_latest.count && n < cap; i++) out[n++] = g_latest.e[i].account_id;
    plat_unlock(&g_lock);
    return n;
}

// Returns 1 when the launcher answered with a roster.
static int refresh(void) {
    static char buf[16384];   // 100 names fit several times over; only this thread uses it
    static FriendRoster roster, names;
    static int last_status = 200, last_count = -1;
    const char *body;
    int status = launcher_request("GET", "/local/v1/friends", buf, sizeof buf, &body);
    // Nextendo: the friends the launcher gives an id for (it leaves the player out itself), used as they are; an
    // entry without one is only a name, for a friend the game's server names (friends_merge).
    int nextendo = mkwmp_config_nextendo();
    int ok = nextendo ? friends_parse_accounts(status, body, &roster)
                      : friends_parse(status, body, mkwmp_config()->name, &roster);
    names.count = 0;
    if (ok && nextendo) friends_parse_names(status, body, &names);
    if (!ok) {
        // Keep the last good roster: a launcher hiccup must not empty the friend list.
        if (status != last_status) mkwmp_log("friends: launcher answered %d, keeping %d in the list", status, g_latest.count);
        last_status = status;
        return 0;
    }
    last_status = 200;
    plat_lock(&g_lock);
    g_latest = roster;
    g_names = names;
    plat_unlock(&g_lock);
    if (roster.count != last_count) mkwmp_log("friends: %d in the launcher roster", roster.count);
    last_count = roster.count;
    return 1;
}

static void refresher(void *unused) {
    (void)unused;
    uint64_t last = 0;
    int fetched = 0, good = 0;
    for (;;) {
        uint32_t wait = REFRESH_MS;
        if (!seat_keepalive_paused()) {   // paused = the player left online
            uint64_t now = plat_ms();
            if (!fetched || !good || now - last >= REFRESH_MS) {
                good |= refresh();
                last = plat_ms();
                fetched = 1;
                // No roster yet (launcher still loading: 503): the game reads its friend list once,
                // ~2.5 s after the sign-in, so ask again in a second, not in 15 s.
                if (!good) wait = 1000;
            } else {
                wait = (uint32_t)(REFRESH_MS - (now - last));   // going online again within 15 s
            }
        }
        plat_event_set(g_answered);
        plat_event_wait(g_wake, wait);
    }
}

void friends_online(int wait_ms) {
    static PlatLock start_lock = PLAT_LOCK_INIT;
    if (!mkwmp_config_has_seat(mkwmp_config())) return;
    static int started;
    plat_lock(&start_lock);
    if (!started) {
        // The thread fetches at once; the events exist before it runs.
        g_wake = plat_event_new(0);
        g_answered = plat_event_new(1);
        if (g_wake && g_answered && plat_thread_start(refresher, NULL)) started = 1;
        else mkwmp_log("friends: could not start the refresh thread");
    } else {
        plat_event_reset(g_answered);
        plat_event_set(g_wake);
    }
    int wait = started && wait_ms > 0;
    plat_unlock(&start_lock);
    if (wait) plat_event_wait(g_answered, (uint32_t)wait_ms);
}
