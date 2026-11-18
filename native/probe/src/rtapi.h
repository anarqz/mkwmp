// The probe's only view of the mkw-recomp runtime (resources/docs/PLUGIN-API-READINESS.md).
//
// Everything outside the backend calls the runtime through g_rt and nothing else. Today one backend fills it: coff.c,
// from mkw.exe's COFF symbol table, its .ovr section, its g_host registry and the rt_dispatch import of mkwgame.dll
// (Windows, loaded as the winmm.dll proxy). The table is shaped like upstream's planned MkwPluginApi, so a plugin
// backend fills the same entries from that API and the rest of the probe does not change.
//
// Exception: keypad.c's detours and ImGui calls resolve their own COFF symbols (coff_symbol). The keypad moves into
// the recomp itself and a plugin gets text-entry / overlay hooks instead, so that file stays COFF-only.
#ifndef MKWMP_RTAPI_H
#define MKWMP_RTAPI_H

#include "recomp.h"

#include <stdint.h>

typedef struct RtApi {
    // ---- required: the backend fails without them
    void (*override)(const char *name, GuestFn fn);   // a host function for an SDK name; only before the game loads
    int (*guest_modules)(void);                       // guest modules loaded so far (non-zero: too late to override)
    uint64_t (*guest_symbol)(const char *name);       // a guest symbol's address (0 if none)
    void (*log)(const char *fmt, ...);                // the runtime's log (logs/mkw.log)
    uint64_t (*call)(Cpu *c, uint64_t fn, int n, ...);   // call guest code at fn with n integer arguments; x0
    const char *(*cfg_get)(const char *key);          // an MKW_* setting (environment / mkw.ini)

    // ---- optional: NULL when the backend has no such thing; callers check
    int (*core_away)(void);       // give the emulated core up while blocking on the host ...
    void (*core_back)(int *away); // ... and take it back
    GuestFn (*stock)(const char *name);   // the runtime's own handler of an SDK name (for call-original)
    // the runtime's fs handlers, chained to after logging (observer)
    GuestFn fs_open_file, fs_entry_type, fs_read_file, fs_file_size, fs_close_file;
    // make an indirect guest call to addr run fn (vtable hooks with a fake address); 1 on success
    int (*host_fn_at)(uint64_t addr, GuestFn fn);
    // the host side of the game code is loaded (hooks that patch it can go in)
    int (*game_code_loaded)(void);
    // see every indirect guest call: c->pc is the target, x30 the caller. *original gets the runtime's dispatcher,
    // which ours must call. 1 installed, 0 not possible (logged by the caller)
    int (*dispatch_hook)(GuestFn ours, GuestFn *original);
} RtApi;

extern RtApi g_rt;

#endif
