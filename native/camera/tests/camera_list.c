// Lists the cameras the library sees, through the built library itself (read-only: no device is opened).
//
//   python native/camera/build.py --list
// or by hand (Windows):
//   zig cc -O2 -Wall -Wextra -Werror -Inative/camera/src -o camera_list.exe native/camera/tests/camera_list.c
//   camera_list.exe native\camera\build\mkwmp-camera.dll
#include "mkwmp_camera.h"

#include <stdio.h>

#ifdef _WIN32
#include <windows.h>
static void *lib_open(const char *path) { return LoadLibraryA(path); }
static void *lib_sym(void *lib, const char *name) { return (void *)(uintptr_t)GetProcAddress((HMODULE)lib, name); }
#else
#include <dlfcn.h>
static void *lib_open(const char *path) { return dlopen(path, RTLD_NOW); }
static void *lib_sym(void *lib, const char *name) { return dlsym(lib, name); }
#endif

static void log_line(const char *line) { printf("%s\n", line); }

int main(int argc, char **argv) {
    if (argc != 2) { fprintf(stderr, "usage: %s <mkwmp-camera library>\n", argv[0]); return 2; }
    void *lib = lib_open(argv[1]);
    if (!lib) { fprintf(stderr, "cannot load %s\n", argv[1]); return 1; }
    int (*abi)(void) = (int (*)(void))lib_sym(lib, "mkwmp_camera_abi");
    void (*init)(mkwmp_camera_log_fn) = (void (*)(mkwmp_camera_log_fn))lib_sym(lib, "mkwmp_camera_init");
    int (*devices)(char *, int) = (int (*)(char *, int))lib_sym(lib, "mkwmp_camera_devices_json");
    int (*status)(void) = (int (*)(void))lib_sym(lib, "mkwmp_camera_status");
    if (!abi || !init || !devices || !status || !lib_sym(lib, "mkwmp_camera_start") || !lib_sym(lib, "mkwmp_camera_stop") ||
        !lib_sym(lib, "mkwmp_camera_frame") || !lib_sym(lib, "mkwmp_camera_set_mirror")) {
        fprintf(stderr, "an export is missing\n");
        return 1;
    }
    if (abi() != MKWMP_CAMERA_ABI) { fprintf(stderr, "ABI %d, want %d\n", abi(), MKWMP_CAMERA_ABI); return 1; }
    init(log_line);
    static char json[16384];
    int n = devices(json, sizeof json);
    if (n < 0) { fprintf(stderr, "the device list does not fit\n"); return 1; }
    char tiny[4];
    if (devices(tiny, sizeof tiny) != -1) { fprintf(stderr, "a short buffer was not refused\n"); return 1; }
    if (status() != MKWMP_CAMERA_IDLE) { fprintf(stderr, "not idle\n"); return 1; }
    printf("%s\n", json);
    return 0;
}
