#include "uuid5.h"

#include <string.h>

const uint8_t kUuidNamespaceUrl[16] = {
    0x6b, 0xa7, 0xb8, 0x11, 0x9d, 0xad, 0x11, 0xd1, 0x80, 0xb4, 0x00, 0xc0, 0x4f, 0xd4, 0x30, 0xc8,
};

typedef struct {
    uint32_t h[5];
    uint64_t len;        // bytes hashed so far
    uint8_t block[64];   // pending partial block
} Sha1;

static uint32_t rol(uint32_t v, int n) { return (v << n) | (v >> (32 - n)); }

static void sha1_block(uint32_t h[5], const uint8_t *p) {
    uint32_t w[80];
    for (int i = 0; i < 16; i++)
        w[i] = (uint32_t)p[4 * i] << 24 | (uint32_t)p[4 * i + 1] << 16 | (uint32_t)p[4 * i + 2] << 8 | p[4 * i + 3];
    for (int i = 16; i < 80; i++) w[i] = rol(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
    uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4];
    for (int i = 0; i < 80; i++) {
        uint32_t f, k;
        if (i < 20)      { f = (b & c) | (~b & d);          k = 0x5a827999; }
        else if (i < 40) { f = b ^ c ^ d;                   k = 0x6ed9eba1; }
        else if (i < 60) { f = (b & c) | (b & d) | (c & d); k = 0x8f1bbcdc; }
        else             { f = b ^ c ^ d;                   k = 0xca62c1d6; }
        uint32_t t = rol(a, 5) + f + e + k + w[i];
        e = d; d = c; c = rol(b, 30); b = a; a = t;
    }
    h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e;
}

static void sha1_init(Sha1 *s) {
    static const uint32_t iv[5] = {0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476, 0xc3d2e1f0};
    memcpy(s->h, iv, sizeof iv);
    s->len = 0;
}

static void sha1_update(Sha1 *s, const void *data, size_t len) {
    const uint8_t *p = data;
    size_t fill = (size_t)(s->len % 64);
    s->len += len;
    while (len) {
        size_t take = 64 - fill < len ? 64 - fill : len;
        memcpy(s->block + fill, p, take);
        fill += take; p += take; len -= take;
        if (fill == 64) { sha1_block(s->h, s->block); fill = 0; }
    }
}

static void sha1_final(Sha1 *s, uint8_t out[20]) {
    uint64_t bits = s->len * 8;
    uint8_t tail[72] = {0x80};
    size_t fill = (size_t)(s->len % 64);
    size_t pad = (fill < 56 ? 56 : 120) - fill;   // 0x80, zeros, then the 8-byte length
    for (int i = 0; i < 8; i++) tail[pad + i] = (uint8_t)(bits >> (56 - 8 * i));
    sha1_update(s, tail, pad + 8);
    for (int i = 0; i < 20; i++) out[i] = (uint8_t)(s->h[i / 4] >> (24 - 8 * (i % 4)));
}

void sha1(const void *data, size_t len, uint8_t out[20]) {
    Sha1 s;
    sha1_init(&s);
    sha1_update(&s, data, len);
    sha1_final(&s, out);
}

void uuid5(const uint8_t ns[16], const void *name, size_t len, uint8_t out[16]) {
    Sha1 s;
    uint8_t digest[20];
    sha1_init(&s);
    sha1_update(&s, ns, 16);
    sha1_update(&s, name, len);
    sha1_final(&s, digest);
    memcpy(out, digest, 16);
    out[6] = (uint8_t)((out[6] & 0x0f) | 0x50);   // version 5
    out[8] = (uint8_t)((out[8] & 0x3f) | 0x80);   // RFC 4122 variant
}
