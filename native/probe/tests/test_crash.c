// The crash records (src/crash.h): real faults in child processes, and what each leaves in its folder.
//
// crash.c lives in a DLL in the game (winmm.dll or the mod's mkwmp.dll), and one of its paths is that DLL's unload at
// process exit, so the test builds it as a DLL too (this file with -DCRASH_TEST_DLL) and the test program (this file
// again) loads it in every child. The children stand for mkw.exe: a fault in the test program is "in the runtime".
//
//   $zig = "C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"; $s = "native\probe\src"
//   & $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -shared -DCRASH_TEST_DLL -o scratch\crashtest.dll `
//       native\probe\tests\test_crash.c $s\crash.c $s\breadcrumbs.c $s\log.c $s\plat_win.c
//   & $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o scratch\test_crash.exe `
//       native\probe\tests\test_crash.c
//   scratch\test_crash.exe
//
// Children (test_crash.exe <scenario> <folder>):
//   av         a null write in the program: recorded at first chance (nothing can repair it), with the dump
//   guest      the same with a guest Cpu in a register: the guest pc, lr and frames
//   lib        a fault inside ntdll (not the runtime's code): recorded by the unhandled-exception filter
//   exit       a fault a "runtime reporter" answers with ExitProcess(71): recorded when the DLL unloads
//   repaired   a fault a "runtime fault handler" repairs (commits the page): no record, the program goes on
//   overflow   a stack overflow: recorded by the writer thread
//   abort      abort()
//   terminate  MKWMP_CRASH_EXCEPTION with a reason (mkwmp-voice.dll's std::terminate)
//   idle       nothing: the start-up work only (the size cap of a long mkwmp-crash.log)
#include <windows.h>

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef CRASH_TEST_DLL

#include "../src/breadcrumbs.h"
#include "../src/crash.h"
#include "../src/log.h"
#include "../src/plat.h"

__declspec(dllexport) int crashtest_install(const char *dir, int before_runtime) {
    plat_set_home(dir);
    log_init();
    log_set_event_sink(crumb_text);
    crash_install(dir, before_runtime);
    crumb_state("game", "1.8.0");
    crumb_state("mesh", "created (this player made the mesh)");
    crumb_state("lobby", "127.0.0.1:8900");
    crumb_state("uid", "6f1c2d3e-0000-5000-8000-00000000abcd");
    mkwmp_event("seat: joined as tester");
    for (int i = 0; i < 70; i++) crumb("event %d", i);   // more than the ring holds: the oldest fall out
    mkwmp_event("Pia UDP recv 1 <- 10.0.0.2:5000 (88 bytes)");
    crumb_peer(0x0200000a, 5000), crumb_peer(0x0300000a, 5000), crumb_peer(0x0400000a, 5000);
    for (int i = 0; i < 400 && !crash_ready(); i++) Sleep(25);
    return crash_ready();
}

// The room as the seat reply would tell it: this player (ussid 3) is the first, so the host. The lobby reported it
// 1.5 s ago ("at", ms since 1970): the record's age_ms counts from then.
__declspec(dllexport) void crashtest_room(void) {
    const uint32_t ussids[] = {3, 5, 12};
    FILETIME ft;
    GetSystemTimeAsFileTime(&ft);
    uint64_t now = ((uint64_t)ft.dwHighDateTime << 32 | ft.dwLowDateTime) / 10000 - 11644473600000ull;
    crumb_room("0b4e7a10-1111-4222-8333-444455556666", 3, ussids, 3, now - 1500);
}

#else

static int failures;
#define CHECK(c) do { if (!(c)) { printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #c); failures++; } } while (0)

#define MKWMP_CRASH_EXCEPTION 0xE04D4B57u

// ---- the children
typedef struct { uint64_t x[32], sp, pc; uint8_t pad[48]; } FakeCpu;

__attribute__((noinline)) static void poke(uint64_t at) { *(volatile int *)(uintptr_t)at = 1; }
static void (*volatile g_poke)(uint64_t) = poke;

// As a lifted function: the Cpu stays in a callee-saved register across the call that faults (x5 is 0).
__attribute__((noinline)) static void lifted(FakeCpu *c) {
    for (int i = 0; i < 2; i++) {
        g_poke(c->x[5]);
        c->x[0]++;
    }
}

__attribute__((noinline)) static int deeper(volatile int *n) {
    volatile char pad[4096];
    pad[0] = (char)*n;
    return *n ? deeper(n) + pad[0] : 0;
}

static void *g_reserved;

