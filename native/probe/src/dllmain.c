// Proxy winmm.dll: the code-execution foothold inside mkw.exe.
//
// mkw.exe and SDL3.dll import timeBeginPeriod/timeEndPeriod from WINMM.dll. Dropped in the game
// folder, this DLL is loaded in place of System32's winmm for every module in the process (the
// game, SDL, AND the GPU driver DLLs, which import timeGetDevCaps etc.). All real exports are
// re-exported as forwarders to winmm_host.dll (a renamed copy of the real winmm) via winmm.def,
// so nothing breaks. DllMain runs regardless of the forwarders: it resolves the recomp runtime
// from mkw.exe's COFF symbols and registers our NPLN overrides -- which it can do here because
// DllMain runs at process init, before the exe entry point, i.e. before the recomp's ldr_load.
#include "coff.h"
#include "log.h"
#include "recomp.h"

#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int npln_register(void);

// Under Proton, Steam's SDL_GAMECONTROLLER_IGNORE_DEVICES (meant for native Linux SDL games)
// reaches this Windows process and lists the Xbox 360 id that Steam Input's virtual pad has
// through Wine's XInput, so the game's SDL3 would ignore every controller. Clear it under Wine
// (before SDL reads its environment); MKWMP_KEEP_SDL_IGNORE=1 keeps it.
static void wine_controller_fixup(void) {
    HMODULE ntdll = GetModuleHandleA("ntdll.dll");
    if (!ntdll || !GetProcAddress(ntdll, "wine_get_version")) return;
    if (GetEnvironmentVariableA("MKWMP_KEEP_SDL_IGNORE", NULL, 0)) return;
    if (GetEnvironmentVariableA("SDL_GAMECONTROLLER_IGNORE_DEVICES", NULL, 0)) {
        SetEnvironmentVariableA("SDL_GAMECONTROLLER_IGNORE_DEVICES", NULL);
        log_line("wine: cleared SDL_GAMECONTROLLER_IGNORE_DEVICES (Steam's Linux list)");
    }
    // Steam Input's pad reaches Windows SDL as a Steam virtual gamepad (28de:12fd on the Ally X),
    // which SDL3 hides unless this is set (verified with resources/tools/padprobe.c under Proton).
    SetEnvironmentVariableA("SDL_GAMECONTROLLER_ALLOW_STEAM_VIRTUAL_GAMEPAD", "1");
    log_line("wine: SDL_GAMECONTROLLER_ALLOW_STEAM_VIRTUAL_GAMEPAD=1");
}

// MKWMP_PAD_DIAG=1: 20 s after start, log what XInput and the game's SDL3 see (controller debugging).
typedef struct { DWORD packet; WORD buttons; BYTE lt, rt; SHORT lx, ly, rx, ry; } PadState;
static DWORD WINAPI pad_diag(LPVOID unused) {
    (void)unused;
    for (int round = 0; round < 3; round++) {
        Sleep(round ? 15000 : 20000);
        HMODULE xi = LoadLibraryA("xinput1_4.dll");
        DWORD (WINAPI *get)(DWORD, PadState *) = xi ? (void *)GetProcAddress(xi, "XInputGetState") : NULL;
        for (DWORD i = 0; get && i < 4; i++) {
            PadState s; mkwmp_log("pad-diag: XInput slot %lu %s", (unsigned long)i, get(i, &s) == 0 ? "CONNECTED" : "-");
        }
        HMODULE sdl = GetModuleHandleA("SDL3.dll");
        if (!sdl) { mkwmp_log("pad-diag: SDL3 not loaded"); continue; }
        uint32_t *(*joys)(int *) = (void *)GetProcAddress(sdl, "SDL_GetJoysticks");
        const char *(*name)(uint32_t) = (void *)GetProcAddress(sdl, "SDL_GetJoystickNameForID");
        const char *(*hint)(const char *) = (void *)GetProcAddress(sdl, "SDL_GetHint");
        void (*sdl_free)(void *) = (void *)GetProcAddress(sdl, "SDL_free");
        int n = 0; uint32_t *ids = joys ? joys(&n) : NULL;
        const char *allow = hint ? hint("SDL_GAMECONTROLLER_ALLOW_STEAM_VIRTUAL_GAMEPAD") : NULL;
        const char *ignore = hint ? hint("SDL_GAMECONTROLLER_IGNORE_DEVICES") : NULL;
        mkwmp_log("pad-diag: SDL joysticks %d; allow-steam-virtual=%s ignore-list=%s", n,
                  allow ? allow : "(unset)", ignore ? "(set)" : "(unset)");
        for (int i = 0; i < n; i++) mkwmp_log("pad-diag:   %u %s", ids[i], name ? name(ids[i]) : "?");
        if (ids && sdl_free) sdl_free(ids);
        // Under Wine, Steam Input's pad can appear after the game initialised SDL, and SDL3's
        // Windows backends then miss the hotplug. XInput connected + no SDL joystick: restart
        // SDL's joystick/gamepad subsystems so they rescan and post SDL_EVENT_GAMEPAD_ADDED.
        PadState probe_state;
        if (n == 0 && get && get(0, &probe_state) == 0) {
            void (*quit_sub)(uint32_t) = (void *)GetProcAddress(sdl, "SDL_QuitSubSystem");
            int (*init_sub)(uint32_t) = (void *)GetProcAddress(sdl, "SDL_InitSubSystem");
            if (quit_sub && init_sub) {
                quit_sub(0x2000u | 0x200u);   // SDL_INIT_GAMEPAD | SDL_INIT_JOYSTICK
                int ok = init_sub(0x2000u | 0x200u);
                int m = 0; uint32_t *again = joys ? joys(&m) : NULL;
                mkwmp_log("pad-diag: rescanned SDL joysticks (init %d): %d now", ok, m);
                if (again && sdl_free) sdl_free(again);
            }
        }
    }
    return 0;
}

