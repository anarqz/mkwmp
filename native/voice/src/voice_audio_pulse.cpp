// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-only; see LICENSE and NOTICE.
// mkwmp voice: the audio engine's Linux device backend (voice_audio_engine.h): PulseAudio, or PipeWire's pulse server
// (pipewire-pulse), which serves the same protocol (the Steam Deck's case). libpulse.so.0 is loaded at run time and the
// few functions used here are resolved by name: the library links nothing but libc, and a system without libpulse gets
// "voice is off" (logged once; Start returns false), never a failed load of the whole library.
//
// The rules of voice_audio.cpp's header comment hold here as they do for WASAPI:
// - every connection, stream open, poll and close runs on the session's own device workers (ListDevices: on a helper
//   thread its caller waits for at most 3 s); nothing the game's threads call reaches this file;
// - each worker owns its own connection (a pa_threaded_mainloop and its pa_context) and streams; every wait for the
//   server (connecting, a stream becoming ready, a device list) is a poll with a deadline (2 s) that also watches the
//   session's stop event. The calls that could still block (taking the mainloop's lock while its thread is wedged,
//   stopping that thread) happen while the worker counts as busy, so the watchdog abandons it after 8 s like a hung
//   WASAPI call, and a fresh worker opens a fresh connection;
// - capture: the chosen source, else the default, else the first source that is not a monitor (voice_audio_devices.h;
//   a monitor is a source with monitor_of_sink set, or named like one); none: listen-only, checked every 2 s. The
//   stream is float32le 48 kHz mono (the server resamples and downmixes), read every 10 ms into the capture chain;
// - playback: one float32le 48 kHz stereo stream on the chosen sink, else the default sink (by name, so the sink in use
//   is known), kept 60 ms full (tlength; the stream's own buffer, like WASAPI's padding), topped up every 10 ms;
// - echo reference: a record stream on "<sink in use>.monitor", the loopback's equivalent: the game and the voice mix
//   together, switched against the voice mix by EchoLink's hysteresis as on Windows;
// - every stream is PA_STREAM_DONT_MOVE: a device that goes away fails the stream instead of the server moving it
//   elsewhere unseen, so the device in use (and the echo reference that follows it) is always the one opened. A lost
//   or stalled (2 s) stream is reopened; the default source and sink are polled every 3 s (a change reopens);
//   failures retry every 2 s, backing off to 16 s (the engine's RoleThread).
// Device ids are PulseAudio names (UTF-8), names their descriptions; inside the engine they are held in the same wide
// form as WASAPI ids, so the device rules (voice_audio_devices.h) are shared code. No device name is ever logged.
#include "voice_audio.h"
#include "voice_audio_devices.h"
#include "voice_audio_dsp.h"
#include "voice_audio_engine.h"
#include "voice_echo.h"

#include "voice_log.h"

#include <dlfcn.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <memory>
#include <mutex>
#include <new>
#include <string>
#include <thread>
#include <type_traits>
#include <vector>

