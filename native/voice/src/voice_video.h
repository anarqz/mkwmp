// mkwmp voice: video/v1 above the datagrams (voice_wire.h; VIDEO_API.md). A frame is one standalone JPEG, cut into
// chunks of at most 1100 bytes, one per datagram; the receiver puts a frame together again from its chunks, in any
// order, and drops a frame with a chunk missing. Header only, no sockets, no clock of its own (every call is given
// the time): tests/voice_video_test.cpp.
//   ChunkFrame    a frame as its datagrams (none when it does not fit 64 chunks)
//   Reassembler   chunks in (keyed by the sender's seat and the frame number), whole frames out
//   Pacer         what one sender may put on the wire: 64 KiB and 400 datagrams in any second (spread: no quarter of
//                 a second takes more than three tenths of it), and the relay's fps divisor; a frame goes whole or
//                 not at all
#pragma once

#include "voice_wire.h"

#include <array>
#include <cstdint>
#include <deque>
#include <vector>

namespace mkwmp::voice_video {

namespace wire = mkwmp::voice_wire;

constexpr size_t kMaxFrame = wire::kMaxVideoChunk * wire::kMaxVideoChunks; // 70400 bytes
constexpr size_t kBytesPerSecond = 64 * 1024, kDatagramsPerSecond = 400, kBurstBytes = kBytesPerSecond * 3 / 10;
constexpr int kFps = 10; // at the divisor 1 (VIDEO_API.md: 256x144 MJPEG, ten frames a second)

// The datagrams of one frame, in order. `random(i)` gives chunk i's random byte. Empty: no bytes, or too many.
template <typename Random>
std::vector<std::vector<uint8_t>> ChunkFrame(const uint8_t *frame, size_t size, uint16_t number, uint32_t ts, Random random) {
    std::vector<std::vector<uint8_t>> out;
    if (!frame || size == 0 || size > kMaxFrame) return out;
    const size_t chunks = (size + wire::kMaxVideoChunk - 1) / wire::kMaxVideoChunk;
    for (size_t i = 0; i < chunks; ++i) {
        const size_t at = i * wire::kMaxVideoChunk, n = size - at < wire::kMaxVideoChunk ? size - at : wire::kMaxVideoChunk;
        const uint8_t flags = static_cast<uint8_t>(wire::kVideoKeyFrame | (i + 1 == chunks ? wire::kVideoLastChunk : 0));
        out.push_back(wire::EncodeVideoUp(flags, number, static_cast<uint8_t>(i), static_cast<uint8_t>(chunks), ts, wire::kCodecMjpeg,
                                          static_cast<uint8_t>(random(i)), frame + at, n));
    }
    return out;
}

// a is after b, as frame numbers go (u16, wrapping)
inline bool Newer(uint16_t a, uint16_t b) { return static_cast<int16_t>(static_cast<uint16_t>(a - b)) > 0; }

struct Frame {
    uint8_t seat = 0;
    uint16_t number = 0;
    uint32_t ts = 0;
    std::vector<uint8_t> bytes;
};

class Reassembler {
  public:
    // One chunk as the relay sent it (wire::DecodeVideoDown), at `now_ms`. True when it completed a frame: `out` is
    // it. A frame is given once, never after a newer one of the same seat, and never with a chunk missing: its
    // chunks are dropped when a newer frame of that seat is complete, after kStaleMs, or when the slots run out.
    bool Feed(const wire::VideoChunk &c, int64_t now_ms, Frame &out) {
        if (c.seat >= wire::kMaxSeats || c.codec != wire::kCodecMjpeg || c.chunks == 0 || c.chunks > wire::kMaxVideoChunks ||
            c.chunk >= c.chunks || c.size == 0 || c.size > wire::kMaxVideoChunk)
            return false;
        Last &last = last_[c.seat];
        if (last.valid && now_ms - last.at > kForgetMs) last.valid = false; // a new sender on the seat, or one that began again
        if (last.valid && !Newer(c.frame, last.number)) return false;        // late or again
        Slot *slot = nullptr, *spare = nullptr;
        for (Slot &s : slots_) {
            if (s.used && now_ms - s.at > kStaleMs) Drop(s);
            if (s.used && s.seat == c.seat && s.number == c.frame) slot = &s;
            else if (!s.used && !spare) spare = &s;
        }
        if (!slot) {
            if (!spare) { // all in use: the one that has waited longest goes
                spare = &slots_[0];
                for (Slot &s : slots_)
                    if (s.at < spare->at) spare = &s;
                Drop(*spare);
            }
            slot = spare;
            slot->used = true;
            slot->seat = c.seat;
            slot->number = c.frame;
            slot->chunks = c.chunks;
            slot->have = 0;
            slot->got = 0;
            slot->at = now_ms;
        }
        if (slot->chunks != c.chunks || (slot->have >> c.chunk & 1)) return false; // not this frame's, or twice
        slot->part[c.chunk].assign(c.data, c.data + c.size);
        slot->have |= uint64_t{1} << c.chunk;
        if (++slot->got < slot->chunks) return false;
        out.seat = c.seat;
        out.number = c.frame;
        out.ts = c.ts;
        out.bytes.clear();
        for (int i = 0; i < slot->chunks; ++i) out.bytes.insert(out.bytes.end(), slot->part[i].begin(), slot->part[i].end());
        last = {true, c.frame, now_ms};
        for (Slot &s : slots_) // this one, and the seat's older ones: they will never be shown
            if (s.used && s.seat == c.seat && !Newer(s.number, c.frame)) { if (&s != slot) dropped_++; s.used = false; }
        return true;
    }
    // A seat's sender is gone, or the room changed: nothing of it is kept.
    void Reset() {
        for (Slot &s : slots_) s.used = false;
        for (Last &l : last_) l.valid = false;
    }
    uint32_t dropped() const { return dropped_; } // frames given up with a chunk missing

