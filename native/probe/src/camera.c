#include "camera.h"

#include "camlib.h"
#include "log.h"
#include "plat.h"
#include "rtapi.h"

#include <stdio.h>
#include <string.h>

// ---- pure: the gate, the settings, the room ---------------------------------------------------------------------

const char *camera_unavailable(uint32_t api_version, int has_image_api, const char *game_version, int has_settings,
                               const char *env_switch) {
    if (env_switch && !strcmp(env_switch, "0")) return "MKWMP_CAMERA=0";
    if (!game_version || strcmp(game_version, "1.8.0")) return "the game is not 1.8.0 (older versions have no GameChat camera)";
    if (api_version < CAMERA_API_VERSION || !has_image_api)
        return "this mkw-recomp's plugin API is older than version 3 (no native_image_create): it needs a newer mkw-recomp";
    if (!has_settings) return "this mkw-recomp gives mods no settings";
    return NULL;
}

static int setting_on(int (*get)(const char *, char *, size_t), const char *key) {
    char v[8] = "";
    return get && get(key, v, sizeof v) && !strcmp(v, "1");   // unset, "0" or anything else: off
}

void camera_settings_read(int (*setting_get)(const char *key, char *out, size_t cap), CameraSettings *out) {
    memset(out, 0, sizeof *out);
    out->consent = setting_on(setting_get, "consent18");
    out->camera = setting_on(setting_get, "camera");
    out->view = setting_on(setting_get, "camera_view");
    out->mirror = setting_on(setting_get, "camera_mirror");
    if (!setting_get || !setting_get("camera_device", out->device, sizeof out->device)) out->device[0] = 0;
    out->device[sizeof out->device - 1] = 0;
}

int camera_chatting(const CameraSettings *s, int online) { return online && s->consent && (s->camera || s->view); }

int camera_room_encode(uint8_t out[CAMERA_ROOM_SIZE], uint64_t self, int self_camera, const CameraMember *members, int n) {
    memset(out, 0, CAMERA_ROOM_SIZE);
    memcpy(out + 0x28, &self, 8);
    out[0x30] = self_camera != 0;
    int32_t count = 0;
    int flagged = 0;
    for (int pass = 0; pass < 2; pass++) {   // the cameras first: the game gives its four to the first four it reads
        for (int i = 0; i < n && count < CAMERA_ROOM_MEMBERS; i++) {
            uint64_t account = members[i].account;
            if (!account || account == self) continue;
            if (!pass && (!members[i].camera || flagged >= CAMERA_REMOTE)) continue;
            int seen = 0;
            for (int k = 0; k < count && !seen; k++) seen = !memcmp(out + 0x40 + k * 0x10, &account, 8);
            if (seen) continue;
            uint8_t *e = out + 0x40 + count * 0x10;
            memcpy(e, &account, 8);
            e[8] = !pass;
            flagged += !pass;
            count++;
        }
    }
    memcpy(out + 0x38, &count, 4);
    return count;
}

// ---- pure: the frame cycle ----------------------------------------------------------------------------------------

void camera_cycle_request(CameraCycle *v, int image, uint64_t account, uint64_t handle[2]) {
    v->target = image;
    if (v->account != account) v->delivered = 0;   // another member's pictures: whatever comes first is new
    v->account = account;
    v->pending = 1;
    v->seq++;
    handle[0] = CAMERA_SYNC_MAGIC | (uint64_t)(v->index & 0xff);
    handle[1] = v->seq;
}

int camera_cycle_poll(CameraCycle *v, const uint64_t handle[2], uint32_t source_serial, int *write) {
    *write = -1;
    // a request long gone (the camera was stopped, or given to another member, since): nothing waits on it
    if (camera_sync_index(handle) != v->index || handle[1] != v->seq || !v->pending) return 1;
    if (!source_serial || source_serial == v->delivered) return 0;   // no picture newer than the one shown
    *write = v->target;
    v->delivered = source_serial;
    v->pending = 0;
    return 1;
}

void camera_cycle_stop(CameraCycle *v) {
    v->seq++;
    v->pending = 0;
    v->delivered = 0;
    v->account = 0;
    v->target = -1;
}

// ---- pure: the pictures we make ourselves -------------------------------------------------------------------------

