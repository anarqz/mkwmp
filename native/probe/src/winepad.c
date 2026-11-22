// The controller under Wine / Proton: XInput asked from the mod (winepad.h says why and when).
#include "winepad.h"

#include "log.h"
#include "plat.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
#endif

const char *winepad_wine_version(void) {
#ifdef _WIN32
    HMODULE nt = GetModuleHandleA("ntdll.dll");   // in every process, never unloaded: nor is the string it returns
    const char *(*get)(void) = nt ? (const char *(*)(void))(void (*)(void))GetProcAddress(nt, "wine_get_version") : NULL;
    return get ? get() : NULL;
#else
    return NULL;
#endif
}

int winepad_touch(WinepadGetState get) {
    if (!get) return -1;
    int mask = 0;
    for (uint32_t i = 0; i < 4; i++) {
        uint8_t state[16];   // XINPUT_STATE: a DWORD packet number and a 12-byte XINPUT_GAMEPAD
        if (get(i, state) == 0) mask |= 1 << i;   // ERROR_SUCCESS; ERROR_DEVICE_NOT_CONNECTED otherwise
    }
    return mask;
}

const char *winepad_slots(int mask, char *buf, size_t size) {
    if (mask < 0) return "no XInput";
    if (!mask) return "none";
    size_t len = 0;
    if (size) buf[0] = 0;
    for (int i = 0; i < 4; i++)
        if (mask & (1 << i)) {
            int n = snprintf(buf + len, len < size ? size - len : 0, "%s%d", len ? " " : "", i);
            if (n > 0) len += (size_t)n;
        }
    return buf;
}

#ifdef _WIN32
// The XInput DLLs Windows and Wine ship, newest first. The one found stays loaded: Wine's XInput keeps a thread of its own.
static WinepadGetState load_xinput(void) {
    static const char *const dlls[] = {"xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll"};
    for (int i = 0; i < 3; i++) {
        HMODULE x = LoadLibraryA(dlls[i]);
        if (!x) continue;
        WinepadGetState get = (WinepadGetState)(void (*)(void))GetProcAddress(x, "XInputGetState");
        if (get) {
            mkwmp_log("winepad: %s loaded", dlls[i]);
            return get;
        }
        FreeLibrary(x);
    }
    return NULL;
}
#endif

static struct {
    WinepadGetState get;
    int rounds;
    uint32_t interval_ms;
    int last;
    void (*done)(void *user);
    void *user;
} g_pad;

static void asks(void *arg) {
    (void)arg;
    char b[16];
    for (int round = 1; round <= g_pad.rounds; round++) {
        plat_sleep_ms(g_pad.interval_ms);
        int mask = winepad_touch(g_pad.get);
        if (mask != g_pad.last)
            mkwmp_log("winepad: XInput slots connected: %s (ask %d, %.1f s after load)", winepad_slots(mask, b, sizeof b),
                      round, round * g_pad.interval_ms / 1000.0);
        g_pad.last = mask;
    }
    mkwmp_log("winepad: done after %d asks; XInput slots connected: %s", g_pad.rounds + 1, winepad_slots(g_pad.last, b, sizeof b));
    if (g_pad.done) g_pad.done(g_pad.user);
}

int winepad_start_with(const char *wine, WinepadGetState get, int rounds, uint32_t interval_ms,
                       void (*done)(void *user), void *user) {
    if (!wine) return 0;
    const char *off = getenv("MKWMP_WINEPAD");
    if (off && !strcmp(off, "0")) {
        mkwmp_log("winepad: Wine %s: XInput left alone (MKWMP_WINEPAD=0)", wine);
        return -1;
    }
    if (!get) {
        mkwmp_log("winepad: Wine %s: no XInput DLL, SDL finds the controller on its own", wine);
        return -1;
    }
    char b[16];
    int mask = winepad_touch(get);   // before mkw-recomp's input starts SDL's gamepads (its first poll, after load)
    mkwmp_log("winepad: Wine %s: XInput asked at plugin load, slots connected: %s; again every %.1f s for %.0f s", wine,
              winepad_slots(mask, b, sizeof b), interval_ms / 1000.0, rounds * interval_ms / 1000.0);
    g_pad.get = get;
    g_pad.rounds = rounds;
    g_pad.interval_ms = interval_ms;
    g_pad.last = mask;
    g_pad.done = done;
    g_pad.user = user;
    if (rounds > 0 && !plat_thread_start(asks, NULL)) {
        mkwmp_log("winepad: no thread for the later asks");
        if (done) done(user);
    } else if (rounds <= 0 && done) {
        done(user);
    }
    return 1;
}

int winepad_start(void) {
    const char *wine = winepad_wine_version();
    if (!wine) return 0;   // Windows itself sends SDL its device changes: nothing to do there
#ifdef _WIN32
    const char *off = getenv("MKWMP_WINEPAD");
    WinepadGetState get = off && !strcmp(off, "0") ? NULL : load_xinput();   // off: winepad_start_with says so
#else
    WinepadGetState get = NULL;
#endif
    return winepad_start_with(wine, get, WINEPAD_ROUNDS, WINEPAD_INTERVAL_MS, NULL, NULL);
}
