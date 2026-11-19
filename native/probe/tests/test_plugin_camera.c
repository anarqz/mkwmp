// The built plugin (mkwmp.dll / mkwmp.so) against a fake MkwPluginApi, for the GameChat camera's gate: on plugin API
// 3 with game 1.8.0 the 21 nn::chat overrides and nn::gs::TryWaitNativeSync are registered and the Camera tab is
// added; on plugin API 2, or game 1.0.0, none is, the plugin still loads, and mkwmp.log says why. One mode per run
// (the probe's state is the process's). Windows and Linux.
//
//   $zig = "C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"
//   & $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o scratch\test_plugin_camera.exe `
//       native\probe\tests\test_plugin_camera.c
//   foreach ($m in "v3", "v2", "game100") {
//       scratch\test_plugin_camera.exe native\probe\build\plugin\windows\mkwmp.dll <an empty folder per mode> $m }
// Linux: build_plugin.py --test builds and runs it against mkwmp.so.
#include "../src/mkw_plugin.h"

#include <stdarg.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
static void *lib_open(const char *path) { return LoadLibraryA(path); }
static void *lib_sym(void *lib, const char *name) { return (void *)(uintptr_t)GetProcAddress((HMODULE)lib, name); }
static void env_set(const char *k, const char *v) { SetEnvironmentVariableA(k, v); _putenv_s(k, v ? v : ""); }
#else
#include <dlfcn.h>
static void *lib_open(const char *path) { return dlopen(path, RTLD_NOW | RTLD_LOCAL); }
static void *lib_sym(void *lib, const char *name) { return dlsym(lib, name); }
static void env_set(const char *k, const char *v) { if (v) setenv(k, v, 1); else unsetenv(k); }
#endif

static int failures;
#define CHECK(c) do { if (!(c)) { printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #c); failures++; } } while (0)

static int g_chat, g_trywait, g_other, g_tabs, g_camera_tab, g_images;
static void (*g_loaded)(void *);

static void fake_host(MkwCpu *c) { (void)c; }
static void fake_log(const char *fmt, ...) { (void)fmt; }
static const char *fake_cfg_get(const char *key) { return getenv(key); }
static int fake_override(const char *name, MkwGuestFn fn) {
    if (!fn) return 0;
    if (strstr(name, "_ZN2nn4chat")) g_chat++;
    else if (!strcmp(name, "_ZN2nn2gs17TryWaitNativeSyncERKNS0_16NativeSyncHandleE")) g_trywait++;
    else g_other++;
    return 1;
}
static MkwGuestFn fake_stock(const char *name) {
    return !strncmp(name, "_ZN2nn2fs", 9) || !strcmp(name, "_ZN2nn6socket12FreeAddrInfoEPNS0_8AddrInfoE") ||
           strstr(name, "TryWaitNativeSync") || strstr(name, "AttachToRoomInfoChangedEvent") ? fake_host : NULL;
}
static uint64_t fake_call_original(MkwCpu *c) { (void)c; return 0; }
static uint64_t fake_guest_symbol(const char *name) { (void)name; return 0; }
static uint64_t fake_module_base(const char *name) { (void)name; return 0; }
static int fake_guest_modules(void) { return 0; }
static uint64_t fake_call(MkwCpu *c, uint64_t fn, int n, ...) { (void)c, (void)fn, (void)n; return 0; }
static MkwCpu *fake_current_cpu(void) { return NULL; }
static int fake_core_away(void) { return 1; }
static void fake_core_back(int *away) { *away = 0; }
static int fake_override_address(uint64_t addr, MkwGuestFn fn) { (void)addr, (void)fn; return 1; }
static void fake_on_game_loaded(void (*fn)(void *), void *user) { (void)user; g_loaded = fn; }
static void fake_notice(const char *text, int seconds) { (void)text, (void)seconds; }
static int fake_overlay_set(MkwOverlayFn draw, void *user) { (void)draw, (void)user; return 1; }
static void fake_overlay_show(MkwOverlayFn draw, int on) { (void)draw, (void)on; }
static int fake_setting_get(const char *key, char *out, size_t cap) { (void)key, (void)out, (void)cap; return 0; }   // nothing set
static int fake_setting_set(const char *key, const char *value) { (void)key, (void)value; return 1; }
static int fake_on_exit(void (*cb)(void *), void *user) { (void)cb, (void)user; return 1; }
static int fake_settings_tab(const char *title, void (*draw)(const struct MkwPage *)) {
    g_tabs += draw != NULL;
    g_camera_tab += !strcmp(title, "Camera");
    return 1;
}
static uint32_t fake_image_create(int w, int h, void **pixels, int *row_bytes) {
    *pixels = calloc((size_t)w * 4, (size_t)h);
    *row_bytes = w * 4;
    return 0x80000000u | (uint32_t)g_images++;
}
static int fake_image_touch(uint32_t handle) { return (handle & 0x80000000u) != 0; }

