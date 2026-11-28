// The plugin backend of rtapi.h: mkw-recomp's native plugin API, version 1 (mkw_plugin.h: upstream's header as it
// is, runtime/plugin/mkw_plugin.h of adriadam10/mkw-recomp), Windows and Linux.
//
// The runtime loads mods/<mod>/plugin/mkwmp.dll (mkwmp.so) when the player has the mod enabled and calls
// mkw_plugin_init once, on the main thread, before the game code is loaded. We fill g_rt from its table and run the
// start-up shared with the winmm.dll proxy (probe.c). What the COFF backend dug out of mkw.exe (symbols, the .ovr
// table, g_host, the rt_dispatch import) is an API entry here, with two differences:
//   - indirect-call hooks are per address (override_address) and only while the game is being loaded, so the probe's
//     version-specific hooks (npln_address_hooks, camera_game_loaded) go in from on_game_loaded, not lazily online;
//   - the mod draws nothing of its own into the game: no labels, no notices (api->notice), no keypad (mkw-recomp
//     draws its own number pad for the software keyboard). What it does draw is the launcher's: its toasts and, on
//     plugin API 5, its overlay page, in the game's own frames (ingame.h; the launcher decides, the mod only draws).
//     The overlay trigger (overlay.h) is the one input of its own.
//
// Folders: the probe's home (mkwmp.log, mkwmp.env, the voice library) is the mod's folder, mods/<mod>/. mkwmp.toml
// and the lobby CA are read from there, else from the game's folder, where the MKWMP launcher writes them
// (config.c).
//
// Camera: with plugin API 3 and game 1.8.0, camera.c answers the game's GameChat calls (nn::chat) and fills the images
// it shows above the karts; on anything older nothing of it is registered.
//
// Accounts: everything is set up in the MKWMP launcher. The mod has no pages or tabs of its own in mkw-recomp's menus;
// the launcher writes the account it signed in with (consent18, server, pin, pin_name), the voice switch, devices and
// volumes and the camera switches into the mod's settings (plugin API 2: <player>/mod-settings/mkwmp.ini) whenever the
// game is closed (launcher/src/Mkwmp.Core/Launcher.ModSettings.cs). Started without the launcher, the mod signs in
// itself with them (seatd.c answers the launcher's local API in-process). A mkwmp.toml with a running launcher still
// wins; API 1 runtimes use mkwmp.toml only.
//
// Types: the probe's Cpu (recomp.h) starts with MkwCpu's guaranteed prefix (x0..x31, sp, pc), and both are passed by
// pointer, so a GuestFn is handed to the runtime as an MkwGuestFn as it is. Nothing here reads past that prefix.
#include "mkw_plugin.h"
#include "winepad.h"   // the controller under Wine / Proton: XInput asked at load (winepad.h)

#include "breadcrumbs.h"
#include "camera.h"
#include "camlib.h"
#include "config.h"
#include "crash.h"
#include "friends.h"
#include "guesthook.h"
#include "launcher.h"
#include "log.h"
#include "nextendo.h"
#include "nnssl.h"
#include "overlay.h"
#include "plat.h"
#include "probe.h"
#include "roomnet.h"
#include "rtapi.h"
#include "seatapi.h"
#include "seat.h"
#include "seatd.h"
#include "voice.h"

#include <stdarg.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

// The one mkw-recomp the mod runs on (launcher/src/Mkwmp.Core/Targets/MkwRecomp/Recomp.cs, Supported): its plugin API's
// port_version, exactly; or MKWMP's own build of that release (the fork's tags: "0.1.18-mkwmp", "0.1.18-mkwmp.1", as
// 0.1.15-mkwmp and 0.1.16-mkwmp.1 were), which adds plugin API 5 to it. What the runtime can do is never read from
// this string: every entry is taken by the size of its table (overlay.c, ingame.h, camera.c).
#define MKWMP_RECOMP_VERSION "0.1.18"
#define MKWMP_RECOMP_FORK "-mkwmp"

static int recomp_supported(const char *version) {
    size_t n = strlen(MKWMP_RECOMP_VERSION), m = strlen(MKWMP_RECOMP_FORK);
    if (!version || strncmp(version, MKWMP_RECOMP_VERSION, n) != 0) return 0;
    const char *rest = version + n;
    if (!*rest) return 1;                                   // the official release
    if (strncmp(rest, MKWMP_RECOMP_FORK, m) != 0) return 0; // "0.1.180", "0.1.18-other"
    return !rest[m] || rest[m] == '.';                      // "-mkwmp", "-mkwmp.1"
}

