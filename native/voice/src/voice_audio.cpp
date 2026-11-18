// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-only; see LICENSE and NOTICE.
// mkwmp voice (ported from ForzaInfinity's overlay, same author): voice chat's audio engine (OD-154, VC-3; VOICE_CHAT.md §3.2, §12.4). Two engine threads, never
// the game's: capture (WASAPI shared, event-driven; the player's chosen microphone or else the default communications
// one, never a monitor or loopback input: voice_audio_devices.h; none: listen-only; §15 levels: voice_volume.h) → 48 kHz mono → echo
// cancellation (WebRTC AEC3 + its 100 Hz high-pass, against the mix about to play: voice_echo.h) → AGC → RNNoise (noise suppression; its voice probability gates sending, 300 ms hangover) → Opus 20 ms
// frames (SILK wideband 6 kbit/s, CVBR, complexity 10, no FEC/DTX) → repacketized 3 (or 6) per packet → SendFn; and
// playback: per seat an adaptive 1-2 packet jitter buffer → Opus decoder with deep PLC for gaps → stereo mix (open:
// every talker centred, both channels the old mono mix; proximity: voice_spatial.h places each by its car) → one
// WASAPI shared render stream on the output (~60 ms queued; resampled and mapped to the output's own format). The decoder
// starts at complexity 7 (NoLACE) on every platform and steps
// down only on measured decode cost (voice_audio_complexity.h): no platform-based quality choice, so a Deck player
// gets what a Windows player gets. Releasing transmit (push-to-talk, mute) ends the spurt with a short packet that
// carries the end flag. Every codec failure logs and turns voice off; a missing, unplugged or changed device is retried
// or reopened every 2-3 s (backing off to 16 s when the device keeps failing). Nothing here touches the network.
//
// The game is never held by a device (2026-10-31: an output reopen under Proton never returned and the game froze).
// Every device open, poll, reopen and close (capture, playback, loopback) runs on the session's own device workers;
// nothing the game's threads call (Receive, SetPose, SeatTalking, SeatHeard, Running, ListenOnly, Sending) waits for
// them: those take the session lock only with a try (Start and Stop hold it exclusively just to swap a pointer, never
// across a device call or a thread wait). A watchdog abandons a worker whose device call has not returned for 8 s (a
// fresh worker retries, with backoff; the abandoned one keeps its own reference to the session, so nothing is freed
// under it, and it touches nothing shared when it comes back). Stop waits 1.5 s at most, then leaves stuck workers
// behind. Playback is WASAPI, not XAudio2: the game's own XAudio2 runs on the same xaudio2_9/FAudio library in this
// process, and a voice mastering voice being torn down and rebuilt on a device change shares that library's state
// with the game (inference for the freeze: the game's main thread waited on a lock while the voice output reopened).
//
// The device layer is per platform, behind voice_audio_engine.h: voice_audio_wasapi.cpp (Windows: WASAPI, and its
// Wine/Proton audit) and voice_audio_pulse.cpp (Linux: PulseAudio or PipeWire's pulse server, libpulse loaded at
// run time). Every rule above holds for both: they open, poll and close devices only on the device workers below.
#include "voice_audio.h"
#include "voice_audio_complexity.h"
#include "voice_audio_devices.h"
#include "voice_audio_dsp.h"
#include "voice_audio_engine.h"
#include "voice_audio_platform.h"
#include "voice_audio_testing.h"
#include "voice_echo.h"
#include "voice_spatial.h"
#include "voice_loudness.h"

#include "voice_log.h"

#include <xmmintrin.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <new>
#include <shared_mutex>
#include <string>
#include <vector>

#include "opus.h"
#include "rnnoise.h"

