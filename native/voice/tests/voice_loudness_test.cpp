// mkwmp voice: the listener-side loudness rules (voice_loudness.h). Run by build.py --test.
#include "voice_loudness.h"

#include <cmath>
#include <cstdio>
#include <vector>

using namespace fi::audio::loudness;

static int failures = 0;
static void Check(bool ok, const char *what, double got) {
    std::printf("%s %s (%.2f)\n", ok ? "ok  " : "FAIL", what, got);
    if (!ok) failures++;
}

static double Db(double x) { return 20.0 * std::log10(x + 1e-12); }

// 20 ms of a 220 Hz tone at `amp` (peak), phase continuing across frames.
static std::vector<float> Tone(double amp, int frame) {
    std::vector<float> v(960);
    for (int i = 0; i < 960; i++) v[i] = static_cast<float>(amp * std::sin(2.0 * 3.14159265358979 * 220.0 * (frame * 960 + i) / 48000.0));
    return v;
}
static double Rms(const std::vector<float> &v) {
    double e = 0;
    for (float x : v) e += static_cast<double>(x) * x;
    return std::sqrt(e / v.size());
}
static double Peak(const std::vector<float> &v) {
    double p = 0;
    for (float x : v) p = std::fmax(p, std::fabs(x));
    return p;
}

int main() {
    // A quiet talker (-38 dBFS rms) is lifted, at most +12 dB.
    {
        Leveler l;
        double rms = 0;
        for (int f = 0; f < 400; f++) { auto v = Tone(0.0178, f); l.Process(v.data(), v.size()); rms = Rms(v); }
        Check(std::fabs(Db(rms) - (-38.0 + 12.0)) < 0.6, "quiet talker lifted by the +12 dB cap", Db(rms));
    }
    // A normal talker (-20 dBFS rms) stays where it is.
    {
        Leveler l;
        double rms = 0;
        for (int f = 0; f < 400; f++) { auto v = Tone(0.1414, f); l.Process(v.data(), v.size()); rms = Rms(v); }
        Check(std::fabs(Db(rms) + 20.0) < 0.5, "normal talker unchanged", Db(rms));
    }
    // A scream (-1 dBFS peak) after normal speech: never above -6 dBFS, and down to the target level within 300 ms.
    {
        Leveler l;
        for (int f = 0; f < 200; f++) { auto v = Tone(0.1414, f); l.Process(v.data(), v.size()); }
        double worst = 0, rms = 0;
        for (int f = 200; f < 215; f++) { auto v = Tone(0.89, f); l.Process(v.data(), v.size()); worst = std::fmax(worst, Peak(v)); rms = Rms(v); }
        Check(worst <= 0.5 + 1e-6, "scream never passes -6 dBFS per talker", Db(worst));
        Check(Db(rms) < -15.0, "scream brought near the speech level within 300 ms", Db(rms));
    }
    // Silence and noise below -50 dBFS do not move the level (no pumping in pauses).
    {
        Leveler l;
        for (int f = 0; f < 100; f++) { auto v = Tone(0.1414, f); l.Process(v.data(), v.size()); }
        const double before = l.GainDb();
        for (int f = 0; f < 200; f++) { auto v = Tone(0.001, f); l.Process(v.data(), v.size()); }
        Check(std::fabs(l.GainDb() - before) < 0.01, "gain holds through pauses", l.GainDb() - before);
    }
    // The mix limiter: three screaming talkers summed never pass -3 dBFS, and quiet audio passes untouched.
    {
        Limiter lim;
        std::vector<float> lr(2 * 960);
        double worst = 0;
        for (int f = 0; f < 50; f++) {
            auto v = Tone(0.5, f);
            for (int i = 0; i < 960; i++) lr[2 * i] = lr[2 * i + 1] = 3.0f * v[i];
            lim.Process(lr.data(), 960);
            for (float x : lr) worst = std::fmax(worst, std::fabs(x));
        }
        Check(worst <= Limiter::kCeiling + 1e-6, "mix ceiling -3 dBFS", Db(worst));
        Limiter quiet;
        auto v = Tone(0.1, 0);
        for (int i = 0; i < 960; i++) lr[2 * i] = lr[2 * i + 1] = v[i];
        quiet.Process(lr.data(), 960);
        Check(std::fabs(lr[2 * 100] - v[100]) < 1e-7, "quiet mix untouched", lr[200] - v[100]);
    }
    std::printf("voice_loudness_test: %s\n", failures ? "FAIL" : "PASS");
    return failures ? 1 : 0;
}
