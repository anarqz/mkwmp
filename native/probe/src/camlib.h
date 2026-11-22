// The capture library (native/camera: mkwmp-camera.dll / libmkwmp-camera.so), loaded from the mod's folder the first
// time the camera is wanted, and driven from one thread of this file's own: loading it, listing devices (COM) and
// opening a camera can each take a while, and the caller is the game's thread (camera.c). Every function here returns at once and may be called from any thread.
#ifndef MKWMP_CAMLIB_H
#define MKWMP_CAMLIB_H

#include <stddef.h>
#include <stdint.h>

enum {
    CAMLIB_UNLOADED,   // nobody wanted the camera yet
    CAMLIB_MISSING,    // the library is not in the mod's folder, or is another version
    CAMLIB_IDLE,       // loaded, not capturing
    CAMLIB_STARTING,
    CAMLIB_RUNNING,    // pictures arrive
    CAMLIB_FAILED,     // no such device, busy, or unreadable
};

// The folder the library is in (MkwPluginApi.mod_dir, MkwPage.mod_dir); NULL or "": the probe's home.
void camlib_set_dir(const char *dir);
// Capture from this device ("" the first one), mirrored or not, or stop (on 0). width x height RGBA comes out.
void camlib_want(int on, const char *device, int mirror, int width, int height, int fps);
// The game is closing: no capture wanted, and the webcam closed now (not at the worker's next pass).
void camlib_stop_now(void);
// The settings are on screen: load the library and keep the device list fresh for the next seconds.
void camlib_show_devices(void);
// {"devices":[{"id","name"}]} as last listed; 0 while there is none yet.
int camlib_devices_json(char *out, size_t cap);
// The newest picture when its serial is not *serial (see mkwmp_camera_frame); 0 otherwise.
int camlib_frame(uint8_t *rgba, size_t cap, uint32_t *serial);
int camlib_state(void);

#endif
