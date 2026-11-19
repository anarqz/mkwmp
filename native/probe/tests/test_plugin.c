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
// With MKWMP_TEST_SERVER (build_plugin.py --test starts tests/fake_seat.py), also the options page (mkw_mod_page)
// against a fake MkwPage: consent first, sign-up storing the PIN, a friend request, a profile name the server refuses,
// and a version 2 init with those settings signing in itself (in a child process: the probe's state is global).
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

// ---- a fake MkwPage: settings in memory, the widgets recorded, one button "clicked" by label
enum { MAX_SETTINGS = 32 };
static struct { char key[65], value[1025]; } g_settings[MAX_SETTINGS];
static int g_nsettings, g_disabled;
static char g_click[128], g_texts[16384];
static struct { char name[40], detail[60]; } g_rows[64];
static int g_nrows;

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
static void p_log(const char *fmt, ...) { (void)fmt; }
static void p_redraw(void) {}
static void record(const char *t) {
    size_t n = strlen(g_texts);
    snprintf(g_texts + n, sizeof g_texts - n, "%s\n", t ? t : "");
}
static void p_heading(const char *t) { record(t); }
static void p_text(const char *t) { record(t); }
static void p_note(int kind, const char *t) { (void)kind; record(t); }
static void p_separator(void) {}
static void p_same_line(void) {}
static int g_stack[32], g_depth;   // each section's own flag; disabled while any is
static void p_disabled_begin(int on) {
    assert(g_depth < 32);
    g_stack[g_depth++] = on != 0;
    g_disabled += on != 0;
}
static void p_disabled_end(void) {
    assert(g_depth > 0);   // always paired
    g_disabled -= g_stack[--g_depth];
}
static void p_busy(const char *t) { record(t); }
static int p_checkbox(const char *k, const char *l, int d) { (void)k, (void)l, (void)d; return 0; }
static int p_input(const char *k, const char *l, const char *h, int m, int f) { (void)k, (void)l, (void)h, (void)m, (void)f; return 0; }
static int p_choice(const char *k, const char *l, const char *const *v, const char *const *lb, int n) {
    (void)k, (void)l, (void)v, (void)lb, (void)n; return 0;
}
static int p_button(const char *label) {
    if (g_disabled || strcmp(label, g_click)) return 0;
    g_click[0] = 0;
    return 1;
}
static void p_list_begin(const char *id, int rows) { (void)id, (void)rows; g_nrows = 0; }
static int p_list_row(const char *name, const char *detail, const char *const *b, int n) {
    (void)b, (void)n;
    if (g_nrows < 64) {
        snprintf(g_rows[g_nrows].name, sizeof g_rows[0].name, "%s", name);
        snprintf(g_rows[g_nrows++].detail, sizeof g_rows[0].detail, "%s", detail ? detail : "");
    }
    return 0;
}
static void p_list_end(void) {}
static int g_cols;
static void p_columns_begin(int n) { assert(!g_cols); g_cols = n; }
static void p_column_next(void) { assert(g_cols); }
static void p_columns_end(void) { assert(g_cols); g_cols = 0; }
static int p_slider(const char *k, const char *l, int mn, int mx, int d) { (void)k, (void)l, (void)mn, (void)mx, (void)d; return 0; }
static void p_list_empty(const char *t) { (void)t; }

