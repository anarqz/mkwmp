// The GameChat camera (camera.h): the gate (plugin API below 3, another game: nothing registered), the settings
// (everything off unless "1"), the chat room's pick (chatroom.h), GetRoomInfo's bytes, the frame cycle, the test pattern, the 28 overrides driven as
// the game drives them (boot, the per-frame poll and request of 1.8.0's video update) against fake native images, and
// the game's own camera set-up (the hooked "is a camera attached" node and the chat held on for its prompt).
//
//   $zig = "C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"; $s = "native\probe\src"
//   & $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o scratch\test_camera.exe `
//       native\probe\tests\test_camera.c $s\camera.c $s\chatroom.c $s\camlib.c $s\gameaddrs.c $s\guesthook.c $s\log.c $s\plat_win.c
//   scratch\test_camera.exe            (the camera installed, with MKWMP_CAMERA_LOOPBACK=1)
//   scratch\test_camera.exe api2       (a plugin API 2 runtime: the feature stays off)
#include "../src/camera.h"
#include "../src/gameaddrs.h"
#include "../src/plat.h"
#include "../src/rtapi.h"

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int failures;
#define CHECK(c) do { if (!(c)) { printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #c); failures++; } } while (0)

// ---- a fake runtime ------------------------------------------------------------------------------------------------
RtApi g_rt;
enum { MAX_OV = 64 };
static struct { const char *name; GuestFn fn; } g_ov[MAX_OV];
static int g_nov, g_stock_trywait_calls, g_stock_attach_calls, g_signals;
static uint64_t g_signalled;

static void rt_override(const char *name, GuestFn fn) {
    if (g_nov < MAX_OV) g_ov[g_nov].name = name, g_ov[g_nov++].fn = fn;
}
static int rt_guest_modules(void) { return 0; }
static uint64_t rt_guest_symbol(const char *name) { return strstr(name, "SignalSystemEvent") ? 0x5151 : 0; }
static void rt_log(const char *fmt, ...) { (void)fmt; }
static uint64_t rt_call(Cpu *c, uint64_t fn, int n, ...) {
    (void)c;
    va_list ap;
    va_start(ap, n);
    uint64_t a0 = n > 0 ? va_arg(ap, uint64_t) : 0;
    va_end(ap);
    if (fn == 0x5151) g_signals++, g_signalled = a0;
    return 0;
}
static void stock_trywait(Cpu *c) { g_stock_trywait_calls++; c->x[0] = 1; }   // an unknown decoder id: done
static void stock_attach(Cpu *c) { g_stock_attach_calls++; c->x[0] = 0; }
static GuestFn rt_stock(const char *name) {
    if (strstr(name, "TryWaitNativeSync")) return stock_trywait;
    if (strstr(name, "AttachToRoomInfoChangedEvent")) return stock_attach;
    return NULL;
}

static uint64_t call(const char *part, uint64_t a0, uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, uint64_t a5) {
    for (int i = 0; i < g_nov; i++)
        if (strstr(g_ov[i].name, part)) {
            Cpu c;
            memset(&c, 0, sizeof c);
            c.x[0] = a0, c.x[1] = a1, c.x[2] = a2, c.x[3] = a3, c.x[4] = a4, c.x[5] = a5;
            g_ov[i].fn(&c);
            return c.x[0];
        }
    printf("FAIL no override named *%s*\n", part);
    failures++;
    return ~0ull;
}
#define P(p) ((uint64_t)(uintptr_t)(p))

// ---- fake native images (plugin API 3) and the host's session ---------------------------------------------------------
enum { MAX_IMAGES = 64, ROW = 2048 };   // rows further apart than 384 * 4: the padding is not ours to write
static struct { uint8_t *pixels; int w, h, touches; } g_img[MAX_IMAGES];
static int g_nimg;
static uint32_t image_create(int w, int h, void **pixels, int *row_bytes) {
    if (g_nimg >= MAX_IMAGES) return 0;
    g_img[g_nimg].pixels = calloc((size_t)ROW, (size_t)h);
    g_img[g_nimg].w = w, g_img[g_nimg].h = h;
    *pixels = g_img[g_nimg].pixels;
    *row_bytes = ROW;
    return 0x80000000u | (uint32_t)g_nimg++;
}
static int image_touch(uint32_t handle) {
    uint32_t i = handle & 0x7fffffffu;
    if (!(handle & 0x80000000u) || i >= (uint32_t)g_nimg) return 0;
    g_img[i].touches++;
    return 1;
}

static struct { const char *key, *value; } g_settings[8];
static int g_nsettings;
static void set(const char *key, const char *value) {
    for (int i = 0; i < g_nsettings; i++)
        if (!strcmp(g_settings[i].key, key)) { g_settings[i].value = value; return; }
    g_settings[g_nsettings].key = key, g_settings[g_nsettings++].value = value;
}
static int setting_get(const char *key, char *out, size_t cap) {
    for (int i = 0; i < g_nsettings; i++)
        if (!strcmp(g_settings[i].key, key) && g_settings[i].value) { snprintf(out, cap, "%s", g_settings[i].value); return 1; }
    return 0;
}
static int no_settings(const char *key, char *out, size_t cap) { (void)key, (void)out, (void)cap; return 0; }

enum { SELF = 0x1111 };
static int g_online, g_npeers;
static uint64_t g_peers[16], g_muted;
static int h_online(void) { return g_online; }
static int h_peers(uint64_t *out, int cap) {
    int n = g_npeers < cap ? g_npeers : cap;
    memcpy(out, g_peers, (size_t)n * 8);
    return n;
}
static int g_nfriends;
static uint64_t g_friends[4];
static int h_friends(uint64_t *out, int cap) {
    int n = g_nfriends < cap ? g_nfriends : cap;
    memcpy(out, g_friends, (size_t)n * 8);
    return n;
}
static int h_muted(uint64_t account) { return account == g_muted; }
static uint64_t h_self(void) { return SELF; }
static const char *h_name(void) { return "Bia_7"; }
static uint64_t h_account_of(const char *name) { return !strcmp(name, "Rafa") ? 0x7afa : 0x9999; }

