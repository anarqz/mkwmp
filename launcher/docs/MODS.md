# Mods

The launcher's **Mods** page (Home dock) lists the mods of the game in use, turns them on and off, shows the
options a mod declares with the launcher's own rows, and installs mods from GameBanana, a link or a file.
This file is the research it was built from (how mkw-recomp does each of these) and the design.

Everything below about mkw-recomp is from our fork at tag `v0.1.16-mkwmp` (`anarqz/mkw-recomp`); paths are
relative to its root, with line numbers.

## 1. How mkw-recomp does it

### 1.1 A mod: the folder and `mod.json`

A mod is one folder of `<game data>/mods/` (`runtime/ui/launcher/mods.cpp:187`, `list_cb` :235). Names that
start with a dot are the start screen's own (`.merged`, `.downloads`, `.gamebanana`, `.rebuild`) and names of
64 bytes or more are skipped (:236-238); at most 64 mods are listed (`mods.h:15`).

```
mods/Timer/
├── mod.json
├── romfs/          overlays the game's files; romfs/manifest.tsv lists them
├── exefs/          <build id>.ips / .pchtxt: patches of the game's code
└── plugin/         native libraries (.dll, .so) the runtime loads with the mod on
```

`mod.json` is **written by the importer**, not by the mod's author (`tools/mod/import.py:269-279`); a shipped one
only contributes `title`, `switches` and `patches`. Its fields (`tools/mod/README.md:76-83`, read at
`mods.cpp:243-275` by a string scanner over the first 4096 bytes, not a JSON parser):

| Field | Meaning |
|---|---|
| `name` | the folder name: what the enabled list holds |
| `title` | what the row shows |
| `has_romfs`, `romfs_files` | it overlays game files, and how many |
| `has_exefs`, `exefs_builds`, `exefs_match` | it ships code patches, for which game builds, and whether one is this build |
| `has_plugin` | it has a `plugin/` folder (also detected from the folder itself, `mods.cpp:276-280`) |
| `switches` | `{"TIMER": "1"}`: config keys (`MKW_<name>`) set while the mod is on, for patch sets compiled into the game (`mods_apply`, :321) |
| `patches` (`sites`, `spans`), `image` | code patches merged into the generated game code: the game must be **built again** (`m->code`, :258; `mark_rebuild` :55) |
| `warnings` | `"shaders"`, `"graphics"`: files the port treats its own way (`import.py:247-250`) |

There is **no version, author, description, dependency or conflict field**, and no versioning of the format.
MKWMP's own package adds `version` (`native/probe/build_plugin.py:159`), which the game ignores.

`romfs/manifest.tsv` (`import.py:238-257`) has a line per file: `path<TAB>size<TAB>sha256<TAB>original sha256 or -`.
The game checks **all of it at start and dies on a mismatch** (`runtime/sdk/fs/romfs.c:191-254`): a wrong size or
hash, a path the game lacks without `-`, or `-` for a path it has. So a mod's romfs cannot just be copied in.

### 1.2 On and off

- The enabled mods are **`mods = a,b` under `[launcher]` of mkw.ini** (config key `MKW_MODS`; section table
  `runtime/core/base/cfg.c:86`). `set_enabled` (`mods.cpp:342-364`) drops the name from the list and, when
  enabling, **appends** it: *the order is the precedence, the newest mod wins shared files*.
- The game reads the list **only when it starts**: the RomFS overlays in list order, later ones winning
  (`romfs.c:347-370`, :394-405), then each mod's `plugin/*.dll` (`runtime/plugin/plugin.c:386-414`). The
  environment variable `MKW_MODS` overrides the file; `--mod <name>` adds to it for one run (`core/boot/main.c:710-726`).
- `mods_ack = 1`: the player ticked the start screen's "I understand" box under its risk text once
  (`mods_risk_draw`, `mods.cpp:67-79`). Only that screen uses it.
- `mods_played`: the list the last start played. At Play, when it differs from `mods`, the save is copied to
  `<profile>/save-before-mods/<time>/` (the newest ten stay) and `mods_played` takes the list
  (`mods_before_play`, :121-150).
- **Conflicts.** Two mods replacing one file: the later one wins, logged (`romfs.c:394-405`). Two mods changing
  the *same game archive* (`*.sarc.zs`) are merged file by file into `mods/.merged/romfs` by `tools/mod/merge.py`,
  run after every toggle and again at Play (`mods_merge_start` :736, `mods_merge_sync` :928); inner files changed
  by several mods are a picker (`mods/choices.json`, :863). The game reads `.merged` last (`romfs.c:374-380`).
- **Code mods** (exefs, `image`, `patches`) leave `mods/.rebuild`; the start screen builds the game's code
  again before Play (`launcher.cpp:948`).
