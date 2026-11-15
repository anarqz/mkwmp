# mkw-recomp v0.1.10 -> v0.1.12 against the MKWMP probe

Sources: `git diff v0.1.10 v0.1.12` of adriadam10/mkw-recomp (184 commits, 245 files; clone in
`scratchpad/mkw-recomp-src`), the official Windows `mkw.exe` of both releases (COFF symbol table, sections,
imports/exports parsed with `scratchpad/tools/pecheck.py` and `scratchpad/tools2/chk.py`), `SDL3.dll` and
`libusb-1.0.dll` from both zips, and the probe at `/Users/anarqz/Projects/mkwmp/native/probe/src`.
Nothing was run; no mkwgame.dll of 0.1.12 exists here (the player's setup builds it), so everything about it is
from source.

mkw.exe SHA-256: 0.1.10 `4fe39fc7...febcbdcb`, 0.1.11 `8d65b3e3...9f54d2a43`, 0.1.12 `fbd215a5...5d66d1b1`
(the same three values `rust/client/src/recomp.rs:20-24` already lists).

**Bottom line: no change in 0.1.12 requires a different probe binary.** Every name resolves, every layout the
probe reads is at the same offset, all three detour prologues match byte for byte. The one DLL can serve both
versions without a runtime branch. What must change is outside the probe (the launcher's version gate), plus
behaviour changes that need a live test.

---

## BREAKING (must change)

### B1. The launcher refuses 0.1.12 (not the probe)
- `rust/client/src/recomp.rs:14` `SUPPORTED = &["0.1.10"]`, `:16-17` `RECOMMENDED` / `DOWNLOAD`, and the tests at
  `:117-119`. 0.1.12's hash is already in `KNOWN` (`:23`), so it is recognised and rejected.
- User-facing text: `site/index.html:90` ("Windows mkw-recomp 0.1.10 build").
- (Not breaking, comments only: `keypad.c:26`, `npln.c:1670-1672`, `npln.c:1708-1711` name 0.1.10.)

### B2. Probe source: nothing
No probe file needs an edit for 0.1.12 to load, resolve, register and detour. If a runtime version check is wanted
for logging only, there is no version string in mkw.exe; the cheapest discriminator available to the probe is the
COFF symbol `rt_sj_owner` (checked in the COFF symbol tables: absent in 0.1.10, RVA 0x3B240 in 0.1.12; also
`cfg_game_dir` 0x10920, `race_scene_on` 0x1E6A0) or the exe hash.

---

## NEEDS LIVE TEST

### T1. First start after the update: mkwgame.dll must be rebuilt, `--play` alone is fatal until then
- `RT_GAME_ID` changed: `f3dd89d35d5d19af` (0.1.10 exe) -> `c0dd6cf12c07c23a` (0.1.12 exe); it hashes
  `runtime/cpu/*.h` + `gamelib.h` (`tools/build/build.py:143-147`) and `cpu.h` changed. (Commit aff07e6 only undid
  one accidental change of `gamelib.h`; the id still moved.)
- `gamelib.c check()`: a library with another id -> `rt_fatal("game code: built for another version of the
  runtime")`. `want_launcher()` returns 0 for `--play`, so `mkw.exe --play` (what `rust/client/src/launch.rs:88`
  and `site/install.sh` run-game.sh start) goes straight into that fatal.
- Test: update a 0.1.10 folder to 0.1.12, press Play in the MKWMP launcher without opening mkw.exe first. Expected:
  game dies at once with that line in `logs/mkw.log`. The user has to open mkw.exe once (its start screen rebuilds
  the game code, minutes). Decide whether the MKWMP launcher should detect it: `<game>/mkwgame.id` content vs the id
  of the exe (the 16-hex string is in mkw.exe's .rdata), or just tell the user.

### T2. The rt_dispatch IAT hook on the new mkwgame.dll
- `npln.c:1388-1418` patches mkwgame.dll's import `rt_dispatch` from `mkw.exe`. Source says it still holds:
  `build.py RT_EXPORTS` = `rt_dispatch, rt_sj_owner (new), rt_svc, rt_udf, rt_ticks, rt_spin_gate, fnprof_hit`;
  mkw.exe 0.1.12 exports exactly those 7 names (6 before); gen.py still emits `MUSTTAIL rt_dispatch(c)`. The DLL
  still links against the import library made from mkw.exe's .def (`build.py:227-231`, `:310-313`, unchanged), so
  the import module name stays `mkw.exe` (`npln.c:1401` compares it); `gamecode.py`'s diff only rewrites the
  linker response file quoting (bda1b60).
  `GetModuleHandleW(L"mkwgame.dll")` is by base name, so the DLL living in another folder (T5) does not matter.
- Test: go online once; `mkwmp.log` must show "BulkGetDocuments and Pia join tracing installed" and not
  "rt_dispatch import not found".

### T3. Guest longjmp now unwinds host frames (commit 5dbc49d, `runtime/cpu/sjlj.c`)
- New: functions that call setjmp are entered through `rt_sj_owner`; `dispatch_slow` calls `rt_sj_resume(c)` when no
  host function is at `c->pc`, which `longjmp`s (non-SEH) back to the owner's frame. Any host frame in between is
  dropped without running its tail: `profile_dispatch` after `original_dispatch(c)` (`npln.c:1358` onwards, only
  logging, no lock held: harmless), and any probe override sitting inside `g_rt.rt_call` (`guesthook.c:65-72`,
  `npln.c:113-125`, `npln.c:218`) if the guest longjmps out of the called function.
- In 0.1.10 the same event was a fatal dispatch failure, so this cannot be worse; upstream saw it only on game 1.8.0
  in Thai (text code, main+0x1bb0b34). `rt_sj_resume` only fires for a pc inside a live owner's range, so the
  probe's fake addresses (0x0FF0'0000'0000+, found by `host_lookup` first) are unaffected.
- Test: a normal online session on 1.0.0 with `mkw.log` grepped for "sjlj:"; no line expected. If one appears
  during netplay, check that no probe SRW lock was held across an `rt_call`.

### T4. Frame rate: `MKW_FPS=auto` changed meaning (7727353, b2655d2, ff023bf; `runtime/core/boot/game.c`, `fps_auto.c`)
- 0.1.10: `auto` = the game picks its interval (1 or 2). 0.1.12: `auto` = the runtime picks 1-4 from measured frame
  time (60/30/20/15 fps "at real speed", 3 and 4 only in a race via the new `race_scene.c`), `game` = the old
  behaviour, plus `20` and `15`. New defaults: `LOW_LATENCY=1` (`frame_jit.c`, acquire waits), `UPSCALER=bilinear`,
  new `VRR`, frame generation rate from the present interval.
- Online impact unknown. Intervals 3 and 4 are only used while `race_scene_on()`; whether an *online* race runs
  through the sequence nodes that set it (IsSequenceFlag after ReadyRace/StartIntroMovie/WaitIntroMovie, or
  ChatUIReady after ReadyFreeRun) is not known from source. Even without them, 1 vs 2 is now chosen by the
  runtime's measurement (`fps_auto.c`) instead of by the game. The probe's race-end idle-kick handling
  (`npln.c:1208`, the Proton default skip of 0x9b928162 around `:1331-1347`) was tuned on 0.1.10 timing.
- Test: online race, slow machine (Deck) vs PC, default settings. Grep `logs/mkw.log` for
  `race: in a race at the start of`, `game: auto interval N -> M`, `game: interval N capped at 2 (out of a race)`.
  No `race:` line during an online race means 3/4 cannot occur online. Repeat with `MKW_FPS=game` in `mkwmp.env`.
- If `auto` desyncs or kicks: pin `MKW_FPS=game` (environment wins over mkw.ini; the menu greys the row out). No
  version branch needed: on 0.1.10 any value other than `30`/`60` already means "the game picks"
  (`game.c:86` there: `want = "30" ? 2 : "60" ? 1 : 0`).
- `race_scene.c` rewrites slot +10 of 14 sequence-node vtables (1.0.0: main+0x3165fe0..0x3177780) to host addresses
  at patch page +0xe00..0xef0. No overlap with the probe's `VT_ONLINE_PAUSE` main+0x3248970 (`voice_ui.c:24`) nor
  with its fake addresses; adds 16 entries to `g_host` (cap 8192).

### T5. "Choose game folder" (dc54c5e, 5ae9d2e; `cfg.c cfg_game_dir`, key `MKW_GAME_DIR` / mkw.ini `game_dir`)
- Unset (default): the game dir is the exe's folder; nothing moves.
- Set: mkw.exe, SDL3.dll, libopus, libusb, tools and the *selector* mkw.ini stay beside the exe; `mkwgame.dll`,
  `mkwgame.id`, the installation's own `mkw.ini`, `exefs/ romfs/ profile/ mods/ cache/ spirv/ texpre/
  pipeline-cache.bin setup-state.json backups/` and **`logs/mkw.log`** live in the chosen folder
  (`main.c log_open`, `gamelib.c`, `romfs.c`, ...).
- winmm.dll is a static import of mkw.exe, so Windows/Wine load it from the exe's folder as before; the probe
  finds `mkwmp.toml`, `mkwmp.env`, `mkwmp-voice.dll`, the lobby CA and writes `mkwmp.log` through
  `GetModuleFileNameW(NULL)` (`log.c:24`, `config.c:99`, `dllmain.c:114`, `voice.c:128`, `lobby_ca.c:60`): all
  still the exe's folder. The probe keeps working if the MKWMP "game folder" is the folder with mkw.exe.
- What goes wrong when it is set: `rust/client/src/support.rs:71` copies `<game>/logs/mkw.log` (now in the other
  folder; resolve it from `game_dir` in `<exe dir>/mkw.ini`, value quoted, absolute or relative to that ini);
  `resources/tools/prepare_player_two.ps1:10` copies mkwgame.dll / mkwgame.id / mkw.ini from the exe folder;
  `site/install.sh` run-game.sh writes `game/logs/proton-launcher.log` (harmless).
- Test: pick another folder in the recomp start screen, start through the MKWMP launcher: `mkwmp.log` appears beside
  mkw.exe with "runtime resolved", overrides registered, T2's line; bug report zip: check which mkw.log it holds.

### T6. Profiles (6698c5d; `players.c`, `MKW_PLAYER`, `RtConfig.profile_base`)
- The save/account folder is now `profile/` or `profile/players/<n>/`, chosen by mkw.ini `player`; the stock
  `GetNickname` returns `nickname.txt` instead of "Local" (`ov_system.c`). The probe's `GetNickname` override is
  never gated (`npln.c:1632`, `:1727-1729` un-gate `nn::account`), so the MKWMP name still wins in game.
- `instance.lock` moved to `profile_base` (one game per profile folder whichever player): two instances for local
  tests still need two `MKW_PROFILE_DIR`s, as before.
- Test: create a second recomp profile, play online with it: the name shown is the MKWMP one; the NSA id / seat do
  not depend on the profile (`npln.c:303 FAKE_NSA_ID` etc.).

### T7. Controllers: SDL3.dll is now built from source with libusb (7b46132)
- Same SDL 3.4.18, same 1271 exports; all ten names the probe resolves by `GetProcAddress` are exported by both
  DLLs (`SDL_GetJoysticks, SDL_GetJoystickNameForID, SDL_GetHint, SDL_free, SDL_QuitSubSystem, SDL_InitSubSystem`:
  `dllmain.c:53-56, 79-82, 95-96`; `SDL_GetGamepads, SDL_GetGamepadFromID, SDL_GetGamepadButton,
  SDL_GetGamepadAxis`: `keypad.c:112-116`, `voice_ui.c:65-68`). It still imports `WINMM.dll!timeBeginPeriod/timeEndPeriod` (in
  `winmm.def:138-139`). Its CRT changed from msvcrt to UCRT; new `libusb-1.0.dll` beside it (no winmm import),
  loaded lazily by SDL's HIDAPI.
- Per-player motion sensors in `input.c` (4bd02c4): arrays instead of one pad; the probe's
  `SDL_QuitSubSystem/SDL_InitSubSystem` rescan (`dllmain.c:62-63`, `:98-99`) closes gamepads under the runtime as it
  did before; whether the runtime's new per-player sensor state survives that is untested.
- Test under Proton on the Deck/Ally: pad detected at boot and after the pad-watch rescan ("pad-watch:" / "pad-diag:"
  lines), keypad and in-game voice UI respond to the pad; on Windows with a Switch 2 controller over USB if available.

### T8. Keypad and room-id answer
- Hooks install on 0.1.12 by construction (see S4), but the path is worth one run: join by room code, keypad opens,
  OK and cancel; `mkwmp.log` "keypad: hooks installed" and voice.c's "room id from the launcher: N (runtime sees
  "N")" (`voice.c:225-230`; `SWKBD_ANSWER`/`SWKBD_TEST` are still known keys and `cfg_get` still reads the
  environment first).

### T9. recomp Tools page: "Add to Steam", desktop shortcut, Undo update (fca3612, `tools/prepare/steam.py`)
- Add to Steam writes every Steam user's `shortcuts.vdf`: removes any entry whose `Exe` equals `"<mkw.exe path>"`
  and adds one with **empty LaunchOptions** (name "Mario Kart World"). On Windows a hand-made mkw.exe shortcut
  would lose `--play` (it then opens recomp's start screen; the probe still loads from the exe folder). MKWMP's
  own Deck installer points its shortcut at the MKWMP launcher under the name "Mario Kart World (MKWMP)"
  (`site/install.sh:24`, `:398-420`), a different Exe, so it is not the entry replaced. What the script does when
  run inside Proton (it reads `HKCU\Software\Valve\Steam`) is untested; a hand-made Linux shortcut with the
  site's launch options (`site/index.html:92`, `WINEDLLOVERRIDES="winmm=n,b" ...`) would lose them only if the
  script reached the real shortcuts.vdf with the same Exe string.
- The in-app updater (`launcher_update.cpp:241-264`) renames, one by one, the entries the new package brings to
  `<name>.old` and moves the new ones in; `update.py clean` then keeps the `.old` ones in `update-previous/`. From
  that reading it does not touch names the package lacks (`winmm.dll`, `winmm_host.dll`, `mkwmp*`), but it was
  not run: verify on one in-app update 0.1.10/0.1.11 -> 0.1.12 that the probe files are still there (then T1
  applies). The setup's new stale-file removal (`gamecopy.py remove_stale`, called from `prepare.py:343-363`) only
  prunes inside `<game>/exefs` and `<game>/romfs`.
- Test (Windows): press Add to Steam in recomp's Tools page, start that entry: the probe loads, recomp's start
  screen opens instead of `--play`.

---

## SAFE (checked, unchanged)

### S1. (a) Game build / guest addresses
- `recomp/versions.py` VERSIONS is the same in both tags: 1.0.0 (`9e244438c3d8ef1c`) and 1.8.0
  (`d00e4f399e411d06`); only `romfs_products/romfs_mismatch` were added (setup refuses exefs/romfs of different
  versions, e731b43). `recomp/patches-mkw.txt` (1.0.0) is not in the diff. `patches-mkw-180.txt` gains the `lan`
  set (ef20acc), on only with `MKW_LAN=1`.
- Layout base unchanged (main at 0x7000000000; `layout.py` not in the diff), so every `0x70...` constant and
  `GUEST_MAIN()` in the probe means the same guest address as on 0.1.10 for the same game build. The probe does
  not check the game version (no build-id test in `native/probe/src`), exactly as before; 0.1.10 already ran both
  1.0.0 and 1.8.0.
- `version.c` only adds fields (`expheap_create`, `race_events`).

### S2. (b) COFF names
- All 31 names of `coff.c:13-45` are present in both symbol tables, each exactly once (no duplicate static of
  `g_host` / `g_fibers`), same storage class and section. `rt_dispatch`, `ov_show_keyboard`, `ovr_start/ovr_stop`
  present too. Symbol count 22655 -> 22910.

| symbol | 0.1.10 RVA | 0.1.12 RVA |
|---|---|---|
| hostfn_add_override | 0x35BD0 | 0x3A260 |
| ldr_count / ldr_symbol | 0x148C0 / 0x14A70 | 0x16D00 / 0x16EB0 |
| rt_log / rt_call / cfg_get | 0x17E90 / 0x36700 / 0xDEF0 | 0x1A2D0 / 0x3AD90 / 0xE260 |
| ov_open_file (static) | 0x1546F0 | 0x15DC00 |
| kern_fiber_switch / kern_fiber_initialize | 0x2BD40 / 0x2B8A0 | 0x302F0 / 0x2FE50 |
| fiber_switch_to (static) | 0x2BE20 | 0x303D0 |
| kern_cpu / kern_core_away / kern_core_back | 0x26D80 / 0x285B0 / 0x28640 | 0x2B330 / 0x2CB60 / 0x2CBF0 |
| g_fibers / g_host (.data) | 0x182A480 / 0x1A88410 | 0x1870220 / 0x1ACE1D0 |
| rt_dispatch | 0x354F0 | 0x39B70 |

- Signatures: `runtime/cpu/hostfn.c` (hostfn_add_override, rt_call), `runtime/core/kernel/*` (kern_*, fibers),
  `runtime/core/boot/loader.c` (ldr_*), `runtime/sdk/swkbd/*` (text_entry_run, SwkbdRequest), `runtime/sdk/ov/ov_net.c`,
  `runtime/ui/menu/menu_notice.*` and the vendored ImGui are **not among the 245 changed files**. The fs handlers
  `ov_open_file / ov_entry_type / ov_read_file / ov_file_size / ov_close_file` are statics of
  `runtime/sdk/fs/fs_host.c`, whose whole diff is one line (`struct tm t = {0};` in `rotate_backup`).
  `rt_log` and `cfg_get` prototypes are unchanged in `host.h` / `cfg.h` (cfg.c only adds keys and the game dir).
  First 16 bytes of every function in the table are identical apart from rip-relative displacements.

### S3. (c) Struct layouts
- `Cpu` (`runtime/cpu/cpu.h`): one field added inside the old padding, `void *sj; // 344`; `x[32]@0, sp@256,
  pc@264, thread@312, v@512` unchanged; size unchanged. The probe's prefix struct (`recomp.h:19-25`, 320 bytes) is
  only ever used through pointers the runtime hands out (no `Cpu` allocated by the probe), so `sj` is never missing.
  Binary check: `rt_dispatch` starts `48 8B 81 38 01 00 00` (`mov rax,[rcx+0x138]` = thread@312) in both exes.
- `g_host`: `dispatch.c` diff is one line (`rt_sj_resume` in `dispatch_slow`); `HOST_CAP 8192`, entry
  `{uint64_t addr; GuestFn fn;}`, hash `(addr >> 2) * 0x9E3779B97F4A7C15 >> 51` unchanged = `guesthook.c:10-23`.
  Symbol span to the next symbol is 0x20000 (8192 x 16) in both exes.
- hostfn: file unchanged: `MAX_ADDED 64`, `added[]` spans 0x400 in both exes, "fatal after ldr_load" and
  "more than 64" unchanged, lookup order `added[]` first.
- `g_fibers` spans 0x2000 (1024 pointers) in both; `kernel_fiber.c` unchanged (the probe's fiber overrides are off
  by default anyway, `npln.c:1708-1713`).
- Thread object: kernel untouched; `sjlj.c` only uses the existing `kern_host_depth()`.

### S4. (d) Detours (keypad.c is the only user of detour.c; guesthook.c patches guest vtables, not x64 code)

| function | 0.1.10 RVA | 0.1.12 RVA | first bytes in 0.1.12 | probe signature | match |
|---|---|---|---|---|---|
| text_entry_run | 0x18DFF0 | 0x197B20 | `41 57 41 56 56 57 53 48 83 EC 20 4C 89 CE` | kEntrySig (14, steal 7) | yes |
| menu_notice_shown | 0x26EBB0 | 0x27E190 | `56 48 83 EC 20 48 8B 35` | kShownSig (8, steal 5) | yes |
| menu_notice_draw | 0x26EBE0 | 0x27E1C0 | `56 57 48 81 EC 78 01 00 00 C5 78 29 84 24` | kDrawSig (14, steal 9) | yes |

- The seven ImGui symbols resolve (0x1B8A30, 0x1F4BA0, 0x1F4AB0, 0x1F5F50, 0x1B1A40, 0x1CCBC0, 0x1B8C50).
  `menu.cpp` still calls `menu_notice_shown()` / `menu_notice_draw(width, scale)` once per overlay frame
  (its diff only touches the perf overlay and adds `classic_poll()`).
- `.text` grew 0x713776 -> 0x724096; `detour.c alloc_near` searches +-0x70000000, unaffected.

### S5. (e) Override names and precedence
- mkw.exe's `.ovr` table (what `stock_handler`, `npln.c:1469-1494`, reads): same size 0x14C0, **330 names, the
  list is byte-identical** between 0.1.10 and 0.1.12 (`scratchpad/tools2/ovr10.txt` vs `ovr12.txt`). No override
  was added, removed or renamed; every `kRegs` name (`npln.c:1598-1668`) that had a stock handler still has one.
- 777a37e only moves `ov_lan_address` from `ov_socket.c` to the new `ov_lanaddr.c` and ranks interfaces (default
  route first, /32 last; fa3e458, d572461); `ov_lan_only()` is now public. The registered names are the same and
  it only matters with `MKW_LAN=1`; the probe's own `GetCurrentIpConfigInfo` override (`npln.c:1600`) is used once
  active.
- Precedence: `hostfn.c` unchanged, `added[]` is searched before the linked `.ovr` entries: ours still win.
- The 64-slot budget is unchanged; the probe's static asserts (`npln.c:1681-1683`) still describe it.

### S6. (f) Environment, ini, loading
- `MKW_ASSUME_FOCUS`, `MKW_MESH_COMPUTE`, `MKW_SWKBD_ANSWER`, `MKW_SWKBD_TEST`, `MKW_PROFILE_DIR`, `MKW_RESOLUTION`,
  `MKW_LAN`, `MKW_CTL`, `MKW_TRACE`, `MKW_SEQ_WATCH` are all still in `cfg.c known[]` and read at the same places.
  New keys: `GAME_DIR, PLAYER, SHORTCUT_ASKED, HDR, LATENCY, LOW_LATENCY, VRR, CLASSIC_GP, DYN_DIFF, POOL_SPILL,
  SPARSE_NO_BUFFER`; `FPS` values `auto|game|60|30|20|15` (T4); `QUICK_RACE` gains `gp`.
- `cfg_get` precedence (environment, then mkw.ini) unchanged; `cfg_init` is now idempotent and can run earlier
  (first `cfg_game_dir()` call), still after DllMain.
- `--play`, `--launcher`, `MKW_LAUNCHED`, `MKW_NO_LAUNCHER` handling unchanged (`main.c want_launcher`).
- mkw.exe still imports `WINMM.dll!timeBeginPeriod`; import order changed (SDL3.dll now after WINMM.dll), which
  does not matter: DllMain of the proxy still runs before the exe entry point and before `ldr_load`
  (`npln.c:1706` "TOO LATE" guard stays valid).
- New in `win32_mem.c`: `_set_invalid_parameter_handler` reports a UCRT invalid-parameter call as a crash in
  `mkw.log` instead of a silent 0xC0000409 (2981919). Process-wide for the shared UCRT; if the probe links the same
  UCRT, a bad CRT call in the probe is now logged (as "mkw" only if a frame of mkw.exe/mkwgame.dll is on the stack).
  New `constructor(101)` AVX2 check with a message box (9895782): runs after the proxy's DllMain.
- Fibers: no change in the runtime. Present/framegen/latency (`gfx_present.c`, `vk_framegen.c`, `vk_latency.c`):
  internal, no symbol the probe uses; `vk_present_flip` now returns an id. Present of a larger driver surface (#140).
- Networking in the runtime: only the LAN address choice (S5) and the 1.8.0 `lan` patch set. No change to
  `ov_socket.c` handlers beyond the split, none to nifm (`ov_net.c`), no TLS/gRPC code exists upstream.
- Memory: new `mem_runtime_alloc` at the top of the heap block (render-buffer second pool, 4394ac0), guest
  addresses the probe does not use.

---

## Files
- Report: `/private/tmp/claude-501/-Users-anarqz-Projects-mkwmp/c34dc01a-76e3-47dd-a7b1-28afef93809f/scratchpad/recomp-0.1.12-diff.md`
- Scripts: `.../scratchpad/tools/pecheck.py`, `.../scratchpad/tools/ovr.py`, `.../scratchpad/tools2/chk.py`
- Override lists: `.../scratchpad/tools2/ovr10.txt`, `.../scratchpad/tools2/ovr12.txt`
- SDL3.dll / libusb extracted to `.../scratchpad/sdl/10`, `.../scratchpad/sdl/12`
