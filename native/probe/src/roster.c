#include "roster.h"

#include <stdio.h>
#include <string.h>

static uint64_t rd64(const uint8_t *p) { uint64_t v; memcpy(&v, p, 8); return v; }
static uint32_t rd32(const uint8_t *p) { uint32_t v; memcpy(&v, p, 4); return v; }

// ---- the member tree ---------------------------------------------------------------------------------------------
enum { DEPTH_MAX = 48 };   // a balanced tree of ROSTER_NODES_MAX nodes is 16 deep at most

static size_t node_bytes(const struct roster_layout *l) {
    size_t n = 0;
    const size_t ends[] = {l->left + 8u, l->right + 8u, l->key + 4u, l->local + 1u, l->ussid + 4u, l->uid + 12u,
                           l->st + 4u, l->name + 12u, l->sid + 8u, l->lid + 1u};
    for (size_t i = 0; i < sizeof ends / sizeof ends[0]; i++) if (ends[i] > n) n = ends[i];
    return n;
}

// One of the game's strings (char * at +0, capacity at +8) into out[max], never past the capacity or max.
static void read_string(const RosterMem *m, const uint8_t *field, char *out, size_t max) {
    out[0] = 0;
    uint64_t p = rd64(field);
    size_t cap = rd32(field + 8);
    if (!p || !cap) return;
    if (cap > max) cap = max;
    const char *s = m->at(m->user, p, cap);
    if (!s) return;
    size_t len = 0;
    while (len < cap - 1 && s[len]) len++;
    memcpy(out, s, len);
    out[len] = 0;
}

int roster_walk(const RosterMem *m, const struct roster_layout *l, uint64_t ctx, Roster *out) {
    out->count = 0;
    if (!ctx) return 0;
    size_t head = (l->root + 8u > l->count + 4u) ? l->root + 8u : l->count + 4u, need = node_bytes(l);
    const uint8_t *c = m->at(m->user, ctx, head);
    if (!c) return -1;
    uint64_t cur = rd64(c + l->root);
    uint32_t count = rd32(c + l->count), visited = 0, last = 0;
    if (count > ROSTER_NODES_MAX) return -1;
    const uint8_t *stack[DEPTH_MAX];
    int depth = 0;
    while ((cur || depth) && visited < count) {
        for (; cur; depth++) {   // down the left side
            if (depth >= DEPTH_MAX) return -1;
            const uint8_t *n = m->at(m->user, cur, need);
            if (!n) return -1;
            stack[depth] = n;
            cur = rd64(n + l->left);
        }
        const uint8_t *n = stack[--depth];
        uint32_t key = rd32(n + l->key);
        if ((visited && key <= last) || rd32(n + l->ussid) != key) return -1;   // not the tree we know
        last = key;
        visited++;
        cur = rd64(n + l->right);
        if (rd32(n + l->st) != 2 || out->count >= ROSTER_MAX) continue;   // not present (yet, or any more)
        RosterRow *r = &out->rows[out->count++];
        r->ussid = key;
        r->local = n[l->local] != 0;
        r->lid = n[l->lid];
        r->sid = rd64(n + l->sid);
        read_string(m, n + l->uid, r->uid, sizeof r->uid);
        read_string(m, n + l->name, r->name, sizeof r->name);
    }
    return out->count;
}

uint64_t roster_hash(const Roster *r) {
    uint64_t h = 0xcbf29ce484222325ull;   // FNV-1a
#define MIX(byte) (h = (h ^ (uint8_t)(byte)) * 0x100000001b3ull)
    MIX(r->count);
    for (int i = 0; i < r->count; i++) {
        const RosterRow *row = &r->rows[i];
        for (int k = 0; k < 4; k++) MIX(row->ussid >> (8 * k));
        for (const char *s = row->uid; *s; s++) MIX(*s);
        MIX(0);
        for (const char *s = row->name; *s; s++) MIX(*s);
        MIX(0);
    }
#undef MIX
    return h;
}

// ---- the match key -----------------------------------------------------------------------------------------------
static const char *digits(const char *s, int sign, int hex) {
    if (sign && *s == '-') s++;
    const char *from = s;
    while ((*s >= '0' && *s <= '9') || (hex && ((*s >= 'a' && *s <= 'f') || (*s >= 'A' && *s <= 'F')))) s++;
    return s > from && s - from <= 10 ? s : NULL;
}

int roster_key_ok(const char *s) {
    for (int field = 0; field < 5; field++) {
        s = digits(s, field >= 1 && field <= 3, field == 4);
        if (!s || *s != (field < 4 ? '_' : 0)) return 0;
        s++;
    }
    return 1;
}

static int key_from(const char *s, size_t n, char out[ROSTER_KEY_CAP]) {
    size_t len = 0;
    while (len < n && s[len]) len++;
    if (len >= n || len >= ROSTER_KEY_CAP) return 0;   // no end in sight
    memcpy(out, s, len + 1);
    return roster_key_ok(out);
}

