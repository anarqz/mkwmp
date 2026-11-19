/* mkwmp-voice.dll (Linux: libmkwmp-voice.so): lobby voice chat inside the game process (docs/contracts/VOICE_API.md).
 * The probe loads it when the game goes online and drives it through this C ABI; the library owns the audio engine
 * (ForzaInfinity's: WASAPI on Windows, PulseAudio on Linux, AEC3 echo cancellation, AGC, RNNoise, Opus; plus
 * listener-side loudness normalization) and the UDP socket to the lobby's voice relay. Every call returns at once and
 * is safe from any thread; nothing here blocks on a device or the network. */
#ifndef MKWMP_VOICE_H
#define MKWMP_VOICE_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(MKWMP_VOICE_BUILD) && defined(_WIN32)
#define MKWMP_VOICE_API __declspec(dllexport)
#elif defined(MKWMP_VOICE_BUILD)
#define MKWMP_VOICE_API __attribute__((visibility("default")))
#else
#define MKWMP_VOICE_API
#endif

#define MKWMP_VOICE_ABI 1

typedef void (*mkwmp_voice_log_fn)(const char *line);

typedef struct {
    uint64_t account; /* the player's account id (VOICE_API.md §1) */
    uint8_t seat;     /* the relay's voice seat */
    uint8_t talking;  /* heard talking now (a packet within 300 ms, not muted) */
    uint8_t muted;    /* in this player's mute list */
    uint8_t self;     /* this player */
} mkwmp_voice_peer;

enum {
    MKWMP_VOICE_SUBSCRIBED = 1, /* the relay answered: this player is in a voice room */
    MKWMP_VOICE_RUNNING = 2,    /* the audio engine runs */
    MKWMP_VOICE_SENDING = 4,    /* this player's voice is leaving the PC now */
    MKWMP_VOICE_LISTEN_ONLY = 8 /* no microphone: voice is received only */
};

typedef int (*mkwmp_voice_abi_fn)(void);
MKWMP_VOICE_API int mkwmp_voice_abi(void); /* MKWMP_VOICE_ABI */
MKWMP_VOICE_API void mkwmp_voice_init(mkwmp_voice_log_fn log);
/* Join the relay at host:port with the seat ticket (re-callable: a new ticket or relay). */
MKWMP_VOICE_API void mkwmp_voice_connect(const char *host, int port, const char *ticket);
/* Leave voice (sends leave, stops the engine). */
MKWMP_VOICE_API void mkwmp_voice_disconnect(void);
/* The launcher's settings: enabled 0/1, device ids (UTF-8, "" = system default), gains 1..100. */
MKWMP_VOICE_API void mkwmp_voice_settings(int enabled, const char *input_id, const char *output_id, int input_gain, int output_gain);
/* The accounts this player muted (replaces the list; at most 32 are sent to the relay). */
MKWMP_VOICE_API void mkwmp_voice_set_mutes(const uint64_t *accounts, int count);
MKWMP_VOICE_API void mkwmp_voice_set_self(int mic_muted, int deafened);
/* This player's own account id (to mark `self` in peers). */
MKWMP_VOICE_API void mkwmp_voice_set_account(uint64_t account);
/* {"inputs":[{"id","name"}],"outputs":[...]} as this process sees them; enumerates through COM on the calling thread
 * (Linux: PulseAudio, on a helper thread waited for at most 3 s; never call it on a game thread). Returns the length, or -1 if `cap` is too small. */
MKWMP_VOICE_API int mkwmp_voice_devices_json(char *buf, int cap);
/* The room's peers from the relay's last state (this player included). Returns the count written. */
MKWMP_VOICE_API int mkwmp_voice_peers(mkwmp_voice_peer *out, int cap);
MKWMP_VOICE_API int mkwmp_voice_status(void);

