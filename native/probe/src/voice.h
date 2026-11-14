// Lobby voice chat, probe side (docs/contracts/VOICE_API.md §4, §5). The probe loads mkwmp-voice.dll (the engine and
// the relay client, native/voice) from the game folder the first time the game goes online, feeds it the seat ticket
// and the relay address, and polls the launcher's voice settings (GET /local/v1/voice) every 2 s. Mutes chosen in
// the game's menus go to the engine (and through it to the relay) and to the launcher (POST /local/v1/voice/mute).
// Without the DLL, a launcher or a ticket, voice simply stays off.
#ifndef MKWMP_VOICE_PROBE_H
#define MKWMP_VOICE_PROBE_H

#include <stddef.h>
#include <stdint.h>

enum { VOICE_MAX_MUTES = 64, VOICE_DEVICE_ID = 256 };

typedef struct {
    int enabled;                   // default 1
    char input[VOICE_DEVICE_ID];   // "" = system default
    char output[VOICE_DEVICE_ID];
    int input_gain, output_gain;   // 1..100, default 100
    int mute_count;
    uint64_t mutes[VOICE_MAX_MUTES];
} VoiceSettings;

// Pure parser of a GET /local/v1/voice answer. Returns 1 for a usable 200 (fields missing keep their defaults), 0
// otherwise (and *out is the defaults). Unit-tested.
int voice_parse_settings(int http_status, const char *body, VoiceSettings *out);

// The game went online (seated): load the engine if needed and join the relay of `lobby_host`.
void voice_online(const char *lobby_host);
// The player left online.
void voice_offline(void);

// For the game's menus (any thread, never blocks): is this account muted by the player; is it talking now.
int voice_is_muted(uint64_t account);
int voice_is_talking(uint64_t account);
// Mute or unmute a player from the game's menus (`name` may be NULL). Applies at once; the launcher is told on the
// voice thread.
void voice_set_muted(uint64_t account, const char *name, int muted);
// 1 while voice runs in a room (the engine is on and the relay has this player in a lobby).
int voice_active(void);

#endif