RtApi g_rt;

static const MkwPluginApi *g_api;
static int g_active;                     // probe_register registered overrides (mkwmp.toml or MKWMP_ENABLE)
static int g_needs_setup;                // enabled, but no account set up by the launcher: said at game load
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
    mkwmp_event("plugin: game %s loaded (build id %s, main at %#llx)", g_api->game_version ? g_api->game_version : "?",
              g_api->game_id ? g_api->game_id : "?", (unsigned long long)main);
    if (g_seat_early && g_active) seat_take_early();   // connecting... / in line / connected, in the menus already
    if (g_needs_setup)
        mkwmp_log("plugin: online play is not set up: sign in with the MKWMP launcher (it writes the mod's settings)");
    if (main && main != GUEST_MAIN(0))
        mkwmp_log("plugin: main is not at %#llx: every version-specific hook stays off", (unsigned long long)GUEST_MAIN(0));
    else if (g_active) {
        npln_address_hooks();   // error skips, idle-kick guard, BulkGet / Pia / matchmake traces (by game build)
        camera_game_loaded();   // the game's "Use the camera?" prompt (nothing unless the camera is installed)
    }
    g_rt.override_address = NULL;   // closed from here on: a later install attempt sees no means and stays off
}

// ---- the mod's settings (plugin API 2), written by the MKWMP launcher while the game is closed
static int has_settings(const MkwPluginApi *api) {
    return api->size >= offsetof(MkwPluginApi, setting_set) + sizeof api->setting_set && api->setting_get;
}

static void game_exiting(void *user) {
    (void)user;
    // the devices closed before the process goes: the webcam, then the microphone and output (the relay is left)
    camlib_stop_now();
    voice_shutdown();
    seatd_stop();
}

