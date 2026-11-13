// NPLN interception overrides.
//
// Local capture bridge. The observed NPLN gRPC path uses embedded OpenSSL via
// SendMsg/RecvMsg, so the server must offer TLS with capture_cert.h's certificate.
// nn::ssl stubs below cover other SDK paths, not the captured gRPC transport.
//
// Gates, completed local async prerequisites and runtime fiber fixes start NPLN.
// Winsock carries its TLS records to server/mkwmp_server.py, which terminates
// TLS, decodes the HTTP/2 + protobuf requests and supplies experimental responses.
//
// Env: MKWMP_PORT (default 8900), MKWMP_HOST (default 127.0.0.1).
#include "recomp.h"
#include "log.h"
#include "config.h"
#include "capture_cert.h"

#include <winsock2.h>
#include <ws2tcpip.h>
#include <stdlib.h>
#include <string.h>
#include <stdarg.h>
#include <stdio.h>

// ---- logging ---------------------------------------------------------------
// One sink for everything: our own mkwmp.log (the file players attach to
// reports) plus a mirror into the game log via rt_log as a backstop. Format
// once, emit twice. Safe to call from any override/thread.
static void mlog(const char *fmt, ...) {
    char buf[4096];
    va_list ap;
    va_start(ap, fmt);
    int n = vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    mkwmp_log("%s", buf);
    if (g_rt.rt_log) g_rt.rt_log("mkwmp: %s", buf);
}
static void rlog(const char *s) { mlog("%s", s); }
static void dump_args(const char *name, Cpu *c, int n) {
    if (!g_rt.rt_log) return;
    uint64_t a[6] = {0};
    for (int i = 0; i < n && i < 6; i++) a[i] = arg_x(c, i);
    mlog("CALL %s x0=%#llx x1=%#llx x2=%#llx x3=%#llx x4=%#llx", name,
                (unsigned long long)a[0], (unsigned long long)a[1], (unsigned long long)a[2],
                (unsigned long long)a[3], (unsigned long long)a[4]);
}
static void dump_cstr(const char *label, const void *p) {
    if (g_rt.rt_log) mlog("%s = %s", label, p ? (const char *)p : "(null)");
}
static void rhex(const char *tag, const void *buf, size_t len) {
    if (!g_rt.rt_log) return;
    static const char H[] = "0123456789abcdef";
    const unsigned char *b = buf;
    char hb[1537];
    size_t cap = len > 512 ? 512 : len, k = 0;
    for (size_t i = 0; i < cap; i++) { hb[k++] = H[b[i] >> 4]; hb[k++] = H[b[i] & 0xf]; hb[k++] = ' '; }
    hb[k] = 0;
    mlog("%s len=%llu %s%s", tag, (unsigned long long)len, hb, cap < len ? "..." : "");
}

// Linux errno numbers the game reads
enum { G_EINTR = 4, G_EAGAIN = 11, G_EINVAL = 22, G_EINPROGRESS = 115 };
static _Thread_local int g_errno;

// SDK pthread_cond_timedwait reads the SDK clock via a direct call, bypassing
// this import override. Keep successful SDK timestamps in the same epoch.
// gRPC reads the output even on SDK clock failure, so supply a fallback then.
static void ov_clock_gettime(Cpu *c) {
    int id = (int)arg_x(c, 0);
    int64_t *out = arg_p(c, 1);
    if (out && g_rt.ldr_symbol && g_rt.rt_call) {
        uint64_t original = g_rt.ldr_symbol("clock_gettime");
        if (original && !g_rt.rt_call(c, original, 2, (uint64_t)(unsigned)id, (uint64_t)(uintptr_t)out)
            && out[1] >= 0 && out[1] < 1000000000) {
            ret_x(c, 0);
            return;
        }
    }
    if (!out || (id != 0 && id != 1)) {
        g_errno = G_EINVAL; ret_x(c, (uint64_t)-1); return;
    }
    if (id == 0) {
        FILETIME time;
        GetSystemTimePreciseAsFileTime(&time);
        ULARGE_INTEGER ticks;
        ticks.LowPart = time.dwLowDateTime; ticks.HighPart = time.dwHighDateTime;
        uint64_t unix_ticks = ticks.QuadPart - 116444736000000000ull;
        out[0] = (int64_t)(unix_ticks / 10000000ull);
        out[1] = (int64_t)((unix_ticks % 10000000ull) * 100ull);
    } else {
        LARGE_INTEGER count, frequency;
        QueryPerformanceCounter(&count); QueryPerformanceFrequency(&frequency);
        out[0] = count.QuadPart / frequency.QuadPart;
        out[1] = (count.QuadPart % frequency.QuadPart) * 1000000000ll / frequency.QuadPart;
    }
    ret_x(c, 0);
}

static void ov_cond_timedwait(Cpu *c) {
    uint64_t cond = arg_x(c, 0), mutex = arg_x(c, 1);
    const int64_t *deadline = arg_p(c, 2);
    uint64_t original = g_rt.ldr_symbol("pthread_cond_timedwait");
    uint64_t clockwait = g_rt.ldr_symbol("pthread_cond_clockwait");
    // Only the verified gRPC call site converts its deadline to clock ID 0.
    if (c->x[30] != 0x700104a8bcull || !clockwait || !deadline ||
        deadline[1] < 0 || deadline[1] >= 1000000000) {
        ret_x(c, original ? g_rt.rt_call(c, original, 3, cond, mutex,
                                        (uint64_t)(uintptr_t)deadline) : G_EINVAL);
        return;
    }
    int64_t now[2], mono[2];
    uint64_t gettime = g_rt.ldr_symbol("clock_gettime");
    c->x[0] = 0;
    c->x[1] = (uintptr_t)now;
    ov_clock_gettime(c);
    if (c->x[0] || !gettime ||
        g_rt.rt_call(c, gettime, 2, (uint64_t)1, (uint64_t)(uintptr_t)mono)) {
        ret_x(c, G_EAGAIN);
        return;
    }
    __int128 remaining = ((__int128)deadline[0] - now[0]) * 1000000000 + deadline[1] - now[1];
    if (remaining < 0) remaining = 0;
    if (remaining > INT64_MAX) remaining = INT64_MAX;
    int64_t ns = mono[1] + (int64_t)(remaining % 1000000000);
    int64_t absolute[2] = {mono[0] + (int64_t)(remaining / 1000000000) + ns / 1000000000,
                           ns % 1000000000};
    static volatile LONG announced;
    if (InterlockedCompareExchange(&announced, 1, 0) == 0) rlog("gRPC timed waits use SDK monotonic clock");
    ret_x(c, g_rt.rt_call(c, clockwait, 4, cond, mutex, (uint64_t)1,
                          (uint64_t)(uintptr_t)absolute));
}

// ===========================================================================
// Gates
// ===========================================================================
static void ov_ok(Cpu *c) { ret_x(c, 0); }
static void ov_friend_list_available(Cpu *c) {
    // The Online Play gate at main+0x2228120 requires a ready list.
    // The existing recomp GetFriendList override supplies zero friends.
    uint8_t *available = arg_p(c, 0);
    if (available) *available = 1;
    rlog("friends list available (empty local list)");
    ret_x(c, 0);
}