// ---- the pure parts ------------------------------------------------------------------------------------------------
static void test_gate(void) {
    CHECK(camera_unavailable(3, 1, "1.8.0", 1, NULL) == NULL);
    CHECK(camera_unavailable(4, 1, "1.8.0", 1, "1") == NULL);
    CHECK(camera_unavailable(2, 1, "1.8.0", 1, NULL) != NULL);    // plugin API 2
    CHECK(camera_unavailable(1, 0, "1.8.0", 0, NULL) != NULL);
    CHECK(camera_unavailable(3, 0, "1.8.0", 1, NULL) != NULL);    // version 3 said, the entries missing
    CHECK(camera_unavailable(3, 1, "1.0.0", 1, NULL) != NULL);    // no GameChat in 1.0.0
    CHECK(camera_unavailable(3, 1, NULL, 1, NULL) != NULL);
    CHECK(camera_unavailable(3, 1, "1.8.0", 0, NULL) != NULL);    // no settings: nothing could turn it on
    CHECK(camera_unavailable(3, 1, "1.8.0", 1, "0") != NULL);     // MKWMP_CAMERA=0
    CHECK(strstr(camera_unavailable(2, 0, "1.8.0", 1, NULL), "version 3") != NULL);
}

static void test_settings(void) {
    CameraSettings s;
    camera_settings_read(no_settings, &s);   // nothing set: everything off
    CHECK(!s.consent && !s.camera && !s.view && !s.mirror && !s.device[0]);
    CHECK(!camera_chatting(&s, 1));
    camera_settings_read(NULL, &s);
    CHECK(!s.camera && !s.view && !camera_chatting(&s, 1));
    g_nsettings = 0;
    set("camera", "0"), set("camera_view", "true"), set("camera_mirror", "yes"), set("consent18", "1");
    camera_settings_read(setting_get, &s);   // only "1" is on
    CHECK(s.consent && !s.camera && !s.view && !s.mirror && !camera_chatting(&s, 1));
    set("camera", "1"), set("camera_device", "USB Video Device");
    camera_settings_read(setting_get, &s);
    CHECK(s.camera && !s.view && !strcmp(s.device, "USB Video Device"));
    CHECK(camera_chatting(&s, 1) && !camera_chatting(&s, 0));   // only online
    set("consent18", NULL);
    camera_settings_read(setting_get, &s);
    CHECK(s.camera && !camera_chatting(&s, 1));                 // not without the 18+ consent
    set("consent18", "1"), set("camera", NULL), set("camera_view", "1"), set("camera_mirror", "1");
    camera_settings_read(setting_get, &s);
    CHECK(!s.camera && s.view && s.mirror && camera_chatting(&s, 1));   // watching alone also makes a chat
    g_nsettings = 0;
}

static uint64_t u64_at(const uint8_t *p) { uint64_t v; memcpy(&v, p, 8); return v; }
static int32_t i32_at(const uint8_t *p) { int32_t v; memcpy(&v, p, 4); return v; }

static void test_room(void) {
    uint8_t r[CAMERA_ROOM_SIZE];
    memset(r, 0xee, sizeof r);
    CHECK(camera_room_encode(r, 0xabcdef0123456789ull, 1, NULL, 0) == 0);
    for (int i = 0; i < 0x28; i++) CHECK(r[i] == 0);
    CHECK(u64_at(r + 0x28) == 0xabcdef0123456789ull && r[0x30] == 1 && i32_at(r + 0x38) == 0);
    for (int i = 0x31; i < 0x38; i++) CHECK(r[i] == 0);
    for (int i = 0x3c; i < CAMERA_ROOM_SIZE; i++) CHECK(r[i] == 0);

    // cameras first, in their order; the player, account 0 and a repeat left out
    CameraMember m[] = {{10, 0}, {11, 1}, {0x77, 1}, {0, 1}, {12, 0}, {13, 1}, {11, 0}, {13, 1}};
    CHECK(camera_room_encode(r, 0x77, 0, m, 8) == 4);
    CHECK(r[0x30] == 0 && i32_at(r + 0x38) == 4);
    CHECK(u64_at(r + 0x40) == 11 && r[0x48] == 1 && u64_at(r + 0x50) == 13 && r[0x58] == 1);
    CHECK(u64_at(r + 0x60) == 10 && r[0x68] == 0 && u64_at(r + 0x70) == 12 && r[0x78] == 0);
    for (int i = 0x49; i < 0x50; i++) CHECK(r[i] == 0);
    CHECK(u64_at(r + 0x80) == 0);

    // 14 members, 6 with a camera: 11 written, four flagged, the other two cameras listed without the flag
    CameraMember big[14];
    for (int i = 0; i < 14; i++) big[i].account = 100 + (uint64_t)i, big[i].camera = i % 2 == 0 && i < 12;
    CHECK(camera_room_encode(r, 1, 1, big, 14) == CAMERA_ROOM_MEMBERS);
    CHECK(i32_at(r + 0x38) == 11);
    int flagged = 0;
    for (int i = 0; i < 11; i++) flagged += r[0x40 + i * 0x10 + 8];
    CHECK(flagged == CAMERA_REMOTE);
    CHECK(u64_at(r + 0x40) == 100 && u64_at(r + 0x50) == 102 && u64_at(r + 0x60) == 104 && u64_at(r + 0x70) == 106);
    CHECK(u64_at(r + 0x80) == 101 && r[0x88] == 0);
    CHECK(u64_at(r + 0x40 + 10 * 0x10) != 0);   // the eleventh entry ends exactly at 0xf0
}

