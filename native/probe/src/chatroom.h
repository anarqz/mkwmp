// The chat room: MKWMP's stand-in for a GameChat room (research/docs/CAMERA-RE.md section 8). On the console a chat
// room is a small group made in the system, apart from any game; a game only learns who is in it (nn::chat::GetRoomInfo)
// and tells the system which game session the player is in (SetSharingKey), so that cameras are shared between the
// room's members who play together. A race of 24 can hold several rooms; it is never 24 players sharing.
//
// Here ONE room drives both features: the camera's members (camera.c: GetRoomInfo) and who voice chat is shared with
// (voice.c: everybody else in the lobby's voice room is muted for this player).
//
// Who is in it:
//   - the server's grant, once a relay speaks rooms (chatroom_grant): the server decides and the plugin takes what it
//     is given. Nothing calls it yet; chatroom_request says what the plugin would ask for (the sharing key and the
//     friends it wants);
//   - until then the local pick (chatroom_pick): at most CHATROOM_FRIENDS of the player's friends, in the roster's
//     order, who are in the same game session. "In the same session" is what the lobby's voice relay says today: its
//     room is the lobby, so a friend among the voice peers races with the player. With MKWMP_CAMERA_LOOPBACK=1 (one PC,
//     no relay) every friend counts as in the session: the local sharing key is mirrored for them.
// MKWMP_VOICE_ROOM=0 keeps voice lobby-wide (the room then drives the camera only).
#ifndef MKWMP_CHATROOM_H
#define MKWMP_CHATROOM_H

#include <stddef.h>
#include <stdint.h>

enum {
    CHATROOM_FRIENDS = 4,       // members besides the player (the game has that many remote cameras)
    CHATROOM_KEY_SIZE = 0x28,   // nn::chat::SharingKey as the game fills it: text, at most 0x27 characters
};

typedef struct {
    uint64_t account;   // NetworkServiceAccountId: friends_account_id(name)
    uint8_t camera;     // the member's camera is on (known from a grant only; the local pick says 0, loopback 1)
} ChatMember;

typedef struct {
    uint64_t id;        // the server's room id (0: no grant, the local pick)
    int granted;        // the members are the server's
    int n;
    ChatMember member[CHATROOM_FRIENDS];   // never the player
    int in_lobby;       // of the local pick: how many are in the lobby's voice room (the rest: loopback's mirror)
} ChatRoom;

// ---- pure, unit-tested (tests/test_camera.c) -----------------------------------------------------------------------

typedef struct {
    uint64_t self;
    int loopback;                // every friend counts as in the session
    const uint64_t *friends;     // the roster, in its order
    int nfriends;
    const uint64_t *peers;       // the other players of the lobby's voice room
    int npeers;
} ChatRoomInput;
// The local pick: friends in the session, those the relay vouches for first, each once, never the player or account 0.
void chatroom_pick(const ChatRoomInput *in, ChatRoom *out);

// What the plugin asks of the server: the game's sharing key and the friends it wants in the room (those in the lobby
// first). The server answers with a grant or nothing; it never takes the list as the truth.
typedef struct {
    char key[CHATROOM_KEY_SIZE];
    int n;
    uint64_t friends[CHATROOM_FRIENDS];
} ChatRoomRequest;
void chatroom_ask(const ChatRoomInput *in, const char *key, ChatRoomRequest *out);

// ---- the room of this process; any thread --------------------------------------------------------------------------

typedef struct {
    uint64_t (*self_account)(void);
    int (*friends)(uint64_t *accounts, int cap);   // may be NULL
    int (*peers)(uint64_t *accounts, int cap);     // may be NULL
} ChatRoomHost;
// Once, by the plugin backend (again by camera_install with the same host): from then on there is a room. Without it
// (the COFF backend) nothing is filtered.
void chatroom_install(const ChatRoomHost *host);
int chatroom_installed(void);
// Asks the host again and picks; the camera's and the voice's threads call it. 1 when the room changed.
int chatroom_refresh(void);
// The game's sharing key (camera.c: SetSharingKey; NULL or "" when it cleared it).
void chatroom_set_key(const char *key);
// The room as last picked or granted, and the key ("" none) when key is not NULL. 0 when there is no room object.
int chatroom_get(ChatRoom *out, char key[CHATROOM_KEY_SIZE]);
int chatroom_has(uint64_t account);
// 1 when voice is shared with the room only: a room object exists and MKWMP_VOICE_ROOM is not "0".
int chatroom_voice_filter(void);
// For the transport that will speak rooms (none yet). 1 when there is something to ask: a key is set.
int chatroom_request(ChatRoomRequest *out);
// The server's answer: the room it put the player in (n members besides the player, at most CHATROOM_FRIENDS kept).
// members NULL or n < 0: no grant any more, back to the local pick.
void chatroom_grant(uint64_t id, const ChatMember *members, int n);

#endif
