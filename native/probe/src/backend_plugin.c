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
// Camera: with plugin API 3 and game 1.8.0, camera.c answers the game's GameChat calls (nn::chat) and fills the images
// it shows above the karts; on anything older nothing of it is registered.
//
// Accounts: with plugin API 2, the mod's options page (modpage.c, in the start screen) stores the player's consent,
// server, PIN and voice switch; at init they make the probe sign in itself (seatd.c answers the launcher's local API
// in-process). A mkwmp.toml with a launcher still wins; API 1 runtimes use mkwmp.toml only.
//
// Types: the probe's Cpu (recomp.h) starts with MkwCpu's guaranteed prefix (x0..x31, sp, pc), and both are passed by
// pointer, so a GuestFn is handed to the runtime as an MkwGuestFn as it is. Nothing here reads past that prefix.
#include "mkw_plugin.h"

#include "camera.h"
#include "cameratab.h"
#include "config.h"
#include "friends.h"
#include "guesthook.h"
#include "launcher.h"
#include "log.h"
#include "notify.h"
#include "plat.h"
#include "probe.h"
#include "rtapi.h"
#include "seatapi.h"
#include "seat.h"
#include "seatd.h"
#include "voice.h"
#include "voice_ui.h"
#include "voicetab.h"

#include <stdarg.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

RtApi g_rt;

static const MkwPluginApi *g_api;
static int g_active;                     // probe_register registered overrides (mkwmp.toml or MKWMP_ENABLE)
static int g_needs_setup;                // enabled, but the options page was never completed: said at game load
static int g_seat_early;                 // take the seat when the game loads (seat mode "start")

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
    if (g_seat_early && g_active) seat_take_early();   // connecting... / in line / connected, in the menus already
    if (g_needs_setup && g_api->notice)
        g_api->notice("MKWMP: open Mods, MKWMP, Options on the start screen to set up online play", 10);
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

// ---- the mod's own settings (plugin API 2: its options page in the start screen, modpage.c)
static int has_settings(const MkwPluginApi *api) {
    return api->size >= offsetof(MkwPluginApi, setting_set) + sizeof api->setting_set && api->setting_get &&
           api->profile_name;
}

MKW_PLUGIN_EXPORT void mkw_mod_page(const MkwPage *p);   // modpage.c: also the in-game MKWMP tab

static void game_exiting(void *user) {
    (void)user;
    seatd_stop();
}

