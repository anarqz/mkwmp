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

// One POST to the configured launcher (re-reads mkwmp.toml once on 401 / refused connection).
// Returns 0 if a launcher is configured, -1 if mkwmp.toml has no `launcher` key.
int seat_post(SeatReply *out);

// Block (on the calling guest thread) until seated, refused or unavailable, polling every 2 s
// while queued, at most `max_wait_ms`. Logs and notifies the queue position.
SeatState seat_acquire(SeatReply *out, int max_wait_ms);

// Start the keepalive thread (POST every 15 s while the process lives). Idempotent.
void seat_keepalive_start(void);

// Latest ticket from a seated reply (renewals included). Returns 1 if there is one.
int seat_ticket(char *dst, size_t n);

#endif
