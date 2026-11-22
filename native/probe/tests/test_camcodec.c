// The camera's codec (camcodec.h): the JPEG round trip (how close the picture comes back, at the size sent and at
// odd sizes), a frame's size and the encoder's way down and back up to keep it under 6 KiB, the frame rate and its
// divisor, the time a frame takes, and a decoder that is handed cut, corrupted and foreign streams.
//
//   $zig = "C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"; $s = "native\probe\src"
//   & $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o scratch\test_camcodec.exe `
//       native\probe\tests\test_camcodec.c $s\camcodec.c $s\camera.c $s\chatroom.c $s\camlib.c $s\gameaddrs.c `
//       $s\guesthook.c $s\log.c $s\plat_win.c
//   scratch\test_camcodec.exe
// (the fuzzing part is worth a run with -O1 -fsanitize=undefined,bounds too.)
// Against another codec, by hand: `test_camcodec.exe write a.jpg` writes a frame for any viewer to open, and
// `test_camcodec.exe read b.jpg out.rgba` decodes somebody else's baseline JPEG (up to 384x216) to 384x216 RGBA.
#include "../src/camcodec.h"
#include "../src/camera.h"
#include "../src/plat.h"
#include "../src/rtapi.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

RtApi g_rt;
static void rt_log(const char *fmt, ...) { (void)fmt; }

static int failures;
#define CHECK(c) do { if (!(c)) { printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #c); failures++; } } while (0)

enum { W = CAMCODEC_OUT_W, H = CAMCODEC_OUT_H, SW = CAMCODEC_W, SH = CAMCODEC_H };
static uint8_t pattern[W * H * 4], face[W * H * 4], noise[W * H * 4], back[W * H * 4], small[W * H * 4], small_back[W * H * 4];
static uint8_t jpeg[CAMCODEC_FRAME_MAX], big[256 * 1024];

static uint32_t g_seed = 12345;
static uint32_t rnd(void) { return g_seed = g_seed * 1664525u + 1013904223u; }

// Something like a webcam's picture: soft gradients, a round "face", a little sensor noise. tick moves it.
static void make_face(uint8_t *rgba, int w, int h, int tick) {
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++) {
            uint8_t *p = rgba + (y * w + x) * 4;
            int dx = x - w / 2 - tick, dy = y - h / 2, inside = dx * dx + dy * dy < (h / 3) * (h / 3);
            int n = (int)(rnd() >> 28) - 8;   // -8..7
            p[0] = (uint8_t)((inside ? 200 - dy / 2 : 40 + x * 60 / w) + n);
            p[1] = (uint8_t)((inside ? 150 - dy / 2 : 60 + y * 80 / h) + n);
            p[2] = (uint8_t)((inside ? 120 : 90 + (x + y) * 50 / (w + h)) + n);
            p[3] = 255;
        }
}

static double psnr(const uint8_t *a, const uint8_t *b, int pixels) {
    double sum = 0;
    for (int i = 0; i < pixels * 4; i++)
        if (i % 4 != 3) sum += (double)(a[i] - b[i]) * (a[i] - b[i]);
    if (sum == 0) return 99;
    return 10 * log10(255.0 * 255.0 / (sum / (pixels * 3)));
}
static int close_to(const uint8_t *p, int r, int g, int b, int by) { return abs(p[0] - r) <= by && abs(p[1] - g) <= by && abs(p[2] - b) <= by && p[3] == 255; }

