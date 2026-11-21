// The GameChat camera of Mario Kart World 1.8.0, fed by MKWMP (research/docs/CAMERA-RE.md): the player's webcam, or a
// test pattern, shown above the kart, and the other players' pictures above theirs. Plugin backend only, 1.8.0 only,
// and only on a runtime with plugin API 3 (native_image_create / native_image_touch): anything less and nothing is
// overridden, so the runtime's own "no chat is ever started" stubs stay.
//
// How it works. The game talks to GameChat through nn::chat; 21 of those functions and nn::gs::TryWaitNativeSync are
// overridden here (CAMERA_OVERRIDES):
//   - the chat session: IsChatting is 1 while the player is online (or answering the game's camera prompt on the way
//     there, camera_hold) and has `camera` or `camera_view` on (and gave the 18+ consent). The camera itself exists in
//     a private friend room only (camera_in_room): there GetRoomInfo lists the chat room's members with their camera
//     flags (chatroom.h: at most four of the player's mutual friends in the room; camera_room_encode); in a matchmade
//     race or battle it lists nobody and the player's own camera flag is down, so no face is drawn, captured or sent.
//     IsChatting does not follow the room: the game forgets the prompt's answer when the chat ends, and the prompt
//     comes before any room. A change signals the room event the game attached;
//   - the pictures: per camera the game makes three native images. Ours come from the plugin API (RGBA8, the runtime's
//     memory). The game cycles them: it asks for the next image (RequestNext*CameraImage gives it a sync handle of
//     ours), polls the handle each frame (TryWaitNativeSync) and shows the image once the poll says done. We answer
//     done only when a newer picture exists, and write it into the image right then, on the game's thread, before
//     answering: the image being written is never the one shown (CameraCycle);
//   - the game's own set-up (camera_setup_install; research/docs/CAMERA-RE.md 9): a player's face is drawn and sent
//     only after the player answered the game's "Use the camera?" prompt with Camera On, and the game shows that
//     prompt, when Online Play is chosen, only if the console's camera service says a camera is attached. The
//     runtime's stand-in for that service says none is; while the game's node asks, and the Camera tab has the
//     camera on, ours says yes. MKWMP_CAMERA_SETUP=0 leaves the runtime's answer alone (no prompt, no faces).
//     Camera Off in that prompt turns the local camera off for the session (camera_declined).
//
// Settings (the mod's, per player; all off unless "1", as voice): `camera` (show my camera), `camera_view` (see the
// others'), `camera_mirror`, `camera_device` (a capture device's name; "" the first one; CAMERA_DEVICE_TEST the test
// pattern). Capture is the separate library native/camera (camlib.c loads it); without a picture from it the local
// camera shows a tile with the player's name.
//
// Environment (tests): MKWMP_CAMERA=0 keeps the whole feature off. MKWMP_CAMERA_LOOPBACK=1 shows the local picture as
// every other member's too (nothing is sent or received): the chat room is then made up of the roster's first four
// friends, wherever they are, friend room or not, each by the account id the lobby gives it, which is how the game
// finds the kart to draw a picture above; with nobody at all a made-up member is added, so one PC alone exercises the
// remote path. MKWMP_CAMERA_MEMBERS=name,name lists more members by name with their cameras on (in a room only).
// MKWMP_CAMERA_TRACE=1 logs the flow, bounded: the first calls of every override with their arguments (which game
// object and image a handle belongs to, what was written and whether the runtime took it), a line a second with each
// camera's polls / requests / writes when they change, and the game's own state when it changes (camera.c trace_game:
// what its GameChat mode waits for, whether its screen guard hands cameras to the UI, whether the player went through
// the game's camera set-up, which players the UI may draw a face for, the texture ids it draws).
//
// MKWMP_CAMERA_CODEC=1, with the loopback: the others' picture is the local one as it would arrive over the relay
// (camcodec.h: a 256x144 JPEG of at most 6 KiB, ten a second, decoded and scaled back), so the codec is seen live.
//
// ---- The network (the relay's video/v1: roomnet.c, through the voice library) -------------------------------------
// The transport plugs in here and nowhere else, and only moves bytes (the codec is camcodec.h, called from camera.c):
//   camera_set_jpeg_out(fn, user)    every frame to send while the game streams the local camera: one standalone JPEG
//   camera_jpeg_in(account, ...)     a member's frame as it arrived: decoded, shown at the game's next poll
//   camera_set_send_divisor(n)       the relay's "send fewer": every frame, every second or every fourth
//   (camera_set_frame_out / camera_frame_in are the same two ends before and after the codec, RGBA)
//   chatroom_set(id, friends, n)     (chatroom.h) the friend room and the mutual friends in it, as the relay said
//   camera_subscriptions(out, cap)   the accounts the game asked pictures of (at most 4): the relay's "send me these"
//   camera_local_streaming()         1 while the game wants the local camera sent: the relay's "my camera is on"
// Without a friend room from the relay there are no members and no camera (the loopback makes its own room up).
#ifndef MKWMP_CAMERA_PROBE_H
#define MKWMP_CAMERA_PROBE_H

