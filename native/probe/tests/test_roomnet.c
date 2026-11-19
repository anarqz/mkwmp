// The chat room on the network (roomnet.h): when room mode is on, the relay's answer as the chat room's grant (the
// members GetRoomInfo lists, with their camera flags), and whose pictures are asked of the relay. With chatroom.c: a
// grant replaces the local pick, nobody included, and no grant brings the pick back.
//
//   $zig = "C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"; $s = "native\probe\src"
//   & $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o scratch\test_roomnet.exe `
//       native\probe\tests\test_roomnet.c $s\chatroom.c $s\log.c $s\plat_win.c
//   scratch\test_roomnet.exe
#include "../src/chatroom.h"
#include "../src/roomnet.h"
#include "../src/rtapi.h"

#include <stdio.h>
#include <string.h>

RtApi g_rt;

static int failures;
#define CHECK(c) do { if (!(c)) { printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #c); failures++; } } while (0)

enum { SELF = 0x10, ANA = 0x11, BIA = 0x12, CAIO = 0x13, DUDA = 0x14, EDU = 0x15 };
static uint64_t self_account(void) { return SELF; }
static int friends(uint64_t *out, int cap) {
    static const uint64_t list[] = {ANA, BIA, CAIO, DUDA, EDU};
    int n = 0;
    for (; n < 5 && n < cap; n++) out[n] = list[n];
    return n;
}
static uint64_t g_peers[8];
static int g_npeers;
static int peers(uint64_t *out, int cap) {
    int n = 0;
    for (; n < g_npeers && n < cap; n++) out[n] = g_peers[n];
    return n;
}

