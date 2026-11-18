// The seat server's API (docs/contracts/SEAT_API.md), called directly by the plugin: what the MKWMP launcher does for
// the probe when it runs, done here when it does not (mkw-recomp's own start screen and the mod's options page).
// Users: seatd.c (the game: seat, heartbeat, friends) and modpage.c (the options page: register, PIN, friends).
// Blocking calls (plat_http); any thread.
#ifndef MKWMP_SEATAPI_H
#define MKWMP_SEATAPI_H

#include <stddef.h>
#include <stdint.h>

#define SEATAPI_DEFAULT_SERVER "https://mkw.anarqz.dev"

enum { SEATAPI_NAME_MAX = 16, SEATAPI_PIN_LEN = 6 };

// The answer of a call, and when it failed, why: the server's error code (NAME_REGISTERED, PIN_WRONG, ...) and its
// message, the Retry-After of a 429, or status 0 with what the transport said.
typedef struct {
    int status;
    int retry_after;
    char code[48];
    char message[200];
} SeatApiResult;

// A player's name by the server's rules: 3-16 of [A-Za-z0-9_-]. 1 when valid.
int seatapi_name_ok(const char *name);
int seatapi_pin_ok(const char *pin);    // six digits
// The server's base URL from a setting: trimmed, a missing scheme gets https://, no trailing '/'. 0 when unusable.
int seatapi_base(const char *setting, char *out, size_t cap);

// ---- flat JSON readers for the server's answers (no escapes in its keys; string values are unescaped)
int seatapi_json_str(const char *body, const char *key, char *out, size_t cap);
int seatapi_json_int(const char *body, const char *key, long long *out);
// The value of key when it is an array or object: its first and one-past-last byte (brackets included). 0 if none.
int seatapi_json_span(const char *body, const char *key, const char **begin, const char **end);

// ---- profiles
// POST /seat/v1/profiles {name}: registers name; its PIN (shown once) into pin. 201 on success.
SeatApiResult seatapi_register(const char *base, const char *name, char pin[SEATAPI_PIN_LEN + 1]);

// ---- status: the lobby the seats lead to
typedef struct {
    char lobby[128];       // NPLN host:port
    int voice_port;        // 0: the server has voice off
    char *lobby_ca;        // PEM, malloc'd (free it), NULL when the server has none
    int seats_total, seated, queued;
} SeatApiStatus;
SeatApiResult seatapi_status(const char *base, SeatApiStatus *out);

// ---- sessions (a seat). The view of a session after a join or a poll.
typedef struct {
    char session_id[80], session_key[160];   // the key only after a join
    char state[16];                          // "seated" or "queued"
    int position, seats_total;
    int next_poll_ms;
    char ticket[520], ticket_lobby[128];     // seated
} SeatApiView;
SeatApiResult seatapi_join(const char *base, const char *name, const char *pin, SeatApiView *out);
SeatApiResult seatapi_poll(const char *base, const char *session_id, const char *session_key, SeatApiView *out);
SeatApiResult seatapi_leave(const char *base, const char *session_id, const char *session_key);

// ---- presence and friends
// POST /seat/v1/presence {name, pin}: a token for the friends routes. Also how a PIN is checked: 401 PIN_WRONG.
SeatApiResult seatapi_presence(const char *base, const char *name, const char *pin, char *token, size_t cap);
SeatApiResult seatapi_presence_end(const char *base, const char *token);

enum { SEATAPI_FRIENDS_MAX = 100, SEATAPI_REQUESTS_MAX = 50 };
typedef struct {
    char name[SEATAPI_NAME_MAX + 1];
    char state[12];   // friends: "playing", "online", "offline"; requests: ""
} SeatApiFriend;
typedef struct {
    int nfriends, nincoming, noutgoing;
    SeatApiFriend friends[SEATAPI_FRIENDS_MAX], incoming[SEATAPI_REQUESTS_MAX], outgoing[SEATAPI_REQUESTS_MAX];
} SeatApiFriends;
SeatApiResult seatapi_friends(const char *base, const char *token, SeatApiFriends *out);
SeatApiResult seatapi_friend_request(const char *base, const char *token, const char *name);   // POST: ask / accept theirs
SeatApiResult seatapi_friend_accept(const char *base, const char *token, const char *name);
SeatApiResult seatapi_friend_remove(const char *base, const char *token, const char *name);  // reject, cancel or end

#endif