// The chat room (chatroom.h): at most four friends in the same game session, the server's grant instead when there is one.
static void test_chatroom(void) {
    const uint64_t friends[] = {1, 2, 3, SELF, 2, 0, 5, 6, 7}, lobby[] = {3, 9, 7, 5, 6, 2};
    ChatRoomInput in = {.self = SELF, .friends = friends, .nfriends = 9, .peers = lobby, .npeers = 6};
    ChatRoom r;
    chatroom_pick(&in, &r);   // the friends racing with the player, in the roster's order; a stranger (9) never
    CHECK(r.n == 4 && !r.granted && r.id == 0 && r.in_lobby == 4);
    CHECK(r.member[0].account == 2 && r.member[1].account == 3 && r.member[2].account == 5 && r.member[3].account == 6);
    CHECK(!r.member[0].camera && !r.member[3].camera);   // whose camera is on: the relay's to say
    in.npeers = 0;
    chatroom_pick(&in, &r);   // friends who are elsewhere: nobody (it was every friend, and before that the whole lobby)
    CHECK(r.n == 0);
    in.npeers = 1, in.loopback = 1;
    chatroom_pick(&in, &r);   // loopback mirrors the session for every friend; the one the relay has comes first
    CHECK(r.n == 4 && r.in_lobby == 1 && r.member[0].account == 3 && r.member[1].account == 1 && r.member[2].account == 2 &&
          r.member[3].account == 5 && r.member[0].camera && r.member[3].camera);
    ChatRoomRequest q;
    in.npeers = 6, in.loopback = 0;
    chatroom_ask(&in, "-4156392711634492312", &q);   // what the server will be asked: the key, the friends wanted
    CHECK(!strcmp(q.key, "-4156392711634492312") && q.n == 4 && q.friends[0] == 2 && q.friends[3] == 6);
    in.npeers = 1;
    chatroom_ask(&in, NULL, &q);   // friends not seen in the lobby are still asked for: the server knows where they are
    CHECK(q.key[0] == 0 && q.n == 4 && q.friends[0] == 3 && q.friends[1] == 1 && q.friends[2] == 2 && q.friends[3] == 5);
    CHECK(!chatroom_installed() && !chatroom_voice_filter() && !chatroom_request(&q));   // no room object yet: nothing filtered
}

// The game's own loop over one camera (1.8.0, 0x2996af0 and 0x2996c50): poll slot[next]'s sync; when done,
// current = next, next advances, and the next image is asked for.
typedef struct { int current, next; uint64_t sync[3][2]; } Game;
static void test_cycle(void) {
    CameraCycle v = {.index = 2, .target = -1};
    Game g = {.current = -1, .next = 0};
    int write, shown[8], n = 0;
    uint32_t serial = 0, in_image[3] = {0};
    CHECK(!camera_sync_ours(g.sync[0]));   // a zero handle is the runtime's to answer (it says done)
    g.current = 0, g.next = 1;
    camera_cycle_request(&v, g.next, 0x55, g.sync[g.next]);
    CHECK(camera_sync_ours(g.sync[1]) && camera_sync_index(g.sync[1]) == 2);
    for (int frame = 0; frame < 40; frame++) {
        if (frame % 5 == 4) serial++;   // a new picture every fifth frame
        int done = camera_cycle_poll(&v, g.sync[g.next], serial, &write);
        if (!done) { CHECK(write == -1); continue; }
        CHECK(write == g.next);                 // the image written is the one asked for ...
        CHECK(write != g.current);              // ... never the one on screen
        in_image[write] = serial;
        g.current = g.next;
        g.next = g.next > 1 ? 0 : g.next + 1;
        camera_cycle_request(&v, g.next, 0x55, g.sync[g.next]);
        CHECK(in_image[g.current] == serial);   // what is shown now is the newest picture
        if (n < 8) shown[n++] = g.current;
    }
    CHECK(n == 8 && serial == 8);               // one advance per new picture, none without
    static const int want[8] = {1, 2, 0, 1, 2, 0, 1, 2};
    CHECK(!memcmp(shown, want, sizeof want));
    // no new picture: the poll stays "not yet", and again
    CHECK(!camera_cycle_poll(&v, g.sync[g.next], serial, &write) && write == -1);
    CHECK(!camera_cycle_poll(&v, g.sync[g.next], 0, &write));
    // the stream stops: the handle the game still holds answers done, writing nothing
    uint64_t old[2] = {g.sync[g.next][0], g.sync[g.next][1]};
    camera_cycle_stop(&v);
    CHECK(camera_cycle_poll(&v, old, serial + 1, &write) == 1 && write == -1);
    // given to another member: an old handle stays done, the new request waits for that member's first picture
    uint64_t h[2];
    camera_cycle_request(&v, 0, 0x66, h);
    CHECK(camera_cycle_poll(&v, old, 9, &write) == 1 && write == -1);
    CHECK(!camera_cycle_poll(&v, h, 0, &write));
    CHECK(camera_cycle_poll(&v, h, 1, &write) == 1 && write == 0);
    CHECK(camera_cycle_poll(&v, h, 1, &write) == 1 && write == -1);   // asked again: done, not written twice
    // a handle of another camera, a decoder's handle
    uint64_t other[2] = {CAMERA_SYNC_MAGIC | 3, v.seq}, decoder[2] = {7, 1234};
    CHECK(camera_cycle_poll(&v, other, 5, &write) == 1 && write == -1);
    CHECK(!camera_sync_ours(decoder));
}

enum { W = CAMERA_W, HT = CAMERA_H };
static uint8_t pic[4][W * HT * 4];
#define PX(b, x, y) ((b) + ((y) * W + (x)) * 4)
static int is(const uint8_t *p, int r, int g, int b) { return p[0] == r && p[1] == g && p[2] == b && p[3] == 255; }

