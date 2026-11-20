// crash.h. Everything between a fault and the record's last line uses fixed buffers, a formatter of its own and
// kernel32 only: no heap, no C runtime locks, no loader lock (modules are read from the PEB's list).
#include "crash.h"

#if !defined(_WIN32) || !defined(__x86_64__)

void crash_install(const char *dir, int before_runtime) { (void)dir, (void)before_runtime; }
void crash_set_guest_cpu(void *(*current)(void)) { (void)current; }
const char *crash_last_id(void) { return ""; }
int crash_ready(void) { return 0; }

#else

#include "breadcrumbs.h"
#include "log.h"
#include "plat.h"

#include <windows.h>
#include <winternl.h>
#include <dbghelp.h>
#include <signal.h>
#include <stdarg.h>
#include <stdlib.h>
#include <string.h>

#ifndef MKWMP_VERSION
#define MKWMP_VERSION "dev"
#endif

#define CODE_HEAP_CORRUPTION 0xC0000374u
#define CODE_STACK_BUFFER_OVERRUN 0xC0000409u   // "fail fast" as the C runtime raises it
#define CODE_FAIL_FAST 0xC0000602u
#define CODE_INVALID_CRT_PARAMETER 0xC0000417u
#define CODE_FATAL_APP_EXIT 0x40000015u         // abort()

#define GUEST_LO 0x7000000000ull   // main's base (recomp layout.py FIRST_BASE); the guest modules end below 2^39
#define GUEST_HI 0x8000000000ull

// ---- a formatter with no C runtime behind it: %s %c %d %u %x %lld %llu %llx %%, with a width and a 0 flag
static size_t put(char *o, size_t cap, size_t at, char ch) {
    if (at + 1 < cap) o[at] = ch;
    return at + 1;
}

static size_t vfmt(char *o, size_t cap, const char *f, va_list ap) {
    size_t at = 0;
    for (; *f; f++) {
        if (*f != '%') { at = put(o, cap, at, *f); continue; }
        f++;
        int zero = 0, width = 0, wide = 0, n = 0;
        if (*f == '0') zero = 1, f++;
        while (*f >= '0' && *f <= '9') width = width * 10 + (*f++ - '0');
        while (*f == 'l') wide++, f++;
        char tmp[24];
        unsigned long long u = 0;
        int negative = 0;
        switch (*f) {
        case 's': {
            const char *s = va_arg(ap, const char *);
            int len = 0;
            for (s = s ? s : "(null)"; *s; s++, len++) at = put(o, cap, at, *s);
            for (; len < width; len++) at = put(o, cap, at, ' ');
            continue;
        }
        case 'c': at = put(o, cap, at, (char)va_arg(ap, int)); continue;
        case '%': at = put(o, cap, at, '%'); continue;
        case 'd': {
            long long v = wide >= 2 ? va_arg(ap, long long) : (long long)va_arg(ap, int);
            negative = v < 0;
            u = negative ? 0ull - (unsigned long long)v : (unsigned long long)v;
            do tmp[n++] = (char)('0' + u % 10); while (u /= 10);
            break;
        }
        case 'u':
            u = wide >= 2 ? va_arg(ap, unsigned long long) : (unsigned long long)va_arg(ap, unsigned);
            do tmp[n++] = (char)('0' + u % 10); while (u /= 10);
            break;
        case 'x':
            u = wide >= 2 ? va_arg(ap, unsigned long long) : (unsigned long long)va_arg(ap, unsigned);
            do tmp[n++] = "0123456789abcdef"[u & 15]; while (u >>= 4);
            break;
        default:
            if (!*f) f--;   // a lone % at the end
            continue;
        }
        if (negative) tmp[n++] = '-';
        for (int i = n; i < width; i++) at = put(o, cap, at, zero ? '0' : ' ');
        while (n) at = put(o, cap, at, tmp[--n]);
    }
    if (cap) o[at < cap ? at : cap - 1] = 0;
    return at < cap ? at : cap ? cap - 1 : 0;
}

static size_t fmt(char *o, size_t cap, const char *f, ...) {
    va_list ap;
    va_start(ap, f);
    size_t n = vfmt(o, cap, f, ap);
    va_end(ap);
    return n;
}

// ---- state
typedef struct {
    const char *stage;
    EXCEPTION_POINTERS *ep;
    DWORD tid;
    void *cpu;            // the runtime's Cpu of the faulting thread, when it could be asked
    const char *reason;
    int blame_caller;     // the event has no fault address of its own (abort, terminate): name the first frame of
                          // someone else's code instead of the C runtime's or ours
    char id[48];
} Job;

typedef struct {
    volatile int32_t live;   // 1: holds a fault nobody has resolved or recorded yet
    DWORD tid;
    uint64_t ms;
    EXCEPTION_RECORD er;
    CONTEXT ctx;
} Pending;

typedef struct {
    uintptr_t base, size;
    uint32_t stamp;
    char name[48];
} Mod;

enum { PENDING = 8, MODS = 256, FRAMES = 64, EVENTS = 50 };

static volatile int32_t g_installed, g_ready, g_before_runtime;
static wchar_t g_dir[MAX_PATH];          // with a trailing separator
static uint64_t g_start_ms;
static uintptr_t g_exe, g_self;
static volatile uintptr_t g_game;        // mkwgame.dll, once seen
static void *(*volatile g_cpu_current)(void);

static HANDLE g_writer, g_job_ev, g_done_ev;
static DWORD g_writer_tid;
static volatile int32_t g_busy_tid, g_count;
static Job g_job;
static struct { DWORD tid, code; void *address; } g_last;
static char g_last_id[48];

static Pending g_pending[PENDING];
static volatile uint32_t g_pending_next;

static LPTOP_LEVEL_EXCEPTION_FILTER g_prev_filter;
static _invalid_parameter_handler g_prev_invalid;
static void (*g_prev_abort)(int);
static volatile int32_t g_chain_tid;     // the thread inside a chained filter / handler: a ring of two stops there

static char g_os[128] = "?", g_gpu[320] = "?";
typedef BOOL (WINAPI *MiniDumpFn)(HANDLE, DWORD, HANDLE, MINIDUMP_TYPE, PMINIDUMP_EXCEPTION_INFORMATION,
                                  PMINIDUMP_USER_STREAM_INFORMATION, PMINIDUMP_CALLBACK_INFORMATION);