// 5x7 glyphs, a row per byte, bit 4 the left column: A-Z, 0-9, '-', '_' (the characters of an MKWMP name).
static const uint8_t kGlyph[38][7] = {
    {0x0e, 0x11, 0x11, 0x1f, 0x11, 0x11, 0x11}, {0x1e, 0x11, 0x11, 0x1e, 0x11, 0x11, 0x1e}, {0x0e, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0e},
    {0x1e, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1e}, {0x1f, 0x10, 0x10, 0x1e, 0x10, 0x10, 0x1f}, {0x1f, 0x10, 0x10, 0x1e, 0x10, 0x10, 0x10},
    {0x0e, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0f}, {0x11, 0x11, 0x11, 0x1f, 0x11, 0x11, 0x11}, {0x0e, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0e},
    {0x07, 0x02, 0x02, 0x02, 0x02, 0x12, 0x0c}, {0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11}, {0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1f},
    {0x11, 0x1b, 0x15, 0x15, 0x11, 0x11, 0x11}, {0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11}, {0x0e, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0e},
    {0x1e, 0x11, 0x11, 0x1e, 0x10, 0x10, 0x10}, {0x0e, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0d}, {0x1e, 0x11, 0x11, 0x1e, 0x14, 0x12, 0x11},
    {0x0f, 0x10, 0x10, 0x0e, 0x01, 0x01, 0x1e}, {0x1f, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04}, {0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0e},
    {0x11, 0x11, 0x11, 0x11, 0x11, 0x0a, 0x04}, {0x11, 0x11, 0x11, 0x15, 0x15, 0x1b, 0x11}, {0x11, 0x11, 0x0a, 0x04, 0x0a, 0x11, 0x11},
    {0x11, 0x11, 0x0a, 0x04, 0x04, 0x04, 0x04}, {0x1f, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1f},
    {0x0e, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0e}, {0x04, 0x0c, 0x04, 0x04, 0x04, 0x04, 0x0e}, {0x0e, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1f},
    {0x1e, 0x01, 0x01, 0x0e, 0x01, 0x01, 0x1e}, {0x02, 0x06, 0x0a, 0x12, 0x1f, 0x02, 0x02}, {0x1f, 0x10, 0x1e, 0x01, 0x01, 0x11, 0x0e},
    {0x06, 0x08, 0x10, 0x1e, 0x11, 0x11, 0x0e}, {0x1f, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08}, {0x0e, 0x11, 0x11, 0x0e, 0x11, 0x11, 0x0e},
    {0x0e, 0x11, 0x11, 0x0f, 0x01, 0x02, 0x0c},
    {0x00, 0x00, 0x00, 0x1f, 0x00, 0x00, 0x00}, {0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1f},
};

static const uint8_t *glyph(char ch) {
    if (ch >= 'a' && ch <= 'z') ch = (char)(ch - 'a' + 'A');
    if (ch >= 'A' && ch <= 'Z') return kGlyph[ch - 'A'];
    if (ch >= '0' && ch <= '9') return kGlyph[26 + ch - '0'];
    if (ch == '-') return kGlyph[36];
    if (ch == '_') return kGlyph[37];
    return NULL;   // a space
}

static void fill(uint8_t *rgba, int w, int h, int x0, int y0, int x1, int y1, int r, int g, int b) {
    if (x0 < 0) x0 = 0;
    if (y0 < 0) y0 = 0;
    if (x1 > w) x1 = w;
    if (y1 > h) y1 = h;
    for (int y = y0; y < y1; y++) {
        uint8_t *p = rgba + ((size_t)y * (size_t)w + (size_t)x0) * 4;
        for (int x = x0; x < x1; x++, p += 4) p[0] = (uint8_t)r, p[1] = (uint8_t)g, p[2] = (uint8_t)b, p[3] = 255;
    }
}

// name in a dark band across the middle. The game shows the centre square of the picture (the icon is round), so the
// text is sized to fit a width of h.
static void name_band(uint8_t *rgba, int w, int h, const char *name, int band) {
    int len = name ? (int)strlen(name) : 0;
    if (len > 20) len = 20;
    if (!len) return;
    int scale = (h - 16) / (len * 6);
    if (scale > h / 54) scale = h / 54;   // 4 at 216 lines
    if (scale < 1) scale = 1;
    int tw = len * 6 * scale - scale, th = 7 * scale;
    int x0 = (w - tw) / 2, y0 = (h - th) / 2;
    if (band) fill(rgba, w, h, 0, y0 - 2 * scale, w, y0 + th + 2 * scale, 16, 16, 16);
    for (int i = 0; i < len; i++) {
        const uint8_t *g = glyph(name[i]);
        if (!g) continue;
        for (int gy = 0; gy < 7; gy++)
            for (int gx = 0; gx < 5; gx++)
                if (g[gy] & (0x10 >> gx)) {
                    int x = x0 + (i * 6 + gx) * scale, y = y0 + gy * scale;
                    fill(rgba, w, h, x, y, x + scale, y + scale, 255, 255, 255);
                }
    }
}

