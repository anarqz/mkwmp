// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-only; see LICENSE and NOTICE.
// mkwmp voice: the audio engine's Windows device backend (voice_audio_engine.h). WASAPI shared mode both ways (capture,
// playback and the output's loopback for the echo reference) and the endpoint lists, through COM. Every call here
// runs on a device worker of voice_audio.cpp (ListDevices: on its caller's thread, never the game's); the rules in
// voice_audio.cpp's header comment hold.
//
// Wine/Proton audit (Windows/Deck parity). Every API the engine calls, and why it behaves the same on both. "Known":
// Wine's implementation (mmdevapi + winepulse, FAudio-backed xaudio2_9) has it, from its sources and Proton's use by
// games; "inference": reasoned, not yet run here (SAURON's Deck run checks it: voice_audio_test + a device session).
//
// | API / behaviour                                | Windows | Wine/Proton                                   | Engine's choice                                        |
// | ---------------------------------------------- | ------- | --------------------------------------------- | ------------------------------------------------------ |
// | CoInitializeEx(MTA), CoCreateInstance(MMDeviceEnumerator) | yes | known (mmdevapi)                     | used as is                                             |
// | GetDefaultAudioEndpoint(eCapture, eCommunications) | yes | known: accepted; every role maps to the one default (inference) | used; same device as eConsole there   |
// | IMMDevice::GetId, Activate(IAudioClient)       | yes     | known                                         | used                                                   |
// | EnumAudioEndpoints(ACTIVE), GetDevice, OpenPropertyStore(FriendlyName), IMMEndpoint::GetDataFlow | yes | known: winepulse lists the output's monitor as a capture endpoint named "Monitor of ..." | monitors/loopbacks rejected by name (localized too; "<words> <an output's name>"), id and data flow; none left: listen-only |
// | EnumAudioEndpoints: the sound server's default as its own entry | no | known: a "PulseAudio"/PipeWire default stand-in; with no microphone it captures the output's monitor | never opened nor listed (voice_audio_devices.h IsDefaultAlias) |
// | GetMixFormat → WAVEFORMATEXTENSIBLE float32    | usual   | known: winepulse reports extensible float32 at the PulseAudio/PipeWire rate (44.1 or 48 kHz), N channels | any PCM8/16/24/32 or float32/64, any rate/channels, converted by voice_audio_dsp.h; the delivered rate is watched |
// | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM / SRC_DEFAULT_QUALITY | Win10+ | newer Wine only; quality differs (inference) | **not used**: our own downmix + windowed-sinc resampler on both |
// | Shared mode + AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 100 ms buffer | yes | known; Wine rounds the period to its own (~10 ms) | packet sizes are not assumed: every wake drains all packets (copied, released, then processed); 500 ms wait timeout as a fallback |
// | AUDCLNT_E_DEVICE_INVALIDATED on unplug         | yes     | not guaranteed: PulseAudio moves the stream (inference) | also: no data for 2 s = reopen; default device id polled every 3 s (capture and render) |
// | A device call that blocks (a sink switching a2dp ↔ alsa) | rare | known (2026-10-31): mmdevapi/winepulse may block for seconds or more | on device workers only; a watchdog abandons one stuck 8 s, a fresh one retries |
// | IMMNotificationClient                          | yes     | partial (inference)                           | **not used**: polling works on both                    |
// | MMCSS (AvSetMmThreadCharacteristics, avrt.dll) | yes     | stub / may be absent                          | **not used** (no avrt import); plain threads          |
// | FTZ/DAZ via _mm_setcsr on each engine thread   | yes     | known: MXCSR is per thread CPU state, Wine keeps it | set on capture, playback and test-seam entry       |
// | WASAPI shared render, EVENTCALLBACK, the output's mix format | yes | known (winepulse)               | 100 ms buffer kept ~60 ms full; 20 ms wait timeout so a missed event never starves it; our resampler + channel map (L→ch0, R→ch1, mono = (L+R)/2) |
// | IMMDeviceEnumerator::GetDevice(the player's output id, §15) | yes | known                              | refused: the default output, logged once, no reopen loop |
// | XAudio2 for voice                              | yes     | the game's own XAudio2 shares the library     | **not used** (only the one-shot toast chime keeps it)  |
// | Processor affinity / priority, GetActiveProcessorCount, IsProcessorFeaturePresent, wine_get_version | yes | yes | **not used**: no platform input anywhere |
// | QueryPerformanceCounter, GetTickCount64, CreateThread, events | yes | known                                  | used (decode-cost timing, clocks, threads)             |
// | libopus RTCD (cpuid: SSE4.1/AVX2)              | yes     | known: cpuid is the real CPU (Deck: Zen 2, AVX2) | same code paths on both CPUs with AVX2              |
// | WebRTC AEC3 cpu_features (cpuid + xgetbv: AVX2) | yes    | known: both are CPU instructions, no OS call  | same AEC3 paths on both CPUs with AVX2; no audio API   |
// | AUDCLNT_STREAMFLAGS_LOOPBACK on the output in use (echo reference only) | yes | inference: winepulse's monitor; older Wine may refuse | polled every 10 ms (no event); refused: the mix is the reference |
#include "voice_audio.h"
#include "voice_audio_devices.h"
#include "voice_audio_dsp.h"
#include "voice_audio_engine.h"
#include "voice_echo.h"

