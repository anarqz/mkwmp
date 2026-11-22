// The controller under Wine / Proton (winepad.c): XInput asked from the mod, so that mkw-recomp's SDL sees the pad.
//
// On the ROG Xbox Ally X under Proton (SteamOS Game Mode) the game logged "input: no gamepad" and never found Steam
// Input's pad. A throwaway plugin that only called XInputGetState every 2 s made SDL see it in the same build, and the
// controller then worked for the whole run (21/11/2026). Our fork's unreleased fix/wine-gamepad-rescan does the same
// inside the runtime; the official mkw-recomp 0.1.17 has neither, so the mod does it: under Wine only (ntdll exports
// wine_get_version), once at plugin load, which mkw-recomp runs before its input starts SDL's gamepads, then every
// 2 s for the first 40 s from a thread of its own (the stop-gap's schedule). It draws nothing.
// MKWMP_WINEPAD=0 leaves it out.
#ifndef MKWMP_WINEPAD_H
#define MKWMP_WINEPAD_H

#include <stddef.h>
#include <stdint.h>

// XInputGetState's shape (slot, XINPUT_STATE *): 0 (ERROR_SUCCESS) for a connected slot.
typedef uint32_t (*WinepadGetState)(uint32_t slot, void *state);

#define WINEPAD_ROUNDS 20         // the asks after the first one...
#define WINEPAD_INTERVAL_MS 2000  // ...this far apart: the first 40 s

// Wine's version when the Windows build runs under Wine or Proton, else NULL (Windows itself, Linux).
const char *winepad_wine_version(void);

// XInputGetState on slots 0-3 through get: a bit for each connected slot; -1 without get.
int winepad_touch(WinepadGetState get);

// The slots of winepad_touch's result for the log: "0 2", "none", "no XInput".
const char *winepad_slots(int mask, char *buf, size_t size);

// At plugin load: under Wine, XInput now and then every WINEPAD_INTERVAL_MS for WINEPAD_ROUNDS asks (a thread).
// 1 started; 0 not under Wine (nothing done); -1 turned off (MKWMP_WINEPAD=0) or no XInput DLL.
int winepad_start(void);

// winepad_start with its parts given (tests): wine NULL is "not under Wine", get NULL "no XInput DLL". The thread
// calls done(user) after its last ask when done is not NULL.
int winepad_start_with(const char *wine, WinepadGetState get, int rounds, uint32_t interval_ms,
                       void (*done)(void *user), void *user);

#endif