// The engine's inside (voice_audio_engine.h): one namespace with the device backend; the public API is at the end.
namespace fi::audio::engine {

constexpr int kHangoverSubs = 30; // 300 ms of 10 ms blocks
constexpr float kVoiceThreshold = 0.5f;
constexpr int64_t kTalkingMs = 300;
constexpr int kMaxConcealFrames = 6;   // a talker gone quiet without an end flag: 120 ms of PLC, then silence
constexpr size_t kMaxBuffered = 4;     // packets per seat; older ones are dropped (latency bound)
constexpr size_t kMaxInbox = 256;

std::atomic<int64_t> g_fake_now{-1};
int64_t NowMs() {
    const int64_t fake = g_fake_now.load();
    return fake >= 0 ? fake : plat::TickMs();
}

void FlushDenormals() { _mm_setcsr(_mm_getcsr() | 0x8040); } // FTZ | DAZ: RNNoise costs 3.7x without (§12.4)

double QpcUs() { return plat::PerfUs(); }

// ---------------------------------------------------------------- shared state

std::atomic<bool> g_running{false}, g_failed{false}, g_allowed{false}, g_ptt{false}; // g_failed: voice_audio_engine.h
std::atomic<int> g_bundle{3};
std::atomic<int64_t> g_sending_until{0};
std::atomic<int> g_complexity{ComplexityController::kTop};
std::atomic<double> g_cost_override{-1.0}, g_cost_p95{0.0};
std::atomic<uint64_t> g_concealed{0};
std::atomic<testing::EchoTap> g_echo_tap{nullptr}; // the echo bench's view of each capture block (null in a game)
std::atomic<bool> g_mix_reference{true}; // false only in tests that loop a talker's own voice back (testing::SetMixReference)

// §15: the player's levels and chosen devices; they outlive an engine session (set by voice.cpp every 50 ms anyway).
std::atomic<float> g_mic_gain{1.0f};
struct SeatGains {
    std::atomic<float> gain[kSeats];
    SeatGains() { for (auto &g : gain) g.store(1.0f); }
} g_seat_gains;
std::mutex g_choice_lock;
std::wstring g_mic_choice, g_output_choice;           // endpoint ids (IMMDevice ids, PulseAudio names); empty: the system default
std::atomic<uint32_t> g_mic_serial{0}, g_output_serial{0}; // bumped on a change: the open stream reopens

// NR-7: the lobby's voice mode and the spatial tuning; they outlive an engine session like the levels above.
std::atomic<uint8_t> g_voice_mode{static_cast<uint8_t>(spatial::Mode::Open)};
std::mutex g_params_lock;
spatial::Params g_params;           // Sanitized
std::atomic<uint32_t> g_params_serial{0};
spatial::Params SpatialParams() { std::lock_guard lock(g_params_lock); return g_params; }

std::wstring MicChoice() { std::lock_guard lock(g_choice_lock); return g_mic_choice; }
std::wstring OutputChoice() { std::lock_guard lock(g_choice_lock); return g_output_choice; }
// The render endpoint voice plays to now (the chosen output while it opens, else the default's id; empty: none open).
// The echo canceller's reference is this device's signal (the mix voice plays to it), so the two never differ.
std::wstring g_output_open;
std::wstring OutputInUse() { std::lock_guard lock(g_choice_lock); return g_output_open; }
void SetOutputInUse(const std::wstring &id) { std::lock_guard lock(g_choice_lock); g_output_open = id; }

float SafeGain(float g) { return std::isfinite(g) ? std::clamp(g, 0.0f, 1.0f) : 1.0f; }

// Codec-level failures: voice is off for the game session. Flags only (any thread, the game's included): the device
// workers see it within their next wait and the voice thread's next Start retires the session.
void Fail(const char *what) {
    if (!g_failed.exchange(true)) Log("voice: %s; voice chat is off for this session", what);
    g_running = false;
}

// ---------------------------------------------------------------- capture chain

class CaptureChain {
public:
    CaptureChain(SendFn send, EchoLink *echo) : send_(std::move(send)), echo_(echo) {}
    ~CaptureChain() {
        if (ns_) rnnoise_destroy(ns_);
        if (enc_) opus_encoder_destroy(enc_);
        if (rp_) opus_repacketizer_destroy(rp_);
    }
    bool Init() {
        ns_ = rnnoise_create(nullptr);
        if (!ns_ || rnnoise_get_frame_size() != kSub) { Log("voice: RNNoise unavailable"); return false; }
        int err = 0;
        enc_ = opus_encoder_create(kRate, 1, OPUS_APPLICATION_VOIP, &err);
        if (err != OPUS_OK || !enc_) { Log("voice: Opus encoder unavailable (%d)", err); return false; }
        // §3.2 + §12.4: OPUS_SET_BANDWIDTH(WB) too, or 6 kbit/s falls to narrowband and NoLACE/deep PLC never run.
        const int ok = opus_encoder_ctl(enc_, OPUS_SET_BITRATE(6000)) | opus_encoder_ctl(enc_, OPUS_SET_VBR(1)) |
            opus_encoder_ctl(enc_, OPUS_SET_VBR_CONSTRAINT(1)) | opus_encoder_ctl(enc_, OPUS_SET_COMPLEXITY(10)) |
            opus_encoder_ctl(enc_, OPUS_SET_SIGNAL(OPUS_SIGNAL_VOICE)) | opus_encoder_ctl(enc_, OPUS_SET_INBAND_FEC(0)) |
            opus_encoder_ctl(enc_, OPUS_SET_DTX(0)) | opus_encoder_ctl(enc_, OPUS_SET_PACKET_LOSS_PERC(0)) |
            opus_encoder_ctl(enc_, OPUS_SET_MAX_BANDWIDTH(OPUS_BANDWIDTH_WIDEBAND)) |
            opus_encoder_ctl(enc_, OPUS_SET_BANDWIDTH(OPUS_BANDWIDTH_WIDEBAND));
        if (ok != OPUS_OK) { Log("voice: Opus encoder settings refused"); return false; }
        rp_ = opus_repacketizer_create();
        if (!rp_) { Log("voice: Opus repacketizer unavailable"); return false; }
        return true;
    }
    // A device buffer in `format`; `silent` buffers count as zeros.
    void FeedDevice(const dsp::DeviceFormat &format, const uint8_t *data, size_t frames, bool silent) {
        if (!(format == format_)) {
            format_ = format;
            resampler_.Reset(format.rate);
            Log("voice: capture %u Hz, %u channel(s), %s in %u-byte samples -> 48 kHz mono", format.rate, format.channels,
                dsp::KindName(format.kind), format.container);
        }
        if (silent || !data) mono_.assign(frames, 0.0f);
        else dsp::ToMono(format, data, frames, mono_);
        resampler_.Process(mono_.data(), mono_.size(), at48_);
        Feed48(at48_.data(), at48_.size());
    }
    // A capture gap (device reopened): the gate starts over.
    void Restart() {
        fill_ = 0;
        subs_ = 0;
        prev_sent_ = false;
    }

private:
    void Feed48(const float *x, size_t n) {
        for (size_t i = 0; i < n; i++) {
            raw_[fill_++] = x[i];
            if (fill_ == kSub) { fill_ = 0; CancelEcho(); Block(); }
        }
    }
    // Release 2.0.0: each raw 10 ms block through AEC3 and its 100 Hz high-pass (voice_echo.h) before the gain, RNNoise
    // and Opus. Without a canceller (never in a started engine) the 80 Hz high-pass alone, as before.
    void CancelEcho() {
        if (echo::Canceller *canceller = echo_ ? echo_->canceller.get() : nullptr) {
            const testing::EchoTap tap = g_echo_tap.load(std::memory_order_relaxed);
            float before[kSub];
            if (tap) std::memcpy(before, raw_, sizeof raw_);
            canceller->Capture(raw_);
            if (tap) tap(before, raw_, kSub);
            for (int i = 0; i < kSub; i++) sub_[i] = raw_[i] * 32768.0f;
            if (!echo_logged_ && ++echo_blocks_ % 100 == 0) { // once per session, when AEC3 has aligned to the far end
                if (const int delay = canceller->DelayMs(); delay >= 0) {
                    echo_logged_ = true;
                    Log("voice: echo canceller on (AEC3, delay %d ms)", delay);
                }
            }
        } else {
            for (int i = 0; i < kSub; i++) sub_[i] = hp_.Process(raw_[i]) * 32768.0f;
        }
    }
    void Block() {
        // transmit released (push-to-talk let go, muted, voice off) during a spurt: end it now, not at the bundle's end
        if (prev_sent_ && !g_allowed.load()) EndSpurt();
        if (subs_ == 0) bundle_ = std::clamp(g_bundle.load(), 3, kMaxBundle) == 6 ? 6 : 3; // a packet's size is fixed at its start
        agc_.Process(sub_, kSub, prev_voice_, kVoiceThreshold);
        // §15: "My microphone volume". After the AGC, which would undo any gain before it (echo cancellation works on
        // the raw capture, earlier still), and before RNNoise and the encoder: the voice that leaves the PC is this level.
        if (const float mic = g_mic_gain.load(std::memory_order_relaxed); mic != 1.0f)
            for (int i = 0; i < kSub; i++) sub_[i] *= mic;
        float clean[kSub];
        prev_voice_ = rnnoise_process_frame(ns_, clean, sub_);
        if (prev_voice_ >= kVoiceThreshold) since_voice_ = 0;
        else if (since_voice_ < 1000000) since_voice_++;
        if (since_voice_ <= kHangoverSubs) open_ = true;
        for (int i = 0; i < kSub; i++) pcm_[subs_ * kSub + i] = clean[i] / 32768.0f;
        if (++subs_ == 2 * bundle_) Datagram();
    }
    void Datagram() {
        const uint32_t ts = frames_;
        frames_ += static_cast<uint32_t>(bundle_);
        subs_ = 0;
        const bool allowed = g_allowed.load(), ptt = g_ptt.load();
        const bool send = allowed && (ptt || open_);
        open_ = false;
        if (!send) { prev_sent_ = false; return; }
        if (!prev_sent_) opus_encoder_ctl(enc_, OPUS_RESET_STATE); // a new spurt; the receiver resets at a ts gap
        // the last packet of a spurt: the hangover runs out before the next block, unless new voice reopens it
        const bool end = !ptt && since_voice_ + 1 > kHangoverSubs;
        prev_sent_ = true;
        EncodeAndSend(bundle_, ts, static_cast<uint8_t>((end ? 1 : 0) | (ptt ? 2 : 0)));
    }
    // The partial bundle, its last 10 ms padded with silence (one silent 20 ms frame if nothing is buffered), as a
    // packet of 1..bundle frames with the end flag: listeners stop at once instead of concealing.
    void EndSpurt() {
        const int frames = std::max(1, (subs_ + 1) / 2);
        std::fill(pcm_ + subs_ * kSub, pcm_ + frames * kFrame, 0.0f);
        const uint32_t ts = frames_;
        frames_ += static_cast<uint32_t>(frames);
        subs_ = 0;
        open_ = false;
        prev_sent_ = false;
        EncodeAndSend(frames, ts, static_cast<uint8_t>(1 | (g_ptt.load() ? 2 : 0)));
    }
    void EncodeAndSend(int frames, uint32_t ts, uint8_t flags) {
        opus_repacketizer_init(rp_);
        for (int k = 0; k < frames; k++) {
            const int len = opus_encode_float(enc_, pcm_ + k * kFrame, kFrame, frames_buf_[k], static_cast<opus_int32>(sizeof frames_buf_[k]));
            if (len < 1) { Fail("the Opus encoder failed"); return; }
            if (opus_repacketizer_cat(rp_, frames_buf_[k], len) != OPUS_OK) { Fail("the Opus repacketizer failed"); return; }
        }
        const opus_int32 size = opus_repacketizer_out(rp_, packet_, static_cast<opus_int32>(sizeof packet_));
        if (size < 1) { Fail("the Opus repacketizer failed"); return; }
        g_sending_until = (flags & 1) ? 0 : NowMs() + 20LL * frames + 60;
        try {
            send_(packet_, static_cast<size_t>(size), flags, seq_++, ts);
        } catch (...) {
            Fail("the voice sender threw");
        }
    }

