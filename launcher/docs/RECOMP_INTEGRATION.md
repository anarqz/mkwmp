# mkw-recomp inside the launcher

The direction: the player opens only the MKWMP launcher and never sees mkw-recomp's screens. This document says what
mkw-recomp does on a player's machine, which parts the launcher can already drive, what is missing, and the plan in
three stages.

The line that does not move: the player brings their own dump of the game (`.nup`, `.nx`, or loose `exefs/` +
`romfs/`). Everything made from it (`mkwgame.dll`, the shaders, the caches) is made on their machine and is never
downloaded, uploaded or shipped by MKWMP. The launcher never helps to obtain the game.

Sources, read on 2026-11-20:

- `fork:` is anarqz/mkw-recomp at tag `v0.1.15-mkwmp` (commit `5b3667a`). Read, not built, not run.
- `zip:` is the release asset `mkw-recomp-0.1.15-mkwmp-windows-x64.zip` (217,227,522 bytes, sha256
  `2a5ae5b856692d4a6eae357fcc80e63dd12f5b7725429a05c77117f043d472df`, both checked here), listed without extracting.
- `install:` is a set-up game folder on a Windows PC (16 threads, 31.7 GB, RTX 5050 Laptop), read only.
  **It is not the pinned release**: its `setup-state.json` says `"port": "3c51af6"`, a CI build of the fork branch
  `ci/nup-layered-build` on the `v0.1.16-rc` line, and its mkw.exe (sha256 `f40d435a...`) is not in `Recomp.Known`.
  Its sizes and timings are real and close, but two things differ from the tag and are marked `(rc)` below: the
  `tonemap` key with `spirv-renodx\`, and the game id.
- "not verified" means the claim was not confirmed in source, zip or install.

## 0. Findings

### The setup, step by step

All of the setup is one Python program, `setup\src\tools\prepare\prepare.py`, run by the Python the zip carries. The
start screen only starts it and reads its lines (`fork: runtime/ui/launcher/launcher_setup.cpp` `setup_start`,
`on_line`). mkw.exe has no `--setup` flag and needs none: the launcher can run the same command.

| Step | Runner | Headless today? | How |
|---|---|---|---|
| `check` (not a `done` key, runs every time) | Python: `gamesource.py` `check`; for a `.nx` also `mkw.exe --unpack` | yes | part of `prepare.py`; prints `version <name>` |
| `exefs` | Python: `nup.py` `Nup.extract` (`.nup`), `gamecopy.py` `copy_tree` (loose), `mkw.exe --unpack` (`.nx`) | yes | part of `prepare.py` |
| `romfs` | `link`: one line in `mkw.ini`. `copy`: `Nup.extract` / `copy_tree` / `mkw.exe --unpack` | yes | `--romfs link` or `--romfs copy` |
| `generate` | Python: `setup\src\recomp\gen.py` (worker processes) | yes | part of `prepare.py`; no progress count |
| `compile` | `setup\clang\clang.exe -cc1` per chunk (command line taken from `zig cc -###`), link by `setup\zig\zig.exe lld-link` with `setup\lib\crt` | yes | part of `prepare.py` |
| `shaders` | Python: `sm86\spec\spec.py` (downloads NVIDIA's `nvdisasm` and `ptxas`, first time only), `tools\shaders\extract.py`, `pipeline_pairs.py`, `translate.py` with `setup\glslc\glslc.exe` | yes, needs internet the first time | part of `prepare.py`, runs beside generate and compile |
| `tonemap` (rc) | Python: `tools\shaders\renodx.py` with spirv-cross / glslangValidator / spirv-val beside glslc | yes | not at the tag; in 0.1.16-rc a `done` key without a `step` line |
| `warm` | `mkw.exe --prewarm` (Vulkan device, no game code), after `pipeline_pairs.py` / `pack.py` checks | yes, needs the GPU | part of `prepare.py`; by hand: `mkw.exe --prewarm` with `MKW_GAME_DIR` |
| `cleanup` | Python: `clear_work`, `game_texts.build_all`, `seqnodes.write`; `mkw.exe --unpack` when the RomFS is a package | yes | part of `prepare.py`; runs every time |

The command for all of it, as the start screen builds it (`fork: launcher_setup.cpp:142-145`):

```
<runtime>\setup\python\python.exe -I <runtime>\setup\src\tools\prepare\prepare.py
    --game <game folder> --runtime <folder of mkw.exe> --port 0.1.15-mkwmp
    [--source <dump.nup | dump.nx | folder>] [--romfs link|copy]
```

Without `--source` and `--romfs` it repeats the last setup from `setup-state.json` and runs only the steps whose key
changed. That is the launcher's "make this install current" operation after every runtime update.

### The other answers

| Question | Answer |
|---|---|
| Skipping the start screen | `mkw.exe --play` or `MKW_NO_LAUNCHER=1` never reach it (`fork: runtime/core/boot/main.c` `want_launcher`). The launcher already starts `mkw.exe --play` (`MkwRecompTarget.Launch`). The config key is `start_screen = skip` under `[launcher]` in `mkw.ini` (`START_SCREEN`, `auto\|always\|skip`, default `auto`, `fork: runtime/core/base/cfg.c:131`), but it is a weaker tool: section 3.1. |
| What `--play` skips besides the window | The "set up again after an update" check (`port`), the code-mod rebuild, the mods merge, the save backup before a new mod set, the last-run notice, the GPU check. The launcher must do the first; the rest is section 2. |
| Runtime versus generated | The zip holds `mkw.exe`, `SDL3.dll`, `libopus-0.dll`, `libusb-1.0.dll`, `setup\` and `licenses\`, nothing else (`fork: tools/build/release.py` `windows`, `dist.py` `WIN_DLLS`; zip listing). Everything else in a game folder is made on the player's machine: section 1.8. |
| `setup\` on a runtime update | `setup\` is runtime, not player data. `setup\src` and `setup\lib` belong to one mkw.exe: the step keys hash `setup\src`, and the game id comes from `setup\src\runtime\cpu\*.h`. An update that keeps an old `setup\src` builds a game the new mkw.exe refuses. Compare every file of the zip, `setup\` included. |
| The in-app updater | It asks `releases/latest` of `anarqz/mkw-recomp` (`fork: tools/prepare/update.py` `REPO`). A player who opens mkw.exe directly is offered the next fork release and leaves the pin. Write `update_check = 0`. |
| Game id of the pin | `c0dd6cf12c07c23a` (recomputed here from the tag's headers and found in the zip's mkw.exe; matches `Recomp.GameIds`). The 0.1.16-rc line is `c9bbcb75a9ac9a49`: the bump to it recompiles the game for every player. |
| The setup's own downloads | One: NVIDIA's CUDA 12.8 `nvdisasm` and `ptxas` (126 MB on Windows, pinned by sha256) from `developer.download.nvidia.com`, by `spec.py`, to build the shader decoder's spec in `<game>\sm86\`. Nothing of the game. |
| Credits | mkw-recomp's `LICENSE`: `MIT License`, `Copyright (c) 2026 Adrià`. Section 4. |

## 1. The setup pipeline

### 1.1 Who starts it, and with what

`prepare.py` is started in four places, all with the same Python and `-I`:

| Caller | Arguments | When |
|---|---|---|
| `fork: launcher_setup.cpp` `setup_start` | `--game G --runtime R --port V --source S --romfs link\|copy` | Game files > Set up |
| same, `source = NULL`, `link_romfs = -1` | `--game G --runtime R --port V` | automatically at start when the last setup was by another version, and before Play when a code mod changed (`fork: launcher.cpp` `launcher_run`, `page_main`) |
| `fork: launcher_update.cpp` `prepare_next` | the *new* package's Python and script: `--game G --runtime <new package> --next --port V` | the in-app update, set up beside the played version in `<game>\setup-next\` |
| the AppImage's `AppRun --prepare-next` | the same, on Linux | `fork: tools/build/release.py` `APPRUN` |

`prepare.py`'s arguments (`fork: tools/prepare/prepare.py` `main`): `--source`, `--game` (required), `--runtime`
(default: `--game`), `--romfs copy|link`, `--jobs N` (default: from cores and free memory), `--cc`, `--glslc`,
`--target linux|windows`, `--next`, `--port`. Exit codes: 0 done, 1 failed, 2 bad command line or no earlier setup.

What it does to its own process before the steps:

- removes every `PYTHON*` variable and sets `OPENBLAS_NUM_THREADS=1`;
- `own_children()` (`fork: tools/prepare/machine.py`): on Windows a job object with kill-on-close, so killing
  python.exe kills gen.py's workers, the compilers and glslc; it also hides the console window it was given;
- `hold_lock(game)`: `<game>\setup.lock`, one byte locked with `msvcrt.locking`, retried every 2 s **without a time
  limit**. A second setup of the same folder waits silently (one line in `logs\setup.log`). The file stays on disk
  afterwards, empty: its presence means nothing;
- opens `<game>\logs\setup.log` (rewritten at every setup) and, after 120 s without an event, dumps its Python stacks
  there.

### 1.2 How it reports

One event per line on stdout (`machine.emit`), the same lines in `logs\setup.log` with the tools' output and timings:

| Line | Meaning |
|---|---|
| `step <name>` | the step started (`check exefs romfs generate compile shaders warm cleanup`) |
| `skip <name>` | its key is unchanged: not run |
| `progress <done> <total>` | for the step under way; units differ per step (below) |
| `version <name>` | from `check`: the game version of the dump (`1.0.0`, `1.8.0`). The start screen ignores it; the launcher should not |
| `done` | everything finished, exit 0 |
| `error <code> <detail>` | exit 1 (or 2 for `source`) |

Error codes (`fork: prepare.py` header, `gamesource.py`, `gamecopy.py`; the start screen's texts are in
`launcher.cpp` `setup_error_text`):

| Code | Detail | Cause |
|---|---|---|
| `source` | text | no `--source` and no earlier setup; or a package that could not be unpacked |
| `moved` | the old path | the last setup's dump is not there any more (link mode, or a copy whose folder is gone) |
| `nogame` | path | not a `.nup`/`.nx`, or no `exefs/main` and `romfs/` within two levels |
| `encrypted` | path | `exefs/main` is not an NSO |
| `version` | build id | not 1.0.0 (`9e244438c3d8ef1c`) or 1.8.0 (`d00e4f399e411d06`) (`fork: recomp/versions.py`) |
| `romfs` | file count | under 95 % of the version's files (1.0.0: 205,660; 1.8.0: 206,202) |
| `mismatch` | text | RomFS and `exefs/main` from different versions |
| `space` | `N GB, M GB free in <dir>` | not enough room for the RomFS copy plus 3 GiB for the first build |
| `failed` | a tool's English message | compile, link, generator, translator |
| `io` | the system's message | file errors |

The start screen adds three of its own that `prepare.py` never prints: `no_setup` (tools missing), `cancelled`,
`stopped` (the process ended without `done` or `error`). A driver needs the same three.

Per step:

| Step | Progress lines | Other signs |
|---|---|---|
| check | none | `version <name>` |
| exefs | `.nup`: `progress d t` from `Nup.extract`; `.nx`: bytes from `mkw.exe --unpack` | |
| romfs | link: none (instant). copy: as exefs | `mkw.ini` gets `romfs = <path>` (link) or `romfs = romfs` (copy) |
| generate | **none** (gen.py's output goes to the log only): show an indeterminate bar | `setup-work\generated\` grows to about 0.95 GiB |
| compile | `progress n total`, one per unit, `total` = units + 1 for the link (install: 397) | `compile: N of M units compiled, the rest reused` in the log |
| shaders | translate.py's `progress n total` every 100 programs (install: 11,604), buffered by `beside.py` and shown when the main loop reaches the step | spec.py, extract.py and pipeline_pairs.py give no count |
| warm | only `progress 0 1` and `progress 1 1` | `logs\warm.log` has a line every 2 s: `vk_pipeline: pairs prewarm: N of M shader libraries, T ms`. Tail it for a real bar |
| cleanup | bytes from `mkw.exe --unpack` when the RomFS is a package | |

### 1.3 The steps

**check** (`fork: tools/prepare/gamesource.py` `check`, `find_game`). Reads the first 0x48 bytes of `exefs/main`
(from the package's table for a `.nup`), takes the NSO build id, looks it up in `recomp/versions.py`, counts the RomFS
files, and refuses a RomFS whose `System/Resource/ResourceSizeTable.Product.<n>` names another version. Then, before
anything is written, `check_space`.

**exefs.** Six files, 43.8 MB (install), into `<game>\exefs\`. Key: the source path.

**romfs.** `link` is not a file system link. It is one line, `romfs = <path of the .nup, or of the romfs folder>`, in
`<game>\mkw.ini` (`prepare.py` `ini_set`), and the runtime reads the package in place with positional reads
(`fork: runtime/sdk/fs/nup.c`, `pack.c`, `romfs.c`: `plat_file_pread`). No hard link, symlink or junction is made.
`copy` extracts or copies about 22 GiB into `<game>\romfs\`, six files at a time, resumable (`gamecopy.py`:
`COPY_JOBS`, `.part` files, a file of the right size is kept), and removes files the version does not have.
Key: `link <path>` or `copy <version>`.

**generate.** `python.exe recomp\gen.py --exefs <game>\exefs --out <game>\setup-work\generated --jobs N --patches
recomp\patches-mkw[-180].txt --mods <game>\mods [--image setup-work\image.json]` (`prepare.py` `generate`). The
AArch64 code of the dump lifted to C. `N` is `min(cores, memory budget / 0.7 GB)`.

**compile** (`fork: tools/prepare/gamecode.py` `compile_game`, `fastcc.py`, `machine.py` `crt_link`). About 396 C
chunks plus `modules.c`. `zig.exe cc -target x86_64-windows-gnu ... -###` is asked once for the `-cc1` line, and
`setup\clang\clang.exe` runs it per chunk (30 % faster than zig's own LLVM; `MKW_SETUP_CC=zig` goes back to zig). The
link is `zig.exe lld-link <flags of setup\lib\crt\link.json> -OUT:mkwgame.dll.part -PDB:... -IMPLIB:... @link.rsp
setup\lib\libmkw.dll.a <crt objects>`, with a 30 min limit, then a rename to `mkwgame.dll`, then `mkwgame.id` is
written. Every object has a `.key` stamp in `setup-work\obj\` and is reused while its C, flags, compiler and headers
are the same. A chunk with NUL bytes (a damaged write) makes `prepare.py` generate once more.

**shaders** (`fork: tools/prepare/shaderstep.py` `shaders`). Starts in a thread when `generate` starts
(`beside.py`), and `translate.py` waits for generate's end:

1. `sm86\spec\spec.py --out <game>\sm86 --cache <game>\setup-work\nvidia --cc <json> --crt setup\lib\crt\link.json`,
   only when `<game>\sm86\instructions.json` is missing or broken. It downloads NVIDIA's two packages, assembles a
   tiny PTX with `ptxas.exe`, starts `nvdisasm.exe` suspended, injects a DLL it compiles here
   (`VirtualAllocEx`/`WriteProcessMemory`/`CreateRemoteThread(LoadLibraryW)`, `spec.py` `inject`) and captures the
   instruction text from memory. Up to 8 tries (install: 5 rejected captures before one was accepted).
2. `tools\shaders\extract.py --romfs <romfs or .nup> --exefs <game>\exefs --out <game>\setup-work\shaders`.
3. `tools\shaders\pipeline_pairs.py --shaders setup-work\shaders --out <game>\pipeline-pairs.bin`.
4. `tools\shaders\translate.py --shaders setup-work\shaders --out <game>\spirv --glslc setup\glslc\glslc.exe
   --no-validate --progress --jobs N --spec <game>\sm86\instructions.json`, `N = min(cores, 61, free memory / 0.75
   GB)`. When a worker dies (out of memory) it runs again with half the workers; what is translated stays.

**tonemap (rc).** `tools\shaders\renodx.py --spirv <game>\spirv --out <game>\spirv-renodx --tools setup\glslc`. A
failure is logged and the setup goes on.

**warm** (`prepare.py` `warm`). `ensure_pairs`, `ensure_pack` (writes `spirv\shaders.pack`, 860 MiB in the install),
then `mkw.exe --prewarm` with `MKW_LOG_NAME=warm` and `MKW_GAME_DIR=<game>`, stdout and stderr dropped, a 1,800 s
limit. It compiles every shader library into the driver's cache and `pipeline-cache.bin` (`fork:
runtime/gfx/vk/pipe/vk_pipe_pairs.c` `vk_pipeline_prewarm_main`). It uses the GPU that `mkw.ini` picks. A crash, a
non-zero exit or the time limit does **not** fail the setup: the step is marked done and the game builds pipelines as
it draws. Whether it opens a window: not verified (warm.log shows SDL's video driver starting and no window line).

**cleanup.** Removes `setup-work\` except `obj\`, `zig-cache\` and `shaders\`; builds the four extra languages into
`<game>\lang\` (ca, gl, eu, tr); writes `seq_nodes-<n>.tsv`. Its key is empty: it runs at every setup.

### 1.4 Resuming

- `<game>\setup-state.json`: `{"done": {step: key}, "source", "romfs_mode", "version": 1, "port"}`, saved after each
  step through a `.tmp` and a rename (`prepare.py` `save_state`). A step whose stored key equals the new key prints
  `skip`. A file with another `"version"` (`PREP_VERSION`) counts as empty: everything runs.
- Killing the process is safe at any point. The next run repeats the step that was under way; `compile` keeps its
  objects, `translate.py` keeps what it translated, a RomFS copy keeps finished files.
- `setup-work\generated\` gone and `compile` to run: `generate` runs again (`regenerate_if_cleaned`).
  `exefs\main` gone: `exefs` runs again (`redo_exefs_if_missing`).
- `setup-next\` is only the in-app update's. A run without `--next` first promotes or removes it
  (`nextsetup.promote`): a leftover from an in-app update is handled by the next plain run.

### 1.5 What makes a step run again

| Key | What it covers (`prepare.py` `main`, `keys`) | Changes when |
|---|---|---|
| `exefs` | the source path | another dump path |
| `romfs` | `link <path>` or `copy <game version>` | another path, mode or game version |
| `generate` | game id, game version, sha256 of `recomp\*.py` + `recomp\patches-mkw*.txt` (only files listed in `setup\src\FILES`), and a `mods <hash>` suffix when a mod carries code | the runtime changed the lifter, its patches or the cpu headers; a code mod came or went |
| `compile` | the same plus `windows`, `tools\build\build.py`, `tools\prepare\gamecode.py`, and the CPU level when `MKW_SETUP_CPU=v2` | as generate, or the build flags. Objects are still reused one by one |
| `shaders` | game version, sha256 of `sm86\translate\**`, `sm86\decode\**`, `extract.py`, `translate.py` | the translator changed |
| `tonemap` (rc) | the shaders key + sha256 of `renodx.py` | |
| `warm` | game id, game version, the shaders hash, `pairs<N>`, `pack<N>` (format versions) | any of the above |
| `cleanup` | empty | always runs |
| `port` | the `--port` text, written before `warm` | not a step: it is how the start screen notices another version set the folder up (`launcher.cpp` `setup_from_other_version`: a text search for `"port": "<its version>"`) |

The game id (`fork: tools/build/build.py` `game_id`): the first 16 hex digits of sha256 over the generated code's
compile flags and the bytes of `runtime\cpu\*.h` and `runtime\core\boot\gamelib.h`. mkw.exe carries its own
(`RT_GAME_ID`) and refuses a `mkwgame.dll` built for another (`fork: runtime/core/boot/gamelib.c` `check`:
"built for another version of the runtime"; `rt_game_present` compares `mkwgame.id` without loading the DLL).

What this means for the launcher's gates in `Recomp.cs`:

- `RebuildProblemFor` (the `GameIds` table against `mkwgame.id`) catches a changed game id only. `generate` and
  `shaders` can change with the same game id: an update that touches only `recomp\` or `sm86\`. The start screen
  catches those through `port`. With `mkw.exe --play` nobody does, and the game runs with old code or old shaders.
- So the gate becomes: `setup-state.json` `port` differs from the installed runtime's version, or `mkwgame.id`
  differs, or `mkwgame.dll` is missing: run the driver without `--source`. It costs seconds when nothing changed
  (`check`, `skip` lines, `cleanup`).
- `RebuildProblem`'s text ("open mkw-recomp once and let it finish its setup") goes away with stage 1.

### 1.6 Time

Measured in the install's `logs\setup.log` (build 3c51af6, game 1.8.0, RomFS linked from a 23.4 GB `.nup`; 16 compile
and 16 generate jobs, 15 translate workers). That log is of a resumed run: `exefs`, `romfs` and `generate` were
already done and are not timed in it.

| Step | Wall time | Note |
|---|---|---|
| check + exefs + romfs (link) + generate | about 3 min, not logged | bounded by file times: `setup.lock` created 11:07:54, the logged run started about 11:11:00 |
| compile | 186.0 s (CPU 2,476 s) | 396 of 396 units, link 0.5 s |
| shaders | 266.3 s beside the others; 80.3 s of it after compile | spec.py 54.0 s, extract.py 53.7 s, pipeline_pairs.py 9.6 s, translate.py 79.0 s |
| tonemap (rc) | 21.6 s | |
| warm | 76.0 s (CPU 903 s) | 13,684 shader libraries; cache saved, 415,470 KB |
| cleanup | 24.4 s | |
| the logged run | 389.2 s | |

So a first setup on that PC is about 9 to 10 minutes. From the fork's own comments, not measured here: a whole setup
took 16 min on Linux and 31 min on Windows on the author's 6-thread PC before the reuse work (`prepare.py`, the
comment on issue 188); an update on that PC takes 329 to 390 s (`tools/prepare/README.md`, `beside.py`). A Steam Deck
(4 cores, 16 GB shared) is not measured anywhere: plan for tens of minutes and a progress page that survives a sleep.

Limits in the source: warm 1,800 s, link 30 min, stall dump after 120 s without an event (a dump, not a stop).

### 1.7 Disk

Measured in the install after its setup (link mode):

| Path | Size | Kept after setup |
|---|---|---|
| `exefs\` | 43.8 MB | yes |
| `mkwgame.dll`, `.pdb`, `.id` | 214.2 MB + 12.6 MB | yes |
| `spirv\` (46,472 files, with `shaders.pack`) | 1,804.6 MB | yes |
| `spirv-renodx\` (rc) | 2.6 MB | yes |
| `pipeline-cache.bin`, `pipeline-keys.bin`, `pipeline-pairs.bin` | 485.2 MB, 0.6 MB, 1.1 MB | yes; the cache grows as the game is played |
| `setup-work\obj\`, `shaders\`, `zig-cache\` | 275.0 MB, 96.0 MB, 0.04 MB | yes, for the next update |
| `sm86\` | 12.0 MB | yes |
| `lang\` | 10.5 MB | yes |
| `setup-work\generated\` | about 0.95 GiB (`gamecopy.py` comment) | no, removed by cleanup |
| `setup-work\nvidia\` | 126 MB download plus its unpacking (`spec.py` header) | no |
| `romfs\`, copy mode only | about 22 GiB | yes |

About 2.96 GB stays, about 4.1 GB at the peak, plus 22 GiB in copy mode. The runtime itself is 728.6 MB unpacked
(zip: mkw.exe 35.0 MB, three DLLs 4.8 MB, `setup\` 688.6 MB: zig 379.0, python 108.9, clang 104.7, glslc 78.1, lib
16.4, src 1.4). `check_space` asks only for the RomFS copy plus 3 GiB before the first compile, so the launcher should
ask for 5 GB free itself (27 GB for copy mode).

### 1.8 Runtime versus generated

| Shipped in the zip (replace on update) | Made on the player's machine (never ship, never delete on update) |
|---|---|
| `mkw.exe` | `mkwgame.dll`, `mkwgame.dll.pdb`, `mkwgame.id` |
| `SDL3.dll`, `libopus-0.dll`, `libusb-1.0.dll` | `exefs\`, `romfs\` (copy mode) |
| `setup\python\`, `setup\zig\`, `setup\clang\`, `setup\glslc\` | `spirv\`, `spirv-renodx\`, `pipeline-pairs.bin`, `pipeline-cache.bin`, `pipeline-keys.bin`, `pipeline-keys.prewarm` |
| `setup\lib\` (`libmkw.dll.a`, `crt\`) | `sm86\` (NVIDIA's spec; `release.py` `check_no_game` refuses to pack it) |
| `setup\src\` (`recomp\`, `sm86\`, `tools\`, `runtime\cpu\`, `gamelib.h`, `i18n\game\`, `FILES`) | `setup-work\`, `setup-next\`, `setup-state.json`, `setup.lock` |
| `licenses\` | `lang\`, `seq_nodes-<n>.tsv`, `logs\` |
| | `mkw.ini`, `profile\`, `mods\`, `backups\`, `steam-art\`, `splash.png` |
| | the in-app updater's: `update\`, `update-previous\`, `update-done`, `*.old` |

Notes for `RuntimeInstaller`:

- The 0.1.15-mkwmp pin listed `setup` in `RecompRuntime.Pinned.Preserved` (an existing `setup\` kept whole). The
  0.1.16-mkwmp pin takes it out: `setup\` changed between the two (python scripts in `setup\src\tools\prepare\`,
  `setup\src\runtime\cpu\dispatch.*`, `setup\lib\crt\`, spirv-cross added under `setup\glslc\`; nothing removed), so
  the installer compares every file there and replaces only those that changed, with its backup and rollback. An
  install made by the 0.1.15-mkwmp pin has no `setup\` files in its marker; from 0.1.16-mkwmp on they are listed, so
  a file a later pin drops from `setup\` goes to the backup like any other.

- `setup\src\FILES` lists the shipped sources, and the keys count only those (`shaderstep.py` `shipped`), so a file
  left by an older package does not change a key. It can still be imported by Python. Delete what is under
  `setup\src\` and not in the new `FILES`.
- `nextsetup.py` `OUTPUTS` and `WARMED` are the fork's own list of generated outputs; check the keep list against it
  at every bump.
- mkw.exe cannot be replaced while it runs. Refuse while the game runs, as `InstallAsync` does.

## 2. What mkw-recomp's screens do

Two UIs, both Dear ImGui: the start screen (its own SDL window, `fork: runtime/ui/launcher/`) and the in-game menu
(drawn over the game's picture, `fork: runtime/ui/menu/`). The settings tabs are one piece of code shown by both
(`settings_tabs.cpp`).

### 2.1 Start screen: main page (`launcher.cpp` `page_main`)

| Function | In the MKWMP launcher | Missing, and what it needs |
|---|---|---|
| Play; before it: rebuild when a code mod changed, `mods_merge_sync`, `mods_before_play` (copies `save\` to `save-before-mods\<time>\` when the mod list changed, then writes `mods_played`) | PLAY with `mkw.exe --play`, which skips all three | The MKWMP mod has no `romfs\` and no code (`mod.json`: title, version, `has_plugin`), so merge and rebuild do nothing for it. For players with other mods: run `python -I setup\src\tools\mod\merge.py --game G --romfs R --enable <mods> --choices G\mods\choices.json` and check `mods\.rebuild` before PLAY, or state that other mods are managed in mkw-recomp. The save backup is a few lines to port |
| Profile chip, Profiles page (`launcher_players.cpp`: players under `profile\players`, `MKW_PLAYER`) | the launcher's own profile (gamertag) | mkw-recomp players are not shown. One player is the default; nothing needed for stage 1 |
| Controllers seen, with battery | the launcher reads XInput for itself | a list is cosmetic; not needed |
| Update card (`launcher_update.cpp`) | replaced by `RuntimeInstaller` | write `update_check = 0` and lock it |
| Last run notice (`launcher_lastrun.cpp`: `lastrun.py --game G --version V` prints `clean`, `known N`, `fixed N vX`, `crash`) | bug-report zip has the logs | optional: run the same script after the game exits with a non-zero code (a fatal exits 70, `fork: runtime/cpu/dispatch.c` `rt_fatal`) |
| GPU warning (`list_gpus`, `vk_gpu_report`: Vulkan 1.3, extensions, queue) | none | section 2.4 |
| Desktop shortcut, Add to Steam (`steam.py`), library art | none | the launcher is the shortcut; stage 3 |
| GitHub and Discord links, version, "no game data" footer | About | section 4 |

### 2.2 Start screen: Game files (`page_files`, `setup_steps`, `launcher_finddumps.cpp`)

| Function | In the MKWMP launcher | Missing |
|---|---|---|
| Pick the dump: folder dialog, file dialog (`.nup`, `.nx`), and a list found by `finddumps.py` | Setup screen exists | the picker and the list. `python -I setup\src\tools\prepare\finddumps.py --game G [--seconds 4]` prints `dump<TAB>nup\|nx\|folder<TAB><version or ?><TAB><path>` per copy, then `done <n>`. It looks only at the player's own disks (home folders, Proton prefixes, removable media, drive roots, four levels deep) and at files that already are dumps. Using it is finding what the player has, not obtaining the game |
| RomFS mode: copy or link | none | a choice; default `link` (no 22 GiB copy), with the warning that the dump must stay where it is |
| Set up, progress per step, Cancel, Try again, open the log folder | none | the driver of section 3.1 |
| Install folder ("Choose game folder", `game_dir` in the mkw.ini beside mkw.exe, at most 131 UTF-16 units on Windows) | `Recomp.DataDir` reads it | stage 1 should keep data beside mkw.exe and not offer this |
| Summary card (game version, port, mode, source, folder) | `Recomp.GameVersion`, `GameLine` | source and mode are in `setup-state.json` (`source`, `romfs_mode`) |
| "Files not where the setup left them" (`files_gone`) | none | before PLAY: in link mode, does `setup-state.json` `source` still exist? Else the game dies in a fatal |

### 2.3 Settings (start screen and in-game menu)

Tabs, in order (`settings_tabs.cpp` `tab_order`): Screen, Graphics, Sound, Controls, General, then one tab per mod
(`MkwPluginApi.settings_tab`, in the game only), Advanced, Hacks.

| Tab | Covered by `RecompSettings.cs` | Not covered |
|---|---|---|
| Screen | fullscreen, window_size, vk_present, picture_fit, aspect, hud_position, hdr | **the GPU row** (`gpu_index`); the note "in use: VSync" (needs the running game) |
| Graphics | quality preset, aniso, tex_quality, detail_distance, shadow_size, resolution, upscaler, sharpness, operation_mode, fps, logic_hz, framegen, vrr, low_latency, perf_overlay; from 0.1.17 temporal, temporal_quality, dlss_preset, cas, cas_sharpness, fg_engine, reflex and the no-row shadow_draws | **Best settings for this PC** (`auto_set`) and its Undo |
| Sound | volume, audio_channels, background_mute | |
| Controls | pad_layout, button_icons, menu_shortcut, motion, rumble_strength, rumble_curve, rumble_triggers, switch2, dsu, dsu_server, dsu_slot, stick_deadzone, mouse_sens; from 0.1.17 the no-row pad_p2; from 0.1.18 renhdrum | **key and button bindings** (`binds_page.cpp`, the `bind_*` keys); per-player pad and battery rows |
| General | lang, menu_lang, update_check, start_screen, discord | `lan` (left out on purpose) |
| Advanced | the scheduling, shader, texture, fix, Vulkan and diagnostics rows; from 0.1.18 the no-row tex_pressure and vram_log (#481) | input_record, Open log folder, Copy system info, Reset all; 0.1.18's gpu_clocks and udmabuf (Linux only), pass_hazards (an experiment) |
| Hacks | none, on purpose | |
| Mods' tabs | none of MKWMP's since 1.4.3 (its MKWMP, Voice chat and Camera tabs and its options page are gone); the launcher sets everything and writes the mod's settings (`ModSettings.cs`, `Launcher.ModSettings.cs`) | |

**Optional libraries (0.1.18).** No release ships DLSS, FSR 3.1 or RenHDrum (NVIDIA's licence forbids distributing
DLSS's files on their own). 0.1.18's menu downloads them when its Graphics or Controls tab is drawn with a pick that
needs them (`settings_tabs.cpp` `libs_sync`, `ui/launcher/libs_fetch.cpp`): `setup\python\python.exe -I
setup\src\tools\prepare\libs.py fetch --features dlss,fsr3,... --dir <mkw.exe's folder>`, each file checked against
the size and sha256 of `libs.tsv` and kept when already there. A pick made in the launcher never draws that tab, so
the launcher runs the same command after it writes one (`RecompLibs.cs`, `Launcher.GameSettings.cs`
`FetchGameLibraries`), with the same rule for the NVIDIA-only files (any NVIDIA display adapter; none listed counts
as one, as `libs_sync` before its GPU list). The files are not in the zip, so an update of the runtime never touches
them (`RecompRuntime.Preserved`).

### 2.4 The three missing settings, and what each needs

**GPU picker.** `gpu_index` is an index into `vkEnumeratePhysicalDevices` order; unset means the first usable
discrete card (`fork: runtime/gfx/vk/device/vk_device.c` `pick_device`). `gpu_name` (a case-insensitive part of the
name, no menu row) also works and does not depend on the order.

- Today, without a fork change: every run, the setup's warm step included, logs the list in that order:
  `[host] vk: gpu 0: Intel(R) Graphics (type 1, api 1.4.323)`, `vk: gpu 1: NVIDIA GeForce RTX 5050 Laptop GPU (type
  2, api 1.4.341)`, `vk: using gpu 1: ...` (install: `logs\warm.log`, `logs\mkw.log`). After the first setup the
  launcher can offer the choice from `warm.log` and write `gpu_name`. Type 2 is discrete, 1 integrated, 4 CPU.
- Proper: a fork flag `mkw.exe --gpus` that prints one line per device (index, name, type, API version, VRAM, usable
  or why not) from `vk_gpu_report` and `settings_list_gpus`, before any setup. About 40 lines in `main.c`. It also
  gives the "no GPU here can run the game" warning before the player waits ten minutes.

**Key bindings and controller mapping.** `mkw.ini` keys `bind_a` ... `bind_rstick_down` under `[input]`
(`cfg.c` `sections`); the value syntax and the default tables are in `fork: runtime/input/input_map.c`, not read for
this document (not verified). The menu captures a press live. The launcher needs: the action list and value grammar
(a table in `RecompSettings.cs`, checked at each bump), a capture control for keyboard keys (WPF has it) and pad
buttons (XInput names mapped to the fork's `PAD_*` names). No fork change. Until then the in-game menu's Controls tab
is the only place: keep a way to open it (section 3.1).

**Best settings for this PC.** The rule is short (`settings_tabs.cpp` `auto_set`): take the chosen GPU, else the
first discrete one; its largest device-local heap in MB; the screen's height. No discrete card or under 6 GB:
preset `medium`, 720p. 16 GB or more: `high` at the screen's height. Between: `console`, 1080p. Never above the
screen; then `quality`, the four preset rows, `resolution`, and `upscaler = fsr`. The launcher has the presets already
(`GameSetting` options with `Sets`). It lacks the VRAM figure: `--gpus` above gives it; without it, `warm.log` has
`mem: ... heap 0 7910 MiB flags 0x1 DEVICE_LOCAL`.

### 2.5 In-game menu and what else draws over the game (`menu.cpp`, `menu.h`)

| Function | Status |
|---|---|
| The settings window (F1, Esc, Minus+Plus or Guide), "Continue", "Quit game" | The overlay replaces it as the thing a pad opens (`OVERLAY.md`). With plugin API 3 (the pin) the pad shortcut is still mkw-recomp's; with API 4 the mod swallows it. F1 and Esc always open it |
| Live settings (volume, fps, framegen, present mode...) | The launcher writes `mkw.ini` only while the game is closed. Live changes need the menu, or a new plugin API entry (`cfg_set`) |
| Notices at the top of the picture (`menu_notice`: controller battery, messages; also the plugin API's `notice`) | the runtime's own; the mod no longer posts notices (1.4.3); must stay |
| The number pad / text entry (`ui/menu/text_entry.cpp`, reached from `sdk/swkbd`) | the game's software keyboard (the Room ID search) depends on it; the probe's own keypad is gone (1.4.3); must stay |
| Plugin drawings (`plugin_overlay.cpp`, `overlay_set`) and mods' tabs (`mod_page_ui.cpp`) | the mod's settings tabs use the latter (the mod draws nothing over the game); must stay |
| Performance overlay (F10, `perf_overlay`) | setting covered; drawn by the menu code |

### 2.6 Mods page, Tools page, dialogs

| Function | Status |
|---|---|
| Mods: import (`tools/mod/import.py`), enable, delete, merge and conflict picker (`merge.py`), GameBanana browser (`gamebanana.py`), the `mkw-recomp:` URL scheme (`--gb-install`), a mod's options page | The launcher installs and enables only the MKWMP mod. Other mods: out of scope for stage 1; say so in the UI |
| Tools: back up saves (`saves.py backup --profile P --dest G\backups --name N`), open saves / backups / logs, desktop and Steam shortcuts, Check now, Undo update, Report a problem | bug reports and logs: the launcher's own. Save backup: optional, the script is callable |
| Message boxes (`SDL_ShowSimpleMessageBox`, title "mkw-recomp"): CPU without AVX2/FMA/BMI2, exit 3 (`main.c` `require_x86_64_v3`); "already running from this folder" (`boot.c` `lock_profile`); a mod's plugin did not start (`plugin.c` `plugin_problem`) | These are the screens a player can still see in stage 1. The launcher can prevent the first two: check the CPU before installing, and `IsRunning` before PLAY |
| A fatal (`rt_fatal`): `FATAL: ...` in `logs\mkw.log`, exit 70, no box | read the last `FATAL:` line for the player |

## 3. The plan

### 3.1 Stage 1: the launcher drives the setup and the game

No fork change. Works with the pinned 0.1.15-mkwmp.

**The driver** (`Targets/MkwRecomp/`, beside `RuntimeInstaller`):

1. Preconditions: the runtime is installed and verified; the game is not running; 5 GB free (27 GB for copy); the
   path of the game folder is at most 131 UTF-16 units; the CPU has AVX2, FMA and BMI2, or SSE4.2 for the
   release's `-noavx2` package (it carries `setup/cpu-level` = `v2`). The launcher installs that package on a
   processor without AVX2, FMA or BMI2 (`RecompRuntime.Pinned` → `NoAvx2`); either package counts as the pinned
   version where it runs, and a game compiled for the other level (the compile step's key ends in ` v2` or not)
   is set up again.
2. Start the command of section 0 with stdout redirected, no window, working directory the runtime folder, and the
   launcher's environment minus any `PYTHON*`. First setup: `--source <dump> --romfs link`. Otherwise neither.
   `--port` is the manifest's version, spelled as mkw.exe spells it: `0.1.15-mkwmp` (found in the zip's mkw.exe; it
   is `git describe` without the `v`, `build.py` `port_version`).
3. Read lines as section 1.2. Keep the last `step`; map `progress`; on `warm`, tail `logs\warm.log`. Remember
   `version`. Treat exit without `done` or `error` as `stopped`.
4. Cancel: kill the process tree. The job object kills the tools too. Nothing to clean: the next run resumes.
5. If no line arrives for a while at the very start, another setup may hold `setup.lock`: say so, do not time out on
   your own while a compile or a prewarm is silent (generate prints nothing for minutes).
6. After `done`: `setup-state.json` has `port` = the manifest's version and `mkwgame.id` = the runtime's id. Those
   two are the launcher's "ready" test from then on.

**When it runs**: on the Setup screen (first time); automatically after `RuntimeInstaller` changed mkw.exe or
anything under `setup\src` or `setup\lib`; before PLAY when the "ready" test fails. `TargetStatus.NeedsRebuild`
becomes "run the driver", not a message.

**The game**: keep `mkw.exe --play`. It bypasses the start screen whatever `mkw.ini` says. Also write, once, under
`[launcher]` in `mkw.ini`, and lock in the Game settings page:

- `start_screen = skip`: for a player who starts mkw.exe by hand or from Steam. It is not a guarantee: the screen
  still opens when the game is not set up, the dump is gone, another version set the folder up, a code mod is to be
  built, the last run ended in a fatal, or View/Select is held (`launcher.cpp` `console_start`).
- `update_check = 0`: so the fork's updater never moves the runtime off the pin.

**The menu**: the overlay is what a pad opens; mkw-recomp's menu stays reachable with F1/Esc and through the
overlay's "Game settings (mkw-recomp)" button on API 4. Stage 1 needs it for bindings and live settings.

**As built** (2026-11-20; `Targets/MkwRecomp/RecompSetup.cs`, `RecompSetupProgress.cs`, `Launcher.GameSetup.cs`,
tests in `GameSetupTests.cs`):

- The command, the event words and the error codes above were read again in the fork at the tag and in the zip's own
  `prepare.py` (the same text). Run from the zip: `--help` lists exactly the arguments of 1.1; with an empty `--game` and
  no `--source` it prints `error source no earlier setup in this folder` and exits 2. Nothing was run with a game.
- Done from the list above: every precondition (the CPU check with .NET's `Avx2`/`Fma`/`Bmi2.IsSupported`), the command,
  the lines, `warm.log`'s count, the three codes of the start screen, cancel by killing the process tree, the notice
  when the lock is held (no line for 10 s at the start), the "ready" test (`RecompSetup.Status`, with the linked copy
  that moved as `DumpMissing`), `update_check = 0` and `start_screen = skip` written and locked.
- Time left: the steps are weighed in seconds of the PC of 1.6 (check 4, exefs 6, romfs 1 linked or 900 copied,
  generate 160, compile 186, shaders 80 beside a build or 266 alone, warm 76, cleanup 24), skipped steps leave the
  total, and what remains is scaled by how long the finished steps really took here.
- The whole log of a run is `<launcher>\logs\setup-<stamp>.log` (the last 10 are kept): the command, each line with its
  time, and the setup's own `logs\setup.log` at the end.
- Not done: Skip for a silent `warm`; running the setup by itself after `RuntimeInstaller` (the window decides: the
  first one needs the player's copy); the mods merge and the save backup before PLAY (section 2.1).
- The tests use a stand-in script for `prepare.py`. `MKWMP_SETUP_LIVE=<unpacked runtime>` makes one test start the real
  program through the driver with a folder that is not a game: run once on Windows against the unpacked pin, it gave
  `step check`, `error encrypted <path>`, exit 1, as expected.
- Not verified: anything under Proton, and a run with a real copy of the game through the driver (every step after
  `check`).

| | |
|---|---|
| Effort | Driver, state machine and tests against recorded logs: 3 to 4 days. Setup screen wiring (pick, mode, progress, errors with the fork's 13 codes translated): 2 to 3 days. Gate change in `Recomp.cs` / `MkwRecompTarget.Check`: half a day. `gpu_name` from `warm.log` and Auto-set: 1 day. Bindings page: 2 to 3 days, can follow |
| Risks | (1) Proton: `spec.py`'s DLL injection into `nvdisasm.exe` and Python's process pools under Wine are not verified; section 5. (2) NVIDIA's download is the one network dependency the launcher does not control: a failure is `error failed spec.py failed: ...`; offer Try again. (3) A silent `warm` that hangs holds the setup up to 30 min; show the libraries count from `warm.log` and offer Skip (kill mkw.exe: the step is then marked done by `prepare.py`). (4) An antivirus that holds `mkwgame.dll.part` or the 396 objects makes `io` errors; the retry resumes. (5) The player moves or deletes the `.nup` in link mode: check before PLAY. (6) Starting mkw.exe by hand shows mkw-recomp's screen: accepted in stage 1 |
| Bump workflow adds | step "Setup contract" below |

**What `bump-mkw-recomp` must add** (the skill still names `rust/client/src/recomp.rs`; the C# homes are
`Recomp.cs`, `RecompRuntime.cs`, `RecompSettings.cs`):

1. The pin, from the release and the zip, never from memory: version, tag, asset name, `size`, `digest` (`gh release
   view vNEW -R anarqz/mkw-recomp --json assets`), the sha256 of the zip's `mkw.exe` (also into `Recomp.Known`), the
   game id (`build.py` `game_id`; also into `Recomp.GameIds`), the sum of the entries' sizes.
2. The zip's top level against section 1.8. A new top-level file or a new generated output (`nextsetup.py`
   `OUTPUTS`, `WARMED`; `release.py` `windows`; `dist.py` `WIN_DLLS`, `SOURCES`) changes the installer's lists.
3. The setup contract, by diff of `tools/prepare/prepare.py`, `machine.py`, `gamesource.py`, `gamecopy.py`,
   `shaderstep.py` and `runtime/ui/launcher/launcher_setup.cpp` between OLD and NEW: `STEPS`, the arguments, the
   event words, the error codes (`launcher.cpp` `setup_error_text` is the list the fork's own UI knows),
   `PREP_VERSION`, the lock, the log names. 0.1.16-rc already shows one such change: `tonemap`, a `done` key with no
   `step` line, and a new folder `spirv-renodx\`.
4. Whether the game id changed: if so, say in the release notes that every player rebuilds (about 3 minutes of
   compile on a fast PC, more on a Deck).
5. `main.c` `want_launcher` (`--play`, `MKW_NO_LAUNCHER`), `cfg.c` `metas` (the settings schema; `RecompSettings`
   `Runtimes`), `update.py` `REPO`.
6. `licenses\` in the zip and `runtime/third_party/README.md` against section 4.
7. A live run of the driver on Windows and on the Deck from a clean folder and as an update from the previous pin.
   Extending `tools/recomp/check_release.py` with points 1 to 3 makes them mechanical.

### 3.2 Stage 2: a fork build without mkw-recomp's UI

The runtime's build is `fork: tools/build/build.py` (it writes a Ninja file; there is no CMake for the runtime).
`runtime_sources()` compiles every `.c` and `.cpp` under `runtime/`. A flag means: one `build.py` argument that adds a
`-D` to `rtflags` and filters that list, `#ifdef`s at the few call sites, and one more job in
`.github/workflows/release.yml`. No file is deleted, so upstream merges touch the same lines as before.

Split it in two, because the two UIs are not equally removable.

**2a. No start screen** (`--no-start-screen`, `-DMKW_NO_START_SCREEN`). Clean:

- leave out `runtime/ui/launcher/*.cpp` (14 files), `runtime/third_party/imgui/backends/imgui_impl_sdl3.cpp` and
  `imgui_impl_sdlrenderer3.cpp` (included only by `launcher.cpp`; the in-game menu uses `imgui_impl_vulkan` and its
  own `menu_sdl.cpp`), and with them nanosvg and stb_image (used only by `launcher_brand.cpp` and `mods_browse.cpp`);
- `runtime/core/boot/main.c`: `want_launcher` returns 0, `run_launcher` and `launcher_refresh_romfs` are compiled
  out. main.c is the only file outside `ui/launcher/` that includes one of its headers or calls into it (checked by
  search);
- `runtime/ui/menu/settings_tabs.cpp` stays (the in-game menu uses it); the General tab's `start_screen` and
  `update_check` rows go under the same `#ifdef`;
- a not-set-up folder then ends in `FATAL: game code: the game files are not set up`, exit 70: fine, the launcher
  never starts it that way. Better: exit with its own code and one line on stdout.

**2b. No settings menu** (`-DMKW_NO_MENU`). Not a removal of ImGui: notices, the number pad, plugin drawings, mods'
tabs and the performance overlay are all drawn by `ui/menu/menu.cpp` through ImGui's Vulkan back end, and the game and
the mod use them (section 2.5; the mod only its settings tabs: since 1.4.3 it draws nothing over the game). What can go is the window itself:
`layout_menu` in `menu.cpp`, `settings_tabs.cpp`, `binds_page.cpp`, `option_rows.cpp`, and the triggers
(`menu_sdl.cpp` F1/Esc, `input.c` `input_menu_gate`). Do this only when the launcher has bindings, the GPU picker and
live settings. A smaller first step with the same effect for the player: one config value, `menu_shortcut = off`,
which stops F1, Esc and the pad shortcut from opening it (today an unknown value behaves as `minus_plus`,
`OVERLAY.md` section 1).

Rules for both: never touch `runtime/cpu/*.h` or `runtime/core/boot/gamelib.h` (the game id; that is why the fork
keeps `runtime/plugin/plugin_dispatch.h` apart), and keep `RT_EXPORTS` and the plugin API table unchanged, so the same
mod and the same `mkwgame.dll` work on both builds.

Additions worth making in the same fork release, each small: `--gpus` (section 2.4); `mkw.exe --version` printing
`rt_port_version` and `RT_GAME_ID` (so the launcher stops depending on a hash table for the game id); a plugin API
entry `cfg_set(name, value)` for live settings from the overlay.

| | |
|---|---|
| Effort | 2a: 1 to 2 days with CI and a Windows + Deck run. `menu_shortcut = off`: an hour. 2b: 3 to 5 days, after the launcher's settings are complete. `--gpus`, `--version`: half a day each |
| Risks | Every upstream release moves `launcher.cpp` and `settings_tabs.cpp` (the fork's `v0.1.15-mkwmp` to `v0.1.16-rc`: 97 files, 4,545 lines added); the `#ifdef`s at call sites are few but must be re-checked. A second binary means a second sha256 in `Recomp.Known` and a second asset unless the UI-less build becomes the only one. The probe's COFF names must be re-checked against the new binary (`check_release.py`). With no start screen there is no fallback UI when the launcher is broken: keep the full build downloadable |
| Bump workflow adds | rebase the flag commits; build both variants in CI; `check_release.py` on the UI-less zip; confirm by `strings` that no `ui/launcher` text is in it; confirm the game id is the same as the full build's |

### 3.3 Stage 3: packaging

**One installer.** The launcher installs itself and, on first run, the pinned runtime zip into a folder it owns
(`RuntimeInstaller`). Two choices for the zip: download it from the fork's release (smaller installer, the pin's
sha256 makes it safe) or carry it inside the installer (217 MB more, works offline). MIT allows both; carrying it
makes MKWMP the distributor of everything in `setup\`, so section 4's list applies in full. The dump is asked for on
the Setup screen and nowhere else.

**Steam Deck / Proton.** One Steam shortcut: the launcher (`mkwmp.exe` under Proton). It installs the
Windows zip inside its prefix or under `Z:\home\deck\...`, runs the setup there, starts mkw.exe as its child (one app
id, one prefix: what `OVERLAY.md` section 4 needs). Section 5 has the open points.

**The AppImages** (`mkw-recomp-0.1.15-mkwmp-x86_64.AppImage`, 233 MB; `-aarch64.AppImage`, 144 MB). What using them
would mean, nothing of it built:

- a native Linux runtime: `mkw` (ELF), `mkwgame.so`, the Linux `setup/` (zig cross-compiling for Linux, the Linux
  `nvdisasm`), no Wine in the game's path. Better frame times than Proton are likely but not measured here;
- the AppImage's `AppRun` copies `mkw` to `${XDG_DATA_HOME:-~/.local/share}/mkw-recomp` and links `setup/` to its
  mount (`release.py` `APPRUN`); the game data lives there unless `game_dir` says otherwise. `Recomp.DataDir`
  already maps such Linux paths to `Z:\`;
- the launcher is a Windows program under Wine. It would have to start a Linux process from inside the prefix
  (`start /unix` or `winepath`, and out of Steam's container), run the Linux `prepare.py` the same way, and watch a
  process it cannot see as a Windows process. `IsRunning`, the window helpers of the overlay and gamescope's
  one-app rule would all need a second implementation;
- the mod must be the Linux plugin (`mods/mkwmp/plugin/mkwmp.so`, already built and in `Recomp.ModLibraries`); the
  winmm probe does not exist there;
- aarch64 is for ARM handhelds; a WPF launcher does not run there at all.

So the AppImage path is a native Linux launcher's job, not this launcher's.

| | |
|---|---|
| Effort | Installer and first-run flow: 3 to 4 days. Deck: the same code, plus 2 to 3 days of live testing with the user driving Steam |
| Risks | Disk on a 64 GB Deck (about 4.1 GB peak plus the dump). The prefix is deleted when the shortcut is removed: keep the game folder outside `pfx\` or say so. A Proton update can break the toolchain under Wine after the fact: the driver's errors must name the step and the log |
| Bump workflow adds | the installer's bundled manifest (when the zip is carried); the Deck run in point 7 above |

## 4. Credits

MIT asks one thing of whoever passes the software on: the copyright notice and the permission notice go with every
copy or substantial portion. The fork's `LICENSE` (`fork: LICENSE`) has one copyright line, upstream's author's, and
adds none for the fork:

```
MIT License

Copyright (c) 2026 Adrià
```

followed by the standard permission and warranty paragraphs. The zip carries it as `licenses\mkw-recomp-LICENSE`.
`RecompRuntime.Notice` already holds the full text, and `RecompRuntime.CreditSummary` the sentence for the window.

### Wording and places

| Place | Text |
|---|---|
| About (`ShellViewModel.AboutText`, a fourth line, with a "Licenses" button) | `The game runs on mkw-recomp by Adrià and its contributors (MIT License) - https://github.com/adriadam10/mkw-recomp` |
| Setup screen, under the title, before the player picks the dump | `RecompRuntime.CreditSummary` as it is: "The game runs on mkw-recomp, a static recompilation of Mario Kart World by Adrià and its contributors (MIT License). MKWMP installs its own build of it, with the additions online play needs. mkw-recomp builds the game from your own copy, on your device; MKWMP is not affiliated with mkw-recomp or with Nintendo." |
| Setup screen, the install step | `Installing mkw-recomp 0.1.15-mkwmp (github.com/anarqz/mkw-recomp, a fork of github.com/adriadam10/mkw-recomp)` |
| "Licenses" view (About) | the full MIT text (`RecompRuntime.Notice`) and a button that opens `<runtime>\licenses\` |
| `NOTICE.md` at the repo root, a new section "mkw-recomp" | below |
| On disk | never delete or rename `<runtime>\licenses\`; the installer copies it like any other file of the zip |

`NOTICE.md` section:

```
## mkw-recomp

The MKWMP launcher downloads and installs mkw-recomp 0.1.15-mkwmp
(https://github.com/anarqz/mkw-recomp, tag v0.1.15-mkwmp), MKWMP's fork of mkw-recomp by Adrià
(https://github.com/adriadam10/mkw-recomp). mkw-recomp is under the MIT License,
Copyright (c) 2026 Adrià; its full text is installed as licenses/mkw-recomp-LICENSE and shown in
the launcher under About > Licenses. The fork adds entries to the plugin API and changes nothing
else of the game's behaviour. mkw-recomp contains no game code or assets: it builds the game on the
player's device from the player's own copy. MKWMP is not affiliated with or endorsed by mkw-recomp's
author or by Nintendo.
```

### What else a copy of the zip carries

In `licenses\` of the zip (keep all of it):

| Folder | File | Component |
|---|---|---|
| `licenses\` | `mkw-recomp-LICENSE` | mkw-recomp (MIT) |
| `SDL3\` | `LICENSE.txt` | SDL 3 (`SDL3.dll`) |
| `opus\` | `COPYING` | libopus (`libopus-0.dll`) |
| `libusb\` | `COPYING` | libusb (`libusb-1.0.dll`, LGPL-2.1: keep it a separate, replaceable DLL) |
| `libvpx\` | `LICENSE` | libvpx (linked into mkw.exe) |
| `zstd\` | `LICENSE` | Zstandard (linked into mkw.exe) |
| `xxhash\` | `LICENSE` | xxHash |
| `VulkanMemoryAllocator\` | `LICENSE` | VMA |
| `vulkan-headers\`, `vulkan-loader\` | `LICENSE` | Khronos headers and loader |

Inside `setup\`, with their own license files in place: CPython (`setup\python\LICENSE.txt`), numpy, lz4, fontTools
and pip (their `dist-info\licenses\`), zig (`setup\zig\LICENSE` and the libc licenses under `setup\zig\lib\libc\`),
and in `setup\src`: `recomp\LICENSE.totk-pc-recomp` (MIT, Copyright (c) 2026 Valiantsin Dzerakh),
`sm86\spec\LICENSE.DocumentSASS` (MIT, Copyright (c) 2022 0xD0GF00D), `sm86\*\LICENSE.nvsass-disassembler` (MIT,
Copyright (c) 2026 Mingyuan Ma).

Gaps in the zip, found by listing it. They are upstream's to fix, and matter to MKWMP only if the installer carries
the zip itself (stage 3):

- code compiled into mkw.exe whose license text is in the source tree but not in `licenses\`: Dear ImGui (MIT,
  `runtime/third_party/imgui/LICENSE.txt`), bc7enc (MIT), NXFormat (MIT), nanosvg (zlib), stb_image (MIT or public
  domain), AMD FidelityFX FSR 1 (`runtime/shaders/ffx/LICENSE.txt`), the Roboto font (Apache-2.0,
  `runtime/ui/fonts/LICENSE-Roboto.txt`), and totk-pc-recomp's part of the runtime
  (`runtime/LICENSE.totk-pc-recomp`);
- `setup\clang\clang.exe` (LLVM, Apache-2.0 with LLVM exceptions) and `setup\glslc\` (shaderc, glslang, SPIRV-Tools,
  and Microsoft's VC runtime DLLs from conda-forge's `vc14_runtime`) have no license file beside them.

If MKWMP carries the zip, add those texts to its own `licenses\` folder. This is an engineering reading of the
licenses, not legal advice.

What is never in any package of ours: the game, anything generated from it, and NVIDIA's `nvdisasm`, `ptxas` and the
spec made from them (the player's machine downloads those from NVIDIA).

## 5. Proton / Steam Deck

The launcher under Proton is the Windows build (`Mkwmp.Core.Platform.WineHost`) and installs the Windows zip. Then
every tool of the setup is a Windows program under Wine:

| Step | Windows programs started | Concern |
|---|---|---|
| all | `python.exe` (CPython 3.14, python-build-standalone) | job object (`CreateJobObjectW`, kill-on-close), `msvcrt.locking`, `GlobalMemoryStatusEx`: all standard, not verified under Proton |
| exefs, romfs, cleanup | `mkw.exe --unpack` (only for `.nx`, and at cleanup for a packed RomFS) | none expected |
| generate | `python.exe` worker processes (multiprocessing by spawn) | memory: 0.7 GB a worker; a Deck gets few workers |
| compile | `zig.exe cc -###` once, `clang.exe -cc1` x 396, `zig.exe lld-link` | process creation under Wine is slow: 400 short processes. `zig.exe` is 190 MB. `ZIG_LIB_DIR`, `ZIG_GLOBAL_CACHE_DIR`, `ZIG_LOCAL_CACHE_DIR` are set by `prepare.py` to paths inside the package and `<game>\setup-work\zig-cache` |
| shaders | `ptxas.exe`, `nvdisasm.exe` (NVIDIA's, downloaded), a DLL built by zig and injected with `CreateRemoteThread`; then `glslc.exe` thousands of times from 4 to 8 workers | **the largest unknown**: injection into a suspended process under Wine, and HTTPS from Wine's Python (`update.ca_file` falls back to pip's CA bundle). If it fails, the fallback is a fork change: let the Windows build take a spec built by a native helper. Not verified either way |
| warm | `mkw.exe --prewarm` | Vulkan through Wine's winevulkan to the Deck's RADV; under gamescope the launcher's window must keep the focus. Up to 30 min of silence is allowed by the code; expect minutes |

Running the fork's Windows binaries under plain Wine is something upstream does for tests (`fork:
tools/run/wine.sh`: its own prefix, `WINEDLLOVERRIDES="mscoree=;mshtml="`, `WINEDEBUG=-all`). The whole setup under
Proton is not something the source claims: the Deck path upstream supports is the AppImage.

**Paths.** The launcher passes Windows paths (`C:\...` in the prefix, `Z:\home\deck\...` outside it), and
`prepare.py` keeps `--game` as given (absolute, not resolved). Limits: the game folder at most 131 UTF-16 units
(`CFG_GAME_DIR_WIN32_MAX`, `cfg.h`), which a path inside
`Z:\home\deck\.local\share\Steam\steamapps\compatdata\<id>\pfx\drive_c\...` can approach; prefer a short folder such
as `Z:\home\deck\mkwmp\game`. `setup-state.json` and `mkw.ini` then hold `Z:\` paths: a folder set up under Proton is
not directly usable by the native Linux build, and the other way round, except through `game_dir` and
`Recomp.DataDir`.

**RomFS `link` mode.** It is a path in `mkw.ini`, not a link (section 1.3), so there is nothing for a Proton prefix to
support: no hard link, symlink or junction is created. What it needs is that the same path is readable every time the
game starts: a `.nup` on the SD card is `Z:\run\media\deck\<card>\...`, and Steam's container must expose that mount
to the prefix. Home and `/run/media` normally are; other mounts may need `STEAM_COMPAT_MOUNTS` (general Proton
behaviour, not verified here). The only hard links in the setup are `translate.py --base`'s, used by the in-app
update's `--next` mode, which the launcher does not use.

**Environment the fork reads** (all optional):

| Variable | Read by | Effect |
|---|---|---|
| `MKW_NO_LAUNCHER=1`, `MKW_LAUNCHED` | `main.c` `want_launcher` | no start screen |
| `MKW_GAME_DIR` | `cfg.c`; set by `prepare.py` for the prewarm | the data folder; wins over `game_dir` in `mkw.ini` |
| `MKW_<KEY>` for any setting | `cfg.c` `raw_get` | wins over `mkw.ini`; the launcher shows such a setting locked |
| `MKW_LOG_NAME` | `main.c` `log_name_own` | the log file's name (`warm` for the prewarm) |
| `MKW_MODS`, and `--mod NAME` | `main.c` | the enabled mods; `--mod mkwmp` enables ours for one run without touching `mkw.ini` |
| `MKW_SETUP_CPU=v2`, `MKW_SETUP_CC=zig` | `gamecode.py`, `fastcc.py` | x86-64-v2 game code (the pinned mkw.exe itself still needs AVX2); zig instead of clang |
| `ZIG_GLOBAL_CACHE_DIR` | `machine.py` | when set, zig's cache is not moved into the game folder |
| `MKW_UPDATE_REPO`, `MKW_UPDATE_API`, `MKW_UPDATE_AS` | `update.py` | the in-app updater's source; unused once `update_check = 0` |
| `MKW_DEVICE_PROFILE`, `SteamDeck`, `SteamGamepadUI`, `SteamTenfoot`, `GAMESCOPE_WAYLAND_DISPLAY` | `cfg.c` `on_steam_deck`, `cfg_steam_console` | the Deck's defaults (fullscreen, 1280x800, fifo, 720p) and "started from Gaming Mode". The Windows build cannot read `/sys/class/dmi`, so under Proton only `SteamDeck=1` selects the Deck profile |
| `SteamGameId`, `SteamAppId` | `cfg.c` `cfg_steam_started` | started by Steam |
| `PYTHON*` | removed by `prepare.py` | |

To test live on the Deck, in this order, before anything else of stage 1 is polished: the driver's command from a
terminal inside the launcher's prefix up to `done`; the `shaders` step alone (delete `<game>\sm86\`); `warm` with the
launcher's window in front; a second run that prints only `skip`.

## 6. Testing a fork build

The launcher runs only the mkw.exe builds it knows by SHA-256 (`Recomp.Known`). Any other one is "unknown mkw-recomp",
PLAY stays off, and an install of ours is offered the pinned release again. To try a build of the fork (its CI zip, or
a local build) on a device without changing the launcher:

1. Install the pinned release with the launcher as usual, and let its setup finish once.
2. Copy **only `mkw.exe`** from the test build over the one in that folder. Keep everything else, `setup\` above all:
   its sources are hashed into the game id (1.5), so a test build runs on the game the release set up.
3. Take its SHA-256: `(Get-FileHash mkw.exe -Algorithm SHA256).Hash.ToLower()` on Windows, `sha256sum mkw.exe` on Linux.
4. Add it to `client.toml` beside the launcher (more than one: comma-separated; a TOML array works too):

   ```toml
   [dev]
   recomp_exe_sha256 = "<64 hex digits>"
   ```

   or, for one run, set `MKWMP_DEV_RECOMP_SHA256=<hex>[,<hex>]` in the launcher's environment.
5. Start the game from the launcher (PLAY), never from mkw.exe directly: the mod, the session file and the setup gate
   are the launcher's.

A listed build is taken for the pinned version (`RecompRuntime.Pinned`, e.g. 0.1.16-mkwmp.1), on either processor
build: its game id, the mod, the game settings and the setup gate are the release's. A release's own hash stays that
release even when listed. It is said everywhere, so it is never taken for the release:

- the log: `mkw.exe <first 16 hex>: accepted as a DEVELOPMENT build (dev recomp_exe_sha256)` (the log masks a whole
  64-hex word as a token);
- "Development mkw-recomp" on the checklist's mkw-recomp row and in Settings › Game, with the full hash;
- `system.txt` of a report (`Supported("…") DEVELOPMENT build, mkw.exe <hash>`) and the crash issue's `mkw-recomp` row.

The launcher never replaces it: no update is offered for it, and an update or repair of the install (`RuntimeInstaller`)
puts every other file of the pinned release in place and leaves `mkw.exe` as it is. To go back, remove the hash: the
build is unknown again, and the checklist's INSTALL puts the release's mkw.exe back (or copy it back by hand).
The launcher writes the list back when it saves `client.toml`, and never adds one.
