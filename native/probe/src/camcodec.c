#include "camcodec.h"

#include <stdlib.h>
#include <string.h>

// ---- what the encoder and the decoder share ------------------------------------------------------------------------

static const uint8_t kZigzag[64] = {0,  1,  8,  16, 9,  2,  3,  10, 17, 24, 32, 25, 18, 11, 4,  5,  12, 19, 26, 33, 40, 48,
                                    41, 34, 27, 20, 13, 6,  7,  14, 21, 28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23,
                                    30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63};

// The DCT's basis, T[x][u] = c(u) / 2 * cos((2x + 1) u pi / 16), from the eight cosines (no libm).
static void dct_basis(float t[8][8]) {
    static const float kCos[9] = {1.0f, 0.98078528f, 0.92387953f, 0.83146961f, 0.70710678f, 0.55557023f, 0.38268343f, 0.19509032f, 0.0f};
    for (int x = 0; x < 8; x++)
        for (int u = 0; u < 8; u++) {
            int m = (2 * x + 1) * u % 32;
            if (m > 16) m = 32 - m;
            float c = m > 8 ? -kCos[16 - m] : kCos[m];
            t[x][u] = (u ? 0.5f : 0.35355339f) * c;
        }
}

static inline uint8_t clip8(int v) { return (uint8_t)(v < 0 ? 0 : v > 255 ? 255 : v); }

void camcodec_scale(const uint8_t *src, int sw, int sh, uint8_t *dst, int dw, int dh) {
    if (!src || !dst || sw < 1 || sh < 1 || dw < 1 || dh < 1) return;
    for (int y = 0; y < dh; y++) {
        // the source position of the target pixel's centre, in 1/256 of a pixel
        int64_t fy = ((int64_t)(2 * y + 1) * sh * 256) / (2 * dh) - 128;
        if (fy < 0) fy = 0;
        int y0 = (int)(fy >> 8), wy = (int)(fy & 255), y1 = y0 + 1 < sh ? y0 + 1 : sh - 1;
        if (y0 >= sh) y0 = sh - 1;
        const uint8_t *a = src + (size_t)y0 * (size_t)sw * 4, *b = src + (size_t)y1 * (size_t)sw * 4;
        uint8_t *out = dst + (size_t)y * (size_t)dw * 4;
        for (int x = 0; x < dw; x++, out += 4) {
            int64_t fx = ((int64_t)(2 * x + 1) * sw * 256) / (2 * dw) - 128;
            if (fx < 0) fx = 0;
            int x0 = (int)(fx >> 8), wx = (int)(fx & 255), x1 = x0 + 1 < sw ? x0 + 1 : sw - 1;
            if (x0 >= sw) x0 = sw - 1;
            for (int k = 0; k < 3; k++) {
                int top = a[x0 * 4 + k] * (256 - wx) + a[x1 * 4 + k] * wx, bottom = b[x0 * 4 + k] * (256 - wx) + b[x1 * 4 + k] * wx;
                out[k] = (uint8_t)((top * (256 - wy) + bottom * wy + 32768) >> 16);
            }
            out[3] = 255;
        }
    }
}

// ---- the encoder -----------------------------------------------------------------------------------------------------

// The standard's tables (ITU T.81 annex K): quantisation at quality 50, and the Huffman tables every decoder knows.
static const uint8_t kQuant[2][64] = {
    {16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
     18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99},
    {17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
     99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99}};
static const uint8_t kDcBits[2][16] = {{0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0}, {0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0}};
static const uint8_t kDcVals[12] = {0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11};
static const uint8_t kAcBits[2][16] = {{0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d}, {0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77}};
static const uint8_t kAcVals[2][162] = {
    {0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81,
     0x91, 0xa1, 0x08, 0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18,
     0x19, 0x1a, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
     0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75,
     0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99,
     0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3,
     0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2, 0xe3, 0xe4, 0xe5,
     0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa},
    {0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13, 0x22, 0x32, 0x81, 0x08,
     0x14, 0x42, 0x91, 0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0, 0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25,
     0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26, 0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47,
     0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74,
     0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97,
     0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba,
     0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe2, 0xe3, 0xe4,
     0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa}};

