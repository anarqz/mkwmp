// mkwmp voice: the audio engine's inside, shared by the engine (voice_audio.cpp: the codec chains, the mix, the
// session, its device workers and their watchdog, the public API) and the one device backend built with it
// (voice_audio_wasapi.cpp on Windows, voice_audio_pulse.cpp on Linux). A backend only opens, polls and closes devices,
// on the worker it is called on, and talks to the engine through the calls below; the rules it must keep are in
// voice_audio.cpp's header comment. Not a public header: the engine's own sources only.
#pragma once

#include "voice_audio.h"
#include "voice_audio_devices.h"
#include "voice_audio_dsp.h"
#include "voice_audio_platform.h"
#include "voice_echo.h"
#include "voice_log.h"

#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

namespace fi::audio::engine {

constexpr int kRate = 48000, kSub = 480, kFrame = 960, kMaxBundle = 6, kSeats = 24;
constexpr int kQueuedMs = 60;          // the render stream is kept this full: the ~60 ms target
constexpr uint32_t kRetryMs = 2000;

int64_t RealMs(); // the real monotonic clock (never the tests' fake one)

// Codec-level failures: voice is off for the game session (flags only; any thread).
void Fail(const char *what);
extern std::atomic<bool> g_failed;

// §15: the player's chosen devices (endpoint ids; empty: the system default) and the serials bumped on a change.
extern std::atomic<uint32_t> g_mic_serial, g_output_serial;
std::wstring MicChoice();
std::wstring OutputChoice();
// The render endpoint voice plays to now (empty: none open); the echo reference follows it.
std::wstring OutputInUse();
void SetOutputInUse(const std::wstring &id);

// ---------------------------------------------------------------- echo canceller and its far end

// Release 2.0.0, always on (OD-144): AEC3 between the far end (Render) and the microphone (Capture). One per session.
// The far end is the output's loopback (game + voice) while it delivers steadily, else the voice mix; exactly one of
// the two feeds AEC3 at a time, switched with hysteresis (the loopback after 1 s of steady data, back to the mix after
// 300 ms without), so a loopback that comes and goes never feeds AEC3 both references at once.
struct EchoLink {
    std::unique_ptr<echo::Canceller> canceller;
    std::atomic<bool> loopback_open{false};
    std::atomic<int64_t> loopback_data_ms{0}, loopback_since_ms{0};
    std::atomic<bool> use_loopback{false};
    int switches = 0; // the mixer's thread only