#include "voice_log.h"

#include <windows.h>
#include <mmreg.h>
#include <audioclient.h>
#include <mmdeviceapi.h>

#include <algorithm>
#include <cstdio>
#include <memory>
#include <string>
#include <vector>

namespace fi::audio::engine {

// The worker's COM apartment (MTA) and the auto-reset event its WASAPI stream signals.
struct WorkerContext {
    HRESULT com = E_FAIL;
    HANDLE event = nullptr;
};

const char *const kWorkerNeeds = "COM/event";

bool BackendReady() { return true; } // WASAPI is part of Windows (and of Wine)

WorkerContext *EnterWorker() {
    auto *c = new (std::nothrow) WorkerContext;
    if (!c) return nullptr;
    c->com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    c->event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    return c;
}

bool WorkerReady(const WorkerContext *c) { return c && SUCCEEDED(c->com) && c->event; }

void LeaveWorker(WorkerContext *c) {
    if (!c) return;
    if (c->event) CloseHandle(c->event);
    if (SUCCEEDED(c->com)) CoUninitialize();
    delete c;
}

namespace {
template <typename T> void SafeRelease(T *&p) { if (p) { p->Release(); p = nullptr; } }
} // namespace

std::string Utf8(const std::wstring &w) {
    if (w.empty()) return {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), static_cast<int>(w.size()), nullptr, 0, nullptr, nullptr);
    std::string s(n > 0 ? n : 0, '\0');
    if (n > 0) WideCharToMultiByte(CP_UTF8, 0, w.c_str(), static_cast<int>(w.size()), s.data(), n, nullptr, nullptr);
    return s;
}

