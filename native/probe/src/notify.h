// In-game notifications. The server tells the DLL what to show ("Connected to
// MKWMP", "4th in queue", ...); this is the renderer-agnostic core that holds
// them. A transport (server -> DLL) feeds notify_push; a renderer (overlay)
// consumes notify_drain. Both are built on top of this; see docs/OVERLAY.md.
#ifndef MKWMP_NOTIFY_H
#define MKWMP_NOTIFY_H

#include <stdint.h>

typedef enum {
    NOTIFY_INFO = 0,
    NOTIFY_SUCCESS = 1,
    NOTIFY_WARN = 2,
    NOTIFY_ERROR = 3,
} NotifyLevel;

typedef struct {
    NotifyLevel level;
    uint32_t id;         // monotonic; lets a renderer dedupe/replace
    char text[128];
} Notification;

// Enqueue a notification (thread-safe). Logs it. If the queue is full the
// oldest pending entry is dropped. text is truncated to fit.
void notify_push(NotifyLevel level, const char *text);

// Copy up to `max` pending notifications into `out`, removing them from the
// queue. Returns the count. A renderer calls this each frame/tick.
int notify_drain(Notification *out, int max);

#endif
