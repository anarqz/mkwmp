#include "probe.h"

#include "breadcrumbs.h"
#include "log.h"
#include "plat.h"
#include "seat.h"

#include <stdint.h>
#include <string.h>

int npln_register(void);

#ifndef MKWMP_VERSION
#define MKWMP_VERSION "dev"
#endif

// <home>/mkwmp.env: KEY=VALUE lines (# comments) applied over the process environment before the probe reads its
// settings. Lets a launch whose environment is fixed (e.g. a Steam shortcut on the Deck) be reconfigured by editing
// a file; VALUE empty unsets KEY.
static void apply_env_file(void) {
    char path[1024];
    if (!plat_home_path(path, sizeof path, "mkwmp.env")) return;
    char buf[4096];
    long got = plat_read_file(path, buf, sizeof buf - 1);
    if (got < 0) return;
    buf[got] = 0;
    for (char *line = strtok(buf, "\r\n"); line; line = strtok(NULL, "\r\n")) {
        while (*line == ' ' || *line == '\t') line++;
        char *eq = strchr(line, '=');
        if (!*line || *line == '#' || !eq || eq == line) continue;
        *eq = 0;
        plat_setenv(line, eq[1] ? eq + 1 : NULL);   // Windows: the process environment and the CRT's getenv() copy
        log_line(eq[1] ? "env file: set" : "env file: unset");
        log_line(line);
    }
}

void probe_log_start(void) {
    log_init();
    log_set_event_sink(crumb_text);   // mkwmp_event lines are the crash record's events (crash.h)
    seat_set_room_sink(crumb_room);   // and the room the seat reply names, its "room:" line
    log_line("mkwmp probe " MKWMP_VERSION);
    apply_env_file();
}

int probe_register(void) {
    int n = npln_register();
    if (n >= 0) log_kv("overrides registered", (uint64_t)n);
    return n;
}