static LONG CALLBACK fake_fault_handler(EXCEPTION_POINTERS *ep) {   // the runtime's: commits on first touch
    const EXCEPTION_RECORD *er = ep->ExceptionRecord;
    if (er->ExceptionCode != EXCEPTION_ACCESS_VIOLATION || (void *)er->ExceptionInformation[1] != g_reserved)
        return EXCEPTION_CONTINUE_SEARCH;
    return VirtualAlloc(g_reserved, 4096, MEM_COMMIT, PAGE_READWRITE) ? EXCEPTION_CONTINUE_EXECUTION : EXCEPTION_CONTINUE_SEARCH;
}

static LONG CALLBACK fake_reporter(EXCEPTION_POINTERS *ep) {   // the runtime's: logs, then ends the process
    if (ep->ExceptionRecord->ExceptionCode == EXCEPTION_ACCESS_VIOLATION) ExitProcess(71);
    return EXCEPTION_CONTINUE_SEARCH;
}

static int child(const char *scenario, const char *dir) {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
    if (!strcmp(scenario, "repaired")) AddVectoredExceptionHandler(1, fake_fault_handler);   // ours goes ahead of it
    if (!strcmp(scenario, "exit")) AddVectoredExceptionHandler(0, fake_reporter);
    char path[MAX_PATH];
    GetModuleFileNameA(NULL, path, sizeof path);
    char *slash = strrchr(path, '\\');
    snprintf(slash ? slash + 1 : path, 32, "crashtest.dll");
    HMODULE lib = LoadLibraryA(path);
    if (!lib) return 90;
    int (*install)(const char *, int) = (int (*)(const char *, int))(void (*)(void))GetProcAddress(lib, "crashtest_install");
    if (!install || !install(dir, 0)) return 91;

    if (!strcmp(scenario, "av")) {
        *(volatile int *)(uintptr_t)0x10 = 1;
    } else if (!strcmp(scenario, "guest")) {
        uint64_t *mem = VirtualAlloc((void *)(uintptr_t)0x7000000000ull, 0x10000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (!mem) return 92;
        static FakeCpu cpu;
        FakeCpu *volatile where = &cpu;
        // two guest frames at main+0x8000: {next fp, lr}
        mem[0x1000] = 0x7000008020ull, mem[0x1001] = 0x7000005678ull;
        mem[0x1004] = 0, mem[0x1005] = 0x700000abccull;
        cpu.x[29] = 0x7000008000ull, cpu.x[30] = 0x7000004444ull, cpu.sp = 0x7000007ff0ull, cpu.pc = 0x7000001234ull;
        lifted(where);
    } else if (!strcmp(scenario, "lib")) {
        void (*room)(void) = (void (*)(void))GetProcAddress(lib, "crashtest_room");
        if (!room) return 95;
        room();
        void (*move)(void *, const void *, size_t) =
            (void (*)(void *, const void *, size_t))(void (*)(void))GetProcAddress(GetModuleHandleA("ntdll.dll"), "RtlMoveMemory");
        char to[64];
        move(to, (const void *)(uintptr_t)0x20, sizeof to);
    } else if (!strcmp(scenario, "exit") || !strcmp(scenario, "repaired")) {
        g_reserved = VirtualAlloc(NULL, 1 << 16, MEM_RESERVE, PAGE_READWRITE);
        *(volatile int *)g_reserved = 7;
        return *(volatile int *)g_reserved == 7 ? 0 : 93;   // reached only when the page was committed
    } else if (!strcmp(scenario, "overflow")) {
        volatile int n = 1;
        return deeper(&n);
    } else if (!strcmp(scenario, "abort")) {
        abort();
    } else if (!strcmp(scenario, "terminate")) {
        ULONG_PTR args[1] = {(ULONG_PTR) "std::terminate in the test"};
        RaiseException(MKWMP_CRASH_EXCEPTION, EXCEPTION_NONCONTINUABLE, 1, args);
    } else if (!strcmp(scenario, "idle")) {
        return 0;
    }
    return 94;
}

// ---- the parent
static char g_root[MAX_PATH];
static char g_text[4 << 20];

static const char *folder(const char *scenario) {
    static char dir[MAX_PATH];
    snprintf(dir, sizeof dir, "%s\\%s", g_root, scenario);
    CreateDirectoryA(dir, NULL);
    return dir;
}

static DWORD run(const char *scenario) {
    char exe[MAX_PATH], line[3 * MAX_PATH];
    GetModuleFileNameA(NULL, exe, sizeof exe);
    snprintf(line, sizeof line, "\"%s\" %s \"%s\"", exe, scenario, folder(scenario));
    STARTUPINFOA si = {.cb = sizeof si};
    PROCESS_INFORMATION pi;
    if (!CreateProcessA(exe, line, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &si, &pi)) return 0xdead;
    DWORD code = 0xdead;
    if (WaitForSingleObject(pi.hProcess, 120000) == WAIT_OBJECT_0) GetExitCodeProcess(pi.hProcess, &code);
    else TerminateProcess(pi.hProcess, 0xdead);
    CloseHandle(pi.hThread), CloseHandle(pi.hProcess);
    return code;
}

// The file's text ("" when it is not there).
static const char *read_file(const char *scenario, const char *name) {
    char path[MAX_PATH];
    snprintf(path, sizeof path, "%s\\%s", folder(scenario), name);
    g_text[0] = 0;
    FILE *f = fopen(path, "rb");
    if (!f) return g_text;
    size_t n = fread(g_text, 1, sizeof g_text - 1, f);
    fclose(f);
    g_text[n] = 0;
    return g_text;
}

// "<key> <value>" of the record's line that starts with key; "" without one.
static const char *field(const char *record, const char *key) {
    static char value[512];
    value[0] = 0;
    for (const char *p = record; (p = strstr(p, key)) != NULL; p += strlen(key)) {
        if (p != record && p[-1] != '\n') continue;
        p += strlen(key);
        while (*p == ' ') p++;
        size_t n = strcspn(p, "\r\n");
        snprintf(value, sizeof value, "%.*s", (int)n, p);
        break;
    }
    return value;
}

// The dump the record names is there and is a minidump.
static int dump_ok(const char *scenario, const char *record) {
    char name[160], path[MAX_PATH], magic[4] = {0};
    if (sscanf(field(record, "dump:"), "%159s", name) != 1 || strncmp(name, "mkwmp-crash-", 12)) return 0;
    snprintf(path, sizeof path, "%s\\%s", folder(scenario), name);
    FILE *f = fopen(path, "rb");
    if (!f) return 0;
    size_t n = fread(magic, 1, 4, f);
    fclose(f);
    return n == 4 && !memcmp(magic, "MDMP", 4);
}

static int count(const char *text, const char *what) {
    int n = 0;
    for (const char *p = text; (p = strstr(p, what)) != NULL; p += strlen(what)) n++;
    return n;
}

static void test_av(void) {
    DWORD code = run("av");
    CHECK(code == 0xC0000005u);
    char r[sizeof g_text / 64];
    snprintf(r, sizeof r, "%s", read_file("av", "mkwmp-crash.log"));
    CHECK(count(r, "=== MKWMP CRASH ") == 1 && count(r, "=== END ") == 1);   // first chance and last chance: one record
    CHECK(!strcmp(field(r, "exception:"), "0xc0000005 ACCESS_VIOLATION"));
    CHECK(!strcmp(field(r, "stage:"), "first-chance"));
    CHECK(!strcmp(field(r, "access:"), "write 0x0000000000000010"));
    CHECK(strstr(field(r, "address:"), " test_crash.exe+0x") != NULL);
    CHECK(strstr(field(r, "summary:"), "ACCESS_VIOLATION in test_crash.exe+0x") != NULL);
    CHECK(strstr(field(r, "summary:"), "(players=~4, host=?, mesh=created)") != NULL);
    CHECK(strlen(field(r, "time_utc:")) == 24 && field(r, "time_utc:")[23] == 'Z');
    CHECK(strstr(field(r, "os:"), "Windows ") != NULL);
    CHECK(strstr(r, "\n  mesh=created (this player made the mesh)\r\n") != NULL);
    CHECK(strstr(r, "\n  lobby=127.0.0.1:8900\r\n") != NULL && strstr(r, "\n  game=1.8.0\r\n") != NULL);
    CHECK(!strcmp(field(r, "peers:"), "3"));
    CHECK(!strcmp(field(r, "room:"), "lobby=? ussid=? uid=6f1c2d3e-0000-5000-8000-00000000abcd host=? players=~4 ussids=? age_ms=?"));
    CHECK(strstr(r, "module: test_crash.exe base=0x") != NULL && strstr(r, "module: crashtest.dll base=0x") != NULL);
    CHECK(strstr(r, "\nregisters:\r\n  rax=") != NULL);
    CHECK(strstr(r, "\nstack:\r\n  #00 test_crash.exe+0x") != NULL && strstr(r, "\n  #01 ") != NULL);
    // the last 50 events, oldest first: the ring's oldest are gone, the last one is the last line
    CHECK(count(r, "Z t") == 50 && !strstr(r, "seat: joined as tester") && !strstr(r, " event 19\r\n"));
    CHECK(strstr(r, " event 69\r\n") != NULL);
    CHECK(strstr(r, "Pia UDP recv 1 <- 10.0.0.2:5000 (88 bytes)\r\ndump: ") != NULL);
    CHECK(dump_ok("av", r));
    // the session log: its own lines, then the crash's, with the record's id
    char id[64] = "";
    sscanf(strstr(r, "=== MKWMP CRASH ") ? strstr(r, "=== MKWMP CRASH ") + 16 : "", "%63s", id);
    const char *log = read_file("av", "mkwmp.log");
    char want[128];
    snprintf(want, sizeof want, "ms] CRASH %s ACCESS_VIOLATION in test_crash.exe+0x", id);
    CHECK(strstr(log, "ms] seat: joined as tester\r\n") != NULL && strstr(log, want) != NULL);
}

static void test_guest(void) {
    CHECK(run("guest") == 0xC0000005u);
    const char *r = read_file("guest", "mkwmp-crash.log");
    CHECK(strstr(field(r, "guest:"), "pc=main+0x1234 lr=main+0x4444 sp=0x7000007ff0 fp=0x7000008000") != NULL);
    CHECK(strstr(r, "\nguest_stack:\r\n  g#00 main+0x5678\r\n  g#01 main+0xabcc\r\n") != NULL);
}

static void test_lib(void) {
    CHECK(run("lib") == 0xC0000005u);
    const char *r = read_file("lib", "mkwmp-crash.log");
    CHECK(count(r, "=== MKWMP CRASH ") == 1);
    CHECK(!strcmp(field(r, "stage:"), "unhandled"));
    CHECK(strstr(field(r, "address:"), " ntdll.dll+0x") != NULL);
    CHECK(!strcmp(field(r, "access:"), "read 0x0000000000000020"));
    CHECK(strstr(r, "module: ntdll.dll base=0x") != NULL);
    CHECK(strstr(r, " test_crash.exe+0x") != NULL);   // the caller, a frame up
    CHECK(strstr(field(r, "guest:"), "none") != NULL);
    CHECK(strstr(field(r, "summary:"), "(players=3, host=1, mesh=created)") != NULL);
    const char *room = field(r, "room:");
    CHECK(!strncmp(room, "lobby=0b4e7a10-1111-4222-8333-444455556666 ussid=3 uid=6f1c2d3e-0000-5000-8000-00000000abcd host=1 players=3 ussids=3,5,12 age_ms=", 130));
    long long age = strlen(room) > 130 ? atoll(room + 130) : -1;
    CHECK(age >= 1500 && age < 60000);   // from the lobby's "at", 1.5 s before the crash
    CHECK(strstr(r, " room: lobby=0b4e7a10-1111-4222-8333-444455556666 ussid=3 host=1 players=3 ussids=3,5,12\r\n") != NULL);
    CHECK(dump_ok("lib", r));
}

static void test_exit(void) {
    CHECK(run("exit") == 71);
    const char *r = read_file("exit", "mkwmp-crash.log");
    CHECK(count(r, "=== MKWMP CRASH ") == 1);
    CHECK(!strcmp(field(r, "stage:"), "exit"));
    CHECK(!strncmp(field(r, "access:"), "write 0x", 8));
    CHECK(strstr(r, "\nstack:\r\n  #00 test_crash.exe+0x") != NULL);
    CHECK(dump_ok("exit", r));
}

static void test_repaired(void) {
    CHECK(run("repaired") == 0);
    CHECK(!read_file("repaired", "mkwmp-crash.log")[0]);
    CHECK(!strstr(read_file("repaired", "mkwmp.log"), "CRASH"));
}

static void test_overflow(void) {
    CHECK(run("overflow") == 0xC00000FDu);
    const char *r = read_file("overflow", "mkwmp-crash.log");
    CHECK(!strcmp(field(r, "exception:"), "0xc00000fd STACK_OVERFLOW"));
    CHECK(count(r, " test_crash.exe+0x") > 20);   // the recursion, unwound
    CHECK(dump_ok("overflow", r));
}

static void test_abort(void) {
    DWORD code = run("abort");
    CHECK(code == 3 || code == 0xC0000409u);
    const char *r = read_file("abort", "mkwmp-crash.log");
    CHECK(count(r, "=== MKWMP CRASH ") == 1);
    CHECK(!strcmp(field(r, "stage:"), "abort"));
    CHECK(!strcmp(field(r, "exception:"), "0x40000015 ABORT"));
    CHECK(strstr(field(r, "summary:"), "ABORT in test_crash.exe+0x") != NULL);   // who called abort, not the C runtime
    CHECK(!strcmp(field(r, "reason:"), "abort() was called"));
}

static void test_terminate(void) {
    run("terminate");
    const char *r = read_file("terminate", "mkwmp-crash.log");
    CHECK(count(r, "=== MKWMP CRASH ") == 1);
    CHECK(!strcmp(field(r, "exception:"), "0xe04d4b57 TERMINATE"));
    CHECK(!strcmp(field(r, "reason:"), "std::terminate in the test"));
    CHECK(strstr(field(r, "summary:"), "TERMINATE in test_crash.exe+0x") != NULL);
}

// A crash log over the cap: the old records keep four lines each, the newest eight stay whole, none is lost. And at
// most three dumps are kept.
static void test_cap(void) {
    char path[MAX_PATH];
    snprintf(path, sizeof path, "%s\\mkwmp-crash.log", folder("idle"));
    FILE *f = fopen(path, "wb");
    for (int i = 0; i < 12; i++) {
        fprintf(f, "=== MKWMP CRASH old-%02d ===\r\nsummary: ACCESS_VIOLATION in x.dll+0x%d (players=4, mesh=?)\r\n"
                   "time_utc: 2026-11-20T10:00:%02d.000Z\r\nregisters:\r\n", i, i, i);
        for (int k = 0; k < 2000; k++) fprintf(f, "  filler line %d of record %d, to be over the cap\r\n", k, i);
        fprintf(f, "=== END old-%02d ===\r\n", i);
    }
    fclose(f);
    for (int i = 0; i < 5; i++) {
        snprintf(path, sizeof path, "%s\\mkwmp-crash-old-%d.dmp", folder("idle"), i);
        f = fopen(path, "wb");
        fputs("MDMP", f);
        fclose(f);
        Sleep(30);   // distinct write times: the two oldest go
    }
    CHECK(run("idle") == 0);
    const char *r = read_file("idle", "mkwmp-crash.log");
    CHECK(count(r, "=== MKWMP CRASH old-") == 12 && count(r, "=== END old-") == 12 && count(r, "summary: ") == 12);
    CHECK(count(r, "time_utc: ") == 12 && count(r, "registers:") == 8);
    CHECK(strstr(r, "of record 3,") == NULL && strstr(r, "of record 4,") != NULL);
    CHECK(strlen(r) < 1u << 20);
    int dumps = 0;
    for (int i = 0; i < 5; i++) {
        snprintf(path, sizeof path, "%s\\mkwmp-crash-old-%d.dmp", folder("idle"), i);
        int there = GetFileAttributesA(path) != INVALID_FILE_ATTRIBUTES;
        dumps += there;
        CHECK(there == (i >= 2));
    }
    CHECK(dumps == 3);
}

// The test's own folder, when everything passed (two levels: a folder per scenario, files in each).
static void remove_tree(const char *dir) {
    char pattern[MAX_PATH], path[MAX_PATH];
    snprintf(pattern, sizeof pattern, "%s\\*", dir);
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern, &fd);
    if (h != INVALID_HANDLE_VALUE) {
        do {
            if (!strcmp(fd.cFileName, ".") || !strcmp(fd.cFileName, "..")) continue;
            snprintf(path, sizeof path, "%s\\%s", dir, fd.cFileName);
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) remove_tree(path);
            else DeleteFileA(path);
        } while (FindNextFileA(h, &fd));
        FindClose(h);
    }
    RemoveDirectoryA(dir);
}

int main(int argc, char **argv) {
    if (argc == 3) return child(argv[1], argv[2]);
    char tmp[MAX_PATH];
    GetTempPathA(sizeof tmp, tmp);
    snprintf(g_root, sizeof g_root, "%smkwmp-crash-test-%lu", tmp, GetCurrentProcessId());
    CreateDirectoryA(g_root, NULL);
    test_av();
    test_guest();
    test_lib();
    test_exit();
    test_repaired();
    test_overflow();
    test_abort();
    test_terminate();
    test_cap();
    if (failures) {
        printf("%d failure(s); the children's folders are in %s\n", failures, g_root);
    } else {
        remove_tree(g_root);
        printf("OK test_crash\n");
    }
    return failures != 0;
}

#endif
