/* mkwmp_camera.h where there is no capture yet (Linux; V4L2 is the plan: /dev/video* with VIDIOC_QUERYCAP, YUYV or
 * MJPG, mmap streaming, which also lists OBS's v4l2loopback device). The device list is empty and a start fails, so
 * the probe shows nothing (the game's images stay empty), or the test pattern when that is the device chosen. */
#include "mkwmp_camera.h"

#include <stdio.h>

static mkwmp_camera_log_fn g_log;
static int g_status = MKWMP_CAMERA_IDLE;

MKWMP_CAMERA_API int mkwmp_camera_abi(void) { return MKWMP_CAMERA_ABI; }
MKWMP_CAMERA_API void mkwmp_camera_init(mkwmp_camera_log_fn log) { g_log = log; }

MKWMP_CAMERA_API int mkwmp_camera_devices_json(char *buf, int cap) {
    int n = snprintf(buf, cap > 0 ? (size_t)cap : 0, "{\"devices\":[]}");
    return n < cap ? n : -1;
}

MKWMP_CAMERA_API void mkwmp_camera_start(const char *device_id, int width, int height, int fps, int mirror) {
    (void)device_id, (void)width, (void)height, (void)fps, (void)mirror;
    if (g_log && g_status != MKWMP_CAMERA_FAILED) g_log("camera: this build has no capture on this system");
    g_status = MKWMP_CAMERA_FAILED;
}

MKWMP_CAMERA_API void mkwmp_camera_set_mirror(int mirror) { (void)mirror; }
MKWMP_CAMERA_API void mkwmp_camera_stop(void) { g_status = MKWMP_CAMERA_IDLE; }

MKWMP_CAMERA_API int mkwmp_camera_frame(uint8_t *rgba, int cap, uint32_t *serial) {
    (void)rgba, (void)cap, (void)serial;
    return 0;
}

MKWMP_CAMERA_API int mkwmp_camera_status(void) { return g_status; }
