// The plugin backend of rtapi.h: mkw-recomp's native plugin API, version 1 (mkw_plugin.h: upstream's header as it
// is, runtime/plugin/mkw_plugin.h of adriadam10/mkw-recomp), Windows and Linux.
//
// The runtime loads mods/<mod>/plugin/mkwmp.dll (mkwmp.so) when the player has the mod enabled and calls
// mkw_plugin_init once, on the main thread, before the game code is loaded. We fill g_rt from its table and run the
// start-up shared with the winmm.dll proxy (probe.c). What the COFF backend dug out of mkw.exe (symbols, the .ovr
// table, g_host, the rt_dispatch import) is an API entry here, with two differences:
//   - indirect-call hooks are per address (override_address) and only while the game is being loaded, so the probe's
//     version-specific hooks (npln_address_hooks, voice_ui_install) go in from on_game_loaded, not lazily online;
//   - mkw-recomp draws its own number pad for the software keyboard: no keypad, no text hook.
//
// Folders: the probe's home (mkwmp.log, mkwmp.env, the voice library) is the mod's folder, mods/<mod>/. mkwmp.toml
// and the lobby CA are read from there, else from the game's folder, where the MKWMP launcher writes them
// (config.c).
//
// Types: the probe's Cpu (recomp.h) starts with MkwCpu's guaranteed prefix (x0..x31, sp, pc), and both are passed by
// pointer, so a GuestFn is handed to the runtime as an MkwGuestFn as it is. Nothing here reads past that prefix.
#include "mkw_plugin.h"

#include "guesthook.h"
#include "log.h"
#include "notify.h"
#include "plat.h"
#include "probe.h"
#include "rtapi.h"
#include "voice_ui.h"

#include <stdarg.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

RtApi g_rt;

static const MkwPluginApi *g_api;
static int g_active;                     // probe_register registered overrides (mkwmp.toml or MKWMP_ENABLE)

// The SDK names whose stock handlers the fs overrides chain to: npln.c's kRegs entries for ov_fs_open_file ...
// (the COFF backend takes the same handlers from mkw.exe's ov_open_file ... symbols).
static const char kFsOpenFile[] = "_ZN2nn2fs8OpenFileEPNS0_10FileHandleEPKci";
static const char kFsEntryType[] = "_ZN2nn2fs12GetEntryTypeEPNS0_18DirectoryEntryTypeEPKc";
static const char kFsReadFile[] = "_ZN2nn2fs8ReadFileEPmNS0_10FileHandleElPvm";
static const char kFsFileSize[] = "_ZN2nn2fs11GetFileSizeEPlNS0_10FileHandleE";
static const char kFsCloseFile[] = "_ZN2nn2fs9CloseFileENS0_10FileHandleE";

static MkwGuestFn to_api(GuestFn fn) { return (MkwGuestFn)(void (*)(void))fn; }
static GuestFn to_guest(MkwGuestFn fn) { return (GuestFn)(void (*)(void))fn; }

// ---- g_rt entries that adapt an API entry (the API's 1 is success, as g_rt's)
static void pl_override(const char *name, GuestFn fn) {
    if (!g_api->override_symbol(name, to_api(fn))) mkwmp_log("plugin: override of %s refused by the runtime", name);
}

static uint64_t pl_call(Cpu *c, uint64_t fn, int n, ...) {
    uint64_t a[8] = {0};
    if (n < 0) n = 0;
    if (n > 8) n = 8;
    va_list ap;
    va_start(ap, n);
    for (int i = 0; i < n; i++) a[i] = va_arg(ap, uint64_t);
    va_end(ap);
    return g_api->call((MkwCpu *)(void *)c, fn, n, a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7]);
}

static GuestFn pl_stock(const char *name) { return to_guest(g_api->stock(name)); }

static int pl_override_address(uint64_t addr, GuestFn fn) {
    return g_api->override_address(addr, to_api(fn)) != 0;
}

static uint64_t pl_call_original(Cpu *c) { return g_api->call_original((MkwCpu *)(void *)c); }

static int pl_guest_thread(Cpu *c) {
    (void)c;
    return 1;   // the API calls every override on the guest thread that made the call
}

