#include "camera.h"

#include "camcodec.h"
#include "camlib.h"
#include "gameaddrs.h"
#include "guesthook.h"
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

int camera_in_room(uint64_t room_id, int prompt) { return room_id != 0 || prompt; }

int camera_hold(CameraHold *h, int online, uint64_t now) {
    if (!h->at) return 0;
    if (online) {
        h->was_online = 1;
        return 1;
    }
    // left online play, or never got there (a clock read before `at` was taken is not "long ago")
    if (h->was_online || (now > h->at && now - h->at > CAMERA_HOLD_MS)) {
        h->at = 0, h->was_online = 0;
        return 0;
    }
    return 1;
}

int camera_declined(int prompted, int online, int face_flag) { return prompted && online && !face_flag; }

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
static CameraFrameOut g_out;
static void *g_out_user;
static CameraJpegOut g_jpeg_out;           // the transport's: every frame to send, encoded (camcodec.h)
static void *g_jpeg_user;
static int g_divisor = 1, g_loop_codec;    // the send divisor asked; loopback through the codec (MKWMP_CAMERA_CODEC=1)
static CamEncoder g_enc, g_loop_enc;       // the game's thread only
static uint8_t g_jpeg[CAMCODEC_FRAME_MAX], g_loop_rgba[PICTURE_BYTES];   // the game's thread only
static uint32_t g_tick, g_capture_serial;
static uint64_t g_pattern_at, g_capture_at;
static char g_name[64];
static int g_key_cleared;                  // ClearSharingKey calls (the next SetSharingKey is logged again)
static CameraHold g_hold;                  // the chat held on for the game's camera prompt (camera_hold)
static int g_prompted;                     // the game's prompt opened at the last Online Play (camera_declined)
static int g_face;                         // the game's flag as setup_watch read it last: Camera On was chosen
static int g_declined;                     // Camera Off was: the local camera is off until the next Online Play
static int g_camera_on;                    // `camera` on and not declined: the local camera is captured, shown and sent
static const struct game_addrs *g_setup;   // the build's row once the game's camera set-up is hooked (set once)
static uint64_t g_setup_original;          // the update the hook stands in front of

static void bump(Source *s) {
    if (!++s->serial) s->serial = 1;
}

// ---- the trace (MKWMP_CAMERA_TRACE=1): what the game asks and what it does with the answers ------------------------
// Three parts, all bounded: the first TRACE_CALLS calls of every override with their arguments; once a second while a
// chat runs, each camera's polls / requests / pictures written since the last line (only when the numbers changed); and
// the game's own state, read from its memory on its thread and logged when it changes (trace_game below).
enum { TRACE_CALLS = 12, TRACE_SUMMARIES = 90, TRACE_STATE_LINES = 400, TRACE_SLOTS = CAMERA_VIDEOS + 3 };
enum {
    T_ROOM, T_SIGNAL, T_KEY, T_SIZE, T_HANDLE, T_FINALIZE, T_START, T_STOP, T_OE,
    T_REQUEST = T_OE + 6,                // per camera from here on
    T_POLL = T_REQUEST + CAMERA_VIDEOS,
    T_WRITE = T_POLL + CAMERA_VIDEOS,
    T_FOREIGN = T_WRITE + CAMERA_VIDEOS, // the game's poll of a handle that is not ours (a camera's first one is zero)
    T_SETUP,                             // the game's camera set-up node, every update the hook sees
    T_N,
};
typedef struct { uint32_t requests, unknown, polls, done, writes, refused; } TraceCount;
static int g_trace;
static uint32_t g_trace_n[T_N];             // calls logged so far (the game's threads; a lost count costs one line)
static TraceCount g_count[CAMERA_VIDEOS];   // under g_lock: since the last summary

static int tr(int what) {
    if (!g_trace || g_trace_n[what] >= TRACE_CALLS) return 0;
    g_trace_n[what]++;
    return 1;
}