static void ov_show_error_variant(Cpu *c) {
    // SDK error applets need a console service that the shipped host lacks.
    const uint32_t *variant = arg_p(c, 0);
    if (!variant || !g_rt.rt_log) return;
    if (variant[0] == 1) {
        uint32_t result = variant[1];
        mlog("game error result=%#x (%04u-%04u) caller=%#llx",
                    result, 2000u + (result & 0x1ffu), (result >> 9) & 0x1fffu,
                    (unsigned long long)c->x[30]);
    } else if (variant[0] == 2) {
        mlog("game error code=%04u-%04u caller=%#llx",
                    variant[1], variant[2], (unsigned long long)c->x[30]);
    } else {
        mlog("game error variant=%u caller=%#llx",
                    variant[0], (unsigned long long)c->x[30]);
    }
}
static void ov_fiber_switch(Cpu *c) {
    // The shipped runtime rejects NULL, although GetCurrentFiber returns NULL
    // on a thread's base context. Its lower-level switch already supports it.
    if (!arg_x(c, 0) && g_dele.fiber_switch_to && c->thread) {
        g_dele.fiber_switch_to(c->thread, c, NULL);
    } else if (g_dele.fiber_switch) g_dele.fiber_switch(c);
}
typedef struct {
    uint64_t addr, func, arg, stack, size;
    Cpu *cpu;
    void *host;
    int state;
    volatile LONG running;
} RecompFiber;
static void fiber_entry(void *p) {
    RecompFiber *f = p;
    uint8_t *thread = g_dele.current_cpu()->thread;
    RecompFiber **leaving = (RecompFiber **)(thread + 0x1e0);
    if (*leaving) { InterlockedExchange(&(*leaving)->running, 0); *leaving = NULL; }
    *(int *)(thread + 4) = 0;
    uint64_t next = g_rt.rt_call(f->cpu, f->func, 1, f->arg);
    f->state = 2;
    RecompFiber *to = NULL;
    if (next) {
        for (int i = 0; i < 1024; i++) {
            RecompFiber *candidate = g_dele.fibers[i];
            if (candidate && candidate->addr == next) { to = candidate; break; }
        }
        if (!to) { rlog("fiber returned an unknown target"); abort(); }
    }
    // rt_call can migrate the fiber. The shipped fiber_main caches &tcur
    // across that call; fetch the current thread again through the runtime.
    thread = g_dele.current_cpu()->thread;
    g_dele.fiber_switch_to(thread, f->cpu, to);
    abort();
}
static void ov_fiber_initialize(Cpu *c) {
    uint64_t addr = arg_x(c, 0);
    if (g_dele.fiber_initialize) g_dele.fiber_initialize(c);
    if (g_dele.fibers) {
        for (int i = 0; i < 1024; i++) {
            RecompFiber *f = g_dele.fibers[i];
            if (f && f->addr == addr) {
                g_dele.fiber_free(f->host);
                f->host = g_dele.fiber_new(256u * 1024u * 1024u, fiber_entry, f);
                if (!f->host) abort();
                break;
            }
        }
    }
}
static void ov_nifm_is_network_available(Cpu *c) { ret_x(c, 1); }
static void ov_nifm_submit_wait(Cpu *c) { (void)c; }
static void ov_nifm_ip_config(Cpu *c) {
    // nn::nifm returns network-order in_addr values. The transport below
    // binds to loopback, so advertise that same address to Pia.
    static const uint8_t fields[5][4] = {
        {127, 0, 0, 1}, {255, 0, 0, 0}, {0}, {0}, {0}
    };
    for (int i = 0; i < 5; ++i) {
        void *out = arg_p(c, i);
        if (out) memcpy(out, fields[i], sizeof fields[i]);
    }
    ret_x(c, 0);
}
#define FAKE_NSA_ID 0x1122334455667788ull
static const char kDummyToken[] =
    "eyJhbGciOiJub25lIn0.eyJzdWIiOiJta3dtcC1sb2NhbCIsImlzcyI6Im1rd21wIn0.";
static const char kSecondDummyToken[] =
    "eyJhbGciOiJub25lIn0.eyJzdWIiOiJta3dtcC1sb2NhbC0yIiwiaXNzIjoibWt3bXAifQ.";
static int g_second_player;
static void ov_nsa_available(Cpu *c) { uint8_t *o = arg_p(c, 0); if (o) *o = 1; ret_x(c, 0); }
static void ov_nsa_get_id(Cpu *c) { uint64_t *o = arg_p(c, 0); if (o) *o = FAKE_NSA_ID + g_second_player; ret_x(c, 0); }
static void ov_nsa_load_token(Cpu *c) {
    uint64_t *out_len = arg_p(c, 0);
    char *buf = arg_p(c, 1);
    uint64_t cap = arg_x(c, 2);
    const char *token = g_second_player ? kSecondDummyToken : kDummyToken;
    size_t len = strlen(token);
    if (buf && cap >= len) { memcpy(buf, token, len); if (out_len) *out_len = len; }
    else if (out_len) *out_len = 0;
    ret_x(c, 0);
}
static void ov_nsa_async_event(Cpu *c) {
    uint64_t event = arg_x(c, 1);
    uint64_t create = g_rt.ldr_symbol("_ZN2nn2os17CreateSystemEventEPNS0_15SystemEventTypeENS0_14EventClearModeEb");
    uint64_t signal = g_rt.ldr_symbol("_ZN2nn2os17SignalSystemEventEPNS0_15SystemEventTypeE");
    if (!event || !create || !signal) { ret_x(c, (uint64_t)-1); return; }
    uint64_t result = g_rt.rt_call(c, create, 3, event, (uint64_t)0, (uint64_t)1);
    if (!result) {
        g_rt.rt_call(c, signal, 1, event);
        rlog("account token async event -> completed");
    }
    ret_x(c, result);
}
static void ov_clock_init(Cpu *c) {
    // The SDK's task reserves 0x90 bytes plus an initialized flag. Use its
    // leading storage for a genuine SystemEvent, leaving that flag clear.
    uint64_t task = arg_x(c, 0);
    uint64_t create = g_rt.ldr_symbol("_ZN2nn2os17CreateSystemEventEPNS0_15SystemEventTypeENS0_14EventClearModeEb");
    uint64_t signal = g_rt.ldr_symbol("_ZN2nn2os17SignalSystemEventEPNS0_15SystemEventTypeE");
    if (!task || !create || !signal) { ret_x(c, (uint64_t)-1); return; }
    uint64_t result = g_rt.rt_call(c, create, 3, task, (uint64_t)0, (uint64_t)1);
    if (!result) g_rt.rt_call(c, signal, 1, task);
    rlog("network clock task -> local completed event");
    ret_x(c, result);
}
static void ov_clock_event(Cpu *c) { /* event is at offset zero; x0 already points to it */ (void)c; }
static void ov_clock_destroy(Cpu *c) {
    uint64_t task = arg_x(c, 0);
    uint64_t destroy = g_rt.ldr_symbol("_ZN2nn2os18DestroySystemEventEPNS0_15SystemEventTypeE");
    if (destroy && task && *(uint8_t *)(uintptr_t)(task + 0x28)) g_rt.rt_call(c, destroy, 1, task);
}
static void ov_nsd_env_id(Cpu *c) {
    char *out = arg_p(c, 0);
    if (out) { memset(out, 0, 8); memcpy(out, "lp1", 3); }
    ret_x(c, 0);
}
static void ov_fs_mount_rom(Cpu *c) { dump_cstr("fs::MountRom(name)", arg_p(c, 0)); ret_x(c, 0); }
static void ov_fs_unmount(Cpu *c) { dump_cstr("fs::Unmount", arg_p(c, 0)); ret_x(c, 0); }

// We virtualize exactly one file: _npln:/npln_config.json (NPLN requires it; the recomp's romfs
// doesn't have it). All other fs calls delegate to the recomp's real handler so content loading is
// unaffected. Our file handle is a distinctive magic value the fs handlers key on.
#define NPLN_CFG_PATH "_npln:/npln_config.json"
#define NPLN_FH 0x6d6b776d706e0001ull   // "mkwmp\0\0\1"
// Empty config is sufficient for the first auth request. The old startup abort
// was gRPC's unavailable poll/eventfd backend, not a missing JSON property.
static const char kNplnConfig[] = "{}";