std::wstring Wide(const std::string &s) {
    if (s.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(n > 0 ? n : 0, L'\0');
    if (n > 0) MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
    return w;
}

namespace {

// PKEY_Device_FriendlyName ({a45c254e-df1c-4efd-8020-67d146a850e0}, 14), spelled out: no INITGUID and no extra lib.
constexpr PROPERTYKEY kFriendlyName = {{0xa45c254e, 0xdf1c, 0x4efd, {0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0}}, 14};

std::wstring FriendlyName(IMMDevice *device) {
    std::wstring name;
    IPropertyStore *store = nullptr;
    if (SUCCEEDED(device->OpenPropertyStore(STGM_READ, &store))) {
        PROPVARIANT value;
        PropVariantInit(&value);
        if (SUCCEEDED(store->GetValue(kFriendlyName, &value)) && value.vt == VT_LPWSTR && value.pwszVal) name = value.pwszVal;
        PropVariantClear(&value);
    }
    SafeRelease(store);
    return name;
}

std::vector<std::wstring> EndpointIds(IMMDeviceEnumerator *enumerator, EDataFlow flow, std::vector<IMMDevice *> *devices) {
    std::vector<std::wstring> ids;
    IMMDeviceCollection *all = nullptr;
    UINT count = 0;
    if (SUCCEEDED(enumerator->EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, &all)) && SUCCEEDED(all->GetCount(&count)))
        for (UINT i = 0; i < count; i++) {
            IMMDevice *device = nullptr;
            LPWSTR id = nullptr;
            if (SUCCEEDED(all->Item(i, &device)) && SUCCEEDED(device->GetId(&id))) {
                ids.push_back(id);
                if (devices) { devices->push_back(device); device = nullptr; }
            }
            if (id) CoTaskMemFree(id);
            SafeRelease(device);
        }
    SafeRelease(all);
    return ids;
}

// The active capture endpoints as voice_audio_devices.h sees them, and the default communications input's index (-1:
// none, or the default is not one of the enumerated endpoints: then it is never opened). `logged` (the capture
// worker's own) logs each rejected endpoint once, by kind only: a friendly name can carry a person's name ("...'s
// AirPods"); no audio is ever logged. null logs nothing.
std::vector<devices::Candidate> CaptureCandidates(IMMDeviceEnumerator *enumerator, int &default_index, std::vector<std::wstring> *logged) {
    std::vector<IMMDevice *> devices, outputs;
    const std::vector<std::wstring> ids = EndpointIds(enumerator, eCapture, &devices);
    const std::vector<std::wstring> render = EndpointIds(enumerator, eRender, &outputs);
    std::vector<std::wstring> render_names; // a capture named after one is its monitor (devices::NamesAnOutput)
    for (IMMDevice *&output : outputs) { render_names.push_back(FriendlyName(output)); SafeRelease(output); }
    std::wstring default_id;
    IMMDevice *def = nullptr;
    LPWSTR def_id = nullptr;
    if (SUCCEEDED(enumerator->GetDefaultAudioEndpoint(eCapture, eCommunications, &def)) && SUCCEEDED(def->GetId(&def_id))) default_id = def_id;
    if (def_id) CoTaskMemFree(def_id);
    SafeRelease(def);
    std::vector<devices::Candidate> list;
    default_index = -1;
    for (size_t i = 0; i < ids.size(); i++) {
        devices::Candidate c{ids[i], FriendlyName(devices[i]), std::find(render.begin(), render.end(), ids[i]) != render.end()};
        c.monitor = devices::NamesAnOutput(c.name, render_names);
        IMMEndpoint *endpoint = nullptr;
        EDataFlow flow = eCapture;
        if (SUCCEEDED(devices[i]->QueryInterface(__uuidof(IMMEndpoint), reinterpret_cast<void **>(&endpoint))) &&
            SUCCEEDED(endpoint->GetDataFlow(&flow)) && flow != eCapture)
            c.render = true;
        SafeRelease(endpoint);
        SafeRelease(devices[i]);
        if (ids[i] == default_id) default_index = static_cast<int>(i);
        if (logged && devices::IsMonitorOrLoopback(c) && std::find(logged->begin(), logged->end(), c.id) == logged->end()) {
            logged->push_back(c.id);
            Log("voice: capture endpoint %zu%s is %s; not used", i, ids[i] == default_id ? " (the default)" : "",
                devices::IsDefaultAlias(c) ? "the sound server's default stand-in (it may be a monitor)" : "a monitor/loopback");
        }
        list.push_back(std::move(c));
    }
    return list;
}


std::wstring SelectCapture(IMMDeviceEnumerator *enumerator) {
    thread_local std::vector<std::wstring> logged; // per capture worker
    int default_index = -1;
    const std::vector<devices::Candidate> list = CaptureCandidates(enumerator, default_index, &logged);
    return PickFrom(list, default_index);
}

// The active render endpoints (devices::Candidate, render set), for the output's list and choice.
std::vector<devices::Candidate> RenderCandidates(IMMDeviceEnumerator *enumerator) {
    std::vector<IMMDevice *> devices;
    const std::vector<std::wstring> ids = EndpointIds(enumerator, eRender, &devices);
    std::vector<devices::Candidate> list;
    for (size_t i = 0; i < ids.size(); i++) {
        list.push_back({ids[i], FriendlyName(devices[i]), true});
        SafeRelease(devices[i]);
    }
    return list;
}

// The output to open: the player's choice while it is active, else empty (the default output).
std::wstring SelectOutput(IMMDeviceEnumerator *enumerator) {
    const std::wstring chosen = OutputChoice();
    if (chosen.empty() || !enumerator) return {};
    const std::vector<devices::Candidate> list = RenderCandidates(enumerator);
    return devices::FindChosen(list, chosen, false) >= 0 ? chosen : std::wstring();
}

// The default output's id (eConsole), or empty: polled to follow a changed or lost output on both platforms.
std::wstring DefaultRenderId(IMMDeviceEnumerator *enumerator) {
    std::wstring result;
    IMMDevice *device = nullptr;
    LPWSTR id = nullptr;
    if (enumerator && SUCCEEDED(enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device)) && SUCCEEDED(device->GetId(&id))) result = id;
    if (id) CoTaskMemFree(id);
    SafeRelease(device);
    return result;
}

