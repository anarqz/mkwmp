// "The mod wins" (modmode.h): when is MKWMP enabled as a mkw-recomp mod, so that the winmm.dll probe stays dormant.
// The parsing on strings, the decision on a throw-away game folder under %TEMP%.
//
//   $zig = "C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"
//   & $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o scratch\test_modmode.exe `
//       native\probe\tests\test_modmode.c native\probe\src\modmode.c native\probe\src\plat_win.c
//   scratch\test_modmode.exe
#include "../src/modmode.h"

#include <windows.h>
#include <stdio.h>
#include <string.h>

static int failures;
#define CHECK(c) do { if (!(c)) { printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #c); failures++; } } while (0)

static void test_list(void) {
    CHECK(modmode_list_has("mkwmp", "mkwmp"));
    CHECK(modmode_list_has("hd-textures, mkwmp ,other", "mkwmp"));
    CHECK(modmode_list_has("a,b,mkwmp", "mkwmp"));
    CHECK(modmode_list_has("MKWMP", "mkwmp"));          // the folder mods/MKWMP is mods/mkwmp on Windows
    CHECK(!modmode_list_has("", "mkwmp"));
    CHECK(!modmode_list_has(NULL, "mkwmp"));
    CHECK(!modmode_list_has("mkwmp2,xmkwmp,mkw mp", "mkwmp"));
    CHECK(!modmode_list_has("mkwmp-camera", "mkwmp"));
    CHECK(!modmode_list_has(",,", "mkwmp"));
}

static void test_ini(void) {
    char v[64];
    CHECK(modmode_ini_value("[launcher]\nmods = mkwmp\n", "MODS", v, sizeof v) && !strcmp(v, "mkwmp"));
    CHECK(modmode_ini_value("\xEF\xBB\xBF[launcher]\r\nMKW_MODS = \"a, mkwmp\"\r\n", "MODS", v, sizeof v) && !strcmp(v, "a, mkwmp"));
    CHECK(modmode_ini_value("mods = a\nMods=b\n", "MODS", v, sizeof v) && !strcmp(v, "b"));   // the last one wins
    CHECK(modmode_ini_value("mods = a\nmods =\n", "MODS", v, sizeof v) && !strcmp(v, "a"));   // an empty one does not
    CHECK(!modmode_ini_value("# mods = mkwmp\n; mods = mkwmp\n[mods]\nmods_ack = 1\nmods_played = mkwmp\n", "MODS", v, sizeof v));
    CHECK(!modmode_ini_value("", "MODS", v, sizeof v));
    CHECK(modmode_ini_value("game_dir = \"D:\\MKW data\"\n", "GAME_DIR", v, sizeof v) && !strcmp(v, "D:\\MKW data"));
    CHECK(modmode_ini_value("mods = mkwmp", "MODS", v, sizeof v) && !strcmp(v, "mkwmp"));   // no newline at the end
}

static void test_cmdline(void) {
    char m[64];
    modmode_cmdline_mods("\"C:\\Games\\mkw recomp\\mkw.exe\" --play --mod mkwmp", m, sizeof m);
    CHECK(!strcmp(m, "mkwmp"));
    modmode_cmdline_mods("mkw.exe --mod \"hd pack\" --play --mod mkwmp", m, sizeof m);
    CHECK(!strcmp(m, "hd pack,mkwmp"));
    modmode_cmdline_mods("mkw.exe --play --profile --mod", m, sizeof m);
    CHECK(!strcmp(m, ""));
    modmode_cmdline_mods("mkw.exe --mods mkwmp --romfs --modx", m, sizeof m);
    CHECK(!strcmp(m, ""));
    modmode_cmdline_mods(NULL, m, sizeof m);
    CHECK(!strcmp(m, ""));
}

// ---- the decision, on files ----------------------------------------------------------------------------------------

static char g_root[MAX_PATH];

static void path(char *out, size_t cap, const char *rel) { snprintf(out, cap, "%s\\%s", g_root, rel); }

static void mkdirs(const char *rel) {
    char p[MAX_PATH];
    path(p, sizeof p, rel);
    for (char *s = p + strlen(g_root) + 1;; s++) {
        if (*s == '\\' || *s == '/' || !*s) {
            char c = *s;
            *s = 0;
            CreateDirectoryA(p, NULL);
            *s = c;
            if (!c) break;
        }
    }
}

static void put(const char *rel, const char *text) {
    char p[MAX_PATH];
    path(p, sizeof p, rel);
    FILE *f = fopen(p, "wb");
    CHECK(f != NULL);
    if (!f) return;
    fwrite(text, 1, strlen(text), f);
    fclose(f);
}

static void drop(const char *rel) {
    char p[MAX_PATH];
    path(p, sizeof p, rel);
    DeleteFileA(p);
}

static int enabled(const char *env_game_dir, const char *env_mods, const char *cmdline, char *plugin, size_t cap) {
    char exe_dir[MAX_PATH];
    path(exe_dir, sizeof exe_dir, "game\\");   // as plat_exe_path gives it: with the separator
    ModmodeInput in = {exe_dir, env_game_dir, env_mods, cmdline};
    return modmode_mod_enabled(&in, plugin, cap);
}