namespace fi::audio::engine {
namespace {

// ---------------------------------------------------------------- the slice of libpulse's C API used here

// Declared by hand from libpulse's public headers (pulse/def.h, sample.h, channelmap.h, volume.h, introspect.h,
// stream.h, context.h, thread-mainloop.h), stable since PulseAudio 0.9 and served unchanged by pipewire-pulse. The info
// structs are prefixes: only the leading fields read here are declared, and they are only ever read through pointers
// libpulse hands out.
namespace pa {

struct threaded_mainloop;
struct mainloop_api;
struct context;
struct stream;
struct operation;
struct spawn_api;

enum : int { CONTEXT_UNCONNECTED, CONTEXT_CONNECTING, CONTEXT_AUTHORIZING, CONTEXT_SETTING_NAME, CONTEXT_READY, CONTEXT_FAILED, CONTEXT_TERMINATED };
enum : int { STREAM_UNCONNECTED, STREAM_CREATING, STREAM_READY, STREAM_FAILED, STREAM_TERMINATED };
enum : int { OPERATION_RUNNING, OPERATION_DONE, OPERATION_CANCELLED };
constexpr int SAMPLE_FLOAT32LE = 5;
constexpr int CONTEXT_NOAUTOSPAWN = 0x1;
constexpr int STREAM_DONT_MOVE = 0x200, STREAM_ADJUST_LATENCY = 0x2000;
constexpr int SEEK_RELATIVE = 0;
constexpr uint32_t INVALID_INDEX = UINT32_MAX;
constexpr int CHANNELS_MAX = 32;

struct sample_spec {
    int format; // pa_sample_format_t
    uint32_t rate;
    uint8_t channels;
};
struct buffer_attr {
    uint32_t maxlength, tlength, prebuf, minreq, fragsize;
};
struct channel_map {
    uint8_t channels;
    int map[CHANNELS_MAX]; // pa_channel_position_t
};
struct cvolume {
    uint8_t channels;
    uint32_t values[CHANNELS_MAX];
};
struct server_info {
    const char *user_name, *host_name, *server_version, *server_name;
    sample_spec spec;
    const char *default_sink_name, *default_source_name;
    uint32_t cookie;
    channel_map map;
};
struct source_info { // prefix
    const char *name;
    uint32_t index;
    const char *description;
    sample_spec spec;
    channel_map map;
    uint32_t owner_module;
    cvolume volume;
    int mute;
    uint32_t monitor_of_sink; // INVALID_INDEX: not a monitor
    const char *monitor_of_sink_name;
};
struct sink_info { // prefix
    const char *name;
    uint32_t index;
    const char *description;
};

using server_info_cb = void (*)(context *, const server_info *, void *);
using source_info_cb = void (*)(context *, const source_info *, int eol, void *);
using sink_info_cb = void (*)(context *, const sink_info *, int eol, void *);

struct Api {
    threaded_mainloop *(*threaded_mainloop_new)();
    void (*threaded_mainloop_free)(threaded_mainloop *);
    int (*threaded_mainloop_start)(threaded_mainloop *);
    void (*threaded_mainloop_stop)(threaded_mainloop *);
    void (*threaded_mainloop_lock)(threaded_mainloop *);
    void (*threaded_mainloop_unlock)(threaded_mainloop *);
    mainloop_api *(*threaded_mainloop_get_api)(threaded_mainloop *);
    context *(*context_new)(mainloop_api *, const char *);
    int (*context_connect)(context *, const char *, int, const spawn_api *);
    int (*context_get_state)(const context *);
    int (*context_errno)(const context *);
    void (*context_disconnect)(context *);
    void (*context_unref)(context *);
    operation *(*context_get_server_info)(context *, server_info_cb, void *);
    operation *(*context_get_source_info_list)(context *, source_info_cb, void *);
    operation *(*context_get_sink_info_list)(context *, sink_info_cb, void *);
    int (*operation_get_state)(const operation *);
    void (*operation_cancel)(operation *);
    void (*operation_unref)(operation *);
    stream *(*stream_new)(context *, const char *, const sample_spec *, const channel_map *);
    int (*stream_connect_record)(stream *, const char *, const buffer_attr *, int);
    int (*stream_connect_playback)(stream *, const char *, const buffer_attr *, int, const cvolume *, stream *);
    int (*stream_get_state)(const stream *);
    int (*stream_peek)(stream *, const void **, size_t *);
    int (*stream_drop)(stream *);
    size_t (*stream_readable_size)(const stream *);
    size_t (*stream_writable_size)(const stream *);
    int (*stream_write)(stream *, const void *, size_t, void (*)(void *), int64_t, int);
    int (*stream_disconnect)(stream *);
    void (*stream_unref)(stream *);
    const char *(*strerror)(int);
};

} // namespace pa

// libpulse, loaded once for the process and never unloaded; null when it is not there (said once).
const pa::Api *Pulse() {
    static std::once_flag once;
    static pa::Api api;
    static bool ok = false;
    std::call_once(once, [] {
        void *lib = dlopen("libpulse.so.0", RTLD_NOW | RTLD_LOCAL);
        if (!lib) {
            Log("voice: libpulse.so.0 could not be loaded; voice chat needs PulseAudio or PipeWire's pulse server and is off");
            return;
        }
        bool all = true;
        const auto bind = [&](auto &fn, const char *name) {
            void *p = dlsym(lib, name);
            if (!p) {
                if (all) Log("voice: libpulse.so.0 has no %s; voice chat is off", name);
                all = false;
            }
            fn = reinterpret_cast<std::remove_reference_t<decltype(fn)>>(p);
        };
        bind(api.threaded_mainloop_new, "pa_threaded_mainloop_new");
        bind(api.threaded_mainloop_free, "pa_threaded_mainloop_free");
        bind(api.threaded_mainloop_start, "pa_threaded_mainloop_start");
        bind(api.threaded_mainloop_stop, "pa_threaded_mainloop_stop");
        bind(api.threaded_mainloop_lock, "pa_threaded_mainloop_lock");
        bind(api.threaded_mainloop_unlock, "pa_threaded_mainloop_unlock");
        bind(api.threaded_mainloop_get_api, "pa_threaded_mainloop_get_api");
        bind(api.context_new, "pa_context_new");
        bind(api.context_connect, "pa_context_connect");
        bind(api.context_get_state, "pa_context_get_state");
        bind(api.context_errno, "pa_context_errno");
        bind(api.context_disconnect, "pa_context_disconnect");
        bind(api.context_unref, "pa_context_unref");
        bind(api.context_get_server_info, "pa_context_get_server_info");
        bind(api.context_get_source_info_list, "pa_context_get_source_info_list");
        bind(api.context_get_sink_info_list, "pa_context_get_sink_info_list");
        bind(api.operation_get_state, "pa_operation_get_state");
        bind(api.operation_cancel, "pa_operation_cancel");
        bind(api.operation_unref, "pa_operation_unref");
        bind(api.stream_new, "pa_stream_new");
        bind(api.stream_connect_record, "pa_stream_connect_record");
        bind(api.stream_connect_playback, "pa_stream_connect_playback");
        bind(api.stream_get_state, "pa_stream_get_state");
        bind(api.stream_peek, "pa_stream_peek");
        bind(api.stream_drop, "pa_stream_drop");
        bind(api.stream_readable_size, "pa_stream_readable_size");
        bind(api.stream_writable_size, "pa_stream_writable_size");
        bind(api.stream_write, "pa_stream_write");
        bind(api.stream_disconnect, "pa_stream_disconnect");
        bind(api.stream_unref, "pa_stream_unref");
        bind(api.strerror, "pa_strerror");
        ok = all; // the library stays loaded either way: nothing of it is called unless every symbol is there
    });
    return ok ? &api : nullptr;
}

constexpr uint32_t kServerMs = 2000; // connecting, a stream becoming ready, a device list: each at most this long
constexpr uint32_t kPollMs = 5;
constexpr uint32_t kBytesPerFloat = 4;

// Sleeps `ms`; true when `stop` (may be null) is set.
bool Stopped(plat::Event stop, uint32_t ms) { return plat::WaitAny(&stop, 1, ms) == 0; }

// One connection to the sound server: its own mainloop thread and context, used by one worker (or one ListDevices
// helper) only. Every wait is a poll with a deadline that also watches `stop`.
class Connection {
public:
    Connection(const pa::Api &api, plat::Event stop) : pa_(api), stop_(stop) {}
    Connection(const Connection &) = delete;
    Connection &operator=(const Connection &) = delete;
    ~Connection() {
        if (!ml_) return;
        if (ctx_) {
            pa_.threaded_mainloop_lock(ml_);
            pa_.context_disconnect(ctx_);
            pa_.context_unref(ctx_);
            pa_.threaded_mainloop_unlock(ml_);
        }
        pa_.threaded_mainloop_stop(ml_);
        pa_.threaded_mainloop_free(ml_);
    }