static void test_pattern(void) {
    memset(pic, 0, sizeof pic);
    camera_pattern(pic[0], W, HT, 0, "Bia_7");
    camera_pattern(pic[1], W, HT, 1, "Bia_7");
    camera_pattern(pic[2], W, HT, 0, "Rafa");
    camera_pattern(pic[3], W, HT, 0, "Bia_7");
    CHECK(!memcmp(pic[0], pic[3], sizeof pic[0]));   // the same tick and name: the same picture
    CHECK(memcmp(pic[0], pic[1], sizeof pic[0]));    // it moves
    CHECK(memcmp(pic[0], pic[2], sizeof pic[0]));    // the name is in it
    for (int i = 3; i < W * HT * 4; i += 4) CHECK(pic[0][i] == 255 && pic[1][i] == 255);
    // tick 0: eight bars of 48 columns in the bottom rows: white, yellow, cyan, green, magenta, red, blue, black
    static const uint8_t bars[8][3] = {{191, 191, 191}, {191, 191, 0}, {0, 191, 191}, {0, 191, 0},
                                       {191, 0, 191},   {191, 0, 0},   {0, 0, 191},   {16, 16, 16}};
    for (int i = 0; i < 8; i++) CHECK(is(PX(pic[0], i * 48 + 24, HT - 3), bars[i][0], bars[i][1], bars[i][2]));
    CHECK(is(PX(pic[0], 0, HT - 3), 191, 191, 191) && is(PX(pic[0], 47, HT - 3), 191, 191, 191));
    // one tick later the bars are four columns to the left
    CHECK(is(PX(pic[1], 43, HT - 3), 191, 191, 191) && is(PX(pic[1], 44, HT - 3), 191, 191, 0));
    // the corner mark is top left of the square the game shows (columns 84..300): red in white, and not bottom or right
    CHECK(is(PX(pic[0], 84 + 16, 16), 255, 0, 0) && is(PX(pic[0], 84 + 5, 5), 255, 255, 255));
    CHECK(!is(PX(pic[0], 84 + 16, HT - 17), 255, 0, 0) && !is(PX(pic[0], 300 - 16, 16), 255, 0, 0));
    // the bouncing square: at the top at tick 0, three rows lower a tick later
    CHECK(is(PX(pic[0], W / 2 + HT / 4 + 3, 1), 255, 255, 255) && !is(PX(pic[1], W / 2 + HT / 4 + 3, 1), 255, 255, 255));
    CHECK(is(PX(pic[1], W / 2 + HT / 4 + 3, 3 + 26), 255, 255, 255));
    // the name: white on the dark band across the middle, inside the square the game shows
    int white = 0, dark = 0, outside = 0;
    for (int x = 0; x < W; x++) {
        const uint8_t *p = PX(pic[0], x, HT / 2);
        if (is(p, 255, 255, 255)) white++, outside += x < 84 || x >= 300;
        else if (is(p, 16, 16, 16)) dark++;
    }
    CHECK(white > 10 && dark > 100 && white + dark == W && !outside);
    // 'B' is the first glyph of Bia_7: its left column is solid over all seven rows (scale 4: 28 pixels)
    int x0 = -1;
    for (int x = 0; x < W && x0 < 0; x++)
        if (is(PX(pic[0], x, HT / 2), 255, 255, 255)) x0 = x;
    int solid = 0;
    for (int y = 0; y < HT; y++) solid += is(PX(pic[0], x0, y), 255, 255, 255);
    CHECK(solid == 28);

    camera_tile(pic[1], W, HT, "Bia_7");
    camera_tile(pic[2], W, HT, NULL);
    CHECK(memcmp(pic[1], pic[2], sizeof pic[1]));
    for (int i = 3; i < W * HT * 4; i += 4) CHECK(pic[2][i] == 255);
    CHECK(PX(pic[2], 0, 0)[0] < 64 && PX(pic[2], W - 1, HT - 1)[0] < 64);   // dark, no text
    camera_pattern(NULL, W, HT, 0, "x");   // refused quietly
    camera_pattern(pic[0], 4, 4, 0, "x");
}

// ---- the overrides, as the game calls them ---------------------------------------------------------------------------
static uint8_t g_types[5][0x100];   // the game's Local/RemoteCameraVideoType objects
static CameraHost host(uint32_t api) {
    CameraHost h = {.api_version = api, .game_version = "1.8.0", .native_image_create = api >= 3 ? image_create : NULL,
                    .native_image_touch = api >= 3 ? image_touch : NULL, .setting_get = setting_get, .mod_dir = "",
                    .online = h_online, .peers = h_peers, .friends = h_friends, .muted = h_muted, .self_account = h_self, .self_name = h_name,
                    .account_of = h_account_of, .no_thread = 1};
    return h;
}

static int image_is(uint32_t handle, const uint8_t *rgba) {
    const uint8_t *p = g_img[handle & 0x7fffffffu].pixels;
    for (int y = 0; y < HT; y++)
        if (memcmp(p + y * ROW, rgba + y * W * 4, W * 4)) return 0;
    return 1;
}

static int g_out_calls;
static uint8_t g_out_first[4];
static void frame_out(const uint8_t *rgba, int w, int h, uint64_t ms, void *user) {
    (void)ms;
    if (w == W && h == HT && user == &g_out_calls) g_out_calls++, memcpy(g_out_first, rgba, 4);
}

static void test_api2(void) {
    CameraHost h = host(2);
    CHECK(camera_install(&h) == 0 && g_nov == 0);   // nothing registered: the runtime's own stubs stay
    h = host(3);
    h.game_version = "1.0.0";
    CHECK(camera_install(&h) == 0 && g_nov == 0);
    h = host(3);
    h.native_image_touch = NULL;
    CHECK(camera_install(&h) == 0 && g_nov == 0);
    h = host(3);
    plat_setenv("MKWMP_CAMERA", "0");
    CHECK(camera_install(&h) == 0 && g_nov == 0);
    plat_setenv("MKWMP_CAMERA", NULL);
    camera_refresh();   // harmless when not installed
    CHECK(camera_frame_in(5, pic[0], W, HT) == 0);
}

