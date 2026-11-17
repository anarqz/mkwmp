# mkw-recomp v0.1.12 -> v0.1.13 against the MKWMP probe (and game 1.0.0 -> 1.8.0)

Sources: `git diff v0.1.12 v0.1.13` of adriadam10/mkw-recomp (99 commits, 174 files), the official Windows `mkw.exe`,
`SDL3.dll` of both release zips (parsed with `tools/recomp/check_release.py`: COFF symbol table, sections,
imports/exports, `.ovr`, prologues with capstone), the release notes of v0.1.13, the probe at `native/probe/src`, and,
read only, one player folder that had already been updated in-app from 0.1.12 to 0.1.13 and set up for game 1.8.0
(its `mkwgame.dll`, `mkwgame.id`, `setup-state.json`, `logs/warm.log`, and the 1.0.0 and 1.8.0 `exefs` dumps).
Nothing was run by this analysis. Each statement below says where it comes from: **binary** (parsed from the
release files), **source** (read in the upstream diff or the probe), **observed** (a line of that folder's existing
logs, written by someone else's run), or it is listed under NEEDS LIVE TEST.

mkw.exe SHA-256: 0.1.12 `fbd215a5f232e9fc786bb4a7e5823d0d7a64ad04e09f50ed37b1cb2d5d66d1b1`,
**0.1.13 `1c47ffa2502a1ee92c2d01da0791802c18bf6c3365040c2b4fa1c91dacbc8695`** (33 757 696 bytes; zip sha256 `676a7ed5...`).

**Bottom line: 0.1.13 needs no different probe binary.** Every COFF name resolves exactly once, the three detour
prologues match byte for byte, the `.ovr` name list is identical, `Cpu`, `g_host`, `added[]`, `g_fibers` are where
they were, the exports and the WINMM imports are the same, and `RT_GAME_ID` did not change. The launcher must learn
the new hash (B1). **Game 1.8.0 is a different matter and is not a runtime question:** the runtime side of 1.8.0
overlaps nothing the probe uses and every override name still binds, but the probe's own hard-coded guest addresses
are 1.0.0's (B3).

Reproduce the binary part:

    .venv\Scripts\python.exe -I tools\recomp\check_release.py <0.1.12 zip or folder> <0.1.13 zip or folder> [--game-dir <set-up game folder>]

Result on these two releases: `0 FAIL, 1 WARN` (the launcher's hash list).

---

## BREAKING (must change)

### B1. The launcher refuses 0.1.13 (not the probe)
- `rust/client/src/recomp.rs:20-24` `KNOWN` has no entry for `1c47ffa2...`, so 0.1.13 is `Recomp::Unknown`
  ("Unknown mkw-recomp: MKWMP needs 0.1.10 or 0.1.12", `:48`). Add
  `("0.1.13", "1c47ffa2502a1ee92c2d01da0791802c18bf6c3365040c2b4fa1c91dacbc8695")` to `KNOWN`, `"0.1.13"` to
  `SUPPORTED` (`:14`), decide `RECOMMENDED` / `DOWNLOAD` (`:16-17`), and update the test strings at `:141-142`.
- User-facing text that names versions: `README.md:30` ("mkw-recomp 0.1.10 or 0.1.12 ... game, version 1.0.0"),
  `site/index.html:76`, `:90` ("0.1.10"), `tools/release/notes.md`.
- (Comment only: `recomp.rs:3` says mkw.exe carries no version string. It does: `rt_port_version`, logged as
  `log: ... (MKW Recomp 0.1.13)` by `runtime/core/boot/main.c:165`; binary: the whole string `0.1.13` is in `.rdata`
  of the 0.1.13 exe, `0.1.12` in the 0.1.12 one, `0.1.10` in the 0.1.10 one. The hash stays the stricter gate; the
  string is `git describe`, so an unofficial build can carry the same one.)

### B2. Probe source for the runtime: nothing
No probe file needs an edit for 0.1.13 to load, resolve, register and detour (binary, S2-S6). Observed besides: the
shipped probe (`winmm.dll` sha256 `8dd5e1d7...`, the 1.0.2 build) was loaded by the 0.1.13 mkw.exe during that folder's
setup warm-up, and its `logs/warm.log` holds `mkwmp: dormant mode: 330 stock handlers in .ovr`,
`mkwmp: npln: 22 overrides gated (dormant until Online)` and one `mkwmp: dormant call:` line, the same first two lines as
the same folder's 0.1.12 `mkw.log`. That covers COFF resolution, `.ovr` reading and registration; it does not cover
going online, the keypad or the rt_dispatch hook (T2, T8).

### B3. Game 1.8.0: the probe's guest addresses are 1.0.0's (probe work, independent of 0.1.13)
The probe has no game-version test (no build-id or version check in `native/probe/src`), and 1.8.0 is another
executable: `main` is 0x37f0000 long instead of 0x3410000, the modules after it sit elsewhere (observed, `warm.log` /
`mkw.log`):

| module | game 1.0.0 | game 1.8.0 |
|---|---|---|
| main | 0x7000000000, size 0x3410000 | 0x7000000000, size 0x37f0000 |
| subsdk0 | 0x7003600000 (size 0x8d9000) | 0x7003800000 (size 0xe0000, a new module) |
| subsdk1 | - | 0x7003a00000 (size 0x8f0000, 1.0.0's subsdk0 by size and imports) |
| sdk | 0x7004000000 | 0x7004400000 |
| patch page | 0x7003410000 | 0x70037f0000 |

Every `0x70...` constant of the probe therefore names something else on 1.8.0 (source; none was re-derived here):
- `voice_ui.c:23-27`, `:215-216`: `VT_ONLINE_PAUSE = 0x3248970` slots 7 and 11, `FN_PLATE_TEXT = 0x21deb68`.
  `guesthook_vtable` (`guesthook.c:37-63`) only checks that the slot is committed memory; on 1.8.0 main+0x32489a8 and
  main+0x32489c8 are inside `main`, so it would overwrite 16 bytes of whatever 1.8.0 keeps there and log "installed".
  This one writes; it must be gated or re-derived before any 1.8.0 run with the voice UI on (`MKWMP_VOICE_UI=0` turns it
  off, `voice_ui.c:209-213`).
- `npln.c:118`: the gRPC call site `0x700104a8bc` whose `pthread_cond_timedwait` deadline is converted; on 1.8.0 the
  test never matches (falls through to the original), so the conversion is simply lost.
- `npln.c:1247-1375` (`profile_dispatch`, on every dispatch once the IAT hook is in): unconditional comparisons of
  `c->pc` / `x30` against 1.0.0 addresses followed by reads through guest registers without `guest_readable`:
  `0x7001e3f91c` (`:1269`, memcpy from x1), `0x700261f894` / `0x700261f044` (`:1278-1288`, reads controller+0x4a8 and
  the global at `0x70033d2548`), `0x7001a93138` / `0x7001a79fa0` (`:1291-1294`, seq+0x28), `is_pia_join_step`
  (`:1000-1008`, job+0x38), `0x7001e463dc`/`0x700146a200` (`:1265`), and the error-service slot `0x700222d78c`
  (`:1307`, `:1342-1355`: the idle-kick / referee-timeout skip). On 1.8.0 these are other functions with other
  arguments: wrong logs at best, a fault or a skipped unrelated call at worst.
- `npln.c:1172`, `:1287`: the global `0x70033d2548` (inside 1.8.0's `main` too, another object).
- `npln.c:1259`: `0x70046966a0` is sdk+0x6966a0 on 1.0.0 and sdk+0x2966a0 on 1.8.0.
- Trace-only (behind `MKWMP_TRACE_*`): `npln.c:1022-1043`, `:1185`, `:1254-1255`.

What does carry over to 1.8.0 unchanged: everything resolved by name (S5, G3).

---

## NEEDS LIVE TEST

### T1. First start after the update (no rebuild forced this time)
- `RT_GAME_ID` is `c0dd6cf12c07c23a` in both exes (binary, `.rdata`), because neither `runtime/cpu/*.h` nor
  `gamelib.h` is in the diff and `GEN_FLAGS` is unchanged (`tools/build/build.py:152-160`; the only change strips CRs
  before hashing). A `mkwgame.dll` built by 0.1.12's setup passes `gamelib.c:17`; observed: the updated folder's 1.0.0
  `mkwgame.id` (built 15/11 by 0.1.12) is `c0dd6cf12c07c23a`. So `mkw.exe --play` (`rust/client/src/launch.rs:88`) does
  not hit "built for another version of the runtime" after this update, unlike 0.1.10 -> 0.1.12.
- The start screen still sets up again when it is opened (the port version differs, and `patches-mkw*.txt` are part
  of the generate key, `tools/prepare/prepare.py` GENERATOR): until then the old game code runs without the new
  `vertplus` set, which only matters for a tall `MKW_ASPECT=window` (`patches.c:374-377` logs "tall Window needs
  regenerated game code").
- Test: update a 0.1.12 folder in-app, press Play in the MKWMP launcher without touching recomp's start screen: the
  game starts; then run recomp's setup once and repeat.

### T2. The rt_dispatch IAT hook
- Binary: mkw.exe 0.1.13 exports the same 7 names (`fnprof_hit rt_dispatch rt_sj_owner rt_spin_gate rt_svc rt_ticks
  rt_udf`, module name `mkw.exe`); a `mkwgame.dll` built by 0.1.13's setup for game 1.8.0 imports exactly those 7
  from `mkw.exe` by name, so the slot `npln.c:1388-1418` patches exists. `tools/prepare/gamecode.py` changes only what
  is recompiled (per-chunk header keys), not how the DLL links (`lib/libmkw.dll.a`).
- Test: go online once; `mkwmp.log` shows "BulkGetDocuments and Pia join tracing installed".

### T3. Automatic frame rate changed again (abb6f09, `runtime/core/boot/game.c:99-126`, `:146`; `fps_auto.c:19-25`, `:74-78`)
- 0.1.12: `MKW_FPS=auto` held one whole interval 1-4 chosen by the runtime. 0.1.13: while 30 fps fits, `auto_want`
  returns 0 and the game picks 1 or 2 per frame itself (the 0.1.10/0.1.11 behaviour, what the probe's race-end timing
  was tuned on, `npln.c:1208`, `:1331-1347`); the runtime's 3 or 4 apply only below 30 fps and only while
  `race_scene_on()`; with frame generation on, the whole interval is kept. A stall no longer moves the interval for up
  to 20 s.
- Observed (warm-up of 1.8.0 on a PC, title only): `game: auto interval 1 -> 2 ...` lines keep appearing and
  `nvn: present interval 2 -> 1 (from main+0x69ce28)`.
- Test: online race, Deck against PC, default settings; grep `mkw.log` for `race: in a race`, `game: auto interval`,
  `game: speed`. If it desyncs or kicks, pin `MKW_FPS=game` (unchanged meaning).

### T4. Discord Rich Presence (#455; new `runtime/core/presence/`, `boot.c:370`, `fs_host.c:904`)
- Off by default (`MKW_DISCORD`, `cfg.c`). Always: `presence_start()` makes one more runtime thread at boot, and every
  file open goes through `presence_file_opened` (name compares and atomics only). The probe's `nn::fs::OpenFile`
  override delegates to the stock `ov_open_file` (`npln.c:1617`, never gated), which is where the new call sits, so
  it is reached either way.
- Test: one online session with `MKW_DISCORD=1` and Discord running; nothing expected in `mkwmp.log`.

### T5. Classic VS (#452; `runtime/core/hacks/classic.c`, `MKW_CLASSIC_VS`, off by default)
- Rewrites each area's routes into laps on the destination course, in the `general_format` archive and in the game's
  memory. Whether online VS/room races read those routes is not known from source; two players with different
  settings would then load different races. Same question as `MKW_CLASSIC_GP` since 0.1.12.
- Test: a friend room with one player on `MKW_CLASSIC_VS=1`. If it breaks, force `MKW_CLASSIC_VS=0` and
  `MKW_CLASSIC_GP=0` from the launcher's environment (the environment wins over mkw.ini).

### T6. Vert+ and the new patch-page host (#450; `patches.c:31`, `:228-244`, `:504`; both patch files)
- New host function `host_camera_fov` at patch page + 0xf30 (registered whenever the game has patch sets), new set
  `vertplus` (1.0.0 site main+0x45fba4, 1.8.0 main+0x894a4), on only for `MKW_ASPECT=window` in a window taller than
  16:9. A Steam Deck (1280x800) is such a window. One more `g_host` entry (cap 8192); no address the probe uses (G2).
- Test on a Deck with `MKW_ASPECT=window` after recomp's setup regenerated the game code: online race, online pause
  menu / voice UI and the keypad still placed right (the 2D fit changed: `aspect_ui_fit`, `patches.c:288-291`).

### T7. Threads and affinity (#449, #480; `vk_backend.c:276-303`, `runtime/platform/win32.c:105-144`)
- With 6 or more physical cores the race frame is now recorded on several threads by default (`MKW_REPLAY_PAR`), and
  the replay thread takes the last of the fastest cores; `plat_process_affinity` gives every other thread of the
  process, the probe's and `mkwmp-voice.dll`'s included, all processors but that core's (the call existed in 0.1.12;
  which core changed). Performance only as far as source shows.
- Test: voice chat during an online race on a hybrid Intel CPU and on a 6-core machine: no new audio gaps.

### T8. Keypad and room-id answer
- Hooks install by construction (S4) and the seven ImGui symbols resolve. `menu.cpp` now calls
  `ui_id_check("in-game menu")` before `ImGui::NewFrame` (`runtime/ui/menu/id_check.cpp`): it logs
  `ui: N widgets share the ImGui ID ...` when two items under the mouse share an ID. The keypad draws on the
  foreground draw list without items, so no line is expected from it.
- Test: join by room code; `mkwmp.log` "keypad: hooks installed"; no `ui:` line in `mkw.log` while the keypad is open.

### T9. The setup's warm-up runs with the probe (2a71904; `tools/prepare/prepare.py warm`, `main.c:113-139`)
- The warm-up starts `mkw.exe` from the exe folder with `MKW_GAME_DIR`, `MKW_PROFILE_DIR=<work>/warm-profile`,
  `MKW_ASSUME_FOCUS=1` and now `MKW_LOG_NAME=warm`, so its log is `logs/warm.log` and the player's `mkw.log` rotation
  is left alone (good for `rust/client/src/support.rs:74`). The proxy `winmm.dll` is in that folder, so the probe
  loads in the warm-up too (observed, B2). Observed in the same folder: `mkwmp.log` beside mkw.exe is 0 bytes with
  the warm-up's time, i.e. a setup run appears to replace the player's last `mkwmp.log`.
- Test: note `mkwmp.log` before an in-app update + setup and look at it after; decide whether the probe should stay
  out when `MKW_LOG_NAME` is set (plain `getenv`, not a cfg key).
- Observed, about 0.1.12's T9: after the in-app update 0.1.12 -> 0.1.13 the folder still has `winmm.dll`,
  `winmm_host.dll`, `mkwmp.toml`, `mkwmp-voice.dll`, `mkwmp-lobby-ca.pem`; the previous runtime is in
  `update-previous/` (`version` = 0.1.12). `launcher_update.cpp` is not in this diff.

### T10. Game 1.8.0 with the probe (after B3)
- Dormant boot: observed fine (B2: the warm-up reached the title with 22 gated overrides on 1.8.0).
- Online: every address-dependent part of B3; the new callers of G3 (1.8.0's `subsdk0` calls `nn::socket::Close`,
  `GetLastError`, `EventFd*` and `nn::fs` through the probe's overrides once active); `camera` set and GameChat
  (`MKW_CAMERA_SKIP`, `patches.c` `host_camera_event`): 1.8.0's online pause menu may differ from the one
  `voice_ui.c` was written against (VOICE-RE.md is 1.0.0).
- The game folder is no longer the exe folder in that setup (`mkw.ini` `game_dir = ...\ROM\updates\917504`): 0.1.12's
  T5 applies as written (probe files beside mkw.exe, `mkw.log` in the game folder).

---

## Game 1.8.0: runtime paths in this diff

### G1. What upstream changed for 1.8.0 (source)
- `recomp/versions.py`: **not in the diff**. Still 1.0.0 `9e244438c3d8ef1c` and 1.8.0 `d00e4f399e411d06`; binary:
  those are the build ids of the two local `exefs/main` dumps.
- `recomp/patches-mkw-180.txt`: the `output` set moved from main+0xb87b9c/0xb87ba8 (a display-mode table, wrong) to
  GraphicsSystem's constructor main+0xdce850/0xdce854 (1.8.0 fixes 3840x2160); new `vertplus` set, one site
  main+0x894a4 (`bl host_camera_fov`). `patches-mkw.txt` (1.0.0) gains only its `vertplus` site main+0x45fba4.
- `runtime/core/boot/version.c:70-101` (the `.number = 180` row): new `output_lines = 2160`, `final_at_screen = 1`,
  `aspect_first_lr = 0x8a6d8`, `fov_entry_x = 20`, `fov_half_v = 14`; every older field of the row is unchanged
  (`frame_update 0x69cbb0`, `frame_slot 0x367aac0`, `race_events 0x3534d90..0x354a5d8`, `camera_service 0x378ecb8`).
- `runtime/core/boot/patches.c:78-79`: for `number >= 180` the second render-buffer pool also holds a whole shadow map
  (photo mode, #440); `:405-441` output size follows `output_lines` / `final_at_screen`.
- Graphics for 1.8.0 (no symbol or address the probe uses): constants pushed to bank c24 (#423), the photo mode
  compute push, the shadow map released when written over (#468, `vk_tex_evict.c`), `classic.c` sized for 1.8.0's 202
  routes.
- Observed on 1.8.0 with 0.1.13: `patches: lan off, zstd on, output off, camera on, aspect off, vertplus off, shadow
  off, seed off (page 0x70037f0000)`.

### G2. Addresses: upstream 1.8.0 against the probe
- Numerically, none of the probe's 50 guest constants is within 0x100 of any of the 214 `main` sites of
  `patches-mkw-180.txt` or of a `version.c` address, except the range bound 0x1700000 (`npln.c:1023`, a trace range)
  near the spin site main+0x170005c. 14 of 1.8.0's sites lie inside the probe's trace ranges (`npln.c:1022-1030`),
  which mean nothing on 1.8.0 anyway (B3). The probe's two vtable slots (main+0x32489a8, +0x32489c8) are not among the
  slots `race_scene.c` rewrites for either version.
- Host addresses the runtime registers are on the patch page (0x70037f0000 + 0xe00..0xff0 on 1.8.0, new: +0xf30);
  the probe's are 0x0FF0'0000'0000 and up (`guesthook.c:13`): no collision, and `dispatch.c:151-154` only rejects
  host addresses inside a module.
- Layout base: `recomp/layout.py` is not in the diff (`FIRST_BASE = 0x7000000000`, 2 MiB alignment); `GUEST_MAIN()`
  is still main's base on both game versions.

### G3. Override names on 1.8.0 (binary: the dynsym of both exefs dumps, parsed with a scratch NSO reader)
- All 64 `kRegs` names (`npln.c:1598-1668`), the two core-mask names (`:1737-1738`) and the six names the probe looks
  up with `ldr_symbol` are **defined by 1.8.0's `sdk`**, as by 1.0.0's. None is absent or renamed.
- Importers changed: 1.0.0's `subsdk0` is 1.8.0's `subsdk1` (same imports: `pthread_cond_timedwait`, `clock_gettime`,
  `__errno_location`), and 1.8.0's new `subsdk0` (806 defined symbols) imports `nn::fs::OpenFile/GetFileSize/CloseFile`,
  `nn::socket::GetLastError/Close/EventFd/EventFdRead/EventFdWrite` and the `nn::os::*SystemEvent` functions. An
  override applies by name (`hostfn.c:63-66`), so those calls reach the probe's handlers too: the fs ones delegate, the
  socket ones have stock handlers and are gated until Online (S5). New code path under the probe's socket layer: T10.
- mkw.exe's `.ovr` list is the same for both game versions (it is the runtime's, S5).

---

## SAFE (checked, unchanged)

### S1. Toolchain and image ("Windows release built on Windows itself", #396)
- Source: #396 makes `build.py`, `windeps.py`, `dist.py --target windows` runnable on Windows (response files, drive
  letters, llvm-mingw's Windows zip of the same pin `20261020`). The release itself is still built by
  `.github/workflows/release.yml` job `windows` on `ubuntu-latest` in `archlinux:latest`, as 0.1.12.
- Binary: same linker version field (14.0), same image base 0x140000000, same section list in the same order
  (`.text .rdata .buildid .data .pdata .fns .obs .ovr .tls .rsrc .reloc` + DWARF), same 26 imported DLLs in the same
  order (UCRT `api-ms-win-crt-*`, `WINMM.dll` before `SDL3.dll`), COFF symbol table present
  (NumberOfSymbols 26892 -> 26992). `.text` 0x724096 -> 0x72dcc6, `.data` 0x3517d38 -> 0x3598d38.

### S2. COFF names
- All 31 names of `coff.c:12-47` are in 0.1.13's table, each exactly once, same section and storage class;
  `rt_dispatch`, `ov_show_keyboard`, `ovr_start/ovr_stop` too.

| symbol | 0.1.12 RVA | 0.1.13 RVA |
|---|---|---|
| hostfn_add_override | 0x3A260 | 0x3CD30 |
| ldr_count / ldr_symbol | 0x16D00 / 0x16EB0 | 0x16E50 / 0x17000 |
| rt_log / rt_call / cfg_get | 0x1A2D0 / 0x3AD90 / 0xE260 | 0x1A420 / 0x3D860 / 0xE260 |
| ov_open_file / ov_entry_type / ov_read_file (static) | 0x15DC00 / 0x15E0D0 / 0x15E160 | 0x1673F0 / 0x1678D0 / 0x167960 |
| ov_file_size / ov_close_file (static) | 0x15DEA0 / 0x15DF70 | 0x1676A0 / 0x167770 |
| kern_fiber_switch / kern_fiber_initialize | 0x302F0 / 0x2FE50 | 0x310F0 / 0x30C50 |
| fiber_switch_to (static) | 0x303D0 | 0x311D0 |
| plat_fiber_new / plat_fiber_free | 0x12BA50 / 0x12BAE0 | 0x133A60 / 0x133AF0 |
| kern_cpu / kern_core_away / kern_core_back | 0x2B330 / 0x2CB60 / 0x2CBF0 | 0x2C130 / 0x2D960 / 0x2D9F0 |
| g_fibers / g_host (.data) | 0x1870220 / 0x1ACE1D0 | 0x1881740 / 0x1AE2290 |
| rt_dispatch | 0x39B70 | 0x3C640 |

- First instructions of every function in the table: identical bytes, or identical instructions with only
  rip-relative displacements different (capstone).
- Source: `runtime/cpu/*` (hostfn.c, dispatch.c, cpu.h, sjlj.c), `runtime/core/kernel/*`, `runtime/core/boot/loader.c`,
  `runtime/sdk/swkbd/*`, `runtime/sdk/ov/*`, `runtime/ui/menu/menu_notice.*`, the vendored ImGui and the input code
  are **not among the 174 changed files**. `runtime/sdk/fs/fs_host.c` gains one include and one line
  (`presence_file_opened(path)` at the top of `open_file`). `cfg.c` only adds keys; `host.h` adds two fields at the end
  of `FpsAuto` and one prototype.

### S3. Struct layouts
- `Cpu`: `runtime/cpu/cpu.h` unchanged (`x@0, sp@256, pc@264, thread@312, sj@344, v@512`) = `recomp.h:19-25`. Binary:
  `rt_dispatch` starts `48 8B 81 38 01 00 00` (`mov rax,[rcx+0x138]`) in both exes.
- `g_host`: `dispatch.c` unchanged (`HOST_CAP 8192`, `{addr, fn}`, the hash of `guesthook.c:23`); span to the next
  symbol 0x20000 in both exes.
- `added[]`: `hostfn.c` unchanged (`MAX_ADDED 64`, searched before `.ovr`); span 0x400 in both. The probe's budget
  asserts (`npln.c:1681-1683`) still describe it.
- `g_fibers`: span 0x2000 in both; kernel untouched.

### S4. Detours (`keypad.c:45-51`, `:419-429`)

| function | 0.1.12 RVA | 0.1.13 RVA | first bytes in 0.1.13 | probe signature | match |
|---|---|---|---|---|---|
| text_entry_run | 0x197B20 | 0x1A1320 | `41 57 41 56 56 57 53 48 83 EC 20 4C 89 CE` | kEntrySig (14, steal 7) | yes |
| menu_notice_shown | 0x27E190 | 0x287D60 | `56 48 83 EC 20 48 8B 35` | kShownSig (8, steal 5) | yes |
| menu_notice_draw | 0x27E1C0 | 0x287D90 | `56 57 48 81 EC 78 01 00 00 C5 78 29 84 24` | kDrawSig (14, steal 9) | yes |

- The stolen bytes are whole, position-independent instructions in 0.1.13 (capstone).
- The seven ImGui symbols resolve (0x1C2230, 0x1FE3A0, 0x1FE2B0, 0x1FF750, 0x1BB240, 0x1D63C0, 0x1C2450).
- `menu.cpp` diff: `ui_id_check_init()` after the context is made, `ui_id_check("in-game menu")` before `NewFrame`,
  a comment; `menu_notice_shown()` / `menu_notice_draw()` are called as before.
- `detour.c alloc_near` (+-0x70000000) is unaffected by the 0xB0000 larger image.

### S5. Override names and precedence
- `.ovr`: same size 0x14C0, **330 names, the list identical in the same order** (binary). 37 of the 64 `kRegs` names
  have a stock handler, as before; the probe logs "22 overrides gated" on both (observed): `npln.c:1718`, `:1727-1729`
  leave the fiber pair unregistered and never gate the fs / account / CancelNetworkRequest ones.
- No override was added, removed or renamed; `runtime/sdk` has no change besides `fs_host.c`'s line.
- Precedence: `hostfn.c:63-66` unchanged, `added[]` first.

### S6. Environment, ini, loading
- `MKW_ASSUME_FOCUS`, `MKW_MESH_COMPUTE`, `MKW_SWKBD_ANSWER`, `MKW_SWKBD_TEST`, `MKW_PROFILE_DIR`, `MKW_RESOLUTION`,
  `MKW_LAN`, `MKW_CTL`, `MKW_TRACE`, `MKW_GAME_DIR`, `MKW_PLAYER` are still in `cfg.c known[]` (binary: the bare keys
  are strings of the exe). New keys: `ASTC_BC1, ASTC_BC7, CLASSIC_VS, DISCORD, DISCORD_APP, DUMP_DRAWS_SKIP,
  QUEUE_WAITS, TEX_OVERWRITTEN`; new names outside `known[]`: `MKW_LOG_NAME` (T9, plain `getenv`) and `MKW_REPLAY_PAR` (both strings of the exe),
  `MKW_VERTPLUS_PATCH` (the patch set's switch, from the patch file), `MKW_PUSH_CONST` (release notes only).
  `cfg_get` precedence unchanged.
- `--play`, `--launcher`, `MKW_LAUNCHED`, `MKW_NO_LAUNCHER`: `want_launcher` (`main.c:557`) unchanged; new mode
  `--probe-discord`.
- mkw.exe imports `WINMM.dll!timeBeginPeriod` only, SDL3.dll `timeBeginPeriod` + `timeEndPeriod`: both forwarded by
  `native/probe/src/winmm.def`. Import order unchanged.
- `SDL3.dll`: rebuilt (bytes differ), still 3.4.18 (observed "gfx: SDL 3.4.18"), 1271 exports in both, all ten names
  the probe resolves present (`dllmain.c:53-56, 79-82, 95-96`, `keypad.c:112-116`, `voice_ui.c:65-68`).
  `libusb-1.0.dll`, `libopus-0.dll`: same sizes.
- Platform: `win32.c` adds `plat_cpu_core_of` / `plat_cpu_fast` (T7), `win32_sys.c` a named-pipe client for Discord,
  `win32_mem.c` implements `plat_mem_populated` with `VirtualQuery` (frame captures only). No new process-wide hook,
  handler or constructor.
- Networking: no change in `runtime/sdk` (sockets, nifm, LAN). The only new network-ish code is the local Discord pipe.
- Start screen (`launcher.cpp`): controller navigation of its buttons, a GitHub link, ID pushes; not reached with
  `--play`.

---

## Files
- This report: `resources/docs/RECOMP-0.1.13.md`
- Checker: `tools/recomp/check_release.py` (usage in its docstring; `--ovr-dump DIR` writes the two `.ovr` lists)
- Scratch (job tmp, not in the repo): `scripts/nso_names.py` (G3), `scripts/overlap.py` (G2)
