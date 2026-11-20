#include "breadcrumbs.h"

#include "plat.h"

#include <stdarg.h>
#include <stdio.h>
#include <string.h>

// ---- events: slot = sequence % CRUMB_EVENTS. A slot's `seq` is 0 while its text is being written and the event's
// own sequence (from 1) once complete, so a reader knows which slots hold a whole line of the turn it expects.
typedef struct {
    volatile uint64_t seq;
    CrumbEvent e;
} Slot;

static Slot g_ring[CRUMB_EVENTS];
static volatile uint64_t g_head;   // events written so far

void crumb_text(const char *text) {
    if (!text) return;
    uint64_t seq = plat_inc(&g_head);
    Slot *s = &g_ring[(seq - 1) % CRUMB_EVENTS];
    plat_store(&s->seq, (uint64_t)0);
    s->e.ms = plat_ms();
    s->e.tid = plat_thread_id();
    size_t n = 0;
    for (; text[n] && n < CRUMB_TEXT - 1; n++) s->e.text[n] = text[n] == '\r' || text[n] == '\n' ? ' ' : text[n];
    s->e.text[n] = 0;
    plat_store(&s->seq, seq);
}

void crumb(const char *fmt, ...) {
    char buf[CRUMB_TEXT];
    va_list ap;
    va_start(ap, fmt);
    int n = vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n >= 0) crumb_text(buf);
}

int crumb_events(CrumbEvent *out, int cap) {
    uint64_t head = plat_load(&g_head);
    uint64_t first = head > CRUMB_EVENTS ? head - CRUMB_EVENTS + 1 : 1;
    if (cap > 0 && head - first + 1 > (uint64_t)cap) first = head - (uint64_t)cap + 1;
    int n = 0;
    for (uint64_t seq = first; seq <= head && n < cap; seq++) {
        const Slot *s = &g_ring[(seq - 1) % CRUMB_EVENTS];
        if (plat_load(&s->seq) != seq) continue;   // being written, or already taken by a newer event
        out[n] = s->e;
        out[n].text[CRUMB_TEXT - 1] = 0;
        if (plat_load(&s->seq) == seq) n++;        // else it was overwritten while copied: dropped
    }
    return n;
}

// ---- state: a slot is claimed once by its key (compare-and-swap on `used`) and its value rewritten in place.
typedef struct {
    volatile int32_t used;
    char key[CRUMB_KEY];
    char value[CRUMB_VALUE];
} State;

static State g_state[CRUMB_STATES];

