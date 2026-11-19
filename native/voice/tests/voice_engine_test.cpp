// The audio engine through its test seam (voice_audio_testing.h), on any platform: no device, no sound server.
// 1. headless: a tone through capture -> AEC3 -> AGC -> RNNoise -> Opus -> packets, the packets back through the jitter
//    buffer, the decoder and the mix; 2. fake devices: Start with only a monitor input is listen-only, and Stop is
//    quick; 3. a device call that never returns: the watchdog abandons it, Stop still returns within 1.5 s, and the
//    workers left behind end once the call returns.
#include "voice_audio.h"
#include "voice_audio_testing.h"
#include "voice_log.h"

#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace audio = fi::audio;

namespace {

std::mutex g_log_lock;
std::vector<std::string> g_lines;

int failures = 0;
#define CHECK(cond)                                                                    \
    do {                                                                               \
        if (!(cond)) { std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); failures++; } \
    } while (0)

int Count(const char *needle) {
    std::lock_guard lock(g_log_lock);
    int n = 0;
    for (const std::string &l : g_lines) n += l.find(needle) != std::string::npos;
    return n;
}

int64_t Ms() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

struct Packet {
    std::vector<uint8_t> data;
    uint8_t flags;
    uint16_t seq;
    uint32_t ts;
};

void Headless() {
    std::mutex lock;
    std::vector<Packet> sent;
    CHECK(audio::testing::StartHeadless([&](const uint8_t *opus, size_t size, uint8_t flags, uint16_t seq, uint32_t ts) {
        std::lock_guard l(lock);
        sent.push_back({std::vector<uint8_t>(opus, opus + size), flags, seq, ts});
    }));
    audio::SetTransmit(true, true); // push-to-talk held: the gate is open whatever RNNoise says
    std::vector<float> block(480);
    for (int n = 0, t = 0; n < 100; n++) { // 1 s
        for (float &x : block) x = 0.3f * static_cast<float>(std::sin(2 * 3.14159265 * 220.0 * t++ / 48000.0));
        audio::testing::ProcessCapture(block.data(), block.size(), 48000, 1);
    }
    CHECK(audio::Sending());
    audio::SetTransmit(false, false);
    std::printf("headless: %zu packets sent\n", sent.size());
    CHECK(sent.size() >= 15);
    audio::testing::SetClockMs(1000);
    for (const Packet &p : sent) audio::Receive(3, p.seq, p.ts, p.flags, p.data.data(), p.data.size());
    CHECK(audio::SeatTalking(3));
    CHECK(!audio::SeatTalking(4));
    std::vector<float> mix(48000);
    audio::testing::PullMix(mix.data(), mix.size());
    double energy = 0;
    for (const float x : mix) energy += static_cast<double>(x) * x;
    const double rms = std::sqrt(energy / mix.size());
    std::printf("headless: mix rms %.4f, decoder complexity %d\n", rms, audio::testing::DecoderComplexity());
    CHECK(rms > 0.01);
    audio::testing::SetClockMs(-1);
    audio::Stop();
    CHECK(!audio::Running());
}

struct Fake : audio::testing::FakeDevices {
    bool monitors_only = false;
    std::atomic<bool> hang_render{false};
    std::mutex m;
    std::condition_variable cv;
    bool release = false;
    std::vector<audio::devices::Candidate> CaptureList(int &default_index) override {
        default_index = 0;
        if (monitors_only) return {{L"alsa_output.pci.analog-stereo.monitor", L"Monitor of Built-in Audio", false, true}};
        return {{L"mic", L"Built-in Microphone", false, false}};
    }
    bool OpenCapture(const std::wstring &, audio::dsp::DeviceFormat &format) override {
        format = {48000, 1, audio::dsp::SampleKind::Float32, 4};
        return true;
    }
    int ReadCapture(std::vector<uint8_t> &buffer) override {
        buffer.assign(480 * 4, 0);
        return 480;
    }
    void CloseCapture() override {}
    bool OpenRender() override {
        if (hang_render.load()) { // a driver call that never returns, until the test lets it go
            std::unique_lock l(m);
            cv.wait(l, [&] { return release; });
            return false;
        }
        return true;
    }
    bool Render(const float *, size_t) override { return true; }
    void CloseRender() override {}
};