    // False: no server (or it refused us) within kServerMs, or stopped. `error` says why.
    bool Open(std::string &error) {
        ml_ = pa_.threaded_mainloop_new();
        if (!ml_) { error = "no mainloop"; return false; }
        ctx_ = pa_.context_new(pa_.threaded_mainloop_get_api(ml_), "MKWMP voice");
        if (!ctx_) { error = "no context"; return false; }
        if (pa_.context_connect(ctx_, nullptr, pa::CONTEXT_NOAUTOSPAWN, nullptr) < 0) { error = Error(); return false; }
        if (pa_.threaded_mainloop_start(ml_) < 0) { error = "no mainloop thread"; return false; }
        const int64_t deadline = RealMs() + kServerMs;
        for (;;) {
            Lock();
            const int state = pa_.context_get_state(ctx_);
            if (state == pa::CONTEXT_FAILED || state == pa::CONTEXT_TERMINATED) error = Error();
            Unlock();
            if (state == pa::CONTEXT_READY) return true;
            if (state == pa::CONTEXT_FAILED || state == pa::CONTEXT_TERMINATED) return false;
            if (RealMs() > deadline) { error = "the sound server did not answer"; return false; }
            if (Stopped(stop_, kPollMs)) { error = "stopped"; return false; }
        }
    }

    void Lock() { pa_.threaded_mainloop_lock(ml_); }
    void Unlock() { pa_.threaded_mainloop_unlock(ml_); }
    const pa::Api &api() const { return pa_; }
    pa::context *ctx() const { return ctx_; }
    plat::Event stop() const { return stop_; }
    // The context's last error (call with the lock held, or before the mainloop runs).
    std::string Error() const {
        const char *e = pa_.strerror(pa_.context_errno(ctx_));
        return e ? e : "unknown error";
    }

