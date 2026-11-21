#include "camlib.h"

#include "log.h"
#include "plat.h"
#include "../../camera/src/mkwmp_camera.h"

#include <stdio.h>
#include <string.h>

static struct {
    void *lib;
    void (*init)(mkwmp_camera_log_fn);
    int (*devices_json)(char *, int);
    void (*start)(const char *, int, int, int, int);
    void (*set_mirror)(int);
    void (*stop)(void);
    int (*frame)(uint8_t *, int, uint32_t *);
    int (*status)(void);
} g_lib;

static PlatLock g_lock = PLAT_LOCK_INIT;
static char g_dir[1024];
static struct { int on, mirror, width, height, fps; char device[256]; } g_want;
static uint64_t g_shown_at;        // camlib_show_devices' last call (0: never)
static char g_devices[8192];
static int g_have_devices, g_missing, g_thread;
static int g_loaded;               // g_lib is complete (read without the lock: set once, after the pointers)

static void lib_log(const char *line) { mkwmp_log("%s", line); }

// The worker's: 1 when the library is there.
static int load(void) {
    if (plat_load(&g_loaded)) return 1;
    if (g_missing) return 0;
    char path[1200], dir[sizeof g_dir];
    plat_lock_shared(&g_lock);
    memcpy(dir, g_dir, sizeof dir);
    plat_unlock_shared(&g_lock);
    if (dir[0]) {
        size_t n = strlen(dir);
        snprintf(path, sizeof path, "%s%s%s", dir, dir[n - 1] == '/' || dir[n - 1] == '\\' ? "" : "/", PLAT_CAMERA_LIB);
    } else if (!plat_home_path(path, sizeof path, PLAT_CAMERA_LIB)) {
        g_missing = 1;
        return 0;
    }
    void *m = plat_dl_open(path);
    if (!m) {
        mkwmp_log("camera: %s not found (error %lu): no capture, the test pattern still works", PLAT_CAMERA_LIB, plat_last_error());
        g_missing = 1;
        return 0;
    }
    mkwmp_camera_abi_fn abi = (mkwmp_camera_abi_fn)plat_dl_sym(m, "mkwmp_camera_abi");
    if (!abi || abi() != MKWMP_CAMERA_ABI) {
        mkwmp_log("camera: %s ABI %d, want %d: no capture", PLAT_CAMERA_LIB, abi ? abi() : -1, MKWMP_CAMERA_ABI);
        plat_dl_close(m);
        g_missing = 1;
        return 0;
    }
#define SYM(f, name) *(void **)&g_lib.f = plat_dl_sym(m, name)
    SYM(init, "mkwmp_camera_init");
    SYM(devices_json, "mkwmp_camera_devices_json");
    SYM(start, "mkwmp_camera_start");
    SYM(set_mirror, "mkwmp_camera_set_mirror");
    SYM(stop, "mkwmp_camera_stop");
    SYM(frame, "mkwmp_camera_frame");
    SYM(status, "mkwmp_camera_status");
#undef SYM
    if (!g_lib.init || !g_lib.devices_json || !g_lib.start || !g_lib.set_mirror || !g_lib.stop || !g_lib.frame || !g_lib.status) {
        mkwmp_log("camera: %s lacks an export: no capture", PLAT_CAMERA_LIB);
        plat_dl_close(m);
        memset(&g_lib, 0, sizeof g_lib);
        g_missing = 1;
        return 0;
    }
    g_lib.lib = m;
    g_lib.init(lib_log);
    plat_store(&g_loaded, 1);
    mkwmp_log("camera: capture library loaded");
    return 1;
}