static void test_round_trip(void) {
    int w = 0, h = 0;
    // the size that is sent, a webcam-like picture, the quality the encoder starts at
    make_face(small, SW, SH, 0);
    int n = camcodec_jpeg_encode(small, SW, SH, CAMCODEC_QUALITY, jpeg, sizeof jpeg);
    CHECK(n > 700 && n <= CAMCODEC_TARGET && jpeg[0] == 0xff && jpeg[1] == 0xd8 && jpeg[n - 2] == 0xff && jpeg[n - 1] == 0xd9);
    CHECK(camcodec_jpeg_decode(jpeg, n, small_back, &w, &h) && w == SW && h == SH);
    double db = psnr(small, small_back, SW * SH);
    printf("  256x144 webcam-like picture at quality %d: %d bytes, PSNR %.1f dB\n", CAMCODEC_QUALITY, n, db);
    CHECK(db > 30);
    for (int i = 3; i < SW * SH * 4; i += 4) CHECK(small_back[i] == 255);
    // a higher quality is a larger and a closer picture
    int n90 = camcodec_jpeg_encode(small, SW, SH, 90, big, sizeof big);
    CHECK(n90 > n && camcodec_jpeg_decode(big, n90, small_back, &w, &h) && psnr(small, small_back, SW * SH) > db + 3);
    // flat colours come back as they went (the test pattern's bars, the corner mark)
    camera_pattern(pattern, W, H, 0);
    camcodec_scale(pattern, W, H, small, SW, SH);
    n = camcodec_jpeg_encode(small, SW, SH, CAMCODEC_QUALITY, jpeg, sizeof jpeg);
    CHECK(n && camcodec_jpeg_decode(jpeg, n, small_back, &w, &h));
    printf("  256x144 test pattern at quality %d: %d bytes, PSNR %.1f dB\n", CAMCODEC_QUALITY, n, psnr(small, small_back, SW * SH));
    static const int bars[8][3] = {{191, 191, 191}, {191, 191, 0}, {0, 191, 191}, {0, 191, 0}, {191, 0, 191}, {191, 0, 0}, {0, 0, 191}, {16, 16, 16}};
    for (int i = 0; i < 8; i++) CHECK(close_to(small_back + ((SH - 6) * SW + i * 32 + 16) * 4, bars[i][0], bars[i][1], bars[i][2], 12));
    // sizes that are not whole 16x16 blocks, down to one pixel, and the largest: the edges are right too
    static const int sizes[][2] = {{384, 216}, {250, 141}, {17, 9}, {8, 8}, {1, 1}, {3, 200}, {384, 1}};
    for (int i = 0; i < 7; i++) {
        int sw = sizes[i][0], sh = sizes[i][1];
        for (int k = 0; k < sw * sh; k++) {   // a smooth ramp, red across and green down
            uint8_t *p = face + k * 4;
            p[0] = (uint8_t)(40 + (k % sw) * 180 / sw), p[1] = (uint8_t)(40 + (k / sw) * 180 / sh), p[2] = 90, p[3] = 255;
        }
        n = camcodec_jpeg_encode(face, sw, sh, 75, big, sizeof big);
        CHECK(n > 0 && camcodec_jpeg_decode(big, n, back, &w, &h) && w == sw && h == sh);
        // (a ramp a few pixels long is all edge to 4:2:0 chroma: less is asked of the tiny ones)
        CHECK(psnr(face, back, sw * sh) > (sw >= 32 && sh >= 32 ? 40 : sw >= 32 || sh < 32 ? 29 : 25));
        CHECK(close_to(back + ((sh - 1) * sw + sw - 1) * 4, face[((sh - 1) * sw + sw - 1) * 4], face[((sh - 1) * sw + sw - 1) * 4 + 1], 90, 14));
    }
    // refused: nothing, a size over the largest, a buffer too small
    CHECK(!camcodec_jpeg_encode(NULL, SW, SH, 40, jpeg, sizeof jpeg) && !camcodec_jpeg_encode(small, 385, 216, 40, big, sizeof big));
    CHECK(!camcodec_jpeg_encode(small, SW, 0, 40, jpeg, sizeof jpeg) && !camcodec_jpeg_encode(small, SW, SH, 40, jpeg, 1000));
}

