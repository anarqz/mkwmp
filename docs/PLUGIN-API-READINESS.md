# Readiness for mkw-recomp's native plugin API (MkwPluginApi)

Status 2026-11-18: upstream has **proposed** the API (conversation in `~/MESSAGES.MD`, not in the repo). Nothing has landed
on mkw-recomp main (2cf005a). Upstream's CONTRIBUTING.md (ecafc22) now promises "generic, documented hooks" for outside
projects and names MKWMP as the place for online play.

Proposed by upstream: `.dll`/`.so` from `<mod>/plugin/`, loaded only when the player enables the mod;
`mkw_plugin_init(const MkwPluginApi *api, uint32_t api_version)` before the game loads; a versioned function table
(log, cfg_get, override by name without the 64 cap, call a game function, guest memory r/w, nn::os events, romfs);
CPU-state accessors; overlay (ImGui) and text-entry hooks; the numeric keypad moves into the recomp itself.

Our reply asked in addition for: by-address overrides that also catch indirect calls (with call-original and x30),
`kern_core_away/back`, observer/chaining (`HOST_OBSERVER` + `rt_original`), guest symbol lookup, `on_game_loaded`,
game build id + mkwgame.id, `--mod <name>` with `--play`, cheap guest memory (1:1 mapping or inline `guest_to_host`),
a `size` field next to `api_version`, overlay for notices.

## Every recomp dependency of the probe, and the API entry that replaces it

| Probe today (file) | What for | API entry | Asked? |
|---|---|---|---|
| `hostfn_add_override` (coff.c, npln.c kRegs: 64/64 used) | nn::nifm/account/time/socket/ssl/friends overrides | override by name, no cap | proposed |
| `rt_log`, `cfg_get` | logging, MKW_* keys | log, cfg_get | proposed |
| `ldr_symbol` + `rt_call` (npln.c) | call the game's `pthread_cond_clockwait`, `clock_gettime` | guest symbol lookup + call | asked |
| `Cpu` layout, `arg_x/ret_x` (recomp.h) | arguments / return | CPU accessors | proposed |
| `Cpu.thread`, `kern_cpu` (npln.c:311/327) | the current guest thread inside a socket override | **current-Cpu accessor; which host threads may call guest code** | **not asked** |
| `kern_core_away/back` | block on host (WSAPoll/Sleep) without holding the emulated core | core away/back pair | asked |
| `ov_open_file`... (g_dele fs) | log then call the stock handler | observer / call-original | asked |
| `g_host` vtable slots 7/11 of 0x7003248970 (guesthook.c, voice_ui.c) | pause-menu voice mute markers / clicks | by-address override incl. indirect calls | asked |
| `rt_dispatch` import patch (npln.c, guesthook.c) | drop false comm error 0x9b928162 (Proton), BulkGetDocuments, traces | by-address override with x30 | asked |
| fibers (`g_fibers`, `plat_fiber_*`, `kern_fiber_*`) | old workaround | none: delete | n/a |
| detours `text_entry_run`, `menu_notice_shown/draw` (keypad.c, detour.c) | Room ID keypad, notices | text-entry hook; keypad upstream; overlay | proposed |
| ImGui symbols via COFF (keypad.c) | drawing the keypad / notices | overlay hook | proposed |
| guest addresses per game build (gameaddrs.c) | pick 1.0.0 vs 1.8.0 table | build id in API + `on_game_loaded` | asked |
| `winmm.dll` proxy (dllmain.c, winmm.def) | getting loaded at all | plugin loader | proposed |
| Proton SDL env fixes (dllmain.c:23-75) | Steam Deck pads under Proton | none on native Linux; keep for Windows/Proton | n/a |

## Gaps still worth raising with upstream

1. **Thread rules.** Which host threads may call into the API (guest calls, memory, events)? Our seat/friends/voice
   threads are plain host threads; today `rt_call` gets a `Cpu` from `kern_cpu`. Need: a documented way for a plugin
   thread to call a guest function (or a "run on guest thread" queue).
2. **Plugin data folder.** With `MKW_GAME_DIR`, mods/ moves. The API should give the plugin its own folder path
   (for `mkwmp.toml`, `mkwmp.log`, the voice library), so we stop assuming "beside mkw.exe".
3. **Load errors visible to the player.** If `mkw_plugin_init` refuses (unsupported api_version / game build), a way
   to show a message on the start screen instead of a silent run without online.
4. **Mod enabled by our launcher.** `--mod mkwmp` with `--play` (asked) — and that the in-app updater never touches
   `<mods>/mkwmp/` (today it is safe for `winmm.dll` only because it ships no such file).
5. **Linux build specifics.** Native Linux mkw is an AppImage: plugins `dlopen`ed with `RTLD_LOCAL`, and the API must
   not require linking against symbols of the executable. Our voice library is loaded by us, not by the recomp.
6. **Override order.** Two plugins on the same name: last wins or chain? (We only need chain-to-stock.)

## Our side: work that can start before the API exists

Phase 1 (no upstream dependency, keeps 0.1.10-0.1.14 working):
- **Done (branch probe-rtapi-seam):** `native/probe/src/rtapi.h`, one table `RtApi g_rt` (override, guest_modules,
  guest_symbol, log, call, cfg_get; optional core_away/back, stock, fs_* chaining, host_fn_at, game_code_loaded,
  dispatch_hook). `coff.c` is the only backend: COFF symbols, the `.ovr` stock table, the `g_host` registry and the
  `rt_dispatch` import of mkwgame.dll all live there now (they were in npln.c / guesthook.c). Exception: keypad.c
  still resolves its detour targets and ImGui through `coff_symbol`, since the keypad moves upstream. The plugin
  backend later is one new file that fills the same table.
- **Done (branch probe-plat):** `plat.h` (locks, atomics, clocks, threads, events, UTF-8 files beside the game,
  environment, dynamic libraries, memory queries / vtable writes, focus + keys) and `plat_net.h` (sockets), with
  `plat_win.c` / `plat_net_win.c` and `plat_posix.c` / `plat_net_posix.c`. Every core source compiles for
  x86_64-linux-gnu with -Werror and links into a .so with `--no-undefined` against a stub `g_rt`. Windows-only by
  design: dllmain.c (loader), coff.c (backend), keypad.c / detour.c (keypad, moving upstream).
- **Done:** the Proton / Wine patches are removed (user decision, 2026-11-18): Deck players under Proton lose the
  controller fixes and the default 0x9b928162 skip until the native plugin build exists. Retest the end-of-battle
  kick on a native Deck then (`MKWMP_SKIP_ERROR=9b928162` is the opt-in).
- Linux gaps the plugin API must fill: guest memory checks (`plat_mem_readable` reads /proc/self/maps, slow), the
  voice UI's keyboard shortcuts (no global keyboard from a .so), SDL3 if the AppImage links it statically.
- **Done:** removed the fiber code (fixed upstream since 0.1.10).
- To do: build that Linux `.so` in CI (zig, stub backend) so portability does not rot.

Phase 2 (when an API build exists): `backend_plugin.c` + `mkw_plugin_init`; launcher picks loader by recomp version
(winmm for COFF releases, plugin for API releases) and passes `--mod`; Steam Deck native Linux path without Proton.