/* ---- Friend rooms and video (VOICE_API.md section 6, VIDEO_API.md). MKWMP_VOICE_ABI stays 1: a library without this
 * part has no mkwmp_voice_room_api, and the probe then keeps voice as it was and the camera off the network.
 * Voice and the camera exist only in a private friend room (Online, Friends, create or join a room); the relay knows
 * the room by itself and nothing is asked of it. In room mode the library says so on every control up (self bit 3),
 * reads the relay's room (state down ver 2), subscribes to the pictures the probe chose (op 6) and carries the
 * camera's frames (0x3E95). With a relay that speaks rooms:
 *   - in a friend room (room_id != 0) voice is the whole room's: everyone hears everyone (mutes and deafen as ever);
 *   - in no friend room (room_id 0: a matchmade race or battle) there is no voice at all: the audio engine is
 *     stopped, so the microphone is closed and nothing is played, and no camera frame is sent.
 * A relay that answers state ver 1 knows no friend rooms: voice stays voice/v1 as that relay routes it, no camera.
 * The relay is joined in room mode even with voice chat off in the settings (then muted and deafened, the audio
 * engine not started): the camera needs the room.
 * Room API 2 (1 was the chat rooms asked of the relay with a sharing key and a room ticket: gone). */
#define MKWMP_VOICE_ROOM_API 2
#define MKWMP_VOICE_ROOM_FRIENDS 23 /* the mutual friends one state names at most (a lobby of 24) */
#define MKWMP_VOICE_VIDEO_MAX 4     /* the pictures one player may subscribe to */

typedef struct {
    uint64_t account;
    uint8_t camera; /* the friend's camera is on */
    uint8_t bound;  /* the friend's endpoint is bound at the relay */
} mkwmp_voice_member;

typedef struct {
    int32_t relay;       /* the relay's state version: 0 none yet, 1 a relay without friend rooms, 2 one with */
    int32_t fps_divisor; /* 1, 2 or 4: send every frame, every second or every fourth */
    uint64_t room_id;    /* the friend room; 0: in none */
    int32_t count;       /* this player's mutual friends bound in the room, in seat order */
    mkwmp_voice_member member[MKWMP_VOICE_ROOM_FRIENDS];
} mkwmp_voice_room;

/* A friend's frame, whole (a JPEG as its sender gave it), on the library's network thread: use it and return. */
typedef void (*mkwmp_voice_video_fn)(uint64_t account, const uint8_t *jpeg, int len, void *user);

typedef int (*mkwmp_voice_room_api_fn)(void);
MKWMP_VOICE_API int mkwmp_voice_room_api(void); /* MKWMP_VOICE_ROOM_API */
/* Room mode on or off (off: voice/v1 exactly as before, nothing of the rest is sent). */
MKWMP_VOICE_API void mkwmp_voice_room_mode(int on);
/* This player's camera is on (self bit 2): 0 with the camera off, or after Camera Off in the game's prompt. */
MKWMP_VOICE_API void mkwmp_voice_room_camera(int on);
/* The friends whose pictures are wanted (at most MKWMP_VOICE_VIDEO_MAX, the rest is left out). Sent when it changes
 * and with every refresh. */
MKWMP_VOICE_API void mkwmp_voice_video_subscribe(const uint64_t *accounts, int count);
/* One frame of this player's camera: cut into datagrams and sent now. 1 sent; 0 not (in no friend room, no mutual
 * friend in it, the camera bit is off, a relay without rooms, or over what one sender may send: 64 KiB and 400
 * datagrams a second, and the relay's divisor). */
MKWMP_VOICE_API int mkwmp_voice_video_send(const uint8_t *jpeg, int len, uint32_t ts_ms);
MKWMP_VOICE_API void mkwmp_voice_video_receiver(mkwmp_voice_video_fn fn, void *user);
/* The room as the relay last said. Returns 1 while in room mode with a state from the relay, else 0 (*out zeroed). */
MKWMP_VOICE_API int mkwmp_voice_room_state(mkwmp_voice_room *out);

#ifdef __cplusplus
}
#endif

#endif