// The whole path: a 384x216 picture in, a frame, a 384x216 picture out.
static void test_frames(void) {
    static CamEncoder e;
    camcodec_encoder_init(&e);
    CHECK(e.quality == CAMCODEC_QUALITY && e.divisor == 1);
    make_face(face, W, H, 0);
    int n = camcodec_encode(&e, face, 1000, jpeg, sizeof jpeg);
    CHECK(n > 0 && n <= CAMCODEC_TARGET && e.frames == 1 && e.last_bytes == n);
    CHECK(camcodec_decode(jpeg, n, back));
    double db = psnr(face, back, W * H);
    printf("  384x216 -> 256x144 JPEG -> 384x216, webcam-like: %d bytes, PSNR %.1f dB\n", n, db);
    CHECK(db > 27);   // the sensor noise does not survive 256x144; the picture does
    for (int i = 3; i < W * H * 4; i += 4) CHECK(back[i] == 255);
    // the frame's own size is what the decoder goes by: SOF0 says 256x144
    int w = 0, h = 0;
    CHECK(camcodec_jpeg_decode(jpeg, n, back, &w, &h) && w == SW && h == SH);

    // a picture no quality 40 fits in 6 KiB: the encoder goes down until it does, and every frame it gives fits
    for (int i = 0; i < W * H * 4; i++) noise[i] = i % 4 == 3 ? 255 : (uint8_t)(rnd() >> 24);
    uint64_t ms = 2000;
    int worst = 0, lowest = 100;
    for (int i = 0; i < 20; i++, ms += 100) {
        for (int k = 0; k < 4000; k++) noise[(rnd() % (W * H)) * 4] = (uint8_t)rnd();
        n = camcodec_encode(&e, noise, ms, jpeg, sizeof jpeg);
        CHECK(n > 0 && n <= CAMCODEC_TARGET && camcodec_decode(jpeg, n, back));
        if (n > worst) worst = n;
        if (e.last_quality < lowest) lowest = e.last_quality;
    }
    printf("  noise (the worst case): quality down to %d, the largest frame %d bytes, %u of %u frames encoded twice or more\n",
           lowest, worst, (unsigned)e.over, (unsigned)e.frames);
    CHECK(lowest < CAMCODEC_QUALITY && lowest >= CAMCODEC_QUALITY_MIN && e.over >= 1 && e.over < e.frames / 2);
    // an easy picture again: the quality comes back up to where it started, a step a frame, and no further
    for (int i = 0; i < 40; i++, ms += 100) {
        make_face(face, W, H, i);
        n = camcodec_encode(&e, face, ms, jpeg, sizeof jpeg);
        CHECK(n > 0 && n <= CAMCODEC_TARGET);
    }
    CHECK(e.quality == CAMCODEC_QUALITY && e.last_quality == CAMCODEC_QUALITY);

    // the frame rate: pictures come 15 a second (the capture's rate) for three seconds; 10, 5 and 2.5 a second leave
    static const int want[3] = {30, 15, 8};
    for (int d = 0; d < 3; d++) {
        camcodec_encoder_init(&e);
        camcodec_encoder_divisor(&e, 1 << d);
        int sent = 0;
        for (int i = 0; i < 45; i++) sent += camcodec_encode(&e, face, 5000 + (uint64_t)i * 1000 / 15, jpeg, sizeof jpeg) > 0;
        printf("  45 pictures in 3 s at the divisor %d: %d frames\n", 1 << d, sent);
        CHECK(sent >= want[d] - 1 && sent <= want[d] + 1);
    }
    camcodec_encoder_divisor(&e, 3);
    CHECK(e.divisor == 1);
    // pictures that come slower than the rate all go; after a pause the first one goes at once
    camcodec_encoder_init(&e);
    int sent = 0;
    for (int i = 0; i < 10; i++) sent += camcodec_encode(&e, face, 9000 + (uint64_t)i * 250, jpeg, sizeof jpeg) > 0;
    CHECK(sent == 10);
    CHECK(!camcodec_encode(&e, face, 9000 + 9 * 250 + 20, jpeg, sizeof jpeg) && camcodec_encode(&e, face, 60000, jpeg, sizeof jpeg) > 0);
    CHECK(!camcodec_encode(&e, face, 60001, jpeg, 100));   // a buffer too small for a frame
}

static void test_timing(void) {
    static CamEncoder e;
    enum { N = 300 };
    static uint8_t frames[8][CAMCODEC_TARGET];
    int len[8], total = 0;
    camcodec_encoder_init(&e);
    for (int i = 0; i < 8; i++) {   // warm, and the frames to decode
        make_face(face, W, H, i * 3);
        len[i] = camcodec_encode(&e, face, (uint64_t)i * 1000, frames[i], sizeof frames[i]);
        CHECK(len[i] > 0);
    }
    uint64_t t0 = plat_ms();
    for (int i = 0; i < N; i++) total += camcodec_encode(&e, face, 100000 + (uint64_t)i * 1000, jpeg, sizeof jpeg);
    uint64_t t1 = plat_ms();
    for (int i = 0; i < N; i++) CHECK(camcodec_decode(frames[i & 7], len[i & 7], back));
    uint64_t t2 = plat_ms();
    double enc = (double)(t1 - t0) / N, dec = (double)(t2 - t1) / N;
    printf("  timing over %d frames: encode (scale 384x216 -> 256x144 + JPEG) %.2f ms, decode (JPEG + scale to 384x216) %.2f ms; "
           "%d bytes a frame, %.1f KiB/s at 10 fps\n", N, enc, dec, total / N, total / N * 10 / 1024.0);
    CHECK(total / N <= CAMCODEC_TARGET);
    CHECK(enc < 8 && dec < 8);   // a frame of the game is 16 ms; ten of these a second
}

