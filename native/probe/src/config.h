// Client config read by the probe from <game dir>\mkwmp.toml (written by the launcher).
// Presence of the file opts the game into multiplayer; its absence means a stock offline
// launch. Even when present the probe stays dormant (stock offline behaviour) until the game
// asks for the network (Online Play), see seat.h.
#ifndef MKWMP_CONFIG_H
#define MKWMP_CONFIG_H

#include <stddef.h>

// Whose servers the game plays on (`backend = "nextendo"`; absent or anything else: an MKWMP server).
// docs/contracts/NEXTENDO_API.md.
typedef enum { BACKEND_MKWMP = 0, BACKEND_NEXTENDO } MkwmpBackend;

typedef struct {
    int present;             // a config file was found and parsed
    int backend;             // MkwmpBackend. Nextendo: no server / ticket / voice (voice_port is 0, ticket empty)
    char nextendo_ca[96];    // Nextendo's CA, a file beside mkwmp.toml (PEM); default "nextendo-ca.pem"
    char host[128];          // lobby host: IP literal or hostname (default 127.0.0.1)
    int port;                // lobby port (default 8900)
    char name[64];           // player username (may be empty)
    char ticket[520];        // seat ticket (launcher seat = "start"; may be empty)
    int slot;                // account slot, 1 or 2 (default 1)
    char launcher_host[64];  // launcher local seat API (`launcher = "127.0.0.1:47820"`)
    int launcher_port;       // 0 = no launcher configured
    char launcher_token[96]; // bearer for the local seat API
    char lobby_ca[96];       // file (in the game dir) with the lobby's TLS CA, PEM or DER;
                             // default "mkwmp-lobby-ca.pem" (see lobby_ca.h)
    int voice_port;          // lobby voice relay UDP port (default 8907, 0 = voice off; VOICE_API.md)
    int direct;              // no launcher: the plugin signs in itself (seatd.c, mkwmp_config_use_direct)
    char launcher_exe[300];  // the launcher's executable (`launcher_exe = "C:/.../mkwmp.exe"`, forward
                             // slashes), for the mod to start it as its overlay host (overlay.c); may be empty
} MkwmpConfig;

// Parse the minimal `key = "value"` / `key = N` format. Pure; unit-tested.
// Unknown keys are ignored. Missing fields keep their defaults.
void mkwmp_config_parse(const char *text, MkwmpConfig *out);

// Load <exe dir>\mkwmp.toml once (cached). Returns a struct with present=0 if
// the file is absent or unreadable, defaults otherwise.
const MkwmpConfig *mkwmp_config(void);

// <the folder mkwmp.toml was read from>/name (home, or the executable's folder; home when there is none): the files
// the launcher writes beside it (the lobby CA). 1 on success.
int mkwmp_config_path(char *out, size_t cap, const char *name);

// The plugin build without a launcher: the player's own account from the mod's settings. The config is then present
// (the probe engages), direct, and named name; the launcher's local API is answered by seatd.c. A mkwmp.toml with a
// `launcher` key still wins (the MKWMP launcher runs: it holds the seat).
void mkwmp_config_use_direct(const char *name);
// The mod, when the launcher a leftover mkwmp.toml names is not running: forget that launcher (and the file).
void mkwmp_config_ignore_launcher(void);
// The voice relay's port as the seat server announced it (direct mode; 0 = voice off).
void mkwmp_config_set_voice_port(int port);
// A seat service answers /local/v1/... : the launcher, or the plugin itself (direct).
int mkwmp_config_has_seat(const MkwmpConfig *c);

// Re-read mkwmp.toml (e.g. the launcher restarted with a new token). Returns the same cache. The backend stays the
// one the process started with: the game cannot change networks while it runs.
const MkwmpConfig *mkwmp_config_reload(void);

// The session plays on Nextendo Network (mkwmp_config()->backend, for readers who want the question).
int mkwmp_config_nextendo(void);

#endif
