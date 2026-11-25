/* mkwmp-camera.dll (Linux: libmkwmp-camera.so): the player's webcam for the GameChat camera of MKW 1.8.0
 * (the probe's camera.c shows it above the kart). The probe loads the library from the mod's folder the first time the
 * camera is turned on or its settings are shown, so a broken camera driver cannot stop the game from starting, and
 * drives it through this C ABI.
 *
 * Windows: DirectShow, the one API that lists real webcams and the OBS Virtual Camera alike. Linux: no capture yet
 * (the list is empty and start fails; V4L2 is the plan).
 *
 * Every call returns at once and is safe from any thread, except mkwmp_camera_devices_json, which enumerates through
 * COM on the calling thread: never call it on a game thread. The picture is always handed out as RGBA8 (R, G, B, A
 * bytes, A = 255), top row first, of the size asked in mkwmp_camera_start: the source is cropped around its centre to
 * that aspect ratio and scaled. */
#ifndef MKWMP_CAMERA_H
#define MKWMP_CAMERA_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(MKWMP_CAMERA_BUILD) && defined(_WIN32)
#define MKWMP_CAMERA_API __declspec(dllexport)
#elif defined(MKWMP_CAMERA_BUILD)
#define MKWMP_CAMERA_API __attribute__((visibility("default")))
#else
#define MKWMP_CAMERA_API
#endif

#define MKWMP_CAMERA_ABI 1

typedef void (*mkwmp_camera_log_fn)(const char *line);

enum {
    MKWMP_CAMERA_IDLE = 0,     /* not started, or stopped */
    MKWMP_CAMERA_STARTING = 1, /* opening the device */
    MKWMP_CAMERA_RUNNING = 2,  /* frames arrive */
    MKWMP_CAMERA_FAILED = 3    /* no such device, in use by another program, or no format we can read (logged) */
};

typedef int (*mkwmp_camera_abi_fn)(void);
MKWMP_CAMERA_API int mkwmp_camera_abi(void); /* MKWMP_CAMERA_ABI */
MKWMP_CAMERA_API void mkwmp_camera_init(mkwmp_camera_log_fn log);
/* {"devices":[{"id":"...","name":"..."}]} as this process sees them, UTF-8. The id is the device's name (a second
 * device of the same name gets " (2)", and so on): it is what the player's setting stores. Returns the length, or -1
 * if `cap` is too small. */
MKWMP_CAMERA_API int mkwmp_camera_devices_json(char *buf, int cap);
/* Capture from the device with that id ("" = the first one) and hand frames out as width x height RGBA, at most fps
 * a second, mirrored left-right when mirror is non-zero. Re-callable: another device or size restarts the capture. */
MKWMP_CAMERA_API void mkwmp_camera_start(const char *device_id, int width, int height, int fps, int mirror);
MKWMP_CAMERA_API void mkwmp_camera_set_mirror(int mirror); /* applies to the next frame */
MKWMP_CAMERA_API void mkwmp_camera_stop(void);
/* The newest frame: when its serial differs from *serial, it is copied to rgba (cap bytes, at least width * height
 * * 4), *serial is updated and 1 returned; else 0. Serials are never 0, so *serial = 0 asks for whatever there is. */
MKWMP_CAMERA_API int mkwmp_camera_frame(uint8_t *rgba, int cap, uint32_t *serial);
MKWMP_CAMERA_API int mkwmp_camera_status(void); /* MKWMP_CAMERA_* */

#ifdef __cplusplus
}
#endif

#endif
