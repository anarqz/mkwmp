// Linux smoke test of the PulseAudio backend on this machine's real sound server (build.py --target linux --smoke).
// Lists the devices, starts the engine with a sender that drops everything and no talkers (so only silence is
// played), runs ~3 s, prints Running/ListenOnly/OutputDeviceInUse, and checks that Stop returns within 1.5 s. With
// the built library's path as argv[1] it also loads it and checks its C ABI (mkwmp_voice_abi, devices_json).
#include "mkwmp_voice.h"
#include "voice_audio.h"
#include "voice_log.h"

#include <dlfcn.h>

#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <thread>

namespace fi {
void Log(const char *format, ...) {
    char line[512];
    va_list ap;
    va_start(ap, format);
    std::vsnprintf(line, sizeof line, format, ap);
    va_end(ap);
    std::printf("  log: %s\n", line);
    std::fflush(stdout);
}
} // namespace fi

namespace audio = fi::audio;

int main(int argc, char **argv) {
    int failures = 0;
    for (const bool capture : {true, false}) {
        const auto list = audio::ListDevices(capture);
        std::printf("ListDevices(%s): %zu\n", capture ? "true" : "false", list.size());
        for (const auto &d : list) std::printf("  id=\"%s\" name=\"%s\"\n", d.id.c_str(), d.id.empty() ? "System default" : d.name.c_str());
        if (list.empty() || !list[0].id.empty()) { std::printf("FAIL: \"System default\" is not first\n"); failures++; }
    }
    const bool started = audio::Start([](const uint8_t *, size_t, uint8_t, uint16_t, uint32_t) {});
    std::printf("Start: %s\n", started ? "true" : "false");
    if (!started) failures++;
    std::this_thread::sleep_for(std::chrono::seconds(3));
    std::printf("Running: %s\n", audio::Running() ? "true" : "false");
    std::printf("ListenOnly: %s\n", audio::ListenOnly() ? "true" : "false");
    std::printf("OutputDeviceInUse: \"%s\"\n", audio::OutputDeviceInUse().c_str());
    const auto t0 = std::chrono::steady_clock::now();
    audio::Stop();
    const auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - t0).count();
    std::printf("Stop: %lld ms (limit 1500)\n", static_cast<long long>(ms));
    if (ms > 1500) failures++;
    std::printf("Running after Stop: %s\n", audio::Running() ? "true" : "false");
    if (audio::Running()) failures++;

    if (argc > 1) {
        void *lib = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
        if (!lib) {
            std::printf("FAIL: dlopen %s: %s\n", argv[1], dlerror());
            failures++;
        } else {
            auto abi = reinterpret_cast<mkwmp_voice_abi_fn>(dlsym(lib, "mkwmp_voice_abi"));
            auto json = reinterpret_cast<int (*)(char *, int)>(dlsym(lib, "mkwmp_voice_devices_json"));
            static char buf[16384];
            const int n = json ? json(buf, sizeof buf) : -2;
            std::printf("%s: abi %d, devices_json %d bytes: %s\n", argv[1], abi ? abi() : -1, n, n > 0 ? buf : "");
            if (!abi || abi() != MKWMP_VOICE_ABI || n <= 0) failures++;
        }
    }
    std::printf("voice_pulse_smoke: %s\n", failures ? "FAILED" : "ok");
    return failures ? 1 : 0;
}
