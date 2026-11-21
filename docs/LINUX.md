# MKWMP on SteamOS and Linux

MKWMP on Linux is the Windows launcher run by Proton, as one Steam shortcut. The launcher installs
mkw-recomp's Windows build in its own folder and starts the game as its child, so the launcher, the
game and the overlay are one Steam app in one Proton prefix. `tools/linux/install-mkwmp.sh` sets all
of it up.

Status, 2026-11-20. Tested on SteamOS (kernel 7.2.7-valve2, an ASUS ROG Xbox Ally X, Desktop Mode)
with Proton 11.0: the installer against copies of that machine's Steam files, the launcher's start,
its first-run pages, its download and install of mkw-recomp, and the game's setup as far as it
gets. **Not working yet: the game's setup under Proton** (section 5, the first item). **Not run: the live edit of Steam's files, a start from
Steam, anything in Game Mode, the game itself.** Section 7 lists what is left.

## 1. For players

In Desktop Mode, in a terminal (Konsole):

```
curl -fsSL https://github.com/anarqz/mkwmp/releases/latest/download/install-mkwmp.sh | bash
```

(until a release carries the script: download `tools/linux/install-mkwmp.sh` and `artwork.json`
into one folder and run `bash install-mkwmp.sh`.)

1. **Close Steam first** (Steam menu > Exit). The script stops and says so if Steam runs: Steam
   keeps its shortcut list in memory and writes it back when it closes, so a change made under it is
   lost. The script never closes Steam itself.
2. The script downloads MKWMP, checks it against the release's signed `SHA256SUMS`, unpacks it into
   `~/Games/MKWMP`, adds the shortcut **MKWMP** with Proton and its artwork, and says what is next.
3. Open Steam. MKWMP is under Non-Steam in the library. The first start takes a few seconds more:
   Proton makes its prefix.
4. In MKWMP: a name, then your own copy of the game. MKWMP downloads mkw-recomp into
   `~/Games/MKWMP/mkw-recomp` and builds the game there from your copy.
5. In a race, hold **View** (Select) for a second to bring MKWMP over the game.

Everything MKWMP writes is in `~/Games/MKWMP`: `client.toml` (settings), `logs/`, `mkw-recomp/` (the
game). The Proton prefix (`steamapps/compatdata/<shortcut id>`) holds nothing of yours.

To update: MKWMP updates itself. To repair the shortcut or move it to another Proton: run the script
again (it changes the same shortcut). To remove: `bash install-mkwmp.sh --uninstall` (the shortcut,
its Proton setting, its artwork, its layout entry); with `--purge` also the folder and the prefix.

## 2. The script

`tools/linux/install-mkwmp.sh`: bash that runs one embedded Python 3 program (standard library only;
SteamOS has Python and no pip). Options:

| Option | What |
|---|---|
| `--dir <folder>` | the install folder (default `~/Games/MKWMP`) |
| `--version <x.y.z>` | that release; default the newest with a Windows zip |
| `--from-zip <file>` | a local `mkwmp-<version>-windows-x64.zip` (a test build): no download, no signature |
| `--proton <name>` | `11`, `10`, `experimental`, `GE-Proton11-1`, a tool's internal name. Default below |
| `--launch-options <text>` | the shortcut's launch options; default none |
| `--steam-root <folder>` | Steam's folder when it is not found |
| `--no-steam` | the files only |
| `--artwork <file>` | another list of links; `--art-only` downloads the artwork again; `--no-art` none |
| `--uninstall`, `--purge`, `--yes` | section 1 |
| `--dry-run` | says what would be done and writes nothing |

What it does, in order; nothing is written before everything that can refuse has been checked:

1. **Steam.** The first of `~/.local/share/Steam`, `~/.steam/steam`, `~/.steam/root`,
   `~/.steam/debian-installation`, Flatpak's `~/.var/app/com.valvesoftware.Steam/...`, snap's, that
   has `userdata/`. With Flatpak Steam it also prints the `flatpak override --filesystem` line the
   sandbox needs to see the install folder (not tested: no Flatpak Steam here).