static int is_cfg(const char *p) { return p && strcmp(p, NPLN_CFG_PATH) == 0; }
static int fs_interesting(const char *p) {
    return p && (p[0] == '_' || strstr(p, "npln") || strstr(p, "config") || strstr(p, "Npln"));
}
static void ov_fs_entry_type(Cpu *c) {
    const char *path = arg_p(c, 1);
    if (fs_interesting(path)) mlog("fs GetEntryType %s", path);
    if (is_cfg(path)) {
        int32_t *out = arg_p(c, 0);
        if (out) *out = 1;   // DirectoryEntryType::File
        ret_x(c, 0);
        return;
    }
    if (g_dele.fs_entry_type) g_dele.fs_entry_type(c); else ret_x(c, 0);
}
static void ov_fs_open_file(Cpu *c) {
    const char *path = arg_p(c, 1);
    if (fs_interesting(path)) mlog("fs OpenFile %s mode=%lld", path, (long long)arg_x(c, 2));
    if (is_cfg(path)) {
        uint64_t *out = arg_p(c, 0);   // FileHandle { void* } -> our magic
        if (out) *out = NPLN_FH;
        mlog("serving %s (%llu bytes)", NPLN_CFG_PATH, (unsigned long long)(sizeof(kNplnConfig) - 1));
        ret_x(c, 0);
        return;
    }
    if (g_dele.fs_open_file) g_dele.fs_open_file(c); else ret_x(c, 0);
}
// GetFileSize(long* out, FileHandle h): h is the u64 in x1.
static void ov_fs_file_size(Cpu *c) {
    if (arg_x(c, 1) == NPLN_FH) {
        int64_t *out = arg_p(c, 0);
        if (out) *out = (int64_t)(sizeof(kNplnConfig) - 1);
        ret_x(c, 0);
        return;
    }
    if (g_dele.fs_file_size) g_dele.fs_file_size(c); else ret_x(c, 0);
}
// ReadFile(size_t* outRead, FileHandle h, long offset, void* buf, size_t size)
static void ov_fs_read_file(Cpu *c) {
    if (arg_x(c, 1) == NPLN_FH) {
        uint64_t *outread = arg_p(c, 0);
        int64_t off = (int64_t)arg_x(c, 2);
        char *buf = arg_p(c, 3);
        uint64_t size = arg_x(c, 4);
        int64_t total = (int64_t)(sizeof(kNplnConfig) - 1);
        int64_t n = (off >= total) ? 0 : (total - off < (int64_t)size ? total - off : (int64_t)size);
        if (buf && n > 0) memcpy(buf, kNplnConfig + off, (size_t)n);
        if (outread) *outread = (uint64_t)n;
        ret_x(c, 0);
        return;
    }
    if (g_dele.fs_read_file) g_dele.fs_read_file(c); else ret_x(c, 0);
}
// CloseFile(FileHandle h): h in x0.
static void ov_fs_close_file(Cpu *c) {
    if (arg_x(c, 0) == NPLN_FH) { ret_x(c, 0); return; }
    if (g_dele.fs_close_file) g_dele.fs_close_file(c); else ret_x(c, 0);
}

// ===========================================================================
// Real-socket bridge to the local gRPC server
// ===========================================================================
#define NFD 128
static SOCKET g_sock[NFD];
static int g_used[NFD];
typedef struct { int level, option, value, used; } SocketOption;
static SocketOption g_options[NFD][16];
static SOCKET g_wake_peer[NFD];
static uint64_t g_wake_count[NFD];
static int g_wake_semaphore[NFD];
static SRWLOCK g_wake_lock = SRWLOCK_INIT;
static int g_ready;
static struct sockaddr_in g_server;

static void net_init(void) {
    if (g_ready) return;
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);
    for (int i = 0; i < NFD; i++) {
        g_sock[i] = INVALID_SOCKET;
        g_wake_peer[i] = INVALID_SOCKET;
    }
    // Server from mkwmp.toml; env vars override for testing.
    const MkwmpConfig *cfg = mkwmp_config();
    const char *host = getenv("MKWMP_HOST");
    const char *port = getenv("MKWMP_PORT");
    if (!host || !*host) host = cfg->host;
    int pnum = port && *port ? atoi(port) : cfg->port;
    g_server.sin_family = AF_INET;
    g_server.sin_port = htons((unsigned short)pnum);
    g_server.sin_addr.s_addr = inet_addr(host && *host ? host : "127.0.0.1");
    if (g_server.sin_addr.s_addr == INADDR_NONE) g_server.sin_addr.s_addr = inet_addr("127.0.0.1");
    g_ready = 1;
    mlog("net bridge ready -> %s:%u", host && *host ? host : "127.0.0.1",
                (unsigned)ntohs(g_server.sin_port));
}

static int fd_new(SOCKET s) {
    for (int i = 0; i < NFD; i++) if (!g_used[i]) {
        g_used[i] = 1; g_sock[i] = s;
        memset(g_options[i], 0, sizeof g_options[i]);
        return i;
    }
    return -1;
}
static SOCKET fd_get(int fd) { return (fd >= 0 && fd < NFD && g_used[fd]) ? g_sock[fd] : INVALID_SOCKET; }

// gRPC probes EventFd before selecting its poll backend. Keep a real counter and
// a coalesced UDP wakeup so the same descriptor participates in WSAPoll.
static void ov_eventfd(Cpu *c) {
    net_init();
    SOCKET rd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    SOCKET wr = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    struct sockaddr_in addr = {0};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    int size = sizeof addr;
    int fd = -1;
    if (rd == INVALID_SOCKET || wr == INVALID_SOCKET ||
        bind(rd, (struct sockaddr *)&addr, sizeof addr) ||
        getsockname(rd, (struct sockaddr *)&addr, &size) ||
        connect(wr, (struct sockaddr *)&addr, size)) goto fail;
    u_long nb = 1;
    ioctlsocket(rd, FIONBIO, &nb);
    ioctlsocket(wr, FIONBIO, &nb);
    fd = fd_new(rd);
    if (fd < 0) goto fail;
    g_wake_peer[fd] = wr;
    g_wake_count[fd] = arg_x(c, 0);
    g_wake_semaphore[fd] = (arg_x(c, 1) & 1) != 0;
    if (g_wake_count[fd]) send(wr, "w", 1, 0);
    mlog("EventFd -> fd=%d flags=%llu", fd, (unsigned long long)arg_x(c, 1));
    ret_x(c, (uint64_t)fd);
    return;
fail:
    if (rd != INVALID_SOCKET) closesocket(rd);
    if (wr != INVALID_SOCKET) closesocket(wr);
    g_errno = G_EINVAL;
    ret_x(c, (uint64_t)-1);
}
static void ov_eventfd_read(Cpu *c) {
    int fd = (int)arg_x(c, 0);
    uint64_t *out = arg_p(c, 1);
    if (!out || fd_get(fd) == INVALID_SOCKET || g_wake_peer[fd] == INVALID_SOCKET) {
        g_errno = G_EINVAL; ret_x(c, (uint64_t)-1); return;
    }
    AcquireSRWLockExclusive(&g_wake_lock);
    uint64_t count = g_wake_count[fd];
    if (count) {
        *out = g_wake_semaphore[fd] ? 1 : count;
        g_wake_count[fd] -= *out;
        if (!g_wake_count[fd]) { char byte; recv(g_sock[fd], &byte, 1, 0); }
    }
    ReleaseSRWLockExclusive(&g_wake_lock);
    if (!count) g_errno = G_EAGAIN;
    ret_x(c, count ? 0 : (uint64_t)-1);
}
static void ov_eventfd_write(Cpu *c) {
    int fd = (int)arg_x(c, 0);
    uint64_t value = arg_x(c, 1);
    if (fd_get(fd) == INVALID_SOCKET || g_wake_peer[fd] == INVALID_SOCKET || value == UINT64_MAX) {
        g_errno = G_EINVAL; ret_x(c, (uint64_t)-1); return;
    }
    AcquireSRWLockExclusive(&g_wake_lock);
    uint64_t count = g_wake_count[fd];
    int ok = value < UINT64_MAX - count;
    if (ok && value && !count) ok = send(g_wake_peer[fd], "w", 1, 0) == 1;
    if (ok) g_wake_count[fd] += value;
    ReleaseSRWLockExclusive(&g_wake_lock);
    if (!ok) g_errno = G_EAGAIN;
    ret_x(c, ok ? 0 : (uint64_t)-1);
}

