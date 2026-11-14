/* mkwmp-voice.dll: lobby voice chat inside the game process (docs/contracts/VOICE_API.md). The probe (winmm.dll)
 * loads it when the game goes online and drives it through this C ABI; the DLL owns the audio engine (ForzaInfinity's:
 * WASAPI, AEC3 echo cancellation, AGC, RNNoise, Opus; plus listener-side loudness normalization) and the UDP socket to
 * the lobby's voice relay. Every call returns at once and is safe from any thread; nothing here blocks on a device or
 * the network. */
#ifndef MKWMP_VOICE_H
#define MKWMP_VOICE_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifdef MKWMP_VOICE_BUILD
#define MKWMP_VOICE_API __declspec(dllexport)
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
 * (never call it on a game thread). Returns the length, or -1 if `cap` is too small. */
MKWMP_VOICE_API int mkwmp_voice_devices_json(char *buf, int cap);
/* The room's peers from the relay's last state (this player included). Returns the count written. */
MKWMP_VOICE_API int mkwmp_voice_peers(mkwmp_voice_peer *out, int cap);
MKWMP_VOICE_API int mkwmp_voice_status(void);

#ifdef __cplusplus
}
#endif

#endif
