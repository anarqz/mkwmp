// mkwmp voice: video/v1 above the datagrams (voice_video.h, VIDEO_API.md): a frame cut into chunks, put together
// again through loss, repeats and any order, and what one sender may put on the wire. Run by build.py --test.
#include "voice_video.h"

#include <algorithm>
#include <cstdio>
#include <random>

using namespace mkwmp::voice_video;

static int failures = 0;
static void Check(bool ok, const char *what) {
    std::printf("%s %s\n", ok ? "ok  " : "FAIL", what);
    if (!ok) failures++;
}

static std::vector<uint8_t> Picture(size_t size, uint32_t seed) {
    std::vector<uint8_t> v(size);
    std::mt19937 rng(seed);
    for (uint8_t &b : v) b = static_cast<uint8_t>(rng());
    if (size >= 2) v[0] = 0xff, v[1] = 0xd8;
    return v;
}

// The relay's part: a video up becomes the video down of `seat` (the chunk's bytes unchanged).
static std::vector<uint8_t> Down(const std::vector<uint8_t> &up, uint8_t seat) {
    wire::VideoChunk c;
    if (!wire::DecodeVideoUp(up.data(), up.size(), c)) return {};
    return wire::EncodeVideoDown(c.flags, c.frame, c.chunk, c.chunks, c.ts, c.codec, seat, c.data, c.size);
}

// One datagram into the reassembler; true when a frame came out.
static bool Feed(Reassembler &r, const std::vector<uint8_t> &down, int64_t now, Frame &out) {
    wire::VideoChunk c;
    return wire::DecodeVideoDown(down.data(), down.size(), c) && r.Feed(c, now, out);
}

static std::vector<std::vector<uint8_t>> Datagrams(const std::vector<uint8_t> &picture, uint16_t number, uint8_t seat, uint32_t ts = 0) {
    std::vector<std::vector<uint8_t>> out;
    for (const auto &up : ChunkFrame(picture.data(), picture.size(), number, ts, [](size_t i) { return 0xa0 + i; })) out.push_back(Down(up, seat));
    return out;
}

