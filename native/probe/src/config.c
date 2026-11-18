#include "config.h"

#include "plat.h"
#include <stdlib.h>
#include <string.h>

static void defaults(MkwmpConfig *c) {
    memset(c, 0, sizeof *c);
    strcpy(c->host, "127.0.0.1");
    c->port = 8900;
    c->slot = 1;
    c->voice_port = 8907;
    strcpy(c->lobby_ca, "mkwmp-lobby-ca.pem");
}

// Copy a trimmed, unquoted value into dst (size n, always NUL-terminated).
static void store_str(char *dst, size_t n, const char *s, const char *end) {
    while (s < end && (*s == ' ' || *s == '\t')) s++;
    while (end > s && (end[-1] == ' ' || end[-1] == '\t')) end--;
    if (end - s >= 2 && *s == '"' && end[-1] == '"') { s++; end--; }
    size_t len = (size_t)(end - s);
    if (len >= n) len = n - 1;
    memcpy(dst, s, len);
    dst[len] = 0;
}

void mkwmp_config_parse(const char *text, MkwmpConfig *out) {
    defaults(out);
    const char *p = text;
    while (*p) {
        const char *line = p;
        while (*p && *p != '\n') p++;
        const char *eol = p;
        if (*p) p++;
        while (line < eol && (*line == ' ' || *line == '\t')) line++;
        if (line >= eol || *line == '#' || *line == '[') continue;  // blank/comment/section
        const char *eq = line;
        while (eq < eol && *eq != '=') eq++;
        if (eq >= eol) continue;
        const char *key_end = eq;
        while (key_end > line && (key_end[-1] == ' ' || key_end[-1] == '\t')) key_end--;
        size_t klen = (size_t)(key_end - line);
        const char *val = eq + 1;
        if (klen == 6 && !strncmp(line, "server", 6)) {
            char buf[128];
            store_str(buf, sizeof buf, val, eol);
            char *colon = strrchr(buf, ':');
            if (colon) {
                *colon = 0;
                out->port = atoi(colon + 1);
            }
            strncpy(out->host, buf, sizeof out->host - 1);
            out->host[sizeof out->host - 1] = 0;
        } else if (klen == 4 && !strncmp(line, "name", 4)) {
            store_str(out->name, sizeof out->name, val, eol);
        } else if (klen == 6 && !strncmp(line, "ticket", 6)) {
            store_str(out->ticket, sizeof out->ticket, val, eol);
        } else if (klen == 8 && !strncmp(line, "launcher", 8)) {
            char buf[96];
            store_str(buf, sizeof buf, val, eol);
            char *colon = strrchr(buf, ':');
            if (colon) {
                *colon = 0;
                out->launcher_port = atoi(colon + 1);
                strncpy(out->launcher_host, buf, sizeof out->launcher_host - 1);
                out->launcher_host[sizeof out->launcher_host - 1] = 0;
            }
        } else if (klen == 14 && !strncmp(line, "launcher_token", 14)) {
            store_str(out->launcher_token, sizeof out->launcher_token, val, eol);
        } else if (klen == 8 && !strncmp(line, "lobby_ca", 8)) {
            store_str(out->lobby_ca, sizeof out->lobby_ca, val, eol);
        } else if (klen == 10 && !strncmp(line, "voice_port", 10)) {
            char buf[16];
            store_str(buf, sizeof buf, val, eol);
            if (buf[0]) out->voice_port = atoi(buf);
        } else if (klen == 4 && !strncmp(line, "slot", 4)) {
            char buf[16];
            store_str(buf, sizeof buf, val, eol);
            if (buf[0]) out->slot = atoi(buf);
        }
    }
    out->present = 1;
}

static MkwmpConfig cfg;
static int loaded;

const MkwmpConfig *mkwmp_config_reload(void) {
    loaded = 0;
    return mkwmp_config();
}

const MkwmpConfig *mkwmp_config(void) {
    if (loaded) return &cfg;
    loaded = 1;
    defaults(&cfg);

    char path[1024];
    if (!plat_home_path(path, sizeof path, "mkwmp.toml")) return &cfg;
    char buf[4096];
    long got = plat_read_file(path, buf, sizeof buf - 1);
    if (got < 0) return &cfg;   // absent -> present=0, stock launch
    buf[got] = 0;
    mkwmp_config_parse(buf, &cfg);
    return &cfg;
}