void camera_pattern(uint8_t *rgba, int w, int h, uint32_t tick, const char *name) {
    static const uint8_t kBar[8][3] = {{191, 191, 191}, {191, 191, 0}, {0, 191, 191}, {0, 191, 0},
                                       {191, 0, 191},   {191, 0, 0},   {0, 0, 191},   {16, 16, 16}};
    if (!rgba || w < 8 || h < 8) return;
    int shift = (int)((tick * 4u) % (uint32_t)w);
    for (int x = 0; x < w; x++) {   // the first row, then copied down
        const uint8_t *c = kBar[((x + shift) % w) * 8 / w];
        uint8_t *p = rgba + (size_t)x * 4;
        p[0] = c[0], p[1] = c[1], p[2] = c[2], p[3] = 255;
    }
    for (int y = 1; y < h; y++) memcpy(rgba + (size_t)y * (size_t)w * 4, rgba, (size_t)w * 4);
    int left = w > h ? (w - h) / 2 : 0;   // the left edge of the square the game shows
    // the corner mark: red in a white frame, top left
    int m = h / 10;
    fill(rgba, w, h, left + 4, 4, left + 4 + m + 4, 4 + m + 4, 255, 255, 255);
    fill(rgba, w, h, left + 6, 6, left + 6 + m, 6 + m, 255, 0, 0);
    // the square bouncing between the top and the bottom, right of the middle
    int size = h / 8, travel = h - size, t = (int)((tick * 3u) % (uint32_t)(2 * travel));
    int sy = t < travel ? t : 2 * travel - t, sx = w / 2 + h / 4;
    fill(rgba, w, h, sx, sy, sx + size, sy + size, 255, 255, 255);
    name_band(rgba, w, h, name, 1);
}

void camera_tile(uint8_t *rgba, int w, int h, const char *name) {
    if (!rgba || w < 1 || h < 1) return;
    for (int y = 0; y < h; y++) {
        int r = 44 - 18 * y / h, g = 48 - 20 * y / h, b = 60 - 24 * y / h;   // a dark slate, lighter at the top
        fill(rgba, w, h, 0, y, w, y + 1, r, g, b);
    }
    name_band(rgba, w, h, name, 0);
}

// ---- the feature ----------------------------------------------------------------------------------------------------

enum { KIND_NONE, KIND_PATTERN, KIND_CAPTURE, KIND_TILE };
enum { PICTURE_BYTES = CAMERA_W * CAMERA_H * 4, CAPTURE_FPS = 15, PATTERN_MS = 66 };
#define LOOPBACK_ACCOUNT 0x4d4b574d504c4f4full   // the member made up when a loopback lobby has nobody else

typedef struct {
    uint64_t type;                     // the game's Local/RemoteCameraVideoType object (0: not initialised)
    int w, h, row, made;               // images made for it (kept across a finalize: images live until exit)
    uint8_t *pixels[CAMERA_IMAGES];
    uint32_t handle[CAMERA_IMAGES];
    CameraCycle cycle;
} Video;

typedef struct {
    uint64_t account;                  // remote sources: whose pictures (0: free)
    uint32_t serial;                   // of the newest picture (0: none)
    uint8_t rgba[PICTURE_BYTES];
} Source;

static CameraHost H;
static int g_installed, g_loopback, g_env_pattern;
static char g_env_members[512];
static GuestFn g_stock_attach, g_stock_trywait;

static PlatLock g_lock = PLAT_LOCK_INIT;   // everything below
static Video g_video[CAMERA_VIDEOS];       // 0 the local camera
static Source g_local, g_remote[CAMERA_REMOTE];
static CameraSettings g_settings;
static int g_chatting, g_room_dirty, g_local_streaming, g_local_kind;
static uint8_t g_room[CAMERA_ROOM_SIZE];
static uint64_t g_event;                   // the room-info-changed event the game attached
static CameraMember g_ext[CAMERA_MEMBERS_MAX];
static int g_ext_n, g_ext_set;
static CameraFrameOut g_out;
static void *g_out_user;
static uint32_t g_tick, g_capture_serial;
static uint64_t g_pattern_at, g_capture_at;
static char g_name[64];

static void bump(Source *s) {
    if (!++s->serial) s->serial = 1;
}

static Source *source_of(uint64_t account) {
    if (!account) return NULL;
    for (int i = 0; i < CAMERA_REMOTE; i++)
        if (g_remote[i].account == account) return &g_remote[i];
    return NULL;
}

// Nearest-neighbour copy of a sw x sh picture (rows sw * 4 apart) over a dw x dh one (rows drow apart).
static void blit(uint8_t *dst, int dw, int dh, int drow, const uint8_t *src, int sw, int sh) {
    if (dw == sw && dh == sh) {
        for (int y = 0; y < dh; y++) memcpy(dst + (size_t)y * (size_t)drow, src + (size_t)y * (size_t)sw * 4, (size_t)sw * 4);
        return;
    }
    for (int y = 0; y < dh; y++) {
        const uint8_t *row = src + (size_t)(y * sh / dh) * (size_t)sw * 4;
        uint8_t *out = dst + (size_t)y * (size_t)drow;
        for (int x = 0; x < dw; x++) memcpy(out + (size_t)x * 4, row + (size_t)(x * sw / dw) * 4, 4);
    }
}