int main(int argc, char **argv) {
    if (argc != 4) { fprintf(stderr, "usage: %s <plugin library> <empty folder> v3|v2|game100\n", argv[0]); return 2; }
    const char *mode = argv[3];
    env_set("MKWMP_ENABLE", "1");   // engaged without a mkwmp.toml or a sign-up
    env_set("MKWMP_EAGER", NULL);
    env_set("MKWMP_CAMERA", NULL);
    void *lib = lib_open(argv[1]);
    if (!lib) { fprintf(stderr, "cannot load %s\n", argv[1]); return 1; }
    MkwPluginInit init = (MkwPluginInit)lib_sym(lib, "mkw_plugin_init");
    if (!init) { fprintf(stderr, "no mkw_plugin_init\n"); return 1; }

    MkwPluginApi api = {
        .size = sizeof api, .version = MKW_PLUGIN_API_VERSION, .port_version = "test", .game_version = "1.8.0",
        .game_id = "00", .mod_dir = argv[2], .mod_name = "mkwmp",
        .log = fake_log, .cfg_get = fake_cfg_get, .guest_modules = fake_guest_modules,
        .guest_symbol = fake_guest_symbol, .module_base = fake_module_base,
        .override_symbol = fake_override, .stock = fake_stock, .on_game_loaded = fake_on_game_loaded,
        .override_address = fake_override_address, .call = fake_call, .call_original = fake_call_original,
        .current_cpu = fake_current_cpu, .core_away = fake_core_away, .core_back = fake_core_back,
        .notice = fake_notice, .overlay_set = fake_overlay_set, .overlay_show = fake_overlay_show,
        .language = "en", .profile_name = "Bia", .setting_get = fake_setting_get, .setting_set = fake_setting_set,
        .on_exit = fake_on_exit, .settings_tab = fake_settings_tab,
        .native_image_create = fake_image_create, .native_image_touch = fake_image_touch,
    };
    uint32_t version = 3;
    if (!strcmp(mode, "v2")) {   // a plugin API 2 runtime: the table ends with settings_tab
        api.size = (uint32_t)offsetof(MkwPluginApi, native_image_create);
        api.version = version = 2;
    } else if (!strcmp(mode, "game100")) {
        api.game_version = "1.0.0";
    } else if (strcmp(mode, "v3")) {
        fprintf(stderr, "unknown mode %s\n", mode);
        return 2;
    }
    const char *message = NULL;
    CHECK(init(&api, version, &message) == 0 && !message);   // the plugin loads on every one of them
    CHECK(g_other > 40);                                       // online play's own overrides, as ever
    if (!strcmp(mode, "v3")) {
        CHECK(g_chat == 21 && g_trywait == 1 && g_camera_tab == 1);
    } else {
        CHECK(g_chat == 0 && g_trywait == 0 && g_camera_tab == 0 && g_images == 0);
    }
    CHECK(g_loaded != NULL);
    if (g_loaded) g_loaded(NULL);

    char path[1200];
    snprintf(path, sizeof path, "%s/mkwmp.log", argv[2]);
    static char text[1 << 16];
    FILE *f = fopen(path, "rb");
    CHECK(f != NULL);
    if (f) {
        text[fread(text, 1, sizeof text - 1, f)] = 0;
        fclose(f);
    }
    CHECK(strstr(text, "overrides registered") != NULL);
    // 28: the 21 nn::chat ones, TryWaitNativeSync and the 6 nn::oe screenshot / recording calls (counted in g_other)
    if (!strcmp(mode, "v3")) CHECK(strstr(text, "camera: 28 overrides (plugin API 3)") != NULL);
    else if (!strcmp(mode, "v2")) CHECK(strstr(text, "camera: off: this mkw-recomp's plugin API is older than version 3") != NULL);
    else CHECK(strstr(text, "camera: off: the game is not 1.8.0") != NULL);
    printf("test_plugin_camera %s: %s (%d nn::chat overrides, TryWaitNativeSync %d, Camera tab %d)\n", mode,
           failures ? "FAIL" : "PASS", g_chat, g_trywait, g_camera_tab);
    fflush(stdout);
#ifdef _WIN32
    TerminateProcess(GetCurrentProcess(), failures != 0);   // the probe's threads are not ours to wait for
#endif
    _Exit(failures != 0);
}