// The game's side of the pictures, from 1.8.0's main (research/docs/CAMERA-RE.md 1.3 and 2.1; the screen guard and the
// texture ids were read for this trace, 19/11/2026): read-only, and only when the code the offsets were read from is
// the code in memory. What decides whether a picture reaches the screen, in the game's order:
//   1. the room lists the account with a camera -> the game gives it a video object and starts its stream;
//   2. the first poll of image 0's zero sync answers done -> "shows image 0", then our syncs cycle the three images;
//   3. mode 2 (0x24342e0: chatting, the online manager's u32 0, a session number; it sets the sharing key) makes
//      GameChat want cameras shown; the screen guard calls nn::oe::DisableScreenShot and
//      BeginPresentPhotographedContents, waits ten frames, and from then on hands the pictures to the UI
//      (0x1c8cd0 returns early while S+0xc or S+0x14 is set: every camera then has no picture);
//   4. the UI's lookup by player (0x7fe880) gives a picture only for a player index entered in T's table of 26 with
//      kind 1 or 2, whose account is in T with "has one". The local player is entered by the scene update (0x25a090)
//      and a remote one when its kart is made (0x11f734), which needs the sharing key set by then and the face
//      rectangle among that player's parameters (hash 0xfe0f1639). A player sends that rectangle (0x1235c8) only with
//      its own sharing key set and F's bit 11, which is set when the player answers the game's "Use the camera?" prompt
//      with Camera On (OnlineCameraSetup.module's AIGlobalSeqSetSequenceFlag2, flag 11 DisplayFaceIconMyself; see "the
//      game's own camera set-up" below): a player who did not go through the prompt has no face anywhere;
//   5. what it draws is the texture id of the shown image (V's +0x68), which the game got at boot by making its own
//      texture over the memory pool of the native image's texture (0xd31e60, 0xaa1b0: no pool, no id).
//   V = a *VideoType object - 0xa30 (the game's video object): +0xa00 current image (-1: none shown yet), +0xa04 next,
//       +0xa08 the last poll's answer, +0xa18 assigned to a member, +0xa19 stream started, +0xa20 the member's account;
//       image i's texture as the UI takes it at V + 8 + i * 0x350 + 0x280: +0x10 the memory pool the game found behind
//       the native image's texture (0: none, and then it has no texture id), +0x68 its id in the game's texture table
//       (-1: none), which is all the UI copies (0x2fd7c0).
//   M = the local V - 0x38 (the chat manager): +0x3828 chatting, +0x3829 the sharing key is set.
//   S = [main+0x37b3858] + 0xd944, the screen guard (update 0x6355f0): +0 who wants cameras shown (bit 0 GameChat),
//       +4 who forbids it, +0xc 1 while screenshots are allowed and cameras are NOT handed to the UI, +0x10 / +0x14 the
//       ten frames it waits after nn::oe::DisableScreenShot before the first picture.
//   T = [main+0x37b84c8] + 0x17b8, what the UI draws (filled each frame by 0x1c8cd0 when S allows): +0x278 mode
//       (2 GameChat), +0x2b8 the local picture (+8 texture id) with +0x2d8 "has one"; four of { +0 account, +8 valid,
//       +0x18 texture id, +0x30 has one } from +0x2e0, 0x38 apart; +0x290 the id the game puts everywhere first;
//       +8 the players a face may be drawn for: 26 of { 16 bytes the face rectangle, +0x10 kind (0 the console's
//       camera, 1 GameChat whole picture, 2 GameChat with a rectangle), +0x14 entered }, 0x18 apart.
//   F = [main+0x37b3858] + 0x12c, flags: bit 11 the player chose Camera On in the game's prompt, bit 12 the camera
//       is wanted in this scene (0x1cb64f8); +0x120's bit 3 goes with bit 12 in the scene update (0x25a184).
//   The inputs of mode 2: the u32 at [[main+0x37c9828]+0xd8]+8 (0xd956e0: must be 0) and the session number, the u64
//       at [[main+0x37be190]+0x48]+0x170 (0x258f340: must not be 0; its decimal text is the sharing key).
#define GAME_MAIN(off) (0x7000000000ull + (uint64_t)(off))
static int peek(uint64_t addr, void *out, size_t n) {
    memset(out, 0, n);
    if (plat_mem_readable((const void *)(uintptr_t)addr) < n) return 0;
    memcpy(out, (const void *)(uintptr_t)addr, n);
    return 1;
}
static uint64_t peek64(uint64_t addr) { uint64_t v; peek(addr, &v, 8); return v; }
static uint32_t peek32(uint64_t addr) { uint32_t v; peek(addr, &v, 4); return v; }
static uint8_t peek8(uint64_t addr) { uint8_t v; peek(addr, &v, 1); return v; }

static int game_code_is(uint64_t off, const uint32_t *words, int n) {
    uint32_t got[8];
    return n <= 8 && peek(GAME_MAIN(off), got, 4u * (size_t)n) && !memcmp(got, words, 4u * (size_t)n);
}

// A line of the game's state, logged when it changes. Each kind of line has its own budget, so one that changes often
// cannot use up the others' (a camera's line once did, before the sharing key was ever set).
static void trace_line(int slot, const char *text) {
    static char last[TRACE_SLOTS][480];   // the game's thread only
    static int lines[TRACE_SLOTS];
    int cap = slot < 3 ? TRACE_STATE_LINES : TRACE_STATE_LINES / 10;
    if (lines[slot] >= cap || !strcmp(last[slot], text)) return;
    snprintf(last[slot], sizeof last[slot], "%s", text);
    lines[slot]++;
    mkwmp_log("camera: trace: game: %s%s", text, lines[slot] == cap ? " (the last line of this kind)" : "");
}

