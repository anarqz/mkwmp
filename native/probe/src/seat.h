// Seat client: the probe asks mkwmp-launcher's local seat API for a seat when the game first asks
// for the network (Online Play). Contract (docs/contracts/SEAT_API.md, local API):
//   POST http://<launcher>/local/v1/seat, Authorization: Bearer <launcher_token>, no body
//   200 {"state":"seated","ticket":"mkw1...","lobby":"host:port","name":"..."}
//   200 {"state":"queued","position":N,"seatsTotal":T}
//   409 {"state":"refused","message":"..."}   503 seat server unreachable   401 bad token
// The same POST is the keepalive while online (the launcher releases the seat after 90 s quiet).
#ifndef MKWMP_SEAT_H
#define MKWMP_SEAT_H

#include <stddef.h>
#include <stdint.h>

typedef enum {
    SEAT_SEATED = 0,
    SEAT_QUEUED,
    SEAT_REFUSED,      // 409: show message, do not retry
    SEAT_UNAVAILABLE,  // 503 / launcher not running / bad token after reload
} SeatState;

typedef struct {
    SeatState state;
    int position, total;   // queued
    char ticket[520];      // seated
    char lobby[128];       // seated: authoritative lobby "host:port"
    char message[128];     // refused
} SeatReply;

// Pure parser of a local-API response (HTTP status + JSON body). Unit-tested.
void seat_parse(int http_status, const char *body, SeatReply *out);

// The room as the lobby numbers it, when a seat reply carries the optional object
//   "room": {"lobby": "<lobby uuid>", "ussid": 3, "ussids": [3, 5, 12], "at": <ms since 1970>}   ("room": null: none)
// (lobby NULL: in none). ussids is ascending (the first is the mesh host); at is when the lobby reported the room (0
// when a launcher before it leaves it out). The loader sets the sink (probe.c: breadcrumbs.h's crumb_room, for the crash
// record); seat_parse_room reads a reply body and calls it, and does nothing for a reply without the key.
typedef void (*SeatRoomSink)(const char *lobby, uint32_t ussid, const uint32_t *ussids, int n, uint64_t at);
void seat_set_room_sink(SeatRoomSink sink);
void seat_parse_room(const char *body);

// One POST to the configured launcher (re-reads mkwmp.toml once on 401 / refused connection).
// Returns 0 if a launcher is configured, -1 if mkwmp.toml has no `launcher` key.
int seat_post(SeatReply *out);

// Block (on the calling guest thread) until seated, refused or unavailable, polling every 2 s
// while queued, at most `max_wait_ms`. Logs and notifies the queue position.
SeatState seat_acquire(SeatReply *out, int max_wait_ms);

// Start the keepalive thread (POST every 15 s while the process lives). Idempotent.
void seat_keepalive_start(void);

// Leaving online: stop posting (the launcher frees the seat after 90 s). Returns 1 if it was
// running. Going online again: seat_acquire, then seat_keepalive_resume.
int seat_keepalive_pause(void);
void seat_keepalive_resume(void);
int seat_keepalive_paused(void);

// Latest ticket from a seated reply (renewals included). Returns 1 if there is one.
int seat_ticket(char *dst, size_t n);

// Takes the seat now, on a thread of its own, with the player's notices (connecting, queue position, connected) and
// the keepalive once seated: the plugin's seat mode "start". Once per process.
void seat_take_early(void);

#endif