    // The mixer's thread, once per 20 ms frame: true while the loopback is the far end.
    bool UseLoopback() {
        const int64_t now = RealMs();
        const bool fresh = loopback_open.load() && now - loopback_data_ms.load() < 300;
        const bool was = use_loopback.load();
        bool use = was;
        if (was && !fresh) use = false;
        else if (!was && fresh && now - loopback_since_ms.load() >= 1000) use = true;
        if (use != was) {
            use_loopback.store(use);
            if (++switches <= 5 || switches % 50 == 0)
                Log("voice: echo reference is now the %s (switch %d)", use ? "output's loopback" : "voice mix", switches);
        }
        return use;
    }
    // The loopback worker: one 10 ms block of what the output plays (48 kHz mono).
    void LoopbackBlock(const float *block) {
        const int64_t now = RealMs();
        if (now - loopback_data_ms.load() > 300) loopback_since_ms.store(now);
        loopback_data_ms.store(now);
        if (use_loopback.load() && canceller) canceller->Render(block);
    }
};

class CaptureChain;
class Mixer;

// ---------------------------------------------------------------- the session and its device workers

// One engine session: the chains, the echo canceller and the device workers that feed them. Shared by the engine (the
// published pointer) and every worker (its own reference), so a worker left behind in a hung driver call never has
// anything freed under it. The workers: capture, playback, the echo reference's loopback, and the watchdog.
enum Role : int { kCaptureRole, kPlaybackRole, kLoopbackRole, kRoles, kWatchdogRole = kRoles };

struct RoleState {
    std::atomic<uint32_t> gen{0};          // bumped when a stuck worker is abandoned: the old one is no longer current
    std::atomic<int64_t> busy_since{0};    // when the current worker left its last wait (0: waiting, or none)
    std::atomic<int> abandoned{0};
    std::atomic<bool> active{false};       // a worker should be running for this role
};

struct Session {
    std::atomic<bool> running{true};
    plat::Event stop = nullptr;            // manual reset: Stop, or a failed Start
    EchoLink echo;                         // before the chains: they point at it
    std::unique_ptr<CaptureChain> capture;
    std::unique_ptr<Mixer> mixer;
    std::mutex capture_lock, mix_lock;     // one worker at a time feeds the chain / pulls the mix (a late one skips)
    RoleState roles[kRoles];
    plat::Event first_try[2] = {};         // set after the capture's and the playback's first device attempt
    std::atomic<int> device_ok[2] = {};    // 1 opened, 2 failed
    std::mutex threads_lock;
    std::vector<plat::Thread> threads;
    Session();
    Session(const Session &) = delete;
    Session &operator=(const Session &) = delete;
    ~Session();
};

struct Worker {
    std::shared_ptr<Session> s;
    int role;
    uint32_t gen;
    uint32_t start_delay_ms;
};

bool Current(const Worker &w);
bool Live(const Worker &w);
// A wait the watchdog does not count as a stuck device call: plat::WaitAny's answer (the signalled event's index).
int WaitIdle(const Worker &w, int count, const plat::Event *events, uint32_t ms);

enum class Outcome { Reopen, Retry, RetrySlow }; // reopen at once; retry in 2 s; retry with backoff (2..16 s)

// One device buffer into the capture chain, only while this worker is the current one; false: stop.
bool Feed(const Worker &w, const dsp::DeviceFormat &f, const uint8_t *data, size_t frames, bool silent);
// A capture gap (device reopened): the gate starts over.
void RestartCapture(const Worker &w);
// The capture attempt's result: a microphone is open (false: listen-only).
void MicState(const Worker &w, bool ok);
// `frames` of the 48 kHz stereo mix (interleaved L, R); false: stop.
bool PullFromMixer(const Worker &w, float *stereo, size_t frames);
// The playback attempt's result and the endpoint the mix now goes to (empty: none).
void OutputState(const Worker &w, bool ok, const std::wstring &in_use);

// The capture endpoint voice may use (voice_audio_devices.h): the player's chosen microphone while it is there, else
// the default or first microphone; empty: only monitors/loopbacks/default stand-ins, or no input at all. Per worker.
std::wstring PickFrom(const std::vector<devices::Candidate> &list, int default_index);
std::string FormatText(const dsp::DeviceFormat &f);

// ---------------------------------------------------------------- the device backend (one per platform)

// False: this system has no audio API voice can use (logged once); Start returns false and voice stays off.
bool BackendReady();
// Each device worker's own per-thread state (WASAPI: COM and its wake event), made on the worker's thread before its
// first device call and released when it ends. Null, or not WorkerReady: the worker opens nothing.
struct WorkerContext;
WorkerContext *EnterWorker();
bool WorkerReady(const WorkerContext *context);
void LeaveWorker(WorkerContext *context);
extern const char *const kWorkerNeeds; // what a worker without a context lacks, for the log
// One open device session each; they return when stopped, or when the device fails or must be reopened. Every device
// call in them happens here, on the worker, between its waits (the watchdog times the rest).
Outcome CaptureSession(const Worker &w, WorkerContext *context);
Outcome PlaybackSession(const Worker &w, WorkerContext *context);
Outcome LoopbackSession(const Worker &w, WorkerContext *context);
// UTF-8 <-> the endpoint ids' wide form (the device rules compare wide strings on every platform).
std::string Utf8(const std::wstring &w);
std::wstring Wide(const std::string &s);

} // namespace fi::audio::engine