// On the game's thread (IsChatting), types[] the *VideoType objects it gave us (0: none).
static void trace_game(const uint64_t types[CAMERA_VIDEOS]) {
    static const uint32_t kGetter[8] = {0x39686008, 0x7100051f, 0x540001e1, 0x39686408, 0x7100051f, 0x54000181, 0xb94a0008, 0x37f80148};
    static const uint32_t kFill[8] = {0xf001af48, 0x91216108, 0xf9400108, 0x91403508, 0x39656109, 0x37000069, 0x39654108, 0x36000268};
    static const uint32_t kMode[2] = {0xb9427808, 0xaa0003f4}, kGuard[4] = {0x39405008, 0x7100051f, 0x540002a0, 0xd503237f};
    static int checked, ok;
    if (!checked) {
        checked = 1;
        ok = game_code_is(0x2996b50, kGetter, 8) && game_code_is(0x1c8e0c, kFill, 8) && game_code_is(0x1c8e74, kMode, 2) &&
             game_code_is(0x6355f0, kGuard, 4);
        if (!ok) mkwmp_log("camera: trace: the game's own state is not read: this is not the 1.8.0 code its offsets are from");
    }
    if (!ok || !types[0]) return;
    char text[480];
    uint64_t m = types[0] - 0xa30 - 0x38, g = peek64(GAME_MAIN(0x37b3858)), t = peek64(GAME_MAIN(0x37b84c8));
    uint64_t s = g ? g + 0xd944 : 0, online = peek64(GAME_MAIN(0x37c9828)), net = peek64(GAME_MAIN(0x37be190));
    t = t ? t + 0x17b8 : 0;
    online = online ? peek64(online + 0xd8) : 0;
    net = net ? peek64(net + 0x48) : 0;
    snprintf(text, sizeof text, "chatting %u, sharing key %u, mode %d (2: GameChat; it waits for: chatting, online state %u to "
             "be 0, session number %lld not 0); screen guard: wanted by %#x, forbidden by %#x, held by %#x, cameras %s, "
             "settling %u (%d frames left)", peek8(m + 0x3828), peek8(m + 0x3829), t ? (int)peek32(t + 0x278) : -1,
             online ? peek32(online + 8) : 0, net ? (long long)peek64(net + 0x170) : 0, s ? peek32(s) : 0, s ? peek32(s + 4) : 0,
             s ? peek32(s + 8) : 0, !s ? "?" : peek8(s + 0xc) ? "NOT given to the UI (screenshots allowed)" : "given to the UI",
             s ? peek8(s + 0x14) : 0, s ? (int)peek32(s + 0x10) : 0);
    trace_line(0, text);
    if (t) {
        int k = snprintf(text, sizeof text, "the UI has: local %s texture %lld; the others", peek8(t + 0x2d8) ? "yes," : "NO,",
                         (long long)peek64(t + 0x2b8 + 8));
        for (int i = 0; i < CAMERA_REMOTE && k < (int)sizeof text - 80; i++) {
            uint64_t e = t + 0x2e0 + (uint64_t)i * 0x38;
            if (!peek8(e + 8)) continue;
            k += snprintf(text + k, sizeof text - (size_t)k, " [account %#llx %s texture %lld]", (unsigned long long)peek64(e),
                          peek8(e + 0x30) ? "yes," : "NO,", (long long)peek64(e + 0x18));
        }
        snprintf(text + k, sizeof text - (size_t)k, "; texture of a camera without a picture %lld", (long long)peek64(t + 0x290));
        trace_line(1, text);
        // who may get a face at all: the game's own camera set-up, and the players entered for GameChat
        uint32_t flags = g ? peek32(g + 0x12c) : 0;
        k = snprintf(text, sizeof text, "faces: the player %s the game's GameChat camera set-up (flags %#x: bit 11 set up, bit 12 %u, "
                     "scene bit 3 %u)%s; players a face may be drawn for (index:kind, 1 or 2 GameChat, 0 the console's camera):",
                     flags & 0x800 ? "went through" : "did NOT go through", (unsigned)flags, (flags >> 12) & 1,
                     g ? (peek32(g + 0x120) >> 3) & 1 : 0,
                     flags & 0x800 ? "" : ": its face is sent to nobody and its own kart gets none");
        int entered = 0;
        for (int i = 0; i < 26 && k < (int)sizeof text - 12; i++) {
            uint64_t e = t + 8 + (uint64_t)i * 0x18;
            if (peek8(e + 0x14) != 1) continue;
            k += snprintf(text + k, sizeof text - (size_t)k, " %d:%u", i, (unsigned)peek32(e + 0x10));
            entered++;
        }
        if (!entered) snprintf(text + k, sizeof text - (size_t)k, " none");
        trace_line(2, text);
    }
    for (int i = 0; i < CAMERA_VIDEOS; i++) {
        if (!types[i]) continue;
        uint64_t v = types[i] - 0xa30;
        int32_t next = (int32_t)peek32(v + 0xa04);
        uint64_t sync = v + 8 + (uint64_t)(next >= 0 && next < 3 ? next : 0) * 0x350 + 0x340;
        int32_t id[3];
        uint64_t pool[3];
        for (int k = 0; k < 3; k++) {
            uint64_t w = v + 8 + (uint64_t)k * 0x350 + 0x280;
            id[k] = (int32_t)peek32(w + 0x68), pool[k] = peek64(w + 0x10);
        }
        if (!peek8(v + 0xa18) && i) continue;   // a remote camera given to nobody: nothing to say
        // what cycles every frame (the image shown, the sync waited for) is left out: the line is logged when it changes
        snprintf(text, sizeof text, "camera %d: given to a member %u (account %#llx), started %u, %s, its sync is %s; texture ids "
                 "%d %d %d, %s", i, peek8(v + 0xa18), (unsigned long long)peek64(v + 0xa20), peek8(v + 0xa19),
                 (int32_t)peek32(v + 0xa00) >= 0 ? "shows an image" : "shows NO image yet",
                 (peek64(sync) & ~0xffull) == CAMERA_SYNC_MAGIC ? "ours" : "not ours (the first poll)", (int)id[0], (int)id[1],
                 (int)id[2], pool[0] && pool[1] && pool[2] ? "each over a memory pool" : "NO memory pool behind the native images: the game made no texture of them");
        trace_line(3 + i, text);
    }
}

static Source *source_of(uint64_t account) {
    if (!account) return NULL;
    for (int i = 0; i < CAMERA_REMOTE; i++)
        if (g_remote[i].account == account) return &g_remote[i];
    return NULL;
}

// Nearest-neighbour copy of a sw x sh picture (rows sw * 4 apart) over a dw x dh one (rows drow apart), flipped left
// to right: the game draws its camera images mirrored (the first live run showed the test pattern's name reversed
// above the other kart), so the picture is stored mirrored and shows the right way round. The player's own "Mirror
// my picture" setting still applies on top, in the capture library.
static void blit(uint8_t *dst, int dw, int dh, int drow, const uint8_t *src, int sw, int sh) {
    for (int y = 0; y < dh; y++) {
        const uint8_t *row = src + (size_t)(y * sh / dh) * (size_t)sw * 4;
        uint8_t *out = dst + (size_t)y * (size_t)drow;
        for (int x = 0; x < dw; x++) memcpy(out + (size_t)x * 4, row + (size_t)((dw - 1 - x) * sw / dw) * 4, 4);
    }
}

static void video_write(Video *v, int image, const uint8_t *rgba) {
    if (image < 0 || image >= v->made) return;
    blit(v->pixels[image], v->w, v->h, v->row, rgba, CAMERA_W, CAMERA_H);
    int told = H.native_image_touch(v->handle[image]), i = (int)(v - g_video);
    g_count[i].writes++, g_count[i].refused += !told;
    if (tr(T_WRITE + i)) {
        const uint8_t *p = v->pixels[image] + (size_t)(v->h / 2) * (size_t)v->row + (size_t)(v->w / 2) * 4;
        mkwmp_log("camera: trace: camera %d image %d (handle %#x, pixels %p) written, the runtime %s; middle pixel %u %u %u %u", i,
                  image, (unsigned)v->handle[image], (void *)v->pixels[image], told ? "told" : "REFUSED the handle", p[0], p[1],
                  p[2], p[3]);
    }
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
    if (g_loopback) {
        // with MKWMP_CAMERA_CODEC=1 the others' picture is the local one as it would arrive: encoded, ten a second, decoded
        const uint8_t *shown = g_local.rgba;
        if (g_loop_codec) {
            if (!g_loop_enc.quality) camcodec_encoder_init(&g_loop_enc);
            int n = camcodec_encode(&g_loop_enc, g_local.rgba, now, g_jpeg, sizeof g_jpeg);
            shown = n && camcodec_decode(g_jpeg, n, g_loop_rgba) ? g_loop_rgba : NULL;
            if (shown && g_trace && g_loop_enc.frames % 100 == 1)
                mkwmp_log("camera: trace: codec: frame %u is %d bytes at quality %d (%u encoded again so far)", (unsigned)g_loop_enc.frames,
                          n, g_loop_enc.last_quality, (unsigned)g_loop_enc.over);
        }
        for (int i = 0; shown && i < CAMERA_REMOTE; i++)
            if (g_remote[i].account) {
                memcpy(g_remote[i].rgba, shown, PICTURE_BYTES);
                bump(&g_remote[i]);
            }
    }
    return 1;
}

