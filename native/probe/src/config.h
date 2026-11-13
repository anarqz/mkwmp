// Client config read by the probe from <game dir>\mkwmp.toml. Presence of the
// file opts the game into capture; its absence means a stock offline launch.
#ifndef MKWMP_CONFIG_H
#define MKWMP_CONFIG_H

typedef struct {
    int present;        // a config file was found and parsed
    char host[64];      // server host (default 127.0.0.1)
    int port;           // server port (default 8900)
    char name[64];      // player username (may be empty)
    char ticket[64];    // seat ticket from the client (may be empty)
    int slot;           // account slot, 1 or 2 (default 1)
} MkwmpConfig;

// Parse the minimal `key = "value"` / `key = N` format. Pure; unit-tested.
// Unknown keys are ignored. Missing fields keep their defaults.
void mkwmp_config_parse(const char *text, MkwmpConfig *out);

// Load <exe dir>\mkwmp.toml once (cached). Returns a struct with present=0 if
// the file is absent or unreadable, defaults otherwise.
const MkwmpConfig *mkwmp_config(void);

#endif