static void video_write(Video *v, int image, const uint8_t *rgba) {
    if (image < 0 || image >= v->made) return;
    blit(v->pixels[image], v->w, v->h, v->row, rgba, CAMERA_W, CAMERA_H);
    H.native_image_touch(v->handle[image]);
}

// Every image of a camera shows the tile: at set-up and when its stream stops, so the next member it is given to
// does not start with the last one's face.
static void video_blank(Video *v) {
    static uint8_t tile[PICTURE_BYTES];   // under g_lock
    camera_tile(tile, CAMERA_W, CAMERA_H, v == &g_video[0] ? g_name : NULL);
    for (int i = 0; i < v->made; i++) video_write(v, i, tile);
}

static Video *video_find(uint64_t type) {
    if (!type) return NULL;
    for (int i = 0; i < CAMERA_VIDEOS; i++)
        if (g_video[i].type == type) return &g_video[i];
    return NULL;
}

// The local camera's newest picture into g_local, on the game's thread, under g_lock. 1 when there is a new one.
static int local_picture(void) {
    uint64_t now = plat_ms();
    int fresh = 0;
    if (g_env_pattern || !strcmp(g_settings.device, CAMERA_DEVICE_TEST)) {
        if (g_local_kind != KIND_PATTERN || now - g_pattern_at >= PATTERN_MS) {
            g_pattern_at = now;
            camera_pattern(g_local.rgba, CAMERA_W, CAMERA_H, g_tick++, g_name);
            g_local_kind = KIND_PATTERN;
            fresh = 1;
        }
    } else if (camlib_frame(g_local.rgba, sizeof g_local.rgba, &g_capture_serial)) {
        g_capture_at = now;
        g_local_kind = KIND_CAPTURE;
        fresh = 1;
    } else if (g_local_kind != KIND_TILE && (g_local_kind != KIND_CAPTURE || now - g_capture_at > 2000)) {
        camera_tile(g_local.rgba, CAMERA_W, CAMERA_H, g_name);   // no capture (yet, or any more): the name
        g_local_kind = KIND_TILE;
        fresh = 1;
    }
    if (!fresh) return 0;
    bump(&g_local);
    if (g_loopback)
        for (int i = 0; i < CAMERA_REMOTE; i++)
            if (g_remote[i].account) {
                memcpy(g_remote[i].rgba, g_local.rgba, PICTURE_BYTES);
                bump(&g_remote[i]);
            }
    return 1;
}

// ---- nn::chat: the session

static void ov_is_chatting(Cpu *c) {
    static uint64_t signal;
    uint64_t event = 0;
    int send = 0;
    plat_lock(&g_lock);
    int chatting = g_chatting;
    if (chatting && g_room_dirty && g_event) {
        g_room_dirty = 0;
        event = g_event;
    }
    if (chatting && g_settings.camera) send = local_picture() && g_local_streaming;
    else g_local_kind = KIND_NONE;
    CameraFrameOut out = g_out;
    void *user = g_out_user;
    plat_unlock(&g_lock);
    if (event) {   // the game reads the room again (members, camera flags) when its event is signalled
        if (!signal && g_rt.guest_symbol) signal = g_rt.guest_symbol("_ZN2nn2os17SignalSystemEventEPNS0_15SystemEventTypeE");
        if (signal && g_rt.call) g_rt.call(c, signal, 1, event);
    }
    // g_local is written on this thread only, so it is read here without the lock (out may call camera_* back)
    if (send && out) out(g_local.rgba, CAMERA_W, CAMERA_H, plat_ms(), user);
    ret_x(c, (uint64_t)chatting);
}

static void ov_attach_room_event(Cpu *c) {
    uint64_t event = arg_x(c, 0);
    if (g_stock_attach) g_stock_attach(c);   // the runtime's creates the event
    else ret_x(c, 0);
    plat_lock(&g_lock);
    g_event = g_stock_attach ? event : 0;
    plat_unlock(&g_lock);
    mkwmp_log("camera: the game attached its room event (%#llx)", (unsigned long long)event);
}

static void ov_get_room_info(Cpu *c) {
    uint8_t *out = arg_p(c, 0);
    if (out) {
        plat_lock_shared(&g_lock);
        memcpy(out, g_room, CAMERA_ROOM_SIZE);
        plat_unlock_shared(&g_lock);
    }
    ret_x(c, 0);
}