int main() {
    // ---- chunking
    {
        const auto one = Picture(1100, 1), two = Picture(1101, 2), six = Picture(6 * 1024, 3), most = Picture(kMaxFrame, 4), over = Picture(kMaxFrame + 1, 5);
        auto none = [](size_t) { return 0; };
        Check(ChunkFrame(one.data(), one.size(), 9, 5, none).size() == 1 && ChunkFrame(two.data(), two.size(), 9, 5, none).size() == 2 &&
                  ChunkFrame(six.data(), six.size(), 9, 5, none).size() == 6 && ChunkFrame(most.data(), most.size(), 9, 5, none).size() == 64,
              "a frame is ceil(size / 1100) datagrams: 1, 2, 6 (a 6 KiB frame) and 64");
        Check(ChunkFrame(over.data(), over.size(), 9, 5, none).empty() && ChunkFrame(one.data(), 0, 9, 5, none).empty() &&
                  ChunkFrame(nullptr, 10, 9, 5, none).empty(),
              "a frame over 64 chunks, an empty one and none at all give nothing");
        const auto d = ChunkFrame(six.data(), six.size(), 0xfffe, 123456, [](size_t i) { return 0x40 + i; });
        bool ok = d.size() == 6;
        size_t total = 0;
        for (size_t i = 0; ok && i < d.size(); i++) {
            wire::VideoChunk c;
            ok = wire::DecodeVideoUp(d[i].data(), d[i].size(), c) && c.frame == 0xfffe && c.chunk == i && c.chunks == 6 && c.ts == 123456 &&
                 c.codec == wire::kCodecMjpeg && c.seat == 0x40 + i && (c.flags & wire::kVideoKeyFrame) &&
                 ((c.flags & wire::kVideoLastChunk) != 0) == (i == 5) && c.size == (i < 5 ? 1100u : 6 * 1024 - 5 * 1100u) &&
                 d[i].size() <= wire::kMaxVideoDatagram && !std::memcmp(c.data, six.data() + i * 1100, c.size);
            total += c.size;
        }
        Check(ok && total == six.size(), "every chunk: the frame's number, its place, the time, MJPEG, key frame, last chunk on the last only");
    }

    // ---- reassembly
    {
        Reassembler r;
        Frame f;
        const auto a = Picture(6000, 10);
        const auto da = Datagrams(a, 1, 4, 777);
        bool early = false;
        for (size_t i = 0; i + 1 < da.size(); i++) early |= Feed(r, da[i], 100, f);
        Check(!early && Feed(r, da.back(), 100, f) && f.seat == 4 && f.number == 1 && f.ts == 777 && f.bytes == a, "in order: the frame, at its last chunk");
        Check(!Feed(r, da[0], 101, f) && !Feed(r, da.back(), 101, f), "a frame is given once: its chunks again are nothing");

        // any order
        const auto b = Picture(20000, 11);
        auto db = Datagrams(b, 2, 4);
        std::mt19937 rng(7);
        std::shuffle(db.begin(), db.end(), rng);
        int frames = 0;
        for (const auto &d : db) frames += Feed(r, d, 110, f);
        Check(frames == 1 && f.number == 2 && f.bytes == b, "19 chunks in a shuffled order: the same frame");

        // repeats
        const auto c = Picture(3000, 12);
        const auto dc = Datagrams(c, 3, 4);
        frames = 0;
        for (const auto &d : {dc[0], dc[0], dc[1], dc[1], dc[0], dc[2]}) frames += Feed(r, d, 120, f);
        Check(frames == 1 && f.bytes == c, "chunks that come twice count once");

        // loss: frame 4 lacks a chunk and is never given; frame 5 is, and frame 4's last chunk coming late changes nothing
        const auto l = Picture(5000, 13), n = Picture(4000, 14);
        const auto dl = Datagrams(l, 4, 4), dn = Datagrams(n, 5, 4);
        frames = 0;
        for (size_t i = 0; i < dl.size(); i++)
            if (i != 2) frames += Feed(r, dl[i], 130, f);
        Check(frames == 0, "a frame with a chunk lost is not given");
        const uint32_t dropped = r.dropped();
        frames = 0;
        for (const auto &d : dn) frames += Feed(r, d, 140, f);
        Check(frames == 1 && f.number == 5 && f.bytes == n && r.dropped() == dropped + 1, "the next frame is, and the broken one is dropped then");
        Check(!Feed(r, dl[2], 141, f), "the lost chunk coming after the newer frame: nothing");

        // two frames interleaved (a reordering across frames): both come out, the older first
        const auto p = Picture(2500, 15), q = Picture(2500, 16);
        const auto dp = Datagrams(p, 6, 4), dq = Datagrams(q, 7, 4);
        std::vector<uint16_t> order;
        for (const auto &d : {dp[0], dq[0], dp[1], dq[1], dp[2], dq[2]})
            if (Feed(r, d, 150, f)) order.push_back(f.number);
        Check(order == std::vector<uint16_t>{6, 7}, "two frames' chunks interleaved: both, in order");
        // the newer completes first: the older one is late and dropped
        const auto ds = Datagrams(p, 8, 4), dt = Datagrams(q, 9, 4);
        order.clear();
        for (const auto &d : {ds[0], dt[0], ds[1], dt[1], dt[2], ds[2]})
            if (Feed(r, d, 160, f)) order.push_back(f.number);
        Check(order == std::vector<uint16_t>{9}, "a frame completed after a newer one is not shown");

        // seats are apart: the same frame number from two seats
        const auto x = Picture(2000, 17), y = Picture(2300, 18);
        const auto dx = Datagrams(x, 100, 0), dy = Datagrams(y, 100, 23);
        std::vector<std::pair<int, size_t>> got;
        for (const auto &d : {dx[0], dy[0], dy[1], dx[1], dy[2]})
            if (Feed(r, d, 170, f)) got.push_back({f.seat, f.bytes.size()});
        Check(got == std::vector<std::pair<int, size_t>>{{0, 2000}, {23, 2300}} , "frames are keyed by seat and number");

        // the number wraps
        Reassembler w;
        const auto dw1 = Datagrams(x, 0xffff, 1), dw2 = Datagrams(y, 0, 1), dw3 = Datagrams(x, 0xffff, 1);
        frames = 0;
        for (const auto &d : dw1) frames += Feed(w, d, 10, f);
        for (const auto &d : dw2) frames += Feed(w, d, 20, f);
        for (const auto &d : dw3) frames += Feed(w, d, 30, f);
        Check(frames == 2 && f.number == 0, "frame 0 comes after frame 65535; 65535 after it is old");

        // a chunk that waited over a second is given up; the frame sent again later starts over
        Reassembler s;
        const auto dz = Datagrams(l, 50, 2);
        frames = 0;
        for (size_t i = 0; i + 1 < dz.size(); i++) frames += Feed(s, dz[i], 1000, f);
        frames += Feed(s, dz.back(), 1000 + Reassembler::kStaleMs + 1, f);
        Check(frames == 0 && s.dropped() == 1, "chunks older than a second are given up");

        // a sender that began again (the seat silent for over two seconds): its numbers start over
        Reassembler again;
        frames = 0;
        for (const auto &d : Datagrams(x, 900, 3)) frames += Feed(again, d, 0, f);
        for (const auto &d : Datagrams(y, 0, 3)) frames += Feed(again, d, 500, f);
        Check(frames == 1, "an older number right after is refused");
        for (const auto &d : Datagrams(y, 0, 3)) frames += Feed(again, d, 500 + Reassembler::kForgetMs + 1, f);
        Check(frames == 2 && f.number == 0 && f.bytes == y, "after two silent seconds the seat's numbers start over");

        // what is not a chunk of ours
        Reassembler bad;
        wire::VideoChunk c2;
        const auto good = Datagrams(x, 1, 5);
        wire::DecodeVideoDown(good[0].data(), good[0].size(), c2);
        wire::VideoChunk h264 = c2, other = c2;
        h264.codec = 2;
        Check(!bad.Feed(h264, 0, f), "another codec is not taken");
        Check(!bad.Feed(c2, 0, f), "the first chunk of two alone is no frame");
        other.chunk = 1;
        other.chunks = 3; // the frame was said to have 2
        Check(!bad.Feed(other, 0, f), "a chunk that disagrees about the count is not taken");

        // 30 % loss, reordering and repeats over 300 frames from 4 seats: only whole, right frames come out
        Reassembler lossy;
        std::mt19937 dice(99);
        std::vector<std::pair<int64_t, std::vector<uint8_t>>> net; // (arrival, datagram)
        std::vector<std::vector<uint8_t>> sent[4];
        for (int n2 = 0; n2 < 300; n2++) {
            const uint8_t seat = static_cast<uint8_t>(n2 % 4);
            const auto pic = Picture(1500 + dice() % 6000, 1000 + static_cast<uint32_t>(n2));
            sent[seat].push_back(pic);
            for (const auto &d : Datagrams(pic, static_cast<uint16_t>(n2 / 4), seat)) {
                if (dice() % 100 < 30) continue;
                const int64_t at = n2 * 25 + static_cast<int64_t>(dice() % 40); // up to 40 ms late: chunks and frames cross
                net.push_back({at, d});
                if (dice() % 100 < 10) net.push_back({at + 3, d});
            }
        }
        std::stable_sort(net.begin(), net.end(), [](const auto &a2, const auto &b2) { return a2.first < b2.first; });
        int whole = 0, wrong = 0, backwards = 0, last[4] = {-1, -1, -1, -1};
        for (const auto &[at, d] : net) {
            if (!Feed(lossy, d, at, f)) continue;
            whole++;
            wrong += f.seat > 3 || f.number >= sent[f.seat].size() || f.bytes != sent[f.seat][f.number];
            backwards += static_cast<int>(f.number) <= last[f.seat];
            last[f.seat] = f.number;
        }
        std::printf("     lossy: %d of 300 frames whole, %u given up\n", whole, lossy.dropped());
        Check(whole > 20 && whole < 280 && wrong == 0 && backwards == 0, "30 % loss with reordering: fewer frames, never a wrong or an older one");
    }

    // ---- pacing
    {
        // ten 6 KiB frames a second, as the camera sends (6 datagrams, 6288 bytes with the headers): all pass
        Pacer p;
        int passed = 0;
        for (int i = 0; i < 100; i++) passed += p.Admit(i * 100, 6288, 6, 1);
        Check(passed == 100, "ten 6 KiB frames a second for ten seconds: all sent");

        // a flood: frames of 8000 bytes every millisecond; no window of a second ever holds over 64 KiB or 400 datagrams
        Pacer flood;
        std::vector<std::pair<int64_t, size_t>> sent;
        for (int64_t t = 0; t < 5000; t++)
            if (flood.Admit(t, 8000, 8, 1)) sent.push_back({t, 8000});
        size_t worst = 0;
        for (size_t i = 0; i < sent.size(); i++) {
            size_t sum = 0;
            for (size_t j = i; j < sent.size() && sent[j].first - sent[i].first < 1000; j++) sum += sent[j].second;
            worst = std::max(worst, sum);
        }
        Check(!sent.empty() && worst <= kBytesPerSecond && worst > kBytesPerSecond / 2, "a flood: never over 64 KiB in any second");
        size_t burst = 0;
        for (size_t i = 0; i < sent.size(); i++) {
            size_t sum = 0;
            for (size_t j = i; j < sent.size() && sent[j].first - sent[i].first < 250; j++) sum += sent[j].second;
            burst = std::max(burst, sum);
        }
        Check(burst <= kBurstBytes, "and spread: no quarter of a second takes over three tenths of it");
        Pacer lone;
        Check(lone.Admit(0, 60000, 55, 1) && !lone.Admit(100, 3000, 3, 1) && lone.Admit(250, 3000, 3, 1), "one large frame alone passes; the next waits");

        // many small datagrams: the count is what stops them
        Pacer small;
        int datagrams = 0;
        for (int64_t t = 0; t < 1000; t++)
            if (small.Admit(t, 64 * 30, 64, 1)) datagrams += 64;
        Check(datagrams <= static_cast<int>(kDatagramsPerSecond) && datagrams >= 320, "never over 400 datagrams in a second");
        Check(!small.Admit(1000, 70000, 1, 1), "a frame over a second's bytes never passes");

        // the relay's divisor: 10, 5 and 2.5 frames a second over ten seconds
        for (int divisor : {1, 2, 4}) {
            Pacer d;
            int frames = 0;
            for (int64_t t = 0; t < 10000; t += 10) frames += d.Admit(t, 3000, 3, divisor);
            char what[80];
            std::snprintf(what, sizeof what, "divisor %d: %d frames in ten seconds", divisor, frames);
            Check(frames >= 100 / divisor - 1 && frames <= 100 / divisor + 2 * kFps / divisor, what);
        }
        Pacer d;
        Check(d.Admit(0, 100, 1, 3) && d.Admit(1, 100, 1, 0), "a divisor that is not 1, 2 or 4 is 1");
        // a clock that went back does not stop the camera for good
        Pacer back;
        for (int64_t t = 100000; t < 100020; t++) back.Admit(t, 100, 1, 1);
        Check(back.Admit(5, 100, 1, 1), "a clock that went back: sent");
    }
    std::printf("voice_video_test: %s\n", failures ? "FAIL" : "PASS");
    return failures ? 1 : 0;
}
