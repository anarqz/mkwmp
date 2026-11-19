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

/* ---- Chat rooms and video (VOICE_API.md section 6, VIDEO_API.md). MKWMP_VOICE_ABI stays 1: a library without this
 * part has no mkwmp_voice_room_api, and the probe then keeps voice as it was and the camera off the network. In room
 * mode the library tells the relay so on every control up (self bit 3), asks for the room the probe wants (op 5),
 * subscribes to the members' pictures (op 6), reads the relay's room (state down ver 2) and carries the camera's
 * frames (0x3E95). It joins the relay in room mode even with voice chat off in the settings (then muted and deafened,
 * the audio engine not started): the camera needs the room. Voice is heard from the room's members only. */
#define MKWMP_VOICE_ROOM_API 1

typedef struct {
    uint64_t account;
    uint8_t camera; /* the member's camera is on */
    uint8_t voice;  /* the member is bound and in the voice room */
} mkwmp_voice_member;

typedef struct {
    int32_t relay;       /* the relay's state version: 0 none yet, 1 a relay without rooms, 2 one with */
    int32_t fps_divisor; /* 1, 2 or 4: send every frame, every second or every fourth */
    uint64_t room_id;    /* 0: in no room */
    int32_t count;       /* members besides this player, at most 4 */
    mkwmp_voice_member member[4];
} mkwmp_voice_room;

/* A member's frame, whole (a JPEG as its sender gave it), on the library's network thread: use it and return. */
typedef void (*mkwmp_voice_video_fn)(uint64_t account, const uint8_t *jpeg, int len, void *user);

typedef int (*mkwmp_voice_room_api_fn)(void);
MKWMP_VOICE_API int mkwmp_voice_room_api(void); /* MKWMP_VOICE_ROOM_API */
/* Room mode on or off (off: voice/v1 exactly as before, nothing of the rest is sent). */
MKWMP_VOICE_API void mkwmp_voice_room_mode(int on);
/* What is asked of the relay: the game's sharing key (at most 39 characters; "" or NULL: no room, leave the one held),
 * the accounts wanted in the room (at most 4) and the seat server's room ticket naming them. Sent when it changes and
 * with every refresh. */
MKWMP_VOICE_API void mkwmp_voice_room_request(const char *sharing_key, const uint64_t *want, int count, const char *room_ticket);
/* This player's camera is on (self bit 2): 0 with the camera off, or after Camera Off in the game's prompt. */
MKWMP_VOICE_API void mkwmp_voice_room_camera(int on);
/* The members whose pictures are wanted (at most 4). */
MKWMP_VOICE_API void mkwmp_voice_video_subscribe(const uint64_t *accounts, int count);
/* One frame of this player's camera: cut into datagrams and sent now. 1 sent; 0 not (no room, the camera bit is off, a
 * relay without rooms, or over what one sender may send: 64 KiB and 400 datagrams a second, and the relay's divisor). */
MKWMP_VOICE_API int mkwmp_voice_video_send(const uint8_t *jpeg, int len, uint32_t ts_ms);
MKWMP_VOICE_API void mkwmp_voice_video_receiver(mkwmp_voice_video_fn fn, void *user);
/* The room as the relay last said. Returns 1 while in room mode with a state from the relay, else 0 (*out zeroed). */
MKWMP_VOICE_API int mkwmp_voice_room_state(mkwmp_voice_room *out);

#ifdef __cplusplus
}
#endif

#endif