void FakeListenOnly(Fake &fake) {
    fake.monitors_only = true;
    audio::testing::SetFakeDevices(&fake);
    CHECK(audio::Start([](const uint8_t *, size_t, uint8_t, uint16_t, uint32_t) {}));
    CHECK(audio::Running());
    CHECK(audio::ListenOnly());
    // the playback worker signals its first attempt before it publishes the output in use (OutputState): wait for it
    for (int i = 0; i < 100 && audio::OutputDeviceInUse().empty(); i++) std::this_thread::sleep_for(std::chrono::milliseconds(10));
    CHECK(audio::OutputDeviceInUse() == "fake");
    const int64_t t0 = Ms();
    audio::Stop();
    const int64_t took = Ms() - t0;
    std::printf("fake listen-only: Stop took %lld ms\n", static_cast<long long>(took));
    CHECK(took < 500);
    CHECK(!audio::Running());
    CHECK(audio::OutputDeviceInUse().empty());
    for (int i = 0; i < 100 && audio::testing::WorkersAlive() > 0; i++) std::this_thread::sleep_for(std::chrono::milliseconds(10));
    CHECK(audio::testing::WorkersAlive() == 0);
}

void FakeStuck(Fake &fake) {
    fake.monitors_only = false;
    fake.hang_render = true;
    audio::testing::SetFakeDevices(&fake);
    audio::testing::SetStuckMs(300);
    const int64_t t0 = Ms();
    CHECK(audio::Start([](const uint8_t *, size_t, uint8_t, uint16_t, uint32_t) {})); // a microphone: talking only
    std::printf("fake stuck output: Start took %lld ms\n", static_cast<long long>(Ms() - t0));
    CHECK(audio::Running());
    CHECK(!audio::ListenOnly());
    for (int i = 0; i < 200 && Count("left behind, a fresh one retries") == 0; i++) std::this_thread::sleep_for(std::chrono::milliseconds(10));
    CHECK(Count("left behind, a fresh one retries") >= 1);
    const int64_t t1 = Ms();
    audio::Stop();
    const int64_t took = Ms() - t1;
    std::printf("fake stuck output: Stop took %lld ms, %d worker(s) left behind\n", static_cast<long long>(took), audio::testing::WorkersAlive());
    CHECK(took <= 1600);
    CHECK(!audio::Running());
    {
        std::lock_guard l(fake.m);
        fake.release = true;
    }
    fake.cv.notify_all();
    for (int i = 0; i < 300 && audio::testing::WorkersAlive() > 0; i++) std::this_thread::sleep_for(std::chrono::milliseconds(10));
    CHECK(audio::testing::WorkersAlive() == 0);
    audio::testing::SetStuckMs(0);
    audio::testing::SetFakeDevices(nullptr);
}

} // namespace

namespace fi {
void Log(const char *format, ...) {
    char line[512];
    va_list ap;
    va_start(ap, format);
    std::vsnprintf(line, sizeof line, format, ap);
    va_end(ap);
    std::lock_guard lock(g_log_lock);
    g_lines.push_back(line);
    if (std::getenv("VOICE_TEST_LOG")) std::printf("  log: %s\n", line);
}
} // namespace fi

int main() {
    Headless();
    static Fake fake; // outlives every worker, the abandoned ones included
    FakeListenOnly(fake);
    FakeStuck(fake);
    if (failures) {
        std::printf("voice_engine_test: %d failure(s)\n", failures);
        return 1;
    }
    std::printf("voice_engine_test: ok\n");
    return 0;
}
