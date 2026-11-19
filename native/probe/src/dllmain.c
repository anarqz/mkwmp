// Proxy winmm.dll: the code-execution foothold inside mkw.exe.
//
// mkw.exe and SDL3.dll import timeBeginPeriod/timeEndPeriod from WINMM.dll. Dropped in the game
// folder, this DLL is loaded in place of System32's winmm for every module in the process (the
// game, SDL, AND the GPU driver DLLs, which import timeGetDevCaps etc.). All real exports are
// re-exported as forwarders to winmm_host.dll (a renamed copy of the real winmm) via winmm.def,
// so nothing breaks. DllMain runs regardless of the forwarders: it resolves the recomp runtime
// from mkw.exe's COFF symbols and registers our NPLN overrides -- which it can do here because
// DllMain runs at process init, before the exe entry point, i.e. before the recomp's ldr_load.
//
// Unless MKWMP runs as a mkw-recomp mod in this game (modmode.h): then this DLL only forwards winmm.
#include "coff.h"
#include "keypad.h"
#include "log.h"
#include "modmode.h"
#include "plat.h"
#include "probe.h"
#include "rtapi.h"

#include <windows.h>
#include <stdint.h>

void *coff_symbol(const char *name);

// The mod "mkwmp" is enabled in this game and its plugin file is there (modmode.h). `plugin` gets the file's path.
static int mod_enabled(char *plugin, size_t cap) {
    char exe_dir[1024], game_dir[768], mods[4096];
    if (!plat_exe_path(exe_dir, sizeof exe_dir, "")) return 0;
    ModmodeInput in = {
        .exe_dir = exe_dir,
        .env_game_dir = plat_getenv("MKW_GAME_DIR", game_dir, sizeof game_dir) ? game_dir : NULL,
        .env_mods = plat_getenv("MKW_MODS", mods, sizeof mods) ? mods : NULL,
        .cmdline = GetCommandLineA(),
    };
    return modmode_mod_enabled(&in, plugin, cap);
}

// The start-up shared with the plugin loader is probe.c's; only the COFF resolve and the keypad are this loader's.
static void probe_init(void) {
    // The mod wins: decided first, before the environment file, any override and any hook. Reading mkw.exe's symbol
    // table changes nothing in the process; it only says whether this runtime loads plugins at all (0.1.15 on).
    char plugin[1024];
    int mod = mod_enabled(plugin, sizeof plugin), resolved = -1;
    if (mod) {
        log_init();
        resolved = coff_resolve_runtime();
        if (coff_symbol("plugins_load")) {
            mkwmp_log("the MKWMP mod is enabled in this game (%s): the mod is in charge, this winmm.dll stays dormant "
                      "(no overrides, no hooks)", plugin);
            return;
        }
    }
    probe_log_start();
    if (mod)
        mkwmp_log("the MKWMP mod is enabled (%s) but this mkw-recomp has no plugin loader: winmm.dll stays in charge",
                  plugin);
    if (resolved < 0) resolved = coff_resolve_runtime();
    if (resolved) {
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