static void ov_set_sharing_key(Cpu *c) {
    static char last[0x28];
    const char *key = arg_p(c, 0);
    char text[0x28] = {0};
    for (int i = 0; key && i < 0x27 && key[i]; i++) text[i] = key[i] >= 0x20 && key[i] < 0x7f ? key[i] : '?';
    plat_lock(&g_lock);
    int changed = strcmp(last, text) != 0;
    memcpy(last, text, sizeof last);
    plat_unlock(&g_lock);
    if (changed) mkwmp_log("camera: sharing key \"%s\" (the game's session number: the same for players racing together)", text);
    ret_x(c, 0);
}
static void ov_clear_sharing_key(Cpu *c) { ret_x(c, 0); }

// nn::oe::{Disable,Enable}{ScreenShot,Recording}, {Begin,End}PresentPhotographedContents (the 6 nn::oe imports of 1.8.0 the runtime has no handler for): the game blocks screenshots while it shows players' cameras (after SetSharingKey).
// The SDK's versions call the applet service, which this runtime does not have: the first two-player camera run
// crashed reading address 0 in DisableScreenShot (sdk+0x590340) right after the sharing key. Nothing to block here.
static void ov_screenshot(Cpu *c) { ret_x(c, 0); }

// ---- nn::chat: the pictures

// The game allocates what this says and hands it to Initialize*CameraVideoStorage; the pictures live in the runtime's
// memory (native_image_create), so one page is all we ask of the game's heap, as the runtime's own stub does.
static void ov_buffer_size(Cpu *c) { ret_x(c, 0x1000); }

static void storage_init(Cpu *c, int local) {
    uint64_t type = arg_x(c, 0);
    int w = (int)(int32_t)arg_x(c, 1), h = (int)(int32_t)arg_x(c, 2), count = (int)(int32_t)arg_x(c, 3);
    if (count > CAMERA_IMAGES) count = CAMERA_IMAGES;
    plat_lock(&g_lock);
    Video *v = video_find(type);
    if (!v && local) v = &g_video[0];
    for (int i = 1; !v && i < CAMERA_VIDEOS; i++)
        if (!g_video[i].type) v = &g_video[i];
    if (v && type && w > 0 && h > 0) {
        if (v->made && (v->w != w || v->h != h)) v->made = 0;   // another size: new images (never seen from the game)
        v->type = type, v->w = w, v->h = h;
        while (v->made < count) {
            void *pixels = NULL;
            int row = 0;
            uint32_t handle = H.native_image_create(w, h, &pixels, &row);
            if (!handle || !pixels || row < w * 4 || (v->made && row != v->row)) {
                mkwmp_log("camera: the runtime refused image %d of camera %d (%dx%d)", v->made, (int)(v - g_video), w, h);
                break;
            }
            v->row = row;
            v->pixels[v->made] = pixels;
            v->handle[v->made++] = handle;
        }
        camera_cycle_stop(&v->cycle);
        v->cycle.index = (int)(v - g_video);
        video_blank(v);
        mkwmp_log("camera: %s camera %d: %d images of %dx%d (rows %d bytes apart)", local ? "local" : "remote",
                  v->cycle.index, v->made, w, h, v->row);
    } else {
        mkwmp_log("camera: no room for a %s camera of %dx%d x %d", local ? "local" : "remote", w, h, count);
    }
    plat_unlock(&g_lock);
    ret_x(c, 0);
}
static void ov_init_local(Cpu *c) { storage_init(c, 1); }
static void ov_init_remote(Cpu *c) { storage_init(c, 0); }

static void ov_image_handle(Cpu *c) {
    uint64_t type = arg_x(c, 0);
    int i = (int)(int32_t)arg_x(c, 1);
    plat_lock_shared(&g_lock);
    Video *v = video_find(type);
    // without an image of ours: the runtime's own kind of handle (width << 16 | height), a picture nobody fills
    uint32_t handle = v && i >= 0 && i < v->made ? v->handle[i] : (uint32_t)((v ? v->w : CAMERA_W) << 16 | (v ? v->h : CAMERA_H));
    plat_unlock_shared(&g_lock);
    ret_x(c, handle);
}

static void ov_finalize(Cpu *c) {
    plat_lock(&g_lock);
    Video *v = video_find(arg_x(c, 0));
    if (v) {
        camera_cycle_stop(&v->cycle);
        v->type = 0;   // its images stay, for the next camera of that size
    }
    plat_unlock(&g_lock);
    ret_x(c, 0);
}

static void ov_start_local(Cpu *c) {
    plat_lock(&g_lock);
    g_local_streaming = 1;
    plat_unlock(&g_lock);
    mkwmp_log("camera: the game started the local camera");
    ret_x(c, 0);
}

static void ov_stop_local(Cpu *c) {
    plat_lock(&g_lock);
    int was = g_local_streaming;
    g_local_streaming = 0;
    camera_cycle_stop(&g_video[0].cycle);
    g_video[0].cycle.index = 0;
    video_blank(&g_video[0]);
    plat_unlock(&g_lock);
    if (was) mkwmp_log("camera: the game stopped the local camera");
    ret_x(c, 0);
}

