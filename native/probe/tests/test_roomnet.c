// The friend room on the network (roomnet.h) and the chat room it feeds (chatroom.h): room mode, the relay's state as
// the room (the friend room and the player's mutual friends bound in it), the four faces chosen among them (cameras
// first, in seat order, stable while they stay), and whose pictures are asked of the relay (op 6: from that choice).
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

enum { SELF = 0x10, ANA = 0x11, BIA = 0x12, CAIO = 0x13, DUDA = 0x14, EDU = 0x15, FABI = 0x16, GABI = 0x17 };
static uint64_t self_account(void) { return SELF; }
static int friends(uint64_t *out, int cap) {
    static const uint64_t list[] = {ANA, BIA, SELF, CAIO, BIA, DUDA, EDU};
    int n = 0;
    for (; n < 7 && n < cap; n++) out[n] = list[n];
    return n;
}

// The faces are exactly these accounts with these camera flags, in this order.
static int faces_are(const ChatRoom *r, int n, const uint64_t *accounts, const int *cameras) {
    int ok = r->n == n;
    for (int i = 0; ok && i < n; i++) ok = r->member[i].account == accounts[i] && r->member[i].camera == cameras[i];
    return ok;
}
#define FACES(r, n, ...) faces_are(r, n, (const uint64_t[]){__VA_ARGS__}, (const int[]){CAMS})