    // Runs `start` (which issues one operation) and waits for it; its callbacks have all run when this returns true.
    template <typename Start> bool Run(Start &&start) {
        Lock();
        pa::operation *op = start();
        Unlock();
        if (!op) return false;
        const int64_t deadline = RealMs() + kServerMs;
        bool done = false;
        for (;;) {
            Lock();
            const int state = pa_.operation_get_state(op);
            Unlock();
            if (state != pa::OPERATION_RUNNING) { done = state == pa::OPERATION_DONE; break; }
            if (RealMs() > deadline || Stopped(stop_, kPollMs)) break;
        }
        Lock();
        if (!done) pa_.operation_cancel(op); // no callback runs after this
        pa_.operation_unref(op);
        Unlock();
        return done;
    }

    struct Source {
        std::string name, description;
        bool monitor = false;
    };
    struct Sink {
        std::string name, description;
    };
    struct Server {
        std::string default_sink, default_source;
    };

    bool GetServer(Server &out) {
        return Run([&] {
            return pa_.context_get_server_info(ctx_, [](pa::context *, const pa::server_info *i, void *u) {
                auto *o = static_cast<Server *>(u);
                if (!i) return;
                o->default_sink = i->default_sink_name ? i->default_sink_name : "";
                o->default_source = i->default_source_name ? i->default_source_name : "";
            }, &out);
        });
    }
    bool GetSources(std::vector<Source> &out) {
        out.clear();
        return Run([&] {
            return pa_.context_get_source_info_list(ctx_, [](pa::context *, const pa::source_info *i, int eol, void *u) {
                if (eol || !i || !i->name) return;
                static_cast<std::vector<Source> *>(u)->push_back(
                    {i->name, i->description ? i->description : "", i->monitor_of_sink != pa::INVALID_INDEX});
            }, &out);
        });
    }
    bool GetSinks(std::vector<Sink> &out) {
        out.clear();
        return Run([&] {
            return pa_.context_get_sink_info_list(ctx_, [](pa::context *, const pa::sink_info *i, int eol, void *u) {
                if (eol || !i || !i->name) return;
                static_cast<std::vector<Sink> *>(u)->push_back({i->name, i->description ? i->description : ""});
            }, &out);
        });
    }

private:
    const pa::Api &pa_;
    plat::Event stop_;
    pa::threaded_mainloop *ml_ = nullptr;
    pa::context *ctx_ = nullptr;
};

// One float32le 48 kHz stream on a connection (destroyed before it).
class Stream {
public:
    explicit Stream(Connection &c) : c_(c), pa_(c.api()) {}
    Stream(const Stream &) = delete;
    Stream &operator=(const Stream &) = delete;
    ~Stream() {
        if (!s_) return;
        c_.Lock();
        pa_.stream_disconnect(s_);
        pa_.stream_unref(s_);
        c_.Unlock();
    }

    // A record stream of `device` (mono, delivered in 10 ms fragments); false: it did not become ready.
    bool OpenRecord(const std::string &device, const char *name, std::string &error) {
        const pa::buffer_attr attr{UINT32_MAX, UINT32_MAX, UINT32_MAX, UINT32_MAX, kSub * kBytesPerFloat};
        return Open(device, name, 1, &attr, true, error);
    }
    // A playback stream on `device` (stereo, kQueuedMs queued).
    bool OpenPlayback(const std::string &device, std::string &error) {
        const pa::buffer_attr attr{UINT32_MAX, static_cast<uint32_t>(kRate / 1000 * kQueuedMs * 2 * kBytesPerFloat), UINT32_MAX, UINT32_MAX,
                                   UINT32_MAX};
        return Open(device, "MKWMP voice", 2, &attr, false, error);
    }

