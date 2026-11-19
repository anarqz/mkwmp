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

// Every call's timeout from now on (default 10 s); the game's exit uses a short one for its goodbyes.
void seatapi_set_timeout_ms(int ms);

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
// POST /seat/v1/profiles {name, pin}: the profile again, with the PIN the player already has, after the server purged
// it (unused for 30 days: a call with name and PIN answers 404 PROFILE_NOT_FOUND). 201 restored; 409 NAME_REGISTERED:
// someone else took the name meanwhile.
SeatApiResult seatapi_restore(const char *base, const char *name, const char *pin);

// ---- status: the lobby the seats lead to
typedef struct {
    char lobby[128];       // NPLN host:port
    int voice_port;        // 0: the server has voice off
    char *lobby_ca;        // PEM, malloc'd (free it), NULL when the server has none
    int seats_total, seated, queued;
    int report_kinds;      // SEATAPI_KIND_* bits: the kinds of report the server takes a `kind` for (0: an older server)
} SeatApiStatus;
SeatApiResult seatapi_status(const char *base, SeatApiStatus *out);
// The kinds a status body lists in `reportKinds` (["voice","camera"]), as SEATAPI_KIND_* bits; 0 without the field.
// Pure (tests/test_seatapi.c).
enum { SEATAPI_KIND_VOICE = 1, SEATAPI_KIND_CAMERA = 2 };
int seatapi_report_kinds(const char *status_body);

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
// The long poll: with since (the last answer's cursor) the server answers at the next change or after about 25 s.
// cursor gets this answer's cursor (for the next call). since NULL or "": answers at once.
SeatApiResult seatapi_friends_wait(const char *base, const char *token, const char *since, char *cursor,
                                   size_t cursor_cap, SeatApiFriends *out);
SeatApiResult seatapi_friend_request(const char *base, const char *token, const char *name);   // POST: ask / accept theirs
SeatApiResult seatapi_friend_accept(const char *base, const char *token, const char *name);
SeatApiResult seatapi_friend_remove(const char *base, const char *token, const char *name);  // reject, cancel or end

// ---- the account and voice-chat reports (seat server 1.2.0)
// DELETE /seat/v1/profiles/{name} {pin}: the profile, its PIN, friendships, presences and seats, and the reports it
// filed, gone; the name is free again. 204 deleted; 404 PROFILE_NOT_FOUND is as good (nothing left); 403/401 wrong PIN.
SeatApiResult seatapi_profile_delete(const char *base, const char *name, const char *pin);

enum { SEATAPI_REPORT_DETAILS_MAX = 500 };
// The reasons the server takes, in the order the page lists them.
#define SEATAPI_REPORT_REASONS "harassment", "hate", "sexual", "threats", "disruption", "other"
typedef struct {
    char reported[SEATAPI_NAME_MAX + 1];
    char reason[16];
    char status[8];   // "open" or "closed"
    char kind[8];     // "voice" or "camera" ("voice" when the server does not say: its default)
    long long created;
} SeatApiReport;
// GET /seat/v1/reports (presence bearer): the caller's open report; *has 0 when none. 200 on success.
SeatApiResult seatapi_report_get(const char *base, const char *token, SeatApiReport *out, int *has);
// POST /seat/v1/reports {name, reason, details, kind?}: 201 filed. 409 REPORT_ACTIVE (one open at a time), 404
// REPORT_UNKNOWN (no such player), 400 REPORT_SELF / REPORT_INVALID, 429 (5 tries a day). details may be "" (cut at 500).
// kind: "voice" or "camera", sent as the body's optional `kind`; NULL or "": the field is left out, which is what a
// server that does not list the kind in its status must get (seatapi_report_kinds; it files a voice report).
SeatApiResult seatapi_report_file(const char *base, const char *token, const char *name, const char *reason,
                                  const char *details, const char *kind, SeatApiReport *out);
// The body of that call into json. 0 when the name, the reason or the kind is not one the server takes, or it does
// not fit. Pure (tests/test_seatapi.c).
int seatapi_report_body(char *json, size_t cap, const char *name, const char *reason, const char *details, const char *kind);
// DELETE /seat/v1/reports: the open report withdrawn. 204; 404 REPORT_NOT_FOUND.
SeatApiResult seatapi_report_withdraw(const char *base, const char *token);

#endif