2. **Steam closed.** A process named `steam` or `steamwebhelper` in `/proc`: exit code 3 and the
   reason. Also refused: root, a launcher running from the install folder.
3. **Proton.** The installed builds are the `Proton*` folders with a `proton` file in every Steam
   library, and `compatibilitytools.d`. Default: the newest released Valve Proton (`Proton 11.0`
   here), then Experimental, then a "(Beta)", Hotfix, custom builds (GE). The name written is
   Steam's internal one: `proton_11`, `proton_experimental`, or what a custom build's
   `compatibilitytool.vdf` declares. None installed: `proton_experimental` is written and the script
   says to install it.
4. **Download.** GitHub's release list; `SHA256SUMS` and `SHA256SUMS.sig` verified by Ed25519 with
   the release key (`release1:…`, the one in `launcher/src/Mkwmp.Update/ReleaseSums.cs`), in pure
   Python (RFC 8032); the list's first line must name the version; the zip's SHA-256 must be the
   listed one. Any failure installs nothing.
5. **Files.** The zip's files under `mkwmp/` go into the folder, each through a temporary file and a
   rename. Never replaced: an existing `*.toml` of the folder, `logs/`, `.mkwmp-selfupdate/`. Files no
   zip brought (`mkw-recomp/`) are not looked at. The same path rules as the launcher's own update
   (launcher/docs/UPDATES.md, section 5). `.mkwmp-install` records the version, the Proton and the
   launch options.
6. **Artwork.** Section 3.
7. **The shortcut**, for every Steam user of the machine (`userdata/<id>/config/shortcuts.vdf`,
   binary VDF). The file is parsed into ordered lists and must write back byte for byte before it is
   touched, else the script stops. One entry is added, or the one with the same id or the same exe
   is updated in the fields the script owns (name, exe, start folder, launch options, icon); every
   other entry keeps its bytes, and its own `LastPlayTime` and tags stay. The id is
   `crc32("\"<dir>/mkwmp-launcher.exe\"" + "MKWMP") | 0x80000000`, what Steam itself computes, so it
   is stable for an install folder: a second run changes nothing.
   **Older MKWMP shortcuts are removed** in the same edit: an entry named "Mario Kart World
   (MKWMP)" or "Mario Kart World (debug)", or whose program is the old Linux launcher (a file named
   `mkwmp-launcher`, no `.exe`), and that is not this install's own. Their grid artwork (copied to
   `*.bak-mkwmp-<time>` first) and their `CompatToolMapping` entries go too; their folders and
   prefixes are not touched. Each one is listed, also by `--dry-run`. Nothing else matches: on
   this machine's file, 33 of 34 entries keep their bytes. `--uninstall` does not look for them.
8. **Proton on the shortcut**: `config/config.vdf`, `CompatToolMapping/<id>` = `{name, config "",
   priority 250}`, as Steam writes it. The text file is edited in place: only that block changes.
