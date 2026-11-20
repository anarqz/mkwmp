// The console-style overlay (launcher/docs/OVERLAY.md): in the game, the Guide button, Select+Start, Select held for a
// second or Shift+Tab bring the MKWMP launcher over the game instead of mkw-recomp's menu, with the picture the game
// was showing; the launcher gives the game back (docs/contracts/LAUNCHER_API.md, "The overlay").
//
// The mod's part, plugin build only:
//   - the trigger, on the pad as the game reads it (plugin API 4 `input_filter`: what Steam Input or another remapper
//     made of it), so a trigger never reaches the game or mkw-recomp's own menu shortcut. On plugin API 3 there is no
//     filter: the mod reads the game's SDL3 itself and can only watch (see overlay.c);
//   - the game reads an idle pad while the overlay is up and, after it closes, until every button is up;
//   - the picture (plugin API 4 `frame_capture`), handed to the launcher with the request;
//   - AllowSetForegroundWindow, so the launcher may come to the front on Windows;
//   - the game behind the overlay as behind a console's HOME menu (plugin API 4 `focus_set`): told it is out of focus,
//     silent, and suspended when the game itself lets the console suspend it (MKW: always, except while it uses the
//     network, so an online race runs on). The mod's setting overlay_pause = 0 keeps it running;
//   - "Quit Game" from the overlay (the launcher's action "quit"): the runtime's own clean end (plugin API 4 `quit`);
//   - the wait for the launcher's "back to the game".
// Nothing of it happens without a launcher that answers /local/v1/overlay/state: then every button is the game's and
// mkw-recomp's, as before.
#ifndef MKWMP_OVERLAY_H
#define MKWMP_OVERLAY_H

#include <stdint.h>

// ---- pure, unit-tested (tests/test_overlay.c) ----------------------------------------------------------------------

// The pad's buttons as mkw-recomp hands them to the game (plugin API 4 MKW_PAD_*; runtime/input/input_map.h B_*).
enum {
    OVERLAY_BTN_PLUS = 1u << 10,    // Start / Menu
    OVERLAY_BTN_MINUS = 1u << 11,   // Select / Back / View
};
enum {
    OVERLAY_HOLD_MS = 1000,     // Select held this long is the trigger (Steam takes Guide and Select+Start)
    OVERLAY_REPLAY_MS = 60,     // a shorter Select press, given back to the game after its release
    OVERLAY_STICK_IDLE = 8000,  // a stick inside this is at rest (of 32767)
};

typedef struct {
    uint64_t buttons;
    int32_t lx, ly, rx, ry;
    int guide;   // the Guide (Xbox / PS / Home) button held
} OverlayPad;

typedef enum {
    OVERLAY_BY_NONE,
    OVERLAY_BY_GUIDE,   // the Guide button
    OVERLAY_BY_CHORD,   // Select + Start together
    OVERLAY_BY_HOLD,    // Select held for OVERLAY_HOLD_MS
    OVERLAY_BY_KEYS,    // Shift+Tab
} OverlayTrigger;

typedef struct {
    int idle;             // after a trigger or a close: the game reads an idle pad until every button is up
    int guide_was;
    int select_held;      // Select is down and held back from the game
    int select_fired;     // ... and it already triggered (the hold, or the chord)
    uint64_t select_since;
    uint64_t replay_until;
} OverlayGate;

// One reading of player 1's pad, at now_ms (monotonic). Edits *pad to what the game (and mkw-recomp's menu shortcut)
// may see and returns the trigger in the reading where it happens, else OVERLAY_BY_NONE.
//   armed 0: the overlay cannot open (no launcher): the reading passes untouched and the gate forgets what it held.
//   open 1:  the overlay is up: the reading comes back idle, and a trigger still answers (the launcher comes back to
//            the front if the game took the focus meanwhile).
// Guide never reaches the game. Select reaches it only as a short press, late: on its release, for OVERLAY_REPLAY_MS.
OverlayTrigger overlay_gate(OverlayGate *g, OverlayPad *pad, uint64_t now_ms, int armed, int open);

// The overlay closed (or a trigger was refused): idle until every button is up, so the B that closed it stays there.
void overlay_gate_closed(OverlayGate *g);

// The launcher's answer to GET /local/v1/overlay/state and POST /local/v1/overlay/open, as far as the mod reads it:
// {"open":true|false,"seq":N,"enabled":true|false,"action":"resume"|"menu"|"quit"}. 1 when "open" is there.
typedef struct {
    int open;
    long long seq;
    int enabled;   // the launcher can show itself over the game now (absent: yes)
    int menu;      // action "menu": mkw-recomp's own menu after the return
    int quit;      // action "quit": the game ends (the player chose Quit Game in the overlay)
} OverlayState;
int overlay_parse_state(const char *json, OverlayState *out);

// ---- the overlay of this process -----------------------------------------------------------------------------------

struct MkwPluginApi;
// From mkw_plugin_init, when the game is set up for a launcher (mkwmp.toml names one): registers the pad filter where
// the runtime has one and starts the overlay's thread. Without a launcher configured it does nothing.
void overlay_install(const struct MkwPluginApi *api);

#endif
