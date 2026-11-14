#include "friends.h"
#include "config.h"
#include "launcher.h"
#include "log.h"
#include "seat.h"
#include "uuid5.h"

#include <windows.h>
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
    int seen;           // a "friends" array was read
} ParseCtx;

static void roster_add(ParseCtx *cx, const char *name) {
    FriendRoster *r = cx->roster;
    if (r->count >= FRIENDS_MAX || !valid_name(name)) return;
    uint64_t id = friends_account_id(name);
    if (id == cx->self_id) return;
    for (int i = 0; i < r->count; i++) if (r->e[i].account_id == id) return;
    r->e[r->count].account_id = id;
    memcpy(r->e[r->count].name, name, strlen(name) + 1);
    r->count++;
}

static const char *friend_member(const char *key, const char *value, void *ctx) {
    char *name = ctx;
    int ok;
    if (strcmp(key, "name") || *value != '"') return json_skip(value);
    value = json_string(value, name, FRIEND_NAME_SIZE, &ok);
    if (!ok) name[0] = 0;
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
            char name[FRIEND_NAME_SIZE] = "";
            if (!(p = json_object(p, friend_member, name))) return NULL;
            roster_add(cx, name);
        } else if (!(p = json_skip(p))) {
            return NULL;
        }
        p = skip_ws(p);
        if (*p == ']') return p + 1;
        if (*p++ != ',') return NULL;
        p = skip_ws(p);
    }
}

int friends_parse(int http_status, const char *body, const char *self, FriendRoster *out) {
    if (http_status != 200 || !body) return 0;
    FriendRoster roster;
    roster.count = 0;
    ParseCtx cx = {&roster, self && self[0] ? friends_account_id(self) : 0, 0};
    const char *p = skip_ws(body);
    if (*p != '{' || !json_object(p, root_member, &cx) || !cx.seen) return 0;
    memcpy(out, &roster, sizeof roster);
    return 1;
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

static SRWLOCK g_lock = SRWLOCK_INIT;
static FriendRoster g_latest;    // last good launcher answer
static FriendRoster g_serving;   // the snapshot the game is paging through
static HANDLE g_wake;            // auto-reset: the game went online
static HANDLE g_answered;        // manual-reset: the refresher is up to date since friends_online

int friends_list(const uint64_t owner[2], int offset, int count, GFriend *out) {
    AcquireSRWLockExclusive(&g_lock);
    if (offset <= 0) g_serving = g_latest;
    int n = friends_page(&g_serving, owner, offset, count, out);
    ReleaseSRWLockExclusive(&g_lock);
    return n;
}

static void refresh(void) {
    static char buf[16384];   // 100 names fit several times over; only this thread uses it
    static FriendRoster roster;
    static int last_status = 200, last_count = -1;
    const char *body;
    int status = launcher_request("GET", "/local/v1/friends", buf, sizeof buf, &body);
    if (!friends_parse(status, body, mkwmp_config()->name, &roster)) {
        // Keep the last good roster: a launcher hiccup must not empty the friend list.
        if (status != last_status) mkwmp_log("friends: launcher answered %d, keeping %d in the list", status, g_latest.count);
        last_status = status;
        return;
    }
    last_status = 200;
    AcquireSRWLockExclusive(&g_lock);
    g_latest = roster;
    ReleaseSRWLockExclusive(&g_lock);
    if (roster.count != last_count) mkwmp_log("friends: %d in the launcher roster", roster.count);
    last_count = roster.count;
}

static DWORD WINAPI refresher(LPVOID unused) {
    (void)unused;
    ULONGLONG last = 0;
    int fetched = 0;
    for (;;) {
        DWORD wait = REFRESH_MS;
        if (!seat_keepalive_paused()) {   // paused = the player left online
            ULONGLONG now = GetTickCount64();
            if (!fetched || now - last >= REFRESH_MS) {
                refresh();
                last = GetTickCount64();
                fetched = 1;
            } else {
                wait = (DWORD)(REFRESH_MS - (now - last));   // going online again within 15 s
            }
        }
        SetEvent(g_answered);
        WaitForSingleObject(g_wake, wait);
    }
    return 0;
}

void friends_online(int wait_ms) {
    static SRWLOCK start_lock = SRWLOCK_INIT;
    if (!mkwmp_config()->launcher_port) return;
    static int started;
    AcquireSRWLockExclusive(&start_lock);
    if (!started) {
        // The thread fetches at once; the events exist before it runs.
        g_wake = CreateEventW(NULL, FALSE, FALSE, NULL);
        g_answered = CreateEventW(NULL, TRUE, FALSE, NULL);
        HANDLE thread = g_wake && g_answered ? CreateThread(NULL, 0, refresher, NULL, 0, NULL) : NULL;
        if (thread) { CloseHandle(thread); started = 1; }
        else mkwmp_log("friends: could not start the refresh thread");
    } else {
        ResetEvent(g_answered);
        SetEvent(g_wake);
    }
    int wait = started && wait_ms > 0;
    ReleaseSRWLockExclusive(&start_lock);
    if (wait) WaitForSingleObject(g_answered, (DWORD)wait_ms);
}