bool ParseFormat(const WAVEFORMATEX *w, dsp::DeviceFormat &f) {
    if (!w || !w->nChannels || w->nChannels > 32 || w->nSamplesPerSec < 4000 || w->nSamplesPerSec > 384000) return false;
    WORD tag = w->wFormatTag, valid = w->wBitsPerSample;
    if (tag == WAVE_FORMAT_EXTENSIBLE) {
        if (w->cbSize < 22) return false;
        const auto *x = reinterpret_cast<const WAVEFORMATEXTENSIBLE *>(w);
        tag = static_cast<WORD>(x->SubFormat.Data1); // KSDATAFORMAT_SUBTYPE_PCM = 1, _IEEE_FLOAT = 3
        if (x->Samples.wValidBitsPerSample) valid = x->Samples.wValidBitsPerSample;
    }
    const WORD bits = w->wBitsPerSample;
    f.rate = w->nSamplesPerSec;
    f.channels = w->nChannels;
    f.container = static_cast<uint16_t>(bits / 8);
    if (bits % 8 || w->nBlockAlign != f.container * f.channels) return false;
    if (tag == 3 && bits == 32) f.kind = dsp::SampleKind::Float32;
    else if (tag == 3 && bits == 64) f.kind = dsp::SampleKind::Float64;
    else if (tag == 1 && bits == 8) f.kind = dsp::SampleKind::UInt8;
    else if (tag == 1 && bits == 16) f.kind = dsp::SampleKind::Int16;
    else if (tag == 1 && bits == 24) f.kind = dsp::SampleKind::Int24;
    else if (tag == 1 && bits == 32) f.kind = valid > 16 && valid <= 24 ? dsp::SampleKind::Int24 : dsp::SampleKind::Int32;
    else return false;
    return true;
}

// A microphone that delivers at another rate than its format says, seen again after a reopen: that rate is used (the
// capture worker's memory; a new format or device forgets it).
struct RateMemo {
    dsp::DeviceFormat nominal{0, 0, dsp::SampleKind::Float32, 0};
    uint32_t measured = 0;
    int strikes = 0;
};
thread_local RateMemo t_rate;

} // namespace