static MkwModPage g_page_fn;
static const char *g_profile = "Bia";
static int g_opened = 1;
static void frame(void) {
    MkwPage pg = {
        .size = sizeof pg, .version = MKW_PLUGIN_API_VERSION, .port_version = "test", .mod_dir = ".", .mod_name = "mkwmp",
        .language = "en", .profile_name = g_profile, .scale = 1, .opened = g_opened,
        .log = p_log, .setting_get = s_get, .setting_set = s_set, .redraw = p_redraw,
        .heading = p_heading, .text = p_text, .note = p_note, .separator = p_separator, .same_line = p_same_line,
        .disabled_begin = p_disabled_begin, .disabled_end = p_disabled_end, .busy = p_busy, .checkbox = p_checkbox, .input = p_input, .choice = p_choice,
        .button = p_button, .list_begin = p_list_begin, .list_row = p_list_row, .list_end = p_list_end,
        .list_empty = p_list_empty, .columns_begin = p_columns_begin, .column_next = p_column_next,
        .columns_end = p_columns_end, .slider = p_slider,
    };
    g_opened = 0;
    g_texts[0] = 0;
    g_disabled = g_depth = 0;
    g_page_fn(&pg);
    assert(g_depth == 0);   // every section the page opened, it closed
    assert(!g_cols);        // and its columns
}
// Frames until cond holds (the page's worker thread answers), at most 8 s.
#define FRAMES_UNTIL(cond)                                                                                             \
    do {                                                                                                               \
        int ok_ = 0;                                                                                                   \
        for (int f_ = 0; f_ < 400 && !(ok_ = !!(cond)); f_++) { frame(); usleep(20000); }                                \
        if (!ok_) { fprintf(stderr, "timed out: %s\n--- page:\n%s", #cond, g_texts); assert(0); }                     \
    } while (0)
static int has_row(const char *name, const char *detail) {
    for (int i = 0; i < g_nrows; i++)
        if (!strcmp(g_rows[i].name, name) && !strcmp(g_rows[i].detail, detail)) return 1;
    return 0;
}
static int setting_is(const char *key, const char *value) {
    char v[64];
    return s_get(key, v, sizeof v) && !strcmp(v, value);
}
static int v2_get(const char *key, char *out, size_t cap) { return s_get(key, out, cap); }
static void (*g_exit_cb)(void *);
static int g_tabs;
static int v2_settings_tab(const char *title, void (*draw)(const struct MkwPage *)) { (void)title; assert(draw); g_tabs++; return 1; }
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

    // ---- the options page and the plugin's own sign-in, against the fake seat server
    const char *server = getenv("MKWMP_TEST_SERVER");
    if (server && *server) {
        g_page_fn = (MkwModPage)dlsym(so, "mkw_mod_page");
        assert(g_page_fn);
        s_set("server", server);
        frame();
        assert(strstr(g_texts, "Accept the notice above"));       // nothing works before the consent
        snprintf(g_click, sizeof g_click, "Sign up as Bia");
        frame();
        assert(g_click[0]);                                          // greyed out: not pressed
        s_set("consent18", "1");
        FRAMES_UNTIL(!g_click[0]);
        FRAMES_UNTIL(setting_is("pin_name", "Bia"));
        char pin[16];
        assert(s_get("pin", pin, sizeof pin) && strlen(pin) == 6);
        FRAMES_UNTIL(strstr(g_texts, "Your PIN is ") && strstr(g_texts, "Signed up as Bia"));
        printf("options page: consent first, then signed up as Bia (PIN stored, shown once)\n");
        // a friend request: the list shows it as sent
        s_set("friend_add", "Rafa");
        snprintf(g_click, sizeof g_click, "Send request");
        FRAMES_UNTIL(!g_click[0]);
        FRAMES_UNTIL(has_row("Rafa", "request sent"));
        assert(!s_get("friend_add", pin, sizeof pin));               // the field is emptied
        printf("options page: friend request to Rafa listed as sent\n");
        // signing up a name someone has: the page says so and offers the PIN field
        g_profile = "Luz";
        g_opened = 1;
        snprintf(g_click, sizeof g_click, "Sign up as Luz");
        FRAMES_UNTIL(!g_click[0]);
        FRAMES_UNTIL(strstr(g_texts, "already signed up"));
        s_set("pin_entry", "999999");
        snprintf(g_click, sizeof g_click, "Use this PIN");
        FRAMES_UNTIL(!g_click[0]);
        FRAMES_UNTIL(strstr(g_texts, "Wrong PIN"));
        s_set("pin_entry", "333333");
        snprintf(g_click, sizeof g_click, "Use this PIN");
        FRAMES_UNTIL(!g_click[0]);
        FRAMES_UNTIL(setting_is("pin_name", "Luz") && setting_is("pin", "333333"));
        printf("options page: a taken name, a wrong PIN refused, the right one stored\n");
        g_profile = "Bad Name";
        g_opened = 1;
        frame();
        assert(strstr(g_texts, "cannot be used online"));
        g_profile = "Luz";

        // a version 2 runtime with those settings: the plugin signs in itself (in a child: the probe is global)
        pid_t child = fork();
        if (!child) {
            char dir2[600];
            snprintf(dir2, sizeof dir2, "%s/v2", dir);
            mkdir(dir2, 0755);
            MkwPluginApi v2 = api;
            v2.mod_dir = dir2;
            v2.language = "en";
            v2.profile_name = "Luz";
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
            if (g_tabs != 2) _exit(15);  // Voice chat and MKWMP in the in-game settings
            g_exit_cb(NULL);             // nothing seated yet: a no-op that must not hang
            _exit(0);
        }
        int st = 0;
        waitpid(child, &st, 0);
        if (!WIFEXITED(st) || WEXITSTATUS(st)) fprintf(stderr, "child: status %d\n", WIFEXITED(st) ? WEXITSTATUS(st) : -1);
        assert(WIFEXITED(st) && WEXITSTATUS(st) == 0);
        printf("version 2 init with the page's settings: engaged, signing in itself as Luz\n");

        // a voice-abuse report: filed, shown as waiting, withdrawn; reporting oneself is greyed out
        g_opened = 1;
        FRAMES_UNTIL(strstr(g_texts, "Report voice abuse") && !strstr(g_texts, "Checking your reports"));
        s_set("report_name", "Luz");
        snprintf(g_click, sizeof g_click, "Send report");
        frame();
        assert(g_click[0]);                                          // oneself: not pressed
        s_set("report_name", "Rafa");
        s_set("report_reason", "hate");
        s_set("report_details", "slurs in \"Mario Circuit\"\\lobby");
        FRAMES_UNTIL(!g_click[0]);
        FRAMES_UNTIL(strstr(g_texts, "Your report about Rafa (Hate speech or slurs) is waiting"));
        assert(!s_get("report_name", pin, sizeof pin));              // the form is emptied
        snprintf(g_click, sizeof g_click, "Withdraw");
        FRAMES_UNTIL(!g_click[0]);
        FRAMES_UNTIL(strstr(g_texts, "Report withdrawn.") && !strstr(g_texts, "Your report about"));
        printf("options page: a report filed, shown as waiting, withdrawn; oneself cannot be reported\n");

        // deleting the account: asked first, then gone from the server (the name is free again), the PIN forgotten
        snprintf(g_click, sizeof g_click, "Delete my account");
        FRAMES_UNTIL(!g_click[0]);
        FRAMES_UNTIL(strstr(g_texts, "Delete \"Luz\" from this server?"));
        snprintf(g_click, sizeof g_click, "Delete");
        FRAMES_UNTIL(!g_click[0]);
        FRAMES_UNTIL(strstr(g_texts, "Account deleted from this server."));
        assert(!s_get("pin", pin, sizeof pin) && !s_get("pin_name", pin, sizeof pin));
        snprintf(g_click, sizeof g_click, "Sign up as Luz");         // free again: anyone can sign up as it
        FRAMES_UNTIL(!g_click[0]);
        FRAMES_UNTIL(setting_is("pin_name", "Luz"));
        printf("options page: the account deleted after a confirmation, the name free again\n");
    } else {
        printf("(MKWMP_TEST_SERVER unset: options page not tested)\n");
    }

    remove(path);
    snprintf(path, sizeof path, "%s/mkwmp.env", dir);
    remove(path);
    rmdir(dir);
    printf("test_plugin: OK\n");
    return 0;
}
