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
#include "keypad.h"
#include "log.h"
#include "rtapi.h"

#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int npln_register(void);

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
    if (coff_resolve_runtime()) {
        log_line("runtime resolved from COFF symbols");
        log_kv("hostfn_add_override", (uint64_t)(uintptr_t)g_rt.override);
        int n = npln_register();
        if (n >= 0) log_kv("overrides registered", (uint64_t)n);
        keypad_install();   // x64 detours of runtime functions: needs no override slot
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
