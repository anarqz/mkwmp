// System friend list: what nn::friends::GetFriendList gives the game.
//
// The game only shows an NPLN friend (lobby: SubscribeFriendUsers) whose account id is also
// in the console's friend list (docs/RE-MAP.md, A 2.3 and 2.6). The probe therefore serves the
// launcher's roster as that list:
//   GET http://<launcher>/local/v1/friends, Authorization: Bearer <launcher_token>
//   200 {"friends":[{"name":"Ana"}, ...]}   never the caller, at most 100, names 1..20 chars
// (docs/contracts/LAUNCHER_API.md). Each name gets the account id the lobby derives for it.
#ifndef MKWMP_FRIENDS_H
#define MKWMP_FRIENDS_H

#include <stddef.h>
#include <stdint.h>

enum {
    FRIENDS_MAX = 100,                // contract: at most 100 entries
    FRIEND_NAME_CHARS = 20,           // contract: 1..20 characters
    FRIEND_NAME_SIZE = 4 * FRIEND_NAME_CHARS + 1,   // UTF-8 bytes + NUL
};

// nn::friends::Friend as the SDK's accessors read it (offsets from the `sdk` module, see
// docs/RE-MAP.md A 2.6). Everything the probe does not set stays zero, as Friend::Friend()
// leaves it.
typedef struct {
    uint64_t owner[2];        // 0x000 nn::account::Uid of the user whose list this is
    uint64_t account_id;      // 0x010 Friend::GetAccountId
    char nickname[33];        // 0x018 Friend::GetNickname (nn::account::Nickname, NUL-terminated)
    uint8_t pad39[7];
    uint8_t presence[0xe0];   // 0x040 Friend::GetPresence (zero = offline; the game never asks)
    uint8_t is_favorite;      // 0x120 Friend::IsFavorite
    uint8_t is_newly;         // 0x121 Friend::IsNewly
    uint8_t pad122[6];
    uint8_t is_valid;         // 0x128 Friend::IsValid
    uint8_t pad129[0xd7];
} GFriend;                    // 0x200

typedef struct {
    int count;
    struct {
        uint64_t account_id;
        char name[FRIEND_NAME_SIZE];
    } e[FRIENDS_MAX];
} FriendRoster;

// The lobby's rule (rust/lobby: users::ticket_user_id + social::account_id): the first 8
// bytes, big-endian, of uuid5(NAMESPACE_URL, "mkwmp-user/" + lowercase(name)), top bit
// cleared, 0 mapped to 1. Names are ASCII by the seat server's rule; only ASCII is lowercased.
uint64_t friends_account_id(const char *name);

// Pure parser of a /local/v1/friends answer. Fills *out and returns 1 for a well-formed 200;
// otherwise returns 0 and leaves *out alone. Skips invalid names, repeated players and
// `self` (the caller's own name, may be NULL); stops at FRIENDS_MAX.
int friends_parse(int http_status, const char *body, const char *self, FriendRoster *out);

// GetFriendList paging over a roster: writes entries [offset, offset + count) to `out` and
// returns how many were written.
int friends_page(const FriendRoster *roster, const uint64_t owner[2], int offset, int count, GFriend *out);

// The same over the cached launcher roster. Never blocks on the network. A call with
// offset 0 picks up the latest roster; later pages continue in that same snapshot.
int friends_list(const uint64_t owner[2], int offset, int count, GFriend *out);

// The game went online: start the refresh thread if needed (one GET now, then one every
// 15 s while online) and wait up to `wait_ms` for the first answer, so the list is there
// before the game reads it. Does nothing without a launcher in mkwmp.toml.
void friends_online(int wait_ms);

#endif