9. **The Steam Deck's layout**: `steamapps/common/Steam Controller Configs/<user>/config/
   configset_controller_neptune.vdf` gets `"mkwmp" { "template" "controller_neptune_gamepad_joystick.vdf" }`
   (Steam files a non-Steam game's layout under its name in lower case). That is Valve's "Gamepad
   with Joystick Trackpad": a plain pad, View is the game's Select (the overlay's trigger), the
   right pad is a mouse for the few places that want one. An entry already there is the player's
   and is kept. Other controllers use Steam's default for a shortcut, a plain gamepad.
10. Each of the three Steam files is copied beside itself first (`<name>.bak-mkwmp-<time>`), only
    when it changes, and written through a temporary file, `fsync` and a rename.

`--uninstall` removes exactly what 7 to 9 and the artwork added. On copies of this machine's files
the three files after install and uninstall are byte for byte the originals.

### Launch options

None. Checked on the machine:

- `WINEDLLOVERRIDES`: not needed. The mod is loaded by mkw-recomp (`mods/mkwmp`), not as a DLL beside
  the exe; for the older winmm patch the launcher writes the per-application override itself
  (`WineHost.EnsureDllOverride`), because an override in a child's environment does not reach
  Wine's loader (Infinity's finding).
- DXVK and VKD3D-Proton are not in the game's path: mkw-recomp draws with Vulkan, which Wine passes
  to the host driver (winevulkan). Proton's `PROTON_USE_WINED3D`, `PROTON_NO_D3D*`, `DXVK_*` change
  nothing here. WPF (the launcher) draws with Direct3D 9, so DXVK serves the launcher's window.
- Sync: Proton 11 on this kernel says `ntsync: up and running` (`/dev/ntsync`). Nothing to set.
- `SteamDeck=1` reaches the launcher and so the game (Steam sets it on SteamOS); mkw-recomp's
  Windows build takes its handheld defaults from it (launcher/docs/RECOMP_INTEGRATION.md, section 5).
- gamescope: in Game Mode it is the session's compositor; no option is needed. On a desktop,
  `gamescope -f -- %command%` is the player's choice and not set.
- `PROTON_USE_XALIA`: Proton starts Xalia (its gamepad helper for launchers) beside every program;
  it was running beside the launcher and reading its window through UI Automation. The launcher
  does not answer it under Wine (section 5, "Does not work", 2), so Xalia has nothing to steer
  there. If Game Mode still shows a second focus frame or doubled input:
  `--launch-options 'PROTON_USE_XALIA=0 %command%'`. To check in Game Mode.

## 3. Artwork

No picture is in this repository or in a release. `tools/linux/artwork.json` holds links only: five
pictures of SteamGridDB's game 5483943 ("Mario Kart World"), picked by hand. The script reads the
`artwork.json` beside itself, else the repository's, downloads each link on the player's machine
(plain GET, 30 s, three tries), accepts only PNG or JPEG (an icon may be ICO) by the file's first
bytes, and writes them as Steam looks them up, in `userdata/<id>/config/grid/`:

| Slot | File | Size |
|---|---|---|
| grid | `<id>p.png` | 600 x 900 |
| wide | `<id>.png` | 920 x 430 |
| hero | `<id>_hero.png` | 3840 x 1240 |
| logo | `<id>_logo.png` | |
| icon | `<id>_icon.png`, also the shortcut's `icon` | 256 x 256 |

A link that fails (404) falls back to SteamGridDB's public search (`POST
/api/public/search/main/games`, no key, not a documented API): of that game's pictures, those
without the nsfw, humor or epilepsy mark, not animated, in English or without a language; the first
size asked for; the official style first, then up-votes minus down-votes. If that fails too the slot
is left out and the install goes on; without an icon the shortcut gets MKWMP's own
(`mkwmp-launcher.png`). `--art-only` tries again later. Tested: the five links, and the fallback
with two links broken on purpose.

## 4. Proton and the native build

mkw-recomp ships a Windows zip and Linux AppImages. MKWMP uses the Windows build under Proton.

Why, apart from speed: the launcher is a Windows (WPF) program. Only when the game is its child in
the same prefix do they share one Steam app, which is what gamescope needs to put the launcher's
window over the game and back (launcher/docs/OVERLAY.md, section 4), and what lets the launcher see
the game's process and window. A native game beside a launcher under Wine would need a second
implementation of all of that (launcher/docs/RECOMP_INTEGRATION.md, section 3.3).

What is the same on both paths, so no difference is expected from it:

| | Proton (Windows build) | Native (AppImage) |
|---|---|---|
| The game's code | the same C from the same lifter, compiled by the same clang 22 at the same flags; Windows x64 calling convention | the same, System V calling convention |
| Drawing | Vulkan; winevulkan hands each call to the host's driver (RADV on a Deck) | Vulkan, the same driver |
| Shaders | the same SPIR-V, the driver's own pipeline cache | the same |
| Presentation | SDL3's Windows back end, Wine's X11 driver, a Vulkan swapchain on the X11 window; gamescope composites (or scans out) | SDL3's X11 or Wayland back end; the same gamescope |
| Audio, input | SDL3 over Wine's WASAPI and XInput/HID, then PipeWire and Steam Input | SDL3 over PipeWire and evdev/Steam Input |

Where Proton costs something: every system call of the game goes through Wine's ntdll (files, memory,
threads, waits), and a Vulkan call through one more thunk. With ntsync in the kernel the waits are
kernel objects, not a round trip to wineserver. For a game that is bound by its own compiled code
and the GPU, that is expected to be within the noise of a frame-time measurement. Where Proton can
be better: nowhere by itself. It is not expected to be faster than the native build; "equal" is the
realistic target, and nothing here supports a promise of "better".

**None of this is measured.** No game was run for this document. To settle it, on one machine, the
same track and settings, both paths set up from the same copy:

1. mkw-recomp's own overlay (F10, `perf_overlay`) for the frame time and the game's own counters;
2. MangoHud's log (`MANGOHUD_CONFIG=output_folder=...,log_duration=120,autostart_log=1`) for the
   frame-time distribution (1 % and 0.1 % lows matter more than the average);
3. the same present mode (`vk_present`), resolution and frame limit in both `mkw.ini`.

Until then the recommendation is: ship Proton as the supported path because the overlay and the
launcher need it; keep the native AppImage as the comparison, and if it measures clearly better,
the gap is the thing to report upstream, not a reason to split the product.

Start-up, measured here (Proton 11.0, an already made prefix, files in the page cache): the launcher
from process start to its first rendered frame and exit (`--shot`), about 5 s. The prefix's first
creation took about the same again.

## 5. What was found under Proton

Run on the machine above, the launcher installed by the script into `~/Games/MKWMP` (`--no-steam`),
started from a shell with Proton 11.0 inside Steam Linux Runtime 4 with the shortcut's own prefix
(`compatdata/<id>`), as Steam would start it.

**Does not work**

1. **The game's setup stops at "Building the game".** mkw-recomp's setup compiles with `zig cc`,
   and zig locks its cache's manifest with `NtLockFile` in a form Wine does not implement
   (`fixme:file:NtLockFile Unimplemented yet parameter`, seen with `WINEDEBUG=fixme+file`). zig
   reports `checking cache failed: manifest_create Unexpected` for every file, on `Z:` and on `C:`
   alike; `ZIG_GLOBAL_CACHE_DIR` on `C:` (what the setup's own message suggests) does not help. The
   same call fails the shaders step's `spec.py` (`zig cc` of `intercept_win.c`). Up to there it
   works: `check`, `exefs`, `romfs` (linked), and `generate` in 42 s on 16 threads.
   - The setup would compile with `setup\clang\clang.exe` and never need zig's cache, but
     `fastcc.py` asks `zig cc -###` for the command line and refuses its answer because zig exits 1
     (the line itself is printed). With that one check relaxed in the installed `fastcc.py`, as an
     experiment, the compile ran to its end: clang.exe under Proton, 16 at a time, 394 units in
     230.9 s, the link (`zig lld-link`) in 0.9 s, and `mkwgame.dll` (214 MB) was written. For scale:
     186 s on the 16-thread Windows PC of RECOMP_INTEGRATION.md 1.6, another CPU. The shaders step
     then failed at once in `spec.py`, on the same `zig cc`. The file was put back afterwards.
   - So the fix is the fork's, and small: accept the `-cc1` line when zig failed on its cache, and
     build `spec.py`'s helper DLL with clang and `lld-link` too (or ship it: it is the fork's own
     code). Then `nvdisasm` with the injected DLL, `glslc` and `mkw.exe --prewarm` still have to be
     seen under Proton.
   - The other way: the setup run natively by the Linux package (`prepare.py --target windows` exists
     and cross-compiles with the native zig), writing the Windows game folder. Faster than any
     compile under Wine, but the launcher under Wine would have to start a host program and the
     folder's paths would have to stay `Z:\` paths. Not tried.