static void test_overrides(void) {
    plat_setenv("MKWMP_CAMERA_LOOPBACK", "1");
    plat_setenv("MKWMP_CAMERA_MEMBERS", NULL);
    plat_setenv("MKWMP_CAMERA_PATTERN", NULL);
    plat_setenv("MKWMP_CAMERA_TRACE", "1");   // the trace runs through all of this (the game's memory is not there: no state lines)
    CameraHost h = host(3);
    CHECK(camera_install(&h) == CAMERA_OVERRIDES && g_nov == CAMERA_OVERRIDES);
    for (int i = 0; i < g_nov; i++) {
        CHECK(strstr(g_ov[i].name, "nn4chat") || strstr(g_ov[i].name, "TryWaitNativeSync") || strstr(g_ov[i].name, "nn2oe"));
        for (int k = 0; k < i; k++) CHECK(strcmp(g_ov[i].name, g_ov[k].name));
    }

    // boot (0xd321a0): the event, the local camera and four remote ones, three 384x216 images each
    uint64_t event_obj[8] = {0};
    CHECK(call("AttachToRoomInfoChangedEvent", P(event_obj), 0, 0, 0, 0, 0) == 0 && g_stock_attach_calls == 1);
    uint32_t handles[5][3];
    for (int v = 0; v < 5; v++) {
        const char *calc = v ? "CalculateRemoteCameraVideoBufferSize" : "CalculateLocalCameraVideoBufferSize";
        uint64_t size = call(calc, W, HT, 3, 0, 0, 0);
        CHECK(size >= 0x1000 && size % 0x1000 == 0);
        static uint8_t storage[5][0x1000];
        CHECK(call(v ? "InitializeRemoteCameraVideoStorage" : "InitializeLocalCameraVideoStorage", P(g_types[v]), W, HT, 3,
                   P(storage[v]), size) == 0);
        for (int i = 0; i < 3; i++) {
            handles[v][i] = (uint32_t)call(v ? "GetRemoteCameraImageHandle" : "GetLocalCameraImageHandle", P(g_types[v]), (uint64_t)i, 0, 0, 0, 0);
            CHECK(handles[v][i] == (0x80000000u | (uint32_t)(v * 3 + i)));
        }
    }
    CHECK(g_nimg == 15);
    for (int i = 0; i < 15; i++) {   // every image starts as the placeholder, not as whatever the memory held
        CHECK(g_img[i].touches == 1 && g_img[i].pixels[3] == 255 && g_img[i].pixels[(HT - 1) * ROW + (W - 1) * 4 + 3] == 255);
        CHECK(g_img[i].pixels[W * 4] == 0 && g_img[i].pixels[ROW - 1] == 0 && g_img[i].pixels[ROW + 3] == 255);
    }
    // an object the game never initialised: the runtime's kind of handle
    uint8_t stranger[0x100];
    CHECK(call("GetRemoteCameraImageHandle", P(stranger), 0, 0, 0, 0, 0) == ((uint64_t)W << 16 | HT));

    // everything off: no chat, an empty room with the player's own id, and nothing signalled
    g_online = 1;
    camera_refresh();
    CHECK(call("IsChatting", 0, 0, 0, 0, 0, 0) == 0);
    uint8_t room[CAMERA_ROOM_SIZE];
    memset(room, 0xee, sizeof room);
    CHECK(call("GetRoomInfo", P(room), 0, 0, 0, 0, 0) == 0);
    CHECK(u64_at(room + 0x28) == SELF && room[0x30] == 0 && i32_at(room + 0x38) == 0 && g_signals == 0);
    set("camera", "1"), set("camera_device", CAMERA_DEVICE_TEST);
    camera_refresh();
    CHECK(call("IsChatting", 0, 0, 0, 0, 0, 0) == 0);   // no consent yet
    set("consent18", "1");
    g_online = 0;
    camera_refresh();
    CHECK(call("IsChatting", 0, 0, 0, 0, 0, 0) == 0);   // not online

    // online with the camera on: a chat; the room has the local camera and, in loopback with nobody else, one member
    g_online = 1;
    camera_refresh();
    CHECK(call("IsChatting", 0, 0, 0, 0, 0, 0) == 1);
    CHECK(g_signals == 1 && g_signalled == P(event_obj));   // the room changed: the game's event
    CHECK(call("IsChatting", 0, 0, 0, 0, 0, 0) == 1 && g_signals == 1);
    CHECK(call("GetRoomInfo", P(room), 0, 0, 0, 0, 0) == 0);
    CHECK(u64_at(room + 0x28) == SELF && room[0x30] == 1 && i32_at(room + 0x38) == 1 && room[0x48] == 1);
    uint64_t fake = u64_at(room + 0x40);
    CHECK(fake && fake != SELF);

    // loopback with voice chat off (no voice room) and friends: they are the members, by their account ids, cameras on;
    // the made-up member is only for a player with nobody; the player's own id and a repeat are not listed
    g_friends[0] = 0xf1, g_friends[1] = SELF, g_friends[2] = 0xf2, g_friends[3] = 0xf1, g_nfriends = 4;
    camera_refresh();
    CHECK(call("IsChatting", 0, 0, 0, 0, 0, 0) == 1 && g_signals == 2);
    CHECK(call("GetRoomInfo", P(room), 0, 0, 0, 0, 0) == 0 && i32_at(room + 0x38) == 2);
    CHECK(u64_at(room + 0x40) == 0xf1 && room[0x48] == 1 && u64_at(room + 0x50) == 0xf2 && room[0x58] == 1);
    g_nfriends = 0;
    camera_refresh();
    CHECK(call("IsChatting", 0, 0, 0, 0, 0, 0) == 1 && g_signals == 3);
    CHECK(call("GetRoomInfo", P(room), 0, 0, 0, 0, 0) == 0 && i32_at(room + 0x38) == 1 && u64_at(room + 0x40) == fake);

    // the member refresh (0x2996750): the local stream, then the member's
    camera_set_frame_out(frame_out, &g_out_calls);
    CHECK(camera_local_streaming() == 0);
    CHECK(call("StartLocalCameraStreaming", 0, 0, 0, 0, 0, 0) == 0);
    CHECK(camera_local_streaming() == 1);
    CHECK(call("StartRemoteCameraStreaming", P(&fake), 0, 0, 0, 0, 0) == 0);
    uint64_t subs[8];
    CHECK(camera_subscriptions(subs, 8) == 1 && subs[0] == fake);

    // frames: the game's video update over the local camera (0) and the first remote one (1)
    Game g[2] = {{.current = -1, .next = 0}, {.current = -1, .next = 0}};
    int advances[2] = {0, 0}, sequence[2][6];
    for (int frame = 0; frame < 60 && (advances[0] < 6 || advances[1] < 6); frame++) {
        CHECK(call("IsChatting", 0, 0, 0, 0, 0, 0) == 1);
        for (int v = 0; v < 2; v++) {
            int before[3] = {g_img[v * 3].touches, g_img[v * 3 + 1].touches, g_img[v * 3 + 2].touches};
            if (!call("TryWaitNativeSync", P(g[v].sync[g[v].next]), 0, 0, 0, 0, 0)) {
                for (int i = 0; i < 3; i++) CHECK(g_img[v * 3 + i].touches == before[i]);   // not done: nothing written
                continue;
            }
            for (int i = 0; i < 3; i++)   // only the image asked for was written, and it was not on screen
                if (g_img[v * 3 + i].touches != before[i]) CHECK(i == g[v].next && i != g[v].current);
            g[v].current = g[v].next;
            g[v].next = g[v].next > 1 ? 0 : g[v].next + 1;
            if (v) CHECK(call("RequestNextRemoteCameraImage", P(g[v].sync[g[v].next]), P(g_types[v]), (uint64_t)g[v].next, 0, 0, P(&fake)) == 0);
            else CHECK(call("RequestNextLocalCameraImage", P(g[v].sync[g[v].next]), P(g_types[v]), (uint64_t)g[v].next, 0, 0, 0) == 0);
            if (advances[v] < 6) sequence[v][advances[v]] = g[v].current;
            advances[v]++;
        }
        plat_sleep_ms(25);
    }
    static const int order[6] = {0, 1, 2, 0, 1, 2};
    CHECK(advances[0] >= 6 && advances[1] >= 6);
    CHECK(!memcmp(sequence[0], order, sizeof order) && !memcmp(sequence[1], order, sizeof order));
    CHECK(g_stock_trywait_calls == 2);   // each camera's first poll: slot 0's zero handle, the runtime's to answer
    CHECK(g_out_calls >= 3 && g_out_first[3] == 255);   // the local pictures were handed to the transport
    // what is shown is the test pattern with the player's name, on both: the remote one is the loopback
    const uint8_t *local = g_img[g[0].current].pixels, *remote = g_img[3 + g[1].current].pixels;
    CHECK(local[(HT - 3) * ROW + 3] == 255 && is(local + 16 * ROW + (84 + 16) * 4, 255, 0, 0));
    CHECK(is(remote + 16 * ROW + (84 + 16) * 4, 255, 0, 0));
    int white = 0;
    for (int x = 84; x < 300; x++) white += is(local + (HT / 2) * ROW + x * 4, 255, 255, 255);
    CHECK(white > 10);
    // a decoder's sync (the movie player's) goes to the runtime
    uint64_t decoder[2] = {3, 77};
    CHECK(call("TryWaitNativeSync", P(decoder), 0, 0, 0, 0, 0) == 1 && g_stock_trywait_calls == 3);

    // phase 4's entry: a picture for the member the game asked for is the next one shown; for anyone else, refused
    memset(pic[0], 0x40, sizeof pic[0]);
    CHECK(camera_frame_in(0xdead, pic[0], W, HT) == 0);
    CHECK(camera_frame_in(fake, pic[0], W, HT) == 1);
    CHECK(call("TryWaitNativeSync", P(g[1].sync[g[1].next]), 0, 0, 0, 0, 0) == 1);
    CHECK(image_is(handles[1][g[1].next], pic[0]));
    g[1].current = g[1].next, g[1].next = g[1].next > 1 ? 0 : g[1].next + 1;
    CHECK(call("RequestNextRemoteCameraImage", P(g[1].sync[g[1].next]), P(g_types[1]), (uint64_t)g[1].next, 0, 0, P(&fake)) == 0);
    static uint8_t small[64 * 36 * 4];   // another size is scaled
    memset(small, 0x90, sizeof small);
    CHECK(camera_frame_in(fake, small, 64, 36) == 1);
    CHECK(call("TryWaitNativeSync", P(g[1].sync[g[1].next]), 0, 0, 0, 0, 0) == 1);
    memset(pic[1], 0x90, sizeof pic[1]);
    CHECK(image_is(handles[1][g[1].next], pic[1]));
    CHECK(camera_frame_in(fake, NULL, W, HT) == 0 && camera_frame_in(fake, small, 0, 36) == 0);

    // the lobby fills with strangers: they are not in the chat room, nothing changes
    g_peers[0] = 0xa9, g_peers[1] = 0xa2, g_npeers = 2;
    int signals = g_signals;
    camera_refresh();
    CHECK(call("IsChatting", 0, 0, 0, 0, 0, 0) == 1 && g_signals == signals);
    CHECK(call("GetRoomInfo", P(room), 0, 0, 0, 0, 0) == 0 && i32_at(room + 0x38) == 1 && u64_at(room + 0x40) == fake);
    // two of them are friends: the room is theirs (the one the relay has in the lobby first), the made-up member is
    // gone, the event fires
    g_friends[0] = 0xa1, g_friends[1] = 0xa2, g_nfriends = 2;
    camera_refresh();
    CHECK(call("IsChatting", 0, 0, 0, 0, 0, 0) == 1 && g_signals == signals + 1);
    CHECK(call("GetRoomInfo", P(room), 0, 0, 0, 0, 0) == 0 && i32_at(room + 0x38) == 2);
    CHECK(u64_at(room + 0x40) == 0xa2 && room[0x48] == 1 && u64_at(room + 0x50) == 0xa1 && room[0x58] == 1);
    CHECK(chatroom_installed() && chatroom_has(0xa1) && chatroom_has(0xa2) && !chatroom_has(0xa9) && chatroom_voice_filter());
    // the event handler (0x2995ea0): the made-up member left, so its stream stops; the handle it held answers done
    uint64_t held[2] = {g[1].sync[g[1].next][0], g[1].sync[g[1].next][1]};
    CHECK(call("RequestNextRemoteCameraImage", P(held), P(g_types[1]), 2, 0, 0, P(&fake)) == 0);
    CHECK(call("TryWaitNativeSync", P(held), 0, 0, 0, 0, 0) == 0);   // no newer picture of that member
    CHECK(call("StopRemoteCameraStreaming", P(&fake), 0, 0, 0, 0, 0) == 0);
    CHECK(call("TryWaitNativeSync", P(held), 0, 0, 0, 0, 0) == 1);
    CHECK(camera_subscriptions(subs, 8) == 0 && camera_frame_in(fake, pic[0], W, HT) == 0);
    camera_tile(pic[2], W, HT, NULL);
    for (int i = 0; i < 3; i++) CHECK(image_is(handles[1][i], pic[2]));   // nobody's face is left in the images
    // the player mutes a member: the face goes too
    g_muted = 0xa2;
    camera_refresh();
    CHECK(call("GetRoomInfo", P(room), 0, 0, 0, 0, 0) == 0 && i32_at(room + 0x38) == 2);
    CHECK(u64_at(room + 0x40) == 0xa1 && room[0x48] == 1 && u64_at(room + 0x50) == 0xa2 && room[0x58] == 0);
    g_muted = 0;
    // the game's sharing key is what the room is asked of the server with; nothing asks yet
    ChatRoomRequest ask;
    CHECK(!chatroom_request(&ask));
    CHECK(call("SetSharingKey", P("-4156392711634492312"), 0, 0, 0, 0, 0) == 0);
    camera_refresh();
    CHECK(chatroom_request(&ask) && !strcmp(ask.key, "-4156392711634492312") && ask.n == 2 && ask.friends[0] == 0xa2);
    CHECK(call("GetRoomInfo", P(room), 0, 0, 0, 0, 0) == 0 && i32_at(room + 0x38) == 2);   // the key changes nobody
    // the server's room (phase 4) replaces the local pick; five streams asked: the fifth is refused
    CameraMember relay[] = {{0xb1, 1}, {0xb2, 0}};
    camera_members_set(relay, 2);
    camera_refresh();
    CHECK(call("GetRoomInfo", P(room), 0, 0, 0, 0, 0) == 0 && i32_at(room + 0x38) == 2 && u64_at(room + 0x40) == 0xb1);
    CHECK(chatroom_has(0xb2) && !chatroom_has(0xa1));
    ChatRoom granted;
    CHECK(chatroom_get(&granted, NULL) && granted.granted && granted.n == 2);
    for (uint64_t id = 0xc1; id <= 0xc5; id++)
        CHECK(call("StartRemoteCameraStreaming", P(&id), 0, 0, 0, 0, 0) == (id == 0xc5 ? 1u : 0u));
    CHECK(camera_subscriptions(subs, 8) == 4);

    // the camera turned off in the tab while playing: the room says so; watching goes on (camera_view)
    set("camera", "0"), set("camera_view", "1");
    camera_refresh();
    CHECK(call("IsChatting", 0, 0, 0, 0, 0, 0) == 1);
    CHECK(call("GetRoomInfo", P(room), 0, 0, 0, 0, 0) == 0 && room[0x30] == 0 && i32_at(room + 0x38) == 2);
    CHECK(camera_local_streaming() == 0);
    int out_calls = g_out_calls;
    plat_sleep_ms(80);
    CHECK(call("IsChatting", 0, 0, 0, 0, 0, 0) == 1 && g_out_calls == out_calls);   // nothing leaves any more
    CHECK(call("StopLocalCameraStreaming", 0, 0, 0, 0, 0, 0) == 0);
    camera_tile(pic[2], W, HT, "Bia_7");
    CHECK(image_is(handles[0][0], pic[2]) && image_is(handles[0][2], pic[2]));
    // both off, or offline: the chat ends
    set("camera_view", "0");
    camera_refresh();
    CHECK(call("IsChatting", 0, 0, 0, 0, 0, 0) == 0);
    CHECK(call("GetRoomInfo", P(room), 0, 0, 0, 0, 0) == 0 && i32_at(room + 0x38) == 0 && room[0x30] == 0);
    CHECK(call("SetSharingKey", P("1234567890"), 0, 0, 0, 0, 0) == 0 && call("ClearSharingKey", 0, 0, 0, 0, 0, 0) == 0);
    CHECK(!chatroom_request(&ask));   // the game left its session: nothing to ask
    CHECK(call("FinalizeRemoteCameraVideoStorage", P(g_types[4]), 0, 0, 0, 0, 0) == 0);
    // set up again after a finalize: the images made before are used again (they live until the process ends)
    CHECK(call("InitializeRemoteCameraVideoStorage", P(g_types[4]), W, HT, 3, 0, 0x1000) == 0 && g_nimg == 15);
    CHECK(call("GetRemoteCameraImageHandle", P(g_types[4]), 1, 0, 0, 0, 0) == handles[4][1]);
}