    SendFn send_;
    EchoLink *echo_ = nullptr;
    DenoiseState *ns_ = nullptr;
    OpusEncoder *enc_ = nullptr;
    OpusRepacketizer *rp_ = nullptr;
    dsp::DeviceFormat format_{0, 0, dsp::SampleKind::Float32, 0};
    dsp::Resampler resampler_;
    dsp::HighPass hp_;
    dsp::Agc agc_;
    std::vector<float> mono_, at48_;
    float raw_[kSub] = {}, sub_[kSub] = {};
    int echo_blocks_ = 0;
    bool echo_logged_ = false;
    float pcm_[kMaxBundle * kFrame] = {};
    unsigned char frames_buf_[kMaxBundle][400] = {};
    unsigned char packet_[1500] = {};
    int fill_ = 0, subs_ = 0, bundle_ = 3, since_voice_ = 1000000;
    float prev_voice_ = 0.0f;
    bool open_ = false, prev_sent_ = false;
    uint16_t seq_ = 0;
    uint32_t frames_ = 0;
};

// ---------------------------------------------------------------- playback: jitter buffers, decoders, mix

struct Packet {
    uint8_t seat = 0, flags = 0;
    uint16_t seq = 0;
    uint32_t ts = 0;
    int frames = 0;
    std::vector<uint8_t> data;
};

int32_t TsDiff(uint32_t a, uint32_t b) { return static_cast<int32_t>(a - b); }

struct Seat {
    OpusDecoder *dec = nullptr;
    std::deque<Packet> buffer; // ordered by ts
    bool playing = false, ended = false;
    uint32_t next_ts = 0;
    int target = 1, waited = 0, concealed_run = 0, stable = 0;
    float pcm[kMaxBundle * kFrame] = {};
    int pcm_len = 0, pcm_pos = 0;
    void Clear() {
        buffer.clear();
        playing = ended = false;
        pcm_len = pcm_pos = 0;
        waited = concealed_run = 0;
    }
};

class Mixer {
public:
    explicit Mixer(EchoLink *echo) : echo_(echo) {}
    ~Mixer() {
        for (auto &s : seats_) if (s.dec) opus_decoder_destroy(s.dec);
    }
    bool Init() { // a decoder at the chosen complexity proves the library (OSCE/deep PLC compiled in)
        int err = 0;
        OpusDecoder *probe = opus_decoder_create(kRate, 1, &err);
        if (err != OPUS_OK || !probe) { Log("voice: Opus decoder unavailable (%d)", err); return false; }
        bool ok = true;
        for (int level = ComplexityController::kBottom; level <= ComplexityController::kTop; level++)
            ok = ok && opus_decoder_ctl(probe, OPUS_SET_COMPLEXITY(level)) == OPUS_OK;
        opus_decoder_destroy(probe);
        if (!ok) Log("voice: Opus decoder complexities 5-7 refused");
        g_complexity = control_.Level();
        return ok;
    }
    void Receive(uint8_t seat, uint16_t seq, uint32_t ts, uint8_t flags, const uint8_t *opus, size_t size) {
        if (seat >= kSeats) { // the relay's voice seats are 0-11 (MATCHMAKING_API.md §5c): said once, never silently
            if (!seat_logged_.exchange(true)) Log("voice: a packet for seat %u (beyond %d) is not played", seat, kSeats - 1);
            return;
        }
        if (!opus || size < 1 || size > 400 || silenced_[seat].load()) return;
        const int samples = opus_packet_get_nb_samples(opus, static_cast<opus_int32>(size), kRate);
        if (samples < kFrame || samples > kMaxBundle * kFrame || samples % kFrame) return; // only whole 20 ms frames
        Packet p;
        p.seat = seat; p.seq = seq; p.ts = ts; p.flags = flags; p.frames = samples / kFrame;
        p.data.assign(opus, opus + size);
        last_rx_[seat] = NowMs();
        std::lock_guard lock(inbox_mutex_);
        if (inbox_.size() < kMaxInbox) inbox_.push_back(std::move(p));
    }
    void SetSilenced(uint8_t seat, bool silenced) { if (seat < kSeats) silenced_[seat] = silenced; }
    bool Talking(uint8_t seat) const {
        return seat < kSeats && !silenced_[seat].load() && NowMs() - last_rx_[seat].load() <= kTalkingMs;
    }
    // Talking and audible now: in proximity a talker beyond range is talking but not heard (gain under -60 dB).
    bool Heard(uint8_t seat) const { return Talking(seat) && audible_[seat].load(std::memory_order_relaxed) > 1e-3f; }
    void SetVolume(float v) { volume_ = std::clamp(std::isfinite(v) ? v : 0.0f, 0.0f, 1.0f); }
    // NR-7: the poses of a voice down, stamped with the audio clock (samples mixed so far): applied at the next frame.
    void SetPose(uint8_t seat, const spatial::Pose &talker, const spatial::Pose &self) {
        if (seat >= kSeats) return;
        const double arrived = static_cast<double>(mixed_.load()) / kRate;
        std::lock_guard lock(pose_mutex_);
        if (poses_.size() < kMaxInbox) poses_.push_back({seat, talker, self, arrived});
    }
    // `frames` stereo frames, interleaved L, R.
    size_t Pull(float *out, size_t frames) {
        size_t done = 0;
        while (done < frames) {
            if (mix_pos_ == kFrame) { MixFrame(mix_); mix_pos_ = 0; }
            const size_t take = std::min(frames - done, static_cast<size_t>(kFrame - mix_pos_));
            std::memcpy(out + 2 * done, mix_ + 2 * mix_pos_, 2 * take * sizeof(float));
            mix_pos_ += static_cast<int>(take);
            done += take;
        }
        return done;
    }

private:
    void DrainInbox() {
        std::vector<Packet> in;
        { std::lock_guard lock(inbox_mutex_); in.swap(inbox_); }
        for (auto &p : in) {
            Seat &s = seats_[p.seat];
            if (silenced_[p.seat].load()) continue;
            if (s.playing && TsDiff(p.ts + static_cast<uint32_t>(p.frames), s.next_ts) <= 0) continue; // all of it too late
            auto at = std::find_if(s.buffer.begin(), s.buffer.end(), [&](const Packet &q) { return TsDiff(q.ts, p.ts) >= 0; });
            if (at != s.buffer.end() && at->ts == p.ts) continue; // duplicate
            s.buffer.insert(at, std::move(p));
            while (s.buffer.size() > kMaxBuffered) s.buffer.pop_front(); // keep latency bounded
        }
    }
    bool EnsureDecoder(Seat &s) {
        if (s.dec) return true;
        int err = 0;
        s.dec = opus_decoder_create(kRate, 1, &err);
        if (err != OPUS_OK || !s.dec || opus_decoder_ctl(s.dec, OPUS_SET_COMPLEXITY(control_.Level())) != OPUS_OK) {
            Fail("an Opus decoder could not be created");
            return false;
        }
        return true;
    }
    // Refills `s.pcm` with the next decoded or concealed audio; false when the seat has nothing to play now.
    bool Refill(Seat &s) {
        for (int guard = 0; guard < 8; guard++) {
            if (!s.playing) {
                if (s.buffer.empty()) return false;
                const bool ready = s.buffer.size() >= static_cast<size_t>(s.target) || (s.buffer.back().flags & 1) ||
                    ++s.waited >= 3 * s.target; // never wait more than ~60 ms per target packet
                if (!ready) return false;
                if (!EnsureDecoder(s)) return false;
                opus_decoder_ctl(s.dec, OPUS_RESET_STATE); // a new talk spurt (the encoder reset too)
                s.playing = true;
                s.ended = false;
                s.waited = s.concealed_run = 0;
                s.next_ts = s.buffer.front().ts;
            }
            while (!s.buffer.empty() && TsDiff(s.buffer.front().ts + static_cast<uint32_t>(s.buffer.front().frames), s.next_ts) <= 0)
                s.buffer.pop_front();
            // the expected packet, or one that arrived while its first frames were being concealed
            if (!s.buffer.empty() && TsDiff(s.buffer.front().ts, s.next_ts) <= 0) {
                Packet p = std::move(s.buffer.front());
                s.buffer.pop_front();
                decodes_ += p.frames;
                const int got = opus_decode_float(s.dec, p.data.data(), static_cast<opus_int32>(p.data.size()), s.pcm, p.frames * kFrame, 0);
                const uint32_t end_ts = p.ts + static_cast<uint32_t>(p.frames);
                const int skip = TsDiff(s.next_ts, p.ts) * kFrame; // frames already concealed
                s.next_ts = end_ts;
                if (got <= skip) continue; // a corrupt packet: its frames are concealed instead
                s.pcm_len = got;
                s.pcm_pos = skip;
                s.ended = (p.flags & 1) != 0;
                s.concealed_run = 0;
                if (++s.stable > 500 && s.target > 1) { s.target = 1; s.stable = 0; } // 30 s without an underrun
                return true;
            }
            // the expected packet is missing
            if (s.ended) { s.playing = false; continue; }        // the spurt ended: a later packet is a new spurt
            if (s.buffer.empty()) {                              // underrun
                if (s.concealed_run == 0) { s.target = 2; s.stable = 0; }
                if (s.concealed_run >= kMaxConcealFrames) { s.playing = false; return false; }
            }
            decodes_++;
            const int got = opus_decode_float(s.dec, nullptr, 0, s.pcm, kFrame, 0); // deep PLC
            s.next_ts += 1;
            s.concealed_run++;
            g_concealed++;
            if (got <= 0) return false;
            s.pcm_len = got;
            s.pcm_pos = 0;
            return true;
        }
        return false;
    }
    // NR-7: the poses that arrived since the last frame, the mode and the tuning, for this frame's seats.
    void ApplyPoses() {
        std::vector<PoseUpdate> in;
        { std::lock_guard lock(pose_mutex_); in.swap(poses_); }
        for (const PoseUpdate &u : in) {
            talkers_[u.seat].Update(u.talker, u.arrived);
            scene_.SetListener(u.self, u.arrived);
        }
        if (const uint32_t serial = g_params_serial.load(); serial != params_serial_) { params_serial_ = serial; scene_.params = SpatialParams(); }
        scene_.proximity = static_cast<spatial::Mode>(g_voice_mode.load(std::memory_order_relaxed)) == spatial::Mode::Proximity;
    }
    // One 20 ms stereo frame (interleaved). Each seat's decoded voice (its volume applied) goes through its
    // SeatProcessor: in open mode (and in proximity while this player has no car) that adds exactly the old mono
    // signal to both channels, so open mode is the old mix sample for sample.
    void MixFrame(float *out) {
        DrainInbox();
        ApplyPoses();
        std::fill(out, out + 2 * kFrame, 0.0f);
        const float volume = volume_.load();
        decodes_ = 0;
        int talkers = 0;
        const double started = QpcUs();
        for (int i = 0; i < kSeats; i++) {
            Seat &s = seats_[i];
            filled_[i] = 0;
            if (silenced_[i].load()) {
                if (s.playing || !s.buffer.empty() || s.pcm_len) s.Clear();
                if (spatial_[i].Busy()) spatial_[i].Reset(); // silent at once, not after the delay line's tail
                continue;
            }
            int filled = 0;
            const int decodes_before = decodes_;
            const float gain = volume * g_seat_gains.gain[i].load(std::memory_order_relaxed); // §15: this talker's volume
            float *in = voice_[i].data();
            while (filled < kFrame) {
                if (s.pcm_pos >= s.pcm_len && !Refill(s)) break;
                const int take = std::min(kFrame - filled, s.pcm_len - s.pcm_pos);
                for (int k = 0; k < take; k++) in[filled + k] = s.pcm[s.pcm_pos + k];
                s.pcm_pos += take;
                filled += take;
            }
            // mkwmp: every talker to the same speech level and under a per-talker ceiling, then the player's volumes
            levelers_[i].Process(in, static_cast<size_t>(filled));
            for (int k = 0; k < filled; k++) in[k] *= gain;
            filled_[i] = filled;
            if (filled || decodes_ != decodes_before) talkers++;
        }
        const double cost = QpcUs() - started; // decoding only: the complexity rule's input, as before
        const double t0 = static_cast<double>(mixed_.load()) / kRate;
        for (int i = 0; i < kSeats; i++) {
            if (silenced_[i].load()) continue;
            spatial_[i].Process(voice_[i].data(), filled_[i], kFrame, out, t0, scene_, talkers_[i]);
            audible_[i].store(spatial_[i].AudibleGain(), std::memory_order_relaxed);
        }
        mixed_ += kFrame;
        limiter_.Process(out, kFrame); // mkwmp: the mix never passes -3 dBFS
        for (int k = 0; k < 2 * kFrame; k++) out[k] = std::clamp(out[k], -1.0f, 1.0f);
        // Release 2.0.0: what will be played is the echo canceller's far end (silence too: AEC3 wants every block),
        // unless the output's loopback (game + this mix) is feeding it. NR-7: the final stereo mix as (L + R) / 2, as the
        // loopback path's ToMono does; in open mode that is the old mono mix exactly.
        if (echo::Canceller *canceller = echo_ ? echo_->canceller.get() : nullptr) {
            const bool loopback = echo_->UseLoopback();
            if (g_mix_reference.load(std::memory_order_relaxed) && !loopback) {
                float mono[kFrame];
                for (int k = 0; k < kFrame; k++) mono[k] = (out[2 * k] + out[2 * k + 1]) * 0.5f;
                canceller->Render(mono);
                canceller->Render(mono + kSub);
            }
        }
        // A packet decodes 3 (or 6) frames at once, so the raw time per mix frame is bursty. The load is the cost per
        // 20 ms of audio times the talkers, reported once per 20 ms of audio decoded: the controller's frames are
        // then 20 ms of talking time whatever the bundle.
        if (decodes_) {
            const int talking = std::max(talkers, 1);
            const double load = g_cost_override.load() >= 0 ? g_cost_override.load() : cost * talking / decodes_;
            for (int k = std::max(1, (decodes_ + talking / 2) / talking); k > 0; k--) Adapt(load);
        }
    }
    // The measured decode cost of this frame (all talkers) drives the one adaptive rule.
    void Adapt(double cost_us) {
        const bool changed = control_.OnFrame(cost_us);
        g_cost_p95 = control_.CurrentP95();
        if (!changed) return;
        const auto &c = control_.Last();
        if (c.to < c.from)
            Log("voice: decoder complexity %d -> %d: decode p95 %.0f us per 20 ms over the %.0f us budget", c.from, c.to, c.p95_us, control_.Budget());
        else
            Log("voice: decoder complexity %d -> %d: p95 %.0f us at %d, projected %.0f us at %d (budget %.0f us)", c.from, c.to, c.p95_us, c.from, c.projected_us, c.to, control_.Budget());
        for (auto &s : seats_)
            if (s.dec && opus_decoder_ctl(s.dec, OPUS_SET_COMPLEXITY(c.to)) != OPUS_OK) { Fail("decoder complexity change refused"); return; }
        g_complexity = c.to;
    }