- All of this at Play happens **in the start screen**. `mkw.exe --play` (what the MKWMP launcher runs) goes
  past it.

### 1.3 A mod's options

- A mod's settings are per player: **`<player folder>/mod-settings/<mod>.ini`**, flat `key=value` lines, keys of
  `[A-Za-z0-9._-]` up to 64, values one line up to 1024, rewritten atomically
  (`runtime/plugin/mod_settings.c:18-34`, :86-131; `mod_settings.h`). A plugin reads and writes them through
  `MkwPluginApi.setting_get` / `setting_set` (plugin API 2, `runtime/plugin/mkw_plugin.h`).
- The options **page is code, not data**: the start screen loads the mod's library and calls its exported
  `mkw_mod_page`, which draws through a small immediate-mode API (`heading`, `note`, `checkbox`, `input`,
  `choice`, `slider`, `button`, lists) that the runtime renders with ImGui
  (`runtime/ui/launcher/mod_page.cpp:47-98`, `runtime/plugin/mod_page_ui.cpp`). There is no manifest schema.
  MKWMP's own page is `native/probe/src/modpage.c` (:599-761) with `cameratab.c` and `voicetab.c`.
- A mod **without a plugin has no options at all** (`UI_MOD_NO_OPTIONS`, `mod_page.cpp:90-93`): a texture or
  RomFS replacement is on or off. An archive that offers choices ships several mods (variants) instead.

### 1.4 Importing

`tools/mod/import.py`, run by the start screen in the package's Python: a `.zip` (Python's zipfile), a `.rar`
or `.7z` (through `bsdtar` / Windows' `tar.exe`, :79-95) or a folder; limits 4 GiB and 50 000 files (:42);
names that are absolute or hold `..` refused (:75, :89). `mod_roots` (:98-107) finds the folders that hold
`romfs/`, `exefs/` or `plugin/` in any letter case, at any depth (also `atmosphere/contents/<title id>/`),
skipping dot folders and `__MACOSX`; several is `error variants` and the page asks. The name is the folder's,
or the archive's when the mod sits at its root (:188-191), cleaned to letters, digits, `-`, `_`, space, 64 at
most (:131-133); a name in use is refused (:225-226). It writes `manifest.tsv` against the game's RomFS (a loose
folder or a `.nup`) and `mod.json`, and the page then enables the mod (`mods_poll`, `mods.cpp:164-174`).

### 1.5 GameBanana

`tools/mod/gamebanana.py` (the page is `runtime/ui/launcher/mods_browse.cpp`):

- **API**: GameBanana's public `https://gamebanana.com/apiv11`, no key. Mario Kart World is **game 22152** (:47).
- **List**: `Mod/Index?_aFilters[Generic_Game]=22152&_nPage=&_nPerpage=20&_sSort=` with
  `Generic_MostDownloaded`, `Generic_Newest`, `Generic_MostLiked` (:51, :304-306). **Search**:
  `Util/Search/Results?_sSearchString=&_idGameRow=22152&_sModelName=Mod` (:308-310).
- **A mod**: `Mod/<id>/ProfilePage` (:204), refused when its `_aGame._idRow` is not 22152. Files are `_aFiles`
  (`_idRow`, `_sFile`, `_nFilesize`, `_sMd5Checksum`, `_sAvResult`, `_sDescription`), pictures
  `_aPreviewMedia._aImages` (`_sBaseUrl` + `_sFile220` / `_sFile530` / `_sFile`), up to six.
- **Download** (:220-258): only a file of that mod, only when `_sAvResult` is `clean`, only from
  `gamebanana.com` (and after the redirect `*.gamebanana.com`), MD5 checked; into `mods/.downloads`, then the
  normal import (`mods_browse.cpp:468-475`). Any archive format the import takes.
- **Manners**: its own User-Agent, answers cached ten minutes, at most 60 requests a minute, 20 s timeouts (:50, :135-166).
- **Mature content**: a row's `_bHasContentRatings` / `_sInitialVisibility`. The setting **`gb_mature`**
  (`obfuscate` default, or `show`; `cfg.c:147`) decides: rated mods keep their titles and get their pictures
  pixelated at decode (`mods_browse.cpp:95-100`, :300-319).
- **No update check**: the id of an installed mod is not remembered anywhere.

Live answers seen on 2026-11-20 confirm the shapes above, and that `_sSort=Generic_LatestUpdated` works too.

## 2. What the launcher does

### 2.1 The capability