// ---- the game's own camera set-up -----------------------------------------------------------------------------------
// The game's side, in this process's memory: the node that asks whether a camera is attached (its is_online input, its
// result bits), the runtime's stand-in camera service (an object followed by its table) and the game's flags.
enum { ABSENT = 0xab5e, QUERY_SLOT = 27 };
static uint32_t g_code_check[0x60 / 4], g_code_query[3], g_code_ret0[2];
static uint64_t g_vtable_slot, g_service[2 + 64], g_service_at, g_global_at;
static uint8_t g_node[0x40], g_node_online, g_global[0x200];
static struct game_addrs g_row;
static GuestFn g_hook;
static uint64_t g_hook_addr;
static int g_queries;

static int rt_override_address(uint64_t addr, GuestFn fn) {
    g_hook_addr = addr, g_hook = fn;
    return 1;
}
// main+0x1ca0390 as far as the hook can tell: the request through the service's slot, then the result bits.
static uint64_t rt_call_original(Cpu *c) {
    uint8_t *node = (uint8_t *)(uintptr_t)c->x[0];
    uint32_t state;
    memcpy(&state, node + 0x18, 4);
    if (state >> 30 < 2) {
        g_queries++;
        int attached = g_service[2 + QUERY_SLOT] == P(g_code_ret0);   // the slot's function returns 0
        state = (state & 0x3fffffffu) | (attached ? 0x80000000u : 0xc0000000u);
        memcpy(node + 0x18, &state, 4);
    }
    return c->x[0] = 0x77;
}
static uint64_t run_check(int online) {
    Cpu c;
    memset(&c, 0, sizeof c);
    memset(g_node + 0x18, 0, 4);
    g_node_online = (uint8_t)online;
    c.x[0] = P(g_node);
    g_hook(&c);
    CHECK(c.x[0] == 0x77);                               // the update's own result
    CHECK(g_service[2 + QUERY_SLOT] == ABSENT);          // nobody else is ever told a camera is attached
    uint32_t state;
    memcpy(&state, g_node + 0x18, 4);
    return state >> 30;
}