typedef struct { uint16_t code[256]; uint8_t size[256]; } EncTable;

static void enc_table(EncTable *t, const uint8_t bits[16], const uint8_t *vals) {
    memset(t, 0, sizeof *t);
    unsigned code = 0;
    int k = 0;
    for (int len = 1; len <= 16; len++, code <<= 1)
        for (int i = 0; i < bits[len - 1]; i++, k++) t->code[vals[k]] = (uint16_t)code++, t->size[vals[k]] = (uint8_t)len;
}

typedef struct {
    uint8_t *out;
    int at, cap, full;
    uint32_t acc;
    int n;
} Writer;

static void put_byte(Writer *w, int b) {
    if (w->at < w->cap) w->out[w->at++] = (uint8_t)b;
    else w->full = 1;
}
static void put_bytes(Writer *w, const uint8_t *p, int n) {
    for (int i = 0; i < n; i++) put_byte(w, p[i]);
}
static void put_word(Writer *w, int v) { put_byte(w, v >> 8), put_byte(w, v & 0xff); }
static void put_bits(Writer *w, unsigned code, int size) {
    w->acc = (w->acc << size) | (code & ((1u << size) - 1));
    w->n += size;
    while (w->n >= 8) {
        int b = (int)(w->acc >> (w->n - 8)) & 0xff;
        put_byte(w, b);
        if (b == 0xff) put_byte(w, 0);
        w->n -= 8;
    }
}

// One 8x8 block (samples minus 128) through the DCT, the quantiser and the entropy coder. Returns its DC value.
static int put_block(Writer *w, const float basis[8][8], const float in[64], const uint8_t quant[64], const EncTable *dc,
                     const EncTable *ac, int last_dc) {
    float tmp[64];
    int q[64];
    for (int y = 0; y < 8; y++)
        for (int u = 0; u < 8; u++) {
            float s = 0;
            for (int x = 0; x < 8; x++) s += in[y * 8 + x] * basis[x][u];
            tmp[y * 8 + u] = s;
        }
    for (int u = 0; u < 8; u++)
        for (int v = 0; v < 8; v++) {
            float s = 0;
            for (int y = 0; y < 8; y++) s += tmp[y * 8 + u] * basis[y][v];
            float c = s / (float)quant[v * 8 + u];
            q[v * 8 + u] = (int)(c < 0 ? c - 0.5f : c + 0.5f);
        }
    int diff = q[0] - last_dc, a = diff < 0 ? -diff : diff, size = 0;
    while (a) size++, a >>= 1;
    put_bits(w, dc->code[size], dc->size[size]);
    if (size) put_bits(w, (unsigned)(diff < 0 ? diff - 1 : diff), size);
    int run = 0;
    for (int k = 1; k < 64; k++) {
        int v = q[kZigzag[k]];
        if (!v) { run++; continue; }
        for (; run > 15; run -= 16) put_bits(w, ac->code[0xf0], ac->size[0xf0]);
        a = v < 0 ? -v : v, size = 0;
        while (a) size++, a >>= 1;
        put_bits(w, ac->code[run << 4 | size], ac->size[run << 4 | size]);
        put_bits(w, (unsigned)(v < 0 ? v - 1 : v), size);
        run = 0;
    }
    if (run) put_bits(w, ac->code[0], ac->size[0]);
    return q[0];
}