// ---- the game's own camera set-up ("Use the camera?"; research/docs/CAMERA-RE.md 9) --------------------------------
// Choosing Online Play on the main menu runs OnlineCameraSetup.module before the lobby: its first node,
// AIGlobalSeqCheckVideoCameraConnection (is_online 1), asks the console's camera service whether a camera with one of
// four 640x480 formats is attached (main+0x2994fa0 -> main+0x25a680: request 29, slot 0xd8 of the object at
// [main+0x378ecb8]; 0 = yes). "Invalid" ends the module: no prompt. "Valid" opens the game's dialog (Camera On /
// Camera Off); Camera On, accepted only while IsChatting is 1, sets flag 11 of F (4. above) and, with two players on
// the console, opens the face position page. The flag is dropped when the chat ends or the player returns to the
// title screen. The runtime has no camera service: its stand-in (mkw-recomp patches.c host_camera_event, an object
// followed by its own table) answers 0x7c4, "no camera", so the prompt never came.
// The hook stands in front of that node's update and, when the node is the online one and the Camera tab has the
// camera on, puts a game function that returns 0 in the stand-in's slot for the length of the call. Local play's
// node (is_online 0, the console's own camera, which MKWMP does not feed) and every other asker keep "no camera".
static void hook_camera_check(Cpu *c) {
    const struct game_addrs *a = g_setup;
    uint64_t node = c->x[0], input = peek64(node + a->cam_check_online);
    int online = input && peek8(input) == 1, before = (int)(peek32(node + a->cam_check_result) >> 30);
    plat_lock_shared(&g_lock);
    int want = g_settings.consent && g_settings.camera;
    plat_unlock_shared(&g_lock);
    uint64_t service = peek64(a->cam_service), table = service ? peek64(service) : 0, old = 0;
    uint64_t *slot = NULL;
    int stand_in = service && table == service + 16;   // the runtime's: a real service object's table is the game's
    if (online && want && stand_in) {
        slot = (uint64_t *)(uintptr_t)(table + 8ull * a->cam_query_slot);
        old = plat_mem_readable(slot) >= 8 ? *slot : 0;
        if (!old || old == a->cam_ret0 || !plat_mem_write_u64(slot, a->cam_ret0)) slot = NULL;
    }
    guesthook_original(c, g_setup_original);
    uint64_t ret = c->x[0];
    if (slot) plat_mem_write_u64(slot, old);   // everybody else is still told there is no camera
    int after = (int)(peek32(node + a->cam_check_result) >> 30), answered = after >= 2 && before < 2;
    if (answered && online) {
        // Online Play chosen again: whatever the last prompt's answer was, it is asked anew (or not asked at all)
        plat_lock(&g_lock);
        g_prompted = after == 2, g_declined = 0;
        if (after == 2 && slot) {
            // the prompt is about to open: the game takes Camera On only while a chat runs, and the player is not online yet
            g_hold.at = plat_ms() | 1, g_hold.was_online = 0;
            if (camera_chatting(&g_settings, 1)) g_chatting = 1;   // from the next IsChatting, not the thread's next pass
        }
        plat_unlock(&g_lock);
    }
    if (tr(T_SETUP))
        mkwmp_log("camera: trace: set-up: the game's camera check ran (node %#llx, %s, state %d -> %d; the Camera tab %s; "
                  "the camera service %#llx is %s; answer %s)", (unsigned long long)node, online ? "online" : "local play",
                  before, after, want ? "has the camera on" : "has the camera off", (unsigned long long)service,
                  stand_in ? "the runtime's stand-in" : "not the runtime's stand-in", slot ? "a camera is attached" : "left alone");
    if (answered && online) {
        if (after == 2)
            mkwmp_log("camera: set-up: Online Play chosen: the game asked whether a camera is attached and was told yes; its "
                      "\"Use the camera?\" prompt opens now (Camera On puts the face above the kart)%s", slot ? "" :
                      " (not by MKWMP's answer)");
        else
            mkwmp_log("camera: set-up: Online Play chosen: the game asked whether a camera is attached and was told no (%s): "
                      "no \"Use the camera?\" prompt, so no face above this player's kart, here or for the others",
                      !want ? "the camera is off in the Camera tab, or the 18+ consent is missing" :
                      !stand_in ? "the camera service is not the runtime's stand-in" : "the stand-in could not be changed");
    }
    c->x[0] = ret;
}

int camera_setup_install(const struct game_addrs *a) {
    if (!g_installed) return 0;
    if (g_setup) return 1;
    char v[8] = "";
    if (plat_getenv("MKWMP_CAMERA_SETUP", v, sizeof v) && !strcmp(v, "0")) {
        mkwmp_log("camera: set-up: off (MKWMP_CAMERA_SETUP=0): the game will not ask \"Use the camera?\" and draws no face");
        return 0;
    }
    if (!a || !a->cam_check_slot) {
        mkwmp_log("camera: set-up: off: no addresses of the game's camera prompt for this build (the game will not ask "
                  "\"Use the camera?\" and draws no face)");
        return 0;
    }
    if (!game_guard_slot("camera set-up check", a->cam_check_slot, 0, a->cam_check_fn) ||
        !game_guard_words("camera set-up check", a->cam_check_fn + a->cam_check_at, a->cam_check_words, 4) ||
        !game_guard_words("camera set-up query", a->cam_query_at, a->cam_query_words, 3) ||
        !game_guard_words("camera set-up answer", a->cam_ret0, a->cam_ret0_words, 2)) {
        mkwmp_log("camera: set-up: off: guard mismatch, the game's camera check is NOT hooked (no \"Use the camera?\" prompt)");
        return 0;
    }
    g_setup = a;   // before the hook goes in: it may run at once
    if (!guesthook_vtable(a->cam_check_slot, 0, hook_camera_check, &g_setup_original)) {
        g_setup = NULL;
        mkwmp_log("camera: set-up: off: the game's camera check could not be hooked (no \"Use the camera?\" prompt)");
        return 0;
    }
    mkwmp_log("camera: set-up: the game's camera check is hooked: with the camera on in the Camera tab, choosing Online Play "
              "opens the game's \"Use the camera?\" prompt");
    return 1;
}