// <game dir>\mkwmp.env: KEY=VALUE lines (# comments) applied over the process environment
// before the probe reads its settings. Lets a launch whose environment is fixed (e.g. a Steam
// shortcut on the Deck) be reconfigured by editing a file; VALUE empty unsets KEY.
static void apply_env_file(void) {
    wchar_t path[MAX_PATH];
    DWORD n = GetModuleFileNameW(NULL, path, MAX_PATH);
    if (!n || n >= MAX_PATH) return;
    for (DWORD i = n; i > 0; i--) {
        if (path[i - 1] == L'\\' || path[i - 1] == L'/') { path[i] = 0; break; }
    }
    wcsncat(path, L"mkwmp.env", MAX_PATH - wcslen(path) - 1);
    HANDLE fh = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (fh == INVALID_HANDLE_VALUE) return;
    char buf[4096];
    DWORD got = 0;
    ReadFile(fh, buf, sizeof buf - 1, &got, NULL);
    CloseHandle(fh);
    buf[got] = 0;
    for (char *line = strtok(buf, "\r\n"); line; line = strtok(NULL, "\r\n")) {
        while (*line == ' ' || *line == '\t') line++;
        char *eq = strchr(line, '=');
        if (!*line || *line == '#' || !eq || eq == line) continue;
        *eq = 0;
        SetEnvironmentVariableA(line, eq[1] ? eq + 1 : NULL);
        char kv[512];
        snprintf(kv, sizeof kv, "%s=%s", line, eq + 1);
        _putenv(kv);   // the CRT's getenv() copy too
        log_line(eq[1] ? "env file: set" : "env file: unset");
        log_line(line);
    }
}

#ifndef MKWMP_VERSION
#define MKWMP_VERSION "dev"
#endif

static void probe_init(void) {
    log_init();
    log_line("mkwmp probe " MKWMP_VERSION);
    apply_env_file();
    wine_controller_fixup();
    if (GetEnvironmentVariableA("MKWMP_PAD_DIAG", NULL, 0) || (GetModuleHandleA("ntdll.dll") &&
        GetProcAddress(GetModuleHandleA("ntdll.dll"), "wine_get_version")))
        CloseHandle(CreateThread(NULL, 0, pad_diag, NULL, 0, NULL));
    if (coff_resolve_runtime()) {
        log_line("runtime resolved from COFF symbols");
        log_kv("hostfn_add_override", (uint64_t)(uintptr_t)g_rt.hostfn_add_override);
        int n = npln_register();
        if (n >= 0) log_kv("overrides registered", (uint64_t)n);
    } else {
        log_line("ERROR: failed to resolve recomp runtime; probe inactive");
    }
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved) {
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(inst);
        probe_init();
    }
    return TRUE;
}