    EchoLink *echo_ = nullptr;
    ComplexityController control_;
    int decodes_ = 0; // 20 ms frames decoded or concealed in this mix frame
    std::array<Seat, kSeats> seats_;
    std::array<std::atomic<bool>, kSeats> silenced_{};
    std::array<std::atomic<int64_t>, kSeats> last_rx_{};
    std::atomic<bool> seat_logged_{false};
    std::atomic<float> volume_{1.0f};
    std::mutex inbox_mutex_;
    std::vector<Packet> inbox_;
    float mix_[2 * kFrame] = {}; // stereo, interleaved
    int mix_pos_ = kFrame;
    // NR-7: per seat its decoded voice of this frame, its spatial processing (a 128 KB delay line each, made at Start)
    // and its car; the listener; the audio clock.
    struct PoseUpdate {
        uint8_t seat;
        spatial::Pose talker, self;
        double arrived;
    };
    std::array<std::array<float, kFrame>, kSeats> voice_{};
    std::array<int, kSeats> filled_{};
    std::array<spatial::SeatProcessor, kSeats> spatial_;
    std::array<spatial::Track, kSeats> talkers_;
    std::array<std::atomic<float>, kSeats> audible_{};
    std::array<loudness::Leveler, kSeats> levelers_;
    loudness::Limiter limiter_;
    spatial::Scene scene_;
    uint32_t params_serial_ = ~0u;
    std::atomic<uint64_t> mixed_{0}; // samples mixed
    std::mutex pose_mutex_;
    std::vector<PoseUpdate> poses_;
};

// ---------------------------------------------------------------- engine lifetime

// The session and its workers are in voice_audio_engine.h (the device backend runs inside them).
const char *const kRoleNames[kRoles] = {"microphone", "audio output", "output loopback"};
constexpr int kMaxAbandon = 3;   // per role and session; then that part of voice stays off
constexpr int kMaxWorkers = 16;  // alive in the process, left-behind ones included: no new session beyond it

std::atomic<int> g_workers_alive{0};
std::atomic<int> g_stuck_ms{8000};
std::atomic<testing::FakeDevices *> g_fake{nullptr};

Session::Session() = default;
Session::~Session() {
    plat::CloseEvent(stop);
    for (plat::Event e : first_try) plat::CloseEvent(e);
    for (plat::Thread &t : threads) plat::CloseThread(t);
}

// exclusive: only to publish or take the session pointer (never across a device call or a wait); shared: every call
// that uses the session, with a try on every path the game's threads take
std::shared_mutex g_life;
std::shared_ptr<Session> g_session;
std::atomic<bool> g_mic_ok{false};    // the last capture attempt opened a microphone (false: listen-only)

int64_t RealMs() { return plat::TickMs(); }
bool Current(const Worker &w) { return w.role >= kRoles || w.s->roles[w.role].gen.load() == w.gen; }
bool Live(const Worker &w) { return w.s->running.load() && !g_failed.load() && Current(w); }
void Busy(const Worker &w) { if (w.role < kRoles && Current(w)) w.s->roles[w.role].busy_since.store(RealMs()); }
void Idle(const Worker &w) { if (w.role < kRoles && Current(w)) w.s->roles[w.role].busy_since.store(0); }
// A wait the watchdog does not count as a stuck device call.
int WaitIdle(const Worker &w, int count, const plat::Event *events, uint32_t ms) {
    Idle(w);
    const int r = plat::WaitAny(events, count, ms);
    Busy(w);
    return r;
}

std::string FormatText(const dsp::DeviceFormat &f) {
    char text[96];
    snprintf(text, sizeof text, "%u Hz, %u channel(s), %s in %u-byte samples", f.rate, f.channels, dsp::KindName(f.kind), f.container);
    return text;
}

// The capture endpoint voice may use (voice_audio_devices.h): the player's chosen microphone while it is there, else the
// default or first microphone; empty: only monitors/loopbacks/default stand-ins, or no input at all.
std::wstring PickFrom(const std::vector<devices::Candidate> &list, int default_index) {
    const std::wstring chosen = MicChoice();
    thread_local std::wstring missing_logged; // per capture worker
    if (devices::ChosenMissing(list, chosen, true)) {
        if (missing_logged != chosen) { missing_logged = chosen; Log("voice: the chosen microphone is not there (or is not a microphone); using the system default"); }
    } else {
        missing_logged.clear();
    }
    const int pick = devices::PickCapture(list, default_index, chosen);
    return pick < 0 ? std::wstring() : list[pick].id;
}

void FirstTry(Session &s, int which, bool ok) {
    if (s.device_ok[which].load() == 0) { s.device_ok[which] = ok ? 1 : 2; if (s.first_try[which]) plat::Signal(s.first_try[which]); }
    else if (ok) s.device_ok[which] = 1;
}

// One device buffer into the chain, only while this worker is the current one (a worker abandoned in a driver call
// that comes back late feeds nothing).
bool Feed(const Worker &w, const dsp::DeviceFormat &f, const uint8_t *data, size_t frames, bool silent) {
    std::lock_guard lock(w.s->capture_lock);
    if (!Live(w)) return false;
    w.s->capture->FeedDevice(f, data, frames, silent);
    return true;
}

void RestartCapture(const Worker &w) {
    std::lock_guard lock(w.s->capture_lock);
    if (Live(w)) w.s->capture->Restart();
}

void MicState(const Worker &w, bool ok) {
    if (!Live(w)) return;
    g_mic_ok = ok;
    FirstTry(*w.s, 0, ok);
}

// The same through the test seam's fake devices (same selection, same chain).
Outcome FakeCaptureSession(const Worker &w, testing::FakeDevices *fake) {
    Session &s = *w.s;
    const uint32_t serial = g_mic_serial.load();
    int default_index = -1;
    const std::vector<devices::Candidate> list = fake->CaptureList(default_index);
    const std::wstring chosen = PickFrom(list, default_index);
    dsp::DeviceFormat format;
    if (chosen.empty() || !fake->OpenCapture(chosen, format)) {
        if (chosen.empty()) Log("voice: no microphone (no input, or only monitor/loopback inputs); listening only, checking every 2 s");
        MicState(w, false);
        return chosen.empty() ? Outcome::Retry : Outcome::RetrySlow;
    }
    MicState(w, true);
    Log("voice: microphone open (%s)", FormatText(format).c_str());
    RestartCapture(w);
    std::vector<uint8_t> buffer;
    Outcome outcome = Outcome::Retry;
    for (;;) {
        if (WaitIdle(w, 1, &s.stop, 5) == 0 || !Live(w)) break;
        const int frames = fake->ReadCapture(buffer);
        if (frames < 0) { Log("voice: microphone lost or its format changed; reopening"); outcome = Outcome::Reopen; break; }
        if (frames > 0 && !Feed(w, format, buffer.data(), static_cast<size_t>(frames), false)) break;
        if (g_mic_serial.load() != serial) { outcome = Outcome::Reopen; break; }
    }
    fake->CloseCapture();
    return outcome;
}

bool PullFromMixer(const Worker &w, float *stereo, size_t frames) {
    std::lock_guard lock(w.s->mix_lock);
    if (!Live(w)) return false;
    w.s->mixer->Pull(stereo, frames);
    return true;
}

void OutputState(const Worker &w, bool ok, const std::wstring &in_use) {
    if (!Live(w)) return;
    FirstTry(*w.s, 1, ok);
    SetOutputInUse(in_use); // §15: the device the mix (and so the echo reference) goes to
}

Outcome FakePlaybackSession(const Worker &w, testing::FakeDevices *fake) {
    Session &s = *w.s;
    const uint32_t serial = g_output_serial.load();
    if (!fake->OpenRender()) { OutputState(w, false, std::wstring()); return Outcome::RetrySlow; }
    OutputState(w, true, L"fake");
    Log("voice: playback open (fake output, 48000 Hz, 2 channel(s))");
    float stereo[2 * kSub];
    Outcome outcome = Outcome::Retry;
    for (;;) {
        if (WaitIdle(w, 1, &s.stop, 5) == 0 || !Live(w)) break;
        if (!PullFromMixer(w, stereo, kSub)) break;
        if (!fake->Render(stereo, kSub)) { Log("voice: audio output lost; retrying"); break; }
        if (g_output_serial.load() != serial) { Log("voice: the player chose another output; reopening"); outcome = Outcome::Reopen; break; }
    }
    if (Live(w)) SetOutputInUse(std::wstring());
    fake->CloseRender();
    return outcome;
}

// ---------------------------------------------------------------- the device workers and their watchdog

void RoleThread(void *arg);
void WatchdogThread(void *arg);

bool SpawnWorker(const std::shared_ptr<Session> &s, int role, uint32_t gen, uint32_t start_delay_ms) {
    auto *w = new (std::nothrow) Worker{s, role, gen, start_delay_ms};
    if (!w) return false;
    g_workers_alive++; // counted from here: a worker that never starts is uncounted below
    plat::Thread t = plat::StartThread(role == kWatchdogRole ? WatchdogThread : RoleThread, w);
    if (!t) { g_workers_alive--; delete w; return false; }
    std::lock_guard lock(s->threads_lock);
    s->threads.push_back(t);
    return true;
}

Outcome RunSession(const Worker &w, WorkerContext *context) {
    testing::FakeDevices *fake = g_fake.load();
    switch (w.role) {
    case kCaptureRole: return fake ? FakeCaptureSession(w, fake) : CaptureSession(w, context);
    case kPlaybackRole: return fake ? FakePlaybackSession(w, fake) : PlaybackSession(w, context);
    default: return fake ? Outcome::Retry : LoopbackSession(w, context);
    }
}

void RoleThread(void *arg) {
    const std::unique_ptr<Worker> w(static_cast<Worker *>(arg));
    Session &s = *w->s;
    FlushDenormals();
    Busy(*w);
    WorkerContext *context = EnterWorker(); // WASAPI: COM (MTA) and the stream's wake event
    try {
        if (w->start_delay_ms && WaitIdle(*w, 1, &s.stop, w->start_delay_ms) == 0) {
            // stopped before the retry
        } else if (!WorkerReady(context)) {
            Log("voice: the %s worker has no %s", kRoleNames[w->role], kWorkerNeeds);
        } else {
            int slow = 0;
            while (Live(*w)) {
                const Outcome outcome = RunSession(*w, context);
                if (!Live(*w)) break;
                uint32_t wait = w->role == kLoopbackRole ? 500 : kRetryMs;
                if (outcome == Outcome::Reopen) wait = 0;
                if (outcome == Outcome::RetrySlow) wait = std::min<uint32_t>(16000, kRetryMs << std::min(slow++, 3));
                else slow = 0;
                if (WaitIdle(*w, 1, &s.stop, wait) == 0) break;
            }
        }
    } catch (...) {
        if (Live(*w)) {
            if (w->role == kLoopbackRole) Log("voice: the output loopback failed; the echo canceller hears the voice mix only"); // voice goes on
            else Fail(w->role == kCaptureRole ? "the capture thread failed" : "the playback thread failed");
        }
    }
    Idle(*w);
    if (Current(*w)) {
        if (w->role < 2) FirstTry(s, w->role, false); // only if it never tried: Start stops waiting
        if (w->role == kLoopbackRole) s.echo.loopback_open = false;
    }
    LeaveWorker(context);
    g_workers_alive--;
    // the session goes with the last reference (this one, for a worker left behind)
}

// A worker whose device call has not returned for g_stuck_ms is left behind; a fresh one retries after a backoff. At
// most kMaxAbandon per role, and never past kMaxWorkers alive: then that part of voice stays off (the game goes on).
void Abandon(const std::shared_ptr<Session> &s, int role, int64_t stuck_ms) {
    RoleState &r = s->roles[role];
    const uint32_t gen = ++r.gen;
    r.busy_since = 0;
    const int n = ++r.abandoned;
    Log("voice: the %s worker's device call has not returned for %lld ms; it is left behind, a fresh one retries", kRoleNames[role],
        static_cast<long long>(stuck_ms));
    if (role == kCaptureRole) g_mic_ok = false;
    if (role == kPlaybackRole) SetOutputInUse(std::wstring());
    if (role == kLoopbackRole) s->echo.loopback_open = false;
    if (n > kMaxAbandon || g_workers_alive.load() >= kMaxWorkers || !SpawnWorker(s, role, gen, kRetryMs * static_cast<uint32_t>(n))) {
        r.active = false;
        Log("voice: the %s does not answer; that part of voice stays off for this session", kRoleNames[role]);
    }
}

void WatchdogThread(void *arg) {
    const std::unique_ptr<Worker> w(static_cast<Worker *>(arg));
    while (plat::WaitAny(&w->s->stop, 1, 100) == -1 && Live(*w)) // -1: timed out (as WAIT_TIMEOUT)
        for (int role = 0; role < kRoles; role++) {
            RoleState &r = w->s->roles[role];
            const int64_t since = r.busy_since.load();
            if (r.active.load() && since && RealMs() - since > g_stuck_ms.load()) Abandon(w->s, role, RealMs() - since);
        }
    g_workers_alive--;
}

// Ends a session: its workers are told to stop and waited for at most `wait_ms` (on the voice thread, never the
// game's); any still in a device call after that are left behind with their own reference.
void Retire(const std::shared_ptr<Session> &s, uint32_t wait_ms) {
    s->running = false;
    if (s->stop) plat::Signal(s->stop);
    std::vector<plat::Thread> threads;
    {
        std::lock_guard lock(s->threads_lock);
        threads = s->threads;
    }
    if (threads.empty() || !wait_ms) return;
    if (!plat::WaitThreads(threads, wait_ms))
        Log("voice: an audio device call has not returned; its worker is left behind (voice stops, the game goes on)");
}

std::shared_ptr<Session> TakeSession() {
    std::unique_lock lock(g_life);
    g_running = false;
    // no permission carries over to the next session: its capture worker sends nothing until VoiceThread's SetTransmit
    g_allowed = false;
    g_ptt = false;
    g_sending_until = 0;
    SetOutputInUse(std::wstring()); // its playback worker, no longer live, does not clear it
    return std::move(g_session);
}

void Publish(const std::shared_ptr<Session> &s) {
    std::unique_lock lock(g_life);
    g_session = s;
    g_running = true;
}

std::shared_ptr<Session> CreateSession(SendFn send) {
    if (g_failed.load()) return nullptr;
    try {
        auto s = std::make_shared<Session>();
        s->echo.canceller = std::make_unique<echo::Canceller>(); // always on (OD-144): voice does not start without it
        s->capture = std::make_unique<CaptureChain>(std::move(send), &s->echo);
        s->mixer = std::make_unique<Mixer>(&s->echo);
        if (!s->capture->Init() || !s->mixer->Init()) { Fail("a codec library failed to start"); return nullptr; }
        if (!s->echo.canceller->Init()) { Fail("the echo canceller failed to start"); return nullptr; }
        s->stop = plat::NewEvent(true);
        if (!s->stop) { Fail("no stop event"); return nullptr; }
        g_concealed = 0;
        return s;
    } catch (...) {
        Fail("the engine could not be created");
        return nullptr;
    }
}

// The session, under a shared lock taken with a try: null when Start/Stop holds the pointer this instant (the caller
// drops that one packet or answers "not talking" for one frame). Never waits.
#define FI_TRY_SESSION(lock, s)                                \
    std::shared_lock lock(g_life, std::try_to_lock);           \
    Session *s = lock.owns_lock() ? g_session.get() : nullptr

} // namespace fi::audio::engine

