#include "log.h"

#include <windows.h>
#include <string.h>

static HANDLE g_fh = INVALID_HANDLE_VALUE;
static CRITICAL_SECTION g_cs;
static int g_ready;

static void write_raw(const char *s, size_t n) {
    if (g_fh == INVALID_HANDLE_VALUE) return;
    DWORD wrote;
    WriteFile(g_fh, s, (DWORD)n, &wrote, NULL);
}

void log_init(void) {
    if (g_ready) return;
    InitializeCriticalSection(&g_cs);
    wchar_t path[MAX_PATH];
    DWORD n = GetModuleFileNameW(NULL, path, MAX_PATH);
    if (n && n < MAX_PATH) {
        for (DWORD i = n; i > 0; i--) {
            if (path[i - 1] == L'\\' || path[i - 1] == L'/') { path[i] = 0; break; }
        }
        wcsncat(path, L"mkwmp-probe.log", MAX_PATH - wcslen(path) - 1);
        g_fh = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ, NULL, CREATE_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH, NULL);
    }
    g_ready = 1;
    log_line("=== mkwmp probe log ===");
}

static void put_line(const char *parts[], int nparts) {
    if (!g_ready) return;
    EnterCriticalSection(&g_cs);
    for (int i = 0; i < nparts; i++) {
        if (i) write_raw(" ", 1);
        write_raw(parts[i], strlen(parts[i]));
    }
    write_raw("\r\n", 2);
    if (g_fh != INVALID_HANDLE_VALUE) FlushFileBuffers(g_fh);
    LeaveCriticalSection(&g_cs);
}

void log_line(const char *s) {
    const char *p[1] = {s};
    put_line(p, 1);
}

void log_line2(const char *a, const char *b) {
    const char *p[2] = {a, b};
    put_line(p, 2);
}

static char *hex64(uint64_t v, char *out /* >=19 */) {
    static const char H[] = "0123456789abcdef";
    out[0] = '0'; out[1] = 'x';
    for (int i = 0; i < 16; i++) out[2 + i] = H[(v >> ((15 - i) * 4)) & 0xf];
    out[18] = 0;
    return out;
}

void log_kv(const char *k, uint64_t v) {
    char hb[19];
    hex64(v, hb);
    log_line2(k, hb);
}

void log_hex(const char *tag, const void *buf, size_t len) {
    if (!g_ready) return;
    static const char H[] = "0123456789abcdef";
    const unsigned char *b = (const unsigned char *)buf;
    size_t cap = len > 2048 ? 2048 : len;   // cap per-dump
    EnterCriticalSection(&g_cs);
    char hb[19];
    write_raw(tag, strlen(tag));
    write_raw(" len=", 5);
    write_raw(hex64((uint64_t)len, hb), 18);
    write_raw("\r\n", 2);
    char line[16 * 3 + 2];
    for (size_t off = 0; off < cap; off += 16) {
        int col = 0;
        for (size_t i = 0; i < 16 && off + i < cap; i++) {
            unsigned char c = b[off + i];
            line[col++] = H[c >> 4];
            line[col++] = H[c & 0xf];
            line[col++] = ' ';
        }
        line[col++] = '\r';
        line[col++] = '\n';
        write_raw(line, (size_t)col);
    }
    if (cap < len) write_raw("...(truncated)\r\n", 16);
    if (g_fh != INVALID_HANDLE_VALUE) FlushFileBuffers(g_fh);
    LeaveCriticalSection(&g_cs);
}