// int nnsocketSocket(int domain, int type, int protocol): always a non-blocking TCP socket to loopback.
static void ov_sock_socket(Cpu *c) {
    net_init();
    dump_args("nnsocketSocket", c, 3);
    SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET) { g_errno = G_EINVAL; ret_x(c, (uint64_t)-1); return; }
    u_long nb = 1; ioctlsocket(s, FIONBIO, &nb);
    int fd = fd_new(s);
    if (fd < 0) { closesocket(s); g_errno = G_EINVAL; ret_x(c, (uint64_t)-1); return; }
    ret_x(c, (uint64_t)fd);
}
// int nnsocketConnect(int fd, const sockaddr*, u32 len): connect to our loopback server.
// Port 443 (NPLN TLS) goes to MKWMP_PORT; any other explicit port, such as the
// plaintext Gamesync endpoint we hand out in session assignments, keeps its port.
static void ov_sock_connect(Cpu *c) {
    int fd = (int)arg_x(c, 0);
    SOCKET s = fd_get(fd);
    if (s == INVALID_SOCKET) { g_errno = G_EINVAL; ret_x(c, (uint64_t)-1); return; }
    struct sockaddr_in target = g_server;
    const uint8_t *guest = arg_p(c, 1);
    if (guest && arg_x(c, 2) >= 4 && guest[1] == 2) {
        uint16_t port;
        memcpy(&port, guest + 2, sizeof port);
        if (port && ntohs(port) != 443) target.sin_port = port;
    }
    if (target.sin_port != g_server.sin_port)
        mlog("nnsocketConnect keeps explicit port %u", (unsigned)ntohs(target.sin_port));
    int r = connect(s, (struct sockaddr *)&target, sizeof target);
    if (r == 0) { rlog("nnsocketConnect -> connected (loopback)"); ret_x(c, 0); return; }
    int e = WSAGetLastError();
    if (e == WSAEWOULDBLOCK || e == WSAEINPROGRESS || e == WSAEALREADY) {
        g_errno = G_EINPROGRESS; rlog("nnsocketConnect -> EINPROGRESS (loopback)");
    } else if (e == WSAEISCONN) { ret_x(c, 0); return; }
    else g_errno = G_EINVAL;
    ret_x(c, (uint64_t)-1);
}
static void ov_sock_close(Cpu *c) {
    int fd = (int)arg_x(c, 0);
    SOCKET s = fd_get(fd);
    if (s != INVALID_SOCKET) {
        if (g_wake_peer[fd] != INVALID_SOCKET) closesocket(g_wake_peer[fd]);
        g_wake_peer[fd] = INVALID_SOCKET;
        g_wake_count[fd] = 0;
        closesocket(s); g_sock[fd] = INVALID_SOCKET; g_used[fd] = 0;
    }
    ret_x(c, 0);
}
static void ov_sock_fcntl(Cpu *c) { ret_x(c, 0); }    // sockets already non-blocking
static void ov_sock_setsockopt(Cpu *c) {
    int fd = (int)arg_x(c, 0), level = (int)arg_x(c, 1), option = (int)arg_x(c, 2);
    const int32_t *value = arg_p(c, 3);
    if (fd_get(fd) == INVALID_SOCKET || !value || arg_x(c, 4) < 4) {
        g_errno = G_EINVAL; ret_x(c, (uint64_t)-1); return;
    }
    for (int i = 0; i < 16; i++) {
        SocketOption *saved = &g_options[fd][i];
        if (!saved->used || (saved->level == level && saved->option == option)) {
            *saved = (SocketOption){level, option, *value, 1};
            break;
        }
    }
    ret_x(c, 0);
}
// int nnsocketGetSockOpt(fd, level, opt, void* val, u32* len): answer SO_ERROR = 0 (connected).
static void ov_sock_getsockopt(Cpu *c) {
    int fd = (int)arg_x(c, 0), level = (int)arg_x(c, 1), option = (int)arg_x(c, 2);
    void *val = arg_p(c, 3); uint32_t *len = arg_p(c, 4);
    if (fd_get(fd) == INVALID_SOCKET || !val || !len || *len < 4) {
        g_errno = G_EINVAL; ret_x(c, (uint64_t)-1); return;
    }
    int32_t result = 0;
    for (int i = 0; i < 16; i++) {
        SocketOption *saved = &g_options[fd][i];
        if (saved->used && saved->level == level && saved->option == option) { result = saved->value; break; }
    }
    if (level == 0xffff && option == 0x1007) {
        int bytes = sizeof result;
        getsockopt(g_sock[fd], SOL_SOCKET, SO_ERROR, (char *)&result, &bytes);
        if (result) result = G_EINVAL;
    }
    *(int32_t *)val = result; *len = 4;
    ret_x(c, 0);
}
static void ov_sock_shutdown(Cpu *c) {
    SOCKET s = fd_get((int)arg_x(c, 0));
    if (s != INVALID_SOCKET) shutdown(s, SD_BOTH);
    ret_x(c, 0);
}
static void ov_errno_location(Cpu *c) { ret_x(c, (uintptr_t)&g_errno); }

// nnsocketPoll(pollfd* fds, nfds, timeout): WSAPoll the real sockets. Console pollfd = {s32 fd; s16 events; s16 revents}.
enum { G_POLLIN = 1, G_POLLOUT = 4, G_POLLERR = 8, G_POLLHUP = 0x10, G_POLLNVAL = 0x20 };
static void ov_sock_poll(Cpu *c) {
    uint8_t *g = arg_p(c, 0);
    uint64_t n = arg_x(c, 1);
    int ms = (int)arg_x(c, 2);
    if ((!g && n) || n > NFD) { g_errno = G_EINVAL; ret_x(c, (uint64_t)-1); return; }
    WSAPOLLFD h[NFD];
    int map[NFD], valid = 0, ready = 0;
    for (uint64_t i = 0; i < n; i++) {
        int32_t fd; int16_t events;
        memcpy(&fd, g + i * 8, 4);
        memcpy(&events, g + i * 8 + 4, 2);
        int16_t zero = 0; memcpy(g + i * 8 + 6, &zero, 2);
        SOCKET s = fd_get(fd);
        if (s == INVALID_SOCKET) {
            if (fd >= 0) {
                int16_t invalid = G_POLLNVAL;
                memcpy(g + i * 8 + 6, &invalid, 2);
                ready++;
            }
            continue;
        }
        h[valid].fd = s;
        h[valid].events = (short)(((events & G_POLLIN) ? POLLRDNORM : 0) | ((events & G_POLLOUT) ? POLLWRNORM : 0));
        h[valid].revents = 0;
        map[valid] = (int)i;
        valid++;
    }
    int away = 0;
    if (ms && !ready && c->thread && g_dele.core_away && g_dele.core_back) away = g_dele.core_away();
    int result = 0;
    if (valid) {
        result = WSAPoll(h, (ULONG)valid, ready ? 0 : (ms < 0 ? -1 : ms));
    } else if (!ready && ms) {
        Sleep(ms < 0 ? INFINITE : (DWORD)ms);
    }
    int socket_error = result == SOCKET_ERROR ? WSAGetLastError() : 0;
    if (g_dele.core_back && away) g_dele.core_back(&away);
    if (result == SOCKET_ERROR) {
        g_errno = socket_error == WSAEINTR ? G_EINTR : G_EINVAL;
        ret_x(c, (uint64_t)-1); return;
    }
    if (valid) {
        if (result > 0) {
            for (int k = 0; k < valid; k++) {
                short re = h[k].revents;
                int16_t gre = (int16_t)(((re & (POLLRDNORM | POLLHUP)) ? G_POLLIN : 0) |
                                        ((re & POLLWRNORM) ? G_POLLOUT : 0) |
                                        ((re & POLLERR) ? G_POLLERR : 0) | ((re & POLLHUP) ? G_POLLHUP : 0) |
                                        ((re & POLLNVAL) ? G_POLLNVAL : 0));
                if (gre) { memcpy(g + (size_t)map[k] * 8 + 6, &gre, 2); ready++; }
            }
        }
    }
    ret_x(c, (uint64_t)ready);
}