int main(void) {
    // ---- room mode: the library's room API 2, and nothing else (no ticket, no setting: the relay decides the rest)
    CHECK(roomnet_mode(2) && roomnet_mode(3));
    CHECK(!roomnet_mode(1) && !roomnet_mode(0) && !roomnet_mode(-1));   // the API that asked for rooms, an older library, none loaded

    // ---- the relay's state as the room
    mkwmp_voice_room room = {.relay = 2, .fps_divisor = 1, .room_id = 0x7701, .count = 3,
                             .member = {{ANA, 1, 1}, {BIA, 0, 1}, {CAIO, 1, 0}}};
    ChatMember m[CHATROOM_ROOM_MAX];
    uint64_t id = 9;
    int n = 9;
    CHECK(roomnet_friends(&room, 1, &id, m, &n) && id == 0x7701 && n == 3 && m[0].account == ANA && m[0].camera == 1 &&
          m[1].account == BIA && m[1].camera == 0 && m[2].account == CAIO && m[2].camera == 1);
    mkwmp_voice_room none = {.relay = 2, .fps_divisor = 1};
    CHECK(roomnet_friends(&none, 1, &id, m, &n) && id == 0 && n == 0);   // a relay with rooms, a matchmade lobby: no room, nobody
    mkwmp_voice_room alone = {.relay = 2, .fps_divisor = 1, .room_id = 0x7701};
    CHECK(roomnet_friends(&alone, 1, &id, m, &n) && id == 0x7701 && n == 0);   // a friend room with no mutual friend in it
    mkwmp_voice_room old = {.relay = 1, .fps_divisor = 1, .room_id = 5, .count = 1, .member = {{ANA, 1, 1}}};
    CHECK(!roomnet_friends(&old, 1, &id, m, &n) && id == 0 && n == 0);   // state ver 1: no friend rooms
    CHECK(!roomnet_friends(&room, 0, &id, m, &n) && !roomnet_friends(NULL, 1, &id, m, &n));   // no state yet
    mkwmp_voice_room full = {.relay = 2, .room_id = 3, .count = 99};   // the most a state names, and a count past it
    for (int i = 0; i < MKWMP_VOICE_ROOM_FRIENDS; i++) full.member[i].account = 0x100 + (uint64_t)i, full.member[i].camera = i & 1;
    full.member[5].account = 0;
    CHECK(roomnet_friends(&full, 1, &id, m, &n) && n == CHATROOM_ROOM_MAX - 1 && m[0].account == 0x100 && m[5].account == 0x106 &&
          m[21].account == 0x116 && m[21].camera == 0);

    // ---- the faces: at most four of the mutual friends in the room
    ChatRoom r, was;
    // more than four, in seat order {account, camera}: the cameras first, in seat order; then, places left, the rest
    const ChatMember seven[] = {{ANA, 0}, {BIA, 1}, {CAIO, 0}, {DUDA, 1}, {EDU, 0}, {FABI, 1}, {GABI, 0}};
    chatroom_select(seven, 7, NULL, &r);
#define CAMS 1, 1, 1, 0
    CHECK(r.friends == 7 && r.id == 0 && FACES(&r, 4, BIA, DUDA, FABI, ANA));
#undef CAMS
    was = r;
    chatroom_select(seven, 7, &was, &was);   // the same state again: the same choice (in place)
    CHECK(!memcmp(&was, &r, sizeof r));
    // five cameras: the first four in seat order, the fifth waits
    const ChatMember cams[] = {{ANA, 1}, {BIA, 1}, {CAIO, 0}, {DUDA, 1}, {EDU, 1}, {FABI, 1}};
    chatroom_select(cams, 6, NULL, &r);
#define CAMS 1, 1, 1, 1
    CHECK(r.friends == 6 && FACES(&r, 4, ANA, BIA, DUDA, EDU));
    // stable while they stay: chosen before {Bia, Duda, Fabi, Ana}, and now every camera is on. Nobody on screen is
    // swapped for Caio, Edu or Gabi, who sit before some of them
    const ChatMember all[] = {{ANA, 1}, {BIA, 1}, {CAIO, 1}, {DUDA, 1}, {EDU, 1}, {FABI, 1}, {GABI, 1}};
    r = was;
    chatroom_select(all, 7, &r, &r);
    CHECK(FACES(&r, 4, BIA, DUDA, FABI, ANA));
    // Duda leaves the room: the other three keep their places, the next camera in seat order (Caio) takes the fourth
    const ChatMember left[] = {{ANA, 1}, {BIA, 1}, {CAIO, 1}, {EDU, 1}, {FABI, 1}, {GABI, 1}};
    chatroom_select(left, 6, &r, &r);
    CHECK(r.friends == 6 && FACES(&r, 4, BIA, FABI, ANA, CAIO));
#undef CAMS
    // a friend with the camera off holds a place only until a camera needs it: chosen {Bia, Duda, Fabi, Ana (off)},
    // Edu's camera goes on
    const ChatMember edu_on[] = {{ANA, 0}, {BIA, 1}, {CAIO, 0}, {DUDA, 1}, {EDU, 1}, {FABI, 1}, {GABI, 0}};
    r = was;
    chatroom_select(edu_on, 7, &r, &r);
#define CAMS 1, 1, 1, 1
    CHECK(FACES(&r, 4, BIA, DUDA, FABI, EDU));
#undef CAMS
    // a chosen friend turns the camera off with no other camera waiting: still listed, without the flag, after the cameras
    const ChatMember bia_off[] = {{ANA, 0}, {BIA, 0}, {CAIO, 0}, {DUDA, 1}, {EDU, 0}, {FABI, 1}, {GABI, 0}};
    r = was;
    chatroom_select(bia_off, 7, &r, &r);
#define CAMS 1, 1, 0, 0
    CHECK(FACES(&r, 4, DUDA, FABI, BIA, ANA));
#undef CAMS
    // fewer than four, nobody, account 0 and a repeat
    const ChatMember few[] = {{ANA, 0}, {0, 1}, {BIA, 1}, {ANA, 1}};
    chatroom_select(few, 4, NULL, &r);
#define CAMS 1, 0
    CHECK(r.friends == 2 && FACES(&r, 2, BIA, ANA));
#undef CAMS
    chatroom_select(NULL, 0, &was, &r);
    CHECK(r.n == 0 && r.friends == 0);
    chatroom_select(seven, -3, NULL, &r);
    CHECK(r.n == 0);

    // ---- whose pictures (op 6): what the game asked for, of the faces chosen with a camera
    ChatRoom faces = {.id = 0x7701, .n = 4, .friends = 7, .member = {{BIA, 1}, {DUDA, 1}, {FABI, 1}, {ANA, 0}}};
    uint64_t asked[] = {ANA, BIA, GABI, BIA, 0, FABI}, out[CHATROOM_FRIENDS] = {0};
    CHECK(roomnet_pictures(&faces, asked, 6, out) == 2 && out[0] == BIA && out[1] == FABI);   // Ana's camera is off, Gabi was not chosen
    CHECK(roomnet_pictures(&faces, asked, 0, out) == 0 && roomnet_pictures(NULL, asked, 6, out) == 0);
    ChatRoom nowhere = faces;
    nowhere.id = 0;
    CHECK(roomnet_pictures(&nowhere, asked, 6, out) == 0);   // in no friend room: nobody's pictures
    ChatRoom four = {.id = 1, .n = 4, .friends = 9, .member = {{ANA, 1}, {BIA, 1}, {CAIO, 1}, {DUDA, 1}}};
    uint64_t every[] = {DUDA, CAIO, EDU, BIA, ANA, FABI, GABI};
    CHECK(roomnet_pictures(&four, every, 7, out) == 4 && out[0] == DUDA && out[1] == CAIO && out[2] == BIA && out[3] == ANA);   // never more than four

    // ---- the room of the process: the relay's state in, the faces out
    ChatRoomHost host = {.self_account = self_account, .friends = friends};
    chatroom_install(&host);
    ChatRoom chat;
    CHECK(chatroom_refresh() == 0 && chatroom_get(&chat) && chat.id == 0 && chat.n == 0 && !chatroom_has(ANA));   // friends in the roster are no room
    // a friend room with three mutual friends
    CHECK(roomnet_friends(&room, 1, &id, m, &n));
    chatroom_set(id, m, n);
    CHECK(chatroom_refresh() == 1 && chatroom_get(&chat) && chat.id == 0x7701 && chat.friends == 3);
#define CAMS 1, 1, 0
    CHECK(FACES(&chat, 3, ANA, CAIO, BIA));
#undef CAMS
    CHECK(chatroom_has(ANA) && chatroom_has(BIA) && !chatroom_has(EDU) && !chatroom_has(SELF) && chatroom_refresh() == 0);
    // Bia's camera goes on: the room changes, nobody moves
    room.member[1].camera = 1;
    CHECK(roomnet_friends(&room, 1, &id, m, &n));
    chatroom_set(id, m, n);
#define CAMS 1, 1, 1
    CHECK(chatroom_refresh() == 1 && chatroom_get(&chat) && FACES(&chat, 3, ANA, CAIO, BIA));
#undef CAMS
    // the room fills: 23 mutual friends, the odd ones with a camera. The three on screen stay; one more camera joins
    CHECK(roomnet_friends(&full, 1, &id, m, &n));
    m[0].account = ANA, m[0].camera = 1, m[2].account = BIA, m[2].camera = 1, m[4].account = CAIO, m[4].camera = 1;
    chatroom_set(0x7701, m, n);
#define CAMS 1, 1, 1, 1
    CHECK(chatroom_refresh() == 1 && chatroom_get(&chat) && chat.friends == 22 && FACES(&chat, 4, ANA, CAIO, BIA, 0x101));
#undef CAMS
    uint64_t game[] = {0x101, ANA, 0x103, BIA, CAIO};
    CHECK(roomnet_pictures(&chat, game, 5, out) == 4 && out[0] == 0x101 && out[1] == ANA && out[2] == BIA && out[3] == CAIO);   // 0x103: not chosen
    // a matchmade race: the relay says no room. Nobody, at once, though friends are in the lobby
    CHECK(roomnet_friends(&none, 1, &id, m, &n));
    chatroom_set(id, m, n);
    CHECK(chatroom_refresh() == 1 && chatroom_get(&chat) && chat.id == 0 && chat.n == 0 && chat.friends == 0 && !chatroom_has(BIA));
    CHECK(roomnet_pictures(&chat, game, 5, out) == 0);
    // the friend room again: chosen anew, in seat order (what was on screen in the room before is not kept)
    room.member[0].camera = 0;
    CHECK(roomnet_friends(&room, 1, &id, m, &n));
    chatroom_set(id, m, n);
#define CAMS 1, 1, 0
    CHECK(chatroom_refresh() == 1 && chatroom_get(&chat) && FACES(&chat, 3, BIA, CAIO, ANA));
#undef CAMS
    // the relay stops speaking rooms (state ver 1), or its state is gone: no friend-room features
    int rooms = roomnet_friends(&old, 1, &id, m, &n);
    chatroom_set(id, rooms ? m : NULL, n);
    CHECK(!rooms && chatroom_refresh() == 1 && chatroom_get(&chat) && chat.id == 0 && chat.n == 0);
    chatroom_set(0x7701, NULL, 3);   // a room id with no list is no room either
    CHECK(chatroom_refresh() == 0);

    printf("test_roomnet: %s\n", failures ? "FAIL" : "PASS");
    return failures != 0;
}
