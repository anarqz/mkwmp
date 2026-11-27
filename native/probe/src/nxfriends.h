// Friends on Nextendo Network without ids from Nextendo: the accounts the game's own server names.
//
// The game shows a server friend only when the console's friend list (nn::friends::GetFriendList, friends.h) holds
// the same account id. Nextendo tells the launcher no such ids, but its game server names them to the game: the
// friend users of the sign-in, kept by the game's friend client. This reads them there, once the game's friend manager
// has started, and gives them to friends.c, which serves them as the console's list; the game is then asked, by its
// own "the list changed" routine, to read that list again. Nothing here is written to the game's memory.
//
// Game 1.8.0 only (gameaddrs.h fr_*; every address and offset is guarded), Nextendo mode only. MKWMP_NX_FRIENDS=0
// turns it off; =2 asks the game to read its list again at every new account, not just the first ones of a sign-in.
// Neither an id nor a name is logged: counts only.
#ifndef MKWMP_NXFRIENDS_H
#define MKWMP_NXFRIENDS_H

#include "gameaddrs.h"

#include <stddef.h>
#include <stdint.h>

// ---- pure parts (tests/test_nxfriends.c)
// The account ids of a copy of the game's friend users: `bytes` bytes of `elem`-byte elements, each a string (the
// user: first byte's bit 0 set = long, its length at +8; else the length is that byte >> 1) and the account id at
// +account_at. Users without a name and ids of 0 are skipped, as the game skips them. Sorted, each once; returns how
// many were written (at most cap), -1 for a size that is not whole elements.
int nxfriends_accounts(const uint8_t *users, size_t bytes, size_t elem, size_t account_at, uint64_t *out, int cap);

// A sign-in's set of server accounts.
typedef struct {
    uint64_t ids[100];
    int count;
} NxFriendSet;
// Replace the set by `ids` (sorted, each once). Returns 1 when it changed; *added gets how many are new.
int nxfriends_set(NxFriendSet *s, const uint64_t *ids, int n, int *added);

// ---- the hook
// Hook the friend manager's update of the build (1 installed). Call when the game's code is loaded, in Nextendo mode.
int nxfriends_install(const struct game_addrs *a);

#endif
