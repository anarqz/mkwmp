// The chat room on the network (docs/contracts/VOICE_API.md section 6, VIDEO_API.md): what joins the plugin's chat room
// (chatroom.h), the GameChat camera (camera.h) and the voice library's room API (native/voice/src/mkwmp_voice.h).
// Plugin backend only, and only when the plugin signs in itself (seatd.h: the room ticket comes from the seat server).
//
// One thread, twice a second:
//   - room mode: on while the player's voice is shared with the room only, or the camera's chat runs. The voice
//     library then says so to the relay on every control up;
//   - the request: the game's sharing key (SetSharingKey), the friends wanted (chatroom_request: at most four, those
//     in the lobby first) and the seat server's room ticket naming them (seatd_room_ticket), handed to the library,
//     which asks the relay again with every refresh and at once on a change;
//   - the grant: the relay's room (state down ver 2) becomes the chat room (chatroom_grant), so GetRoomInfo lists
//     exactly the members the server put together, each with the camera flag the relay gave; the relay's send divisor
//     goes to the camera's encoder;
//   - pictures: the accounts the game asked pictures of, that are members with a camera, are subscribed to; the
//     camera's frames go out through the library (camera_set_jpeg_out) and the members' come in (camera_jpeg_in).
// A relay that answers state ver 1 (it has no rooms): no grant, so the room stays the local pick with every camera
// off (no remote camera is shown, none is sent), and voice stays as before rooms: the lobby's, filtered here to the
// local pick (voice.c). A voice library without the room API, a launcher holding the account, or
// MKWMP_CAMERA_LOOPBACK=1 (one PC, no server): this file does nothing at all.
#ifndef MKWMP_ROOMNET_H
#define MKWMP_ROOMNET_H

#include "../../voice/src/mkwmp_voice.h"

#include <stdint.h>

// Once, from the plugin backend, after chatroom_install: starts the thread (unless loopback).
void roomnet_start(void);

// ---- pure, unit-tested (tests/test_roomnet.c) ----------------------------------------------------------------------

// Room mode: the voice library has the room API, the plugin can get room tickets, and there is something to share
// with a room: voice (on in the settings, shared with the room only) or the camera's chat.
static inline int roomnet_mode(int room_api, int tickets, int voice_on, int voice_room_only, int camera_chat) {
    return room_api >= 1 && tickets && ((voice_on && voice_room_only) || camera_chat);
}

// Whose pictures to ask the relay for: the accounts the game asked pictures of (asked) that are members of the
// relay's room with a camera on; each once, at most 4. Returns the count.
static inline int roomnet_pictures(const mkwmp_voice_room *room, const uint64_t *asked, int nasked, uint64_t out[4]) {
    int n = 0;
    if (!room || room->relay != 2 || !room->room_id) return 0;
    for (int i = 0; i < nasked && n < 4; i++) {
        int member = 0, twice = 0;
        for (int k = 0; k < room->count && k < 4; k++) member |= room->member[k].account == asked[i] && room->member[k].camera;
        for (int k = 0; k < n; k++) twice |= out[k] == asked[i];
        if (asked[i] && member && !twice) out[n++] = asked[i];
    }
    return n;
}

// The relay's answer as the chat room's grant: 1 when the relay speaks rooms (members and *id are its room: none, id
// 0, when it put the player in no room); 0 when there is no grant (no state, or a relay without rooms).
#include "chatroom.h"
static inline int roomnet_grant(const mkwmp_voice_room *room, int have, uint64_t *id, ChatMember out[CHATROOM_FRIENDS], int *n) {
    *id = 0, *n = 0;
    if (!have || !room || room->relay != 2) return 0;
    if (!room->room_id) return 1;
    *id = room->room_id;
    for (int i = 0; i < room->count && i < 4 && *n < CHATROOM_FRIENDS; i++)
        if (room->member[i].account) out[*n].account = room->member[i].account, out[(*n)++].camera = room->member[i].camera != 0;
    return 1;
}

#endif