#include "chatroom.h"

#include <stddef.h>
#include <stdint.h>

enum {
    CAMERA_W = 384,              // the size the game asks for; pictures in and out of this file have it
    CAMERA_H = 216,
    CAMERA_IMAGES = 4,           // images per camera we can hold (the game asks for 3; the SDK allows 4)
    CAMERA_REMOTE = 4,           // remote cameras the game has
    CAMERA_VIDEOS = 1 + CAMERA_REMOTE,
    CAMERA_ROOM_SIZE = 0xf0,     // nn::chat::RoomInfo as the game reads it
    CAMERA_ROOM_MEMBERS = 11,    // remote members that fit; the game does not check the count
    CAMERA_MEMBERS_MAX = 32,
    CAMERA_OVERRIDES = 28,
    CAMERA_API_VERSION = 3,      // the plugin API version with native_image_create / native_image_touch
};
#define CAMERA_DEVICE_TEST "test"   // camera_device: the test pattern instead of a capture device

typedef ChatMember CameraMember;   // { account, camera }: a member of the chat room (chatroom.h)

// What the plugin backend gives the camera (the tests: fakes).
typedef struct {
    uint32_t api_version;        // the runtime's plugin API version
    const char *game_version;    // "1.8.0"
    // MkwPluginApi's (version 3); NULL on an older runtime
    uint32_t (*native_image_create)(int width, int height, void **pixels, int *row_bytes);
    int (*native_image_touch)(uint32_t handle);
    int (*setting_get)(const char *key, char *out, size_t cap);   // the mod's settings; NULL: none (API 1)
    const char *mod_dir;         // where the capture library is
    // the session, asked from the camera's own thread
    int (*online)(void);                             // the player is seated and online
    int (*friends)(uint64_t *accounts, int cap);     // the player's friends (the roster): the loopback's room is made of them; may be NULL
    int (*muted)(uint64_t account);                  // muted by the player: the face is hidden too
    uint64_t (*self_account)(void);
    const char *(*self_name)(void);
    uint64_t (*account_of)(const char *name);        // friends_account_id: for MKWMP_CAMERA_MEMBERS
    int no_thread;               // tests: no thread of the camera's; they call camera_refresh themselves
} CameraHost;

// ---- pure, unit-tested (tests/test_camera.c) -----------------------------------------------------------------------

// NULL when the camera can work with this runtime and game; else why not, as one line for the log. In: the API version,
// whether its two image entries are there, the game's version, whether the mod has settings, MKWMP_CAMERA's value.
const char *camera_unavailable(uint32_t api_version, int has_image_api, const char *game_version, int has_settings,
                               const char *env_switch);