    static constexpr int64_t kStaleMs = 1000, kForgetMs = 2000;
    static constexpr size_t kSlots = 16;

  private:
    struct Slot {
        bool used = false;
        uint8_t seat = 0, chunks = 0, got = 0;
        uint16_t number = 0;
        uint64_t have = 0;
        int64_t at = 0;
        std::array<std::vector<uint8_t>, wire::kMaxVideoChunks> part;
    };
    struct Last {
        bool valid = false;
        uint16_t number = 0;
        int64_t at = 0;
    };
    void Drop(Slot &s) {
        if (s.used) dropped_++;
        s.used = false;
    }
    std::array<Slot, kSlots> slots_;
    std::array<Last, wire::kMaxSeats> last_;
    uint32_t dropped_ = 0;
};

class Pacer {
  public:
    // May a frame of `datagrams` datagrams, `bytes` in all (headers counted), leave at `now_ms`? True books it. No
    // second starting anywhere then holds more than the relay takes from one sender, nor two seconds more frames than
    // 2 * kFps / divisor (divisor 1, 2 or 4: the relay's "send fewer"). A second's bytes do not go at once either: a
    // quarter of a second holds at most kBurstBytes (three 6 KiB frames, as ten a second come), unless one frame alone
    // is larger; a relay that counts by its own clock then sees the same rate.
    bool Admit(int64_t now_ms, size_t bytes, size_t datagrams, int divisor) {
        if (divisor != 2 && divisor != 4) divisor = 1;
        while (!sent_.empty() && (now_ms - sent_.front().at >= 2000 || sent_.front().at > now_ms)) sent_.pop_front();
        size_t second_bytes = 0, second_datagrams = 0, quarter_bytes = 0;
        for (const Sent &s : sent_) {
            if (now_ms - s.at < 1000) { second_bytes += s.bytes; second_datagrams += s.datagrams; }
            if (now_ms - s.at < 250) quarter_bytes += s.bytes;
        }
        if (sent_.size() >= static_cast<size_t>(2 * kFps / divisor) || second_bytes + bytes > kBytesPerSecond ||
            second_datagrams + datagrams > kDatagramsPerSecond || (quarter_bytes && quarter_bytes + bytes > kBurstBytes))
            return false;
        sent_.push_back({now_ms, bytes, datagrams});
        return true;
    }

  private:
    struct Sent {
        int64_t at;
        size_t bytes, datagrams;
    };
    std::deque<Sent> sent_;
};

} // namespace mkwmp::voice_video
