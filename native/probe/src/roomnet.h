// The friend room on the network (docs/contracts/VOICE_API.md section 6, VIDEO_API.md): what joins the plugin's chat
// room (chatroom.h), the GameChat camera (camera.h) and the voice library's room API (native/voice/src/mkwmp_voice.h).
// Plugin backend only.
//
// Voice and the camera exist in a private friend room only, and the server knows the room by itself: nothing is asked
// of it (no sharing key, no room ticket). One thread, twice a second:
//   - room mode: on as soon as the voice library has room API 2. The library then says so to the relay on every
//     control up, and the relay answers with state down ver 2;
//   - the room: the relay's friend room and the player's mutual friends bound in it go to the chat room
//     (chatroom_set), which picks at most four faces: GetRoomInfo lists exactly those, each with the camera flag the
//     relay gave; the relay's send divisor goes to the camera's encoder. Room id 0 (a matchmade race or battle): no
//     member, the camera's chat is off, and the library keeps voice off (microphone closed, nothing played);
//   - pictures: the accounts the game asked pictures of, that are chosen faces with a camera, are subscribed to (op 6,
//     at most four); the camera's frames go out through the library (camera_set_jpeg_out) and the friends' come in
//     (camera_jpeg_in).
// A relay that answers state ver 1 (it knows no friend rooms): no room, so no camera is sent or shown; voice is
// voice/v1 as that relay routes it. A voice library without room API 2, or MKWMP_CAMERA_LOOPBACK=1 (one PC, no
// server): this file does nothing at all.
#ifndef MKWMP_ROOMNET_H
#define MKWMP_ROOMNET_H

#include "../../voice/src/mkwmp_voice.h"
#include "chatroom.h"

#include <stdint.h>

// Once, from the plugin backend, after chatroom_install: starts the thread (unless loopback).
void roomnet_start(void);

// ---- pure, unit-tested (tests/test_roomnet.c) ----------------------------------------------------------------------

// Room mode: the voice library speaks friend rooms (room API 2; API 1 asked the relay for rooms, which is gone).
static inline int roomnet_mode(int room_api) { return room_api >= 2; }

// The relay's state as the chat room's input: 1 when the relay speaks friend rooms, and then *id is the room (0: in
// none, and *n is 0) and out the player's mutual friends bound in it, in the relay's (seat) order. 0 when there is
// nothing to go by (no state yet, or a relay without rooms): *id 0, *n 0.
static inline int roomnet_friends(const mkwmp_voice_room *room, int have, uint64_t *id, ChatMember out[CHATROOM_ROOM_MAX], int *n) {
    *id = 0, *n = 0;
    if (!have || !room || room->relay != 2) return 0;
    if (!room->room_id) return 1;
    *id = room->room_id;
    for (int i = 0; i < room->count && i < MKWMP_VOICE_ROOM_FRIENDS && *n < CHATROOM_ROOM_MAX; i++)
        if (room->member[i].account) out[*n].account = room->member[i].account, out[(*n)++].camera = room->member[i].camera != 0;
    return 1;
}

// Whose pictures to ask the relay for: the accounts the game asked pictures of (asked) that are faces chosen for the
// room (chatroom_select) with a camera on; each once, at most 4. Returns the count.
static inline int roomnet_pictures(const ChatRoom *faces, const uint64_t *asked, int nasked, uint64_t out[CHATROOM_FRIENDS]) {
    int n = 0;
    if (!faces || !faces->id) return 0;
    for (int i = 0; i < nasked && n < CHATROOM_FRIENDS; i++) {
        int face = 0, twice = 0;
        for (int k = 0; k < faces->n && k < CHATROOM_FRIENDS; k++) face |= faces->member[k].account == asked[i] && faces->member[k].camera;
        for (int k = 0; k < n; k++) twice |= out[k] == asked[i];
        if (asked[i] && face && !twice) out[n++] = asked[i];
    }
    return n;
}

#endif
