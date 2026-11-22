// The plugin build (build/plugin/linux/mkwmp.so) against a fake MkwPluginApi: no mkw-recomp needed.
//
//   python3 native/probe/build_plugin.py --test
// or by hand:
//   zig cc -target x86_64-linux-gnu -Wall -Wextra -Werror -o test_plugin native/probe/tests/test_plugin.c -ldl
//   ./test_plugin native/probe/build/plugin/linux/mkwmp.so native/probe/src/npln.c
//
// Against mkw-recomp's plugin API version 1 (src/mkw_plugin.h, upstream's header). Checks: an old version and a short
// table are declined with a message; a good table gets init 0, exactly the overrides the COFF path registers (npln.c's
// kRegs, read from the source, plus the core-mask pair), the fs stock handlers asked for under the names the probe
// overrides, mkwmp.env read and mkwmp.log written in mod_dir, and on_game_loaded with main not mapped (no game build)
// installing no address override. A version 1 table (no mod settings) still loads.
// With MKWMP_TEST_SERVER (build_plugin.py --test starts tests/fake_seat.py), also a version 2 init with the settings
// the MKWMP launcher writes (consent18, server, pin, pin_name) signing in itself, and one without them staying off
// (in child processes: the probe's state is global). The mod has no options page and no settings tab of its own: it
// exports mkw_plugin_init only and never calls settings_tab.
#define _GNU_SOURCE
#undef NDEBUG
#include "../src/mkw_plugin.h"

#include <assert.h>
#include <dlfcn.h>
#include <stdarg.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

enum { MAX_NAMES = 256 };
static char *g_overrides[MAX_NAMES], *g_stocks[MAX_NAMES], *g_expected[MAX_NAMES];
static int g_n_overrides, g_n_stocks, g_n_expected, g_logs, g_n_address;
static void (*g_loaded_fn)(void *);

static void fake_host(MkwCpu *c) { (void)c; }
static void fake_log(const char *fmt, ...) { (void)fmt; g_logs++; }
static const char *fake_cfg_get(const char *key) { return getenv(key); }
static int fake_override(const char *name, MkwGuestFn fn) {
    assert(fn && g_n_overrides < MAX_NAMES);
    g_overrides[g_n_overrides++] = strdup(name);
    return 1;
}
// A runtime has stock handlers for the fs names and for the typed FreeAddrInfo (npln.c's kStockFreeAddrInfo, which
// every supported runtime has: without it the probe gives GetCurrentIpConfigInfo(InAddr *)'s slot back to it).
static MkwGuestFn fake_stock(const char *name) {
    assert(g_n_stocks < MAX_NAMES);
    g_stocks[g_n_stocks++] = strdup(name);
    return !strncmp(name, "_ZN2nn2fs", 9) || !strcmp(name, "_ZN2nn6socket12FreeAddrInfoEPNS0_8AddrInfoE") ? fake_host
                                                                                                          : NULL;
}
static uint64_t fake_call_original(MkwCpu *c) { (void)c; return 0; }
static uint64_t fake_guest_symbol(const char *name) { (void)name; return 0; }
static uint64_t fake_module_base(const char *name) { (void)name; return 0; }
static int fake_guest_modules(void) { return 0; }
static uint64_t fake_call(MkwCpu *c, uint64_t fn, int n, ...) { (void)c, (void)fn, (void)n; return 0; }
static MkwCpu *fake_current_cpu(void) { return NULL; }
static int fake_core_away(void) { return 1; }
static void fake_core_back(int *away) { *away = 0; }
static int fake_override_address(uint64_t addr, MkwGuestFn fn) { (void)addr, (void)fn; g_n_address++; return 1; }
static void fake_on_game_loaded(void (*fn)(void *), void *user) { (void)user; g_loaded_fn = fn; }
static void fake_notice(const char *text, int seconds) { (void)text, (void)seconds; }
static int fake_overlay_set(MkwOverlayFn draw, void *user) { (void)draw, (void)user; return 1; }
static void fake_overlay_show(MkwOverlayFn draw, int on) { (void)draw, (void)on; }

