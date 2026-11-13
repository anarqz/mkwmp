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
}

static void probe_init(void) {
    log_init();
    wine_controller_fixup();
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