`IGameTarget` has, with defaults that serve mkw-recomp (`Targets/IGameTarget.cs`): `SupportsMods`, `Mods`,
`SetModEnabled`, `UninstallMod`, `InstallMod`, `ReadModOptions`, `WriteModOptions`. The data is
`Targets/GameMods.cs` (`GameMod`, `ModSource`, `ModInstallRequest`, `ModInstallResult`, `ModProgress`).
`ILauncher` carries it to the window (`Launcher.Mods.cs`): `Mods` (a `ModsView`), `RefreshModsAsync`,
`SetModEnabledAsync`, `UninstallModAsync`, `InstallModAsync`, `DownloadModAsync` (a link, a GameBanana file),
`ModOptions` / `SetModOptionAsync`, `BrowseModsAsync` / `ModPageAsync` / `ModImageAsync`,
`SetShowMatureModsAsync`, `CheckModUpdatesAsync`.

For mkw-recomp (`Targets/MkwRecomp/RecompMods.cs`, `ModImport.cs`):

| | |
|---|---|
| List | the folders of `mods/` as the start screen lists them, `mod.json` read with a JSON parser, plus `version`, `author`, `description` when the file has them |
| On / off | the same list edit (`Mod.WithMod`, generalised from the MKWMP mod's): appended when enabling, every other mod and line kept, written atomically |
| The game runs | nothing is written: every change is refused with "Close the game to change its mods" |
| A plugin | a mod with `plugin/` is only turned on after the player read that it runs code on this PC and said yes (the core refuses without `acceptCode`); an install leaves it off until then |
| Code mods | exefs, `image`, `patches`, `switches`: listed and **locked** ("manage it on mkw-recomp's own Mods page"), never installed: the launcher does not build the game's code |
| MKWMP's own mod | a locked reference row ("Required for online play · managed by the launcher"): no switch, no options, no uninstall; A leads to Settings › Game, where the integration installs and repairs it |
| Shared archives | after the enabled set changes, the install's own `merge.py` is run when its tools are there; else (or when it fails) the stale `mods/.merged` is removed and the mod turned on last wins. Mods that ship the same game file say so in their page |
| Before PLAY | what the start screen does at Play: the first player's save copied to `profile/save-before-mods/<time>/` when the set of mods changed, `mods_played` updated (`RecompMods.BeforePlay`, from `MkwRecompTarget.Launch`) |
| `MKW_MODS` set | the game follows the environment: the list shows it, locked, and says so |

Not written by the launcher: `mods_ack` (the start screen's own box) and `mods/choices.json`.

### 2.2 Installing

`ModImport.Install` does what `import.py` does, in the launcher's own code:

1. The archive is unpacked into `mods/.mkwmp-staging/` (a dot folder: the game never reads it), never over anything.
   `.zip` with .NET's reader; **`.7z` and `.rar` through the system's `tar.exe`**, as mkw-recomp does (no library
   added; under Wine, where there is none, the message says a .zip always works).
2. Checks: 4 GiB and 50 000 files (declared *and* counted while unpacking); every name must stay inside
   (nothing absolute, no `..`, no drive or stream `:`), must not be one Windows bends (`NUL`, a trailing dot or
   space), must not come twice in another letter case; a link is refused (in a zip, in what tar unpacked, in a
   picked folder). `__MACOSX`, `.DS_Store`, `Thumbs.db` are left out.
3. The mod inside is found by the fork's rule (1.4); several are a question (`Variants`).
4. `RomFS/`, `Plugin/` become `romfs/`, `plugin/`. The id is the fork's clean name, narrowed to ASCII (mkw.ini
   is not Unicode all the way) and 63 bytes; the title keeps the real name.
5. `romfs/manifest.tsv` is written against the game's own RomFS (`GameRomfs`: `MKW_ROMFS`, mkw.ini's `romfs`,
   else `<data>/romfs`; a loose folder, compared in exact letter case, or a `.nup`). A game file that is there
   and cannot be read stops the install: a wrong line would stop the game at its next start. An `.nx` package
   is not read: such an install takes plugin mods only.