// ---- the mod's settings, in memory (as the MKWMP launcher leaves them in mkwmp.ini)
enum { MAX_SETTINGS = 32 };
static struct { char key[65], value[1025]; } g_settings[MAX_SETTINGS];
static int g_nsettings;

static int s_get(const char *key, char *out, size_t cap) {
    for (int i = 0; i < g_nsettings; i++)
        if (!strcmp(g_settings[i].key, key)) { snprintf(out, cap, "%s", g_settings[i].value); return 1; }
    return 0;
}
static int s_set(const char *key, const char *value) {
    for (int i = 0; i < g_nsettings; i++)
        if (!strcmp(g_settings[i].key, key)) {
            if (value) snprintf(g_settings[i].value, sizeof g_settings[i].value, "%s", value);
            else g_settings[i] = g_settings[--g_nsettings];
            return 1;
        }
    if (!value) return 1;
    assert(g_nsettings < MAX_SETTINGS);
    snprintf(g_settings[g_nsettings].key, sizeof g_settings[0].key, "%s", key);
    snprintf(g_settings[g_nsettings++].value, sizeof g_settings[0].value, "%s", value);
    return 1;
}
static int setting_is(const char *key, const char *value) {
    char v[64];
    return s_get(key, v, sizeof v) && !strcmp(v, value);
}
static int v2_get(const char *key, char *out, size_t cap) { return s_get(key, out, cap); }
static void (*g_exit_cb)(void *);
static int g_tabs;
static int v2_settings_tab(const char *title, void (*draw)(const struct MkwPage *)) {
    (void)title, (void)draw;
    g_tabs++;
    return 1;
}
static int v2_on_exit(void (*cb)(void *), void *user) { (void)user; g_exit_cb = cb; return 1; }
static int v2_set(const char *key, const char *value) { return s_set(key, value); }

static int has(char **list, int n, const char *name) {
    for (int i = 0; i < n; i++)
        if (!strcmp(list[i], name)) return 1;
    return 0;
}

// npln.c's kRegs names: every {"name", ...} between "kRegs[] = {" and the closing "};"
static void read_kregs(const char *npln_c) {
    FILE *f = fopen(npln_c, "rb");
    assert(f);
    static char src[1 << 20];
    size_t n = fread(src, 1, sizeof src - 1, f);
    fclose(f);
    src[n] = 0;
    char *p = strstr(src, "kRegs[] = {");
    assert(p);
    char *end = strstr(p, "\n};");
    assert(end);
    for (p = strstr(p, "{\""); p && p < end; p = strstr(p, "{\"")) {
        p += 2;
        char *q = strchr(p, '"');
        assert(q && g_n_expected < MAX_NAMES);
        g_expected[g_n_expected++] = strndup(p, (size_t)(q - p));
        p = q;
    }
    assert(g_n_expected > 0);
}