void camera_game_loaded(void) {
    if (g_installed) camera_setup_install(game_addrs());
}

// The flag the prompt's answer sets, read four times a second on the game's thread and logged when it changes.
static void setup_watch(void) {
    static uint64_t at;
    static int was = -1;
    const struct game_addrs *a = g_setup;
    if (!a || !a->game_global) return;
    uint64_t now = plat_ms();
    if (now - at < 250) return;
    at = now;
    uint64_t global = peek64(a->game_global);
    if (!global) return;
    int on = (int)(peek32(global + a->g_flags4) >> a->cam_setup_bit) & 1, changed = on != was && (on || was >= 0);
    was = on;
    plat_lock(&g_lock);
    g_face = on;   // what camera_refresh takes the prompt's answer from
    plat_unlock(&g_lock);
    if (!changed) return;
    if (on)
        mkwmp_log("camera: set-up: done: the player chose Camera On in the game's prompt: its face goes above its kart, "
                  "here and for the chat room's members, in the races from now on");
    else
        mkwmp_log("camera: set-up: undone (Camera Off, the title screen, or the chat ended): no face until the prompt is "
                  "answered again (choose Online Play)");
}

// nn::chat::ShowChatStart / ShowCameraSharingGuide: the system's pages the game opens from its prompt (on the console;
// nothing to show here). The game waits for the call to return and does not read the guide's answer.
static void ov_show_chat_start(Cpu *c) {
    mkwmp_log("camera: set-up: the game asked the system to start a GameChat (the player chose the camera with no chat "
              "running): nothing to show, the prompt comes back");
    ret_x(c, 0);
}
static void ov_show_sharing_guide(Cpu *c) {
    mkwmp_log("camera: set-up: Camera On chosen: the game opened the system's camera sharing guide (nothing to show here)");
    ret_x(c, 0);
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
    if (chatting && g_camera_on) send = local_picture() && g_local_streaming;
    else g_local_kind = KIND_NONE;
    CameraFrameOut out = g_out;
    void *user = g_out_user;
    CameraJpegOut jpeg_out = g_jpeg_out;
    void *jpeg_user = g_jpeg_user;
    int divisor = g_divisor;
    // the trace: once a second the cameras' counts, four times a second the game's own state (logged when it changes)
    static uint64_t counted_at, state_at;
    static int summaries, was_chatting = -1;
    static TraceCount last[CAMERA_VIDEOS];
    TraceCount count[CAMERA_VIDEOS] = {{0}};
    uint64_t types[CAMERA_VIDEOS] = {0}, now = g_trace ? plat_ms() : 0;
    uint32_t serial = g_local.serial;
    int count_now = g_trace && chatting && now - counted_at >= 1000, state_now = g_trace && now - state_at >= 250;
    if (count_now) {
        memcpy(count, g_count, sizeof count);
        memset(g_count, 0, sizeof g_count);
        counted_at = now;
    }
    for (int i = 0; state_now && i < CAMERA_VIDEOS; i++) types[i] = g_video[i].type;
    plat_unlock(&g_lock);
    if (g_trace && chatting != was_chatting) {
        mkwmp_log("camera: trace: IsChatting answers %d from now on (caller %#llx)", chatting, (unsigned long long)c->x[30]);
        was_chatting = chatting;
    }
    if (count_now && summaries < TRACE_SUMMARIES && memcmp(count, last, sizeof count)) {
        char text[480];
        int k = 0;
        for (int i = 0; i < CAMERA_VIDEOS && k < (int)sizeof text - 96; i++)
            if (count[i].polls || count[i].requests || count[i].unknown || count[i].writes)
                k += snprintf(text + k, sizeof text - (size_t)k, " camera %d: %u polls, %u done, %u images asked (%u of a camera "
                              "we do not know), %u written (%u refused);", i, count[i].polls, count[i].done, count[i].requests,
                              count[i].unknown, count[i].writes, count[i].refused);
        mkwmp_log("camera: trace: the last second:%s local picture %u%s", k ? text : " the game polled and asked for nothing;",
                  (unsigned)serial, ++summaries == TRACE_SUMMARIES ? " (the last of these lines)" : "");
        memcpy(last, count, sizeof last);
    }
    if (state_now) {
        state_at = now;
        trace_game(types);
    }
    setup_watch();
    if (event) {   // the game reads the room again (members, camera flags) when its event is signalled
        if (!signal && g_rt.guest_symbol) signal = g_rt.guest_symbol("_ZN2nn2os17SignalSystemEventEPNS0_15SystemEventTypeE");
        if (signal && g_rt.call) g_rt.call(c, signal, 1, event);
        if (tr(T_SIGNAL)) mkwmp_log("camera: trace: the room changed: the game's event %#llx signalled%s", (unsigned long long)event,
                                   signal && g_rt.call ? "" : " NOT (no nn::os::SignalSystemEvent)");
    }
    // g_local is written on this thread only, so it is read here without the lock (out may call camera_* back)
    if (send && out) out(g_local.rgba, CAMERA_W, CAMERA_H, plat_ms(), user);
    if (send && jpeg_out) {   // the frame as it goes on the wire: at the codec's frame rate, never over its size
        uint64_t ms = plat_ms();
        if (!g_enc.quality) camcodec_encoder_init(&g_enc);
        camcodec_encoder_divisor(&g_enc, divisor);
        int n = camcodec_encode(&g_enc, g_local.rgba, ms, g_jpeg, sizeof g_jpeg);
        if (n) jpeg_out(g_jpeg, n, ms, jpeg_user);
    }
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
    if (out && tr(T_ROOM)) {
        char text[400];
        int32_t n;
        uint64_t self;
        memcpy(&self, out + 0x28, 8), memcpy(&n, out + 0x38, 4);
        int k = snprintf(text, sizeof text, "local account %#llx camera %u, %d members:", (unsigned long long)self, out[0x30], (int)n);
        for (int i = 0; i < n && i < CAMERA_ROOM_MEMBERS && k < (int)sizeof text - 40; i++) {
            uint64_t account;
            memcpy(&account, out + 0x40 + i * 0x10, 8);
            k += snprintf(text + k, sizeof text - (size_t)k, " %#llx%s", (unsigned long long)account, out[0x48 + i * 0x10] ? " (camera)" : "");
        }
        mkwmp_log("camera: trace: GetRoomInfo (caller %#llx): %s", (unsigned long long)c->x[30], text);
    }
    ret_x(c, 0);
}