static void ov_start_remote(Cpu *c) {
    const uint64_t *id = arg_p(c, 0);
    uint64_t account = id ? *id : 0;
    plat_lock(&g_lock);
    Source *s = source_of(account);
    for (int i = 0; !s && account && i < CAMERA_REMOTE; i++)
        if (!g_remote[i].account) {
            s = &g_remote[i];
            s->account = account;
            s->serial = 0;
            if (g_loopback && g_local.serial) {
                memcpy(s->rgba, g_local.rgba, PICTURE_BYTES);
                bump(s);
            }
        }
    plat_unlock(&g_lock);
    // the id the game holds for the member: the lobby's account id of that player, if the two agree (live test 1)
    mkwmp_log("camera: the game asks for the camera of account %#llx%s", (unsigned long long)account, s ? "" : ": no stream free");
    ret_x(c, s ? 0 : 1);   // not started: the game asks again next frame
}

static void ov_stop_remote(Cpu *c) {
    const uint64_t *id = arg_p(c, 0);
    uint64_t account = id ? *id : 0;
    plat_lock(&g_lock);
    Source *s = source_of(account);
    if (s) s->account = 0, s->serial = 0;
    for (int i = 1; account && i < CAMERA_VIDEOS; i++)
        if (g_video[i].cycle.account == account) {
            camera_cycle_stop(&g_video[i].cycle);
            video_blank(&g_video[i]);
        }
    plat_unlock(&g_lock);
    if (s) mkwmp_log("camera: the game stopped the camera of account %#llx", (unsigned long long)account);
    ret_x(c, 0);
}

static void request_next(Cpu *c, int local) {
    uint64_t *sync = arg_p(c, 0);
    int image = (int)(int32_t)arg_x(c, 2);
    const uint64_t *id = local ? NULL : arg_p(c, 5);
    plat_lock(&g_lock);
    Video *v = video_find(arg_x(c, 1));
    if (sync) {
        sync[0] = sync[1] = 0;   // not one of ours: the runtime's poll answers done, the picture stays as it is
        if (v && image >= 0 && image < v->made) camera_cycle_request(&v->cycle, image, id ? *id : 0, sync);
    }
    plat_unlock(&g_lock);
    ret_x(c, 0);
}
static void ov_request_local(Cpu *c) { request_next(c, 1); }
static void ov_request_remote(Cpu *c) { request_next(c, 0); }

// nn::gs::TryWaitNativeSync: the game polls the sync of the image it asked for; the movie decoder polls its own.
static void ov_try_wait_sync(Cpu *c) {
    const uint64_t *handle = arg_p(c, 0);
    if (!handle || !camera_sync_ours(handle)) {
        if (g_stock_trywait) g_stock_trywait(c);
        else ret_x(c, 1);
        return;
    }
    int index = camera_sync_index(handle), done = 1;
    if (index < CAMERA_VIDEOS) {
        plat_lock(&g_lock);
        Video *v = &g_video[index];
        const Source *s = index ? source_of(v->cycle.account) : &g_local;
        int write;
        done = camera_cycle_poll(&v->cycle, handle, s ? s->serial : 0, &write);
        if (write >= 0) video_write(v, write, s->rgba);   // the image asked for: not the one on screen
        plat_unlock(&g_lock);
    }
    ret_x(c, (uint64_t)done);
}

// ---- the camera's thread: settings, members, the room, the capture wanted

