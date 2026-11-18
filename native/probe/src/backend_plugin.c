// The plugin backend of rtapi.h: mkw-recomp's native plugin API (mkw_plugin.h, version 1), Windows and Linux.
//
// The runtime loads mods/mkwmp/plugin/mkwmp.dll / mkwmp.so and calls mkw_plugin_init once, on the main thread, before
// the game is loaded. We fill g_rt from its table and run the start-up shared with the winmm.dll proxy (probe.c).
// What the COFF backend dug out of mkw.exe (symbols, the .ovr table, g_host, the rt_dispatch import) is all an API
// entry here. The keypad (keypad.c, COFF-only) becomes a text-entry hook that answers with the launcher's Room ID.
//
// Types: the probe's Cpu (recomp.h) starts with MkwCpu's guaranteed prefix (x0..x31, sp, pc), and both are passed by
// pointer, so a GuestFn is handed to the runtime as an MkwHostFn as it is. Nothing here reads past that prefix.
#include "mkw_plugin.h"

#include "log.h"
#include "notify.h"
#include "plat.h"
#include "probe.h"
#include "rtapi.h"

#include <stdarg.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

RtApi g_rt;

static const MkwPluginApi *g_api;
static volatile int32_t g_game_loaded;   // on_game_loaded has run

// The SDK names whose stock handlers the fs overrides chain to: npln.c's kRegs entries for ov_fs_open_file ...
// (the COFF backend takes the same handlers from mkw.exe's ov_open_file ... symbols).
static const char kFsOpenFile[] = "_ZN2nn2fs8OpenFileEPNS0_10FileHandleEPKci";
static const char kFsEntryType[] = "_ZN2nn2fs12GetEntryTypeEPNS0_18DirectoryEntryTypeEPKc";
static const char kFsReadFile[] = "_ZN2nn2fs8ReadFileEPmNS0_10FileHandleElPvm";
static const char kFsFileSize[] = "_ZN2nn2fs11GetFileSizeEPlNS0_10FileHandleE";
static const char kFsCloseFile[] = "_ZN2nn2fs9CloseFileENS0_10FileHandleE";

static MkwHostFn to_host(GuestFn fn) { return (MkwHostFn)(void (*)(void))fn; }
static GuestFn to_guest(MkwHostFn fn) { return (GuestFn)(void (*)(void))fn; }

// ---- g_rt entries that adapt an API entry (return conventions: the API's 0 is success, g_rt's 1)
static void pl_override(const char *name, GuestFn fn) {
    if (g_api->override(name, to_host(fn))) mkwmp_log("plugin: override of %s refused by the runtime", name);
}

static int pl_guest_modules(void) {
    if (plat_load(&g_game_loaded)) return 1;
    return g_api->main_base && g_api->main_base() != 0;   // a runtime without on_game_loaded
}

static uint64_t pl_call(Cpu *c, uint64_t fn, int n, ...) {
    uint64_t a[8] = {0};
    va_list ap;
    va_start(ap, n);
    for (int i = 0; i < n && i < 8; i++) a[i] = va_arg(ap, uint64_t);
    va_end(ap);
    return g_api->call((MkwCpu *)(void *)c, fn, n < 8 ? n : 8, a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7]);
}

static GuestFn pl_stock(const char *name) { return to_guest(g_api->stock(name)); }

static int pl_host_fn_at(uint64_t addr, GuestFn fn) { return g_api->host_at(addr, to_host(fn)) == 0; }

static int pl_dispatch_hook(GuestFn ours, GuestFn *original) {
    // the runtime writes *original before ours can run, so it gets g_rt's caller's slot itself
    return g_api->dispatch_hook(to_host(ours), (MkwHostFn *)(void *)original) == 0;
}

static int pl_guest_thread(Cpu *c) {
    (void)c;
    return 1;   // the API calls every host function on the guest thread that made the call
}

static void on_game_loaded(void *user) {
    (void)user;
    plat_store(&g_game_loaded, 1);
    mkwmp_log("plugin: game loaded (build %u, main at %#llx)", (unsigned)g_api->game_version,
              (unsigned long long)(g_api->main_base ? g_api->main_base() : 0));
}

