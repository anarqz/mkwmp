// The mod's status lines ("Connected to MKWMP", "4th in queue", friends coming online, ...). They are logged to
// mkwmp.log and kept in a small queue; nothing of them is shown in the game (the mod draws nothing there: the
// launcher shows the player's status itself). A transport (seat.c, seatd.c) feeds notify_push.
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
    uint32_t id;         // monotonic
    char text[128];
} Notification;

// Enqueue a notification (thread-safe). Logs it. If the queue is full the
// oldest pending entry is dropped. text is truncated to fit.
void notify_push(NotifyLevel level, const char *text);

// Copy up to `max` pending notifications into `out`, removing them from the
// queue. Returns the count.
int notify_drain(Notification *out, int max);

#endif
