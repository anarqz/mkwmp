// Client config read by the probe from <game dir>\mkwmp.toml (written by mkwmp-launcher).
// Presence of the file opts the game into multiplayer; its absence means a stock offline
// launch. Even when present the probe stays dormant (stock offline behaviour) until the game
// asks for the network (Online Play), see seat.h.
#ifndef MKWMP_CONFIG_H
#define MKWMP_CONFIG_H

typedef struct {
    int present;             // a config file was found and parsed
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
} MkwmpConfig;

// Parse the minimal `key = "value"` / `key = N` format. Pure; unit-tested.
// Unknown keys are ignored. Missing fields keep their defaults.
void mkwmp_config_parse(const char *text, MkwmpConfig *out);

// Load <exe dir>\mkwmp.toml once (cached). Returns a struct with present=0 if
// the file is absent or unreadable, defaults otherwise.
const MkwmpConfig *mkwmp_config(void);

// Re-read mkwmp.toml (e.g. the launcher restarted with a new token). Returns the same cache.
const MkwmpConfig *mkwmp_config_reload(void);

#endif