static void ov_set_sharing_key(Cpu *c) {
    static char last[0x28];
    const char *key = arg_p(c, 0);
    char text[0x28] = {0};
    for (int i = 0; key && i < 0x27 && key[i]; i++) text[i] = key[i] >= 0x20 && key[i] < 0x7f ? key[i] : '?';
    static int cleared;
    plat_lock(&g_lock);
    int changed = strcmp(last, text) != 0 || cleared != g_key_cleared;
    memcpy(last, text, sizeof last);
    cleared = g_key_cleared;
    plat_unlock(&g_lock);
    if (changed)   // (the key is the game's own business: the server knows the friend room without it)
        mkwmp_log("camera: sharing key \"%s\" (the game's session number: the same for players racing together); the game is "
                  "in its GameChat mode from here", text);
    ret_x(c, 0);
}
static void ov_clear_sharing_key(Cpu *c) {
    plat_lock(&g_lock);
    g_key_cleared++;
    plat_unlock(&g_lock);
    if (tr(T_KEY)) mkwmp_log("camera: trace: ClearSharingKey (the game left its GameChat mode)");
    ret_x(c, 0);
}

// nn::oe::{Disable,Enable}{ScreenShot,Recording}, {Begin,End}PresentPhotographedContents (the 6 nn::oe imports of 1.8.0 the runtime has no handler for): the game blocks screenshots while it shows players' cameras (after SetSharingKey).
// The SDK's versions call the applet service, which this runtime does not have: the first two-player camera run
// crashed reading address 0 in DisableScreenShot (sdk+0x590340) right after the sharing key. Nothing to block here.
// They are also the edges of the game's screen guard (0x6355f0): Disable / Begin when it starts handing the cameras to
// its UI (ten frames later), Enable / End when it stops; while screenshots are allowed the UI gets no camera picture.
static void screenshot(Cpu *c, int which) {
    static const char *const kName[6] = {"DisableScreenShot", "EnableScreenShot", "DisableRecording", "EnableRecording",
                                         "BeginPresentPhotographedContents", "EndPresentPhotographedContents"};
    if (tr(T_OE + which))
        mkwmp_log("camera: trace: nn::oe::%s: the game %s the cameras to its UI", kName[which], which & 1 ? "stops giving" : "will give");
    ret_x(c, 0);
}
static void ov_screenshot_off(Cpu *c) { screenshot(c, 0); }
static void ov_screenshot_on(Cpu *c) { screenshot(c, 1); }
static void ov_recording_off(Cpu *c) { screenshot(c, 2); }
static void ov_recording_on(Cpu *c) { screenshot(c, 3); }
static void ov_present_begin(Cpu *c) { screenshot(c, 4); }
static void ov_present_end(Cpu *c) { screenshot(c, 5); }

// ---- nn::chat: the pictures

// The game allocates what this says and hands it to Initialize*CameraVideoStorage; the pictures live in the runtime's
// memory (native_image_create), so one page is all we ask of the game's heap, as the runtime's own stub does.
static void ov_buffer_size(Cpu *c) {
    if (tr(T_SIZE)) mkwmp_log("camera: trace: Calculate*CameraVideoBufferSize(%d, %d, %d) = 0x1000", (int)arg_x(c, 0), (int)arg_x(c, 1), (int)arg_x(c, 2));
    ret_x(c, 0x1000);
}

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
        if (g_trace)
            mkwmp_log("camera: trace: camera %d is the game's object %#llx (its buffer %#llx, %#llx bytes); handles %#x %#x %#x, "
                      "pixels %p %p %p", v->cycle.index, (unsigned long long)type, (unsigned long long)arg_x(c, 4),
                      (unsigned long long)arg_x(c, 5), (unsigned)v->handle[0], (unsigned)v->handle[1], (unsigned)v->handle[2],
                      (void *)v->pixels[0], (void *)v->pixels[1], (void *)v->pixels[2]);
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
    int index = v ? (int)(v - g_video) : -1;
    plat_unlock_shared(&g_lock);
    if (g_trace && g_trace_n[T_HANDLE] < CAMERA_VIDEOS * CAMERA_IMAGES && ++g_trace_n[T_HANDLE])   // every image of the boot
        mkwmp_log("camera: trace: Get*CameraImageHandle(%#llx, %d) = %#x (%s)", (unsigned long long)type, i, (unsigned)handle,
                  index < 0 ? "NOT an object we know: no picture will reach it" : !(handle & 0x80000000u) ? "NOT an image of ours" : "ours");
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
    if (tr(T_FINALIZE)) mkwmp_log("camera: trace: Finalize*CameraVideoStorage(%#llx)%s", (unsigned long long)arg_x(c, 0), v ? "" : ": not ours");
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
    uint64_t handle[2] = {0, 0};
    plat_lock(&g_lock);
    Video *v = video_find(arg_x(c, 1));
    int index = v ? (int)(v - g_video) : -1;
    if (sync) {
        sync[0] = sync[1] = 0;   // not one of ours: the runtime's poll answers done, the picture stays as it is
        if (v && image >= 0 && image < v->made) camera_cycle_request(&v->cycle, image, id ? *id : 0, sync);
        handle[0] = sync[0], handle[1] = sync[1];
    }
    if (index >= 0 && handle[0]) g_count[index].requests++;
    else g_count[local ? 0 : 1].unknown++;
    plat_unlock(&g_lock);
    if (tr(T_REQUEST + (index >= 0 ? index : local ? 0 : 1)))
        mkwmp_log("camera: trace: RequestNext%sCameraImage(sync %p, object %#llx, image %d, account %#llx): %s %d, sync %#llx:%llu",
                  local ? "Local" : "Remote", (void *)sync, (unsigned long long)arg_x(c, 1), image, (unsigned long long)(id ? *id : 0),
                  index < 0 ? "NOT an object we know, camera" : handle[0] ? "camera" : "REFUSED (no such image), camera", index,
                  (unsigned long long)handle[0], (unsigned long long)handle[1]);
    ret_x(c, 0);
}
static void ov_request_local(Cpu *c) { request_next(c, 1); }
static void ov_request_remote(Cpu *c) { request_next(c, 0); }

