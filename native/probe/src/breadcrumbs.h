// Breadcrumbs: what the crash record (crash.h) says about the session besides the fault itself.
//
//   - events: a lock-free ring of the last CRUMB_EVENTS interesting things (seat, lobby, Pia peers, matchmaking,
//     friend room, voice), each one line with the session's uptime and the thread. Any thread, any time; a writer
//     never waits and never allocates. mkwmp_event (log.h) logs a line and leaves it here in one call.
//   - state: a few named values that describe "now" (versions, the player, the lobby, the mesh role...), overwritten
//     in place.
//   - peers: the remote Pia endpoints that sent or were sent a packet lately: the nearest thing to "players in the
//     room" this process sees (the lobby's own numbers, ussid and lobby id, never reach it in the clear).
//
// Readers (crash.c, from a crash handler) copy without locks: a line being written at that instant may come out
// torn or be skipped, never unterminated. Platform-neutral; nothing here is Windows-only.
#ifndef MKWMP_BREADCRUMBS_H
#define MKWMP_BREADCRUMBS_H

#include <stddef.h>
#include <stdint.h>

enum {
    CRUMB_EVENTS = 64,       // ring size; the record prints the last 50
    CRUMB_TEXT = 176,        // bytes of one event's text, with the terminator
    CRUMB_STATES = 16,
    CRUMB_KEY = 16,
    CRUMB_VALUE = 112,
    CRUMB_PEERS = 32,
};

typedef struct {
    uint64_t ms;             // plat_ms() when it happened
    unsigned long tid;
    char text[CRUMB_TEXT];
} CrumbEvent;

// One event (printf-style, cut to CRUMB_TEXT - 1 bytes; line breaks become spaces).
void crumb(const char *fmt, ...);
void crumb_text(const char *text);

// A named value ("lobby", "mesh", "name"...): set or replaced; an empty value removes it from the record. At most
// CRUMB_STATES names, each CRUMB_KEY - 1 bytes; later names are dropped.
void crumb_state(const char *key, const char *fmt, ...);

// A Pia packet to or from this IPv4 endpoint (network byte order, as sockaddr_in has them). Cheap: every packet calls it.
void crumb_peer(uint32_t ip, uint16_t port);
// Endpoints seen in the last window_ms.
int crumb_peer_count(uint64_t window_ms);
void crumb_peers_reset(void);   // the player left online

// ---- the room as the lobby numbers it: what its log line has ("room order lobby=<uuid> ... by_ussid=3*:<uid>,...",
// the lowest ussid being the mesh host). The game's Gamesync stream is TLS inside the guest, so none of it passes
// through this process in the clear: it is known only when something that talks to the server says it (the seat
// reply's optional "room" object, seat.c). Until then the record has "?" and the player's uid (state "uid"), which
// with the UTC time finds the line.
enum { CRUMB_ROOM_MAX = 24 };
typedef struct {
    char lobby[40];                  // the lobby's UUID as the server prints it; "" = in no room
    uint32_t ussid;                  // this player's
    int n;                           // ussids in the room, this player's among them
    uint32_t ussids[CRUMB_ROOM_MAX]; // ascending, as the seat server sends them: the first is the mesh host
    uint64_t ms;                     // plat_ms() when it was last told
    uint64_t at;                     // when the lobby reported it, ms since 1970 (the seat server's "at"); 0 = not said
} CrumbRoom;
// The room now (lobby NULL or "": left), with the lobby's "at" (0 when the reply had none). A change of the room (not
// of "at" alone) is also an event: "room: lobby=... ussid=... host=... players=...".
void crumb_room(const char *lobby, uint32_t ussid, const uint32_t *ussids, int n, uint64_t at);
// 1 and the room when one is known. The host is the first ussid: crumb_room_host says whether that is this player.
int crumb_room_get(CrumbRoom *out);
int crumb_room_host(const CrumbRoom *room);
// "3,5,12" (in the order given); returns out.
const char *crumb_room_list(const CrumbRoom *room, char *out, size_t cap);

// ---- readers (the crash record; tests)
// The last events, oldest first, at most cap; returns how many.
int crumb_events(CrumbEvent *out, int cap);
// State i (0 .. CRUMB_STATES - 1): 1 and key/value when it holds something.
int crumb_state_at(int i, char key[CRUMB_KEY], char value[CRUMB_VALUE]);

#endif
