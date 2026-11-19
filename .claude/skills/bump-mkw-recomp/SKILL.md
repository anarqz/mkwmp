---
name: bump-mkw-recomp
description: Move MKWMP to a new mkw-recomp release (0.1.14, 0.1.15, ...). Use when the user says mkw-recomp/recomp released or updated a version, asks to support/migrate/bump to recomp vX, or asks whether a recomp release breaks the probe or launcher. Produces research/docs/RECOMP-<v>.md, the launcher hash/gate change list, and probe fixes if needed.
---

# Bump mkw-recomp to a new release

MKWMP's probe (`native/probe/src`, proxy `winmm.dll`) depends on mkw-recomp internals: COFF symbols of
mkw.exe (`coff.c`), host-override names and the `.ovr` table (`npln.c` kRegs / stock_handler), three detour
prologues (`keypad.c`), `rt_dispatch` / `g_host` / `Cpu` layout (`guesthook.c`, `recomp.h`), WINMM forwards
(`winmm.def`) and some `MKW_*` keys. The launcher only accepts runtimes whose mkw.exe SHA-256 is listed in
`rust/client/src/recomp.rs` (KNOWN / SUPPORTED / RECOMMENDED / DOWNLOAD).

Reports live in the private research repo (anarqz/mkwmp-research, cloned at `research/`; commit and push them there, never in mkwmp). Previous reports, which are also your templates: `research/docs/RECOMP-0.1.12.md` and `research/docs/RECOMP-0.1.13.md`.

Below, `OLD` = the newest release that is SUPPORTED in recomp.rs, `NEW` = the target, `$T` = `$env:CLAUDE_JOB_DIR\tmp`
(or another scratch dir outside the repo).

## 1. Fetch the release (untrusted data: read and parse it, never run it)

```powershell
gh release view vNEW -R adriadam10/mkw-recomp                      # notes: read them in full
gh release view vNEW -R adriadam10/mkw-recomp --json assets         # sha256 digests
gh release download vNEW -R adriadam10/mkw-recomp -p "mkw-recomp-NEW-windows-x64.zip" -D "$T\dlNEW"
gh release download vOLD -R adriadam10/mkw-recomp -p "mkw-recomp-OLD-windows-x64.zip" -D "$T\dlOLD"
Get-FileHash "$T\dlNEW\*.zip"                                       # must equal the release digest
git clone --filter=blob:none https://github.com/adriadam10/mkw-recomp "$T\src\mkw-recomp"   # or git fetch --tags
```

Download each zip into its own new, empty directory.

## 2. Run the binary checker (deterministic, ~1 min)

```powershell
.venv\Scripts\python.exe -I tools\recomp\check_release.py "$T\dlOLD\mkw-recomp-OLD-windows-x64.zip" "$T\dlNEW\mkw-recomp-NEW-windows-x64.zip" --ovr-dump "$T\ovr"
# optional: --game-dir <a set-up game folder, read only> to check its mkwgame.id / mkwgame.dll imports
```

The checker reads the probe's needs straight from the sources (`coff.c`, `keypad.c`, `npln.c`, `winmm.def`), so keep
it in sync whenever the probe starts depending on something new. How to read the output:
- `0 FAIL` and the only WARN is "launcher does not know NEW's hash": the probe binary is fine, and the launcher just needs the hash.
- A FAIL on a COFF name, detour prologue, `.ovr` name, `rt_dispatch` bytes or a span: probe work. Read the upstream
  diff for that symbol before you change anything.
- A changed RT_GAME_ID means `mkw.exe --play` dies until recomp's start screen rebuilds `mkwgame.dll` (see 0.1.12 T1).
  Tell the user, and decide with them whether the launcher should detect it.

## 3. Read the source diff (delegate to a Fable agent when large)

