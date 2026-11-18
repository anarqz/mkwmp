// Vendored unchanged from github.com/anarqz/mkw-recomp, branch plugin-loader, commit 0b03f8a3d7e730c518e6a46b8d93c84a1cd19853, runtime/plugin/mkw_plugin.h (MIT).
// mkw_plugin.h: native plugins for mkw-recomp. PROTOTYPE (private fork), API version 1.
//
// A plugin is a shared library (mods/<name>/plugin/<name>.dll on Windows, <name>.so on Linux) that the runtime loads
// when the player enables its mod, before the game is loaded. It exports one function:
//
//     MKW_PLUGIN_EXPORT int mkw_plugin_init(const MkwPluginApi *api, uint32_t api_version);
//
// called once, on the main thread, before ldr_load. It returns 0 to stay loaded; anything else unloads it (the reason
// goes through api->fail first, shown on the start screen). The table stays valid for the whole process.
//
// Versioning: api_version is the version the runtime implements (MKW_PLUGIN_API_VERSION of its build) and api->size
// the size of the table it passes. Entries are only ever appended; a plugin built against version N checks
// api_version >= N (or api->size covers the entries it uses) and refuses otherwise. A field a runtime cannot provide
// is NULL; the plugin checks before calling.
//
// The plugin depends on nothing else of the runtime: no symbols of the executable are resolved, no struct of the
// runtime is copied except the documented MkwCpu prefix below, and it works the same on Windows and Linux.
//
// This header is MIT licensed, as mkw-recomp.
#ifndef MKW_PLUGIN_H
#define MKW_PLUGIN_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define MKW_PLUGIN_API_VERSION 1

#ifdef _WIN32
#define MKW_PLUGIN_EXPORT __declspec(dllexport)
#else
#define MKW_PLUGIN_EXPORT __attribute__((visibility("default")))
#endif

// ---- the guest CPU
// A host function receives the calling guest thread's CPU. Only this prefix is guaranteed (runtime/cpu/cpu.h starts
// with it); everything after it is the runtime's. Arguments follow AAPCS64: x0..x7, then the stack at sp.
typedef struct MkwCpu {
    uint64_t x[32];   // x0..x30 (x31 unused: sp below)
    uint64_t sp;
    uint64_t pc;      // in a host function: the guest address that was called
} MkwCpu;

typedef void (*MkwHostFn)(MkwCpu *cpu);

// Guest memory is mapped 1:1: a guest address is the host pointer to the same bytes (layout.py), so a plugin reads
// and writes it directly; guest_readable says whether a range is mapped.

typedef struct MkwPluginApi {
    uint32_t size;          // sizeof(MkwPluginApi) of the runtime that filled it
    uint32_t api_version;   // MKW_PLUGIN_API_VERSION of that runtime

    // ---- what is running (const for the process)
    const char *runtime_version;   // mkw-recomp's version, "0.1.14"
    uint32_t game_version;         // the game build being played: 100 (1.0.0), 180 (1.8.0); 0 not known yet
    const char *game_code_id;      // RT_GAME_ID of the game code (mkwgame.id), hex
    const char *plugin_dir;        // UTF-8: the folder this plugin was loaded from (its own files go there)
    const char *data_dir;          // UTF-8: the runtime's data folder (MKW_GAME_DIR: saves, logs, mods)

    // ---- log, settings, failure
    void (*log)(const char *fmt, ...);            // logs/mkw.log, prefixed with the plugin's name
    const char *(*cfg_get)(const char *key);      // an MKW_* setting (environment over mkw.ini); NULL if unset
    void (*fail)(const char *reason);             // during init: why it returns non-zero (start screen + log)

    // ---- host functions for SDK names (only during mkw_plugin_init: before the game is loaded)
    // Serve guest calls to `name` (an import of the game, mangled as in the .ovr table) with fn. No limit on count.
    // A later override of the same name replaces an earlier one (and is logged); 0 on success.
    int (*override)(const char *name, MkwHostFn fn);
    // The runtime's own handler of `name` (what runs without the plugin); NULL when the runtime has none. Callable from
    // an override to pass a call through.
    MkwHostFn (*stock)(const char *name);
    // Run fn before the game's own definition of `name` (a function the game defines, not an import); inside fn,
    // call_original runs that definition with the current registers and returns its x0.
    int (*observe)(const char *name, MkwHostFn fn);
    uint64_t (*call_original)(MkwCpu *cpu);

    // ---- calling the game
    uint64_t (*guest_symbol)(const char *name);   // an exported guest definition's address; 0 if none
    // Call guest code at fn with n (<= 8) integer arguments on this guest thread's CPU; returns x0. Only from a host
    // function (cpu is its argument): a plugin's own threads have no guest CPU.
    uint64_t (*call)(MkwCpu *cpu, uint64_t fn, int n, ...);
    uint64_t (*main_base)(void);                  // the main module's load address (0 before the game is loaded)

    // ---- threads
    // Inside a host function that blocks on the host (a socket wait, a sleep): give the emulated core up first and
    // take it back after, so the other guest threads keep running. away = core_away(); ...; core_back(&away).
    int (*core_away)(void);
    void (*core_back)(int *away);

    // ---- guest memory
    int (*guest_readable)(uint64_t addr, size_t n);   // 1 when [addr, addr + n) is mapped and readable

    // ---- indirect calls (any time, any thread)
    // Make an indirect guest call (blr) to `addr`, an address outside every guest module (a plugin picks its own,
    // e.g. in a guest vtable slot it rewrites), run fn. 0 on success.
    int (*host_at)(uint64_t addr, MkwHostFn fn);
    // See every indirect guest call: ours runs with cpu->pc the target and x30 the return address, and must end by
    // calling *original (the runtime's dispatcher) unless it handled the call. One hook per process; 0 on success.
    int (*dispatch_hook)(MkwHostFn ours, MkwHostFn *original);

    // ---- lifecycle
    // fn(user) once the game's modules are loaded and main_base / game_version are known, before its first
    // instruction runs (on the main thread). Several callbacks run in registration order.
    int (*on_game_loaded)(void (*fn)(void *user), void *user);

    // ---- the player's screen
    // A short line over the game for `seconds` (the runtime's notice). Any thread.
    void (*notice)(const char *utf8, int seconds);
    // The game asked for text (nn::swkbd::ShowKeyboard): fn may answer it. header / guide are the game's texts in
    // UTF-8; fn writes the answer (UTF-8, NUL-terminated, at most cap bytes) and returns 1, or returns 0 to let the
    // runtime's own text entry run. Called on the guest thread that asked; it may block (the call does on a console).
    int (*text_entry_hook)(int (*fn)(const char *header, const char *guide, char *answer, size_t cap));
} MkwPluginApi;

typedef int (*MkwPluginInit)(const MkwPluginApi *api, uint32_t api_version);

#ifdef __cplusplus
}
#endif

#endif
