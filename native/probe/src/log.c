#include "log.h"

#include "plat.h"
#include <stdarg.h>
#include <stdio.h>
#include <string.h>

static PlatFile *g_fh;
static PlatLock g_cs = PLAT_LOCK_INIT;
static int g_ready;
static uint64_t g_start_ms;

static void write_raw(const char *s, size_t n) {
    if (!g_fh || !n) return;
    plat_file_write(g_fh, s, n);
}

void log_init(void) {
    if (g_ready) return;
    g_start_ms = plat_ms();
    char path[1024];
    if (plat_home_path(path, sizeof path, "mkwmp.log")) {
        int in_use;
        g_fh = plat_log_create(path, &in_use);   // truncated: each session starts a fresh log
        // mkw-recomp 0.1.12+: started without --play, mkw.exe is first the start screen, which stays open and holds
        // mkwmp.log while it starts the game as a second mkw.exe: that one writes mkwmp-game.log instead.
        if (!g_fh && in_use && plat_home_path(path, sizeof path, "mkwmp-game.log")) g_fh = plat_log_create(path, &in_use);
    }
    g_ready = 1;
    mkwmp_log("=== mkwmp session log ===");
}

// All output funnels here: "[   1234ms] <line>\r\n", written under the lock so lines from any
// thread/fiber stay intact and complete. No flush per line: WriteFile hands the bytes to the OS,
// which keeps them if the game crashes; a FlushFileBuffers (fsync) per line on the game threads
// cost the Steam Deck (Proton, SD card) a large share of its frame rate online (2026-11-15).
static void emit(const char *line, size_t len) {
    if (!g_ready) return;
    char stamp[24];
    int sn = snprintf(stamp, sizeof stamp, "[%8llums] ",
                      (unsigned long long)(plat_ms() - g_start_ms));
    plat_lock(&g_cs);
    write_raw(stamp, sn < 0 ? 0 : (size_t)sn);
    write_raw(line, len);
    write_raw("\r\n", 2);
    plat_unlock(&g_cs);
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