int main(int argc, char **argv) {
    if (argc != 3) { fprintf(stderr, "usage: %s mkwmp.so npln.c\n", argv[0]); return 2; }
    read_kregs(argv[2]);
    g_expected[g_n_expected++] = "nnosGetThreadCoreMask";
    g_expected[g_n_expected++] = "nnosSetThreadCoreMask";

    const char *tmp = getenv("TMPDIR");
    char dir[512];
    snprintf(dir, sizeof dir, "%s/mkwmp-plugin-XXXXXX", tmp && *tmp ? tmp : "/tmp");
    assert(mkdtemp(dir));
    char path[600];
    snprintf(path, sizeof path, "%s/mkwmp.env", dir);
    FILE *env = fopen(path, "wb");
    assert(env);
    fputs("# read from plugin_dir\nMKWMP_TEST_FROM_ENV_FILE=yes\n", env);
    fclose(env);

    setenv("MKWMP_ENABLE", "1", 1);   // engage without a mkwmp.toml
    unsetenv("MKWMP_EAGER");
    unsetenv("MKW_SWKBD_ANSWER");

    void *so = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!so) { fprintf(stderr, "dlopen: %s\n", dlerror()); return 1; }
    MkwPluginInit init = (MkwPluginInit)dlsym(so, "mkw_plugin_init");
    assert(init);
    assert(!dlsym(so, "npln_register") && !dlsym(so, "g_rt"));   // nothing else is exported
    assert(!dlsym(so, "mkw_mod_page"));                            // no options page: the launcher sets everything

    MkwPluginApi api = {
        .size = sizeof api, .version = MKW_PLUGIN_API_VERSION, .port_version = "test", .game_version = "1.8.0",
        .game_id = "00", .mod_dir = dir, .mod_name = "mkwmp",
        .log = fake_log, .cfg_get = fake_cfg_get, .guest_modules = fake_guest_modules,
        .guest_symbol = fake_guest_symbol, .module_base = fake_module_base,
        .override_symbol = fake_override, .stock = fake_stock, .on_game_loaded = fake_on_game_loaded,
        .override_address = fake_override_address, .call = fake_call, .call_original = fake_call_original,
        .current_cpu = fake_current_cpu, .core_away = fake_core_away, .core_back = fake_core_back,
        .notice = fake_notice, .overlay_set = fake_overlay_set, .overlay_show = fake_overlay_show,
    };

    // an older API version, and a table shorter than version 1's: declined with a message, nothing registered
    const char *message = NULL;
    assert(init(&api, 0, &message) != 0 && message && g_n_overrides == 0);
    printf("old version declined: %s\n", message);
    MkwPluginApi short_api = api;
    short_api.size = (uint32_t)offsetof(MkwPluginApi, notice);
    message = NULL;
    assert(init(&short_api, MKW_PLUGIN_API_VERSION, &message) != 0 && message && g_n_overrides == 0);
    printf("short table declined: %s\n", message);

    // the real thing, as a version 1 runtime gives it (no mod settings: the table ends with the overlay)
    MkwPluginApi v1 = api;
    v1.size = (uint32_t)offsetof(MkwPluginApi, language);
    v1.version = 1;
    message = NULL;
    assert(init(&v1, 1, &message) == 0 && !message);
    printf("init 0: %d overrides registered (kRegs %d + core-mask pair)\n", g_n_overrides, g_n_expected - 2);
    assert(g_n_overrides == g_n_expected);
    for (int i = 0; i < g_n_expected; i++) {
        if (!has(g_overrides, g_n_overrides, g_expected[i])) fprintf(stderr, "not registered: %s\n", g_expected[i]);
        assert(has(g_overrides, g_n_overrides, g_expected[i]));
        for (int k = 0; k < i; k++) assert(strcmp(g_overrides[k], g_overrides[i]));   // each name once
    }
    // the fs chaining handlers: the stock handlers of exactly the fs names the probe overrides
    int fs_asked = 0;
    for (int i = 0; i < g_n_stocks; i++)
        if (!strncmp(g_stocks[i], "_ZN2nn2fs", 9) && strcmp(g_stocks[i], "_ZN2nn2fs8MountRomEPKc") &&
            strcmp(g_stocks[i], "_ZN2nn2fs7UnmountEPKc")) {
            assert(has(g_overrides, g_n_overrides, g_stocks[i]));
            fs_asked += !has(g_stocks, i, g_stocks[i]);
        }
    assert(fs_asked == 5);
    assert(g_logs > 0);   // the probe's mlog lines reach the runtime's log
    assert(g_loaded_fn);
    g_loaded_fn(NULL);    // on_game_loaded, main not mapped: no game build, so no address override
    assert(g_n_address == 0);

    // the env file and the log are the mod folder's
    const char *v = getenv("MKWMP_TEST_FROM_ENV_FILE");
    assert(v && !strcmp(v, "yes"));
    snprintf(path, sizeof path, "%s/mkwmp.log", dir);
    FILE *log = fopen(path, "rb");
    assert(log);
    static char text[1 << 16];
    text[fread(text, 1, sizeof text - 1, log)] = 0;
    fclose(log);
    assert(strstr(text, "=== mkwmp session log ==="));
    assert(strstr(text, "runtime resolved from the plugin API"));
    assert(strstr(text, "overrides registered"));
    assert(strstr(text, "plugin: game 1.8.0 loaded"));
    printf("mkwmp.log written in mod_dir (%zu bytes), mkwmp.env applied:\n%s", strlen(text), text);

    assert(strstr(text, "runtime without mod settings (plugin API 1)"));

    // ---- the plugin's own sign-in with the launcher's settings, against the fake seat server
    const char *server = getenv("MKWMP_TEST_SERVER");
    if (server && *server) {
        s_set("server", server);
        s_set("consent18", "1");
        s_set("pin_name", "Luz");   // the launcher's name, not the game's profile name
        s_set("pin", "333333");
        assert(setting_is("pin", "333333"));
        pid_t child = fork();
        if (!child) {
            char dir2[600];
            snprintf(dir2, sizeof dir2, "%s/v2", dir);
            mkdir(dir2, 0755);
            MkwPluginApi v2 = api;
            v2.mod_dir = dir2;
            v2.language = "en";
            v2.profile_name = "Player";
            v2.setting_get = v2_get;
            v2.setting_set = v2_set;
            v2.on_exit = v2_on_exit;
            v2.settings_tab = v2_settings_tab;
            unsetenv("MKWMP_ENABLE");   // engaged by the settings alone
            const char *m = NULL;
            if (init(&v2, MKW_PLUGIN_API_VERSION, &m)) _exit(10);
            char lp[700];   // the session log stays the one the parent opened (the probe opens it once)
            snprintf(lp, sizeof lp, "%s/mkwmp.log", dir);
            FILE *lf = fopen(lp, "rb");
            if (!lf) _exit(11);
            static char t2[1 << 16];
            t2[fread(t2, 1, sizeof t2 - 1, lf)] = 0;
            fclose(lf);
            if (!strstr(t2, "the plugin signs in to") || !strstr(t2, "as Luz itself")) { fputs(t2, stderr); _exit(12); }
            if (!strstr(t2, "overrides registered")) _exit(13);
            if (!g_exit_cb) _exit(14);   // the seat is given back at the game's end
            if (g_tabs != 0) _exit(15);  // no tab of the mod's in mkw-recomp's settings
            g_exit_cb(NULL);             // nothing seated yet: a no-op that must not hang
            _exit(0);
        }
        int st = 0;
        waitpid(child, &st, 0);
        if (!WIFEXITED(st) || WEXITSTATUS(st)) fprintf(stderr, "child: status %d\n", WIFEXITED(st) ? WEXITSTATUS(st) : -1);
        assert(WIFEXITED(st) && WEXITSTATUS(st) == 0);
        printf("version 2 init with the launcher's settings: engaged, signing in itself as Luz, no tabs\n");

        // no account in the settings (the launcher never signed in): online play stays off, still no tabs
        s_set("pin", NULL);
        s_set("pin_name", NULL);
        child = fork();
        if (!child) {
            char dir3[600];
            snprintf(dir3, sizeof dir3, "%s/v2b", dir);
            mkdir(dir3, 0755);
            MkwPluginApi v2 = api;
            v2.mod_dir = dir3;
            v2.language = "en";
            v2.profile_name = "Zed";
            v2.setting_get = v2_get;
            v2.setting_set = v2_set;
            v2.on_exit = v2_on_exit;
            v2.settings_tab = v2_settings_tab;
            unsetenv("MKWMP_ENABLE");
            const char *m = NULL;
            if (init(&v2, MKW_PLUGIN_API_VERSION, &m)) _exit(20);
            if (g_tabs != 0) _exit(21);
            _exit(0);
        }
        st = 0;
        waitpid(child, &st, 0);
        if (!WIFEXITED(st) || WEXITSTATUS(st)) fprintf(stderr, "child: status %d\n", WIFEXITED(st) ? WEXITSTATUS(st) : -1);
        assert(WIFEXITED(st) && WEXITSTATUS(st) == 0);
        printf("version 2 init without an account: online play off, no tabs\n");
    } else {
        printf("(MKWMP_TEST_SERVER unset: the plugin's own sign-in not tested)\n");
    }

    remove(path);
    snprintf(path, sizeof path, "%s/mkwmp.env", dir);
    remove(path);
    rmdir(dir);
    printf("test_plugin: OK\n");
    return 0;
}