void camera_refresh(void) {
    if (!g_installed) return;
    CameraSettings s;
    camera_settings_read(H.setting_get, &s);
    int online = H.online ? H.online() : 0;
    uint64_t self = H.self_account ? H.self_account() : 0;
    const char *name = H.self_name ? H.self_name() : NULL;
    int chatting = camera_chatting(&s, online);

    CameraMember m[CAMERA_MEMBERS_MAX + 1];
    int n = 0;
    plat_lock_shared(&g_lock);
    int ext = g_ext_set;
    if (ext) {
        n = g_ext_n;
        memcpy(m, g_ext, (size_t)n * sizeof m[0]);
    }
    plat_unlock_shared(&g_lock);
    if (!ext && H.peers) {   // before the relay speaks video: the voice room, cameras unknown
        uint64_t accounts[CAMERA_MEMBERS_MAX];
        int k = H.peers(accounts, CAMERA_MEMBERS_MAX);
        for (int i = 0; i < k && n < CAMERA_MEMBERS_MAX; i++) m[n].account = accounts[i], m[n++].camera = 0;
    }
    if (g_env_members[0] && H.account_of) {
        char names[sizeof g_env_members];
        memcpy(names, g_env_members, sizeof names);
        for (char *t = strtok(names, ", "); t && n < CAMERA_MEMBERS_MAX; t = strtok(NULL, ", "))
            m[n].account = H.account_of(t), m[n++].camera = 1;
    }
    int others = 0;
    for (int i = 0; i < n; i++) {
        others += m[i].account && m[i].account != self;
        if (g_loopback) m[i].camera = 1;
        else if (!s.view) m[i].camera = 0;                       // the player does not want to see cameras
        if (H.muted && H.muted(m[i].account)) m[i].camera = 0;   // a muted player's face is hidden too
    }
    if (g_loopback && !others) m[n].account = LOOPBACK_ACCOUNT, m[n++].camera = 1;

    uint8_t room[CAMERA_ROOM_SIZE];
    int listed = camera_room_encode(room, self, chatting && s.camera, chatting ? m : NULL, chatting ? n : 0);
    plat_lock(&g_lock);
    int was = g_chatting, changed = memcmp(room, g_room, sizeof room) != 0;
    g_settings = s;
    g_chatting = chatting;
    snprintf(g_name, sizeof g_name, "%s", name ? name : "");
    if (changed) {
        memcpy(g_room, room, sizeof room);
        g_room_dirty = 1;
    }
    int capture = chatting && s.camera && g_local_streaming && !g_env_pattern && strcmp(s.device, CAMERA_DEVICE_TEST);
    plat_unlock(&g_lock);
    camlib_want(capture, s.device, s.mirror, CAMERA_W, CAMERA_H, CAPTURE_FPS);

    if (was != chatting)
        mkwmp_log("camera: chat %s (online %d, consent %d, camera %d, view %d%s)", chatting ? "on" : "off", online, s.consent,
                  s.camera, s.view, g_loopback ? ", loopback" : "");
    if (changed && chatting) {
        int cameras = 0;
        for (int i = 0; i < listed; i++) cameras += room[0x40 + i * 0x10 + 8];
        mkwmp_log("camera: room: account %#llx camera %s, %d members, %d with a camera", (unsigned long long)self,
                  s.camera ? "on" : "off", listed, cameras);
    }
}

static void worker(void *unused) {
    (void)unused;
    for (;;) {
        camera_refresh();
        plat_sleep_ms(500);
    }
}

// ---- install