static void test_decision(void) {
    char tmp[MAX_PATH], plugin[1024], data[MAX_PATH];
    GetTempPathA(sizeof tmp, tmp);
    snprintf(g_root, sizeof g_root, "%smkwmp-modmode-%lu", tmp, GetCurrentProcessId());
    CreateDirectoryA(g_root, NULL);
    mkdirs("game");
    mkdirs("data\\mods\\mkwmp\\plugin");
    path(data, sizeof data, "data");

    // nothing there: a plain winmm.dll install
    CHECK(!enabled(NULL, NULL, "mkw.exe --play", plugin, sizeof plugin) && !plugin[0]);
    // listed, but no plugin file (the mod is not installed, or it is a mod without native code)
    put("game\\mkw.ini", "[launcher]\nmods = hd, mkwmp\n");
    CHECK(!enabled(NULL, NULL, "mkw.exe --play", plugin, sizeof plugin));
    CHECK(strstr(plugin, "mods/mkwmp/plugin/mkwmp.dll") != NULL);   // where it looked
    // the file, but not listed (the player turned the mod off on the Mods page)
    mkdirs("game\\mods\\mkwmp\\plugin");
    put("game\\mods\\mkwmp\\plugin\\mkwmp.dll", "MZ");
    put("game\\mkw.ini", "[launcher]\nmods = hd\nmods_played = mkwmp\n");
    CHECK(!enabled(NULL, NULL, "mkw.exe --play", plugin, sizeof plugin) && !plugin[0]);
    // both: the mod wins
    put("game\\mkw.ini", "[screen]\nfps = auto\n[launcher]\nmods = hd, mkwmp\n");
    CHECK(enabled(NULL, NULL, "mkw.exe --play", plugin, sizeof plugin));
    CHECK(strstr(plugin, "game\\mods/mkwmp/plugin/mkwmp.dll") != NULL);
    // another mod's plugin only
    drop("game\\mods\\mkwmp\\plugin\\mkwmp.dll");
    put("game\\mods\\mkwmp\\plugin\\other.dll", "MZ");
    CHECK(!enabled(NULL, NULL, "mkw.exe --play", plugin, sizeof plugin));
    put("game\\mods\\mkwmp\\plugin\\mkwmp.dll", "MZ");

    // MKW_MODS in the environment wins over mkw.ini, both ways
    CHECK(!enabled(NULL, "hd", "mkw.exe", plugin, sizeof plugin));
    CHECK(!enabled(NULL, "", "mkw.exe", plugin, sizeof plugin));
    put("game\\mkw.ini", "[launcher]\nmods = hd\n");
    CHECK(enabled(NULL, "mkwmp", "mkw.exe", plugin, sizeof plugin));
    // --mod mkwmp adds it for one run (how another launcher starts the game with its mod)
    CHECK(!enabled(NULL, NULL, "mkw.exe --play", plugin, sizeof plugin));
    CHECK(enabled(NULL, NULL, "mkw.exe --play --mod mkwmp", plugin, sizeof plugin));
    CHECK(enabled(NULL, "hd", "mkw.exe --mod mkwmp", plugin, sizeof plugin));
    // no mkw.ini at all
    drop("game\\mkw.ini");
    CHECK(!enabled(NULL, NULL, "mkw.exe", plugin, sizeof plugin));
    CHECK(enabled(NULL, NULL, "mkw.exe --mod mkwmp", plugin, sizeof plugin));

    // a data folder chosen in the mkw.ini beside mkw.exe: its mkw.ini and its mods folder count, not the exe's
    char ini[2 * MAX_PATH];
    snprintf(ini, sizeof ini, "[launcher]\nmods = mkwmp\ngame_dir = \"%s\"\n", data);
    put("game\\mkw.ini", ini);
    CHECK(!enabled(NULL, NULL, "mkw.exe", plugin, sizeof plugin) && !plugin[0]);   // data\mkw.ini does not exist
    put("data\\mkw.ini", "[launcher]\nmods = mkwmp\n");
    CHECK(!enabled(NULL, NULL, "mkw.exe", plugin, sizeof plugin));                 // data\mods\...\mkwmp.dll neither
    CHECK(strstr(plugin, "data/mods/mkwmp/plugin/mkwmp.dll") != NULL);
    put("data\\mods\\mkwmp\\plugin\\mkwmp.dll", "MZ");
    CHECK(enabled(NULL, NULL, "mkw.exe", plugin, sizeof plugin));
    put("data\\mkw.ini", "[launcher]\nmods = hd\n");
    CHECK(!enabled(NULL, NULL, "mkw.exe", plugin, sizeof plugin));
    // relative to mkw.exe's folder
    put("game\\mkw.ini", "game_dir = ../data\n");
    put("data\\mkw.ini", "mods = mkwmp\n");
    CHECK(enabled(NULL, NULL, "mkw.exe", plugin, sizeof plugin));
    // MKW_GAME_DIR in the environment wins over game_dir
    put("game\\mkw.ini", "[launcher]\nmods = hd\n");
    CHECK(!enabled(NULL, NULL, "mkw.exe", plugin, sizeof plugin));
    CHECK(enabled(data, NULL, "mkw.exe", plugin, sizeof plugin));

    static const char *const files[] = {"game\\mkw.ini", "data\\mkw.ini", "game\\mods\\mkwmp\\plugin\\mkwmp.dll",
                                        "game\\mods\\mkwmp\\plugin\\other.dll", "data\\mods\\mkwmp\\plugin\\mkwmp.dll"};
    static const char *const dirs[] = {"game\\mods\\mkwmp\\plugin", "game\\mods\\mkwmp", "game\\mods", "game",
                                       "data\\mods\\mkwmp\\plugin", "data\\mods\\mkwmp", "data\\mods", "data"};
    char p[MAX_PATH];
    for (size_t i = 0; i < sizeof files / sizeof *files; i++) drop(files[i]);
    for (size_t i = 0; i < sizeof dirs / sizeof *dirs; i++) { path(p, sizeof p, dirs[i]); RemoveDirectoryA(p); }
    RemoveDirectoryA(g_root);
}

int main(void) {
    test_list();
    test_ini();
    test_cmdline();
    test_decision();
    printf(failures ? "test_modmode: %d FAILED\n" : "test_modmode: PASS\n", failures);
    return failures != 0;
}