// One open capture session; returns when stopped, the device fails or must be reopened.
Outcome CaptureSession(const Worker &w, WorkerContext *context) {
    Session &s = *w.s;
    HANDLE ready = context->event;
    const uint32_t serial = g_mic_serial.load();
    IMMDeviceEnumerator *enumerator = nullptr;
    IMMDevice *device = nullptr;
    IAudioClient *client = nullptr;
    IAudioCaptureClient *capture = nullptr;
    WAVEFORMATEX *mix = nullptr;
    dsp::DeviceFormat format;
    std::wstring chosen;
    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator));
    if (SUCCEEDED(hr)) { chosen = SelectCapture(enumerator); if (chosen.empty()) hr = HRESULT_FROM_WIN32(ERROR_NOT_FOUND); }
    if (SUCCEEDED(hr)) hr = enumerator->GetDevice(chosen.c_str(), &device);
    if (SUCCEEDED(hr)) hr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void **>(&client));
    if (SUCCEEDED(hr)) hr = client->GetMixFormat(&mix);
    if (SUCCEEDED(hr) && !ParseFormat(mix, format)) hr = E_NOTIMPL;
    // 100 ms of device buffer: a worker held up (scheduling, a slow block) loses nothing
    if (SUCCEEDED(hr)) hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 1000000, 0, mix, nullptr);
    if (SUCCEEDED(hr)) hr = client->SetEventHandle(ready);
    if (SUCCEEDED(hr)) hr = client->GetService(IID_PPV_ARGS(&capture));
    if (SUCCEEDED(hr)) hr = client->Start();
    thread_local HRESULT last_error = S_OK;
    Outcome outcome = Outcome::Retry;
    if (FAILED(hr)) {
        if (hr != last_error) {
            if (hr == HRESULT_FROM_WIN32(ERROR_NOT_FOUND)) Log("voice: no microphone (no input, or only monitor/loopback inputs); listening only, checking every 2 s");
            else Log("voice: no microphone (0x%08lx); retrying", static_cast<unsigned long>(hr));
        }
        last_error = hr;
        MicState(w, false);
        outcome = hr == HRESULT_FROM_WIN32(ERROR_NOT_FOUND) ? Outcome::Retry : Outcome::RetrySlow;
    } else {
        last_error = S_OK;
        MicState(w, true);
        dsp::DeviceFormat effective = format;
        if (t_rate.strikes >= 2 && t_rate.nominal == format) effective.rate = t_rate.measured;
        Log("voice: microphone open (%s%s)", FormatText(format).c_str(), effective.rate != format.rate ? "; its data is resampled from the rate it really delivers" : "");
        RestartCapture(w);
        dsp::RateWatch watch(effective.rate);
        ULONGLONG last_check = GetTickCount64(), last_data = last_check;
        const size_t stride = static_cast<size_t>(format.container) * format.channels;
        std::vector<uint8_t> packet_copy; // the chain runs on a copy: the device buffer is released at once
        bool done = false;
        while (!done) {
            HANDLE waits[2] = {s.stop, ready};
            if (WaitIdle(w, 2, waits, 500) == 0 || !Live(w)) break;
            UINT32 packet = 0;
            while (SUCCEEDED(hr = capture->GetNextPacketSize(&packet)) && packet) {
                last_data = GetTickCount64();
                BYTE *data = nullptr;
                UINT32 frames = 0;
                DWORD flags = 0;
                hr = capture->GetBuffer(&data, &frames, &flags, nullptr, nullptr);
                if (FAILED(hr)) break;
                const bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                if (!silent && frames) packet_copy.assign(data, data + static_cast<size_t>(frames) * stride);
                capture->ReleaseBuffer(frames);
                // AEC3, RNNoise, Opus and the send run after the release, so they never hold the device buffer
                if (!Feed(w, effective, silent ? nullptr : packet_copy.data(), frames, silent)) { done = true; break; }
                if (const uint32_t measured = watch.Add(frames, RealMs(), (flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0)) {
                    // a Bluetooth profile flip or a relabelled stream: reopen; the same mismatch again: use that rate
                    if (t_rate.nominal == format && t_rate.measured == measured) t_rate.strikes++;
                    else t_rate = {format, measured, 1};
                    Log("voice: the microphone delivers about %.0f Hz while its format says %u Hz; reopening", watch.LastMeasured(), effective.rate);
                    outcome = Outcome::Reopen;
                    done = true;
                    break;
                }
            }
            if (done) break;
            if (FAILED(hr)) {
                if (hr == AUDCLNT_E_DEVICE_INVALIDATED) { Log("voice: microphone lost or its format changed; reopening"); outcome = Outcome::Reopen; }
                else Log("voice: microphone lost (0x%08lx); retrying", static_cast<unsigned long>(hr));
                break;
            }
            // a live shared-mode stream always delivers (silence too); none for 2 s is a lost device that did not say
            // so (Wine/PulseAudio may not report AUDCLNT_E_DEVICE_INVALIDATED)
            if (GetTickCount64() - last_data > 2000) { Log("voice: microphone stalled; reopening"); break; }
            if (g_mic_serial.load() != serial) { Log("voice: the player chose another microphone; reopening"); outcome = Outcome::Reopen; break; } // §15
            if (GetTickCount64() - last_check > 3000) { // the microphone to use changed (the default, or one plugged in)
                last_check = GetTickCount64();
                if (SelectCapture(enumerator) != chosen) { Log("voice: microphone changed; reopening"); outcome = Outcome::Reopen; break; }
            }
        }
        client->Stop();
    }
    if (mix) CoTaskMemFree(mix);
    SafeRelease(capture);
    SafeRelease(client);
    SafeRelease(device);
    SafeRelease(enumerator);
    return g_mic_serial.load() != serial ? Outcome::Reopen : outcome;
}

