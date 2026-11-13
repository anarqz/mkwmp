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

#endif
