// mkwmp voice: what keeps every voice at the same, safe loudness on the listener's side (VOICE_API.md §5). Header only,
// no Windows, unit-tested on its own (tests/voice_loudness_test.cpp).
//
// The talker's own engine already brings speech to -20 dBFS (dsp::Agc), but that AGC moves 0.5 dB per 10 ms and only
// limits peaks at -0.8 dBFS: a sudden scream, an older client or a hot microphone still arrives far louder than the
// others. So every decoded talker goes through a Leveler (its speech level brought to the same target, a fast cut and
// a slow rise, plus an instant per-talker peak ceiling), and the summed mix through a Limiter (a hard ceiling with an
// instant attack and a smooth release). Nobody is louder than anybody else, and nothing reaches the ears near full
// scale however hard someone yells.
#pragma once

#include <algorithm>
#include <cmath>
#include <cstddef>

namespace fi::audio::loudness {

// One talker's decoded voice (mono, [-1, 1]) to the common speech level. Call once per 20 ms frame with the samples
// that frame had for this talker (n may be 0: nothing changes).
class Leveler {
public:
    static constexpr double kTargetDb = -20.0;   // the speech level every talker is brought to (the senders' AGC target)
    static constexpr double kGateDb = -50.0;     // frames quieter than this are not speech: the level holds
    static constexpr double kMinGainDb = -24.0, kMaxGainDb = 12.0;
    static constexpr double kCutDbPerFrame = 3.0;    // down fast: a shout is tamed within ~100 ms
    static constexpr double kRiseDbPerFrame = 0.25;  // up slowly (12.5 dB/s): no pumping in pauses
    static constexpr float kPeakCeiling = 0.5f;      // -6 dBFS per talker after the gain, instantly

    void Process(float *x, size_t n) {
        if (!n) return;
        double e = 0.0;
        float peak = 0.0f;
        for (size_t i = 0; i < n; i++) {
            e += static_cast<double>(x[i]) * x[i];
            peak = std::max(peak, std::abs(x[i]));
        }
        const double rms_db = 10.0 * std::log10(e / static_cast<double>(n) + 1e-12);
        if (rms_db > kGateDb) {
            // a louder frame pulls the level up quickly (~60 ms), a quieter one down slowly (~400 ms)
            const double a = rms_db > level_db_ ? 0.3 : 0.05;
            level_db_ += a * (rms_db - level_db_);
        }
        const double want = std::clamp(kTargetDb - level_db_, kMinGainDb, kMaxGainDb);
        double next = gain_db_ + std::clamp(want - gain_db_, -kCutDbPerFrame, kRiseDbPerFrame);
        // the ceiling: this frame's loudest sample never passes -6 dBFS
        if (peak > 0.0f && peak * DbToGain(next) > kPeakCeiling) next = 20.0 * std::log10(kPeakCeiling / peak);
        // ramp from the last gain to the new one across the frame (no zipper noise)
        const float g0 = static_cast<float>(DbToGain(gain_db_)), g1 = static_cast<float>(DbToGain(next));
        for (size_t i = 0; i < n; i++) {
            const float g = g0 + (g1 - g0) * static_cast<float>(i + 1) / static_cast<float>(n);
            x[i] = std::clamp(x[i] * g, -kPeakCeiling, kPeakCeiling);
        }
        gain_db_ = next;
    }
    double GainDb() const { return gain_db_; }
    double LevelDb() const { return level_db_; }

private:
    static double DbToGain(double db) { return std::pow(10.0, db / 20.0); }
    double level_db_ = kTargetDb, gain_db_ = 0.0;
};

// The stereo mix's ceiling: -3 dBFS, an instant attack (no sample passes it), a 150 ms release.
class Limiter {
public:
    static constexpr float kCeiling = 0.7079f; // -3 dBFS
    // `frames` interleaved stereo frames, in place.
    void Process(float *lr, size_t frames) {
        for (size_t i = 0; i < frames; i++) {
            float &l = lr[2 * i], &r = lr[2 * i + 1];
            const float peak = std::max(std::abs(l), std::abs(r));
            const float need = peak > kCeiling ? kCeiling / peak : 1.0f;
            env_ = need < env_ ? need : env_ + (1.0f - env_) * kRelease;
            l = std::clamp(l * env_, -kCeiling, kCeiling);
            r = std::clamp(r * env_, -kCeiling, kCeiling);
        }
    }
    float Envelope() const { return env_; }

private:
    static constexpr float kRelease = 1.0f / (0.150f * 48000.0f);
    float env_ = 1.0f;
};

} // namespace fi::audio::loudness