void crumb_state(const char *key, const char *fmt, ...) {
    if (!key || !*key) return;
    char value[CRUMB_VALUE];
    va_list ap;
    va_start(ap, fmt);
    int n = vsnprintf(value, sizeof value, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    for (char *p = value; *p; p++)
        if (*p == '\r' || *p == '\n') *p = ' ';
    for (int i = 0; i < CRUMB_STATES; i++) {
        State *s = &g_state[i];
        if (!plat_load(&s->used)) {
            if (plat_cas(&s->used, 1, 0) != 0) { i--; continue; }   // another thread took it now: look at it again
            snprintf(s->key, sizeof s->key, "%s", key);
            plat_store(&s->used, 2);
        }
        while (plat_load(&s->used) == 1) {}   // its key is being written (a few instructions)
        if (strncmp(s->key, key, CRUMB_KEY - 1)) continue;
        // the terminator first: a reader never runs past the buffer, whatever it catches of the two values
        s->value[CRUMB_VALUE - 1] = 0;
        size_t len = strlen(value);
        memcpy(s->value, value, len + 1);
        return;
    }
}

int crumb_state_at(int i, char key[CRUMB_KEY], char value[CRUMB_VALUE]) {
    if (i < 0 || i >= CRUMB_STATES || plat_load(&g_state[i].used) != 2) return 0;
    memcpy(key, g_state[i].key, CRUMB_KEY);
    key[CRUMB_KEY - 1] = 0;
    memcpy(value, g_state[i].value, CRUMB_VALUE);
    value[CRUMB_VALUE - 1] = 0;
    return value[0] != 0;
}

// ---- the room: one writer at a time (the lock); readers take a copy between two equal, even sequence numbers
static CrumbRoom g_room;
static volatile uint32_t g_room_seq;
static PlatLock g_room_lock = PLAT_LOCK_INIT;

int crumb_room_host(const CrumbRoom *room) {
    return room->n > 0 && room->ussid && room->ussids[0] == room->ussid;   // ussids ascending: the first hosts the mesh
}

const char *crumb_room_list(const CrumbRoom *room, char *out, size_t cap) {
    size_t at = 0;
    if (cap) out[0] = 0;
    for (int i = 0; i < room->n && at + 12 < cap; i++)
        at += (size_t)snprintf(out + at, cap - at, "%s%u", i ? "," : "", (unsigned)room->ussids[i]);
    return out;
}

void crumb_room(const char *lobby, uint32_t ussid, const uint32_t *ussids, int n, uint64_t at) {
    CrumbRoom next = {0};
    if (lobby && *lobby) {
        snprintf(next.lobby, sizeof next.lobby, "%s", lobby);
        next.ussid = ussid;
        next.at = at;
        if (n > CRUMB_ROOM_MAX) n = CRUMB_ROOM_MAX;
        for (int i = 0; i < n && ussids; i++) next.ussids[next.n++] = ussids[i];
    }
    plat_lock(&g_room_lock);
    next.ms = plat_ms();
    int changed = strcmp(next.lobby, g_room.lobby) || next.ussid != g_room.ussid || next.n != g_room.n ||
                  memcmp(next.ussids, g_room.ussids, sizeof next.ussids);
    plat_inc(&g_room_seq);
    g_room = next;
    plat_inc(&g_room_seq);
    plat_unlock(&g_room_lock);
    if (!changed) return;
    char list[160];
    if (next.lobby[0])
        crumb("room: lobby=%s ussid=%u host=%d players=%d ussids=%s", next.lobby, (unsigned)next.ussid,
              crumb_room_host(&next), next.n, crumb_room_list(&next, list, sizeof list));
    else
        crumb("room: left");
}

int crumb_room_get(CrumbRoom *out) {
    for (int tries = 0; tries < 8; tries++) {
        uint32_t seq = plat_load(&g_room_seq);
        if (seq & 1) continue;
        *out = g_room;
        out->lobby[sizeof out->lobby - 1] = 0;
        if (out->n < 0 || out->n > CRUMB_ROOM_MAX) out->n = 0;
        if (plat_load(&g_room_seq) == seq) return out->lobby[0] != 0;
    }
    memset(out, 0, sizeof *out);
    return 0;
}

// ---- peers: endpoint -> last time seen. A slot's key is claimed with a compare-and-swap; times are plain stores.
static volatile uint64_t g_peer_key[CRUMB_PEERS];   // 0 free; else 1 << 48 | ip << 16 | port
static volatile uint64_t g_peer_ms[CRUMB_PEERS];

void crumb_peer(uint32_t ip, uint16_t port) {
    uint64_t key = 1ull << 48 | (uint64_t)ip << 16 | port, now = plat_ms();
    int oldest = 0;
    for (int i = 0; i < CRUMB_PEERS; i++) {
        uint64_t k = plat_load(&g_peer_key[i]);
        if (k == key) { g_peer_ms[i] = now; return; }
        if (!k) {
            if (plat_cas(&g_peer_key[i], key, (uint64_t)0) == 0) { g_peer_ms[i] = now; return; }
            i--;   // taken now, maybe by this same endpoint: look at it again
            continue;
        }
        if (g_peer_ms[i] < g_peer_ms[oldest]) oldest = i;
    }
    // full: the endpoint silent for longest gives its place (a lost race here only loses one sighting)
    plat_store(&g_peer_key[oldest], key);
    g_peer_ms[oldest] = now;
}

int crumb_peer_count(uint64_t window_ms) {
    uint64_t now = plat_ms();
    int n = 0;
    for (int i = 0; i < CRUMB_PEERS; i++)
        if (plat_load(&g_peer_key[i]) && now - g_peer_ms[i] <= window_ms) n++;
    return n;
}

void crumb_peers_reset(void) {
    for (int i = 0; i < CRUMB_PEERS; i++) plat_store(&g_peer_key[i], (uint64_t)0);
}