// What comes from the network is not ours: nothing may crash, and what is not a picture is refused.
static void test_hostile(void) {
    int w, h;
    make_face(small, SW, SH, 0);
    int n = camcodec_jpeg_encode(small, SW, SH, CAMCODEC_QUALITY, jpeg, sizeof jpeg);
    CHECK(n > 0);
    // cut short anywhere: refused, until only the end marker is missing
    int taken = 0;
    for (int cut = 0; cut < n; cut++) {
        uint8_t *copy = malloc((size_t)cut + 1);   // its own block: a read past the cut is a read past the heap block
        memcpy(copy, jpeg, (size_t)cut);
        int ok = camcodec_jpeg_decode(copy, cut, back, &w, &h);
        if (ok) { taken++; CHECK(cut >= n - 2 && w == SW && h == SH); }
        free(copy);
    }
    CHECK(taken <= 2);
    // corrupted: any answer, no crash, and a picture only of a size that fits
    int decoded = 0;
    for (int round = 0; round < 6000; round++) {
        memcpy(big, jpeg, (size_t)n);
        int flips = 1 + (int)(rnd() % 6);
        for (int k = 0; k < flips; k++) {
            int at = round % 3 ? (int)(rnd() % (uint32_t)n) : (int)(rnd() % 640);   // a third of them in the headers
            big[at] = round % 2 ? (uint8_t)rnd() : (uint8_t)(big[at] ^ (1u << (rnd() % 8)));
        }
        w = h = 0;
        if (camcodec_jpeg_decode(big, n, back, &w, &h)) {
            decoded++;
            CHECK(w >= 1 && w <= W && h >= 1 && h <= H);
        }
    }
    CHECK(decoded > 0 && decoded < 6000);
    // random bytes behind a JPEG's first two
    for (int round = 0; round < 2000; round++) {
        for (int i = 2; i < 600; i++) big[i] = (uint8_t)rnd();
        big[0] = 0xff, big[1] = 0xd8;
        camcodec_jpeg_decode(big, 600, back, &w, &h);
    }
    // not ours to read: larger than 384x216 (the size is refused before anything is allocated), progressive, 12 bit,
    // four components, a table missing, no start marker, nothing
    memcpy(big, jpeg, (size_t)n);
    int sof = 0;
    for (int i = 2; i < n - 9 && !sof; i++)
        if (big[i] == 0xff && big[i + 1] == 0xc0) sof = i;
    CHECK(sof && big[sof + 5] == 0 && big[sof + 6] == SH && big[sof + 7] == SW >> 8 && big[sof + 8] == (SW & 0xff));
    big[sof + 7] = 0x01, big[sof + 8] = 0x81;   // 385 wide
    CHECK(!camcodec_jpeg_decode(big, n, back, &w, &h));
    big[sof + 7] = 0xff, big[sof + 8] = 0xff, big[sof + 5] = 0xff, big[sof + 6] = 0xff;
    CHECK(!camcodec_jpeg_decode(big, n, back, &w, &h));
    big[sof + 5] = 0, big[sof + 6] = 0, big[sof + 7] = 0, big[sof + 8] = 0;   // no size at all
    CHECK(!camcodec_jpeg_decode(big, n, back, &w, &h));
    memcpy(big, jpeg, (size_t)n), big[sof + 1] = 0xc2;
    CHECK(!camcodec_jpeg_decode(big, n, back, &w, &h));
    memcpy(big, jpeg, (size_t)n), big[sof + 4] = 12;
    CHECK(!camcodec_jpeg_decode(big, n, back, &w, &h));
    memcpy(big, jpeg, (size_t)n), big[sof + 9] = 4;
    CHECK(!camcodec_jpeg_decode(big, n, back, &w, &h));
    memcpy(big, jpeg, (size_t)n), big[2 + 18 + 4] = 3;   // the first quantisation table gets another number
    CHECK(!camcodec_jpeg_decode(big, n, back, &w, &h));
    memcpy(big, jpeg, (size_t)n), big[1] = 0xd9;
    CHECK(!camcodec_jpeg_decode(big, n, back, &w, &h));
    CHECK(!camcodec_jpeg_decode(jpeg, 0, back, &w, &h) && !camcodec_jpeg_decode(NULL, n, back, &w, &h) && !camcodec_decode(jpeg, 3, back));
    CHECK(camcodec_jpeg_decode(jpeg, n, back, &w, &h));   // and the frame itself is still one
}