2. **The launcher's window was gone twice** during this run, with nothing in its log. Looked at
   again the same evening, from systemd's journal of the two test units and two hours of trying to
   make it happen again:
   - "After Try Again", 20:09:33: not a crash. That unit ended with exit code 0, the launcher's own
     exit; a crash does not give 0. It left on the key the test sent at 20:09:32; which key is not
     recorded.
   - "97 s into a running setup", 20:15:32: a crash. That unit's exit code was 5, the low byte of
     0xC0000005: an access violation in native code inside the launcher's process. A deliberate
     one (`--crash-test native`) gives the same 5; an unhandled .NET exception gives 82,
     `Environment.FailFast` 35, a kill 255. .NET reports such a fault only to the Windows Event
     Log, which Wine does not keep, and Proton silences Wine's own output, so nothing was written.
   - It did not happen again: 28 retried setups on the failing install (some with 16 busy threads
     beside), 20 runs of a stand-in setup with 397 progress steps and 16 processes at a time (one
     of 240 s, three stopped and continued, as on the day), the real compile once (289 s, 16
     `clang.exe`), a virtual controller plugged in and out 40 times, all under `WINEDEBUG=+seh`:
     no access violation in the launcher. So the faulting code is not known.
   - What was found on the way and is changed:
     - `logs/launcher-crash.log`: each launcher writes a line when it starts and when it leaves,
       and an unhandled exception with its stack, each on the disk before the call returns. A
       start without an exit is a crash or a kill, and the next start says so in
       `logs/launcher-ui.log`. Section 6 says how to get a native fault's own stack.
     - Xalia (section 2, "Launch options") was asking the launcher's window for its UI Automation
       tree, and WPF answered from inside the launcher through Wine's `uiautomationcore`: 600
       calls in 40 s of a running setup (`WINEDEBUG=+uiautomation`). That is native code running
       in the launcher on every focus change and progress step, for nobody's use, and it is there
       only under Proton. The window no longer answers under Wine (`Platform/WineAutomation.cs`):
       none of those calls are left. Whether this was the fault is not proven.

