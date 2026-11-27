// Previous races: who is in the online lobby, by the id the server gave them, next to the name their game shows
// (docs/contracts/LAUNCHER_API.md "Previous races: POST /local/v1/roster"; research LOBBY-ROSTER-1.8.0.md).
//
// Pure logic, no game and no OS (tests/test_roster.c): reading the game's member tree through a memory reader, the
// hash that says "the set changed", the match key, the JSON body. rosterhook.c owns the hooks and the sending.
// Uids and names are never logged (logs go into bug reports): they only ever go into the body for the launcher.
#ifndef MKWMP_ROSTER_H
#define MKWMP_ROSTER_H

#include "gameaddrs.h"

#include <stddef.h>
#include <stdint.h>

enum {
    ROSTER_MAX = 32,          // rows of one roster (the contract's limit; a lobby holds 24)
    ROSTER_NODES_MAX = 256,   // a node count above this is not a member tree
    ROSTER_UID_CAP = 0x400,   // the game's buffers, with the terminator
    ROSTER_NAME_CAP = 0x40,
    ROSTER_KEY_CAP = 64,      // the referee key "%u_%d_%d_%d_%x" is at most 52 characters
    // the largest body: every byte of every string as \u00XX
    ROSTER_JSON_MAX = ROSTER_MAX * (6 * (ROSTER_UID_CAP + ROSTER_NAME_CAP) + 128) + 256,
};

typedef struct {
    uint32_t ussid;
    uint8_t local;                 // a player of this console
    uint8_t lid;                   // p2p_lid: the local player number its game wrote
    uint64_t sid;                  // p2p_sid: its Pia station (0: none yet)
    char uid[ROSTER_UID_CAP];      // __us.uid, server-written ("" when the game has none)
    char name[ROSTER_NAME_CAP];    // PlayerStatic.name as that member's game wrote it: any bytes
} RosterRow;

typedef struct {
    int count;
    RosterRow rows[ROSTER_MAX];    // ascending ussid
} Roster;

// Guest memory: n bytes at addr as a host pointer, NULL when they cannot all be read.
typedef struct {
    const void *(*at)(void *user, uint64_t addr, size_t n);
    void *user;
} RosterMem;

// The members of the context at ctx: the tree's nodes in order, st == 2 only, at most ROSTER_MAX rows; no more nodes
// are visited than the context counts. ctx 0 is "no lobby": an empty roster. Returns the row count, or -1 when
// something is not as expected (unreadable memory, a count that is no lobby's, keys out of order, a tree too deep):
// *out is then not to be used.
int roster_walk(const RosterMem *m, const struct roster_layout *l, uint64_t ctx, Roster *out);

// A hash of the set of {ussid, uid, name}: two rosters with the same members under the same names have the same one.
uint64_t roster_hash(const Roster *r);

// 1 when s is a referee key: "%u_%d_%d_%d_%x".
int roster_key_ok(const char *s);
// The key a match node holds at `at`: a string object (char * at +0) or the characters themselves. 1 with out filled;
// 0 when neither is a referee key.
int roster_match_key(const RosterMem *m, uint64_t at, char out[ROSTER_KEY_CAP]);

// s as the inside of a JSON string: valid UTF-8 kept, quotes, backslashes and control characters escaped, every byte
// that is not part of a well-formed UTF-8 sequence written as '?' (so the text never grows past 6 bytes a byte).
// Returns the length written (out is terminated), or -1 when cap is too small.
int roster_json_escape(const char *s, char *out, size_t cap);

// The request body: {"event":"<event>","match":"<match>"|null,"members":[...]}. r NULL: no members. Returns its
// length, or -1 when cap is too small (ROSTER_JSON_MAX always fits).
int roster_json(const char *event, const char *match, const Roster *r, char *out, size_t cap);

#endif
