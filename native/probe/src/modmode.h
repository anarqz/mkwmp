// "The mod wins": is MKWMP installed and enabled as a mkw-recomp mod in this game?
//
// MKWMP reaches the game two ways: this probe as the winmm.dll proxy (dllmain.c, COFF backend), and the same probe
// built as a native plugin of the mod "mkwmp" (backend_plugin.c; mkw-recomp 0.1.15 loads mods/<mod>/plugin/*.dll of
// the enabled mods before the game code, runtime/plugin/plugin.c). Both in one process would register every override
// twice and hook the same functions twice. The rule: when the mod is going to load, the winmm.dll probe does nothing
// at all (dllmain.c decides before it registers or patches anything) and the mod is in charge.
//
// "Going to load" is read the way mkw-recomp reads it (runtime/core/base/cfg.c, core/boot/main.c, plugin/plugin.c):
//   - the data folder is the one with mkw.exe, unless MKW_GAME_DIR (environment) or `game_dir` in the mkw.ini beside
//     mkw.exe names another (absolute, or relative to mkw.exe's folder); the settings are then that folder's mkw.ini
//   - the enabled mods are MKW_MODS (environment) or else `mods` of that mkw.ini (written under [launcher]; sections
//     only group), plus every `--mod NAME` of the command line; a comma-separated list
//   - and <data folder>/mods/mkwmp/plugin/mkwmp.dll exists.
// Whether this mkw.exe has a plugin loader at all is the caller's question (a COFF symbol, dllmain.c).
#ifndef MKWMP_MODMODE_H
#define MKWMP_MODMODE_H

#include <stddef.h>

#define MODMODE_MOD "mkwmp"
#define MODMODE_PLUGIN "mkwmp.dll"

// 1 when the comma-separated `list` names `mod` (entries trimmed; case ignored, as the folder's name is on Windows).
int modmode_list_has(const char *list, const char *mod);

// The value of `key` (upper case, no MKW_ prefix: "MODS", "GAME_DIR") in the text of a mkw.ini, as cfg.c reads it:
// "key = value" lines, '#' ';' '[' lines skipped, the key in any case with or without the prefix, the value trimmed
// and unquoted, the last non-empty one wins. 1 with the value in out, 0 when the file does not set it.
int modmode_ini_value(const char *ini, const char *key, char *out, size_t cap);

// Every `--mod NAME` of a Windows command line, comma-joined into out ("" with none).
void modmode_cmdline_mods(const char *cmdline, char *out, size_t cap);

typedef struct {
    const char *exe_dir;        // the folder with mkw.exe, with or without a trailing separator
    const char *env_game_dir;   // MKW_GAME_DIR, NULL when unset
    const char *env_mods;       // MKW_MODS, NULL when unset (set but empty: no mods from mkw.ini either)
    const char *cmdline;        // the process command line, NULL for none
} ModmodeInput;

// 1 when the mod is enabled and its plugin file is there; `plugin` (may be NULL) gets the file's path either way it
// got that far ("" when the mod is not enabled). Reads at most two mkw.ini files and probes one path.
int modmode_mod_enabled(const ModmodeInput *in, char *plugin, size_t cap);

#endif
