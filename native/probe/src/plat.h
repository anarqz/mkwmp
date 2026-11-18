// The probe's only view of the operating system (resources/docs/PLUGIN-API-READINESS.md, phase 1).
//
// The platform-neutral core (npln.c, seat.c, friends.c, voice.c, notify.c, log.c, ...) uses these instead of Win32,
// so the same sources build as the Windows winmm.dll proxy today and as a native Linux .so for mkw-recomp's plugin
// API later. Small primitives are inline below, per platform; the rest is in plat_win.c / plat_posix.c.
//
// Windows-only by design, and not behind this header: the loader (dllmain.c, winmm.def), the COFF backend (coff.c),
// the keypad's x64 detours (keypad.c, detour.c) and the Proton controller fixes.
#ifndef MKWMP_PLAT_H
#define MKWMP_PLAT_H

#include <stdint.h>

#ifdef _WIN32
#include <windows.h>
#else
#include <pthread.h>
#include <time.h>
#endif

// ---- atomics: full barriers, as the Interlocked* functions they replace. Any integer or pointer width.
#define plat_load(p) __atomic_load_n((p), __ATOMIC_SEQ_CST)
#define plat_store(p, v) __atomic_store_n((p), (v), __ATOMIC_SEQ_CST)
#define plat_xchg(p, v) __atomic_exchange_n((p), (v), __ATOMIC_SEQ_CST)   // returns the old value
#define plat_inc(p) __atomic_add_fetch((p), 1, __ATOMIC_SEQ_CST)          // returns the new value
#define plat_add(p, v) __atomic_add_fetch((p), (v), __ATOMIC_SEQ_CST)     // returns the new value
// *p = exchange if *p == comparand; returns the old value. Same argument order as InterlockedCompareExchange.
#define plat_cas(p, exchange, comparand)                                                                         \
    ({                                                                                                           \
        __typeof_unqual__(*(p)) plat_cas_old_ = (comparand);                                                          \
        __atomic_compare_exchange_n((p), &plat_cas_old_, (exchange), 0, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST);     \
        plat_cas_old_;                                                                                           \
    })

// ---- reader/writer lock, statically initialisable, not recursive
#ifdef _WIN32
typedef SRWLOCK PlatLock;
#define PLAT_LOCK_INIT SRWLOCK_INIT
static inline void plat_lock(PlatLock *l) { AcquireSRWLockExclusive(l); }
static inline void plat_unlock(PlatLock *l) { ReleaseSRWLockExclusive(l); }
static inline void plat_lock_shared(PlatLock *l) { AcquireSRWLockShared(l); }
static inline void plat_unlock_shared(PlatLock *l) { ReleaseSRWLockShared(l); }
#else
typedef pthread_rwlock_t PlatLock;
#define PLAT_LOCK_INIT PTHREAD_RWLOCK_INITIALIZER
static inline void plat_lock(PlatLock *l) { pthread_rwlock_wrlock(l); }
static inline void plat_unlock(PlatLock *l) { pthread_rwlock_unlock(l); }
static inline void plat_lock_shared(PlatLock *l) { pthread_rwlock_rdlock(l); }
static inline void plat_unlock_shared(PlatLock *l) { pthread_rwlock_unlock(l); }
#endif

// ---- clocks
#ifdef _WIN32
static inline uint64_t plat_ms(void) { return GetTickCount64(); }   // monotonic milliseconds
static inline void plat_sleep_ms(uint32_t ms) { Sleep(ms); }
// wall clock and monotonic clock as {seconds, nanoseconds} (CLOCK_REALTIME / CLOCK_MONOTONIC)
static inline void plat_realtime(int64_t out[2]) {
    FILETIME time;
    GetSystemTimePreciseAsFileTime(&time);
    ULARGE_INTEGER ticks;
    ticks.LowPart = time.dwLowDateTime, ticks.HighPart = time.dwHighDateTime;
    uint64_t unix_ticks = ticks.QuadPart - 116444736000000000ull;   // 100 ns since 1601 -> since 1970
    out[0] = (int64_t)(unix_ticks / 10000000ull);
    out[1] = (int64_t)((unix_ticks % 10000000ull) * 100ull);
}
static inline void plat_monotonic(int64_t out[2]) {
    LARGE_INTEGER count, frequency;
    QueryPerformanceCounter(&count), QueryPerformanceFrequency(&frequency);
    out[0] = count.QuadPart / frequency.QuadPart;
    out[1] = (count.QuadPart % frequency.QuadPart) * 1000000000ll / frequency.QuadPart;
}
#else
static inline void plat_clock_(clockid_t id, int64_t out[2]) {
    struct timespec t;
    clock_gettime(id, &t);
    out[0] = (int64_t)t.tv_sec, out[1] = (int64_t)t.tv_nsec;
}
static inline void plat_realtime(int64_t out[2]) { plat_clock_(CLOCK_REALTIME, out); }
static inline void plat_monotonic(int64_t out[2]) { plat_clock_(CLOCK_MONOTONIC, out); }
static inline uint64_t plat_ms(void) {
    int64_t t[2];
    plat_monotonic(t);
    return (uint64_t)t[0] * 1000u + (uint64_t)t[1] / 1000000u;
}
static inline void plat_sleep_ms(uint32_t ms) {
    struct timespec t = {(time_t)(ms / 1000u), (long)(ms % 1000u) * 1000000L};
    while (nanosleep(&t, &t)) {}
}
#endif

// ---- threads (plat_win.c / plat_posix.c)
// Start a detached thread running fn(arg). 1 on success.
int plat_thread_start(void (*fn)(void *arg), void *arg);

// ---- events, as Win32 events: auto-reset (a wait consumes the signal) or manual-reset (stays set until reset)
typedef struct PlatEvent PlatEvent;
PlatEvent *plat_event_new(int manual_reset);    // NULL on failure; starts unset; never freed
void plat_event_set(PlatEvent *e);
void plat_event_reset(PlatEvent *e);
int plat_event_wait(PlatEvent *e, uint32_t ms); // 1 signalled, 0 timed out; PLAT_FOREVER waits without limit
#define PLAT_FOREVER 0xFFFFFFFFu

#endif