namespace {

// The mix (48 kHz stereo) in the output's own format: rate, channels (L → 0, R → 1, the rest silent; one channel:
// (L + R) / 2) and sample type.
class RenderConverter {
public:
    void Reset(const dsp::DeviceFormat &f) {
        f_ = f;
        left_.Reset(kRate, f.rate);
        right_.Reset(kRate, f.rate);
        fifo_.clear();
    }
    // `pull` gives 480 frames of the mix (false: stop). Writes `frames` device frames to `out`.
    template <typename Pull> bool Fill(uint8_t *out, size_t frames, Pull &&pull) {
        while (fifo_.size() / 2 < frames) {
            float stereo[2 * kSub], l[kSub], r[kSub];
            if (!pull(stereo, static_cast<size_t>(kSub))) return false;
            for (int i = 0; i < kSub; i++) { l[i] = stereo[2 * i]; r[i] = stereo[2 * i + 1]; }
            left_.Process(l, kSub, lo_);
            right_.Process(r, kSub, ro_);
            const size_t n = std::min(lo_.size(), ro_.size());
            for (size_t i = 0; i < n; i++) { fifo_.push_back(lo_[i]); fifo_.push_back(ro_[i]); }
        }
        const size_t stride = static_cast<size_t>(f_.container) * f_.channels;
        for (size_t i = 0; i < frames; i++) {
            const float l = fifo_[2 * i], r = fifo_[2 * i + 1];
            uint8_t *p = out + i * stride;
            for (uint16_t c = 0; c < f_.channels; c++)
                dsp::WriteSample(f_, p + static_cast<size_t>(c) * f_.container, f_.channels == 1 ? (l + r) * 0.5f : c == 0 ? l : c == 1 ? r : 0.0f);
        }
        fifo_.erase(fifo_.begin(), fifo_.begin() + static_cast<std::ptrdiff_t>(2 * frames));
        return true;
    }

private:
    dsp::DeviceFormat f_;
    dsp::Resampler left_, right_;
    std::vector<float> lo_, ro_, fifo_;
};

} // namespace

