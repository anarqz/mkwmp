// The chat room: MKWMP's stand-in for a GameChat room (research/docs/CAMERA-RE.md section 8). On the console a chat
// room is a small group made in the system, apart from any game; a game only learns who is in it
// (nn::chat::GetRoomInfo). Here it is the faces of a private FRIEND ROOM (Online, Friends, create or join a room):
//   - voice and the camera exist in a friend room only. A matchmade race or battle has neither, for everyone;
//   - in a friend room everyone has voice with everyone (the relay routes it; nothing is filtered here);
//   - the camera is shared between mutual friends in that room only, and a player sees at most CHATROOM_FRIENDS faces.
// The server decides all of it and asks nothing of the plugin: the relay's state (state down ver 2, through
// roomnet.c) names the friend room and the player's mutual friends bound in it, in seat order, at most
// CHATROOM_ROOM_MAX, each with its camera flag. The plugin only picks which four to show (chatroom_select); those are
// the members GetRoomInfo lists (camera.c) and the only accounts pictures are asked of (roomnet.c, op 6).
// Without such a state (a matchmade lobby, an older relay or voice library, not online) there is no room: nobody is
// listed and the camera's chat stays off. With MKWMP_CAMERA_LOOPBACK=1 (one PC, no relay) the room is made up here:
// the first four friends of the roster, cameras on.
#ifndef MKWMP_CHATROOM_H
#define MKWMP_CHATROOM_H

#include <stddef.h>
#include <stdint.h>

enum {
    CHATROOM_FRIENDS = 4,       // faces besides the player's (the game has that many remote cameras; the relay's limit too)
    CHATROOM_ROOM_MAX = 23,     // mutual friends the relay can name in one room (a lobby of 24)
};
#define CHATROOM_LOOPBACK_ID 0x4d4b574d504c4f4full   // the room's id with MKWMP_CAMERA_LOOPBACK=1

typedef struct {
    uint64_t account;   // NetworkServiceAccountId: friends_account_id(name)
    uint8_t camera;     // the member's camera is on
} ChatMember;

typedef struct {
    uint64_t id;        // the friend room (0: in none: no member, no camera)
    int n;
    ChatMember member[CHATROOM_FRIENDS];   // the faces chosen, never the player
    int friends;        // the mutual friends in the room they were chosen from (n of them)
} ChatRoom;

// ---- pure, unit-tested (tests/test_roomnet.c) ----------------------------------------------------------------------

// The faces: at most CHATROOM_FRIENDS of the mutual friends in the room (`friends`, in seat order as the relay gave
// them; account 0 and repeats are skipped). Deterministic, and stable while they stay:
//   1. those chosen before (`before`, may be NULL) who are still there with their camera on keep their place;
//   2. then the others with a camera on, in seat order;
//   3. then, only if places are left, those chosen before with the camera off, and last the rest in seat order.
// So a face on screen is never swapped for another while its camera stays on, and a friend without a camera holds a
// place only until a friend with one needs it. Fills out->n, member and friends; out->id is left 0.
void chatroom_select(const ChatMember *friends, int n, const ChatRoom *before, ChatRoom *out);

// ---- the room of this process; any thread --------------------------------------------------------------------------

typedef struct {
    uint64_t (*self_account)(void);
    int (*friends)(uint64_t *accounts, int cap);   // the roster (the loopback's members); may be NULL
} ChatRoomHost;
// Once, by the plugin backend (again by camera_install with the same host): from then on there is a room object.
void chatroom_install(const ChatRoomHost *host);
int chatroom_installed(void);
// The relay's state (roomnet.c): the friend room and the player's mutual friends bound in it (n of them, in seat
// order). id 0, or friends NULL: in no friend room. The faces are chosen here, the choice before kept as
// chatroom_select says while the room is the same one. (With the loopback nothing calls it; a room given here would
// stand in for the made-up one.)
void chatroom_set(uint64_t id, const ChatMember *friends, int n);
// Publishes the room (the relay's, or the loopback's from the roster); the camera's and roomnet's threads call it.
// 1 when it changed.
int chatroom_refresh(void);
// The room as last published. 0 when there is no room object (*out zeroed).
int chatroom_get(ChatRoom *out);
int chatroom_has(uint64_t account);

#endif