6. `mod.json` is written as the importer writes it (so the start screen's page shows the mod), with the mod's
   own `version`, `author`, `description`; `.mkwmp.json` beside it remembers the source (the link, or
   GameBanana's mod and file ids and the file's date).
7. The folder is moved into place. A mod of that name is replaced only when asked (`Replace`); its old folder
   goes to `mods/.mkwmp-backup/<id>/` first and comes back when the new one does not arrive. The player's
   settings of the mod are outside its folder and stay.
8. The mod is turned on (appended), unless it has a plugin and the player has not said yes yet.

Downloads go to `downloads\` beside the launcher (portable: nothing in `%LOCALAPPDATA%`), as `.part` until whole,
https only, 4 GiB at most, and are deleted once the install has its answer.

### 2.3 GameBanana

`Mods/GameBanana.cs`: the same endpoints and manners (User-Agent `mkwmp-launcher/<version>`, ten-minute cache
in memory and in `cache\gamebanana\`, 60 requests a minute, 20 s), sorts Most Downloaded, Newest, Recently
Updated, Most Liked, pages of twenty. Pictures only from `images.gamebanana.com`, 4 MiB at most, kept in the
cache. A file is downloaded only when the scan calls it clean, from `gamebanana.com`, with its size and MD5
checked. The description is shown as plain text.

Mature content follows the game's own `gb_mature` (one choice for both programs; the page's "Mature Content"
writes it): blurred by default, by decoding the picture a dozen pixels wide, so the sharp one is never in memory.

**Updates** (ours, the start screen has none): a mod installed from GameBanana remembers its file; the Mods page
asks each such mod's page once when it opens, and a clean file added after the installed one is "Update available".

### 2.4 A mod's options: `options.json`

mkw-recomp has no declarative options (1.3), so the launcher defines a small one for mods that want their
options in the launcher: **`options.json` beside `mod.json`**, read by `ModOptions.Parse` into the game
settings' own schema types (`GameSetting`: Toggle, Choice, Range, Text, groups, conditions), drawn with the same
rows as Settings › Game settings, and saved to the same `mod-settings/<mod>.ini` the mod's plugin reads with
`setting_get`. The game ignores the file; a mod can ship both this and a `mkw_mod_page`. It is a separate file
and not a key of `mod.json` because the start screen reads `mod.json` with a 4096-byte string scanner.

```json
{
  "schema": 1,
  "note": "Read when the game starts.",
  "options": [
    {"key": "show", "type": "toggle", "label": "Show the timer", "description": "…", "default": true, "group": "Timer"},
    {"key": "corner", "type": "choice", "label": "Corner", "default": "tr",
     "choices": [{"value": "tl", "label": "Top left"}, {"value": "tr", "label": "Top right"}],
     "visible_when": {"key": "show", "values": ["1"]}},
    {"key": "scale", "type": "range", "label": "Size", "min": 50, "max": 200, "step": 10, "unit": "%", "default": 100},
    {"key": "tag", "type": "text", "label": "Name", "max_length": 12, "pattern": "[A-Za-z0-9 ]*", "restart": false}
  ]
}
```

- `key`: the settings key (the host's rule: letters, digits, `.`, `_`, `-`, 64 at most).
- A toggle writes `1` / `0`; a choice one of its `value`s; a range its number; a text one line.
- An option at its default has no line in the file ("Reset" removes it).
- `label`, `description`, `group`, `note` and a choice's `label` are a string, or one per language
  (`{"en": "…", "pt-BR": "…"}`; the launcher's language, then its base language, then `en`).
- `restart: false` drops the "the game reads this only when it starts" line.
- A wrong entry is left out and the others stay; at most 96 options, 256 KiB.
- Only the first player's settings (`profile/`) are edited, and only while the game is closed, as for MKWMP's own.

MKWMP's own mod ships none: its settings are the launcher's pages (Voice & Camera, the overlay).

### 2.5 The window

- **Home dock › Mods** (not a Settings category: it is a place with its own list and store, like Friends; and
  Settings › Game stays about the install). Hidden over a running game.
- **Mods**: the installed mods (A opens one, X switches it), then Get Mods, Install from a Link, Install from a File.
- **A mod**: its switch, problems, an update, its options, what it is, Uninstall.
- **Get Mods**: search (the on-screen keyboard), the order, Mature Content, a grid of cards, pages.
  **A GameBanana mod**: pictures, files with Install, the description, "Open on GameBanana".
- **Install from a Link**: a field (the on-screen keyboard pastes) and Download and Install.
- View models: `ViewModels/ModsViewModel.cs`, `ModBrowserViewModel.cs`; views: `Views/Mods/`.
  `ModInstaller.RunAsync` is the one install flow (progress, which of several, replace, the code question).

Harness: `shot.ps1 out.png --page mods`, `--page mods/browse`, `--page mods/link`, `--page mods --press down,down,a`
(a mod with options), `--page mods/browse --press down,a` (a GameBanana mod). The demo's mods are made up and
have no pictures.

### 2.6 Tests

`tests/Mkwmp.Core.Tests/ModsTests.cs`: the list and `mod.json`, the list edit, on / off and what is refused,
installs (nested folders, letter case, zip-slip and Windows names, links, limits, variants, replace with a
backup, a folder, no game files), `options.json` and the round trip to the ini, the save copy, GameBanana from
recorded answers (list, cache, page, download, checksum, scan, host, pictures), a link with a fake server.

## 3. Limits, on purpose

- No code mods (the game would have to be built again) and no winner picker for shared archive files: both
  stay with mkw-recomp's own Mods page, which reads the same folders and list.
- The save copy before new mods covers the first player (`profile/`).
- `.7z` / `.rar` need Windows' `tar.exe`.