// One open playback session (WASAPI shared render).
Outcome PlaybackSession(const Worker &w, WorkerContext *context) {
    Session &s = *w.s;
    HANDLE event = context->event;
    const uint32_t serial = g_output_serial.load();
    IMMDeviceEnumerator *enumerator = nullptr;
    IMMDevice *device = nullptr;
    IAudioClient *client = nullptr;
    IAudioRenderClient *render = nullptr;
    WAVEFORMATEX *mix = nullptr;
    dsp::DeviceFormat format;
    UINT32 buffer_frames = 0;
    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator));
    const std::wstring output = SUCCEEDED(hr) ? DefaultRenderId(enumerator) : std::wstring();
    // §15: the player's output while it is there (empty: the default, followed by the poll below)
    std::wstring chosen = SUCCEEDED(hr) ? SelectOutput(enumerator) : std::wstring();
    thread_local bool refused = false;   // the chosen output would not open: the default until the player picks again
    thread_local uint32_t refused_serial = 0;
    if (refused && refused_serial != serial) refused = false;
    if (refused) chosen.clear();
    refused_serial = serial;
    if (!OutputChoice().empty() && chosen.empty() && !refused) {
        thread_local std::wstring missing_logged;
        if (missing_logged != OutputChoice()) { missing_logged = OutputChoice(); Log("voice: the chosen output is not there; using the system default"); }
    }
    const std::wstring target = chosen.empty() ? output : chosen;
    if (SUCCEEDED(hr) && target.empty()) hr = HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    if (SUCCEEDED(hr)) hr = enumerator->GetDevice(target.c_str(), &device);
    if (SUCCEEDED(hr)) hr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void **>(&client));
    if (SUCCEEDED(hr)) hr = client->GetMixFormat(&mix);
    if (SUCCEEDED(hr) && !ParseFormat(mix, format)) hr = E_NOTIMPL;
    if (SUCCEEDED(hr)) hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 1000000, 0, mix, nullptr);
    if (SUCCEEDED(hr)) hr = client->SetEventHandle(event);
    if (SUCCEEDED(hr)) hr = client->GetBufferSize(&buffer_frames);
    if (SUCCEEDED(hr)) hr = client->GetService(IID_PPV_ARGS(&render));
    thread_local HRESULT last_error = S_OK;
    Outcome outcome = Outcome::Retry;
    if (FAILED(hr) && !chosen.empty()) {
        Log("voice: the chosen output refused (0x%08lx); using the system default", static_cast<unsigned long>(hr));
        refused = true; // until the player picks again: no reopen loop on a device that will not open
        outcome = Outcome::Reopen;
    } else if (FAILED(hr)) {
        if (hr != last_error) Log("voice: no audio output (0x%08lx); retrying", static_cast<unsigned long>(hr));
        last_error = hr;
        OutputState(w, false, std::wstring());
        outcome = hr == HRESULT_FROM_WIN32(ERROR_NOT_FOUND) ? Outcome::Retry : Outcome::RetrySlow;
    } else {
        last_error = S_OK;
        OutputState(w, true, target);
        Log("voice: playback open (%s)", FormatText(format).c_str());
        RenderConverter converter;
        converter.Reset(format);
        const UINT32 queued = std::min<UINT32>(buffer_frames, format.rate * kQueuedMs / 1000);
        bool started = false;
        ULONGLONG last_check = GetTickCount64();
        for (;;) {
            UINT32 padding = 0;
            if (FAILED(hr = client->GetCurrentPadding(&padding))) break;
            if (padding < queued) {
                const UINT32 want = queued - padding;
                BYTE *data = nullptr;
                if (FAILED(hr = render->GetBuffer(want, &data))) break;
                const bool ok = converter.Fill(data, want, [&](float *stereo, size_t frames) { return PullFromMixer(w, stereo, frames); });
                render->ReleaseBuffer(want, ok ? 0 : AUDCLNT_BUFFERFLAGS_SILENT);
                if (!ok) break;
            }
            if (!started) { if (FAILED(hr = client->Start())) break; started = true; }
            HANDLE waits[2] = {s.stop, event};
            if (WaitIdle(w, 2, waits, 20) == 0 || !Live(w)) break;
            if (g_output_serial.load() != serial) { Log("voice: the player chose another output; reopening"); outcome = Outcome::Reopen; break; } // §15
            if (GetTickCount64() - last_check > 3000) {
                last_check = GetTickCount64();
                // the chosen output came back or went away (§15), or (on the default) the default changed
                if (!refused && SelectOutput(enumerator) != chosen) { Log("voice: the chosen output came or went; reopening"); outcome = Outcome::Reopen; break; }
                if (chosen.empty() && DefaultRenderId(enumerator) != output) { Log("voice: default audio output changed; reopening"); outcome = Outcome::Reopen; break; }
            }
        }
        if (hr == AUDCLNT_E_DEVICE_INVALIDATED) { Log("voice: audio output lost or its format changed; reopening"); outcome = Outcome::Reopen; }
        else if (FAILED(hr)) Log("voice: audio output lost (0x%08lx); retrying", static_cast<unsigned long>(hr));
        if (started) client->Stop();
        if (Live(w)) SetOutputInUse(std::wstring());
    }
    if (mix) CoTaskMemFree(mix);
    SafeRelease(render);
    SafeRelease(client);
    SafeRelease(device);
    SafeRelease(enumerator);
    return g_output_serial.load() != serial ? Outcome::Reopen : outcome;
}

// ---------------------------------------------------------------- echo reference: loopback of the output in use