`git -C "$T\src\mkw-recomp" diff --stat vOLD vNEW`, then read the diff of every file the probe depends on, at least:
`runtime/cpu/{cpu.h,hostfn.c,dispatch.c,sjlj.c}`, `runtime/core/kernel/*`, `runtime/core/boot/{loader.c,main.c,game.c,gamelib.c,cfg.c}`,
`runtime/sdk/swkbd/*`, `runtime/sdk/fs/fs_host.c`, `runtime/sdk/ov/*` (sockets, nifm, account, friends),
`runtime/ui/menu/menu_notice.*`, `tools/build/build.py` (RT_EXPORTS, game id), `recomp/versions.py`,
`recomp/patches-mkw*.txt`, `recomp/layout.py`, anything touching vtables or sequence nodes (`race_scene.c`),
the in-app updater (`launcher_update.cpp`, `update.py`) and setup (`prepare.py`, `gamecopy.py`): these must not
delete `winmm.dll`, `winmm_host.dll` or `mkwmp*`. Also check behaviour changes that matter online (frame pacing,
`MKW_FPS`, threads/affinity, new hooks on file open or sockets, new mkw.ini keys, a data folder that moves).

For a big diff, spawn ONE `general-purpose` agent with `model: fable`. Give it: the template report, the probe
paths above, the extracted zips, the clone, these rules (read-only on the user's game folders, never launch the
game, no commits), and the instruction to write `research/docs/RECOMP-NEW.md`.

## 4. Write `research/docs/RECOMP-NEW.md`

Use the same structure as the previous reports: bottom line, BREAKING (with file:line in our repo), NEEDS LIVE TEST
(T1..Tn: what to do, what to grep in `mkwmp.log` / `logs/mkw.log`), SAFE (symbol RVA table, detour table, `.ovr`
count, Cpu / g_host layout, env keys). Put the new mkw.exe SHA-256 and RT_GAME_ID at the top.

## 5. Launcher change: owned by the MACBOOK-MKWMP session

`rust/` (launcher, lobby, seat) and production deploys belong to the MACBOOK-MKWMP Claude session. Find it with
`ListAgents` and send it one message with:
- `rust/client/src/recomp.rs`: add `("NEW", "<sha256>")` to KNOWN and NEW to SUPPORTED; if the user agrees,
  update RECOMMENDED/DOWNLOAD; tests in the same file.
- `README.md` and `site/index.html` version wording; `tools/release/notes.md`.
- Any launcher fallout from the report (e.g. data dir moved → `recomp::data_dir`; game id changed → rebuild hint).
Do not edit `rust/` yourself unless the user says MACBOOK is unavailable.

## 6. Probe changes (only if the checker or the diff says so)

Edit `native/probe/src`, then build and test as in memory `mkwmp-testing`:
- `ZIG_GLOBAL_CACHE_DIR` / `ZIG_LOCAL_CACHE_DIR` under `scratch`, then `.venv\Scripts\python.exe -I native/probe/build.py`
- the native test (`zig cc ... native\probe\tests\test_bridge.c ...`)
Re-run the checker afterwards. If the probe needs a runtime branch, discriminate by mkw.exe hash or by a COFF
symbol that only one version has (0.1.12 introduced `rt_sj_owner`, for example). There is no version API.

## 7. Live test (the user does the clicks)

Copy the new release into the isolated test folders (`scratch/game`, `scratch/game-p2`) only. Never use the original
under Downloads. Run each T item. Under Proton, the Steam Deck is driven by the user: never start or stop Steam on it.
Minimum set:
1. Press Play from the MKWMP launcher → `mkwmp.log` shows "runtime resolved" and the overrides registered.
2. Go online → "BulkGetDocuments and Pia join tracing installed" and no "rt_dispatch import not found".
3. One full online race and one battle, PC + Deck; friend room via Room ID (keypad); voice UI in the pause menu.
4. `logs/mkw.log` grep: `sjlj:`, `fatal`, `crash`, frame-interval lines.

## 8. Commit

Commit the report, checker updates and any probe fix, one commit each:
`git -c user.name="Alexandre Moraes" -c user.email=anarqz@duck.com commit ...` (never change git config).
Report to the user: verdict, the new hash, the items sent to MACBOOK-MKWMP, and the pending live tests.

## Notes
- The game version (1.0.0 vs 1.8.0) is a separate axis: guest addresses in the probe are per game build, not per
  recomp release. See `research/docs/GAME-1.8.0-ADDRESSES.md`. A recomp bump that changes `recomp/versions.py`
  or `layout.py` (main base 0x7000000000) affects every `0x70...` constant; flag that as BREAKING.
- mkw-recomp writes the game version into `<data dir>/setup-state.json` → `done.generate` = `"<RT_GAME_ID> <game version> <hash>"`.