// nn::gs::TryWaitNativeSync: the game polls the sync of the image it asked for; the movie decoder polls its own.
static void ov_try_wait_sync(Cpu *c) {
    const uint64_t *handle = arg_p(c, 0);
    if (!handle || !camera_sync_ours(handle)) {
        uint64_t lr = c->x[30], h0 = handle ? handle[0] : 0, h1 = handle ? handle[1] : 0;
        if (g_stock_trywait) g_stock_trywait(c);
        else ret_x(c, 1);
        // the game's camera poll (1.8.0: it returns to main+0x2996b24) with a handle that is not ours: a camera's first
        // poll, of the zero handle its image 0 starts with; the movie player's polls come from elsewhere
        if (g_trace && lr == GAME_MAIN(0x2996b24) && tr(T_FOREIGN))
            mkwmp_log("camera: trace: TryWaitNativeSync(%p = %#llx:%llu) from the game's camera update: not a sync of ours, the "
                      "runtime answers %llu", (void *)handle, (unsigned long long)h0, (unsigned long long)h1, (unsigned long long)c->x[0]);
        return;
    }
    int index = camera_sync_index(handle), done = 1, write = -1;
    uint32_t serial = 0, seq = 0;
    if (index < CAMERA_VIDEOS) {
        plat_lock(&g_lock);
        Video *v = &g_video[index];
        const Source *s = index ? source_of(v->cycle.account) : &g_local;
        serial = s ? s->serial : 0, seq = v->cycle.seq;
        done = camera_cycle_poll(&v->cycle, handle, serial, &write);
        g_count[index].polls++, g_count[index].done += done != 0;
        if (write >= 0) video_write(v, write, s->rgba);   // the image asked for: not the one on screen
        plat_unlock(&g_lock);
        if ((done || g_trace_n[T_POLL + index] < 3) && tr(T_POLL + index))   // every frame polls: the answers that matter
            mkwmp_log("camera: trace: TryWaitNativeSync(camera %d, request %llu of %u, caller %#llx): %s; the source's picture is "
                      "number %u%s", index, (unsigned long long)handle[1], (unsigned)seq, (unsigned long long)c->x[30],
                      !done ? "not yet" : write >= 0 ? "done, image written" : "done, nothing written (an old request)",
                      (unsigned)serial, serial ? "" : " (none: nobody feeds this camera)");
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
    plat_lock(&g_lock);
    int held = camera_hold(&g_hold, online, plat_ms());   // the game's camera prompt, before the sign-in
    // Camera Off in that prompt: the game draws and sends no face of this player, so nothing is captured, shown or
    // sent either, until Online Play is chosen again; the others' cameras are still seen with camera_view on
    int declined = camera_declined(g_prompted, online, g_face), was_declined = g_declined;
    g_declined = declined;
    plat_unlock(&g_lock);
    CameraSettings tab = s;   // what the Camera tab says: the game's next prompt is offered by it
    if (declined) s.camera = 0;
    int chatting = camera_chatting(&s, online || held);
    if (declined != was_declined)
        mkwmp_log(declined ? "camera: set-up: the player chose Camera Off in the game's prompt: the local camera is off for this "
                             "session (not captured, not shown, not sent); the others' cameras %s. Choose Online Play again "
                             "to be asked again" :
                             "camera: set-up: Camera Off no longer holds (the player left online play, or chose Camera On)%s",
                  !declined ? "" : s.view ? "are still shown" : "are not shown either (\"See the others' cameras\" is off)");

    // The members are the chat room's (chatroom.h): at most four of the player's mutual friends in the private friend
    // room, as the relay names them. The game matches a member to a kart by account id alone and gives its four remote
    // cameras to the first four members with the flag, so a room larger than that would show some faces and not others.
    // In no friend room (a matchmade race or battle, the menus) there is no camera at all: nobody is listed, the
    // player's own camera flag is down, nothing is captured or sent. IsChatting itself goes on while the player is
    // online: the game drops the answer to its "Use the camera?" prompt, given at Online Play before any room, the
    // moment the chat ends (setup_watch), and would then draw and send no face in the friend room either. Before the
    // sign-in, while that prompt is held open, the local camera is on for it (nothing leaves: there is no relay yet).
    CameraMember m[CAMERA_MEMBERS_MAX + 1];
    int n = 0;
    chatroom_refresh();
    ChatRoom chat;
    chatroom_get(&chat);
    int in_room = camera_in_room(chat.id, held && !online);
    if (!in_room) s.camera = 0;
    for (int i = 0; i < chat.n; i++) m[n++] = chat.member[i];
    int from_room = n, from_env = 0, made_up = 0;
    if (chat.id && g_env_members[0] && H.account_of) {
        char names[sizeof g_env_members];
        memcpy(names, g_env_members, sizeof names);
        for (char *t = strtok(names, ", "); t && n < CAMERA_MEMBERS_MAX; t = strtok(NULL, ", "))
            m[n].account = H.account_of(t), m[n++].camera = 1, from_env++;
    }
    int others = 0;
    for (int i = 0; i < n; i++) {
        others += m[i].account && m[i].account != self;
        if (g_loopback) m[i].camera = 1;                         // their picture is the local one
        else if (!s.view) m[i].camera = 0;                       // the player does not want to see cameras
        if (H.muted && H.muted(m[i].account)) m[i].camera = 0;   // a muted player's face is hidden too
    }
    // nobody at all: a made-up member, so one PC alone still drives a remote camera (no kart has its account)
    if (g_loopback && !others) m[n].account = LOOPBACK_ACCOUNT, m[n++].camera = 1, made_up = 1;

    uint8_t room[CAMERA_ROOM_SIZE];
    int listed = camera_room_encode(room, self, chatting && s.camera, chatting ? m : NULL, chatting ? n : 0);
    plat_lock(&g_lock);
    int was = g_chatting, changed = memcmp(room, g_room, sizeof room) != 0;
    g_settings = tab;
    g_camera_on = s.camera;
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
        mkwmp_log("camera: chat %s (online %d, consent %d, camera %d%s, view %d%s%s)", chatting ? "on" : "off", online, s.consent,
                  tab.camera, declined ? " (Camera Off in the game's prompt)" : "", s.view, g_loopback ? ", loopback" : "",
                  held && !online ? ", held on for the game's camera prompt until the player is online" : "");
    static int where_was = -1;
    int where = chat.id ? 2 : in_room;
    if (chatting && where != where_was) {
        mkwmp_log("camera: %s", where == 2 ? "in a friend room: the camera is shared with the player's mutual friends there, at most four faces" :
                  where ? "the game's camera prompt: the local camera is on for it (nothing is sent)" :
                          "in no friend room (a matchmade race or battle, or the menus): no camera, the player's or anyone's");
        where_was = where;
    }
    if (changed && chatting) {
        char list[400] = "";
        int cameras = 0, k = 0;
        for (int i = 0; i < listed; i++) {
            uint64_t account;
            memcpy(&account, room + 0x40 + i * 0x10, 8);
            cameras += room[0x40 + i * 0x10 + 8];
            if (k < (int)sizeof list - 40)
                k += snprintf(list + k, sizeof list - (size_t)k, " %#llx%s", (unsigned long long)account,
                              room[0x40 + i * 0x10 + 8] ? " (camera)" : "");
        }
        mkwmp_log("camera: room: account %#llx camera %s, %d members, %d with a camera (friend room %#llx%s: %d of the %d mutual "
                  "friends in it, %d by MKWMP_CAMERA_MEMBERS, %d made up):%s",
                  (unsigned long long)self, s.camera ? "on" : "off", listed, cameras, (unsigned long long)chat.id,
                  g_loopback ? " (loopback)" : "", from_room, chat.friends, from_env, made_up, listed ? list : " nobody");
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
        {"_ZN2nn2oe17DisableScreenShotEv", ov_screenshot_off},
        {"_ZN2nn2oe16EnableScreenShotEv", ov_screenshot_on},
        {"_ZN2nn2oe16DisableRecordingEv", ov_recording_off},
        {"_ZN2nn2oe15EnableRecordingEv", ov_recording_on},
        {"_ZN2nn2oe32BeginPresentPhotographedContentsEv", ov_present_begin},
        {"_ZN2nn2oe30EndPresentPhotographedContentsEv", ov_present_end},
        {"_ZN2nn4chat13ShowChatStartERKNS_7account3UidE", ov_show_chat_start},
        {"_ZN2nn4chat22ShowCameraSharingGuideEv", ov_show_sharing_guide},
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
    ChatRoomHost room = {.self_account = host->self_account, .friends = host->friends};
    chatroom_install(&room);
    char v[8] = "";
    g_loopback = plat_getenv("MKWMP_CAMERA_LOOPBACK", v, sizeof v) && !strcmp(v, "1");
    g_env_pattern = plat_getenv("MKWMP_CAMERA_PATTERN", v, sizeof v) && !strcmp(v, "1");
    g_loop_codec = plat_getenv("MKWMP_CAMERA_CODEC", v, sizeof v) && !strcmp(v, "1");
    g_trace = plat_getenv("MKWMP_CAMERA_TRACE", v, sizeof v) && !strcmp(v, "1");
    memset(g_trace_n, 0, sizeof g_trace_n);
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
    mkwmp_log("camera: %d overrides (plugin API %u)%s%s%s%s; off until the player turns it on in the Camera tab", CAMERA_OVERRIDES,
              (unsigned)host->api_version, g_loopback ? ", loopback" : "", g_env_pattern ? ", test pattern" : "",
              g_trace ? ", trace" : "", g_stock_attach && g_stock_trywait ? "" : ", the runtime has no stock handler to chain to");
    if (!host->no_thread && !plat_thread_start(worker, NULL)) mkwmp_log("camera: no thread: the camera stays off");
    return CAMERA_OVERRIDES;
}

// ---- the network's side (roomnet.c)

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
        // as it is, the right way round: the flip the game needs is made once, when the picture goes into its image
        // (blit; this used blit too, and a member's picture came out flipped twice)
        if (w == CAMERA_W && h == CAMERA_H) memcpy(s->rgba, rgba, PICTURE_BYTES);
        else camcodec_scale(rgba, w, h, s->rgba, CAMERA_W, CAMERA_H);
        bump(s);
    }
    plat_unlock(&g_lock);
    return s != NULL;
}