// Release 2.0.0: the echo canceller's far end is what the speakers play. The voice mix alone misses the game (cars,
// music), so while it works the reference is a WASAPI loopback of the very endpoint the voice plays to (OutputInUse:
// the player's chosen output, or the default while it stands in), which carries the game and the voice mix together.
// It follows that endpoint (reopened within 0.5 s of a change). Without it (no output open, loopback refused, as an
// older Wine may: inference, not yet run on a Deck) the mix is the reference, as in the headless tests. EchoLink picks
// one of the two at a time; a switch costs AEC3 a re-alignment (about a second).
Outcome LoopbackSession(const Worker &w, WorkerContext *) {
    Session &s = *w.s;
    const std::wstring target = OutputInUse();
    if (target.empty()) return Outcome::Retry; // no output open: nothing plays, the retry waits
    IMMDeviceEnumerator *enumerator = nullptr;
    IMMDevice *device = nullptr;
    IAudioClient *client = nullptr;
    IAudioCaptureClient *capture = nullptr;
    WAVEFORMATEX *mix = nullptr;
    dsp::DeviceFormat format;
    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator));
    if (SUCCEEDED(hr)) hr = enumerator->GetDevice(target.c_str(), &device);
    if (SUCCEEDED(hr)) hr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void **>(&client));
    if (SUCCEEDED(hr)) hr = client->GetMixFormat(&mix);
    if (SUCCEEDED(hr) && !ParseFormat(mix, format)) hr = E_NOTIMPL;
    // polled every 10 ms: loopback + event callback is not dependable on older Windows nor on Wine (inference)
    if (SUCCEEDED(hr)) hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 200000, 0, mix, nullptr);
    if (SUCCEEDED(hr)) hr = client->GetService(IID_PPV_ARGS(&capture));
    if (SUCCEEDED(hr)) hr = client->Start();
    thread_local HRESULT last_error = S_OK;
    Outcome outcome = Outcome::Retry;
    if (FAILED(hr)) {
        if (hr != last_error)
            Log("voice: no loopback of the output (0x%08lx); the echo canceller hears the voice mix only", static_cast<unsigned long>(hr));
        last_error = hr;
        outcome = Outcome::RetrySlow;
    } else {
        last_error = S_OK;
        Log("voice: output loopback open (%s); the echo reference once it delivers steadily", FormatText(format).c_str());
        dsp::Resampler resampler;
        resampler.Reset(format.rate);
        std::vector<float> mono, at48;
        float block[echo::Canceller::kBlock];
        int fill = 0;
        ULONGLONG last_check = GetTickCount64();
        if (Live(w)) s.echo.loopback_open = true;
        bool done = false;
        while (!done) {
            if (WaitIdle(w, 1, &s.stop, 10) == 0 || !Live(w)) break;
            UINT32 packet = 0;
            while (SUCCEEDED(hr = capture->GetNextPacketSize(&packet)) && packet) {
                BYTE *data = nullptr;
                UINT32 frames = 0;
                DWORD flags = 0;
                hr = capture->GetBuffer(&data, &frames, &flags, nullptr, nullptr);
                if (FAILED(hr)) break;
                if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) || !data) mono.assign(frames, 0.0f);
                else dsp::ToMono(format, data, frames, mono);
                capture->ReleaseBuffer(frames);
                if (!Live(w)) { done = true; break; }
                resampler.Process(mono.data(), mono.size(), at48);
                for (const float x : at48) {
                    block[fill++] = std::clamp(x, -1.0f, 1.0f);
                    if (fill == echo::Canceller::kBlock) { fill = 0; s.echo.LoopbackBlock(block); }
                }
            }
            if (done) break;
            if (FAILED(hr)) { Log("voice: output loopback lost (0x%08lx); retrying", static_cast<unsigned long>(hr)); break; }
            if (GetTickCount64() - last_check > 500) {
                last_check = GetTickCount64();
                if (OutputInUse() != target) { outcome = Outcome::Reopen; break; } // voice moved to another output: follow it
            }
        }
        if (Current(w)) s.echo.loopback_open = false;
        client->Stop();
    }
    if (mix) CoTaskMemFree(mix);
    SafeRelease(capture);
    SafeRelease(client);
    SafeRelease(device);
    SafeRelease(enumerator);
    return outcome;
}

} // namespace fi::audio::engine

namespace fi::audio {
using namespace engine;

std::vector<Device> ListDevices(bool capture) {
    std::vector<Device> out;
    try {
        const HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED); // RPC_E_CHANGED_MODE: COM is there already
        IMMDeviceEnumerator *enumerator = nullptr;
        if (SUCCEEDED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator)))) {
            int default_index = -1;
            const std::vector<devices::Candidate> list = capture ? CaptureCandidates(enumerator, default_index, nullptr) : RenderCandidates(enumerator);
            for (const devices::Choice &c : devices::Choices(list, capture)) out.push_back({Utf8(c.id), Utf8(c.name)});
        }
        SafeRelease(enumerator);
        if (SUCCEEDED(com)) CoUninitialize();
    } catch (...) {
        out.clear();
    }
    if (out.empty()) out.push_back({}); // "System default" is always there
    return out;
}

} // namespace fi::audio
