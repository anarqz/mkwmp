#include "modmode.h"

#include "plat.h"

#include <stdio.h>
#include <string.h>

static int lower(int c) { return c >= 'A' && c <= 'Z' ? c + 32 : c; }
static int upper(int c) { return c >= 'a' && c <= 'z' ? c - 32 : c; }
static int blank(int c) { return c == ' ' || c == '\t' || c == '\r'; }

int modmode_list_has(const char *list, const char *mod) {
    if (!list || !mod || !*mod) return 0;
    size_t want = strlen(mod);
    for (const char *s = list; *s;) {
        const char *end = strchr(s, ','), *next = end ? end + 1 : s + strlen(s);
        if (!end) end = next;
        while (s < end && blank(*s)) s++;
        while (end > s && blank(end[-1])) end--;
        if ((size_t)(end - s) == want) {
            size_t i = 0;
            while (i < want && lower(s[i]) == lower(mod[i])) i++;
            if (i == want) return 1;
        }
        s = next;
    }
    return 0;
}

int modmode_ini_value(const char *ini, const char *key, char *out, size_t cap) {
    int found = 0;
    if (!ini || !key || !out || !cap) return 0;
    if (!strncmp(ini, "\xEF\xBB\xBF", 3)) ini += 3;   // editors add a BOM
    size_t klen = strlen(key);
    for (const char *p = ini; *p;) {
        const char *line = p, *eol = strchr(p, '\n');
        if (!eol) eol = p + strlen(p);
        p = *eol ? eol + 1 : eol;
        while (line < eol && blank(*line)) line++;
        while (eol > line && blank(eol[-1])) eol--;
        if (line == eol || *line == '#' || *line == ';' || *line == '[') continue;
        const char *eq = memchr(line, '=', (size_t)(eol - line));
        if (!eq) continue;
        const char *kend = eq, *k = line;
        while (kend > k && blank(kend[-1])) kend--;
        if (kend - k > 4 && upper(k[0]) == 'M' && upper(k[1]) == 'K' && upper(k[2]) == 'W' && k[3] == '_') k += 4;
        if ((size_t)(kend - k) != klen) continue;
        size_t i = 0;
        while (i < klen && upper(k[i]) == key[i]) i++;
        if (i != klen) continue;
        const char *v = eq + 1, *vend = eol;
        while (v < vend && blank(*v)) v++;
        if (vend - v >= 2 && *v == '"' && vend[-1] == '"') v++, vend--;
        size_t n = (size_t)(vend - v);
        if (!n || n >= cap) continue;   // "key =" leaves the setting alone
        memcpy(out, v, n);
        out[n] = 0;
        found = 1;
    }
    return found;
}

void modmode_cmdline_mods(const char *cmdline, char *out, size_t cap) {
    if (!out || !cap) return;
    out[0] = 0;
    if (!cmdline) return;
    char arg[256];
    int take = 0;   // the previous argument was --mod
    for (const char *p = cmdline; *p;) {
        while (*p == ' ' || *p == '\t') p++;
        if (!*p) break;
        size_t n = 0;
        int quoted = 0;
        for (; *p && (quoted || (*p != ' ' && *p != '\t')); p++) {
            if (*p == '"') { quoted = !quoted; continue; }
            if (n + 1 < sizeof arg) arg[n++] = *p;
        }
        arg[n] = 0;
        if (take) {
            size_t len = strlen(out);
            if (n && !strchr(arg, ',') && len + n + 2 <= cap) {   // main.c refuses a name with a comma
                if (len) out[len++] = ',';
                memcpy(out + len, arg, n + 1);
            }
            take = 0;
        } else {
            take = !strcmp(arg, "--mod");
        }
    }
}

static int file_exists(const char *path) {
    char b;
    return plat_read_file(path, &b, 1) >= 0;
}

// dir + '/' + name (no doubled separator); 0 when it does not fit.
static int join(char *out, size_t cap, const char *dir, const char *name) {
    size_t n = strlen(dir);
    int sep = n && dir[n - 1] != '/' && dir[n - 1] != '\\';
    return snprintf(out, cap, "%s%s%s", dir, sep ? "/" : "", name) < (int)cap;
}

static long read_text(const char *path, char *buf, size_t cap) {
    long got = plat_read_file(path, buf, cap - 1);
    if (got >= 0) buf[got] = 0;
    return got;
}

int modmode_mod_enabled(const ModmodeInput *in, char *plugin, size_t cap) {
    static char ini[65536];   // one caller, once, in DllMain; a mkw.ini is a few kB
    char path[1024], data_dir[768], value[768], mods[4096] = "";
    if (plugin && cap) plugin[0] = 0;
    if (!in || !in->exe_dir || !*in->exe_dir) return 0;
    if (snprintf(data_dir, sizeof data_dir, "%s", in->exe_dir) >= (int)sizeof data_dir) return 0;

    // the mkw.ini beside mkw.exe: the selection of the data folder, and the settings when none is selected
    int have_ini = join(path, sizeof path, in->exe_dir, "mkw.ini") && read_text(path, ini, sizeof ini) >= 0;
    const char *selected = in->env_game_dir;
    if (!selected && have_ini && modmode_ini_value(ini, "GAME_DIR", value, sizeof value)) selected = value;
    if (selected && *selected && !strchr(selected, '\n')) {
        int absolute = selected[0] == '/' || selected[0] == '\\' || selected[1] == ':';
        char dir[768];
        int fits = absolute ? snprintf(dir, sizeof dir, "%s", selected) < (int)sizeof dir
                            : join(dir, sizeof dir, in->exe_dir, selected);
        if (fits && strcmp(dir, data_dir)) {
            // another folder: its own mkw.ini replaces the settings (cfg_init)
            memcpy(data_dir, dir, strlen(dir) + 1);
            have_ini = join(path, sizeof path, data_dir, "mkw.ini") && read_text(path, ini, sizeof ini) >= 0;
        }
    }

    if (in->env_mods) snprintf(mods, sizeof mods, "%s", in->env_mods);   // the environment wins over mkw.ini
    else if (have_ini) modmode_ini_value(ini, "MODS", mods, sizeof mods);
    char extra[1024];
    modmode_cmdline_mods(in->cmdline, extra, sizeof extra);
    if (!modmode_list_has(mods, MODMODE_MOD) && !modmode_list_has(extra, MODMODE_MOD)) return 0;

    char file[1024];
    if (!join(path, sizeof path, data_dir, "mods/" MODMODE_MOD "/plugin/" MODMODE_PLUGIN)) return 0;
    snprintf(file, sizeof file, "%s", path);
    if (plugin && cap) snprintf(plugin, cap, "%s", file);
    return file_exists(file);
}