    // Appends everything recorded so far to `out` (mono floats; a hole in the data as silence). False: the stream
    // failed (the device went away, the server died).
    bool Read(std::vector<float> &out) {
        c_.Lock();
        bool ok = pa_.stream_get_state(s_) == pa::STREAM_READY;
        while (ok) {
            const size_t readable = pa_.stream_readable_size(s_);
            if (readable == static_cast<size_t>(-1)) { ok = false; break; }
            if (readable == 0) break;
            const void *data = nullptr;
            size_t bytes = 0;
            if (pa_.stream_peek(s_, &data, &bytes) < 0) { ok = false; break; }
            if (bytes == 0) break;
            const size_t n = bytes / kBytesPerFloat, at = out.size();
            out.resize(at + n);
            if (data) std::memcpy(out.data() + at, data, n * kBytesPerFloat);
            else std::fill(out.begin() + static_cast<std::ptrdiff_t>(at), out.end(), 0.0f);
            pa_.stream_drop(s_);
        }
        c_.Unlock();
        return ok;
    }
    // How many stereo frames the stream takes now; false: the stream failed.
    bool Writable(size_t &frames) {
        c_.Lock();
        const bool ok = pa_.stream_get_state(s_) == pa::STREAM_READY;
        const size_t bytes = ok ? pa_.stream_writable_size(s_) : 0;
        c_.Unlock();
        if (!ok || bytes == static_cast<size_t>(-1)) return false;
        frames = bytes / (2 * kBytesPerFloat);
        return true;
    }
    bool Write(const float *stereo, size_t frames) {
        c_.Lock();
        const int r = pa_.stream_write(s_, stereo, frames * 2 * kBytesPerFloat, nullptr, 0, pa::SEEK_RELATIVE); // copied
        c_.Unlock();
        return r >= 0;
    }

private:
    bool Open(const std::string &device, const char *name, uint8_t channels, const pa::buffer_attr *attr, bool record, std::string &error) {
        const pa::sample_spec spec{pa::SAMPLE_FLOAT32LE, static_cast<uint32_t>(kRate), channels};
        // a record stream asks for 10 ms fragments end to end (ADJUST_LATENCY); playback keeps its whole tlength queued
        // in the stream itself, as WASAPI's padding is: the server's own sink latency comes on top
        const int flags = (record ? pa::STREAM_ADJUST_LATENCY : 0) | pa::STREAM_DONT_MOVE;
        c_.Lock();
        s_ = pa_.stream_new(c_.ctx(), name, &spec, nullptr);
        const int r = !s_ ? -1
            : record ? pa_.stream_connect_record(s_, device.c_str(), attr, flags)
                     : pa_.stream_connect_playback(s_, device.c_str(), attr, flags, nullptr, nullptr);
        if (r < 0) error = c_.Error();
        c_.Unlock();
        if (r < 0) return false;
        const int64_t deadline = RealMs() + kServerMs;
        for (;;) {
            c_.Lock();
            const int state = pa_.stream_get_state(s_);
            if (state == pa::STREAM_FAILED || state == pa::STREAM_TERMINATED) error = c_.Error();
            c_.Unlock();
            if (state == pa::STREAM_READY) return true;
            if (state == pa::STREAM_FAILED || state == pa::STREAM_TERMINATED) return false;
            if (RealMs() > deadline) { error = "the stream did not start"; return false; }
            if (Stopped(c_.stop(), kPollMs)) { error = "stopped"; return false; }
        }
    }

