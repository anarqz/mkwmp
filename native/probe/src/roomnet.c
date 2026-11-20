// The friend room on the network (roomnet.h).
#include "roomnet.h"

#include "camera.h"
#include "chatroom.h"
#include "log.h"
#include "plat.h"
#include "voice.h"

#include <stdio.h>
#include <string.h>

// ---- the camera's frames: out on the game's thread, in on the voice library's network thread

static PlatLock g_lock = PLAT_LOCK_INIT;   // the counts below
static struct {
    unsigned sent, held;                    // frames given to the library: on the wire, not sent
    unsigned shown, refused;                // members' frames: shown, not taken by the camera
    int last_out, last_in;                  // bytes
    uint64_t last_from;
} g_count;

static void jpeg_out(const uint8_t *jpeg, int len, uint64_t ms, void *user) {
    (void)user;
    int sent = voice_video_send(jpeg, len, (uint32_t)ms);
    plat_lock(&g_lock);
    if (sent) g_count.sent++, g_count.last_out = len;
    else g_count.held++;
    plat_unlock(&g_lock);
}

static void jpeg_in(uint64_t account, const uint8_t *jpeg, int len, void *user) {
    (void)user;
    int shown = camera_jpeg_in(account, jpeg, len);   // 0: the game did not ask for this member's pictures, or no JPEG
    plat_lock(&g_lock);
    if (shown) g_count.shown++, g_count.last_in = len, g_count.last_from = account;
    else g_count.refused++;
    plat_unlock(&g_lock);
}

// ---- the thread

static void names_of(const uint64_t *accounts, int n, char *out, size_t cap) {
    size_t k = 0;
    out[0] = 0;
    for (int i = 0; i < n && k + 24 < cap; i++) k += (size_t)snprintf(out + k, cap - k, " %#llx", (unsigned long long)accounts[i]);
    if (!n) snprintf(out, cap, " nobody");
}

static void tick(void) {
    static int api_said = -2, mode = -1, camera_bit = -1, npictures = -1, kind_said = -1, divisor = 0, hooked;
    static uint64_t pictures[CHATROOM_FRIENDS], counted_at;
    char who[CHATROOM_FRIENDS * 24 + 8];

    int api = voice_room_api();   // -1 until the game went online once and the library is loaded
    if (api != api_said && api >= 0) {
        mkwmp_log(roomnet_mode(api) ? "room: the voice library has room API %d: friend rooms and the camera go through the relay" :
                                      "room: the voice library has no room API 2 (an older mkwmp-voice): no friend room is known, the "
                                      "camera is not sent or received", api);
        api_said = api;
    }
    if (!roomnet_mode(api)) return;
    if (!hooked) {   // once: the two ends of the camera's transport
        voice_video_receiver(jpeg_in, NULL);
        camera_set_jpeg_out(jpeg_out, NULL);
        hooked = 1;
    }
    if (mode != 1) {
        mkwmp_log("room: room mode on: the relay is told on every control up, and says which friend room the player is in");
        voice_room_mode(1);
        mode = 1;
    }

    // 1. this player's camera, as the game streams it (0 with the camera off, outside a friend room, or after Camera
    //    Off in the game's prompt)
    int camera = camera_local_streaming();
    if (camera != camera_bit) {
        mkwmp_event("room: my camera is %s for the relay (self bit 2)", camera ? "on" : "off");
        voice_room_camera(camera);
        camera_bit = camera;
    }

    // 2. the relay's room is the chat room: the friend room and the mutual friends in it, of whom four faces are chosen
    mkwmp_voice_room room;
    ChatMember friends[CHATROOM_ROOM_MAX];
    uint64_t id = 0;
    int n = 0, have = voice_room_state(&room);
    int rooms = roomnet_friends(&room, have, &id, friends, &n);
    chatroom_set(id, rooms ? friends : NULL, n);
    chatroom_refresh();
    int kind = !have ? 0 : rooms ? 2 : 1;
    if (kind != kind_said) {
        mkwmp_log(kind == 2 ? "room: the relay speaks friend rooms: voice for everyone in the player's friend room, the camera "
                              "between mutual friends there (GetRoomInfo lists at most four of them); none of it in a matchmade lobby" :
                  kind == 1 ? "room: the relay knows no friend rooms (state ver 1): no camera is sent or shown; voice is voice/v1 as "
                              "that relay routes it" :
                              "room: no state from the relay yet: no friend room");
        kind_said = kind;
    }
    int d = have && (room.fps_divisor == 2 || room.fps_divisor == 4) ? room.fps_divisor : 1;
    if (d != divisor) {
        if (divisor || d != 1) mkwmp_log("room: the relay's send divisor is %d: the camera sends %s", d, d == 1 ? "ten frames a second" :
                                         d == 2 ? "five frames a second" : "a frame every 400 ms");
        camera_set_send_divisor(d);
        divisor = d;
    }

    // 3. the pictures wanted: what the game asked for, of the faces chosen that have a camera
    ChatRoom faces;
    uint64_t game[CAMERA_REMOTE], pics[CHATROOM_FRIENDS];
    chatroom_get(&faces);
    int ngame = camera_subscriptions(game, CAMERA_REMOTE);
    int np = roomnet_pictures(&faces, game, ngame, pics);
    if (np != npictures || memcmp(pics, pictures, (size_t)np * sizeof pics[0])) {
        names_of(pics, np, who, sizeof who);
        mkwmp_event("room: video subscription:%s (the game asked pictures of %d member(s); %d mutual friend(s) in the friend room)", who,
                  ngame, faces.friends);
        memcpy(pictures, pics, (size_t)np * sizeof pics[0]);
        npictures = np;
    }
    voice_video_subscribe(pics, np);
    // 4. a line every 10 s while frames move
    uint64_t t = plat_ms();
    if (t - counted_at >= 10000) {
        counted_at = t;
        plat_lock(&g_lock);
        unsigned sent = g_count.sent, held = g_count.held, shown = g_count.shown, refused = g_count.refused;
        int last_out = g_count.last_out, last_in = g_count.last_in;
        uint64_t from = g_count.last_from;
        g_count.sent = g_count.held = g_count.shown = g_count.refused = 0;
        plat_unlock(&g_lock);
        if (sent || shown || refused || (held && camera && id))
            mkwmp_log("room: camera frames in 10 s: %u sent (%u not: no friend in the room, or over the sender's limits; last %d bytes), %u "
                      "received and shown (%u not taken; last %d bytes from %#llx)", sent, held, last_out, shown, refused, last_in,
                      (unsigned long long)from);
    }
}

static void worker(void *unused) {
    (void)unused;
    for (;;) {
        tick();
        plat_sleep_ms(500);
    }
}

void roomnet_start(void) {
    static int started;
    char v[8] = "";
    if (started) return;
    started = 1;
    if (plat_getenv("MKWMP_CAMERA_LOOPBACK", v, sizeof v) && !strcmp(v, "1")) {
        mkwmp_log("room: loopback (MKWMP_CAMERA_LOOPBACK=1): the chat room is made up here, the relay's friend room is not read");
        return;
    }
    if (!plat_thread_start(worker, NULL)) mkwmp_log("room: no thread: no friend room, the camera is not sent or received");
}