typedef struct { void *base; uint64_t size; } GIoVec;
typedef struct {
    void *name; uint32_t namelen, pad;
    GIoVec *iov; uint32_t iovlen, pad2;
    void *control; uint32_t controllen; int32_t flags;
} GMsgHdr;
static void socket_msg(Cpu *c, int reading) {
    SOCKET sock = fd_get((int)arg_x(c, 0));
    GMsgHdr *msg = arg_p(c, 1);
    if (sock == INVALID_SOCKET || !msg || !msg->iov || msg->iovlen > 1024) {
        g_errno = G_EINVAL; ret_x(c, (uint64_t)-1); return;
    }
    WSABUF buffers[1024];
    for (uint32_t i = 0; i < msg->iovlen; i++) {
        if (msg->iov[i].size > INT_MAX) { g_errno = G_EINVAL; ret_x(c, (uint64_t)-1); return; }
        buffers[i].buf = msg->iov[i].base;
        buffers[i].len = (ULONG)msg->iov[i].size;
    }
    DWORD transferred = 0, flags = 0;
    int result = reading ? WSARecv(sock, buffers, msg->iovlen, &transferred, &flags, NULL, NULL)
                         : WSASend(sock, buffers, msg->iovlen, &transferred, 0, NULL, NULL);
    if (result) {
        g_errno = WSAGetLastError() == WSAEWOULDBLOCK ? G_EAGAIN : G_EINVAL;
        ret_x(c, (uint64_t)-1); return;
    }
    if (reading) msg->flags = 0;
    if (transferred && msg->iovlen) rhex(reading ? "<<< RecvMsg" : ">>> SendMsg", buffers[0].buf,
        transferred < buffers[0].len ? transferred : buffers[0].len);
    ret_x(c, transferred);
}
static void ov_sock_sendmsg(Cpu *c) { socket_msg(c, 0); }
static void ov_sock_recvmsg(Cpu *c) { socket_msg(c, 1); }

// int nnsocketGetAddrInfo(node, service, hints, addrinfo** res): return a loopback result so gRPC
// proceeds to create a socket + connect (which we redirect anyway).
typedef struct {
    int32_t ai_flags, ai_family, ai_socktype, ai_protocol;
    uint32_t ai_addrlen; uint32_t _pad;
    char *ai_canonname; void *ai_addr; void *ai_next;
} GAddrInfo;
_Static_assert(offsetof(GAddrInfo, ai_addr) == 0x20, "console addrinfo ABI");
static void ov_sock_freeaddrinfo(Cpu *c) {
    GAddrInfo *ai = arg_p(c, 0);
    while (ai) {
        GAddrInfo *next = ai->ai_next;
        free(ai->ai_addr); free(ai->ai_canonname); free(ai);
        ai = next;
    }
}
static void sock_getaddrinfo(Cpu *c, const char *label, int cpp_layout) {
    net_init();
    dump_cstr(label, arg_p(c, 0));
    void **res = arg_p(c, 3);
    if (!res) { ret_x(c, (uint64_t)-1); return; }
    *res = NULL;
    GAddrInfo *ai = calloc(1, sizeof *ai);
    uint8_t *sa = calloc(1, 16);   // console sockaddr_in: len,family,port,addr,zero[8]
    if (!ai || !sa) { free(ai); free(sa); ret_x(c, (uint64_t)-1); return; }
    sa[0] = 16; sa[1] = 2;                      // len=16, AF_INET=2
    memcpy(sa + 2, &g_server.sin_port, 2);      // port (network order)
    // Keep an explicit numeric service other than 443 (see ov_sock_connect).
    const char *service = arg_p(c, 1);
    if (service && *service) {
        char *end;
        unsigned long port = strtoul(service, &end, 10);
        if (!*end && port && port < 65536 && port != 443) {
            uint16_t be = htons((uint16_t)port);
            memcpy(sa + 2, &be, 2);
        }
    }
    memcpy(sa + 4, &g_server.sin_addr, 4);      // 127.0.0.1
    ai->ai_family = 2; ai->ai_socktype = 1; ai->ai_protocol = 6; ai->ai_addrlen = 16;
    if (cpp_layout) ai->ai_canonname = (char *)sa; // nn::socket::AddrInfo address at +0x18
    else ai->ai_addr = sa;
    *res = ai;
    ret_x(c, 0);
}
static void ov_sock_getaddrinfo(Cpu *c) { sock_getaddrinfo(c, "nnsocketGetAddrInfo node", 0); }
static void ov_sock_getaddrinfo_bsd(Cpu *c) { sock_getaddrinfo(c, "nn::socket::GetAddrInfo BSD node", 0); }
static void ov_sock_getaddrinfo_cpp(Cpu *c) { sock_getaddrinfo(c, "nn::socket::GetAddrInfo typed node", 1); }

// Pia's separate datagram path. Keep it loopback-only; gRPC still uses TCP.
static void ov_pia_socket(Cpu *c) {
    net_init();
    dump_args("Pia Socket", c, 3);
    if (arg_x(c, 0) != 2 || ((unsigned)arg_x(c, 1) & 0xf) != 2) {
        g_errno = G_EINVAL; ret_x(c, UINT64_MAX); return;
    }
    SOCKET s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    u_long nonblocking = 1;
    if (s == INVALID_SOCKET || ioctlsocket(s, FIONBIO, &nonblocking)) {
        if (s != INVALID_SOCKET) closesocket(s);
        g_errno = G_EINVAL; ret_x(c, UINT64_MAX); return;
    }
    int fd = fd_new(s);
    if (fd < 0) { closesocket(s); g_errno = G_EINVAL; }
    ret_x(c, (uint64_t)(int64_t)fd);
}

static void ov_pia_bind(Cpu *c) {
    SOCKET s = fd_get((int)arg_x(c, 0));
    struct sockaddr_in sa = {0};
    sa.sin_family = AF_INET;
    sa.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (s == INVALID_SOCKET || bind(s, (struct sockaddr *)&sa, sizeof sa)) {
        g_errno = G_EINVAL; ret_x(c, UINT64_MAX); return;
    }
    rlog("Pia UDP bound to loopback");
    ret_x(c, 0);
}

static void ov_pia_setsockopt(Cpu *c) {
    int opt = (int)arg_x(c, 2);
    int winopt = opt == 4 ? SO_REUSEADDR : opt == 0x20 ? SO_BROADCAST : 0;
    SOCKET s = fd_get((int)arg_x(c, 0));
    const void *value = arg_p(c, 3);
    dump_args("Pia SetSockOpt", c, 5);
    if (arg_x(c, 1) == 0xffff && winopt && s != INVALID_SOCKET && value && arg_x(c, 4) == 4) {
        if (setsockopt(s, SOL_SOCKET, winopt, value, 4)) {
            g_errno = G_EINVAL; ret_x(c, UINT64_MAX); return;
        }
    }
    ov_sock_setsockopt(c);
}