static void worker(void *unused) {
    (void)unused;
    static char json[sizeof g_devices];
    uint64_t listed_at = 0, failed_at = 0;
    int capturing = 0;
    for (;;) {
        plat_sleep_ms(250);
        plat_lock_shared(&g_lock);
        int on = g_want.on, mirror = g_want.mirror, w = g_want.width, h = g_want.height, fps = g_want.fps;
        char device[sizeof g_want.device];
        memcpy(device, g_want.device, sizeof device);
        uint64_t shown_at = g_shown_at;
        plat_unlock_shared(&g_lock);
        uint64_t now = plat_ms();
        int shown = shown_at && now - shown_at < 3000;
        if (!on && !shown && !capturing) continue;   // nobody wants the camera: the library is not even loaded
        if (!load()) continue;
        if (on) {
            // a camera that did not open (busy, unplugged) is tried again every 15 s
            if (g_lib.status() != MKWMP_CAMERA_FAILED) failed_at = 0;
            else if (!failed_at) failed_at = now;
            else if (now - failed_at > 15000) g_lib.stop(), failed_at = 0;
            g_lib.start(device, w, h, fps, mirror);   // the same arguments again change nothing but the mirror
            capturing = 1;
        } else if (capturing) {
            g_lib.stop();
            capturing = 0;
        }
        // the list: when the settings open, then every 5 s while they are shown (a camera plugged in)
        if (shown && (!listed_at || now - listed_at > 5000)) {
            listed_at = now;
            if (g_lib.devices_json(json, (int)sizeof json) > 0) {
                plat_lock(&g_lock);
                if (!g_have_devices || strcmp(g_devices, json)) mkwmp_log("camera: devices %s", json);
                memcpy(g_devices, json, sizeof g_devices);
                g_have_devices = 1;
                plat_unlock(&g_lock);
            }
        } else if (!shown) {
            listed_at = 0;
        }
    }
}

static void wake(void) {   // g_lock held
    if (!g_thread) g_thread = plat_thread_start(worker, NULL);
}

void camlib_set_dir(const char *dir) {
    plat_lock(&g_lock);
    snprintf(g_dir, sizeof g_dir, "%s", dir ? dir : "");
    plat_unlock(&g_lock);
}

void camlib_want(int on, const char *device, int mirror, int width, int height, int fps) {
    plat_lock(&g_lock);
    g_want.on = on != 0, g_want.mirror = mirror != 0, g_want.width = width, g_want.height = height, g_want.fps = fps;
    snprintf(g_want.device, sizeof g_want.device, "%s", device ? device : "");
    if (on) wake();
    plat_unlock(&g_lock);
}

void camlib_stop_now(void) {
    plat_lock(&g_lock);
    g_want.on = 0;
    g_shown_at = 0;
    plat_unlock(&g_lock);
    if (plat_load(&g_loaded) && g_lib.stop) g_lib.stop();   // the webcam closed now, not at the worker's next pass
}

void camlib_show_devices(void) {
    plat_lock(&g_lock);
    g_shown_at = plat_ms();
    if (!g_shown_at) g_shown_at = 1;
    wake();
    plat_unlock(&g_lock);
}

int camlib_devices_json(char *out, size_t cap) {
    plat_lock_shared(&g_lock);
    int have = g_have_devices;
    if (have) snprintf(out, cap, "%s", g_devices);
    plat_unlock_shared(&g_lock);
    return have;
}

int camlib_frame(uint8_t *rgba, size_t cap, uint32_t *serial) {
    if (!plat_load(&g_loaded)) return 0;
    return g_lib.frame(rgba, cap > 0x7fffffff ? 0x7fffffff : (int)cap, serial);
}

int camlib_state(void) {
    if (!plat_load(&g_loaded)) return plat_load(&g_missing) ? CAMLIB_MISSING : CAMLIB_UNLOADED;
    switch (g_lib.status()) {
    case MKWMP_CAMERA_STARTING: return CAMLIB_STARTING;
    case MKWMP_CAMERA_RUNNING: return CAMLIB_RUNNING;
    case MKWMP_CAMERA_FAILED: return CAMLIB_FAILED;
    default: return CAMLIB_IDLE;
    }
}
