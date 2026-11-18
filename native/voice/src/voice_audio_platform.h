// mkwmp voice: the few OS primitives the audio engine's session machinery uses (voice_audio.cpp): events, worker
// threads that can be waited for with a timeout and then left behind, and the clocks. On Windows each one is the very
// Win32 call the engine always made (CreateEventW, WaitForMultipleObjects, CreateThread, GetTickCount64,
// QueryPerformanceCounter); on Linux the same semantics on std::thread and a condition variable.
#pragma once

#include <cstdint>
#include <vector>

#ifdef _WIN32
#include <windows.h>
#else
#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <ctime>
#include <memory>
#include <mutex>
#include <new>
#include <thread>
#endif

namespace fi::audio::plat {

using ThreadFn = void (*)(void *);

#ifdef _WIN32

using Event = HANDLE;  // null: none
using Thread = HANDLE; // null: none

inline Event NewEvent(bool manual_reset) { return CreateEventW(nullptr, manual_reset ? TRUE : FALSE, FALSE, nullptr); }
inline void Signal(Event e) { SetEvent(e); }
inline void CloseEvent(Event e) { if (e) CloseHandle(e); }
// The index of the first signalled event; -1 when `ms` passed; -2 when the wait itself failed. An auto-reset event is
// consumed.
inline int WaitAny(const Event *events, int count, uint32_t ms) {
    const DWORD r = WaitForMultipleObjects(static_cast<DWORD>(count), events, FALSE, ms);
    if (r < WAIT_OBJECT_0 + static_cast<DWORD>(count)) return static_cast<int>(r - WAIT_OBJECT_0);
    return r == WAIT_TIMEOUT ? -1 : -2;
}
// True when every event is signalled within `ms`.
inline bool WaitAll(const Event *events, int count, uint32_t ms) {
    return WaitForMultipleObjects(static_cast<DWORD>(count), events, TRUE, ms) != WAIT_TIMEOUT;
}

namespace detail {
struct Start {
    ThreadFn fn;
    void *arg;
};
inline DWORD WINAPI Trampoline(void *p) {
    const Start start = *static_cast<Start *>(p);
    delete static_cast<Start *>(p);
    start.fn(start.arg);
    return 0;
}
} // namespace detail

// A running thread (null: it could not be created; `fn` never runs then).
inline Thread StartThread(ThreadFn fn, void *arg) {
    auto *start = new (std::nothrow) detail::Start{fn, arg};
    if (!start) return nullptr;
    HANDLE t = CreateThread(nullptr, 0, detail::Trampoline, start, 0, nullptr);
    if (!t) delete start;
    return t;
}
inline void CloseThread(Thread &t) { if (t) CloseHandle(t); t = nullptr; }
// True when every thread has returned within `ms` (at most MAXIMUM_WAIT_OBJECTS are waited for).
inline bool WaitThreads(std::vector<Thread> threads, uint32_t ms) {
    if (threads.size() > MAXIMUM_WAIT_OBJECTS) threads.resize(MAXIMUM_WAIT_OBJECTS);
    return WaitForMultipleObjects(static_cast<DWORD>(threads.size()), threads.data(), TRUE, ms) != WAIT_TIMEOUT;
}

inline int64_t TickMs() { return static_cast<int64_t>(GetTickCount64()); }
inline double PerfUs() {
    static const double ticks_per_us = [] { LARGE_INTEGER f; QueryPerformanceFrequency(&f); return f.QuadPart / 1e6; }();
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return c.QuadPart / ticks_per_us;
}

#else

// Every event shares one mutex and condition variable: events are set rarely (stop, a first device attempt, a thread's
// end), so one wakeup for all waiters costs nothing, and a wait on several events needs no more. Never destroyed: a
// worker left behind may still wait on it while the process exits.
namespace detail {
struct Hub {
    std::mutex mutex;
    std::condition_variable cv;
};
inline Hub &TheHub() {
    static Hub *hub = new Hub;
    return *hub;
}
} // namespace detail

struct EventObject {
    bool manual_reset = false;
    bool signalled = false;
};
using Event = EventObject *; // null: none

inline Event NewEvent(bool manual_reset) {
    auto *e = new (std::nothrow) EventObject;
    if (e) e->manual_reset = manual_reset;
    return e;
}
inline void Signal(Event e) {
    if (!e) return;
    detail::Hub &hub = detail::TheHub();
    { std::lock_guard lock(hub.mutex); e->signalled = true; }
    hub.cv.notify_all();
}
inline void CloseEvent(Event e) { delete e; }

inline int WaitAny(const Event *events, int count, uint32_t ms) {
    detail::Hub &hub = detail::TheHub();
    std::unique_lock lock(hub.mutex);
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(ms);
    for (;;) {
        for (int i = 0; i < count; i++)
            if (events[i] && events[i]->signalled) {
                if (!events[i]->manual_reset) events[i]->signalled = false;
                return i;
            }
        if (hub.cv.wait_until(lock, deadline) == std::cv_status::timeout) {
            for (int i = 0; i < count; i++)
                if (events[i] && events[i]->signalled) {
                    if (!events[i]->manual_reset) events[i]->signalled = false;
                    return i;
                }
            return -1;
        }
    }
}

inline bool WaitAll(const Event *events, int count, uint32_t ms) {
    detail::Hub &hub = detail::TheHub();
    std::unique_lock lock(hub.mutex);
    const auto all = [&] {
        for (int i = 0; i < count; i++)
            if (!events[i] || !events[i]->signalled) return false;
        return true;
    };
    if (!hub.cv.wait_for(lock, std::chrono::milliseconds(ms), all)) return false;
    for (int i = 0; i < count; i++)
        if (!events[i]->manual_reset) events[i]->signalled = false;
    return true;
}

// A thread is its "returned" event, shared by the thread itself and whoever holds the handle: the last of the two
// frees it (a session may be destroyed by its last worker, on that worker's own thread).
struct ThreadObject {
    EventObject done{true, false};
};
using Thread = std::shared_ptr<ThreadObject>;

inline Thread StartThread(ThreadFn fn, void *arg) {
    Thread t;
    try {
        t = std::make_shared<ThreadObject>();
        std::thread([fn, arg, t] {
            fn(arg);
            Signal(&t->done);
        }).detach();
    } catch (...) {
        return nullptr;
    }
    return t;
}
inline void CloseThread(Thread &t) { t.reset(); }
inline bool WaitThreads(const std::vector<Thread> &threads, uint32_t ms) {
    std::vector<Event> events;
    for (const Thread &t : threads)
        if (t) events.push_back(&t->done);
    return events.empty() || WaitAll(events.data(), static_cast<int>(events.size()), ms);
}

inline int64_t TickMs() {
    timespec ts{};
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return static_cast<int64_t>(ts.tv_sec) * 1000 + ts.tv_nsec / 1000000;
}
inline double PerfUs() {
    timespec ts{};
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return static_cast<double>(ts.tv_sec) * 1e6 + static_cast<double>(ts.tv_nsec) / 1e3;
}

#endif

} // namespace fi::audio::plat