int camcodec_jpeg_encode(const uint8_t *rgba, int w, int h, int quality, uint8_t *out, int cap) {
    if (!rgba || !out || w < 1 || h < 1 || w > CAMCODEC_OUT_W || h > CAMCODEC_OUT_H || cap < 700) return 0;
    if (quality < 1) quality = 1;
    if (quality > 100) quality = 100;
    int scale = quality < 50 ? 5000 / quality : 200 - 2 * quality;
    uint8_t quant[2][64];
    for (int t = 0; t < 2; t++)
        for (int i = 0; i < 64; i++) {
            int v = (kQuant[t][i] * scale + 50) / 100;
            quant[t][i] = (uint8_t)(v < 1 ? 1 : v > 255 ? 255 : v);
        }
    Writer wr = {.out = out, .cap = cap};
    static const uint8_t kHead[] = {0xff, 0xd8, 0xff, 0xe0, 0, 16, 'J', 'F', 'I', 'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0};
    put_bytes(&wr, kHead, sizeof kHead);
    for (int t = 0; t < 2; t++) {
        put_word(&wr, 0xffdb), put_word(&wr, 67), put_byte(&wr, t);
        for (int i = 0; i < 64; i++) put_byte(&wr, quant[t][kZigzag[i]]);
    }
    put_word(&wr, 0xffc0), put_word(&wr, 17), put_byte(&wr, 8), put_word(&wr, h), put_word(&wr, w), put_byte(&wr, 3);
    static const uint8_t kComp[9] = {1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1};
    put_bytes(&wr, kComp, 9);
    for (int t = 0; t < 2; t++) {
        put_word(&wr, 0xffc4), put_word(&wr, 19 + 12), put_byte(&wr, t);
        put_bytes(&wr, kDcBits[t], 16), put_bytes(&wr, kDcVals, 12);
        put_word(&wr, 0xffc4), put_word(&wr, 19 + 162), put_byte(&wr, 0x10 | t);
        put_bytes(&wr, kAcBits[t], 16), put_bytes(&wr, kAcVals[t], 162);
    }
    static const uint8_t kScan[] = {0xff, 0xda, 0, 12, 3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0};
    put_bytes(&wr, kScan, sizeof kScan);

    EncTable dc[2], ac[2];
    for (int t = 0; t < 2; t++) enc_table(&dc[t], kDcBits[t], kDcVals), enc_table(&ac[t], kAcBits[t], kAcVals[t]);
    float basis[8][8];
    dct_basis(basis);
    int last[3] = {0, 0, 0};
    for (int my = 0; my < h && !wr.full; my += 16)
        for (int mx = 0; mx < w && !wr.full; mx += 16) {
            // 16x16 pixels (the edge's repeated past it) to four blocks of Y and one each of Cb and Cr, 2x2 averaged
            float Y[4][64], cb[64], cr[64];
            memset(cb, 0, sizeof cb), memset(cr, 0, sizeof cr);
            for (int y = 0; y < 16; y++) {
                int sy = my + y < h ? my + y : h - 1;
                const uint8_t *row = rgba + (size_t)sy * (size_t)w * 4;
                for (int x = 0; x < 16; x++) {
                    const uint8_t *p = row + (size_t)(mx + x < w ? mx + x : w - 1) * 4;
                    float r = p[0], g = p[1], b = p[2];
                    Y[(y >> 3) * 2 + (x >> 3)][(y & 7) * 8 + (x & 7)] = 0.299f * r + 0.587f * g + 0.114f * b - 128.0f;
                    cb[(y >> 1) * 8 + (x >> 1)] += (-0.168736f * r - 0.331264f * g + 0.5f * b) * 0.25f;
                    cr[(y >> 1) * 8 + (x >> 1)] += (0.5f * r - 0.418688f * g - 0.081312f * b) * 0.25f;
                }
            }
            for (int i = 0; i < 4; i++) last[0] = put_block(&wr, basis, Y[i], quant[0], &dc[0], &ac[0], last[0]);
            last[1] = put_block(&wr, basis, cb, quant[1], &dc[1], &ac[1], last[1]);
            last[2] = put_block(&wr, basis, cr, quant[1], &dc[1], &ac[1], last[2]);
        }
    if (wr.n) put_bits(&wr, 0x7f, 8 - wr.n);   // the last byte is filled with ones
    put_word(&wr, 0xffd9);
    return wr.full ? 0 : wr.at;
}

// ---- the decoder -----------------------------------------------------------------------------------------------------
// Everything it reads is somebody else's: every length is checked against the bytes there are, every index against
// the table it goes into, and the picture's size against CAMCODEC_OUT_W x CAMCODEC_OUT_H before anything is allocated.

typedef struct {
    int defined;
    int mincode[17], maxcode[17], first[17];   // per code length: the smallest and largest code, the index of its first value
    uint8_t vals[256];
} DecTable;

static int dec_table(DecTable *t, const uint8_t bits[16], const uint8_t *vals, int nvals) {
    int code = 0, k = 0;
    for (int len = 1; len <= 16; len++) {
        t->first[len] = k, t->mincode[len] = code;
        code += bits[len - 1], k += bits[len - 1];
        if (code > 1 << len) return 0;   // more codes of this length than there can be
        t->maxcode[len] = bits[len - 1] ? code - 1 : -1;
        code <<= 1;
    }
    if (k != nvals || k > 256) return 0;
    memcpy(t->vals, vals, (size_t)k);
    t->defined = 1;
    return 1;
}

typedef struct {
    const uint8_t *p, *end;
    uint32_t acc;
    int n, pad;   // bits in acc; bytes of zeros put there because the data ended or a marker came
} Reader;

static void fill(Reader *r) {
    while (r->n <= 24) {
        int b = 0;
        if (r->p < r->end && (*r->p != 0xff || (r->p + 1 < r->end && r->p[1] == 0))) {
            b = *r->p;
            r->p += b == 0xff ? 2 : 1;   // ff 00 is the byte ff
        } else {
            r->pad++;                    // the end, or a marker: stays where it is
        }
        r->acc = (r->acc << 8) | (uint32_t)b;
        r->n += 8;
    }
}
static int get_bits(Reader *r, int count) {   // count 0..16
    if (!count) return 0;
    if (r->n < count) fill(r);
    r->n -= count;
    return (int)((r->acc >> r->n) & ((1u << count) - 1));
}
// The next symbol, or -1 when no code of the table matches.
static int get_symbol(Reader *r, const DecTable *t) {
    int code = 0;
    for (int len = 1; len <= 16; len++) {
        code = code << 1 | get_bits(r, 1);
        if (code <= t->maxcode[len] && code >= t->mincode[len]) return t->vals[t->first[len] + code - t->mincode[len]];
    }
    return -1;
}
static int extend(int v, int size) { return size && v < 1 << (size - 1) ? v - (1 << size) + 1 : v; }

typedef struct {
    int id, h, v, tq, td, ta;
    int pw, ph;        // the plane: whole MCUs
    uint8_t *plane;
    int dc;
} Component;

// One block: entropy-decoded, dequantised, through the inverse DCT, to out (rows stride apart). 0: corrupt.
static int get_block(Reader *r, const float basis[8][8], const DecTable *dc, const DecTable *ac, const uint8_t quant[64], int *last_dc,
                     uint8_t *out, int stride) {
    float f[64], tmp[64];
    memset(f, 0, sizeof f);
    int size = get_symbol(r, dc);
    if (size < 0 || size > 11) return 0;
    *last_dc += extend(get_bits(r, size), size);
    if (*last_dc < -32768 || *last_dc > 32767) return 0;
    f[0] = (float)(*last_dc * quant[0]);
    int any = 0;
    for (int k = 1; k < 64;) {
        int rs = get_symbol(r, ac);
        if (rs < 0) return 0;
        int run = rs >> 4;
        size = rs & 15;
        if (!size) {
            if (run != 15) break;   // end of block
            k += 16;
            continue;
        }
        k += run;
        if (k > 63 || size > 10) return 0;
        f[kZigzag[k]] = (float)(extend(get_bits(r, size), size) * quant[k]);
        any = 1;
        k++;
    }
    if (!any) {   // a flat block
        uint8_t flat = clip8((int)(f[0] * 0.125f + 128.5f));
        for (int y = 0; y < 8; y++) memset(out + (size_t)y * (size_t)stride, flat, 8);
        return 1;
    }
    for (int v = 0; v < 8; v++)
        for (int x = 0; x < 8; x++) {
            float s = 0;
            for (int u = 0; u < 8; u++) s += f[v * 8 + u] * basis[x][u];
            tmp[v * 8 + x] = s;
        }
    for (int x = 0; x < 8; x++)
        for (int y = 0; y < 8; y++) {
            float s = 0;
            for (int v = 0; v < 8; v++) s += tmp[v * 8 + x] * basis[y][v];
            s += 128.5f;
            out[(size_t)y * (size_t)stride + (size_t)x] = s <= 0 ? 0 : s >= 255 ? 255 : (uint8_t)s;
        }
    return 1;
}

int camcodec_jpeg_decode(const uint8_t *jpeg, int len, uint8_t *rgba, int *w_out, int *h_out) {
    if (!jpeg || !rgba || !w_out || !h_out || len < 4 || jpeg[0] != 0xff || jpeg[1] != 0xd8) return 0;
    uint8_t quant[4][64];
    int have_quant[4] = {0, 0, 0, 0};
    static const DecTable kNone;
    DecTable *tables = malloc(8 * sizeof *tables);   // [class * 4 + id]
    if (!tables) return 0;
    for (int i = 0; i < 8; i++) tables[i] = kNone;
    Component comp[3];
    memset(comp, 0, sizeof comp);
    int ncomp = 0, w = 0, h = 0, hmax = 1, vmax = 1, restart = 0, ok = 0;
    const uint8_t *p = jpeg + 2, *end = jpeg + len;
    while (!ok) {
        if (end - p < 4 || p[0] != 0xff) break;
        int marker = p[1];
        if (marker == 0xff) { p++; continue; }   // fill bytes before a marker
        int seg = p[2] << 8 | p[3];
        if (seg < 2 || seg > end - p - 2) break;
        const uint8_t *s = p + 4, *seg_end = p + 2 + seg;
        p = seg_end;
        if (marker == 0xdb) {   // quantisation tables, 8 bit only
            for (; s < seg_end; s += 65) {
                if (seg_end - s < 65 || s[0] > 3) goto done;
                for (int i = 0; i < 64; i++) quant[s[0]][i] = s[1 + i] ? s[1 + i] : 1;   // kept in zigzag order
                have_quant[s[0]] = 1;
            }
        } else if (marker == 0xc4) {   // Huffman tables
            while (s < seg_end) {
                if (seg_end - s < 17 || (s[0] & 0xef) > 3) goto done;
                int n = 0;
                for (int i = 0; i < 16; i++) n += s[1 + i];
                if (n > 256 || seg_end - s < 17 + n) goto done;
                if (!dec_table(&tables[(s[0] >> 4) * 4 + (s[0] & 3)], s + 1, s + 17, n)) goto done;
                s += 17 + n;
            }
        } else if (marker == 0xc0) {   // the frame: baseline only
            if (ncomp || seg < 8 + 3 || s[0] != 8) goto done;
            h = s[1] << 8 | s[2], w = s[3] << 8 | s[4], ncomp = s[5];
            if (w < 1 || h < 1 || w > CAMCODEC_OUT_W || h > CAMCODEC_OUT_H || (ncomp != 1 && ncomp != 3) || seg != 8 + 3 * ncomp) goto done;
            for (int i = 0; i < ncomp; i++) {
                Component *c = &comp[i];
                c->id = s[6 + i * 3], c->h = s[7 + i * 3] >> 4, c->v = s[7 + i * 3] & 15, c->tq = s[8 + i * 3];
                if (c->h < 1 || c->h > 2 || c->v < 1 || c->v > 2 || c->tq > 3) goto done;
                if (c->h > hmax) hmax = c->h;
                if (c->v > vmax) vmax = c->v;
            }
            if (ncomp == 1) comp[0].h = comp[0].v = hmax = vmax = 1;   // one component: its factors mean nothing
        } else if (marker == 0xdd) {
            if (seg != 4) goto done;
            restart = s[0] << 8 | s[1];
        } else if (marker == 0xda) {   // the scan: all the components, interleaved
            if (!ncomp || seg != 6 + 2 * ncomp || s[0] != ncomp) goto done;
            for (int i = 0; i < ncomp; i++) {
                Component *c = &comp[i];
                if (s[1 + i * 2] != c->id) goto done;
                c->td = s[2 + i * 2] >> 4, c->ta = s[2 + i * 2] & 15;
                if (c->td > 3 || c->ta > 3 || !tables[c->td].defined || !tables[4 + c->ta].defined || !have_quant[c->tq]) goto done;
            }
            if (s[1 + ncomp * 2] != 0 || s[2 + ncomp * 2] != 63 || s[3 + ncomp * 2] != 0) goto done;
            int mcux = (w + hmax * 8 - 1) / (hmax * 8), mcuy = (h + vmax * 8 - 1) / (vmax * 8);
            for (int i = 0; i < ncomp; i++) {
                comp[i].pw = mcux * comp[i].h * 8, comp[i].ph = mcuy * comp[i].v * 8;   // at most 384 x 224
                comp[i].plane = malloc((size_t)comp[i].pw * (size_t)comp[i].ph);
                if (!comp[i].plane) goto done;
            }
            float basis[8][8];
            dct_basis(basis);
            Reader r = {.p = p, .end = end};
            int count = 0, next_rst = 0;
            for (int my = 0; my < mcuy; my++)
                for (int mx = 0; mx < mcux; mx++) {
                    if (restart && count && count % restart == 0) {
                        // a restart marker: the bits left of the last byte are dropped, the predictions start over
                        if (r.pad * 8 > r.n + 7 || end - r.p < 2 || r.p[0] != 0xff || r.p[1] != 0xd0 + next_rst) goto done;
                        r.p += 2, r.acc = 0, r.n = 0, r.pad = 0;
                        next_rst = (next_rst + 1) & 7;
                        for (int i = 0; i < ncomp; i++) comp[i].dc = 0;
                    }
                    for (int i = 0; i < ncomp; i++) {
                        Component *c = &comp[i];
                        for (int by = 0; by < c->v; by++)
                            for (int bx = 0; bx < c->h; bx++) {
                                uint8_t *at = c->plane + (size_t)((my * c->v + by) * 8) * (size_t)c->pw + (size_t)((mx * c->h + bx) * 8);
                                if (!get_block(&r, basis, &tables[c->td], &tables[4 + c->ta], quant[c->tq], &c->dc, at, c->pw)) goto done;
                            }
                    }
                    if (r.pad * 8 > r.n) goto done;   // bits were read that the data does not have: cut short
                    count++;
                }
            // to RGBA: the chroma planes read at their own resolution (each sample repeated)
            for (int y = 0; y < h; y++) {
                uint8_t *out = rgba + (size_t)y * (size_t)w * 4;
                const uint8_t *row[3];
                for (int i = 0; i < ncomp; i++) row[i] = comp[i].plane + (size_t)(y * comp[i].v / vmax) * (size_t)comp[i].pw;
                for (int x = 0; x < w; x++, out += 4) {
                    int Y = row[0][x * comp[0].h / hmax];
                    if (ncomp == 1) {
                        out[0] = out[1] = out[2] = (uint8_t)Y;
                    } else {
                        int cb = row[1][x * comp[1].h / hmax] - 128, cr = row[2][x * comp[2].h / hmax] - 128;
                        out[0] = clip8(Y + ((91881 * cr + 32768) >> 16));
                        out[1] = clip8(Y - ((22554 * cb + 46802 * cr + 32768) >> 16));
                        out[2] = clip8(Y + ((116130 * cb + 32768) >> 16));
                    }
                    out[3] = 255;
                }
            }
            *w_out = w, *h_out = h;
            ok = 1;
        } else if (marker == 0xd9 || (marker >= 0xc1 && marker <= 0xcf && marker != 0xc8) || marker == 0xdc) {
            break;   // the end without a scan; progressive, lossless, arithmetic, 12 bit; a picture that says its height later
        }
        // anything else (APPn, COM): skipped
    }
done:
    for (int i = 0; i < 3; i++) free(comp[i].plane);
    free(tables);
    return ok;
}

// ---- the sender's and the receiver's side ----------------------------------------------------------------------------

void camcodec_encoder_init(CamEncoder *e) {
    memset(e, 0, sizeof *e);
    e->quality = CAMCODEC_QUALITY;
    e->divisor = 1;
}

void camcodec_encoder_divisor(CamEncoder *e, int divisor) { e->divisor = divisor == 2 || divisor == 4 ? divisor : 1; }

int camcodec_encode(CamEncoder *e, const uint8_t *rgba, uint64_t ms, uint8_t *out, int cap) {
    if (!e || !rgba || !out || cap < CAMCODEC_TARGET) return 0;
    uint64_t interval = (uint64_t)(1000 / CAMCODEC_FPS) * (uint64_t)(e->divisor == 2 || e->divisor == 4 ? e->divisor : 1);
    if (e->due && ms < e->due && e->due - ms <= interval) return 0;   // not due yet (a clock that went back: due again)
    // the next one an interval after this one was due, so pictures that come at another rate still average the rate;
    // after a pause, an interval from now
    e->due = e->due && ms - e->due < interval ? e->due + interval : ms + interval;
    camcodec_scale(rgba, CAMCODEC_OUT_W, CAMCODEC_OUT_H, e->small, CAMCODEC_W, CAMCODEC_H);
    if (e->quality < CAMCODEC_QUALITY_MIN || e->quality > CAMCODEC_QUALITY) e->quality = CAMCODEC_QUALITY;
    int n = 0, again = 0;
    for (;;) {
        n = camcodec_jpeg_encode(e->small, CAMCODEC_W, CAMCODEC_H, e->quality, out, cap);
        if (n && n <= CAMCODEC_TARGET) break;
        if (e->quality <= CAMCODEC_QUALITY_MIN) return 0;   // (no picture of this size is that large at this quality)
        // over: a quality that should fit with room to spare (the size falls a little slower than the quality)
        int q = n ? e->quality * CAMCODEC_TARGET / n * 9 / 10 : e->quality / 2;
        e->quality = q >= e->quality ? e->quality - 1 : q < CAMCODEC_QUALITY_MIN ? CAMCODEC_QUALITY_MIN : q;
        again = 1;
    }
    e->frames++, e->over += (uint32_t)again;
    e->last_bytes = n, e->last_quality = e->quality;
    // well under: a step back up for the next frame
    if (!again && n < CAMCODEC_TARGET * 3 / 4 && e->quality < CAMCODEC_QUALITY) e->quality += n < CAMCODEC_TARGET / 2 ? 2 : 1;
    if (e->quality > CAMCODEC_QUALITY) e->quality = CAMCODEC_QUALITY;
    return n;
}

int camcodec_decode(const uint8_t *jpeg, int len, uint8_t *rgba) {
    if (!jpeg || !rgba || len < 4) return 0;
    uint8_t *tmp = malloc((size_t)CAMCODEC_OUT_W * CAMCODEC_OUT_H * 4);
    if (!tmp) return 0;
    int w = 0, h = 0, ok = camcodec_jpeg_decode(jpeg, len, tmp, &w, &h);
    if (ok && w == CAMCODEC_OUT_W && h == CAMCODEC_OUT_H) memcpy(rgba, tmp, (size_t)CAMCODEC_OUT_W * CAMCODEC_OUT_H * 4);
    else if (ok) camcodec_scale(tmp, w, h, rgba, CAMCODEC_OUT_W, CAMCODEC_OUT_H);
    free(tmp);
    return ok;
}