    Connection &c_;
    const pa::Api &pa_;
    pa::stream *s_ = nullptr;
};

// ---------------------------------------------------------------- device lists and the selection rules

// The sources as voice_audio_devices.h sees them, and the default source's index (-1: none). A monitor is a source
// that is one (monitor_of_sink) or is named like one. `logged` (the capture worker's own) logs each rejected source
// once, by kind only (a description can carry a person's name); null logs nothing. False: the server did not answer.
bool CaptureCandidates(Connection &c, std::vector<devices::Candidate> &list, int &default_index, std::vector<std::wstring> *logged) {
    Connection::Server server;
    std::vector<Connection::Source> sources;
    std::vector<Connection::Sink> sinks;
    if (!c.GetServer(server) || !c.GetSources(sources) || !c.GetSinks(sinks)) return false;
    std::vector<std::wstring> sink_names;
    for (const Connection::Sink &k : sinks) sink_names.push_back(Wide(k.description));
    list.clear();
    default_index = -1;
    for (size_t i = 0; i < sources.size(); i++) {
        devices::Candidate cand{Wide(sources[i].name), Wide(sources[i].description), false};
        cand.monitor = sources[i].monitor || devices::NamesAnOutput(cand.name, sink_names);
        const bool is_default = sources[i].name == server.default_source;
        if (is_default) default_index = static_cast<int>(i);
        if (logged && devices::IsMonitorOrLoopback(cand) && std::find(logged->begin(), logged->end(), cand.id) == logged->end()) {
            logged->push_back(cand.id);
            Log("voice: capture source %zu%s is %s; not used", i, is_default ? " (the default)" : "",
                devices::IsDefaultAlias(cand) ? "the sound server's default stand-in" : "a monitor");
        }
        list.push_back(std::move(cand));
    }
    return true;
}

std::vector<devices::Candidate> RenderCandidates(const std::vector<Connection::Sink> &sinks) {
    std::vector<devices::Candidate> list;
    for (const Connection::Sink &k : sinks) list.push_back({Wide(k.name), Wide(k.description), true});
    return list;
}

// The output to open: the player's choice while it is there, else empty (the default sink).
std::wstring SelectOutput(const std::vector<Connection::Sink> &sinks) {
    const std::wstring chosen = OutputChoice();
    if (chosen.empty()) return {};
    return devices::FindChosen(RenderCandidates(sinks), chosen, false) >= 0 ? chosen : std::wstring();
}

// Logs `text` when it differs from the worker's last such line (`last`); an empty text resets it.
void LogChange(std::string &last, const std::string &text) {
    if (!text.empty() && text != last) Log("%s", text.c_str());
    last = text;
}

const dsp::DeviceFormat kMonoFormat{static_cast<uint32_t>(kRate), 1, dsp::SampleKind::Float32, kBytesPerFloat};
const dsp::DeviceFormat kStereoFormat{static_cast<uint32_t>(kRate), 2, dsp::SampleKind::Float32, kBytesPerFloat};

} // namespace

// ---------------------------------------------------------------- the backend (voice_audio_engine.h)

struct WorkerContext {}; // nothing per thread: each device session opens its own connection

const char *const kWorkerNeeds = "thread state";

bool BackendReady() { return Pulse() != nullptr; }
WorkerContext *EnterWorker() { return new (std::nothrow) WorkerContext; }
bool WorkerReady(const WorkerContext *context) { return context && Pulse(); }
void LeaveWorker(WorkerContext *context) { delete context; }

Outcome CaptureSession(const Worker &w, WorkerContext *) {
    Session &s = *w.s;
    const uint32_t serial = g_mic_serial.load();
    thread_local std::string last_error;              // per capture worker: each failure is said once
    thread_local std::vector<std::wstring> logged;    // the rejected sources already said
    Connection conn(*Pulse(), s.stop);
    std::string error;
    std::vector<devices::Candidate> list;
    int default_index = -1;
    if (!conn.Open(error) || !CaptureCandidates(conn, list, default_index, &logged)) {
        LogChange(last_error, "voice: no microphone (sound server: " + (error.empty() ? std::string("no device list") : error) + "); retrying");
        MicState(w, false);
        return Outcome::RetrySlow;
    }
    const std::wstring chosen = PickFrom(list, default_index);
    if (chosen.empty()) {
        LogChange(last_error, "voice: no microphone (no input, or only monitor inputs); listening only, checking every 2 s");
        MicState(w, false);
        return Outcome::Retry;
    }
    Stream stream(conn);
    if (!stream.OpenRecord(Utf8(chosen), "MKWMP voice (microphone)", error)) {
        LogChange(last_error, "voice: no microphone (" + error + "); retrying");
        MicState(w, false);
        return Outcome::RetrySlow;
    }
    last_error.clear();
    MicState(w, true);
    Log("voice: microphone open (PulseAudio source, %s)", FormatText(kMonoFormat).c_str());
    RestartCapture(w);
    Outcome outcome = Outcome::Retry;
    std::vector<float> data;
    int64_t last_check = RealMs(), last_data = last_check;
    for (;;) {
        if (WaitIdle(w, 1, &s.stop, 10) == 0 || !Live(w)) break;
        data.clear();
        if (!stream.Read(data)) { Log("voice: microphone lost; reopening"); outcome = Outcome::Reopen; break; }
        if (!data.empty()) {
            last_data = RealMs();
            if (!Feed(w, kMonoFormat, reinterpret_cast<const uint8_t *>(data.data()), data.size(), false)) break;
        }
        // a live record stream always delivers (silence too); none for 2 s is a device that is gone without saying so
        if (RealMs() - last_data > 2000) { Log("voice: microphone stalled; reopening"); break; }
        if (g_mic_serial.load() != serial) { Log("voice: the player chose another microphone; reopening"); outcome = Outcome::Reopen; break; } // §15
        if (RealMs() - last_check > 3000) { // the microphone to use changed (the default, or one plugged in)
            last_check = RealMs();
            if (CaptureCandidates(conn, list, default_index, &logged) && PickFrom(list, default_index) != chosen) {
                Log("voice: microphone changed; reopening");
                outcome = Outcome::Reopen;
                break;
            }
        }
    }
    return g_mic_serial.load() != serial ? Outcome::Reopen : outcome;
}

Outcome PlaybackSession(const Worker &w, WorkerContext *) {
    Session &s = *w.s;
    const uint32_t serial = g_output_serial.load();
    thread_local std::string last_error;
    thread_local bool refused = false;   // the chosen output would not open: the default until the player picks again
    thread_local uint32_t refused_serial = 0;
    if (refused && refused_serial != serial) refused = false;
    refused_serial = serial;
    Connection conn(*Pulse(), s.stop);
    std::string error;
    Connection::Server server;
    std::vector<Connection::Sink> sinks;
    if (!conn.Open(error) || !conn.GetServer(server) || !conn.GetSinks(sinks)) {
        LogChange(last_error, "voice: no audio output (sound server: " + (error.empty() ? std::string("no device list") : error) + "); retrying");
        OutputState(w, false, std::wstring());
        return Outcome::RetrySlow;
    }
    const std::wstring output = Wide(server.default_sink);
    // §15: the player's output while it is there (empty: the default, followed by the poll below)
    std::wstring chosen = refused ? std::wstring() : SelectOutput(sinks);
    if (!OutputChoice().empty() && chosen.empty() && !refused) {
        thread_local std::wstring missing_logged;
        if (missing_logged != OutputChoice()) { missing_logged = OutputChoice(); Log("voice: the chosen output is not there; using the system default"); }
    }
    const std::wstring target = chosen.empty() ? output : chosen;
    if (target.empty()) {
        LogChange(last_error, "voice: no audio output (no sink); retrying");
        OutputState(w, false, std::wstring());
        return Outcome::Retry;
    }
    Stream stream(conn);
    if (!stream.OpenPlayback(Utf8(target), error)) {
        if (!chosen.empty()) {
            Log("voice: the chosen output refused (%s); using the system default", error.c_str());
            refused = true; // until the player picks again: no reopen loop on a device that will not open
            return Outcome::Reopen;
        }
        LogChange(last_error, "voice: no audio output (" + error + "); retrying");
        OutputState(w, false, std::wstring());
        return Outcome::RetrySlow;
    }
    last_error.clear();
    OutputState(w, true, target);
    Log("voice: playback open (PulseAudio sink, %s, %d ms queued)", FormatText(kStereoFormat).c_str(), kQueuedMs);
    Outcome outcome = Outcome::Retry;
    std::vector<float> mix;
    int64_t last_check = RealMs();
    for (;;) {
        size_t frames = 0;
        if (!stream.Writable(frames)) { Log("voice: audio output lost; reopening"); outcome = Outcome::Reopen; break; }
        if (frames) {
            frames = std::min<size_t>(frames, static_cast<size_t>(kRate / 10)); // at most 100 ms at once
            mix.resize(2 * frames);
            if (!PullFromMixer(w, mix.data(), frames)) break;
            if (!stream.Write(mix.data(), frames)) { Log("voice: audio output lost; reopening"); outcome = Outcome::Reopen; break; }
        }
        if (WaitIdle(w, 1, &s.stop, 10) == 0 || !Live(w)) break;
        if (g_output_serial.load() != serial) { Log("voice: the player chose another output; reopening"); outcome = Outcome::Reopen; break; } // §15
        if (RealMs() - last_check > 3000) {
            last_check = RealMs();
            // the chosen output came back or went away (§15), or (on the default) the default changed
            if (conn.GetServer(server) && conn.GetSinks(sinks)) {
                if (!refused && SelectOutput(sinks) != chosen) { Log("voice: the chosen output came or went; reopening"); outcome = Outcome::Reopen; break; }
                if (chosen.empty() && Wide(server.default_sink) != output) { Log("voice: default audio output changed; reopening"); outcome = Outcome::Reopen; break; }
            }
        }
    }
    if (Live(w)) SetOutputInUse(std::wstring());
    return g_output_serial.load() != serial ? Outcome::Reopen : outcome;
}

// The echo canceller's far end: the monitor of the very sink voice plays to (OutputInUse), which carries the game and
// the voice mix together, as WASAPI's loopback does on Windows. It follows that sink (reopened within 0.5 s of a
// change). Without it (no output open, the monitor refused) the mix is the reference.
Outcome LoopbackSession(const Worker &w, WorkerContext *) {
    Session &s = *w.s;
    const std::wstring target = OutputInUse();
    if (target.empty()) return Outcome::Retry; // no output open: nothing plays, the retry waits
    thread_local std::string last_error;
    Connection conn(*Pulse(), s.stop);
    Stream stream(conn);
    std::string error;
    if (!conn.Open(error) || !stream.OpenRecord(Utf8(target) + ".monitor", "MKWMP voice (echo reference)", error)) {
        LogChange(last_error, "voice: no loopback of the output (" + error + "); the echo canceller hears the voice mix only");
        return Outcome::RetrySlow;
    }
    last_error.clear();
    Log("voice: output loopback open (the sink's monitor, %s); the echo reference once it delivers steadily", FormatText(kMonoFormat).c_str());
    Outcome outcome = Outcome::Retry;
    std::vector<float> data;
    float block[echo::Canceller::kBlock];
    int fill = 0;
    int64_t last_check = RealMs();
    if (Live(w)) s.echo.loopback_open = true;
    for (;;) {
        if (WaitIdle(w, 1, &s.stop, 10) == 0 || !Live(w)) break;
        data.clear();
        if (!stream.Read(data)) { Log("voice: output loopback lost; retrying"); break; }
        if (!Live(w)) break;
        for (const float x : data) {
            block[fill++] = std::clamp(x, -1.0f, 1.0f);
            if (fill == echo::Canceller::kBlock) { fill = 0; s.echo.LoopbackBlock(block); }
        }
        if (RealMs() - last_check > 500) {
            last_check = RealMs();
            if (OutputInUse() != target) { outcome = Outcome::Reopen; break; } // voice moved to another output: follow it
        }
    }
    if (Current(w)) s.echo.loopback_open = false;
    return outcome;
}

// UTF-8 <-> UTF-32 (wchar_t on Linux); an invalid sequence becomes U+FFFD.
std::string Utf8(const std::wstring &w) {
    std::string s;
    s.reserve(w.size());
    for (const wchar_t wc : w) {
        uint32_t c = static_cast<uint32_t>(wc);
        if (c > 0x10FFFF || (c >= 0xD800 && c <= 0xDFFF)) c = 0xFFFD;
        if (c < 0x80) {
            s += static_cast<char>(c);
        } else if (c < 0x800) {
            s += static_cast<char>(0xC0 | (c >> 6));
            s += static_cast<char>(0x80 | (c & 0x3F));
        } else if (c < 0x10000) {
            s += static_cast<char>(0xE0 | (c >> 12));
            s += static_cast<char>(0x80 | ((c >> 6) & 0x3F));
            s += static_cast<char>(0x80 | (c & 0x3F));
        } else {
            s += static_cast<char>(0xF0 | (c >> 18));
            s += static_cast<char>(0x80 | ((c >> 12) & 0x3F));
            s += static_cast<char>(0x80 | ((c >> 6) & 0x3F));
            s += static_cast<char>(0x80 | (c & 0x3F));
        }
    }
    return s;
}

std::wstring Wide(const std::string &s) {
    std::wstring w;
    w.reserve(s.size());
    for (size_t i = 0; i < s.size();) {
        const auto b = static_cast<unsigned char>(s[i]);
        const int len = b < 0x80 ? 1 : (b >> 5) == 0x6 ? 2 : (b >> 4) == 0xE ? 3 : (b >> 3) == 0x1E ? 4 : 0;
        uint32_t c = len == 1 ? b : len == 2 ? (b & 0x1F) : len == 3 ? (b & 0x0F) : (b & 0x07);
        bool ok = len > 0 && i + static_cast<size_t>(len) <= s.size();
        for (int k = 1; ok && k < len; k++) {
            const auto t = static_cast<unsigned char>(s[i + static_cast<size_t>(k)]);
            ok = (t & 0xC0) == 0x80;
            c = (c << 6) | (t & 0x3F);
        }
        static const uint32_t kMin[5] = {0, 0, 0x80, 0x800, 0x10000};
        if (ok && (c < kMin[len] || c > 0x10FFFF || (c >= 0xD800 && c <= 0xDFFF))) ok = false; // overlong, out of range
        w += ok ? static_cast<wchar_t>(c) : L'�';
        i += ok ? static_cast<size_t>(len) : 1;
    }
    return w;
}

} // namespace fi::audio::engine

