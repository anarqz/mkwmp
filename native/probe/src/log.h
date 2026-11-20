// Session logger: writes <game dir>\mkwmp.log, fresh each session, every line
// timestamped and flushed (kernel32 only, thread-safe). This is the file players
// attach to bug reports, so everything mkwmp does should go through mkwmp_log.
#ifndef MKWMP_LOG_H
#define MKWMP_LOG_H

#include <stddef.h>
#include <stdint.h>

void log_init(void);
void mkwmp_log(const char *fmt, ...);                // printf-style, one line
void log_line(const char *s);
void log_line2(const char *a, const char *b);        // "a b"
void log_kv(const char *k, uint64_t v);              // "k 0x..."
void log_hex(const char *tag, const void *buf, size_t len);  // tag + hexdump (capped)

// A line that is also an event of the session (join / leave, seat, lobby, peers, friend room, voice): logged as
// mkwmp_log does, and handed to the event sink, which keeps the last ones for the crash record (breadcrumbs.h's
// crumb_text, set by the loader that installs crash.h). Without a sink it is mkwmp_log.
void mkwmp_event(const char *fmt, ...);
void log_set_event_sink(void (*sink)(const char *line));

// crash.c only: the session log's file (PlatFile: the Win32 handle on Windows; NULL when there is none) and the
// plat_ms() its timestamps count from (0 before log_init), to add a last line from a fault without the lock.
struct PlatFile;
struct PlatFile *log_crash_file(uint64_t *start_ms);

#endif