// With the player's consent and a signed-up name, the plugin signs in itself (seatd.c) unless a launcher is configured.
static void use_settings(const MkwPluginApi *api) {
    if (!has_settings(api)) {
        mkwmp_log("plugin: runtime without mod settings (plugin API 1): mkwmp.toml and the MKWMP launcher only");
        return;
    }
    char consent[8] = "", server[256] = "", pin[16] = "", pin_name[64] = "", voice[8] = "";
    api->setting_get("consent18", consent, sizeof consent);
    api->setting_get("server", server, sizeof server);
    api->setting_get("pin", pin, sizeof pin);
    api->setting_get("pin_name", pin_name, sizeof pin_name);
    int voice_on = api->setting_get("voice", voice, sizeof voice) && !strcmp(voice, "1");   // off until turned on
    const char *name = api->profile_name;
    if (strcmp(consent, "1") || !seatapi_name_ok(name) || !seatapi_pin_ok(pin) || strcmp(pin_name, name)) {
        g_needs_setup = !mkwmp_config()->present;   // a mkwmp.toml (the launcher's) still works without the page
        mkwmp_log("plugin: options page not completed for player %s (consent %s, %s): %s", name,
                  strcmp(consent, "1") ? "no" : "yes", seatapi_pin_ok(pin) && !strcmp(pin_name, name) ? "signed up" : "not signed up",
                  g_needs_setup ? "online play stays off" : "using mkwmp.toml");
        return;
    }
    mkwmp_config_use_direct(name);
    if (!mkwmp_config()->direct) {
        mkwmp_log("plugin: mkwmp.toml names a launcher: it holds the seat, the mod's settings wait");
        return;
    }
    SeatdConfig c = {0};
    seatapi_base(server, c.server, sizeof c.server);
    snprintf(c.name, sizeof c.name, "%s", name);
    snprintf(c.pin, sizeof c.pin, "%s", pin);
    c.voice = voice_on;
    c.setting_get = api->setting_get;
    c.setting_set = api->setting_set;
    c.watch_friends = 1;   // friends' notices in game, as the launcher's tray gave
    c.lang_pt = api->language && (!strcmp(api->language, "pt-BR") || !strcmp(api->language, "pt"));
    seatd_start(&c);
    launcher_set_direct(seatd_request);
    char mode[16] = "";
    api->setting_get("seat_mode", mode, sizeof mode);
    g_seat_early = strcmp(mode, "online") != 0;   // "start" (default, as the launcher's): the seat when the game loads
    // the game's own settings get the mod's tabs: voice chat, and the account and friends (the options page)
    if (api->size >= offsetof(MkwPluginApi, settings_tab) + sizeof api->settings_tab && api->settings_tab) {
        api->settings_tab(c.lang_pt ? "Chat de voz" : "Voice chat", voicetab_draw);
        api->settings_tab("MKWMP", mkw_mod_page);
    }
    // the seat given back when the game ends (else the server frees it after 60 s)
    if (api->size >= offsetof(MkwPluginApi, on_exit) + sizeof api->on_exit && api->on_exit)
        api->on_exit(game_exiting, NULL);
    else
        mkwmp_log("plugin: runtime without on_exit: the seat is freed by the server's timeout");
}

// ---- the GameChat camera (camera.c; plugin API 3, game 1.8.0): what it asks of the session
static uint64_t cam_self_account(void) {
    const char *name = mkwmp_config()->name;
    return name[0] ? friends_account_id(name) : 0x1122334455667788ull;   // npln.c's nsa_id (FAKE_NSA_ID without a name)
}
static const char *cam_self_name(void) {
    const char *name = mkwmp_config()->name;
    return name[0] ? name : g_api->size > offsetof(MkwPluginApi, profile_name) && g_api->profile_name ? g_api->profile_name : "";
}

static void use_camera(const MkwPluginApi *api, uint32_t api_version) {
    int v3 = api->size >= offsetof(MkwPluginApi, native_image_touch) + sizeof(void *);
    CameraHost h = {
        .api_version = api_version,
        .game_version = api->game_version,
        .native_image_create = v3 ? api->native_image_create : NULL,
        .native_image_touch = v3 ? api->native_image_touch : NULL,
        .setting_get = has_settings(api) ? api->setting_get : NULL,
        .mod_dir = api->mod_dir,
        .online = voice_is_online,
        .peers = voice_peer_accounts,
        .friends = friends_accounts,
        .muted = voice_is_muted,
        .self_account = cam_self_account,
        .self_name = cam_self_name,
        .account_of = friends_account_id,
    };
    if (!camera_install(&h)) return;
    int pt = api->language && (!strcmp(api->language, "pt-BR") || !strcmp(api->language, "pt"));
    if (api->settings_tab) api->settings_tab(pt ? "Câmera" : "Camera", cameratab_draw);
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
    // version 1's table ends with the overlay; version 2 adds the player, the language and the mod's settings
    if (api_version < 1 || api->size < offsetof(MkwPluginApi, language))
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

    use_settings(api);

    log_line("runtime resolved from the plugin API");
    int n = probe_register();
    if (n < 0) return refuse(message, "MKWMP does not fit this mkw-recomp (see mods/<mod>/mkwmp.log); online play is off");
    g_active = n > 0;
    if (g_active) use_camera(api, api_version);   // the GameChat camera: 1.8.0 on plugin API 3, else off (logged)
    else mkwmp_log("camera: off: online play is not set up");
    return 0;
}
