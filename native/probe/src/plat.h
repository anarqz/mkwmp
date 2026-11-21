// The probe's only view of the operating system (docs/PLUGIN-API-READINESS.md, phase 1).
//
// The platform-neutral core (npln.c, seat.c, friends.c, voice.c, notify.c, log.c, ...) uses these instead of Win32,
// so the same sources build as the Windows winmm.dll proxy today and as a native Linux .so for mkw-recomp's plugin
// API later. Small primitives are inline below, per platform; the rest is in plat_win.c / plat_posix.c.
//
// Windows-only by design, and not behind this header: the loader (dllmain.c, winmm.def), the COFF backend (coff.c).
#ifndef MKWMP_PLAT_H
#define MKWMP_PLAT_H

#include <stddef.h>
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
unsigned long plat_thread_id(void);   // for logs

// ---- files. Paths are UTF-8 (wide on Windows inside plat_win.c, so any folder name works).
// <home>/name: where mkwmp.toml, mkwmp.env, mkwmp.log and the voice library live. Home is the folder of the game
// executable (the winmm.dll proxy), unless plat_set_home moved it (the plugin build: the plugin's own folder,
// PLUGIN-API-READINESS.md gap 2). 1 on success.
int plat_home_path(char *out, size_t cap, const char *name);
// <the game executable's folder>/name, wherever home is (the plugin build looks there for what the MKWMP launcher
// writes: mkwmp.toml, the lobby CA). 1 on success.
int plat_exe_path(char *out, size_t cap, const char *name);
// Home is dir (UTF-8, with or without a trailing separator) from now on; NULL or "" goes back to the game's folder.
// Call before log_init, while no other probe thread runs.
void plat_set_home(const char *dir);
long plat_read_file(const char *path, void *buf, size_t cap);   // bytes read (at most cap); -1 if it cannot be opened
typedef struct PlatFile PlatFile;
// Create or truncate a log file, shared for reading. NULL on failure; *in_use = 1 when that is because another
// process has it open for writing (the recomp's start screen holding mkwmp.log).
PlatFile *plat_log_create(const char *path, int *in_use);
void plat_file_write(PlatFile *f, const void *p, size_t n);
unsigned long plat_last_error(void);   // GetLastError / errno of the last failed call, for logs

// ---- environment (Windows: the process environment, not the CRT's getenv copy, as the runtime reads it)
int plat_getenv(const char *name, char *buf, size_t cap);   // 1 when set; buf (may be NULL) gets the value
void plat_setenv(const char *name, const char *value);      // NULL unsets (Windows: the CRT's copy too)

// ---- dynamic libraries
void *plat_dl_open(const char *path);                        // NULL on failure (plat_last_error)
void *plat_dl_sym(void *lib, const char *name);
void plat_dl_close(void *lib);
// A library the process already loaded, by base name: "SDL3" is SDL3.dll on Windows, libSDL3.so.0 elsewhere. NULL
// when it is not loaded (on Linux also when the program links it statically).
void *plat_dl_loaded(const char *base);
// The voice chat library beside the game (native/voice): mkwmp-voice.dll on Windows, libmkwmp-voice.so elsewhere.
#ifdef _WIN32
#define PLAT_VOICE_LIB "mkwmp-voice.dll"
#else
#define PLAT_VOICE_LIB "libmkwmp-voice.so"
#endif
// The camera capture library, in the same folder (native/camera).
#ifdef _WIN32
#define PLAT_CAMERA_LIB "mkwmp-camera.dll"
#else
#define PLAT_CAMERA_LIB "libmkwmp-camera.so"
#endif

// ---- memory
// Readable bytes from p to the end of its mapping (committed, not guard / no-access); 0 when p is not readable.
size_t plat_mem_readable(const void *p);
// Store v at *at, which may be in read-only memory (a guest vtable): made writable, stored atomically, protection
// restored, instruction cache flushed. 1 on success (plat_last_error otherwise).
int plat_mem_write_u64(uint64_t *at, uint64_t v);

// ---- input, for the overlay's keyboard trigger (Shift+Tab, overlay.c): the only key the mod reads in the game
enum { PLAT_KEY_SHIFT, PLAT_KEY_TAB };
int plat_game_focused(void);   // the foreground window belongs to this process (0 where that cannot be known)
int plat_key_down(int key);    // a PLAT_KEY_* is held (0 where the keyboard cannot be read)

// ---- process
void plat_no_error_dialogs(void);   // no Windows error boxes from this process (SetErrorMode); nothing elsewhere
unsigned long plat_process_id(void);
// Windows: any process may take the foreground from this one once (AllowSetForegroundWindow), for the launcher this
// process asks to come to the front (overlay.c). Nothing elsewhere.
void plat_allow_foreground(void);
// Starts exe (a UTF-8 path) with the argument line args, detached, in exe's folder; 1 when it started. Windows only
// (0 elsewhere).
int plat_spawn(const char *exe, const char *args);

#endif
