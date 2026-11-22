// The controller under Wine (winepad.h): XInput's slot mask, the log's slot list, the decision (not under Wine,
// turned off, no XInput) and the asks' schedule, with a fake XInputGetState. On Windows itself winepad_start does
// nothing (no wine_get_version in ntdll).
//
//   $zig = "C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"; $s = "native\probe\src"
//   & $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o scratch\test_winepad.exe `
//       native\probe\tests\test_winepad.c native\probe\tests\stub_rtapi.c $s\winepad.c $s\log.c $s\plat_win.c
//   scratch\test_winepad.exe
#include "../src/winepad.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifdef _WIN32
#include <windows.h>
#endif

static int failures;
#define CHECK(c) do { if (!(c)) { printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #c); failures++; } } while (0)

static volatile long g_calls;     // fake XInputGetState calls
static uint32_t g_connected;      // slots the fake says are connected

static uint32_t fake_get(uint32_t slot, void *state) {
    memset(state, 0, 16);   // the size of XINPUT_STATE: the fake writes all of it, as XInput does
#ifdef _WIN32
    InterlockedIncrement(&g_calls);
#else
    __atomic_add_fetch(&g_calls, 1, __ATOMIC_SEQ_CST);
#endif
    return (g_connected >> slot) & 1 ? 0 : 1167;   // ERROR_DEVICE_NOT_CONNECTED
}

static volatile long g_done;
static void on_done(void *user) { *(volatile long *)user = 1; }

static void set_env(const char *name, const char *value) {
#ifdef _WIN32
    _putenv_s(name, value ? value : "");
#else
    if (value) setenv(name, value, 1); else unsetenv(name);
#endif
}

static void test_touch_and_slots(void) {
    char b[16];
    CHECK(winepad_touch(NULL) == -1);
    g_connected = 0;
    g_calls = 0;
    CHECK(winepad_touch(fake_get) == 0);
    CHECK(g_calls == 4);   // slots 0-3, each once
    g_connected = 0x5;
    CHECK(winepad_touch(fake_get) == 0x5);
    g_connected = 0xff;   // only four slots exist
    CHECK(winepad_touch(fake_get) == 0xf);
    CHECK(!strcmp(winepad_slots(-1, b, sizeof b), "no XInput"));
    CHECK(!strcmp(winepad_slots(0, b, sizeof b), "none"));
    CHECK(!strcmp(winepad_slots(0x1, b, sizeof b), "0"));
    CHECK(!strcmp(winepad_slots(0x5, b, sizeof b), "0 2"));
    CHECK(!strcmp(winepad_slots(0xf, b, sizeof b), "0 1 2 3"));
    char small[4];   // cut, never past the buffer
    CHECK(!strcmp(winepad_slots(0xf, small, sizeof small), "0 1"));
}

static void test_decision(void) {
    g_calls = 0;
    CHECK(winepad_start_with(NULL, fake_get, 3, 1, NULL, NULL) == 0);   // not under Wine: nothing asked
    CHECK(g_calls == 0);
    set_env("MKWMP_WINEPAD", "0");
    CHECK(winepad_start_with("9.0", fake_get, 3, 1, NULL, NULL) == -1);   // turned off
    CHECK(g_calls == 0);
    set_env("MKWMP_WINEPAD", NULL);
    CHECK(winepad_start_with("9.0", NULL, 3, 1, NULL, NULL) == -1);   // no XInput DLL
#ifdef _WIN32
    // this test runs on Windows itself: ntdll has no wine_get_version, so the mod leaves XInput alone
    if (!winepad_wine_version()) CHECK(winepad_start() == 0);
#else
    CHECK(winepad_wine_version() == NULL);
    CHECK(winepad_start() == 0);
#endif
}

static void test_schedule(void) {
    g_connected = 0x1;
    g_calls = 0;
    g_done = 0;
    // under Wine: one ask at once (before SDL's gamepads start), then 3 more from the thread, 1 ms apart
    CHECK(winepad_start_with("9.0", fake_get, 3, 1, on_done, (void *)&g_done) == 1);
    CHECK(g_calls >= 4);   // the first ask is synchronous: slots 0-3 before the call returns
    for (int i = 0; i < 500 && !g_done; i++) {
#ifdef _WIN32
        Sleep(10);
#else
        struct timespec t = {0, 10000000};
        nanosleep(&t, NULL);
#endif
    }
    CHECK(g_done);
    CHECK(g_calls == 4 * (1 + 3));
    // no later asks: the default schedule is the stop-gap's, 2 s apart for 40 s
    CHECK(WINEPAD_ROUNDS * WINEPAD_INTERVAL_MS == 40000);
}

int main(void) {
    test_touch_and_slots();
    test_decision();
    test_schedule();
    if (failures) {
        printf("%d failure(s)\n", failures);
        return 1;
    }
    printf("test_winepad: ok\n");
    return 0;
}
