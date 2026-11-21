// The probe's only view of the mkw-recomp runtime (docs/PLUGIN-API-READINESS.md).
//
// Everything outside the backend calls the runtime through g_rt and nothing else. Two backends fill it: coff.c, from
// mkw.exe's COFF symbol table, its .ovr section, its g_host registry and the rt_dispatch import of mkwgame.dll
// (Windows, loaded as the winmm.dll proxy), and backend_plugin.c, from mkw-recomp's native plugin API version 1
// (mkw_plugin.h, upstream's header unchanged; Windows and Linux). The rest of the probe does not know which one filled
// it, except where a hook has to go in differently (host_fn_at / dispatch_hook vs override_address).
//
// The probe draws nothing into the game and reads no input of its own there (the overlay trigger aside, overlay.h):
// the software keyboard is mkw-recomp's own number pad.
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
    // c is a guest thread's CPU (it may give its core up: core_away). NULL: the COFF test, the runtime's Cpu.thread
    // (beyond the guaranteed prefix). The plugin API calls every host function on a guest thread.
    int (*guest_thread)(Cpu *c);
    // ---- the plugin API's hooks by address (backend_plugin.c; NULL with COFF, and again once the game runs)
    // Indirect calls to the game function starting at addr run fn (every vtable and function pointer that holds it).
    // Only while the game is being loaded (before its first instruction); 1 on success.
    int (*override_address)(uint64_t addr, GuestFn fn);
    // Inside such an override (or a symbol override): what the call would have run without it; c->pc must still be
    // the overridden address. Returns x0, which it also leaves in c->x[0].
    uint64_t (*call_original)(Cpu *c);
} RtApi;

extern RtApi g_rt;

#endif
