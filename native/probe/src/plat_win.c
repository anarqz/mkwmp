// plat.h on Windows.
#include "plat.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct { void (*fn)(void *); void *arg; } Start;

static DWORD WINAPI thread_main(LPVOID p) {
    Start s = *(Start *)p;
    free(p);
    s.fn(s.arg);
    return 0;
}

int plat_thread_start(void (*fn)(void *arg), void *arg) {
    Start *s = malloc(sizeof *s);
    if (!s) return 0;
    s->fn = fn, s->arg = arg;
    HANDLE h = CreateThread(NULL, 0, thread_main, s, 0, NULL);
    if (!h) { free(s); return 0; }
    CloseHandle(h);
    return 1;
}

// A PlatEvent is the Win32 event handle itself.
PlatEvent *plat_event_new(int manual_reset) { return (PlatEvent *)CreateEventW(NULL, manual_reset ? TRUE : FALSE, FALSE, NULL); }
void plat_event_set(PlatEvent *e) { SetEvent((HANDLE)e); }
void plat_event_reset(PlatEvent *e) { ResetEvent((HANDLE)e); }
int plat_event_wait(PlatEvent *e, uint32_t ms) {
    return WaitForSingleObject((HANDLE)e, ms == PLAT_FOREVER ? INFINITE : (DWORD)ms) == WAIT_OBJECT_0;
}

unsigned long plat_thread_id(void) { return GetCurrentThreadId(); }
unsigned long plat_last_error(void) { return GetLastError(); }

// ---- files
static int to_wide(const char *s, wchar_t *out, int cap) {
    return MultiByteToWideChar(CP_UTF8, 0, s, -1, out, cap) > 0;
}

int plat_home_path(char *out, size_t cap, const char *name) {
    wchar_t path[MAX_PATH];
    DWORD n = GetModuleFileNameW(NULL, path, MAX_PATH);
    if (!n || n >= MAX_PATH) return 0;
    for (DWORD i = n; i > 0; i--) {
        if (path[i - 1] == L'\\' || path[i - 1] == L'/') { path[i] = 0; break; }
    }
    int len = WideCharToMultiByte(CP_UTF8, 0, path, -1, out, (int)cap, NULL, NULL);
    if (len <= 0) return 0;
    size_t at = (size_t)len - 1, nl = strlen(name);
    if (at + nl + 1 > cap) return 0;
    memcpy(out + at, name, nl + 1);
    return 1;
}

long plat_read_file(const char *path, void *buf, size_t cap) {
    wchar_t w[MAX_PATH];
    if (!to_wide(path, w, MAX_PATH)) return -1;
    HANDLE fh = CreateFileW(w, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (fh == INVALID_HANDLE_VALUE) return -1;
    DWORD got = 0;
    ReadFile(fh, buf, (DWORD)cap, &got, NULL);
    CloseHandle(fh);
    return (long)got;
}

PlatFile *plat_log_create(const char *path, int *in_use) {
    *in_use = 0;
    wchar_t w[MAX_PATH];
    if (!to_wide(path, w, MAX_PATH)) return NULL;
    // CREATE_ALWAYS truncates: each session starts a fresh log.
    HANDLE fh = CreateFileW(w, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (fh == INVALID_HANDLE_VALUE) {
        *in_use = GetLastError() == ERROR_SHARING_VIOLATION;
        return NULL;
    }
    return (PlatFile *)fh;
}

void plat_file_write(PlatFile *f, const void *p, size_t n) {
    DWORD wrote;
    WriteFile((HANDLE)f, p, (DWORD)n, &wrote, NULL);
}

// ---- environment
int plat_getenv(const char *name, char *buf, size_t cap) {
    if (!GetEnvironmentVariableA(name, NULL, 0)) return 0;
    if (buf && cap) {
        buf[0] = 0;
        GetEnvironmentVariableA(name, buf, (DWORD)cap);
    }
    return 1;
}

void plat_setenv(const char *name, const char *value) {
    SetEnvironmentVariableA(name, value);
    char kv[1024];   // and the CRT's copy, which getenv() reads ("NAME=" removes it)
    if (snprintf(kv, sizeof kv, "%s=%s", name, value ? value : "") < (int)sizeof kv) _putenv(kv);
}

// ---- dynamic libraries
void *plat_dl_open(const char *path) {
    wchar_t w[MAX_PATH];
    if (!to_wide(path, w, MAX_PATH)) return NULL;
    return (void *)LoadLibraryW(w);
}
void *plat_dl_sym(void *lib, const char *name) { return (void *)GetProcAddress((HMODULE)lib, name); }
void plat_dl_close(void *lib) { FreeLibrary((HMODULE)lib); }
void *plat_dl_loaded(const char *base) {
    char name[64];
    if (snprintf(name, sizeof name, "%s.dll", base) >= (int)sizeof name) return NULL;
    return (void *)GetModuleHandleA(name);
}

// ---- memory
size_t plat_mem_readable(const void *p) {
    MEMORY_BASIC_INFORMATION mi;
    if ((uintptr_t)p < 0x10000 || !VirtualQuery(p, &mi, sizeof mi) || mi.State != MEM_COMMIT || (mi.Protect & (PAGE_NOACCESS | PAGE_GUARD)))
        return 0;
    return (size_t)((const char *)mi.BaseAddress + mi.RegionSize - (const char *)p);
}

int plat_mem_write_u64(uint64_t *at, uint64_t v) {
    DWORD prot;
    if (!VirtualProtect(at, 8, PAGE_READWRITE, &prot)) return 0;
    __atomic_store_n(at, v, __ATOMIC_RELEASE);
    VirtualProtect(at, 8, prot, &prot);
    FlushInstructionCache(GetCurrentProcess(), at, 8);
    return 1;
}

// ---- input
int plat_game_focused(void) {
    HWND fg = GetForegroundWindow();
    DWORD pid = 0;
    if (fg) GetWindowThreadProcessId(fg, &pid);
    return pid == GetCurrentProcessId();
}

int plat_key_down(int key) {
    static const int vk[] = {[PLAT_KEY_PAGE_UP] = VK_PRIOR, [PLAT_KEY_PAGE_DOWN] = VK_NEXT, [PLAT_KEY_M] = 'M'};
    if (key < 0 || key >= (int)(sizeof vk / sizeof vk[0])) return 0;
    return (GetAsyncKeyState(vk[key]) & 0x8000) != 0;
}

// ---- process
int plat_under_wine(void) {
    HMODULE nt = GetModuleHandleA("ntdll.dll");
    return nt && GetProcAddress(nt, "wine_get_version");
}
void plat_no_error_dialogs(void) { SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX); }