int camera_install(const CameraHost *host) {
    static const struct { const char *name; GuestFn fn; } kOverrides[CAMERA_OVERRIDES] = {
        {"_ZN2nn4chat10IsChattingEv", ov_is_chatting},
        {"_ZN2nn4chat28AttachToRoomInfoChangedEventEPNS_2os11SystemEventE", ov_attach_room_event},
        {"_ZN2nn4chat11GetRoomInfoEPNS0_8RoomInfoE", ov_get_room_info},
        {"_ZN2nn4chat13SetSharingKeyERKNS0_10SharingKeyE", ov_set_sharing_key},
        {"_ZN2nn4chat15ClearSharingKeyEv", ov_clear_sharing_key},
        {"_ZN2nn4chat35CalculateLocalCameraVideoBufferSizeEiii", ov_buffer_size},
        {"_ZN2nn4chat36CalculateRemoteCameraVideoBufferSizeEiii", ov_buffer_size},
        {"_ZN2nn4chat33InitializeLocalCameraVideoStorageEPNS0_20LocalCameraVideoTypeEiiiPvm", ov_init_local},
        {"_ZN2nn4chat34InitializeRemoteCameraVideoStorageEPNS0_21RemoteCameraVideoTypeEiiiPvm", ov_init_remote},
        {"_ZN2nn4chat25GetLocalCameraImageHandleEPKNS0_20LocalCameraVideoTypeEi", ov_image_handle},
        {"_ZN2nn4chat26GetRemoteCameraImageHandleEPKNS0_21RemoteCameraVideoTypeEi", ov_image_handle},
        {"_ZN2nn4chat31FinalizeLocalCameraVideoStorageEPNS0_20LocalCameraVideoTypeE", ov_finalize},
        {"_ZN2nn4chat32FinalizeRemoteCameraVideoStorageEPNS0_21RemoteCameraVideoTypeE", ov_finalize},
        {"_ZN2nn4chat25StartLocalCameraStreamingEv", ov_start_local},
        {"_ZN2nn4chat24StopLocalCameraStreamingEv", ov_stop_local},
        {"_ZN2nn4chat26StartRemoteCameraStreamingERKNS_7account23NetworkServiceAccountIdE", ov_start_remote},
        {"_ZN2nn4chat25StopRemoteCameraStreamingERKNS_7account23NetworkServiceAccountIdE", ov_stop_remote},
        {"_ZN2nn4chat27RequestNextLocalCameraImageEPNS_2gs16NativeSyncHandleEPNS0_20LocalCameraVideoTypeEiS2_", ov_request_local},
        {"_ZN2nn4chat28RequestNextRemoteCameraImageEPNS_2gs16NativeSyncHandleEPNS0_21RemoteCameraVideoTypeEiS2_RKNS_7account23NetworkServiceAccountIdE",
         ov_request_remote},
        {"_ZN2nn2oe17DisableScreenShotEv", ov_screenshot},
        {"_ZN2nn2oe16EnableScreenShotEv", ov_screenshot},
        {"_ZN2nn2oe16DisableRecordingEv", ov_screenshot},
        {"_ZN2nn2oe15EnableRecordingEv", ov_screenshot},
        {"_ZN2nn2oe32BeginPresentPhotographedContentsEv", ov_screenshot},
        {"_ZN2nn2oe30EndPresentPhotographedContentsEv", ov_screenshot},
        {"_ZN2nn2gs17TryWaitNativeSyncERKNS0_16NativeSyncHandleE", ov_try_wait_sync},   // last: chained to stock
    };
    char env[8] = "";
    int has_env = plat_getenv("MKWMP_CAMERA", env, sizeof env);
    const char *why = camera_unavailable(host->api_version, host->native_image_create && host->native_image_touch,
                                         host->game_version, host->setting_get != NULL, has_env ? env : NULL);
    if (!why && (!g_rt.override || (g_rt.guest_modules && g_rt.guest_modules()))) why = "the game is already loaded";
    if (why) {
        mkwmp_log("camera: off: %s (plugin API %u, game %s)", why, (unsigned)host->api_version,
                  host->game_version ? host->game_version : "?");
        return 0;
    }
    H = *host;
    char v[8] = "";
    g_loopback = plat_getenv("MKWMP_CAMERA_LOOPBACK", v, sizeof v) && !strcmp(v, "1");
    g_env_pattern = plat_getenv("MKWMP_CAMERA_PATTERN", v, sizeof v) && !strcmp(v, "1");
    if (!plat_getenv("MKWMP_CAMERA_MEMBERS", g_env_members, sizeof g_env_members)) g_env_members[0] = 0;
    for (int i = 0; i < CAMERA_VIDEOS; i++) {
        memset(&g_video[i], 0, sizeof g_video[i]);
        g_video[i].cycle.index = i;
        g_video[i].cycle.target = -1;
    }
    camlib_set_dir(host->mod_dir);
    g_stock_attach = g_rt.stock ? g_rt.stock(kOverrides[1].name) : NULL;
    g_stock_trywait = g_rt.stock ? g_rt.stock(kOverrides[CAMERA_OVERRIDES - 1].name) : NULL;
    for (int i = 0; i < CAMERA_OVERRIDES; i++) g_rt.override(kOverrides[i].name, kOverrides[i].fn);
    g_installed = 1;
    mkwmp_log("camera: %d overrides (plugin API %u)%s%s%s; off until the player turns it on in the Camera tab", CAMERA_OVERRIDES,
              (unsigned)host->api_version, g_loopback ? ", loopback" : "", g_env_pattern ? ", test pattern" : "",
              g_stock_attach && g_stock_trywait ? "" : ", the runtime has no stock handler to chain to");
    if (!host->no_thread && !plat_thread_start(worker, NULL)) mkwmp_log("camera: no thread: the camera stays off");
    return CAMERA_OVERRIDES;
}

// ---- phase 4's side

void camera_set_frame_out(CameraFrameOut fn, void *user) {
    plat_lock(&g_lock);
    g_out = fn;
    g_out_user = user;
    plat_unlock(&g_lock);
}

int camera_frame_in(uint64_t account, const uint8_t *rgba, int w, int h) {
    if (!rgba || w < 1 || h < 1 || w > 4096 || h > 4096) return 0;
    plat_lock(&g_lock);
    Source *s = source_of(account);
    if (s) {
        blit(s->rgba, CAMERA_W, CAMERA_H, CAMERA_W * 4, rgba, w, h);
        bump(s);
    }
    plat_unlock(&g_lock);
    return s != NULL;
}

void camera_members_set(const CameraMember *members, int n) {
    if (n < 0 || !members) n = 0;
    if (n > CAMERA_MEMBERS_MAX) n = CAMERA_MEMBERS_MAX;
    plat_lock(&g_lock);
    memcpy(g_ext, members, (size_t)n * sizeof g_ext[0]);
    g_ext_n = n;
    g_ext_set = 1;
    plat_unlock(&g_lock);
}

int camera_subscriptions(uint64_t *accounts, int cap) {
    int n = 0;
    plat_lock_shared(&g_lock);
    for (int i = 0; i < CAMERA_REMOTE && n < cap; i++)
        if (g_remote[i].account) accounts[n++] = g_remote[i].account;
    plat_unlock_shared(&g_lock);
    return n;
}

int camera_local_streaming(void) {
    plat_lock_shared(&g_lock);
    int on = g_local_streaming && g_chatting && g_settings.camera;
    plat_unlock_shared(&g_lock);
    return on;
}