**Works**

- The window: full screen on the desktop, the fonts (Nunito; Japanese, Korean and Chinese from the
  bundled Noto Sans CJK), every page that was opened. Keyboard input (Enter, arrows, Esc, typing a
  name).
- The battery, the charger and the Wi-Fi level in the top bar: read from `Z:\sys` and `Z:\proc`.
- Wine is recognised (`ntdll wine_get_version`); paths are `Z:\home\deck\...`; `client.toml`,
  `launcher.json` and `logs/` land in the launcher's folder.
- HTTPS and the download: mkw-recomp 0.1.16-mkwmp (221 MB) downloaded, verified and unpacked (23,344
  files) in 18 s; `mkw.ini` written.
- The setup driver: starts the runtime's `python.exe`, reads its lines, shows the step and the time
  left, and reports the failure with the log.
- The update helper (`mkwmp-updater.exe --shot`): draws.

**Small things**

- The update helper's text comes out in a serif face instead of the embedded Nunito
  (`Mkwmp.Updater/Painter.cs`, a GDI+ private font collection). Asking for Tahoma or Arial after
  Segoe UI changed nothing, so a name lookup does succeed and something later substitutes the face.
  Open; the window is readable.
- The session log's `os:` line says `under Wine (ntdll wine_get_version)` now
  (`Launcher.Background.cs`; seen on the machine).
- The file and folder pickers are Wine's own dialog: small, in a corner, opening on "Desktop"
  although the launcher asks for the Linux home, and not usable with a pad. A path typed into its
  "File name" box works. In Game Mode the page's own path box is the only way.
- That path box opens the launcher's on-screen keyboard, which has no backslash; with a physical
  keyboard its letters are the keyboard's shortcuts (Q, F, X), so a path cannot be typed there.
  `Z:/home/deck/...` with forward slashes was not tried.
- `close_to_tray = true` is the default; under gamescope there is no tray to come back from.

**Bigger, for the launcher**

- A pad-usable way to pick the game copy under Proton: the fork's `finddumps.py` list (it already
  looks in Linux home folders and `/run/media`), or a file browser of the launcher's own.
