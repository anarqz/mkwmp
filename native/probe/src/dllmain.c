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
#include "probe.h"
#include "rtapi.h"

#include <windows.h>
#include <stdint.h>

// The start-up shared with the plugin loader is probe.c's; only the COFF resolve and the keypad are this loader's.
static void probe_init(void) {
    probe_log_start();
    if (coff_resolve_runtime()) {
        log_line("runtime resolved from COFF symbols");
        log_kv("hostfn_add_override", (uint64_t)(uintptr_t)g_rt.override);
        probe_register();
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
