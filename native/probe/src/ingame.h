// The in-game overlay: the MKWMP launcher's toasts and its overlay page drawn by the mod inside the game's own frames
// (docs/contracts/LAUNCHER_API.md, "In-game toasts (v1)" and "In-game overlay (v1)"; ported from Infinity's v4
// overlay). The game draws, the launcher decides: the mod asks the launcher's local API what to show (long polls on
// threads of its own) and draws it on the runtime's present thread (plugin API overlay_set), where it never waits,
// reads no guest memory and runs no guest code. Implemented in C++ (src/ingame/); this is what the C core calls.
//
// Tiers, chosen by what the runtime's tables hold (their sizes, never its version string):
//   INGAME_EXACT  plugin API 5 (MkwOverlay.triangles, texture_create, overlay_capture): Infinity's exact look, by the
//                 mod's own Dear ImGui with Nunito; the toasts and the overlay page, the game running on behind it.
//   INGAME_BASIC  plugin API 1 to 4 (mkw-recomp 0.1.18): the toasts with the runtime's rect and text, the same layout
//                 and timing; the overlay page stays the launcher's window (overlay.c: POST /local/v1/overlay/open).
//   INGAME_NONE   no overlay entry, or the drawing faulted: nothing is drawn and nothing is asked, so the launcher
//                 shows its toasts and its overlay itself.
#ifndef MKWMP_INGAME_H
#define MKWMP_INGAME_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

struct MkwPluginApi;
struct MkwPad;

enum { INGAME_NONE = 0, INGAME_BASIC = 1, INGAME_EXACT = 2 };

// What the overlay needs of the mod's C core (overlay.c fills it; the tests fill it with fakes).
typedef struct IngameHost {
    const struct MkwPluginApi *api;
    // One request to the launcher's local API with the session's bearer: the HTTP status (0: it did not answer; -1:
    // there is none to ask). json: the body, or NULL. The answer's body is in buf, NUL-terminated, *answer points at it.
    int (*request)(const char *method, const char *path, const char *json, uint32_t timeout_ms, char *buf, size_t cap,
                   const char **answer);
    void (*log)(const char *fmt, ...);
    uint64_t (*now_ms)(void);                        // monotonic
    void (*sleep_ms)(uint32_t ms);
    int (*thread_start)(void (*fn)(void *arg), void *arg);   // NULL: no threads (the tests step the loops themselves)
    // The page closed (B, Esc, the launcher went away), on the present thread: the game's pad stays idle until every
    // button is up. May be NULL.
    void (*page_closed)(void);
    // The page's own reading of player 1's pad while it has the input: 1 when the player's overlay shortcut was just
    // pressed (it closes the page). May be NULL.
    int (*shortcut)(const struct MkwPad *pad, uint64_t now_ms);
} IngameHost;

// From mkw_plugin_init (overlay_set is only allowed there). Registers the drawing and its fault callback and starts
// the pollers; returns the tier. Logs what it chose.
int ingame_install(const IngameHost *host);
int ingame_tier(void);   // now (INGAME_NONE after a fault)

// The player's overlay shortcut, any thread. 1: the in-game page took it (it opened, or it was open and closed);
// 0: there is no in-game page now (another tier, a launcher without /local/v1/overlay/view, a fault): the caller
// opens the launcher's window overlay as before.
int ingame_page_toggle(const char *by);
int ingame_page_open(void);   // 1 while the in-game page is open (the game reads an idle pad)

#ifdef __cplusplus
}
#endif

#endif
