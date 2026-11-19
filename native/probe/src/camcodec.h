// The camera's pictures on the wire (video/v1 on the voice relay; research/docs/CAMERA-RE.md 4.2): MJPEG, every frame
// one standalone baseline JPEG of 256x144, at most ten a second, starting at quality 40 and kept at or under 6 KiB (the
// relay allows a sender 64 KiB/s). The size travels only inside the JPEG (its SOF0), so the decoder takes any picture
// up to 384x216 and hands out 384x216, the size the game shows.
//
// The codec is this file's own: a baseline JPEG encoder (4:2:0, the standard's tables) and a baseline decoder (8 bit,
// Huffman, one interleaved scan, 1 or 3 components with sampling factors 1 or 2, restart intervals), about 500 lines
// of C with no dependency, not even libm. Why not the Windows Imaging Component: the Linux plugin needs the same codec
// and Proton has no dependable WIC, so WIC would have been a second implementation to keep bit-compatible; and what
// the decoder reads comes from other players, so a decoder that takes only this one kind of JPEG, with every read
// checked and every size capped at 384x216, is less to trust than a general one. It is unit-tested against itself, a
// truncated and a corrupted stream, and (by hand, tests/test_camcodec.c) against the system's own JPEG codec.
//
// Nothing here knows the transport or the game: camera.c calls it between the local picture and camera_set_jpeg_out's
// function, and between camera_jpeg_in and the member's picture.
#ifndef MKWMP_CAMCODEC_H
#define MKWMP_CAMCODEC_H

#include <stddef.h>
#include <stdint.h>

enum {
    CAMCODEC_W = 256,              // what is sent
    CAMCODEC_H = 144,
    CAMCODEC_OUT_W = 384,          // what a decoded picture is scaled to (CAMERA_W x CAMERA_H), and the largest taken
    CAMCODEC_OUT_H = 216,
    CAMCODEC_FPS = 10,             // at the send divisor 1
    CAMCODEC_QUALITY = 40,         // where the encoder starts, and the most it goes back up to
    CAMCODEC_QUALITY_MIN = 8,
    CAMCODEC_TARGET = 6 * 1024,    // bytes a frame may have: one over it is encoded again at a lower quality
    CAMCODEC_FRAME_MAX = 16 * 1024 // a buffer this large holds any frame the encoder makes on its way down
};

// ---- the codec itself (pure; any thread) ---------------------------------------------------------------------------

// rgba (w x h, rows w * 4 apart, 1..384 x 1..216) to one baseline JPEG, quality 1..100. Returns its length, or 0 when
// the arguments make no picture or it does not fit cap.
int camcodec_jpeg_encode(const uint8_t *rgba, int w, int h, int quality, uint8_t *out, int cap);
// One baseline JPEG to RGBA (rows *w * 4 apart; rgba holds CAMCODEC_OUT_W x CAMCODEC_OUT_H). 1 done, with the
// picture's size in *w, *h; 0 for anything else (not a JPEG, progressive or 12 bit, larger than 384x216, cut short,
// corrupt): rgba's content is then undefined, nothing outside it is touched.
int camcodec_jpeg_decode(const uint8_t *jpeg, int len, uint8_t *rgba, int *w, int *h);
// Bilinear, RGBA to RGBA (rows width * 4 apart); src and dst must not overlap.
void camcodec_scale(const uint8_t *src, int sw, int sh, uint8_t *dst, int dw, int dh);

// ---- the sender's side ---------------------------------------------------------------------------------------------
typedef struct {
    int quality;           // of the next frame: lowered when a frame is over CAMCODEC_TARGET, raised again when under
    int divisor;           // 1, 2 or 4: CAMCODEC_FPS / divisor frames a second (the relay asks for less when over budget)
    uint64_t due;          // the time the next frame is due (0: the first one is)
    uint32_t frames, over; // frames encoded; of them, how many had to be encoded again
    int last_bytes, last_quality;
    uint8_t small[CAMCODEC_W * CAMCODEC_H * 4];
} CamEncoder;
void camcodec_encoder_init(CamEncoder *e);
// 1, 2 or 4 (anything else: 1).
void camcodec_encoder_divisor(CamEncoder *e, int divisor);
// A local picture (384x216 RGBA) taken at ms. Returns the length of the JPEG written to out (cap bytes; give it
// CAMCODEC_FRAME_MAX), never over CAMCODEC_TARGET; or 0 when this picture is not sent (not due yet at the frame rate).
int camcodec_encode(CamEncoder *e, const uint8_t *rgba, uint64_t ms, uint8_t *out, int cap);

// ---- the receiver's side -------------------------------------------------------------------------------------------
// A frame as it came to a 384x216 RGBA picture (rgba: CAMCODEC_OUT_W * CAMCODEC_OUT_H * 4 bytes). 1 done, 0 refused.
// Uses a picture's worth of the heap for the length of the call.
int camcodec_decode(const uint8_t *jpeg, int len, uint8_t *rgba);

#endif