namespace fi::audio {
using namespace engine;

// "System default" first, then the sources that are not monitors (capture) or the sinks. The enumeration runs on a
// helper thread with its own connection; the caller waits for it at most 3 s, then answers "System default" alone and
// leaves the helper to finish (it owns everything it uses).
std::vector<Device> ListDevices(bool capture) {
    std::vector<Device> out;
    try {
        if (const pa::Api *api = Pulse()) {
            struct Job {
                std::mutex mutex;
                std::condition_variable cv;
                bool done = false;
                std::vector<Device> result;
            };
            auto job = std::make_shared<Job>();
            std::thread([job, api, capture] {
                std::vector<Device> result;
                try {
                    Connection conn(*api, nullptr);
                    std::string error;
                    std::vector<devices::Candidate> list;
                    std::vector<Connection::Sink> sinks;
                    int default_index = -1;
                    const bool ok = conn.Open(error) &&
                        (capture ? CaptureCandidates(conn, list, default_index, nullptr) : conn.GetSinks(sinks));
                    if (ok && !capture) list = RenderCandidates(sinks);
                    if (ok)
                        for (const devices::Choice &c : devices::Choices(list, capture)) result.push_back({Utf8(c.id), Utf8(c.name)});
                } catch (...) {
                    result.clear();
                }
                std::lock_guard lock(job->mutex);
                job->result = std::move(result);
                job->done = true;
                job->cv.notify_all();
            }).detach();
            std::unique_lock lock(job->mutex);
            if (job->cv.wait_for(lock, std::chrono::seconds(3), [&] { return job->done; })) out = job->result;
        }
    } catch (...) {
        out.clear();
    }
    if (out.empty()) out.push_back({}); // "System default" is always there
    return out;
}

} // namespace fi::audio