int main(void) {
    // ---- room mode: the library's room API, a source of room tickets, and something to share with a room
    CHECK(roomnet_mode(1, 1, 1, 1, 0) && roomnet_mode(1, 1, 0, 1, 1) && roomnet_mode(1, 1, 1, 0, 1) && roomnet_mode(2, 1, 1, 1, 1));
    CHECK(!roomnet_mode(0, 1, 1, 1, 1) && !roomnet_mode(-1, 1, 1, 1, 1));   // an older voice library, or none loaded
    CHECK(!roomnet_mode(1, 0, 1, 1, 1));                                    // a launcher holds the account: no tickets
    CHECK(!roomnet_mode(1, 1, 0, 1, 0) && !roomnet_mode(1, 1, 0, 0, 0));    // voice off and no camera chat: nothing to share
    CHECK(!roomnet_mode(1, 1, 1, 0, 0));                                    // MKWMP_VOICE_ROOM=0 and no camera: voice/v1, lobby-wide

    // ---- the relay's answer as the grant
    mkwmp_voice_room room = {.relay = 2, .fps_divisor = 1, .room_id = 0x7701, .count = 3,
                             .member = {{ANA, 1, 1}, {BIA, 0, 1}, {CAIO, 1, 0}}};
    ChatMember m[CHATROOM_FRIENDS];
    uint64_t id = 9;
    int n = 9;
    CHECK(roomnet_grant(&room, 1, &id, m, &n) && id == 0x7701 && n == 3 && m[0].account == ANA && m[0].camera == 1 &&
          m[1].account == BIA && m[1].camera == 0 && m[2].account == CAIO && m[2].camera == 1);
    mkwmp_voice_room none = {.relay = 2, .fps_divisor = 1};
    CHECK(roomnet_grant(&none, 1, &id, m, &n) && id == 0 && n == 0);   // a relay with rooms and no room: granted, nobody
    mkwmp_voice_room old = {.relay = 1, .fps_divisor = 1, .room_id = 5, .count = 1, .member = {{ANA, 1, 1}}};
    CHECK(!roomnet_grant(&old, 1, &id, m, &n) && id == 0 && n == 0);   // state ver 1: no rooms, no grant
    CHECK(!roomnet_grant(&room, 0, &id, m, &n) && !roomnet_grant(NULL, 1, &id, m, &n));   // no state yet
    mkwmp_voice_room odd = {.relay = 2, .room_id = 3, .count = 9, .member = {{ANA, 1, 1}, {0, 1, 1}, {BIA, 2, 1}, {CAIO, 0, 0}}};
    CHECK(roomnet_grant(&odd, 1, &id, m, &n) && n == 3 && m[1].account == BIA && m[1].camera == 1);   // a count past 4, account 0

    // ---- whose pictures: what the game asked for, of the members with a camera
    uint64_t asked[] = {BIA, ANA, DUDA, ANA, 0, CAIO}, out[4] = {0};
    CHECK(roomnet_pictures(&room, asked, 6, out) == 2 && out[0] == ANA && out[1] == CAIO);   // Bia's camera is off, Duda is no member
    CHECK(roomnet_pictures(&room, asked, 0, out) == 0 && roomnet_pictures(&none, asked, 6, out) == 0);
    CHECK(roomnet_pictures(&old, asked, 6, out) == 0 && roomnet_pictures(NULL, asked, 6, out) == 0);
    mkwmp_voice_room full = {.relay = 2, .room_id = 1, .count = 4, .member = {{ANA, 1, 1}, {BIA, 1, 1}, {CAIO, 1, 1}, {DUDA, 1, 1}}};
    uint64_t all[] = {DUDA, CAIO, BIA, ANA, EDU};
    CHECK(roomnet_pictures(&full, all, 5, out) == 4 && out[0] == DUDA && out[3] == ANA);

    // ---- the chat room takes the grant: exactly the server's room, and the local pick again without one
    ChatRoomHost host = {.self_account = self_account, .friends = friends, .peers = peers};
    chatroom_install(&host);
    g_peers[0] = BIA, g_peers[1] = EDU, g_peers[2] = 0x99, g_npeers = 3;
    ChatRoom chat;
    char key[CHATROOM_KEY_SIZE];
    chatroom_refresh();
    CHECK(chatroom_get(&chat, key) && !chat.granted && chat.n == 2 && chat.member[0].account == BIA && chat.member[1].account == EDU &&
          chat.member[0].camera == 0);   // the local pick: the friends in the lobby, no camera known
    // what is asked of the server: the sharing key and at most four friends, those in the lobby first
    ChatRoomRequest req;
    CHECK(!chatroom_request(&req));   // no sharing key: not in a game session, nothing to ask
    chatroom_set_key("0123456789");
    chatroom_refresh();
    CHECK(chatroom_request(&req) && !strcmp(req.key, "0123456789") && req.n == 4 && req.friends[0] == BIA && req.friends[1] == EDU &&
          req.friends[2] == ANA && req.friends[3] == CAIO);
    // the grant
    CHECK(roomnet_grant(&room, 1, &id, m, &n));
    chatroom_grant(id, m, n);
    CHECK(chatroom_refresh() == 1 && chatroom_get(&chat, NULL) && chat.granted && chat.id == 0x7701 && chat.n == 3 &&
          chat.member[0].account == ANA && chat.member[0].camera && !chat.member[1].camera && chat.member[2].account == CAIO);
    CHECK(chatroom_has(ANA) && chatroom_has(CAIO) && !chatroom_has(EDU) && !chatroom_has(SELF));   // Edu is in the lobby, not in the room
    CHECK(chatroom_refresh() == 0);
    // the relay puts the player in no room: nobody, though friends are in the lobby
    CHECK(roomnet_grant(&none, 1, &id, m, &n));
    chatroom_grant(id, m, n);
    CHECK(chatroom_refresh() == 1 && chatroom_get(&chat, NULL) && chat.granted && chat.id == 0 && chat.n == 0 && !chatroom_has(BIA));
    // a member's camera goes on: the room changes
    room.member[1].camera = 1;
    CHECK(roomnet_grant(&room, 1, &id, m, &n));
    chatroom_grant(id, m, n);
    CHECK(chatroom_refresh() == 1 && chatroom_get(&chat, NULL) && chat.n == 3 && chat.member[1].camera == 1);
    // the relay stops speaking rooms (or the library left it): no grant, the local pick is back
    CHECK(!roomnet_grant(&old, 1, &id, m, &n));
    chatroom_grant(0, NULL, -1);
    CHECK(chatroom_refresh() == 1 && chatroom_get(&chat, NULL) && !chat.granted && chat.n == 2 && chat.member[0].account == BIA &&
          chat.member[0].camera == 0);

    printf("test_roomnet: %s\n", failures ? "FAIL" : "PASS");
    return failures != 0;
}
