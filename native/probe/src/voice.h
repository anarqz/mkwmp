// Voice chat, probe side (docs/contracts/VOICE_API.md §4, §5, §6). With the plugin backend and a relay that speaks
// friend rooms, voice exists in a private friend room only, for everyone in it; a matchmade race or battle has none
// (room mode, roomnet.c: the relay routes it, and the voice library keeps the engine off outside a room). Nothing is
// filtered here. The probe loads mkwmp-voice.dll (the engine and
// the relay client, native/voice) from the game folder the first time the game goes online, feeds it the seat ticket
// and the relay address, and polls the launcher's voice settings (GET /local/v1/voice) every 2 s. Mutes chosen in
// the game's menus go to the engine (and through it to the relay) and to the launcher (POST /local/v1/voice/mute).
// Without the DLL, a launcher or a ticket, voice simply stays off.
#ifndef MKWMP_VOICE_PROBE_H
#define MKWMP_VOICE_PROBE_H

#include "../../voice/src/mkwmp_voice.h"

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
// For the camera (camera.c): 1 between voice_online and voice_offline, whatever the voice settings.
int voice_is_online(void);
// 1 while voice runs in a room (the engine is on and the relay has this player in a lobby).
int voice_active(void);

// ---- friend rooms and video (roomnet.c drives them; mkwmp_voice.h's second part). Any thread.
// The loaded voice library's room API version: -1 while no library is loaded (the game was never online, or voice is
// off on the server), 0 a library without room API 2 (an older mkwmp-voice: no room mode, the camera stays off the
// network).
int voice_room_api(void);
// 1 when voice chat is on in the settings (the launcher's or the mod's), as last read.
int voice_enabled(void);
// The library's calls; nothing without the room API. Room mode on: the relay is joined with voice chat off too (the
// camera needs the room), and a relay that speaks friend rooms carries voice in a friend room only.
void voice_room_mode(int on);
void voice_room_camera(int on);
void voice_video_subscribe(const uint64_t *accounts, int count);
int voice_video_send(const uint8_t *jpeg, int len, uint32_t ts_ms);
void voice_video_receiver(mkwmp_voice_video_fn fn, void *user);
int voice_room_state(mkwmp_voice_room *out);

#endif
