// The GameChat camera of Mario Kart World 1.8.0, fed by MKWMP (research/docs/CAMERA-RE.md): the player's webcam, or a
// test pattern, shown above the kart, and the other players' pictures above theirs. Plugin backend only, 1.8.0 only,
// and only on a runtime with plugin API 3 (native_image_create / native_image_touch): anything less and nothing is
// overridden, so the runtime's own "no chat is ever started" stubs stay.
//
// How it works. The game talks to GameChat through nn::chat; 19 of those functions and nn::gs::TryWaitNativeSync are
// overridden here (CAMERA_OVERRIDES):
//   - the chat session: IsChatting is 1 while the player is online and has `camera` or `camera_view` on (and gave the
//     18+ consent); GetRoomInfo lists the lobby's members with their camera flags (camera_room_encode); a change
//     signals the room event the game attached;
//   - the pictures: per camera the game makes three native images. Ours come from the plugin API (RGBA8, the runtime's
//     memory). The game cycles them: it asks for the next image (RequestNext*CameraImage gives it a sync handle of
//     ours), polls the handle each frame (TryWaitNativeSync) and shows the image once the poll says done. We answer
//     done only when a newer picture exists, and write it into the image right then, on the game's thread, before
//     answering: the image being written is never the one shown (CameraCycle).
//
// Settings (the mod's, per player; all off unless "1", as voice): `camera` (show my camera), `camera_view` (see the
// others'), `camera_mirror`, `camera_device` (a capture device's name; "" the first one; CAMERA_DEVICE_TEST the test
// pattern). Capture is the separate library native/camera (camlib.c loads it); without a picture from it the local
// camera shows a tile with the player's name.
//
// Environment (tests): MKWMP_CAMERA=0 keeps the whole feature off. MKWMP_CAMERA_LOOPBACK=1 shows the local picture as
// every other member's too, and adds a made-up member when the lobby has none, so one PC exercises the remote path.
// MKWMP_CAMERA_MEMBERS=name,name lists more members by name with their cameras on.
//
// ---- For phase 4 (the relay's video/v1, not written yet) ----------------------------------------------------------
// The transport plugs in here and nowhere else:
//   camera_set_frame_out(fn, user)   every new local picture while the game streams the local camera: encode and send
//   camera_frame_in(account, ...)    a decoded picture of a member: shown at the game's next poll
//   camera_members_set(list, n)      the relay's room: who is there and whose camera is on (replaces the list)
//   camera_subscriptions(out, cap)   the accounts the game asked pictures of (at most 4): the relay's "send me these"
//   camera_local_streaming()         1 while the game wants the local camera sent: the relay's "my camera is on"
// Until camera_members_set is first called, the members are the voice room's peers (cameras off unless loopback).
#ifndef MKWMP_CAMERA_PROBE_H
#define MKWMP_CAMERA_PROBE_H

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
    CAMERA_OVERRIDES = 26,
    CAMERA_API_VERSION = 3,      // the plugin API version with native_image_create / native_image_touch
};
#define CAMERA_DEVICE_TEST "test"   // camera_device: the test pattern instead of a capture device

typedef struct {
    uint64_t account;   // NetworkServiceAccountId: friends_account_id(name)
    uint8_t camera;     // the member's camera is on
} CameraMember;

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
    int (*peers)(uint64_t *accounts, int cap);       // the other members of the lobby (voice room)
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
// What the camera's thread does twice a second: settings, members, the room, the capture wanted. Tests call it.
void camera_refresh(void);

// ---- phase 4's side (see the top of this file); any thread unless said ---------------------------------------------
// fn runs on the game's thread, once per new local picture (CAMERA_W x CAMERA_H RGBA, rows CAMERA_W * 4 apart, valid
// during the call only): copy it and return. ms is plat_ms() at capture. NULL removes it.
typedef void (*CameraFrameOut)(const uint8_t *rgba, int w, int h, uint64_t ms, void *user);
void camera_set_frame_out(CameraFrameOut fn, void *user);
// A member's decoded picture, RGBA, rows w * 4 apart, any size (scaled to CAMERA_W x CAMERA_H; send that size to skip
// it). 1 taken; 0 when the game did not ask for that account's pictures (camera_subscriptions) or the size is absurd.
int camera_frame_in(uint64_t account, const uint8_t *rgba, int w, int h);
void camera_members_set(const CameraMember *members, int n);
int camera_subscriptions(uint64_t *accounts, int cap);
int camera_local_streaming(void);

#endif