static void test_hold(void) {
    CameraHold h = {0};
    CHECK(!camera_hold(&h, 0, 1000) && !camera_hold(&h, 1, 1000) && !h.was_online);   // no hold: nothing
    h.at = 1000;
    CHECK(camera_hold(&h, 0, 1000) && camera_hold(&h, 0, 1000 + CAMERA_HOLD_MS));      // the prompt is open
    CHECK(!camera_hold(&h, 0, 1001 + CAMERA_HOLD_MS) && !h.at);                        // never went online
    h.at = 5000;
    CHECK(camera_hold(&h, 1, 6000) && camera_hold(&h, 1, 6000 + 10 * CAMERA_HOLD_MS)); // online: no clock
    CHECK(!camera_hold(&h, 0, 7000) && !h.at && !h.was_online);                        // left online play
    CHECK(!camera_hold(&h, 0, 8000) && !camera_hold(&h, 1, 8000));
}

// After test_overrides: the camera installed, the player online, consent given, camera and camera_view off.
static void test_setup(void) {
    static const uint32_t check[4] = {0xf9401668, 0xb000d169, 0x39400108, 0xf9460129};
    static const uint32_t query[3] = {0xf9400008, 0xf9406d08, 0xd63f0100}, ret0[2] = {0x2a1f03e0, 0xd65f03c0};
    uint64_t online_at = P(&g_node_online);
    memcpy(g_node + 0x28, &online_at, 8);
    g_service[0] = P(&g_service[2]);
    for (int i = 0; i < 64; i++) g_service[2 + i] = 0x5c4;
    g_service[2 + QUERY_SLOT] = ABSENT;
    g_service_at = P(g_service), g_global_at = P(g_global);
    g_vtable_slot = P(g_code_check);
    memcpy(g_code_query, query, sizeof query), memcpy(g_code_ret0, ret0, sizeof ret0);
    g_row = (struct game_addrs){
        .build = 180, .cam_check_slot = P(&g_vtable_slot), .cam_check_fn = P(g_code_check), .cam_check_at = 0x50,
        .cam_check_online = 0x28, .cam_check_result = 0x18, .cam_service = P(&g_service_at), .cam_query_at = P(g_code_query),
        .cam_query_slot = QUERY_SLOT, .cam_ret0 = P(g_code_ret0), .game_global = P(&g_global_at), .g_flags4 = 0x12c,
        .cam_setup_bit = 11};
    memcpy(g_row.cam_check_words, check, sizeof check);
    memcpy(g_row.cam_query_words, query, sizeof query);
    memcpy(g_row.cam_ret0_words, ret0, sizeof ret0);
    g_rt.override_address = rt_override_address;
    g_rt.call_original = rt_call_original;

    // no row, a build without the prompt, a guard that fails, the switch: not hooked
    struct game_addrs none = {.build = 100};
    CHECK(!camera_setup_install(NULL) && !camera_setup_install(&none) && !g_hook);
    CHECK(!camera_setup_install(&g_row) && !g_hook);            // the node's code is not the code the row is for
    memcpy(g_code_check + 0x50 / 4, check, sizeof check);
    g_code_ret0[0] = 0x52800020;                                // "mov w0, #1": not a function that returns 0
    CHECK(!camera_setup_install(&g_row) && !g_hook);
    g_code_ret0[0] = ret0[0];
    plat_setenv("MKWMP_CAMERA_SETUP", "0");
    CHECK(!camera_setup_install(&g_row) && !g_hook);
    plat_setenv("MKWMP_CAMERA_SETUP", NULL);
    CHECK(camera_setup_install(&g_row) && g_hook && g_hook_addr == P(g_code_check));   // the function the slot holds
    CHECK(camera_setup_install(&g_row));                        // once

    // the camera off in the Camera tab: the game is told what the runtime tells it, no camera, and no chat is held on
    g_online = 0;
    camera_refresh();
    CHECK(run_check(1) == 3 && g_queries == 1);
    camera_refresh();
    CHECK(call("IsChatting", 0, 0, 0, 0, 0, 0) == 0);
    set("camera_view", "1");                                    // watching alone: no face of the player's to set up
    camera_refresh();
    CHECK(run_check(1) == 3 && call("IsChatting", 0, 0, 0, 0, 0, 0) == 0);
    // the camera on: local play's node (the console's own camera) still gets "no camera"
    set("camera", "1"), set("camera_view", "0");
    camera_refresh();
    CHECK(run_check(0) == 3 && call("IsChatting", 0, 0, 0, 0, 0, 0) == 0);
    // Online Play chosen: a camera is attached, and the chat runs at once, before the sign-in, for the prompt
    CHECK(run_check(1) == 2 && g_queries == 4);
    CHECK(call("IsChatting", 0, 0, 0, 0, 0, 0) == 1);
    camera_refresh();
    uint8_t room[CAMERA_ROOM_SIZE];
    CHECK(call("IsChatting", 0, 0, 0, 0, 0, 0) == 1);
    CHECK(call("GetRoomInfo", P(room), 0, 0, 0, 0, 0) == 0 && room[0x30] == 1);
    // the prompt's pages, and its answer in the game's flags (logged; nothing to answer)
    CHECK(call("ShowCameraSharingGuide", 0, 0, 0, 0, 0, 0) == 0 && call("ShowChatStart", P(room), 0, 0, 0, 0, 0) == 0);
    g_global[0x12d] = 0x08;                                     // bit 11
    plat_sleep_ms(260);
    CHECK(call("IsChatting", 0, 0, 0, 0, 0, 0) == 1);
    // signed in, then left online play: the chat ends with it (and the game drops the answer)
    g_online = 1;
    camera_refresh();
    CHECK(call("IsChatting", 0, 0, 0, 0, 0, 0) == 1);
    g_online = 0;
    camera_refresh();
    CHECK(call("IsChatting", 0, 0, 0, 0, 0, 0) == 0);
    // a service object that is not the runtime's stand-in (its table elsewhere) is left alone
    static uint64_t table[64];
    table[QUERY_SLOT] = ABSENT;
    g_service[0] = P(table);
    CHECK(run_check(1) == 3 && table[QUERY_SLOT] == ABSENT && call("IsChatting", 0, 0, 0, 0, 0, 0) == 0);
    g_service[0] = P(&g_service[2]);
    set("camera", "0");
    camera_refresh();
}

int main(int argc, char **argv) {
    g_rt.override = rt_override;
    g_rt.guest_modules = rt_guest_modules;
    g_rt.guest_symbol = rt_guest_symbol;
    g_rt.log = rt_log;
    g_rt.call = rt_call;
    g_rt.stock = rt_stock;
    test_gate();
    test_settings();
    test_room();
    test_chatroom();
    test_cycle();
    test_pattern();
    test_hold();
    if (argc > 1 && !strcmp(argv[1], "api2")) {
        test_api2();
        CHECK(!camera_setup_install(NULL));   // not installed: no hook either
        camera_game_loaded();
    } else {
        test_overrides();
        test_setup();
    }
    printf("test_camera%s: %s\n", argc > 1 ? " api2" : "", failures ? "FAIL" : "PASS");
    return failures != 0;
}
