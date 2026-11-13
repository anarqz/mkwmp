#include "log.h"

#include <windows.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>

static HANDLE g_fh = INVALID_HANDLE_VALUE;
static CRITICAL_SECTION g_cs;
static int g_ready;
static uint64_t g_start_ms;

static void write_raw(const char *s, size_t n) {
    if (g_fh == INVALID_HANDLE_VALUE || !n) return;
    DWORD wrote;
    WriteFile(g_fh, s, (DWORD)n, &wrote, NULL);
}

void log_init(void) {
    if (g_ready) return;
    InitializeCriticalSection(&g_cs);
    g_start_ms = GetTickCount64();
    wchar_t path[MAX_PATH];
    DWORD n = GetModuleFileNameW(NULL, path, MAX_PATH);
    if (n && n < MAX_PATH) {
        for (DWORD i = n; i > 0; i--) {
            if (path[i - 1] == L'\\' || path[i - 1] == L'/') { path[i] = 0; break; }
        }
        wcsncat(path, L"mkwmp.log", MAX_PATH - wcslen(path) - 1);
        // CREATE_ALWAYS truncates: each session starts a fresh log.
        g_fh = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH, NULL);
    }
    g_ready = 1;
    mkwmp_log("=== mkwmp session log ===");
}

// All output funnels here: "[   1234ms] <line>\r\n", written and flushed under
// the lock so lines from any thread/fiber stay intact and complete.
static void emit(const char *line, size_t len) {
    if (!g_ready) return;
    char stamp[24];
    int sn = snprintf(stamp, sizeof stamp, "[%8llums] ",
                      (unsigned long long)(GetTickCount64() - g_start_ms));
    EnterCriticalSection(&g_cs);
    write_raw(stamp, sn < 0 ? 0 : (size_t)sn);
    write_raw(line, len);
    write_raw("\r\n", 2);
    if (g_fh != INVALID_HANDLE_VALUE) FlushFileBuffers(g_fh);
    LeaveCriticalSection(&g_cs);
}

void mkwmp_log(const char *fmt, ...) {
    char buf[4096];
    va_list ap;
    va_start(ap, fmt);
    int n = vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    emit(buf, n < (int)sizeof buf ? (size_t)n : sizeof buf - 1);
}

void log_line(const char *s) { emit(s, strlen(s)); }

void log_line2(const char *a, const char *b) { mkwmp_log("%s %s", a, b); }

void log_kv(const char *k, uint64_t v) { mkwmp_log("%s 0x%016llx", k, (unsigned long long)v); }

void log_hex(const char *tag, const void *buf, size_t len) {
    if (!g_ready) return;
    static const char H[] = "0123456789abcdef";
    const unsigned char *b = (const unsigned char *)buf;
    size_t cap = len > 2048 ? 2048 : len;
    mkwmp_log("%s len=%zu", tag, len);
    char line[16 * 3 + 1];
    for (size_t off = 0; off < cap; off += 16) {
        int col = 0;
        for (size_t i = 0; i < 16 && off + i < cap; i++) {
            unsigned char c = b[off + i];
            line[col++] = H[c >> 4];
            line[col++] = H[c & 0xf];
            line[col++] = ' ';
        }
        line[col] = 0;
        emit(line, (size_t)col);
    }
    if (cap < len) emit("...(truncated)", 14);
}