- Everything in OVERLAY.md section 9, point 4 (Game Mode): not run, the machine was in Desktop Mode.

## 6. For maintainers

- The script is the release's `install-mkwmp.sh` (launcher/docs/UPDATES.md, section 8). It must stay
  LF (`*.sh text eol=lf` in `.gitattributes`).
- A new release key goes into `RELEASE_KEYS` in the script as well as `ReleaseSums.cs`.
- Testing the Steam edits without a Steam: the Python between the `MKWMP_PY` markers runs anywhere;
  point `HOME` at a folder holding copies of `shortcuts.vdf`, `config.vdf` and
  `configset_controller_neptune.vdf` in Steam's layout, and compare the files before and after
  install and uninstall. `--dry-run` on a real machine prints the plan and writes nothing.
- Starting the installed launcher as Steam does, from a shell (for logs, or `--shot`): set
  `STEAM_COMPAT_CLIENT_INSTALL_PATH` to Steam's folder, `STEAM_COMPAT_DATA_PATH` to
  `steamapps/compatdata/<id>`, and run `<SteamLinuxRuntime_4>/run -- "<Proton 11.0>/proton"
  waitforexitandrun ./mkwmp-launcher.exe ...` in the install folder. The container kills the
  program when the shell that started it ends: use `systemd-run --user` for a window that stays.
  `waitforexitandrun` first waits until nothing else runs in the prefix, and `proton run` from a
  second shell joins the wineserver that is already there: two tests in one prefix are not
  independent (seen: the launcher's start waited two minutes for another shell's `zig.exe`).
- A launcher that disappears: `logs/launcher-crash.log` has its start and no exit, and the text of
  an unhandled .NET exception when it was one. For a native fault, start it once more with
  `PROTON_LOG=1` (launch options `PROTON_LOG=1 %command%`): `~/steam-<id>.log` then holds .NET's
  own report with the managed stack, as `err:eventlog:ReportEventW` lines, and with
  `WINEDEBUG=+seh` the faulting module and address (`dispatch_exception code=c0000005`). The
  exit code of the unit (or of Steam's game process) tells the kind: 5 a native access violation,
  82 an unhandled .NET exception, 35 `Environment.FailFast`, 255 a kill, 0 the launcher's own
  exit. `--crash-test native|thread|failfast|kill|ui|stack` ends the launcher that way on purpose,
  three seconds after its start, to check what a machine records (`stack`, a stack overflow, hung
  under Proton 11 instead of ending). `MKWMP_WINE_AUTOMATION=1` makes the window answer UI
  Automation under Wine again, to compare.
- Before blaming the launcher, look for the kill in the system journal (`journalctl -b`, not
  `--user`): SteamOS sets `KillUserProcesses=True` in logind, so a Steam started from an SSH
  shell lives in that login's `session-<n>.scope` and is ended with it (`session-<n>.scope:
  Killing process ... (steam) with signal SIGTERM`); Steam then ends its game, whose
  `app-steam-app<id>-<pid>.scope` stops two seconds later, and `launcher-crash.log` has a start
  without an exit. `setsid`, `nohup` or `&` do not leave the scope. Start Steam for a test with
  `systemd-run --user --collect -E DISPLAY=:0 -E WAYLAND_DISPLAY=wayland-0 -E XAUTHORITY=...
  steam steam://rungameid/<id>` instead. Seen 20/11/2026 22:11: an SSH command that timed out
  after 120 s took Steam and a setup 90 s into its shaders step with it; the same setup, the
  same launcher, with Steam started that way, ran to `done` (708 s).
- At a Proton bump: the launcher's `--shot`, the first-run pages, and one whole setup.

## 7. Not done

- The live install on a machine's real Steam files and a start from Steam (the script was run for
  real only with `--no-steam`, and with `--dry-run` against the real files).
- Game Mode: the shortcut in the library with its artwork, the layout, the focus between the
  launcher and the game, the overlay, Xalia.
- The setup to its end, the game, and any performance figure.
- Flatpak and snap Steam, other distributions, Proton 9, 10, Experimental and GE (only 11.0 ran).
