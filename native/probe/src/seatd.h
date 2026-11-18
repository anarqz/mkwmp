// The MKWMP launcher's local API (docs/contracts/LAUNCHER_API.md), answered inside the game when no launcher runs: the
// plugin build with the mod's own settings (its options page, modpage.c). seat.c, friends.c and voice.c keep asking
// /local/v1/... through launcher.c, which hands the requests here instead of to a socket (launcher_set_direct), and
// this file calls the seat server itself (seatapi.c).
#ifndef MKWMP_SEATD_H
#define MKWMP_SEATD_H

#include <stddef.h>

typedef struct {
    char server[256];   // the seat server's base URL (seatapi_base)
    char name[17];      // the registered name the player plays as
    char pin[7];
    int voice;          // voice chat on
    // The mod's settings (MkwPluginApi.setting_get / setting_set): the players muted in game are kept there, across
    // games (keys mutes.1..mutes.3, account ids). NULL: kept for this game only.
    int (*setting_get)(const char *key, char *out, size_t cap);
    int (*setting_set)(const char *key, const char *value);
} SeatdConfig;

// Starts answering with this account. The lobby CA the server publishes is written to <home>/mkwmp-lobby-ca.pem.
void seatd_start(const SeatdConfig *cfg);
// launcher.c's direct handler: the same contract as its HTTP answers (status, body in buf).
int seatd_request(const char *method, const char *path, const char *json, char *buf, size_t cap, const char **body);
// The game ends (MkwPluginApi.on_exit): free the seat and end the presence at once, so friends see the player leave
// and the seat goes to the next in line now, not after the server's 60 s timeout. Short timeouts; best effort.
void seatd_stop(void);

#endif