static MiniDumpFn g_minidump;

// the record being written (one at a time: g_busy_tid)
static HANDLE g_out = INVALID_HANDLE_VALUE;
static char g_line[1280];
static Mod g_mods[MODS];
static int g_nmods;
static uintptr_t g_frames[FRAMES];
static int g_nframes;
static uintptr_t g_cpu_found;
static CrumbEvent g_events[EVENTS];

// ---- memory questions
static int query(uintptr_t p, MEMORY_BASIC_INFORMATION *mi) {
    return p >= 0x10000 && VirtualQuery((const void *)p, mi, sizeof *mi) == sizeof *mi;
}

static int readable(uintptr_t p, size_t n) {
    MEMORY_BASIC_INFORMATION mi;
    if (!query(p, &mi) || mi.State != MEM_COMMIT || (mi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) || !(mi.Protect & 0xff))
        return 0;
    return (uintptr_t)mi.BaseAddress + mi.RegionSize - p >= n;
}

static int writable(uintptr_t p) {
    MEMORY_BASIC_INFORMATION mi;
    if (!query(p, &mi) || mi.State != MEM_COMMIT || (mi.Protect & (PAGE_NOACCESS | PAGE_GUARD))) return 0;
    return (mi.Protect & (PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) != 0;
}

// ---- modules, from the loader's list in the PEB (no loader lock: a fault may have come while it was held)
static int pe_facts(uintptr_t base, uintptr_t *size, uint32_t *stamp) {
    if (!readable(base, 0x400)) return 0;
    const IMAGE_DOS_HEADER *dos = (const IMAGE_DOS_HEADER *)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0 || dos->e_lfanew > 0x300) return 0;
    const IMAGE_NT_HEADERS64 *nt = (const IMAGE_NT_HEADERS64 *)(base + (uintptr_t)dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return 0;
    *size = nt->OptionalHeader.SizeOfImage;
    *stamp = nt->FileHeader.TimeDateStamp;
    return 1;
}

static void modules_load(void) {
    g_nmods = 0;
    PEB *peb = NtCurrentTeb()->ProcessEnvironmentBlock;
    if (!readable((uintptr_t)peb, sizeof *peb) || !readable((uintptr_t)peb->Ldr, sizeof *peb->Ldr)) return;
    LIST_ENTRY *head = &peb->Ldr->InMemoryOrderModuleList;
    int turns = 0;
    for (LIST_ENTRY *e = head->Flink; e && e != head && turns++ < 2048 && g_nmods < MODS; e = e->Flink) {
        LDR_DATA_TABLE_ENTRY *m = CONTAINING_RECORD(e, LDR_DATA_TABLE_ENTRY, InMemoryOrderLinks);
        if (!readable((uintptr_t)m, sizeof *m)) break;
        Mod *o = &g_mods[g_nmods];
        o->base = (uintptr_t)m->DllBase;
        o->size = 0, o->stamp = 0;
        if (!o->base || !pe_facts(o->base, &o->size, &o->stamp)) continue;
        const wchar_t *full = m->FullDllName.Buffer;
        size_t len = m->FullDllName.Length / sizeof(wchar_t), from = 0;
        if (!full || !len || !readable((uintptr_t)full, len * sizeof(wchar_t))) {
            fmt(o->name, sizeof o->name, "module@%llx", (unsigned long long)o->base);
        } else {
            for (size_t i = 0; i < len; i++)
                if (full[i] == L'\\' || full[i] == L'/') from = i + 1;
            size_t k = 0;
            for (size_t i = from; i < len && k < sizeof o->name - 1; i++)
                o->name[k++] = full[i] > 0x20 && full[i] < 0x7f ? (char)full[i] : '_';
            o->name[k] = 0;
        }
        g_nmods++;
    }
}

static const Mod *module_of(uintptr_t addr) {
    for (int i = 0; i < g_nmods; i++)
        if (addr >= g_mods[i].base && addr - g_mods[i].base < g_mods[i].size) return &g_mods[i];
    return NULL;
}

static const char *describe(uintptr_t addr, char *buf, size_t cap) {
    const Mod *m = module_of(addr);
    if (m) fmt(buf, cap, "%s+0x%llx", m->name, (unsigned long long)(addr - m->base));
    else fmt(buf, cap, "(no module) 0x%llx", (unsigned long long)addr);
    return buf;
}

static int name_is(const char *a, const char *b) {
    for (;; a++, b++) {
        char x = *a >= 'A' && *a <= 'Z' ? (char)(*a + 32) : *a, y = *b >= 'A' && *b <= 'Z' ? (char)(*b + 32) : *b;
        if (x != y) return 0;
        if (!x) return 1;
    }
}

static int name_starts(const char *a, const char *prefix) {
    for (; *prefix; a++, prefix++) {
        char x = *a >= 'A' && *a <= 'Z' ? (char)(*a + 32) : *a;
        if (x != *prefix) return 0;
    }
    return 1;
}

// ---- the record's lines
static void out(const char *f, ...) {
    va_list ap;
    va_start(ap, f);
    size_t n = vfmt(g_line, sizeof g_line - 2, f, ap);
    va_end(ap);
    g_line[n++] = '\r', g_line[n++] = '\n';
    DWORD wrote;
    if (g_out != INVALID_HANDLE_VALUE) WriteFile(g_out, g_line, (DWORD)n, &wrote, NULL);
}

static void flush(void) {
    if (g_out != INVALID_HANDLE_VALUE) FlushFileBuffers(g_out);
}

static void path_of(wchar_t *path, const char *name) {
    size_t n = 0;
    for (; g_dir[n] && n < MAX_PATH - 1; n++) path[n] = g_dir[n];
    for (; *name && n < MAX_PATH - 1; name++) path[n++] = (wchar_t)(unsigned char)*name;
    path[n] = 0;
}

static const char *code_name(DWORD code) {
    switch (code) {
    case EXCEPTION_ACCESS_VIOLATION: return "ACCESS_VIOLATION";
    case EXCEPTION_ILLEGAL_INSTRUCTION: return "ILLEGAL_INSTRUCTION";
    case EXCEPTION_PRIV_INSTRUCTION: return "PRIV_INSTRUCTION";
    case EXCEPTION_STACK_OVERFLOW: return "STACK_OVERFLOW";
    case EXCEPTION_INT_DIVIDE_BY_ZERO: return "INT_DIVIDE_BY_ZERO";
    case EXCEPTION_IN_PAGE_ERROR: return "IN_PAGE_ERROR";
    case CODE_HEAP_CORRUPTION: return "HEAP_CORRUPTION";
    case CODE_STACK_BUFFER_OVERRUN: return "FAIL_FAST";
    case CODE_FAIL_FAST: return "FAIL_FAST_EXCEPTION";
    case CODE_INVALID_CRT_PARAMETER: return "CRT_INVALID_PARAMETER";
    case CODE_FATAL_APP_EXIT: return "ABORT";
    case MKWMP_CRASH_EXCEPTION: return "TERMINATE";
    default: return "EXCEPTION";
    }
}

static int fatal_code(DWORD code) {
    return code == EXCEPTION_ACCESS_VIOLATION || code == EXCEPTION_ILLEGAL_INSTRUCTION ||
           code == EXCEPTION_PRIV_INSTRUCTION || code == EXCEPTION_STACK_OVERFLOW ||
           code == EXCEPTION_INT_DIVIDE_BY_ZERO || code == EXCEPTION_IN_PAGE_ERROR || code == CODE_HEAP_CORRUPTION ||
           code == CODE_STACK_BUFFER_OVERRUN || code == CODE_FAIL_FAST || code == MKWMP_CRASH_EXCEPTION;
}

// ---- the guest Cpu: recomp.h's prefix (x0..x30 at 0, sp at 256, pc at 264). Lifted functions are void f(Cpu *), so
// the pointer sits in a host register of the faulting frame or of a frame above it.
static int cpu_like(uintptr_t p) {
    if ((p & 7) || !readable(p, 320)) return 0;
    const uint64_t *x = (const uint64_t *)p;
    uint64_t sp = x[32], pc = x[33];
    if (pc < GUEST_LO || pc >= GUEST_HI || (pc & 3)) return 0;
    return sp && !(sp & 7) && readable((uintptr_t)sp, 16);
}

static void cpu_scan(const CONTEXT *c, int first) {
    if (g_cpu_found) return;
    // the callee-saved registers hold it in every frame; the argument registers only where the fault was
    const DWORD64 kept[] = {c->Rbx, c->Rsi, c->Rdi, c->Rbp, c->R12, c->R13, c->R14, c->R15};
    const DWORD64 args[] = {c->Rcx, c->Rdx, c->R8, c->R9, c->Rax, c->R10, c->R11};
    for (size_t i = 0; i < sizeof kept / sizeof kept[0]; i++)
        if (cpu_like((uintptr_t)kept[i])) { g_cpu_found = (uintptr_t)kept[i]; return; }
    for (size_t i = 0; first && i < sizeof args / sizeof args[0]; i++)
        if (cpu_like((uintptr_t)args[i])) { g_cpu_found = (uintptr_t)args[i]; return; }
}

// ---- the stack, unwound from the fault's context (RtlCaptureStackBackTrace would walk the handler's)
static void walk(const CONTEXT *start) {
    static CONTEXT c;   // 1.2 KB: not on the stack of whoever writes inline
    c = *start;
    g_nframes = 0;
    for (int i = 0; i < FRAMES; i++) {
        uintptr_t pc = (uintptr_t)c.Rip, sp = (uintptr_t)c.Rsp;
        if (!pc) break;
        g_frames[g_nframes++] = pc;
        cpu_scan(&c, i == 0);
        if (!readable(sp, 8)) break;
        DWORD64 base = 0, establisher = 0;
        void *handler_data = NULL;
        MEMORY_BASIC_INFORMATION mi;
        PRUNTIME_FUNCTION fn = query(pc, &mi) && mi.State == MEM_COMMIT ? RtlLookupFunctionEntry(pc, &base, NULL) : NULL;
        if (fn) {
            RtlVirtualUnwind(UNW_FLAG_NHANDLER, base, pc, fn, &c, &handler_data, &establisher, NULL);
        } else {   // a leaf, or a jump to nowhere: the return address is on top
            c.Rip = *(const DWORD64 *)sp;
            c.Rsp = sp + 8;
        }
        if ((uintptr_t)c.Rsp <= sp) break;   // a stack only grows down: anything else is not a frame
    }
}

static const char *guest_name(uint64_t a, char *buf, size_t cap) {
    if (a >= GUEST_LO && a < GUEST_LO + 0x10000000ull)
        fmt(buf, cap, "main+0x%llx", (unsigned long long)(a - GUEST_LO));
    else
        fmt(buf, cap, "0x%llx", (unsigned long long)a);
    return buf;
}

// ---- minidump
static int dumps_prune(int keep) {
    struct { FILETIME time; wchar_t name[80]; } found[48];
    int n = 0;
    wchar_t pattern[MAX_PATH], path[MAX_PATH];
    path_of(pattern, "mkwmp-crash-*.dmp");
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW(pattern, &fd);
    if (h == INVALID_HANDLE_VALUE) return 0;
    do {
        if (n == 48 || wcslen(fd.cFileName) >= 80) continue;
        found[n].time = fd.ftLastWriteTime;
        wcscpy(found[n++].name, fd.cFileName);
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    int left = n;
    while (left > keep) {
        int oldest = -1;
        for (int i = 0; i < n; i++)
            if (found[i].name[0] && (oldest < 0 || CompareFileTime(&found[i].time, &found[oldest].time) < 0)) oldest = i;
        if (oldest < 0) break;
        size_t at = wcslen(g_dir);
        wcscpy(path, g_dir);
        wcscpy(path + at, found[oldest].name);
        DeleteFileW(path);
        found[oldest].name[0] = 0;
        left--;
    }
    return left;
}

static uint64_t dump_write(const Job *j, const wchar_t *path, int type) {
    HANDLE f = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return 0;
    MINIDUMP_EXCEPTION_INFORMATION info = {.ThreadId = j->tid, .ExceptionPointers = j->ep, .ClientPointers = FALSE};
    BOOL ok = g_minidump(GetCurrentProcess(), GetCurrentProcessId(), f, (MINIDUMP_TYPE)type, j->ep ? &info : NULL, NULL, NULL);
    LARGE_INTEGER size = {0};
    GetFileSizeEx(f, &size);
    FlushFileBuffers(f);
    CloseHandle(f);
    if (!ok || !size.QuadPart) { DeleteFileW(path); return 0; }
    return (uint64_t)size.QuadPart;
}

// ---- one record
static void write_record(const Job *j) {
    const EXCEPTION_RECORD *er = j->ep->ExceptionRecord;
    const CONTEXT *ctx = j->ep->ContextRecord;
    char a[160], b[96];
    wchar_t path[MAX_PATH];
    path_of(path, "mkwmp-crash.log");
    g_out = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_ALWAYS,
                        FILE_ATTRIBUTE_NORMAL, NULL);

    modules_load();
    g_cpu_found = j->cpu && cpu_like((uintptr_t)j->cpu) ? (uintptr_t)j->cpu : 0;
    walk(ctx);

    // where: the fault's address, or for an event without one the first frame that is neither the C runtime's nor ours
    uintptr_t where = (uintptr_t)er->ExceptionAddress;
    if (j->blame_caller) {
        for (int i = 0; i < g_nframes; i++) {
            const Mod *m = module_of(g_frames[i]);
            if (m && (m->base == g_self || name_starts(m->name, "ucrtbase") || name_starts(m->name, "msvcr") ||
                      name_starts(m->name, "kernelbase") || name_starts(m->name, "kernel32") ||
                      name_starts(m->name, "ntdll") || name_starts(m->name, "api-ms-")))
                continue;
            where = g_frames[i];
            break;
        }
    }
    int peers = crumb_peer_count(15000);
    char mesh[CRUMB_VALUE] = "?", key[CRUMB_KEY], value[CRUMB_VALUE];
    for (int i = 0; i < CRUMB_STATES; i++)
        if (crumb_state_at(i, key, value) && name_is(key, "mesh")) {
            size_t k = 0;   // its first word: "created", "joined"
            for (; value[k] && value[k] != ' ' && k < sizeof mesh - 1; k++) mesh[k] = value[k];
            mesh[k] = 0;
        }
    // the room as the lobby numbers it, when something told us (breadcrumbs.h); else the Pia endpoints, marked "~"
    static CrumbRoom room;
    int in_room = crumb_room_get(&room);
    char uid[CRUMB_VALUE] = "?", host[4] = "?";
    for (int i = 0; i < CRUMB_STATES; i++)
        if (crumb_state_at(i, key, value) && name_is(key, "uid")) memcpy(uid, value, sizeof uid);
    if (in_room) fmt(b, sizeof b, "%d", room.n), fmt(host, sizeof host, "%d", crumb_room_host(&room));
    else if (peers) fmt(b, sizeof b, "~%d", peers + 1);
    else fmt(b, sizeof b, "?");
    char summary[320];
    fmt(summary, sizeof summary, "%s in %s (players=%s, host=%s, mesh=%s)", code_name(er->ExceptionCode),
        describe(where, a, sizeof a), b, host, mesh);

    SYSTEMTIME now;
    GetSystemTime(&now);
    uint64_t uptime = plat_ms() - g_start_ms;
    out("=== MKWMP CRASH %s ===", j->id);
    out("summary: %s", summary);
    out("time_utc: %04u-%02u-%02uT%02u:%02u:%02u.%03uZ", now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute,
        now.wSecond, now.wMilliseconds);
    out("uptime_ms: %llu", (unsigned long long)uptime);
    out("stage: %s", j->stage);
    out("pid: %u", (unsigned)GetCurrentProcessId());
    out("tid: %u", (unsigned)j->tid);
    out("exception: 0x%08x %s", (unsigned)er->ExceptionCode, code_name(er->ExceptionCode));
    out("address: 0x%016llx %s", (unsigned long long)(uintptr_t)er->ExceptionAddress,
        describe((uintptr_t)er->ExceptionAddress, a, sizeof a));
    if ((er->ExceptionCode == EXCEPTION_ACCESS_VIOLATION || er->ExceptionCode == EXCEPTION_IN_PAGE_ERROR) &&
        er->NumberParameters >= 2) {
        ULONG_PTR kind = er->ExceptionInformation[0];
        out("access: %s 0x%016llx", kind == 0 ? "read" : kind == 1 ? "write" : kind == 8 ? "execute" : "other",
            (unsigned long long)er->ExceptionInformation[1]);
    }
    if (j->reason) {
        size_t k = 0;
        if (readable((uintptr_t)j->reason, 1))
            for (; k < sizeof a - 1 && readable((uintptr_t)j->reason + k, 1) && j->reason[k]; k++)
                a[k] = j->reason[k] >= 0x20 ? j->reason[k] : ' ';
        a[k] = 0;
        out("reason: %s", a);
    }
    out("os: %s", g_os);
    out("gpu: %s", g_gpu);
    flush();   // the lines that matter most are on disk whatever happens next

    out("state:");
    for (int i = 0; i < CRUMB_STATES; i++)
        if (crumb_state_at(i, key, value)) out("  %s=%s", key, value);
    if (in_room) {
        char list[160], ussid[16], age[24];
        fmt(ussid, sizeof ussid, "%u", (unsigned)room.ussid);
        if (room.at) {
            // since the lobby reported the room (the seat server's clock against this PC's: negative if this one is behind)
            FILETIME ft;
            GetSystemTimeAsFileTime(&ft);
            uint64_t unix_ms = ((uint64_t)ft.dwHighDateTime << 32 | ft.dwLowDateTime) / 10000 - 11644473600000ull;
            fmt(age, sizeof age, "%lld", (long long)(unix_ms - room.at));
        } else {
            fmt(age, sizeof age, "%llu", (unsigned long long)(plat_ms() - room.ms));   // a launcher without "at": since told
        }
        out("room: lobby=%s ussid=%s uid=%s host=%s players=%s ussids=%s age_ms=%s", room.lobby, ussid, uid, host, b,
            crumb_room_list(&room, list, sizeof list), age);
    } else {
        out("room: lobby=? ussid=? uid=%s host=? players=%s ussids=? age_ms=?", uid, b);
    }
    out("peers: %d", peers);

    const Mod *faulting = module_of((uintptr_t)er->ExceptionAddress), *blamed = module_of(where);
    for (int i = 0; i < g_nmods; i++) {
        const Mod *m = &g_mods[i];
        if (m != faulting && m != blamed && m->base != g_exe && m->base != g_self && !name_is(m->name, "mkwgame.dll") &&
            !name_starts(m->name, "mkwmp") && !name_is(m->name, "winmm.dll"))
            continue;
        out("module: %s base=0x%llx size=0x%llx time=0x%08x", m->name, (unsigned long long)m->base,
            (unsigned long long)m->size, (unsigned)m->stamp);
    }

    out("registers:");
    out("  rax=%016llx rbx=%016llx rcx=%016llx rdx=%016llx", ctx->Rax, ctx->Rbx, ctx->Rcx, ctx->Rdx);
    out("  rsi=%016llx rdi=%016llx rbp=%016llx rsp=%016llx", ctx->Rsi, ctx->Rdi, ctx->Rbp, ctx->Rsp);
    out("  r8 =%016llx r9 =%016llx r10=%016llx r11=%016llx", ctx->R8, ctx->R9, ctx->R10, ctx->R11);
    out("  r12=%016llx r13=%016llx r14=%016llx r15=%016llx", ctx->R12, ctx->R13, ctx->R14, ctx->R15);
    out("  rip=%016llx eflags=%08x", ctx->Rip, (unsigned)ctx->EFlags);

    out("stack:");
    for (int i = 0; i < g_nframes; i++) out("  #%02d %s", i, describe(g_frames[i], a, sizeof a));

    if (g_cpu_found) {
        const uint64_t *x = (const uint64_t *)g_cpu_found;
        out("guest: cpu=0x%llx pc=%s lr=%s sp=0x%llx fp=0x%llx", (unsigned long long)g_cpu_found,
            guest_name(x[33], a, sizeof a), guest_name(x[30], b, sizeof b), (unsigned long long)x[32],
            (unsigned long long)x[29]);
        out("  x0=%016llx x1=%016llx x2=%016llx x3=%016llx", x[0], x[1], x[2], x[3]);
        out("  x4=%016llx x5=%016llx x6=%016llx x7=%016llx", x[4], x[5], x[6], x[7]);
        out("  x8=%016llx x19=%016llx x20=%016llx x21=%016llx", x[8], x[19], x[20], x[21]);
        out("guest_stack:");
        uint64_t fp = x[29];
        for (int n = 0; n < 24 && fp && !(fp & 7) && readable((uintptr_t)fp, 16); n++) {
            uint64_t next = ((const uint64_t *)(uintptr_t)fp)[0], lr = ((const uint64_t *)(uintptr_t)fp)[1];
            out("  g#%02d %s", n, guest_name(lr, a, sizeof a));
            if (next <= fp || next - fp > (64u << 20)) break;
            fp = next;
        }
    } else {
        out("guest: none (no guest Cpu in the host registers: not a guest thread, or outside lifted code)");
    }

    out("events:");
    int n = crumb_events(g_events, EVENTS);
    uint64_t tick = uptime + g_start_ms;   // plat_ms() when `now` was read
    FILETIME file_now;
    SystemTimeToFileTime(&now, &file_now);
    for (int i = 0; i < n; i++) {   // each with its own UTC time: the record's, less how long ago it was
        ULARGE_INTEGER at;
        at.LowPart = file_now.dwLowDateTime, at.HighPart = file_now.dwHighDateTime;
        if (tick > g_events[i].ms) at.QuadPart -= (tick - g_events[i].ms) * 10000ull;
        FILETIME file_at = {at.LowPart, at.HighPart};
        SYSTEMTIME t = now;
        FileTimeToSystemTime(&file_at, &t);
        out("  [%8llu] %02u:%02u:%02u.%03uZ t%u %s", (unsigned long long)(g_events[i].ms - g_start_ms), t.wHour, t.wMinute,
            t.wSecond, t.wMilliseconds, (unsigned)g_events[i].tid, g_events[i].text);
    }
    flush();

    // the session log's last line (no lock: the faulting thread may hold it; one WriteFile is one line)
    uint64_t log_start = 0;
    HANDLE session = (HANDLE)log_crash_file(&log_start);
    if (session) {
        char line[480];
        size_t len = fmt(line, sizeof line - 2, "[%8llums] CRASH %s %s", (unsigned long long)(plat_ms() - log_start), j->id,
                         summary);
        line[len++] = '\r', line[len++] = '\n';
        DWORD wrote;
        WriteFile(session, line, (DWORD)len, &wrote, NULL);
        FlushFileBuffers(session);
    }

    if (!g_minidump) {
        out("dump: none (dbghelp.dll could not be loaded)");
    } else {
        dumps_prune(CRASH_MAX_DUMPS - 1);
        char name[96];
        fmt(name, sizeof name, "mkwmp-crash-%s.dmp", j->id);
        path_of(path, name);
        const int rich = MiniDumpWithIndirectlyReferencedMemory | MiniDumpScanMemory | MiniDumpWithThreadInfo |
                         MiniDumpWithUnloadedModules;
        uint64_t size = dump_write(j, path, rich);
        const char *kind = "with referenced memory";
        if (!size || size > CRASH_DUMP_CAP) {
            if (size) DeleteFileW(path);
            size = dump_write(j, path, MiniDumpNormal | MiniDumpWithThreadInfo);
            kind = "threads and stacks only";
        }
        if (size) out("dump: %s %llu bytes (%s)", name, (unsigned long long)size, kind);
        else out("dump: none (MiniDumpWriteDump failed, error %u)", (unsigned)GetLastError());
    }
    out("=== END %s ===", j->id);
    flush();
    if (g_out != INVALID_HANDLE_VALUE) CloseHandle(g_out);
    g_out = INVALID_HANDLE_VALUE;
}

// ---- the one way in. On the faulting thread; returns when the record is on disk (or was not ours to write).
static volatile int32_t g_exiting;   // the process is ending: the other threads are gone or going, the writer too

static void record(const char *stage, EXCEPTION_POINTERS *ep, const char *reason, int blame_caller) {
    DWORD tid = GetCurrentThreadId();
    if (!ep || tid == g_writer_tid || (DWORD)plat_load(&g_busy_tid) == tid) return;   // a fault of the recording itself
    for (int waited = 0; plat_cas(&g_busy_tid, (int32_t)tid, 0) != 0; waited += 50) {
        // another thread is being recorded: this one is held until that record is whole, and is not a second story
        if (waited >= 60000) return;
        Sleep(50);
        if (!plat_load(&g_busy_tid)) return;
    }
    for (int i = 0; i < PENDING; i++)
        if (g_pending[i].tid == tid) plat_store(&g_pending[i].live, 0);
    const EXCEPTION_RECORD *er = ep->ExceptionRecord;
    int again = g_last.tid == tid && g_last.code == er->ExceptionCode && g_last.address == er->ExceptionAddress;
    if (again || plat_load(&g_count) >= CRASH_MAX_PER_SESSION) {
        plat_store(&g_busy_tid, 0);
        return;
    }
    g_last.tid = tid, g_last.code = er->ExceptionCode, g_last.address = er->ExceptionAddress;
    int nth = plat_inc(&g_count);

    SYSTEMTIME now;
    GetSystemTime(&now);
    size_t n = fmt(g_job.id, sizeof g_job.id, "%04u%02u%02uT%02u%02u%02uZ-%u", now.wYear, now.wMonth, now.wDay, now.wHour,
                   now.wMinute, now.wSecond, (unsigned)GetCurrentProcessId());
    if (nth > 1) fmt(g_job.id + n, sizeof g_job.id - n, "-%d", nth);
    g_job.stage = stage, g_job.ep = ep, g_job.tid = tid, g_job.reason = reason, g_job.blame_caller = blame_caller;
    void *(*current)(void) = g_cpu_current;
    g_job.cpu = current && er->ExceptionCode != EXCEPTION_STACK_OVERFLOW ? current() : NULL;

    // The writer thread has its own stack (this one may be exhausted) and is not the thread MiniDumpWriteDump must
    // describe. Gone (the process is exiting) or never started: written here.
    if (g_writer && !plat_load(&g_exiting) && WaitForSingleObject(g_writer, 0) == WAIT_TIMEOUT) {
        ResetEvent(g_done_ev);
        SetEvent(g_job_ev);
        WaitForSingleObject(g_done_ev, 90000);
    } else {
        write_record(&g_job);
    }
    memcpy(g_last_id, g_job.id, sizeof g_last_id);
    plat_store(&g_busy_tid, 0);
}

// ---- 1. first chance: never takes the exception
static int runtime_code(uintptr_t pc) {
    MEMORY_BASIC_INFORMATION mi;
    if (!query(pc, &mi) || mi.State != MEM_COMMIT || mi.Type != MEM_IMAGE) return 1;   // no module: a wild jump
    uintptr_t base = (uintptr_t)mi.AllocationBase;
    if (base == g_exe || base == plat_load(&g_game)) return 1;
    if (base == g_self) return 0;
    // mkwgame.dll is loaded after us: recognised by its name in the loader's list, once
    PEB *peb = NtCurrentTeb()->ProcessEnvironmentBlock;
    if (plat_load(&g_game) || !readable((uintptr_t)peb, sizeof *peb) || !readable((uintptr_t)peb->Ldr, sizeof *peb->Ldr))
        return 0;
    LIST_ENTRY *head = &peb->Ldr->InMemoryOrderModuleList;
    int turns = 0;
    for (LIST_ENTRY *e = head->Flink; e && e != head && turns++ < 2048; e = e->Flink) {
        LDR_DATA_TABLE_ENTRY *m = CONTAINING_RECORD(e, LDR_DATA_TABLE_ENTRY, InMemoryOrderLinks);
        if (!readable((uintptr_t)m, sizeof *m)) break;
        if ((uintptr_t)m->DllBase != base) continue;
        static const wchar_t kGame[] = L"mkwgame.dll";
        size_t len = m->FullDllName.Length / sizeof(wchar_t), want = sizeof kGame / sizeof kGame[0] - 1;
        const wchar_t *full = m->FullDllName.Buffer;
        if (len < want || !full || !readable((uintptr_t)full, len * sizeof(wchar_t))) return 0;
        for (size_t i = 0; i < want; i++)
            if ((full[len - want + i] | 0x20) != kGame[i]) return 0;
        if (len > want && full[len - want - 1] != L'\\' && full[len - want - 1] != L'/') return 0;
        plat_store(&g_game, base);
        return 1;
    }
    return 0;
}

// An access violation the runtime's fault handler may still repair: reserved memory it commits on first touch, or a
// committed page (a frozen region's write, a watch). Free memory and kernel addresses nobody can make good.
static int repairable(const EXCEPTION_RECORD *er) {
    MEMORY_BASIC_INFORMATION mi;
    if (er->NumberParameters < 2 || er->ExceptionInformation[0] > 1) return 0;
    return query((uintptr_t)er->ExceptionInformation[1], &mi) && mi.State != MEM_FREE;
}

static void remember(const EXCEPTION_POINTERS *ep) {
    DWORD tid = GetCurrentThreadId();
    Pending *p = NULL;
    for (int i = 0; i < PENDING && !p; i++)
        if (g_pending[i].tid == tid) p = &g_pending[i];   // a thread's later fault replaces its earlier one
    if (!p) p = &g_pending[plat_inc(&g_pending_next) % PENDING];
    plat_store(&p->live, 0);
    p->tid = tid;
    p->ms = plat_ms();
    p->er = *ep->ExceptionRecord;
    p->ctx = *ep->ContextRecord;
    plat_store(&p->live, 1);
}

static LONG CALLBACK first_chance(EXCEPTION_POINTERS *ep) {
    const EXCEPTION_RECORD *er = ep->ExceptionRecord;
    DWORD code = er->ExceptionCode;
    if (!fatal_code(code)) return EXCEPTION_CONTINUE_SEARCH;
    if (code == MKWMP_CRASH_EXCEPTION) {
        record("first-chance", ep, er->NumberParameters ? (const char *)er->ExceptionInformation[0] : NULL, 1);
    } else if (code == CODE_HEAP_CORRUPTION || code == CODE_STACK_BUFFER_OVERRUN || code == CODE_FAIL_FAST) {
        record("first-chance", ep, NULL, 0);   // nothing resumes after these, and the last-chance filter is skipped
    } else if (runtime_code((uintptr_t)ep->ContextRecord->Rip) &&
               (code != EXCEPTION_ACCESS_VIOLATION || plat_load(&g_before_runtime) || !repairable(er))) {
        record("first-chance", ep, NULL, 0);   // the runtime's reporter ends the process for this one, next
    } else {
        remember(ep);
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

// ---- 2. last chance
static LONG WINAPI unhandled(EXCEPTION_POINTERS *ep) {
    DWORD tid = GetCurrentThreadId();
    if ((DWORD)plat_load(&g_chain_tid) == tid) return EXCEPTION_CONTINUE_SEARCH;   // came back through a chain
    if (ep && ep->ExceptionRecord && ep->ContextRecord) record("unhandled", ep, NULL, 0);
    LPTOP_LEVEL_EXCEPTION_FILTER prev = g_prev_filter;
    if (!prev || prev == unhandled) return EXCEPTION_CONTINUE_SEARCH;
    plat_store(&g_chain_tid, (int32_t)tid);
    LONG r = prev(ep);
    plat_store(&g_chain_tid, 0);
    return r;
}

// ---- 3. process exit with a fault still open on this thread: the runtime's reporter called ExitProcess
static int still_open(const Pending *p) {
    if (p->er.ExceptionCode != EXCEPTION_ACCESS_VIOLATION || p->er.NumberParameters < 2) return 1;
    uintptr_t target = (uintptr_t)p->er.ExceptionInformation[1];
    return p->er.ExceptionInformation[0] == 0 ? !readable(target, 1) : p->er.ExceptionInformation[0] == 1 ? !writable(target) : 1;
}

__attribute__((destructor)) static void at_unload(void) {
    if (!plat_load(&g_installed)) return;
    // ExitProcess ends the other threads before it unloads the libraries, but a thread object may not say so yet:
    // from here on nothing is handed to the writer
    plat_store(&g_exiting, 1);
    DWORD tid = GetCurrentThreadId();
    uint64_t now = plat_ms();
    for (int i = 0; i < PENDING; i++) {
        Pending *p = &g_pending[i];
        // the fault's frame is above this one on the same stack: the handler that ended the process ran below it
        if (!plat_load(&p->live) || p->tid != tid || now - p->ms > 60000 || (uintptr_t)&now >= (uintptr_t)p->ctx.Rsp ||
            !still_open(p))
            continue;
        EXCEPTION_POINTERS ep = {&p->er, &p->ctx};
        record("exit", &ep, NULL, 0);
        break;
    }
}

// ---- 4. the C runtime's ways out
static void synthetic(const char *stage, DWORD code, const char *reason) {
    static CONTEXT ctx;   // one at a time is enough: the second caller's record would be refused anyway
    static EXCEPTION_RECORD er;
    static volatile int32_t used;
    if (plat_xchg(&used, 1)) return;
    RtlCaptureContext(&ctx);
    memset(&er, 0, sizeof er);
    er.ExceptionCode = code;
    er.ExceptionAddress = (PVOID)(uintptr_t)ctx.Rip;
    EXCEPTION_POINTERS ep = {&er, &ctx};
    record(stage, &ep, reason, 1);
    plat_store(&used, 0);
}

static void on_abort(int sig) {
    synthetic("abort", CODE_FATAL_APP_EXIT, "abort() was called");
    void (*prev)(int) = g_prev_abort;
    if (prev && prev != SIG_DFL && prev != SIG_IGN && prev != SIG_ERR && prev != on_abort) prev(sig);
}

static void on_invalid_parameter(const wchar_t *expr, const wchar_t *fn, const wchar_t *file, unsigned line,
                                 uintptr_t reserved) {
    DWORD tid = GetCurrentThreadId();
    if ((DWORD)plat_load(&g_chain_tid) == tid) return;
    synthetic("invalid-parameter", CODE_INVALID_CRT_PARAMETER, "the C runtime refused a call's argument");
    _invalid_parameter_handler prev = g_prev_invalid;
    if (!prev || prev == on_invalid_parameter) return;
    plat_store(&g_chain_tid, (int32_t)tid);
    prev(expr, fn, file, line, reserved);
    plat_store(&g_chain_tid, 0);
}

// Ours stay on top: mkw.exe's start-up and the runtime set their own after a DllMain-time install.
static void handlers_assert(void) {
    LPTOP_LEVEL_EXCEPTION_FILTER filter = SetUnhandledExceptionFilter(unhandled);
    if (filter != unhandled) g_prev_filter = filter;
    _invalid_parameter_handler invalid = _set_invalid_parameter_handler(on_invalid_parameter);
    if (invalid != on_invalid_parameter) g_prev_invalid = invalid;
}

// ---- the writer thread: the slow start-up work, then records
static void facts_os(void) {
    typedef LONG (WINAPI *GetVersionFn)(OSVERSIONINFOW *);
    typedef const char *(__cdecl *WineVersionFn)(void);
    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    GetVersionFn get = ntdll ? (GetVersionFn)(void (*)(void))GetProcAddress(ntdll, "RtlGetVersion") : NULL;
    WineVersionFn wine = ntdll ? (WineVersionFn)(void (*)(void))GetProcAddress(ntdll, "wine_get_version") : NULL;
    OSVERSIONINFOW v = {.dwOSVersionInfoSize = sizeof v};
    if (!get || get(&v)) return;
    size_t n = fmt(g_os, sizeof g_os, "Windows %u.%u.%u", (unsigned)v.dwMajorVersion, (unsigned)v.dwMinorVersion,
                   (unsigned)v.dwBuildNumber);
    if (wine) fmt(g_os + n, sizeof g_os - n, " (Wine %s)", wine());
}

static void narrow(const wchar_t *w, char *o, size_t cap) {
    size_t k = 0;
    for (; *w && k < cap - 1; w++) o[k++] = *w >= 0x20 && *w < 0x7f ? (char)*w : '?';
    o[k] = 0;
}

static void facts_gpu(void) {
    size_t at = 0;
    wchar_t seen[4][128];
    int nseen = 0;
    for (DWORD i = 0; i < 16 && nseen < 4; i++) {
        DISPLAY_DEVICEW d = {.cb = sizeof d};
        if (!EnumDisplayDevicesW(NULL, i, &d, 0)) break;
        if (!(d.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP)) continue;
        int dup = 0;
        for (int k = 0; k < nseen; k++) dup |= !wcscmp(seen[k], d.DeviceString);
        if (dup) continue;
        wcscpy(seen[nseen++], d.DeviceString);
        char name[128], version[64] = "?";
        narrow(d.DeviceString, name, sizeof name);
        // DeviceKey: \Registry\Machine\System\CurrentControlSet\Control\Video\{...}\0000, where DriverVersion is
        static const wchar_t kRoot[] = L"\\Registry\\Machine\\";
        size_t root = sizeof kRoot / sizeof kRoot[0] - 1;
        HKEY key;
        if (!_wcsnicmp(d.DeviceKey, kRoot, root) &&
            RegOpenKeyExW(HKEY_LOCAL_MACHINE, d.DeviceKey + root, 0, KEY_QUERY_VALUE, &key) == ERROR_SUCCESS) {
            wchar_t value[64];
            DWORD size = sizeof value - sizeof value[0], type = 0;
            if (RegQueryValueExW(key, L"DriverVersion", NULL, &type, (BYTE *)value, &size) == ERROR_SUCCESS && type == REG_SZ) {
                value[size / sizeof value[0]] = 0;
                narrow(value, version, sizeof version);
            }
            RegCloseKey(key);
        }
        at += fmt(g_gpu + at, sizeof g_gpu - at, "%s%s (driver %s)", at ? "; " : "", name, version);
        if (at >= sizeof g_gpu - 1) break;
    }
}

// Over the cap: every record but the newest CRASH_LOG_KEEP_FULL keeps its id, summary, time and end lines only.
static void log_compact(void) {
    wchar_t path[MAX_PATH], tmp[MAX_PATH];
    path_of(path, "mkwmp-crash.log");
    path_of(tmp, "mkwmp-crash.log.tmp");
    HANDLE in = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (in == INVALID_HANDLE_VALUE) return;
    LARGE_INTEGER size = {0};
    GetFileSizeEx(in, &size);
    char *text = size.QuadPart > CRASH_LOG_CAP && size.QuadPart < (64 << 20) ? malloc((size_t)size.QuadPart + 1) : NULL;
    DWORD got = 0;
    if (text) ReadFile(in, text, (DWORD)size.QuadPart, &got, NULL);
    CloseHandle(in);
    if (!text) return;
    text[got] = 0;
    static const char kStart[] = "=== MKWMP CRASH ", kEnd[] = "=== END ", kSummary[] = "summary:", kTime[] = "time_utc:";
    int records = 0;
    for (const char *p = text; (p = strstr(p, kStart)) != NULL; p += sizeof kStart - 1) records += p == text || p[-1] == '\n';
    HANDLE o = CreateFileW(tmp, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (o == INVALID_HANDLE_VALUE) { free(text); return; }
    int index = 0, inside = 0;
    for (char *line = text; *line;) {
        char *end = strchr(line, '\n');
        size_t len = end ? (size_t)(end - line) + 1 : strlen(line);
        int start = !strncmp(line, kStart, sizeof kStart - 1), stop = !strncmp(line, kEnd, sizeof kEnd - 1);
        if (start) index++, inside = 1;
        int old = inside && index <= records - CRASH_LOG_KEEP_FULL;
        if (!old || start || stop || !strncmp(line, kSummary, sizeof kSummary - 1) || !strncmp(line, kTime, sizeof kTime - 1)) {
            DWORD wrote;
            WriteFile(o, line, (DWORD)len, &wrote, NULL);
        }
        if (stop) inside = 0;
        line += len;
    }
    CloseHandle(o);
    free(text);
    MoveFileExW(tmp, path, MOVEFILE_REPLACE_EXISTING);
}

static DWORD WINAPI writer_main(LPVOID unused) {
    (void)unused;
    HMODULE self;   // never unloaded: the handlers and this thread live in it
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, (LPCWSTR)(void *)writer_main, &self);
    handlers_assert();
    facts_os();
    facts_gpu();
    // dbghelp (and dbgcore, which it forwards MiniDumpWriteDump to) loaded now: nothing may be loaded at a fault
    HMODULE help = LoadLibraryExW(L"dbghelp.dll", NULL, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!help) help = LoadLibraryW(L"dbghelp.dll");
    LoadLibraryExW(L"dbgcore.dll", NULL, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (help) g_minidump = (MiniDumpFn)(void (*)(void))GetProcAddress(help, "MiniDumpWriteDump");
    log_compact();
    dumps_prune(CRASH_MAX_DUMPS);
    plat_store(&g_ready, 1);
    for (;;) {
        if (WaitForSingleObject(g_job_ev, 5000) == WAIT_OBJECT_0) {
            write_record(&g_job);
            SetEvent(g_done_ev);
        } else {
            handlers_assert();
        }
    }
    return 0;
}

void crash_install(const char *dir, int before_runtime) {
    if (plat_xchg(&g_installed, 1)) return;
    plat_store(&g_before_runtime, before_runtime ? 1 : 0);
    uint64_t log_start = 0;
    log_crash_file(&log_start);
    g_start_ms = log_start ? log_start : plat_ms();   // the session log's clock, so both files' times line up
    g_exe = (uintptr_t)GetModuleHandleW(NULL);
    MEMORY_BASIC_INFORMATION mi;
    if (query((uintptr_t)(void *)crash_install, &mi)) g_self = (uintptr_t)mi.AllocationBase;

    DWORD n = 0;
    if (dir && *dir) {
        n = (DWORD)MultiByteToWideChar(CP_UTF8, 0, dir, -1, g_dir, MAX_PATH - 2);
        n = n ? n - 1 : 0;
    } else {
        n = GetModuleFileNameW(NULL, g_dir, MAX_PATH - 2);
        while (n && g_dir[n - 1] != L'\\' && g_dir[n - 1] != L'/') n--;
    }
    if (n && g_dir[n - 1] != L'\\' && g_dir[n - 1] != L'/') g_dir[n++] = L'\\';
    g_dir[n] = 0;

    crumb_state("mkwmp", "%s", MKWMP_VERSION);
    g_job_ev = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_done_ev = CreateEventW(NULL, TRUE, FALSE, NULL);
    AddVectoredExceptionHandler(1, first_chance);
    g_prev_filter = SetUnhandledExceptionFilter(unhandled);
    g_prev_invalid = _set_invalid_parameter_handler(on_invalid_parameter);
    g_prev_abort = signal(SIGABRT, on_abort);
    if (g_job_ev && g_done_ev) g_writer = CreateThread(NULL, 0, writer_main, NULL, 0, &g_writer_tid);
}

void crash_set_guest_cpu(void *(*current)(void)) { g_cpu_current = current; }
const char *crash_last_id(void) { return g_last_id; }
int crash_ready(void) { return plat_load(&g_ready); }

#endif