static void ov_pia_sendto(Cpu *c) {
    SOCKET s = fd_get((int)arg_x(c, 0));
    const char *buf = arg_p(c, 1);
    uint64_t len = arg_x(c, 2);
    if (s == INVALID_SOCKET || !buf || len > 65507) {
        g_errno = G_EINVAL; ret_x(c, UINT64_MAX); return;
    }
    const uint8_t *guest = arg_p(c, 4);
    struct sockaddr_in sa = {0};
    sa.sin_family = AF_INET;
    sa.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    // NAT probes reach the local responder. A peer endpoint learned from a
    // local session must keep its own port instead of going to that responder.
    if (guest && arg_x(c, 5) >= 16 && guest[1] == 2 && guest[4] == 127 &&
        guest[5] == 0 && guest[6] == 0 && guest[7] == 1) {
        memcpy(&sa.sin_port, guest + 2, sizeof sa.sin_port);
    } else if (len == 16 && !buf[0] && !buf[1] && !buf[2] &&
               (buf[3] == 0 || (buf[3] >= 101 && buf[3] <= 103))) {
        sa.sin_port = htons(10025);
    }
    if (!sa.sin_port) {
        g_errno = G_EINVAL; ret_x(c, UINT64_MAX); return;
    }
    rhex(">>> Pia UDP", buf, (size_t)len);
    int sent = sendto(s, buf, (int)len, 0, (struct sockaddr *)&sa, sizeof sa);
    if (sent == SOCKET_ERROR) g_errno = WSAGetLastError() == WSAEWOULDBLOCK ? G_EAGAIN : G_EINVAL;
    ret_x(c, (uint64_t)(int64_t)sent);
}

static void ov_pia_recvfrom(Cpu *c) {
    SOCKET s = fd_get((int)arg_x(c, 0));
    char *buf = arg_p(c, 1);
    uint64_t cap = arg_x(c, 2);
    uint8_t *out = arg_p(c, 4);
    uint32_t *out_len = arg_p(c, 5);
    if (s == INVALID_SOCKET || !buf || cap > INT_MAX || (out && (!out_len || *out_len < 16))) {
        g_errno = G_EINVAL; ret_x(c, UINT64_MAX); return;
    }
    struct sockaddr_in sa;
    int addrlen = sizeof sa;
    int got = recvfrom(s, buf, (int)cap, 0, (struct sockaddr *)&sa, &addrlen);
    if (got == SOCKET_ERROR) {
        int err = WSAGetLastError();
        g_errno = err == WSAEWOULDBLOCK || err == WSAECONNRESET ? G_EAGAIN : G_EINVAL;
    } else if (out) {
        memset(out, 0, 16);
        out[0] = 16; out[1] = 2;
        memcpy(out + 2, &sa.sin_port, 2);
        memcpy(out + 4, &sa.sin_addr, 4);
        *out_len = 16;
    }
    ret_x(c, (uint64_t)(int64_t)got);
}

static void ov_pia_errno(Cpu *c) { ret_x(c, (unsigned)g_errno); }

// ===========================================================================
// SDK SSL setup and certificate hooks; actual TLS is embedded OpenSSL.
// ===========================================================================
static GuestFn original_dispatch;

static int is_pia_join_step(uint64_t target) {
    switch (target) {
    case 0x700156015cull: // WaitAutoConnect
    case 0x70015602dcull:
    case 0x7001560414ull:
    case 0x7001560c10ull: // StartMeshStartup
    case 0x7001560f28ull: // DisconnectNetwork
    case 0x700156102cull: // CreateMesh
    case 0x7001561798ull: // JoinMesh
        return 1;
    default:
        return 0;
    }
}

// Opt-in (MKWMP_TRACE_EDGES=1) coverage of indirect calls made from NPLN
// matchmaking/game-session/Gamesync code. Each distinct caller->target pair is
// logged once with a millisecond tick, so a run shows which code executed
// between a match assignment and the resulting error.
#define EDGE_LO 0x7001200000ull
#define EDGE_HI 0x7001440000ull
#define EDGE_SLOTS 8192
static int g_trace_edges;
static volatile LONG64 g_edges[EDGE_SLOTS];

static void trace_edge(uint64_t caller, uint64_t target, const Cpu *c) {
    if (caller < EDGE_LO || caller >= EDGE_HI) return;
    uint64_t key = ((caller - 0x7000000000ull) << 32) ^ (target - 0x7000000000ull);
    if (!key) return;
    uint64_t h = key * 0x9E3779B97F4A7C15ull;
    for (unsigned probe = 0; probe < 64; probe++) {
        volatile LONG64 *slot = &g_edges[(h + probe) & (EDGE_SLOTS - 1)];
        LONG64 seen = InterlockedCompareExchange64(slot, (LONG64)key, 0);
        if (seen == (LONG64)key) return;
        if (seen == 0) {
            mlog("edge t=%llu caller=main+%#llx target=main+%#llx x0=%#llx x1=%#llx x2=%#llx",
                        (unsigned long long)GetTickCount64(),
                        (unsigned long long)(caller - 0x7000000000ull),
                        (unsigned long long)(target - 0x7000000000ull),
                        (unsigned long long)c->x[0], (unsigned long long)c->x[1],
                        (unsigned long long)c->x[2]);
            return;
        }
    }
}

// main+0x46966a0 constructs a string from a C string in x1 (used for error
// messages such as "Timed out to get my UserSession." and resource names).
// Mirror the ones built by NPLN matchmaking/Gamesync code into mkw.log.
static void trace_sdk_message(const Cpu *c) {
    const char *msg = (const char *)(uintptr_t)c->x[1];
    if ((uintptr_t)msg < 0x10000) return;
    MEMORY_BASIC_INFORMATION mbi;
    if (!VirtualQuery(msg, &mbi, sizeof mbi) || mbi.State != MEM_COMMIT ||
        (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)))
        return;
    size_t avail = (size_t)((const char *)mbi.BaseAddress + mbi.RegionSize - msg);
    char buf[400];
    size_t n = 0;
    for (; n < sizeof buf - 1 && n < avail && msg[n]; n++)
        buf[n] = (msg[n] >= 0x20 && msg[n] < 0x7f) ? msg[n] : '.';
    buf[n] = 0;
    mlog("sdk-msg t=%llu caller=main+%#llx \"%s\"",
                (unsigned long long)GetTickCount64(),
                (unsigned long long)(c->x[30] - 0x7000000000ull), buf);
}

