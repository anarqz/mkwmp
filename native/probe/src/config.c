#include "config.h"

#include "plat.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void defaults(MkwmpConfig *c) {
    memset(c, 0, sizeof *c);
    strcpy(c->host, "127.0.0.1");
    c->port = 8900;
    c->slot = 1;
    c->voice_port = 8907;
    strcpy(c->lobby_ca, "mkwmp-lobby-ca.pem");
    strcpy(c->nextendo_ca, "nextendo-ca.pem");
}

// Nextendo has no MKWMP seat, ticket or voice relay: whatever the file says of them does not apply.
static void nextendo_only(MkwmpConfig *c) {
    c->voice_port = 0;
    c->ticket[0] = 0;
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

// The end of a quoted value: just past its closing quote, so a comment after it (as the contract's example of
// `backend` has) is not part of it. Unquoted values end at the line's end, as every other key's.
static const char *quoted_end(const char *s, const char *end) {
    while (s < end && (*s == ' ' || *s == '\t')) s++;
    if (s >= end || *s != '"') return end;
    for (const char *p = s + 1; p < end; p++)
        if (*p == '"') return p + 1;
    return end;
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
        } else if (klen == 12 && !strncmp(line, "launcher_exe", 12)) {
            store_str(out->launcher_exe, sizeof out->launcher_exe, val, eol);
        } else if (klen == 8 && !strncmp(line, "lobby_ca", 8)) {
            store_str(out->lobby_ca, sizeof out->lobby_ca, val, eol);
        } else if (klen == 11 && !strncmp(line, "nextendo_ca", 11)) {
            store_str(out->nextendo_ca, sizeof out->nextendo_ca, val, quoted_end(val, eol));
        } else if (klen == 7 && !strncmp(line, "backend", 7)) {
            char buf[16];
            store_str(buf, sizeof buf, val, quoted_end(val, eol));
            out->backend = !strcmp(buf, "nextendo") ? BACKEND_NEXTENDO : BACKEND_MKWMP;
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
    if (out->backend == BACKEND_NEXTENDO) nextendo_only(out);
    out->present = 1;
}

static MkwmpConfig cfg;
static int loaded;
static int g_backend = -1;   // the backend of the first load (-1: none yet); a re-read keeps it

const MkwmpConfig *mkwmp_config_reload(void) {
    loaded = 0;
    return mkwmp_config();
}

static int g_in_exe_dir;   // mkwmp.toml came from the executable's folder, not home
static int g_direct, g_direct_voice_port;   // mkwmp_config_use_direct; the voice port is 0 until the server says
static char g_direct_name[64];
static int g_ignore_launcher;   // mkwmp_config_ignore_launcher: a leftover mkwmp.toml names a launcher that is not running

int mkwmp_config_path(char *out, size_t cap, const char *name) {
    mkwmp_config();
    return g_in_exe_dir ? plat_exe_path(out, cap, name) : plat_home_path(out, cap, name);
}

const MkwmpConfig *mkwmp_config(void) {
    if (loaded) return &cfg;
    loaded = 1;
    defaults(&cfg);

    char path[1024];
    char buf[4096];
    long got = -1;
    // home first (the plugin: its mod folder), then the game executable's folder, where the MKWMP launcher writes it
    if (plat_home_path(path, sizeof path, "mkwmp.toml")) got = plat_read_file(path, buf, sizeof buf - 1);
    g_in_exe_dir = 0;
    if (got < 0 && plat_exe_path(path, sizeof path, "mkwmp.toml") && (got = plat_read_file(path, buf, sizeof buf - 1)) >= 0)
        g_in_exe_dir = 1;
    if (got >= 0) {
        buf[got] = 0;
        mkwmp_config_parse(buf, &cfg);
    }
    int alone = 0;   // no launcher answers: Nextendo needs one (its session), so the mod alone is MKWMP only
    if (g_ignore_launcher && cfg.launcher_port) {
        cfg.launcher_port = 0;
        cfg.present = 0;   // nothing else of a launcher's file applies without it
        alone = 1;
    }
    if (g_direct && !cfg.launcher_port) alone = 1;
    if (alone) g_backend = cfg.backend = BACKEND_MKWMP;
    if (g_backend < 0) g_backend = cfg.backend;
    else if (cfg.backend != g_backend) {
        cfg.backend = g_backend;
        if (g_backend == BACKEND_NEXTENDO) nextendo_only(&cfg);
    }
    // the plugin's own sign-in (seatd.c): only when no launcher is configured, which stays the one in charge
    if (g_direct && !cfg.launcher_port) {
        cfg.present = 1;
        cfg.direct = 1;
        snprintf(cfg.name, sizeof cfg.name, "%s", g_direct_name);
        cfg.voice_port = g_direct_voice_port;
        g_in_exe_dir = 0;   // the lobby CA seatd.c saves: home
    }
    return &cfg;   // nothing found: present=0, stock launch
}

void mkwmp_config_use_direct(const char *name) {
    snprintf(g_direct_name, sizeof g_direct_name, "%s", name ? name : "");
    g_direct = 1;
    loaded = 0;
}

void mkwmp_config_set_voice_port(int port) {
    g_direct_voice_port = port;
    if (cfg.direct) cfg.voice_port = port;
}

void mkwmp_config_ignore_launcher(void) {
    g_ignore_launcher = 1;
    loaded = 0;
}

int mkwmp_config_has_seat(const MkwmpConfig *c) { return c->launcher_port || c->direct; }

int mkwmp_config_nextendo(void) { return mkwmp_config()->backend == BACKEND_NEXTENDO; }