// ---- the game is loaded: main is mapped, no guest instruction has run, override_address still answers
static void on_game_loaded(void *user) {
    (void)user;
    uint64_t main = g_api->module_base ? g_api->module_base("main") : 0;
    mkwmp_log("plugin: game %s loaded (build id %s, main at %#llx)", g_api->game_version ? g_api->game_version : "?",
              g_api->game_id ? g_api->game_id : "?", (unsigned long long)main);
    if (main && main != GUEST_MAIN(0))
        mkwmp_log("plugin: main is not at %#llx: every version-specific hook stays off", (unsigned long long)GUEST_MAIN(0));
    else if (g_active) {
        npln_address_hooks();   // error skips, idle-kick guard, BulkGet / Pia / matchmake traces (by game build)
        voice_ui_install();     // the online pause menu's voice labels
    }
    g_rt.override_address = NULL;   // closed from here on: a later install attempt sees no means and stays off
}

static void notice_sink(NotifyLevel level, const char *text) {
    g_api->notice(text, level == NOTIFY_ERROR || level == NOTIFY_WARN ? 8 : 5);
}

// ---- entry
static int refuse(const char **message, const char *reason) {
    static char kept[256];   // the runtime reads it after we return
    snprintf(kept, sizeof kept, "%s", reason);
    if (message) *message = kept;
    mkwmp_log("plugin: declined: %s", reason);
    return 1;
}

MKW_PLUGIN_EXPORT int mkw_plugin_init(const MkwPluginApi *api, uint32_t api_version, const char **message) {
    if (!api) return 1;
    // notice and the overlay are the table's last fields: a shorter table is an older runtime
    if (api_version < 1 || api->size < sizeof(MkwPluginApi))
        return refuse(message, "MKWMP needs a newer mkw-recomp (plugin API version 1)");

    plat_set_home(api->mod_dir);   // mkwmp.log, mkwmp.env, the voice library: the mod's folder
    probe_log_start();
    mkwmp_log("plugin: mkw-recomp %s, plugin API %u (table %u bytes), game %s, mod %s in %s",
              api->port_version ? api->port_version : "?", (unsigned)api_version, (unsigned)api->size,
              api->game_version ? api->game_version : "?", api->mod_name ? api->mod_name : "?",
              api->mod_dir ? api->mod_dir : "(none)");

    const char *missing = !api->override_symbol ? "override_symbol" : !api->guest_symbol ? "guest_symbol"
                        : !api->log ? "log" : !api->call ? "call" : !api->cfg_get ? "cfg_get"
                        : !api->core_away ? "core_away" : !api->core_back ? "core_back"
                        : !api->guest_modules ? "guest_modules" : !api->on_game_loaded ? "on_game_loaded" : NULL;
    if (missing) {
        char reason[128];
        snprintf(reason, sizeof reason, "MKWMP: this mkw-recomp's plugin API has no %s; online play is off", missing);
        return refuse(message, reason);
    }
    g_api = api;

    // ---- required
    g_rt.override = pl_override;
    g_rt.guest_modules = api->guest_modules;   // 0 until the loader binds the game: overrides only during init
    g_rt.guest_symbol = api->guest_symbol;
    g_rt.log = api->log;
    g_rt.call = pl_call;
    g_rt.cfg_get = api->cfg_get;
    // ---- optional
    g_rt.core_away = api->core_away;
    g_rt.core_back = api->core_back;
    g_rt.stock = api->stock ? pl_stock : NULL;
    if (api->stock) {   // the fs overrides log, then call these (call-original through the stock handler)
        g_rt.fs_open_file = pl_stock(kFsOpenFile);
        g_rt.fs_entry_type = pl_stock(kFsEntryType);
        g_rt.fs_read_file = pl_stock(kFsReadFile);
        g_rt.fs_file_size = pl_stock(kFsFileSize);
        g_rt.fs_close_file = pl_stock(kFsCloseFile);
    }
    g_rt.game_code_loaded = api->guest_modules;   // the game's code is loaded with its modules
    g_rt.guest_thread = pl_guest_thread;
    // host_fn_at and dispatch_hook stay NULL: v1 hooks indirect calls per address
    g_rt.override_address = api->override_address ? pl_override_address : NULL;
    g_rt.call_original = api->call_original ? pl_call_original : NULL;

    api->on_game_loaded(on_game_loaded, NULL);
    if (api->notice) notify_set_sink(notice_sink);

    log_line("runtime resolved from the plugin API");
    int n = probe_register();
    if (n < 0) return refuse(message, "MKWMP does not fit this mkw-recomp (see mods/<mod>/mkwmp.log); online play is off");
    g_active = n > 0;
    return 0;
}