static void profile_dispatch(Cpu *c) {
    if (g_trace_edges && g_rt.rt_log) {
        if (c->pc == 0x70046966a0ull && c->x[30] >= EDGE_LO && c->x[30] < EDGE_HI)
            trace_sdk_message(c);
        trace_edge(c->x[30], c->pc, c);
    }
    // The lifted BLR uses rt_dispatch. This call retrieves the completed
    // BulkGetDocuments result into the guest's x8 return buffer.
    uint32_t *result = c->x[30] == 0x7001e463dcull && c->pc == 0x700146a200ull
        ? (void *)(uintptr_t)c->x[8] : NULL;
    uint64_t target = c->pc;
    uint64_t caller = c->x[30];
    if (target == 0x7001e3f91cull && c->x[1] && g_rt.rt_log) {
        uint32_t event[7];
        memcpy(event, (const void *)(uintptr_t)c->x[1], sizeof event);
        if (event[0])
            mlog("controller event kind=%u detail=%#x/%#x/%#x/%#x/%#x/%#x caller=%#llx",
                        event[0], event[1], event[2], event[3], event[4], event[5], event[6],
                        (unsigned long long)caller);
    }
    // These BLRs select the next controller phase and prepare a new operation.
    uint32_t *next_phase = caller == 0x700261f894ull ? (void *)(uintptr_t)c->x[1] : NULL;
    uint64_t operation = next_phase ? c->x[2] : c->x[1];
    int trace_operation = (next_phase || caller == 0x700261f044ull) &&
                          (operation == 2 || operation == 3 || operation == 10);
    uint32_t desired_mode = 0, desired_phase = 0, game_flags = 0;
    if (trace_operation) {
        const uint8_t *controller = (void *)(uintptr_t)c->x[0];
        memcpy(&desired_mode, controller + 0x4a8, sizeof desired_mode);
        memcpy(&desired_phase, controller + 0x4ac, sizeof desired_phase);
        const uint8_t *global = *(const uint8_t **)(uintptr_t)0x70033d2548ull;
        if (global) memcpy(&game_flags, global + 0x118, sizeof game_flags);
    }
    // The game's sequence callbacks are upstream of the Pia join job.
    const uint8_t *seq = (target == 0x7001a93138ull || target == 0x7001a79fa0ull)
        ? (void *)(uintptr_t)c->x[0] : NULL;
    uint32_t seq_before[2] = {0};
    if (seq) memcpy(seq_before, seq + 0x28, sizeof seq_before);
    // JoinRandomSessionJob stores its current step and error inline. Only
    // inspect known step functions for this build and log state transitions.
    const uint8_t *job = is_pia_join_step(target) ? (void *)(uintptr_t)c->x[0] : NULL;
    uint64_t before = 0;
    if (job) memcpy(&before, job + 0x38, sizeof before);
    original_dispatch(c);
    if (trace_operation && g_rt.rt_log)
        mlog("Match controller %s op=%llu target=%#llx ret=%#llx next_phase=%u desired=%u/%u flags=%#x",
                    next_phase ? "complete" : "prepare", (unsigned long long)operation,
                    (unsigned long long)target, (unsigned long long)c->x[0],
                    next_phase ? *next_phase : 0, desired_mode, desired_phase, game_flags);
    if (seq && g_rt.rt_log) {
        uint32_t after[2];
        memcpy(after, seq + 0x28, sizeof after);
        if (memcmp(seq_before, after, sizeof after))
            mlog("Matchmake %s phase=%u/%u -> %u/%u",
                        target == 0x7001a93138ull ? "wait" : "start",
                        seq_before[0], seq_before[1], after[0], after[1]);
    }
    if (result && g_rt.rt_log)
        mlog("BulkGetDocuments completion=%u (target=%#llx)",
                    *result, (unsigned long long)target);
    if (job && g_rt.rt_log) {
        uint64_t after, label;
        uint32_t error;
        memcpy(&after, job + 0x38, sizeof after);
        if (before == after) return;
        memcpy(&label, job + 0x48, sizeof label);
        memcpy(&error, job + 0xd8, sizeof error);
        mlog("Pia JoinRandomSession step=%#llx next=%#llx label=%#llx error=%#x",
                    (unsigned long long)target, (unsigned long long)after,
                    (unsigned long long)label, error);
    }
}

static void install_profile_dispatch_trace(void) {
    static volatile LONG attempted;
    uint8_t *base = (void *)GetModuleHandleW(L"mkwgame.dll");
    if (!base || InterlockedCompareExchange(&attempted, 1, 0)) return;
    IMAGE_DOS_HEADER *dos = (void *)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return;
    IMAGE_NT_HEADERS64 *nt = (void *)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC) return;
    IMAGE_DATA_DIRECTORY dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!dir.VirtualAddress) return;
    IMAGE_IMPORT_DESCRIPTOR *imports = (void *)(base + dir.VirtualAddress);
    for (size_t i = 0; i < dir.Size / sizeof(*imports) && imports[i].Name; i++) {
        IMAGE_IMPORT_DESCRIPTOR *imp = &imports[i];
        if (_stricmp((char *)base + imp->Name, "mkw.exe") || !imp->OriginalFirstThunk) continue;
        IMAGE_THUNK_DATA64 *names = (void *)(base + imp->OriginalFirstThunk);
        IMAGE_THUNK_DATA64 *iat = (void *)(base + imp->FirstThunk);
        for (size_t j = 0; names[j].u1.AddressOfData; j++) {
            uint64_t rva = names[j].u1.AddressOfData;
            if (IMAGE_SNAP_BY_ORDINAL64(rva)) continue;
            IMAGE_IMPORT_BY_NAME *name = (void *)(base + rva);
            if (strcmp((char *)name->Name, "rt_dispatch")) continue;
            DWORD protection;
            if (!VirtualProtect(&iat[j], sizeof iat[j], PAGE_READWRITE, &protection)) return;
            original_dispatch = (GuestFn)(uintptr_t)iat[j].u1.Function;
            InterlockedExchangePointer((void *volatile *)&iat[j].u1.Function, (void *)profile_dispatch);
            VirtualProtect(&iat[j], sizeof iat[j], protection, &protection);
            rlog("BulkGetDocuments and Pia join tracing installed");
            return;
        }
    }
    rlog("BulkGetDocuments completion tracing: rt_dispatch import not found");
}

