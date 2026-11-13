#include "notify.h"
#include "log.h"

#include <windows.h>
#include <string.h>

#define NOTIFY_CAP 16

static CRITICAL_SECTION g_cs;
static int g_ready;
static Notification g_ring[NOTIFY_CAP];
static int g_head, g_count;
static uint32_t g_next_id;

static const char *level_name(NotifyLevel l) {
    switch (l) {
    case NOTIFY_SUCCESS: return "ok";
    case NOTIFY_WARN:    return "warn";
    case NOTIFY_ERROR:   return "error";
    default:             return "info";
    }
}

static void ensure_init(void) {
    // No atomics needed: first touch is from the connection path, not DllMain.
    if (!g_ready) {
        InitializeCriticalSection(&g_cs);
        g_ready = 1;
    }
}

void notify_push(NotifyLevel level, const char *text) {
    ensure_init();
    EnterCriticalSection(&g_cs);
    uint32_t id = ++g_next_id;
    int slot;
    if (g_count == NOTIFY_CAP) {
        g_head = (g_head + 1) % NOTIFY_CAP;   // drop oldest
        slot = (g_head + g_count - 1) % NOTIFY_CAP;
    } else {
        slot = (g_head + g_count) % NOTIFY_CAP;
        g_count++;
    }
    Notification *n = &g_ring[slot];
    n->level = level;
    n->id = id;
    strncpy(n->text, text ? text : "", sizeof n->text - 1);
    n->text[sizeof n->text - 1] = 0;
    LeaveCriticalSection(&g_cs);
    mkwmp_log("notify[%s] %s", level_name(level), n->text);
}

int notify_drain(Notification *out, int max) {
    ensure_init();
    EnterCriticalSection(&g_cs);
    int n = g_count < max ? g_count : max;
    for (int i = 0; i < n; i++) {
        out[i] = g_ring[(g_head + i) % NOTIFY_CAP];
    }
    g_head = (g_head + n) % NOTIFY_CAP;
    g_count -= n;
    LeaveCriticalSection(&g_cs);
    return n;
}