namespace fi::audio {
using namespace engine;

bool Start(SendFn send) {
    try {
        if (g_running.load()) return true;
        if (const auto old = TakeSession()) Retire(old, 0); // a session that failed on its own
        FlushDenormals();
        if (g_failed.load()) return false;
        if (!g_fake.load() && !BackendReady()) return false; // no audio system (logged once by the backend)
        if (g_workers_alive.load() > kMaxWorkers - 4) {
            static std::atomic<bool> said{false};
            if (!said.exchange(true)) Log("voice: earlier audio device calls never returned; voice stays off for this game session");
            return false;
        }
        const std::shared_ptr<Session> s = CreateSession(std::move(send));
        if (!s) return false;
        for (int i = 0; i < 2; i++) s->first_try[i] = plat::NewEvent(true);
        g_mic_ok = false;
        s->roles[kCaptureRole].active = s->roles[kPlaybackRole].active = true;
        // the watchdog first: a first device call that hangs is caught like any later one
        if (!s->first_try[0] || !s->first_try[1] || !SpawnWorker(s, kWatchdogRole, 0, 0) || !SpawnWorker(s, kCaptureRole, 0, 0) ||
            !SpawnWorker(s, kPlaybackRole, 0, 0)) {
            Log("voice: engine threads unavailable");
            Retire(s, 500);
            return false;
        }
        // the first device attempts, waited for here on the voice thread (no lock held: the game never waits on it)
        plat::WaitAll(s->first_try, 2, 3000);
        const bool mic = s->device_ok[0].load() == 1, out = s->device_ok[1].load() == 1;
        if (!mic && !out) {
            Log("voice: no microphone and no audio output; voice stays off");
            Retire(s, 500);
            return false;
        }
        if (!mic) Log("voice: listening only until a microphone appears");
        if (!out) Log("voice: talking only until an audio output appears");
        if (!g_fake.load()) {
            s->roles[kLoopbackRole].active = true; // 2.0.0: optional, voice goes on without
            if (!SpawnWorker(s, kLoopbackRole, 0, 0)) { s->roles[kLoopbackRole].active = false; Log("voice: no loopback thread; the echo canceller hears the voice mix only"); }
        }
        Publish(s);
        Log("voice: audio engine started (decoder complexity %d)", g_complexity.load());
        return true;
    } catch (...) {
        Log("voice: Start failed");
        return false;
    }
}

void Stop() {
    try {
        if (const auto old = TakeSession()) Retire(old, 1500);
    } catch (...) {
        Log("voice: Stop failed");
    }
}

bool Running() { return g_running.load(); }

bool ListenOnly() { return g_running.load() && !g_mic_ok.load(); }

void SetTransmit(bool allowed, bool push_to_talk) {
    g_ptt = push_to_talk;
    g_allowed = allowed;
    if (!allowed) g_sending_until = 0;
}

void SetBundle(int frames) { g_bundle = frames == 6 ? 6 : 3; }

void Receive(uint8_t seat, uint16_t seq, uint32_t ts, uint8_t flags, const uint8_t *opus, size_t size) {
    try {
        FI_TRY_SESSION(lock, s);
        if (s && g_running.load()) s->mixer->Receive(seat, seq, ts, flags, opus, size);
    } catch (...) {
        Fail("Receive failed");
    }
}

void SetSilenced(uint8_t seat, bool silenced) {
    std::shared_lock lock(g_life); // the voice thread; the exclusive side is only ever a pointer swap
    if (g_session) g_session->mixer->SetSilenced(seat, silenced);
}

void SetVolume(float voices) {
    std::shared_lock lock(g_life);
    if (g_session) g_session->mixer->SetVolume(voices);
}

void SetMicGain(float gain) { g_mic_gain.store(SafeGain(gain), std::memory_order_relaxed); }

void SetSeatGain(uint8_t seat, float gain) {
    if (seat < kSeats) g_seat_gains.gain[seat].store(SafeGain(gain), std::memory_order_relaxed);
}

void SetDevices(const std::string &microphone_id, const std::string &output_id) {
    const std::wstring mic = Wide(microphone_id), out = Wide(output_id);
    std::lock_guard lock(g_choice_lock);
    if (mic != g_mic_choice) { g_mic_choice = mic; g_mic_serial++; }
    if (out != g_output_choice) { g_output_choice = out; g_output_serial++; }
}

std::string OutputDeviceInUse() { return Utf8(OutputInUse()); }

// ListDevices is the device backend's (voice_audio_wasapi.cpp, voice_audio_pulse.cpp).

bool SeatTalking(uint8_t seat) {
    FI_TRY_SESSION(lock, s);
    return s && g_running.load() && s->mixer->Talking(seat);
}

bool SeatHeard(uint8_t seat) {
    FI_TRY_SESSION(lock, s);
    return s && g_running.load() && seat < kSeats && s->mixer->Heard(seat);
}

void SetVoiceMode(VoiceMode mode) {
    const auto m = static_cast<uint8_t>(mode);
    g_voice_mode.store(m <= static_cast<uint8_t>(spatial::Mode::Proximity) ? m : static_cast<uint8_t>(spatial::Mode::Open));
}

void SetPose(uint8_t seat, const spatial::Pose &talker, const spatial::Pose &self) {
    try {
        FI_TRY_SESSION(lock, s);
        if (s && g_running.load()) s->mixer->SetPose(seat, talker, self);
    } catch (...) {
        Fail("SetPose failed");
    }
}

void SetSpatialParams(const spatial::Params &params) {
    {
        std::lock_guard lock(g_params_lock);
        g_params = spatial::Sanitized(params);
    }
    g_params_serial++;
}

bool Sending() { return g_running.load() && g_allowed.load() && NowMs() < g_sending_until.load(); }

namespace testing {

bool StartHeadless(SendFn send) {
    Stop();
    g_failed = false; // a test session starts clean
    g_mic_ok = true;  // the test feeds capture itself
    const std::shared_ptr<Session> s = CreateSession(std::move(send));
    if (!s) return false;
    Publish(s);
    return true;
}

void ProcessCaptureFormat(const dsp::DeviceFormat &format, const uint8_t *data, size_t frames) {
    std::shared_lock lock(g_life);
    if (!g_running.load() || !g_session) return;
    FlushDenormals();
    try {
        std::lock_guard feed(g_session->capture_lock);
        g_session->capture->FeedDevice(format, data, frames, false);
    } catch (...) {
        Fail("capture processing failed");
    }
}

void ProcessCapture(const float *interleaved, size_t frames, uint32_t rate, uint16_t channels) {
    ProcessCaptureFormat(dsp::DeviceFormat{rate, channels, dsp::SampleKind::Float32, 4}, reinterpret_cast<const uint8_t *>(interleaved), frames);
}

size_t PullMixStereo(float *interleaved, size_t frames) {
    std::shared_lock lock(g_life);
    if (!g_running.load() || !g_session) { std::fill(interleaved, interleaved + 2 * frames, 0.0f); return 0; }
    FlushDenormals();
    try {
        std::lock_guard pull(g_session->mix_lock);
        return g_session->mixer->Pull(interleaved, frames);
    } catch (...) {
        Fail("mixing failed");
        return 0;
    }
}

size_t PullMix(float *out, size_t samples) {
    std::vector<float> stereo(2 * samples);
    const size_t got = PullMixStereo(stereo.data(), samples);
    for (size_t i = 0; i < samples; i++) out[i] = (stereo[2 * i] + stereo[2 * i + 1]) * 0.5f; // open mode: exactly the mono mix
    return got;
}

void SetClockMs(int64_t ms) { g_fake_now = ms; }
int DecoderComplexity() { return g_complexity.load(); }
void SetDecodeCostOverrideUs(double us) { g_cost_override = us; }
double DecodeCostP95Us() { return g_cost_p95.load(); }
uint64_t ConcealedFrames() { return g_concealed.load(); }
void SetEchoTap(EchoTap tap) { g_echo_tap.store(tap); }
void SetMixReference(bool on) { g_mix_reference.store(on); }
void SetFakeDevices(FakeDevices *devices) { g_fake.store(devices); }
void SetStuckMs(int ms) { g_stuck_ms.store(ms > 0 ? ms : 8000); }
int WorkersAlive() { return g_workers_alive.load(); }

} // namespace testing
} // namespace fi::audio