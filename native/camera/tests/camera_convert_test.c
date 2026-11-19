// camera_convert.h: the centre crop, the four source formats, bottom-up rows, the mirror, and bad arguments.
//
//   python native/camera/build.py --test
// or by hand:
//   zig cc -O2 -Wall -Wextra -Werror -Inative/camera/src -o camera_convert_test native/camera/tests/camera_convert_test.c
#include "camera_convert.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int failures;
#define CHECK(c) do { if (!(c)) { printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #c); failures++; } } while (0)

static int near(int a, int b) { return abs(a - b) <= 3; }
static int is(const uint8_t *p, int r, int g, int b) { return near(p[0], r) && near(p[1], g) && near(p[2], b) && p[3] == 255; }

enum { DW = 384, DH = 216 };
static uint8_t dst[DW * DH * 4];
#define AT(x, y) (dst + ((y) * DW + (x)) * 4)

int main(void) {
    int x0, y0, cw, ch;
    cam_crop(640, 480, 384, 216, &x0, &y0, &cw, &ch);   // 4:3 -> 16:9: top and bottom cut
    CHECK(x0 == 0 && cw == 640 && ch == 360 && y0 == 60);
    cam_crop(1280, 720, 384, 216, &x0, &y0, &cw, &ch);
    CHECK(x0 == 0 && y0 == 0 && cw == 1280 && ch == 720);
    cam_crop(720, 1280, 384, 216, &x0, &y0, &cw, &ch);  // a phone held upright
    CHECK(x0 == 0 && cw == 720 && ch == 405 && y0 == 437);
    cam_crop(1000, 100, 384, 216, &x0, &y0, &cw, &ch);  // very wide: sides cut
    CHECK(ch == 100 && cw == 177 && x0 == 411 && y0 == 0);

    // BGRA 640x480, top-down: left half red, right half blue; the top 60 rows (cropped away) green
    enum { SW = 640, SH = 480 };
    static uint8_t bgra[SW * SH * 4];
    for (int y = 0; y < SH; y++)
        for (int x = 0; x < SW; x++) {
            uint8_t *p = bgra + (y * SW + x) * 4;
            int r = y >= 60 && x < SW / 2 ? 255 : 0, g = y < 60 ? 255 : 0, b = y >= 60 && x >= SW / 2 ? 255 : 0;
            p[0] = (uint8_t)b, p[1] = (uint8_t)g, p[2] = (uint8_t)r, p[3] = 0;
        }
    CHECK(cam_convert(bgra, sizeof bgra, CAMFMT_BGRA, SW, SH, SW * 4, 0, dst, DW, DH, 0));
    CHECK(is(AT(0, 0), 255, 0, 0) && is(AT(100, 108), 255, 0, 0) && is(AT(300, 108), 0, 0, 255) && is(AT(383, 215), 0, 0, 255));
    CHECK(cam_convert(bgra, sizeof bgra, CAMFMT_BGRA, SW, SH, SW * 4, 0, dst, DW, DH, 1));   // mirrored
    CHECK(is(AT(0, 0), 0, 0, 255) && is(AT(383, 215), 255, 0, 0));
    // the same bytes read as a bottom-up DIB: the green band is at the bottom now, and also cropped away
    CHECK(cam_convert(bgra, sizeof bgra, CAMFMT_BGRA, SW, SH, SW * 4, 1, dst, DW, DH, 0));
    CHECK(is(AT(10, 5), 255, 0, 0) && is(AT(10, 210), 255, 0, 0));

    // BGR24 with padded rows (width 642: 1926 bytes, stride 1928), bottom-up: top half white, bottom half black
    enum { TW = 642, TH = 362 };
    int stride = cam_stride(CAMFMT_BGR24, TW);
    CHECK(stride == 1928);
    static uint8_t bgr[1928 * TH];
    for (int y = 0; y < TH; y++)   // y = the row in memory; memory row 0 is the picture's last row
        memset(bgr + y * stride, y >= TH / 2 ? 255 : 0, (size_t)TW * 3);
    CHECK(cam_convert(bgr, sizeof bgr, CAMFMT_BGR24, TW, TH, stride, 1, dst, DW, DH, 0));
    CHECK(is(AT(5, 5), 255, 255, 255) && is(AT(380, 100), 255, 255, 255) && is(AT(5, 115), 0, 0, 0) && is(AT(380, 210), 0, 0, 0));

    // YUY2 640x360: video-range white (235,128,128), black (16,128,128) and red (81,90,240) thirds, left to right
    enum { YW = 640, YH = 360 };
    static uint8_t yuy2[YW * YH * 2];
    for (int y = 0; y < YH; y++)
        for (int x = 0; x < YW; x += 2) {
            uint8_t *p = yuy2 + y * YW * 2 + x * 2;
            int third = x * 3 / YW;
            p[0] = p[2] = third == 0 ? 235 : third == 1 ? 16 : 81;
            p[1] = third == 2 ? 90 : 128;
            p[3] = third == 2 ? 240 : 128;
        }
    CHECK(cam_convert(yuy2, sizeof yuy2, CAMFMT_YUY2, YW, YH, YW * 2, 0, dst, DW, DH, 0));
    CHECK(is(AT(20, 100), 255, 255, 255) && is(AT(192, 100), 0, 0, 0) && is(AT(370, 100), 255, 0, 0));

    // NV12 640x360: top half blue (41,240,110), bottom half green (145,54,34)
    static uint8_t nv12[YW * YH * 3 / 2];
    for (int y = 0; y < YH; y++) memset(nv12 + y * YW, y < YH / 2 ? 41 : 145, YW);
    for (int y = 0; y < YH / 2; y++)
        for (int x = 0; x < YW; x += 2) {
            nv12[YW * YH + y * YW + x] = y < YH / 4 ? 240 : 54;
            nv12[YW * YH + y * YW + x + 1] = y < YH / 4 ? 110 : 34;
        }
    CHECK(cam_convert(nv12, sizeof nv12, CAMFMT_NV12, YW, YH, YW, 0, dst, DW, DH, 0));
    CHECK(is(AT(50, 20), 0, 0, 255) && is(AT(300, 200), 0, 255, 0));

    // refused: a short buffer, a stride below the row, a format unknown, a picture of nothing
    memset(dst, 7, sizeof dst);
    CHECK(!cam_convert(nv12, sizeof nv12 - 1, CAMFMT_NV12, YW, YH, YW, 0, dst, DW, DH, 0));
    CHECK(!cam_convert(bgra, sizeof bgra, CAMFMT_BGRA, SW, SH, SW * 3, 0, dst, DW, DH, 0));
    CHECK(!cam_convert(bgra, sizeof bgra, 9, SW, SH, SW * 4, 0, dst, DW, DH, 0));
    CHECK(!cam_convert(bgra, sizeof bgra, CAMFMT_BGRA, 0, SH, SW * 4, 0, dst, DW, DH, 0));
    CHECK(!cam_convert(NULL, sizeof bgra, CAMFMT_BGRA, SW, SH, SW * 4, 0, dst, DW, DH, 0));
    CHECK(dst[0] == 7 && dst[sizeof dst - 1] == 7);
    // a source smaller than the target is scaled up
    static const uint8_t tiny[2 * 2 * 4] = {10, 20, 30, 0, 10, 20, 30, 0, 10, 20, 30, 0, 10, 20, 30, 0};
    CHECK(cam_convert(tiny, sizeof tiny, CAMFMT_BGRA, 2, 2, 8, 0, dst, DW, DH, 0) && is(AT(200, 100), 30, 20, 10));

    printf("camera_convert_test: %s\n", failures ? "FAIL" : "PASS");
    return failures != 0;
}
