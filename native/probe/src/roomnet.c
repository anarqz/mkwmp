// The chat room on the network (roomnet.h).
#include "roomnet.h"

#include "camera.h"
#include "chatroom.h"
#include "friends.h"
#include "log.h"
#include "plat.h"
#include "seatd.h"
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
    static int api_said = -2, mode = -1, tickets_said = -1, camera_bit = -1, npictures = -1, granted = -1, divisor = 0, hooked;
    static uint64_t pictures[4], counted_at;
    static char asked[200] = "\x01";

    int api = voice_room_api();   // -1 until the game went online once and the library is loaded
    if (api != api_said && api >= 0) {
        mkwmp_log(api >= 1 ? "room: the voice library has room API %d: chat rooms and the camera go through the relay" :
                             "room: the voice library has no room API (an older mkwmp-voice): chat rooms stay local, the camera "
                             "is not sent or received", api);
        api_said = api;
    }
    if (api < 1) return;
    if (!hooked) {   // once: the two ends of the camera's transport
        voice_video_receiver(jpeg_in, NULL);
        camera_set_jpeg_out(jpeg_out, NULL);
        hooked = 1;
    }
    int tickets = seatd_started();
    if (tickets != tickets_said) {
        if (!tickets) mkwmp_log("room: a launcher holds the account (no sign-in of the plugin's own): no room tickets, chat rooms stay local");
        tickets_said = tickets;
    }
    int voice_on = voice_enabled(), room_only = chatroom_voice_filter(), chat = camera_chat_on();
    int on = roomnet_mode(api, tickets, voice_on, room_only, chat);
    if (on != mode) {
        mkwmp_log("room: room mode %s (voice chat %s%s, camera chat %s)%s", on ? "on" : "off", voice_on ? "on" : "off",
                  room_only ? ", shared with the room only" : ", lobby-wide (MKWMP_VOICE_ROOM=0)", chat ? "on" : "off",
                  on && voice_on && !room_only ? ": the camera needs the room, so the relay shares voice with the room only too" : "");
        voice_room_mode(on);
        mode = on;
        if (!on) {
            voice_room_request("", NULL, 0, "");
            voice_video_subscribe(NULL, 0);
            voice_room_camera(0);
            chatroom_grant(0, NULL, -1);   // back to the local pick
            camera_set_send_divisor(1);
            camera_bit = npictures = granted = -1;
            divisor = 0;
            asked[0] = 1;
        }
    }
    if (!on) return;

    // 1. the request: the game's sharing key, the friends wanted and the seat server's ticket naming them
    ChatRoomRequest req;
    uint64_t want[CHATROOM_FRIENDS];
    char names[CHATROOM_FRIENDS][FRIEND_NAME_SIZE];
    const char *list[CHATROOM_FRIENDS];
    int nnames = 0, nwant = 0;
    SeatApiRoomTicket ticket;
    memset(&ticket, 0, sizeof ticket);
    int ask = chatroom_request(&req);
    for (int i = 0; ask && i < req.n && nnames < CHATROOM_FRIENDS; i++)
        if (friends_name_of(req.friends[i], names[nnames], sizeof names[0]) && seatapi_name_ok(names[nnames])) list[nnames] = names[nnames], nnames++;
    if (ask && nnames && seatd_room_ticket(list, nnames, &ticket))
        for (int i = 0; i < ticket.nfriends && nwant < CHATROOM_FRIENDS; i++) want[nwant++] = friends_account_id(ticket.friends[i]);
    char now[sizeof asked], who[4 * 24 + 8];
    names_of(want, nwant, who, sizeof who);
    snprintf(now, sizeof now, "%s|%s", ask && nwant ? req.key : "", who);
    if (strcmp(now, asked)) {
        memcpy(asked, now, sizeof asked);
        if (ask && nwant)
            mkwmp_log("room: asking the relay for a chat room: sharing key \"%s\", %d friend(s) wanted:%s (room ticket of %d bytes)",
                      req.key, nwant, who, (int)strlen(ticket.ticket));
        else
            mkwmp_log("room: nothing to ask the relay (%s)", !ask ? "the game set no sharing key: not in a game session" :
                      !req.n ? "no friend to share with" : !nnames ? "the friends' names are not known yet" :
                      "no room ticket from the seat server");
    }
    if (ask && nwant) voice_room_request(req.key, want, nwant, ticket.ticket);
    else voice_room_request("", NULL, 0, "");

    // 2. this player's camera, as the game streams it (0 with the camera off, or after Camera Off in the game's prompt)
    int camera = camera_local_streaming();
    if (camera != camera_bit) {
        mkwmp_log("room: my camera is %s for the relay (self bit 2)", camera ? "on" : "off");
        voice_room_camera(camera);
        camera_bit = camera;
    }

    // 3. the grant: the relay's room is the chat room
    mkwmp_voice_room room;
    ChatMember members[CHATROOM_FRIENDS];
    uint64_t id = 0;
    int n = 0, have = voice_room_state(&room);
    int grant = roomnet_grant(&room, have, &id, members, &n);
    if (grant) chatroom_grant(id, members, n);
    else chatroom_grant(0, NULL, -1);
    int kind = !have ? 0 : room.relay == 2 ? 2 : 1;
    if (kind != granted) {
        mkwmp_log(kind == 2 ? "room: the relay speaks chat rooms: the room is the one it grants (GetRoomInfo lists its members)" :
                  kind == 1 ? "room: the relay has no chat rooms (state ver 1): no grant; the room is the local pick, no camera is sent "
                              "or shown, voice is the lobby's filtered to the room here" :
                              "room: no state from the relay yet: the room is the local pick");
        granted = kind;
    }
    int d = have && (room.fps_divisor == 2 || room.fps_divisor == 4) ? room.fps_divisor : 1;
    if (d != divisor) {
        if (divisor || d != 1) mkwmp_log("room: the relay's send divisor is %d: the camera sends %s", d, d == 1 ? "ten frames a second" :
                                         d == 2 ? "five frames a second" : "a frame every 400 ms");
        camera_set_send_divisor(d);
        divisor = d;
    }

    // 4. the pictures wanted: what the game asked for, of the room's members with a camera
    uint64_t game[CAMERA_REMOTE], pics[4];
    int ngame = camera_subscriptions(game, CAMERA_REMOTE);
    int np = roomnet_pictures(have ? &room : NULL, game, ngame, pics);
    if (np != npictures || memcmp(pics, pictures, (size_t)np * sizeof pics[0])) {
        names_of(pics, np, who, sizeof who);
        mkwmp_log("room: video subscription:%s (the game asked pictures of %d member(s))", who, ngame);
        memcpy(pictures, pics, sizeof pictures);
        npictures = np;
    }
    voice_video_subscribe(pics, np);

    // 5. a line every 10 s while frames move
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
            mkwmp_log("room: camera frames in 10 s: %u sent (%u not: no room, or over the sender's limits; last %d bytes), %u "
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
        mkwmp_log("room: loopback (MKWMP_CAMERA_LOOPBACK=1): the chat room stays local, nothing is asked of the relay");
        return;
    }
    if (!plat_thread_start(worker, NULL)) mkwmp_log("room: no thread: chat rooms stay local");
}
