#include "config.h"

#include <windows.h>
#include <stdlib.h>
#include <string.h>

static void defaults(MkwmpConfig *c) {
    memset(c, 0, sizeof *c);
    strcpy(c->host, "127.0.0.1");
    c->port = 8900;
    c->slot = 1;
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

    wchar_t path[MAX_PATH];
    DWORD n = GetModuleFileNameW(NULL, path, MAX_PATH);
    if (!n || n >= MAX_PATH) return &cfg;
    for (DWORD i = n; i > 0; i--) {
        if (path[i - 1] == L'\\' || path[i - 1] == L'/') { path[i] = 0; break; }
    }
    wcsncat(path, L"mkwmp.toml", MAX_PATH - wcslen(path) - 1);

    HANDLE fh = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING,
                            FILE_ATTRIBUTE_NORMAL, NULL);
    if (fh == INVALID_HANDLE_VALUE) return &cfg;   // absent -> present=0, stock launch
    char buf[4096];
    DWORD got = 0;
    ReadFile(fh, buf, sizeof buf - 1, &got, NULL);
    CloseHandle(fh);
    buf[got] = 0;
    mkwmp_config_parse(buf, &cfg);
    return &cfg;
}