// A baseline JPEG as other encoders write it: grey (one component), 4:4:4 and 4:2:2, restart markers. Made here by
// rewriting what the encoder wrote would not prove much, so these are checked by hand against the system's codec
// (`read`); what is checked here is the restart path, with a stream whose scan is cut into intervals.
static void test_restart(void) {
    // an 8x8 grey picture is one block: two of them side by side with DRI 1 and RST0 between, written by hand from
    // the encoder's own bytes of two one-block pictures
    uint8_t one[64 * 4], a[1200], b[1200];
    int w, h;
    for (int i = 0; i < 64; i++) one[i * 4] = one[i * 4 + 1] = one[i * 4 + 2] = 200, one[i * 4 + 3] = 255;
    int na = camcodec_jpeg_encode(one, 8, 8, 90, a, sizeof a);
    for (int i = 0; i < 64; i++) one[i * 4] = one[i * 4 + 1] = one[i * 4 + 2] = 40;
    int nb = camcodec_jpeg_encode(one, 8, 8, 90, b, sizeof b);
    CHECK(na > 0 && nb > 0);
    int sos_a = 0, sos_b = 0, sof = 0;
    for (int i = 2; i < na - 1; i++) {
        if (a[i] == 0xff && a[i + 1] == 0xc0 && !sof) sof = i;
        if (a[i] == 0xff && a[i + 1] == 0xda) sos_a = i;
    }
    for (int i = 2; i < nb - 1; i++)
        if (b[i] == 0xff && b[i + 1] == 0xda) sos_b = i;
    CHECK(sof && sos_a && sos_b);
    // the two scans' data (after the 14-byte SOS, before EOI); each picture is one MCU of 16x16 (4:2:0)
    uint8_t out[2600];
    int n = 0;
    memcpy(out, a, (size_t)sos_a), n = sos_a;
    out[sof + 7] = 0, out[sof + 8] = 32;   // 32 wide: two MCUs
    static const uint8_t dri[6] = {0xff, 0xdd, 0, 4, 0, 1};
    memcpy(out + n, dri, 6), n += 6;
    memcpy(out + n, a + sos_a, (size_t)(na - 2 - sos_a)), n += na - 2 - sos_a;
    out[n++] = 0xff, out[n++] = 0xd0;
    memcpy(out + n, b + sos_b + 14, (size_t)(nb - 2 - sos_b - 14)), n += nb - 2 - sos_b - 14;
    out[n++] = 0xff, out[n++] = 0xd9;
    CHECK(camcodec_jpeg_decode(out, n, back, &w, &h) && w == 32 && h == 8);
    CHECK(close_to(back + (4 * 32 + 4) * 4, 200, 200, 200, 4) && close_to(back + (4 * 32 + 20) * 4, 40, 40, 40, 4));
    out[n - 2 - (nb - 2 - sos_b - 14) - 1] = 0xd1;   // the wrong restart number
    CHECK(!camcodec_jpeg_decode(out, n, back, &w, &h));
}

// camera.h's ends of the codec, as far as they go without a game (test_camera.c drives them with one).
static void test_glue(void) {
    make_face(small, SW, SH, 0);
    int n = camcodec_jpeg_encode(small, SW, SH, CAMCODEC_QUALITY, jpeg, sizeof jpeg);
    CHECK(camera_jpeg_in(0x1234, jpeg, n) == 0);   // nobody asked for that account's pictures: not even decoded
    CHECK(camera_jpeg_in(0x1234, NULL, n) == 0 && camera_jpeg_in(0x1234, jpeg, CAMERA_JPEG_MAX + 1) == 0);
    camera_set_send_divisor(4), camera_set_send_divisor(7), camera_set_jpeg_out(NULL, NULL);
}

int main(int argc, char **argv) {
    g_rt.log = rt_log;
    if (argc == 3 && !strcmp(argv[1], "write")) {
        static CamEncoder e;
        camcodec_encoder_init(&e);
        camera_pattern(pattern, W, H, 7);
        int n = camcodec_encode(&e, pattern, 1, jpeg, sizeof jpeg);
        FILE *f = fopen(argv[2], "wb");
        if (!f || !n || fwrite(jpeg, 1, (size_t)n, f) != (size_t)n) return 1;
        fclose(f);
        printf("%s: %d bytes\n", argv[2], n);
        return 0;
    }
    if (argc == 4 && !strcmp(argv[1], "read")) {
        FILE *f = fopen(argv[2], "rb");
        if (!f) return 1;
        int n = (int)fread(big, 1, sizeof big, f), w = 0, h = 0;
        fclose(f);
        if (!camcodec_jpeg_decode(big, n, back, &w, &h) || !camcodec_decode(big, n, face)) { printf("%s: refused\n", argv[2]); return 1; }
        f = fopen(argv[3], "wb");
        if (!f || fwrite(face, 1, sizeof face, f) != sizeof face) return 1;
        fclose(f);
        printf("%s: %dx%d, written as %dx%d RGBA\n", argv[2], w, h, W, H);
        return 0;
    }
    test_round_trip();
    test_frames();
    test_timing();
    test_hostile();
    test_restart();
    test_glue();
    printf("test_camcodec: %s\n", failures ? "FAIL" : "PASS");
    return failures != 0;
}
