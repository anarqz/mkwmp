/* A captured picture (BGRA, BGR24, YUY2 or NV12, as DirectShow and V4L2 hand them out) to the RGBA picture the game
 * shows: cropped around the centre to the target's aspect ratio, scaled (four samples per target pixel), mirrored on
 * request. Pure functions, unit-tested (tests/camera_convert_test.c). */
#ifndef MKWMP_CAMERA_CONVERT_H
#define MKWMP_CAMERA_CONVERT_H

#include <stddef.h>
#include <stdint.h>

enum { CAMFMT_BGRA, CAMFMT_BGR24, CAMFMT_YUY2, CAMFMT_NV12 };

/* Bytes between rows of a w-pixel-wide picture as DirectShow lays it out (rows padded to 4 bytes). */
static inline int cam_stride(int fmt, int w) {
    switch (fmt) {
    case CAMFMT_BGRA: return w * 4;
    case CAMFMT_BGR24: return (w * 3 + 3) & ~3;
    case CAMFMT_YUY2: return (w * 2 + 3) & ~3;
    default: return w; /* NV12: of the Y plane, and of the interleaved UV plane below it */
    }
}

/* Bytes a picture of sh rows needs. */
static inline size_t cam_size(int fmt, int sh, int stride) {
    size_t rows = fmt == CAMFMT_NV12 ? (size_t)sh + (size_t)(sh + 1) / 2 : (size_t)sh;
    return rows * (size_t)stride;
}

/* The largest rectangle of a sw x sh picture with the aspect ratio dw : dh, centred. */
static inline void cam_crop(int sw, int sh, int dw, int dh, int *x0, int *y0, int *cw, int *ch) {
    int w = sw, h = sh;
    if ((int64_t)sw * dh > (int64_t)sh * dw) w = (int)((int64_t)sh * dw / dh);   /* too wide: cut the sides */
    else h = (int)((int64_t)sw * dh / dw);                                       /* too tall: cut top and bottom */
    if (w < 1) w = 1;
    if (h < 1) h = 1;
    *x0 = (sw - w) / 2, *y0 = (sh - h) / 2, *cw = w, *ch = h;
}

static inline uint8_t cam_clip(int v) { return (uint8_t)(v < 0 ? 0 : v > 255 ? 255 : v); }

/* BT.601, video range: what webcams send. */
static inline void cam_yuv(int y, int u, int v, int rgb[3]) {
    int c = 298 * (y - 16), d = u - 128, e = v - 128;
    rgb[0] = cam_clip((c + 409 * e + 128) >> 8);
    rgb[1] = cam_clip((c - 100 * d - 208 * e + 128) >> 8);
    rgb[2] = cam_clip((c + 516 * d + 128) >> 8);
}

/* The pixel at (x, y), y counted from the top of the picture. */
static inline void cam_pixel(const uint8_t *src, int fmt, int sh, int stride, int bottom_up, int x, int y, int rgb[3]) {
    int row = bottom_up ? sh - 1 - y : y;
    const uint8_t *p = src + (size_t)row * (size_t)stride;
    switch (fmt) {
    case CAMFMT_BGRA: rgb[0] = p[x * 4 + 2], rgb[1] = p[x * 4 + 1], rgb[2] = p[x * 4]; break;
    case CAMFMT_BGR24: rgb[0] = p[x * 3 + 2], rgb[1] = p[x * 3 + 1], rgb[2] = p[x * 3]; break;
    case CAMFMT_YUY2: {
        const uint8_t *q = p + (x & ~1) * 2;   /* Y0 U Y1 V */
        cam_yuv(q[(x & 1) * 2], q[1], q[3], rgb);
        break;
    }
    default: {
        const uint8_t *uv = src + (size_t)sh * (size_t)stride + (size_t)(row / 2) * (size_t)stride + (x & ~1);
        cam_yuv(p[x], uv[0], uv[1], rgb);
        break;
    }
    }
}

/* src (len bytes, sw x sh, rows stride apart, the last row first when bottom_up) to dst (dw x dh RGBA, rows dw * 4
 * apart, top row first). 1 done; 0 when the arguments make no picture (dst untouched). */
static inline int cam_convert(const uint8_t *src, size_t len, int fmt, int sw, int sh, int stride, int bottom_up,
                              uint8_t *dst, int dw, int dh, int mirror) {
    if (!src || !dst || sw < 2 || sh < 2 || dw < 1 || dh < 1 || sw > 8192 || sh > 8192) return 0;
    if (fmt < CAMFMT_BGRA || fmt > CAMFMT_NV12) return 0;
    if (stride < sw * (fmt == CAMFMT_BGRA ? 4 : fmt == CAMFMT_BGR24 ? 3 : fmt == CAMFMT_YUY2 ? 2 : 1)) return 0;
    if (fmt == CAMFMT_NV12) bottom_up = 0;   /* planar pictures are never stored upside down */
    if (len < cam_size(fmt, sh, stride)) return 0;
    int x0, y0, cw, ch;
    cam_crop(sw, sh, dw, dh, &x0, &y0, &cw, &ch);
    for (int y = 0; y < dh; y++) {
        uint8_t *out = dst + (size_t)y * (size_t)dw * 4;
        /* two rows and two columns of samples inside the source area of each target pixel */
        int ya = y0 + (int)(((int64_t)(4 * y + 1) * ch) / (4 * dh)), yb = y0 + (int)(((int64_t)(4 * y + 3) * ch) / (4 * dh));
        for (int x = 0; x < dw; x++) {
            int tx = mirror ? dw - 1 - x : x;
            int xa = x0 + (int)(((int64_t)(4 * tx + 1) * cw) / (4 * dw)), xb = x0 + (int)(((int64_t)(4 * tx + 3) * cw) / (4 * dw));
            int a[3], b[3], c[3], d[3];
            cam_pixel(src, fmt, sh, stride, bottom_up, xa, ya, a);
            cam_pixel(src, fmt, sh, stride, bottom_up, xb, ya, b);
            cam_pixel(src, fmt, sh, stride, bottom_up, xa, yb, c);
            cam_pixel(src, fmt, sh, stride, bottom_up, xb, yb, d);
            out[x * 4] = (uint8_t)((a[0] + b[0] + c[0] + d[0] + 2) / 4);
            out[x * 4 + 1] = (uint8_t)((a[1] + b[1] + c[1] + d[1] + 2) / 4);
            out[x * 4 + 2] = (uint8_t)((a[2] + b[2] + c[2] + d[2] + 2) / 4);
            out[x * 4 + 3] = 255;
        }
    }
    return 1;
}

#endif