static void ov_ssl_initialize(Cpu *c) {
    install_profile_dispatch_trace();
    rlog("ssl::Initialize (stub)");
    ret_x(c, 0);
}
static void ov_ssl_ctx_create(Cpu *c) { uint64_t *s = arg_p(c, 0); if (s) *s = 1; ret_x(c, 0); }
static void ov_ssl_import_pki(Cpu *c) { uint64_t *o = arg_p(c, 1); if (o) *o = 1; ret_x(c, 0); }
typedef struct {
    uint32_t id, status, size, pad;
    const unsigned char *data;
} GCertificateInfo;
static void ov_ssl_cert_bufsize(Cpu *c) {
    uint32_t *out = arg_p(c, 0);
    if (out) *out = (uint32_t)sizeof(GCertificateInfo) + (uint32_t)sizeof(kCaptureCert);
    ret_x(c, 0);
}
static void ov_ssl_certificates(Cpu *c) {
    GCertificateInfo **out = arg_p(c, 0);
    GCertificateInfo *info = arg_p(c, 1);
    uint32_t *ids = arg_p(c, 3);
    if (!out || !info || arg_x(c, 2) < sizeof(*info) + sizeof(kCaptureCert)) {
        ret_x(c, (uint64_t)-1); return;
    }
    memset(info, 0, sizeof(*info));
    info->id = ids ? ids[0] : 0;
    info->size = sizeof(kCaptureCert);
    info->data = (unsigned char *)(info + 1);
    memcpy((void *)info->data, kCaptureCert, sizeof(kCaptureCert));
    *out = info;
    ret_x(c, 0);
}
// ---- registration table ----------------------------------------------------
typedef struct { const char *name; GuestFn fn; } Reg;
static const Reg kRegs[] = {
    {"_ZN2nn4nifm18IsNetworkAvailableEv", ov_nifm_is_network_available},
    {"_ZN2nn4nifm27SubmitNetworkRequestAndWaitEv", ov_nifm_submit_wait},
    {"_ZN2nn4nifm22GetCurrentIpConfigInfoEP7in_addrS2_S2_S2_S2_", ov_nifm_ip_config},
    {"_ZN2nn7account32IsNetworkServiceAccountAvailableEPbRKNS0_10UserHandleE", ov_nsa_available},
    {"_ZN2nn7account36EnsureNetworkServiceAccountAvailableERKNS0_10UserHandleE", ov_ok},
    {"_ZN2nn7account26GetNetworkServiceAccountIdEPNS0_23NetworkServiceAccountIdERKNS0_10UserHandleE", ov_nsa_get_id},
    {"_ZN2nn7account37LoadNetworkServiceAccountIdTokenCacheEPmPcmRKNS0_10UserHandleE", ov_nsa_load_token},
    {"_ZN2nn7account44EnsureNetworkServiceAccountIdTokenCacheAsyncEPNS0_12AsyncContextERKNS0_10UserHandleE", ov_ok},
    {"_ZN2nn7account12AsyncContext14GetSystemEventEPNS_2os11SystemEventE", ov_nsa_async_event},
    {"_ZN2nn7account12AsyncContext9GetResultEv", ov_ok},
    {"_ZN2nn4time39AsyncEnsureNetworkClockAvailabilityTask10InitializeENS_2os14EventClearModeE", ov_clock_init},
    {"_ZN2nn4time39AsyncEnsureNetworkClockAvailabilityTask9StartTaskEv", ov_ok},
    {"_ZN2nn4time39AsyncEnsureNetworkClockAvailabilityTask9GetResultEv", ov_ok},
    {"_ZN2nn4time39AsyncEnsureNetworkClockAvailabilityTask14GetFinishEventEv", ov_clock_event},
    {"_ZN2nn4time39AsyncEnsureNetworkClockAvailabilityTaskD1Ev", ov_clock_destroy},
    {"_ZN2nn3nsd24GetEnvironmentIdentifierEPNS0_21EnvironmentIdentifierE", ov_nsd_env_id},
    {"_ZN2nn2fs8MountRomEPKc", ov_fs_mount_rom},
    {"_ZN2nn2fs7UnmountEPKc", ov_fs_unmount},
    {"_ZN2nn2fs8OpenFileEPNS0_10FileHandleEPKci", ov_fs_open_file},
    {"_ZN2nn2fs12GetEntryTypeEPNS0_18DirectoryEntryTypeEPKc", ov_fs_entry_type},
    {"_ZN2nn2fs8ReadFileEPmNS0_10FileHandleElPvm", ov_fs_read_file},
    {"_ZN2nn2fs11GetFileSizeEPlNS0_10FileHandleE", ov_fs_file_size},
    {"_ZN2nn2fs9CloseFileENS0_10FileHandleE", ov_fs_close_file},
    // ssl C++
    {"_ZN2nn3ssl10InitializeEv", ov_ssl_initialize},
    {"_ZN2nn3ssl7Context6CreateENS1_10SslVersionE", ov_ssl_ctx_create},
    {"_ZN2nn3ssl7Context15ImportServerPkiEPmPKcjNS0_17CertificateFormatE", ov_ssl_import_pki},
    {"_ZN2nn3ssl14BuiltInManager28GetBuiltInCertificateBufSizeEPjPNS0_15CaCertificateIdEj", ov_ssl_cert_bufsize},
    {"_ZN2nn3ssl14BuiltInManager22GetBuiltInCertificatesEPPNS0_26BuiltInCertificateInfoBaseEPhjPNS0_15CaCertificateIdEj", ov_ssl_certificates},
    // Remaining SDK and Pia overrides
    {"nnsslInitialize", ov_ssl_initialize},
    {"_ZN2nn6socket11GetAddrInfoEPKcS2_PK8addrinfoPPS3_", ov_sock_getaddrinfo_bsd},
    {"_ZN2nn6socket12FreeAddrInfoEP8addrinfo", ov_sock_freeaddrinfo},
    {"_ZN2nn6socket6SocketENS0_6FamilyENS0_4TypeENS0_8ProtocolE", ov_pia_socket},
    {"_ZN2nn6socket10SetSockOptEiNS0_5LevelENS0_6OptionEPKvj", ov_pia_setsockopt},
    {"_ZN2nn6socket11GetAddrInfoEPKcS2_PKNS0_8AddrInfoEPPS3_", ov_sock_getaddrinfo_cpp},
    {"_ZN2nn6socket4PollEP6pollfdmi", ov_sock_poll},
    {"_ZN2nn6socket4BindEiPKNS0_8SockAddrEj", ov_pia_bind},
    {"_ZN2nn6socket12FreeAddrInfoEPNS0_8AddrInfoE", ov_sock_freeaddrinfo},
    {"_ZN2nn3err9ShowErrorERKNS0_18ErrorResultVariantE", ov_show_error_variant},
    {"_ZN2nn6socket6SendToEiPKvmNS0_7MsgFlagEPKNS0_8SockAddrEj", ov_pia_sendto},
    {"_ZN2nn7friends27CheckFriendListAvailabilityEPbRKNS_7account3UidE", ov_friend_list_available},
    {"pthread_cond_timedwait", ov_cond_timedwait},
    {"_ZN2nn6socket8RecvFromEiPvmNS0_7MsgFlagEPNS0_8SockAddrEPj", ov_pia_recvfrom},
    {"_ZN2nn6socket12GetLastErrorEv", ov_pia_errno},
    {"_ZN2nn6socket5CloseEi", ov_sock_close},
    {"_ZN2nn6socket12GetLastErrnoEv", ov_pia_errno},
    {"clock_gettime", ov_clock_gettime},
    // socket C-API
    {"_ZN2nn2os13SwitchToFiberEPNS0_9FiberTypeE", ov_fiber_switch},
    {"_ZN2nn2os15InitializeFiberEPNS0_9FiberTypeEPFS2_PvES3_S3_mi", ov_fiber_initialize},
    {"_ZN2nn6socket7EventFdEmNS0_12EventFdFlagsE", ov_eventfd},
    {"_ZN2nn6socket11EventFdReadEiPm", ov_eventfd_read},
    {"_ZN2nn6socket12EventFdWriteEim", ov_eventfd_write},
    {"nnsocketSocket", ov_sock_socket},
    {"nnsocketConnect", ov_sock_connect},
    {"nnsocketClose", ov_sock_close},
    {"nnsocketFcntl", ov_sock_fcntl},
    {"nnsocketSetSockOpt", ov_sock_setsockopt},
    {"nnsocketGetSockOpt", ov_sock_getsockopt},
    {"nnsocketShutdown", ov_sock_shutdown},
    {"nnsocketSendMsg", ov_sock_sendmsg},
    {"nnsocketRecvMsg", ov_sock_recvmsg},
    {"nnsocketPoll", ov_sock_poll},
    {"nnsocketFreeAddrInfo", ov_sock_freeaddrinfo},
    {"__errno_location", ov_errno_location},
    {"nnsocketGetAddrInfo", ov_sock_getaddrinfo},
};
_Static_assert(sizeof(kRegs) / sizeof(kRegs[0]) <= 64, "runtime override limit");

int npln_register(void) {
    // Opt-in: engage only if mkwmp.toml is present (or MKWMP_ENABLE=1 for tests).
    // A stock launch (no config) must behave like offline: NPLN init aborts if
    // we open the gates without actively capturing.
    const MkwmpConfig *cfg = mkwmp_config();
    const char *enabled = getenv("MKWMP_ENABLE");
    int engage = cfg->present || (enabled && !strcmp(enabled, "1"));
    if (!engage) { log_line("npln: inactive (no mkwmp.toml; set MKWMP_ENABLE=1 to force)"); return 0; }
    const char *player = getenv("MKWMP_PLAYER");
    g_second_player = player ? !strcmp(player, "2") : cfg->slot == 2;
    const char *edges = getenv("MKWMP_TRACE_EDGES");
    g_trace_edges = edges && !strcmp(edges, "1");
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    if (!g_rt.hostfn_add_override || !g_rt.ldr_count) { log_line("npln_register: runtime not resolved"); return -1; }
    if (!g_dele.fiber_initialize || !g_dele.fiber_switch || !g_dele.fiber_switch_to ||
        !g_dele.fibers || !g_dele.fiber_new || !g_dele.fiber_free || !g_dele.current_cpu ||
        !g_dele.core_away || !g_dele.core_back) {
        log_line("npln_register: required fiber delegates missing; capture disabled"); return -1;
    }
    if (g_rt.ldr_count() != 0) { log_line("npln_register: TOO LATE"); return -1; }
    int n = (int)(sizeof(kRegs) / sizeof(kRegs[0]));
    for (int i = 0; i < n; i++) g_rt.hostfn_add_override(kRegs[i].name, kRegs[i].fn);
    log_kv("npln_register: overrides", (uint64_t)n);
    return n;
}