// The settings as the camera reads them: everything off unless "1".
typedef struct {
    int consent, camera, view, mirror;
    char device[256];
} CameraSettings;
void camera_settings_read(int (*setting_get)(const char *key, char *out, size_t cap), CameraSettings *out);
// 1 while the game is told a chat runs.
int camera_chatting(const CameraSettings *s, int online);
// 1 while there is a camera at all: in a friend room (room_id != 0; the loopback's made-up room counts), or while the
// game's "Use the camera?" prompt is held open before the sign-in (prompt: the player's own camera flag only; the
// webcam itself stays closed until a friend room, camera_capture).
// 0 in a matchmade race or battle and in the menus: nobody is listed, the player's own camera is off.
int camera_in_room(uint64_t room_id, int prompt);
// 1 when the webcam is opened: the chat runs, the player's camera is on, the player is in a friend room (room_id != 0)
// and the game streams the local camera. Never for the prompt alone (it gets no picture: the device stays closed in
// the menus), never in a matchmade race or battle or single player.
int camera_capture(int chatting, int camera_on, uint64_t room_id, int streaming);

// The chat held on before the player is online: the game's "Use the camera?" prompt comes when Online Play is chosen,
// before the sign-in, accepts Camera On only with a chat running, and forgets the answer when the chat ends. at:
// plat_ms() when the game asked whether a camera is attached and was told yes (0: no hold). Returns 1 while held:
// until the player went online and left again, or CAMERA_HOLD_MS passed without going online (the prompt cancelled,
// the sign-in failed).
enum { CAMERA_HOLD_MS = 5 * 60 * 1000 };
typedef struct { uint64_t at; int was_online; } CameraHold;
int camera_hold(CameraHold *h, int online, uint64_t now);

// The prompt's other answer. 1 when the player chose Camera Off: the prompt opened at the last Online Play (prompted),
// the player is online (so it was answered: it comes before the sign-in) and the game's flag DisplayFaceIconMyself is
// down (face_flag 0). The game then draws and sends no face of this player, so the plugin's local camera is off too
// for that session: GetRoomInfo's own-camera flag is 0, nothing is captured and nothing reaches camera_set_frame_out's
// function. The others' cameras are still shown when `camera_view` is on. Choosing Online Play again asks again.
int camera_declined(int prompted, int online, int face_flag);

// nn::chat::RoomInfo: zeroes, the player's account at +0x28 and camera flag at +0x30, the member count at +0x38 and
// { u64 account; u8 camera } at +0x40 + i * 0x10. Members with a camera come first and at most CAMERA_REMOTE keep the
// flag (the game has that many remote cameras); the player's own account, account 0 and repeats are left out; at most
// CAMERA_ROOM_MEMBERS are written. Returns the count written.
int camera_room_encode(uint8_t out[CAMERA_ROOM_SIZE], uint64_t self, int self_camera, const CameraMember *members, int n);

// One camera's frame cycle, as the game drives it.
typedef struct {
    int index;            // 0 the local camera, 1..CAMERA_REMOTE the remote ones: part of the sync handle
    uint64_t account;     // whose pictures the game asked for last (remote cameras)
    int target;           // the image the outstanding request fills
    int pending;          // a request is outstanding
    uint32_t seq;         // its number; a handle with another number is of a request long gone
    uint32_t delivered;   // the serial of the source's picture written last (0: none)
} CameraCycle;
// The runtime's own handles are {decoder id, frame ordinal}: ours carry this in the first word's upper bytes.
#define CAMERA_SYNC_MAGIC 0x4d4b574d50434d00ull   // "MKWMPCM", the low byte is the camera's index
static inline int camera_sync_ours(const uint64_t handle[2]) { return (handle[0] & ~0xffull) == CAMERA_SYNC_MAGIC; }
static inline int camera_sync_index(const uint64_t handle[2]) { return (int)(handle[0] & 0xff); }
// RequestNext*CameraImage: the game wants image `image` filled next; handle gets the sync it will poll.
void camera_cycle_request(CameraCycle *v, int image, uint64_t account, uint64_t handle[2]);
// TryWaitNativeSync on one of our handles, the source's newest picture having serial source_serial (0: none yet).
// Returns what to answer: 1 done, 0 not yet. *write is the image to fill with that picture before answering, or -1.
int camera_cycle_poll(CameraCycle *v, const uint64_t handle[2], uint32_t source_serial, int *write);
// Stop*CameraStreaming: whatever was asked is over; a handle still polled answers done.
void camera_cycle_stop(CameraCycle *v);