// With the player's consent and a signed-up name (both from the launcher), the plugin signs in itself (seatd.c) unless
// a running launcher started the game.
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
    // The MKWMP name is the one the launcher signed in with (pin_name), not the game's profile name.
    const char *name = pin_name;
    // A mkwmp.toml the MKWMP launcher left beside mkw.exe names its local API; the launcher keeps it when it hands the
    // game over to the mod. Started without the launcher (mkw-recomp's own start screen), nobody answers there: the
    // mod's own settings and sign-in take over instead of waiting on a launcher that is not running.
    if (mkwmp_config()->launcher_port && !launcher_alive()) {
        mkwmp_log("plugin: mkwmp.toml names a launcher at port %d, but none is running: the mod's own settings apply",
                  mkwmp_config()->launcher_port);
        mkwmp_config_ignore_launcher();
    }
    int lang_pt = api->language && (!strcmp(api->language, "pt-BR") || !strcmp(api->language, "pt"));
    if (strcmp(consent, "1") || !seatapi_name_ok(name) || !seatapi_pin_ok(pin)) {
        g_needs_setup = !mkwmp_config()->present;   // a mkwmp.toml (the launcher's) still works without them
        mkwmp_log("plugin: no MKWMP account in the mod's settings (consent %s, %s; the MKWMP launcher writes them): %s",
                  strcmp(consent, "1") ? "no" : "yes", seatapi_name_ok(name) && seatapi_pin_ok(pin) ? "signed up" : "not signed up",
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
    c.watch_friends = 1;   // presence and the friends list (their changes are logged, not shown in the game)
    c.lang_pt = lang_pt;
    seatd_start(&c);
    launcher_set_direct(seatd_request);
    char mode[16] = "";
    api->setting_get("seat_mode", mode, sizeof mode);
    g_seat_early = strcmp(mode, "online") != 0;   // "start" (default, as the launcher's): the seat when the game loads
    // the seat given back when the game ends (else the server frees it after 60 s)
    if (api->size >= offsetof(MkwPluginApi, on_exit) + sizeof api->on_exit && api->on_exit)
        api->on_exit(game_exiting, NULL);
    else
        mkwmp_log("plugin: runtime without on_exit: the seat is freed by the server's timeout");
}

// ---- the GameChat camera (camera.c; plugin API 3, game 1.8.0): what it asks of the session
static uint64_t cam_self_account(void) {
    // npln.c's nsa_id: on Nextendo the session's account id, the one other players' games know this player by
    uint64_t account = mkwmp_config_nextendo() ? nextendo_account() : 0;
    if (account) return account;
    const char *name = mkwmp_config()->name;
    return name[0] ? friends_account_id(name) : 0x1122334455667788ull;   // FAKE_NSA_ID without a name
}
static const char *cam_self_name(void) {
    // on Nextendo the account's username, as GetNickname gives the game (npln.c ov_nickname)
    static char username[64];
    if (mkwmp_config_nextendo() && nextendo_username(username, sizeof username) && username[0]) return username;
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
        .friends = friends_accounts,
        .muted = voice_is_muted,
        .self_account = cam_self_account,
        .self_name = cam_self_name,
        .account_of = friends_account_id,
    };
    // room mode (voice in a friend room only) is wanted also where the camera cannot work (an older runtime or game)
    ChatRoomHost room = {.self_account = cam_self_account, .friends = friends_accounts};
    chatroom_install(&room);
    roomnet_start();   // the friend room on the relay: room mode, its state, the camera's frames (nothing with the loopback)
    camera_install(&h);   // off: the reason is logged (camera_off_reason)
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
    // MKWMP runs on one mkw-recomp only (the launcher's pin, Recomp.Supported): an earlier release is refused, so a
    // player who starts the game outside the launcher is never online on it. A developer's build the launcher accepts
    // ([dev] recomp_exe_sha256) may report another version: the launcher sets MKWMP_DEV_RECOMP=1 for it.
    if (!recomp_supported(api->port_version)) {
        char dev[8];
        if (plat_getenv("MKWMP_DEV_RECOMP", dev, sizeof dev) && !strcmp(dev, "1")) {
            mkwmp_log("plugin: mkw-recomp %s is not %s: accepted as a development build (MKWMP_DEV_RECOMP=1)",
                      api->port_version ? api->port_version : "?", MKWMP_RECOMP_VERSION);
        } else {
            char reason[160];
            snprintf(reason, sizeof reason, "MKWMP needs mkw-recomp %s (this is %s): update it with the MKWMP launcher",
                     MKWMP_RECOMP_VERSION, api->port_version ? api->port_version : "unknown");
            return refuse(message, reason);
        }
    }
    // Crash records (crash.h): <game folder>\mkwmp-crash.log and a minidump, for the launcher to offer as a report.
    // The runtime is up already, so its fault handler comes after ours: ours only remembers what that one may repair.
    crash_install(NULL, 0);
    crumb_state("loader", "mod");
    crumb_state("recomp", "%s (plugin API %u)", api->port_version ? api->port_version : "?", (unsigned)api_version);
    crumb_state("game", "%s", api->game_version ? api->game_version : "?");
    winepad_start();   // under Wine only, before the runtime's input starts SDL's gamepads (at its first poll)

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
    // api->notice is not used: the mod's status (connected, in line, friends online) is mkwmp.log's and the launcher's,
    // never a toast over the game (notify.c keeps it in the log only)

    use_settings(api);

    log_line("runtime resolved from the plugin API");
    int n = probe_register();
    if (n < 0) return refuse(message, "MKWMP does not fit this mkw-recomp (see mods/<mod>/mkwmp.log); online play is off");
    g_active = n > 0;
    // Guide / Select+Start / Select held bring the launcher over the game (overlay.h); the launcher's toasts and, on
    // plugin API 5, its overlay page are drawn in the game's own frames (ingame.h)
    overlay_install(api);
    if (g_active && mkwmp_config_nextendo()) {
        // Nextendo Network has no MKWMP relay: no friend room, no camera, no voice (NEXTENDO_API.md section 5)
        mkwmp_log("camera: off: not available on Nextendo Network");
        camera_set_off_reason("not available on Nextendo Network");
        nnssl_register();   // ghost data: the game's HTTPS transfers of document attachments (nnssl.h)
    } else if (g_active) use_camera(api, api_version);   // the GameChat camera: 1.8.0 on plugin API 3, else off (logged)
    else {
        mkwmp_log("camera: off: online play is not set up");
        camera_set_off_reason("online play is not set up (sign in with the MKWMP launcher)");
    }
    return 0;
}