void camera_set_jpeg_out(CameraJpegOut fn, void *user) {
    plat_lock(&g_lock);
    g_jpeg_out = fn;
    g_jpeg_user = user;
    plat_unlock(&g_lock);
}

void camera_set_send_divisor(int divisor) {
    plat_lock(&g_lock);
    g_divisor = divisor == 2 || divisor == 4 ? divisor : 1;
    plat_unlock(&g_lock);
}

int camera_jpeg_in(uint64_t account, const uint8_t *jpeg, int len) {
    static PlatLock one = PLAT_LOCK_INIT;   // one decoded picture at a time: the relay's thread is one anyway
    static uint8_t rgba[PICTURE_BYTES];
    if (!jpeg || len < 4 || len > CAMERA_JPEG_MAX) return 0;
    plat_lock_shared(&g_lock);
    int wanted = source_of(account) != NULL;
    plat_unlock_shared(&g_lock);
    if (!wanted) return 0;   // nothing is decoded for a member the game did not ask for
    plat_lock(&one);
    int ok = camcodec_decode(jpeg, len, rgba) && camera_frame_in(account, rgba, CAMERA_W, CAMERA_H);
    plat_unlock(&one);
    return ok;
}

void camera_members_set(uint64_t room_id, const CameraMember *members, int n) {
    chatroom_set(room_id, members, members && n > 0 ? n : 0);
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
    int on = g_local_streaming && g_chatting && g_camera_on;
    plat_unlock_shared(&g_lock);
    return on;
}


int camera_declined_now(void) {
    plat_lock_shared(&g_lock);
    int declined = g_declined;
    plat_unlock_shared(&g_lock);
    return declined;
}