// ---- the player's screen
// nn::swkbd::ShowKeyboard: the Room ID the player typed in the launcher (voice.c's room_id_poll keeps
// MKW_SWKBD_ANSWER equal to it while online), else the runtime's own text entry.
static int text_entry(const char *header, const char *guide, char *answer, size_t cap) {
    char id[64];
    if (!answer || !cap || !plat_getenv("MKW_SWKBD_ANSWER", id, sizeof id) || !id[0]) {
        mkwmp_log("plugin: text entry \"%s\" / \"%s\": no Room ID from the launcher, the runtime answers",
                  header ? header : "", guide ? guide : "");
        return 0;
    }
    snprintf(answer, cap, "%s", id);
    mkwmp_log("plugin: text entry \"%s\" answered with the launcher's Room ID %s", header ? header : "", answer);
    return 1;
}

static void notice_sink(NotifyLevel level, const char *text) {
    g_api->notice(text, level == NOTIFY_ERROR || level == NOTIFY_WARN ? 8 : 5);
}

// ---- entry
static int refuse(const MkwPluginApi *api, const char *reason) {
    // fail sits in every table that has a size field covering it; a table too short for it gets no message
    if (api && api->size >= offsetof(MkwPluginApi, fail) + sizeof api->fail && api->fail) api->fail(reason);
    mkwmp_log("plugin: refused: %s", reason);
    return 1;
}

MKW_PLUGIN_EXPORT int mkw_plugin_init(const MkwPluginApi *api, uint32_t api_version) {
    if (!api) return 1;
    if (api_version < MKW_PLUGIN_API_VERSION || api->size < sizeof(MkwPluginApi))
        return refuse(api, "MKWMP needs mkw-recomp plugin API version 1 or newer; update mkw-recomp");

    plat_set_home(api->plugin_dir);   // mkwmp.toml, mkwmp.env, mkwmp.log, the voice library: the plugin's folder
    probe_log_start();
    mkwmp_log("plugin: mkw-recomp %s, plugin API %u (table %u bytes), plugin dir %s",
              api->runtime_version ? api->runtime_version : "?", (unsigned)api_version, (unsigned)api->size,
              api->plugin_dir ? api->plugin_dir : "(none: the executable's folder)");

    // what npln_register needs (it checks the same entries again, as it does for COFF)
    const char *missing = !api->override ? "override" : !api->guest_symbol ? "guest_symbol" : !api->log ? "log"
                        : !api->call ? "call" : !api->cfg_get ? "cfg_get" : !api->core_away ? "core_away"
                        : !api->core_back ? "core_back" : NULL;
    if (missing) {
        char reason[128];
        snprintf(reason, sizeof reason, "MKWMP: this mkw-recomp's plugin API has no %s; online play is off", missing);
        return refuse(api, reason);
    }
    g_api = api;

    // ---- required
    g_rt.override = pl_override;
    g_rt.guest_modules = pl_guest_modules;   // 0 until on_game_loaded: overrides are only taken during init
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
    g_rt.host_fn_at = api->host_at ? pl_host_fn_at : NULL;
    g_rt.game_code_loaded = pl_guest_modules;   // the game's code is loaded with its modules
    g_rt.dispatch_hook = api->dispatch_hook ? pl_dispatch_hook : NULL;
    g_rt.guest_thread = pl_guest_thread;

    if (!api->on_game_loaded || api->on_game_loaded(on_game_loaded, NULL))
        mkwmp_log("plugin: no on_game_loaded; the game counts as loaded once main_base is known");
    if (api->text_entry_hook && !api->text_entry_hook(text_entry)) mkwmp_log("plugin: text-entry hook installed");
    else mkwmp_log("plugin: no text-entry hook; the Room ID reaches the game only through MKW_SWKBD_ANSWER");
    if (api->notice) notify_set_sink(notice_sink);

    log_line("runtime resolved from the plugin API");
    int n = probe_register();
    if (n < 0) return refuse(api, "MKWMP: the probe does not fit this mkw-recomp (see mkwmp.log); online play is off");
    return 0;
}