// The test pattern: eight colour bars moving left one step per tick, a white square bouncing down and up, a red
// corner mark top left (so a flipped or mirrored picture shows), and name in a dark band. RGBA, rows w * 4 apart.
void camera_pattern(uint8_t *rgba, int w, int h, uint32_t tick, const char *name);
// The tile shown for a camera with no picture: dark, with the name (may be NULL or "").
void camera_tile(uint8_t *rgba, int w, int h, const char *name);

// ---- the feature ---------------------------------------------------------------------------------------------------

// From mkw_plugin_init, before the game loads: registers the overrides when camera_unavailable says nothing.
// Returns how many were registered (CAMERA_OVERRIDES, or 0 with the reason logged).
int camera_install(const CameraHost *host);
// Why camera_install left the camera off (NULL when it is on, or before camera_install ran), for the Camera tab.
const char *camera_off_reason(void);
// For the Camera tab when camera_install never ran (online play not set up).
void camera_set_off_reason(const char *why);
// What the camera's thread does twice a second: settings, members, the room, the capture wanted. Tests call it.
void camera_refresh(void);
// The game is loaded and has run nothing yet (the plugin API's address overrides are still open): hooks the game's
// camera set-up by the running build's row, camera_setup_install(game_addrs()). Nothing when the camera is not
// installed.
void camera_game_loaded(void);
// The hook itself, by a row (tests: one over their own memory): the guards, then the update of the node that asks
// whether a camera is attached. 1 hooked; 0 with the reason logged (no row, a guard, MKWMP_CAMERA_SETUP=0).
struct game_addrs;
int camera_setup_install(const struct game_addrs *row);

// ---- the network's side (see the top of this file); any thread unless said -----------------------------------------
// fn runs on the game's thread, once per new local picture (CAMERA_W x CAMERA_H RGBA, rows CAMERA_W * 4 apart, valid
// during the call only): copy it and return. ms is plat_ms() at capture. NULL removes it.
typedef void (*CameraFrameOut)(const uint8_t *rgba, int w, int h, uint64_t ms, void *user);
void camera_set_frame_out(CameraFrameOut fn, void *user);
// A member's decoded picture, RGBA, rows w * 4 apart, any size (scaled to CAMERA_W x CAMERA_H; send that size to skip
// it). 1 taken; 0 when the game did not ask for that account's pictures (camera_subscriptions) or the size is absurd.
int camera_frame_in(uint64_t account, const uint8_t *rgba, int w, int h);
// The same two ends with the codec in between (camcodec.h): what video/v1 carries. fn runs on the game's thread, at
// most CAMCODEC_FPS / divisor times a second, with one standalone baseline JPEG (256x144, at most CAMCODEC_TARGET
// bytes; valid during the call only): chunk it and send it. NULL removes it.
enum { CAMERA_JPEG_MAX = 64 * 1024 };   // a frame larger than this is not looked at
typedef void (*CameraJpegOut)(const uint8_t *jpeg, int len, uint64_t ms, void *user);
void camera_set_jpeg_out(CameraJpegOut fn, void *user);
// 1 (ten frames a second), 2 or 4; anything else is 1. Applies from the next frame.
void camera_set_send_divisor(int divisor);
// A member's frame, reassembled: a baseline JPEG of any size up to 384x216. 1 shown; 0 when the game did not ask for
// that account's pictures (nothing is decoded then), or the bytes are not such a JPEG.
int camera_jpeg_in(uint64_t account, const uint8_t *jpeg, int len);
// (tests) chatroom_set: the friend room and the mutual friends in it; room_id 0: in no friend room.
void camera_members_set(uint64_t room_id, const CameraMember *members, int n);
int camera_subscriptions(uint64_t *accounts, int cap);
int camera_local_streaming(void);
// 1 while the player's Camera Off in the game's prompt holds (camera_declined): the Camera tab says so.
int camera_declined_now(void);

#endif