int roster_match_key(const RosterMem *m, uint64_t at, char out[ROSTER_KEY_CAP]) {
    out[0] = 0;
    const uint8_t *f = at ? m->at(m->user, at, 12) : NULL;
    if (f) {   // a string object
        uint64_t p = rd64(f);
        size_t cap = rd32(f + 8);
        if (cap > ROSTER_KEY_CAP) cap = ROSTER_KEY_CAP;
        const char *s = p && cap ? m->at(m->user, p, cap) : NULL;
        if (s && key_from(s, cap, out)) return 1;
    }
    const char *s = at ? m->at(m->user, at, ROSTER_KEY_CAP) : NULL;   // the characters themselves
    if (s && key_from(s, ROSTER_KEY_CAP, out)) return 1;
    out[0] = 0;
    return 0;
}

// ---- JSON --------------------------------------------------------------------------------------------------------
// Bytes of the well-formed UTF-8 sequence at s (1 to 4), 0 when there is none (s is terminated: a missing byte is 0).
static int utf8_sequence(const unsigned char *s) {
#define TAIL(i) (s[i] >= 0x80 && s[i] <= 0xbf)
    unsigned char c = s[0];
    if (c < 0x80) return 1;
    if (c >= 0xc2 && c <= 0xdf) return TAIL(1) ? 2 : 0;
    if (c >= 0xe0 && c <= 0xef) {
        unsigned char lo = c == 0xe0 ? 0xa0 : 0x80, hi = c == 0xed ? 0x9f : 0xbf;   // no overlong form, no surrogate
        return s[1] >= lo && s[1] <= hi && TAIL(2) ? 3 : 0;
    }
    if (c >= 0xf0 && c <= 0xf4) {
        unsigned char lo = c == 0xf0 ? 0x90 : 0x80, hi = c == 0xf4 ? 0x8f : 0xbf;   // U+10000 to U+10FFFF
        return s[1] >= lo && s[1] <= hi && TAIL(2) && TAIL(3) ? 4 : 0;
    }
    return 0;
#undef TAIL
}

int roster_json_escape(const char *text, char *out, size_t cap) {
    static const char hex[] = "0123456789abcdef";
    const unsigned char *s = (const unsigned char *)text;
    size_t n = 0;
    while (*s) {
        if (n + 7 > cap) return -1;   // the longest step and the terminator
        int k = utf8_sequence(s);
        if (!k) { out[n++] = '?'; s++; continue; }
        if (k > 1) { memcpy(out + n, s, (size_t)k); n += (size_t)k; s += k; continue; }
        unsigned char c = *s++;
        if (c == '"' || c == '\\') out[n++] = '\\', out[n++] = (char)c;
        else if (c < 0x20 || c == 0x7f) {
            memcpy(out + n, "\\u00", 4);
            out[n + 4] = hex[c >> 4], out[n + 5] = hex[c & 15];
            n += 6;
        } else out[n++] = (char)c;
    }
    if (n + 1 > cap) return -1;
    out[n] = 0;
    return (int)n;
}

typedef struct { char *out; size_t cap, n; int bad; } Buf;

static void put(Buf *b, const char *s) {
    size_t len = strlen(s);
    if (b->bad || b->n + len + 1 > b->cap) { b->bad = 1; return; }
    memcpy(b->out + b->n, s, len + 1);
    b->n += len;
}

static void put_string(Buf *b, const char *s) {
    put(b, "\"");
    int k = b->bad ? -1 : roster_json_escape(s, b->out + b->n, b->cap - b->n);
    if (k < 0) b->bad = 1;
    else b->n += (size_t)k;
    put(b, "\"");
}

int roster_json(const char *event, const char *match, const Roster *r, char *out, size_t cap) {
    Buf b = {out, cap, 0, cap == 0};
    char num[64];
    put(&b, "{\"event\":");
    put_string(&b, event);
    put(&b, ",\"match\":");
    if (match) put_string(&b, match);
    else put(&b, "null");
    put(&b, ",\"members\":[");
    for (int i = 0; r && i < r->count && i < ROSTER_MAX; i++) {
        const RosterRow *row = &r->rows[i];
        snprintf(num, sizeof num, "%s{\"ussid\":%u,\"uid\":", i ? "," : "", row->ussid);
        put(&b, num);
        put_string(&b, row->uid);
        put(&b, ",\"name\":");
        put_string(&b, row->name);
        snprintf(num, sizeof num, ",\"local\":%s,\"lid\":%u,\"sid\":", row->local ? "true" : "false", row->lid);
        put(&b, num);
        if (row->sid) snprintf(num, sizeof num, "\"%016llx\"}", (unsigned long long)row->sid);
        else snprintf(num, sizeof num, "null}");
        put(&b, num);
    }
    put(&b, "]}");
    return b.bad ? -1 : (int)b.n;
}
