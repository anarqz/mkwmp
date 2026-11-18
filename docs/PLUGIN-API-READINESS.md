# Readiness for mkw-recomp's native plugin API (MkwPluginApi)

**Status 2026-11-18: landed.** mkw-recomp `main` has plugin API version 1 (`ed04f94` feat(plugin), `730ce0f` number pad
and overlay, `c7142dc` docs; the wiki's "Writing a plugin"). The probe now builds against that header unchanged
(`native/probe/src/mkw_plugin.h`, branch `plugin-v1`); see **Version 1 (upstream)** at the end. The sections before it
record what was proposed and our fork's prototype, which v1 supersedes.

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

## Phase 2 started (branch probe-plugin, against the prototype header)

The prototype API is `runtime/plugin/mkw_plugin.h` on the fork's `plugin-loader` branch (API version 1), vendored
unchanged as `native/probe/src/mkw_plugin.h`. Nothing runs it yet: there is no runtime build with a plugin loader.

- `native/probe/src/backend_plugin.c`: `mkw_plugin_init`. Refuses (`api->fail`, non-zero) an `api_version` below 1, a
  table shorter than version 1's, or a table without override / guest_symbol / log / call / cfg_get / core_away /
  core_back. Fills `g_rt`: override, stock, guest_symbol, call, log, cfg_get, core_away/back straight from the table;
  `host_fn_at` from `host_at` and `dispatch_hook` (0-is-success turned into g_rt's 1); `fs_*` = `stock()` of the
  nn::fs names npln.c overrides; `guest_modules` / `game_code_loaded` 0 until `on_game_loaded` fires; new
  `guest_thread` always 1 (npln.c's poll used to read `Cpu.thread`, beyond MkwCpu's prefix; the COFF backend leaves it
  NULL and keeps that test).
- Start-up shared with the winmm loader: `probe.c` (`probe_log_start`: log, version, `mkwmp.env`; `probe_register`:
  `npln_register`). `dllmain.c` keeps only the COFF resolve and the keypad.
- Gap 2 covered on our side: `plat_set_home(api->plugin_dir)`, so `mkwmp.toml`, `mkwmp.env`, `mkwmp.log` and the
  voice library are the plugin folder's.
- Room ID: `text_entry_hook` answers with `MKW_SWKBD_ANSWER` (kept equal to the launcher's Room ID by voice.c), else
  the runtime's own text entry. keypad.c stays COFF-only. `notify_push` also goes to `api->notice` (new
  `notify_set_sink`).
- Build: `native/probe/build_plugin.py` makes `build/plugin/linux/mkwmp.so` and `build/plugin/windows/mkwmp.dll`,
  -Werror, only `mkw_plugin_init` exported (Linux: version script, `--no-undefined`). `--test` runs
  `tests/test_plugin.c`: the .so against a fake API (64 overrides, the same names as the COFF path; refusals; log and
  env file in plugin_dir). In CI (probe-linux.yml).

Not mapped yet / left undone:
- **Plugin threads calling guest code** (gap 1): the API's `call` is only for host functions on a guest thread. The
  probe's own threads do not call guest code today, but nothing enforces it.
- `guest_readable`: guesthook.c / gameaddrs.c still use `plat_mem_readable` (/proc/self/maps on Linux, slow).
- `main_base` / `game_version`: the probe still assumes main at 0x7000000000 (`GUEST_MAIN`) and finds the game build
  by reading guest memory (guesthook.c `game_build`); both could come from the API after `on_game_loaded`.
- `observe` / `call_original`: unused; the fs overrides chain through `stock`, which is enough.
- The voice UI's keyboard shortcuts: `plat_key_down` has no global keyboard from a Linux .so (always 0 there).
- Override order (gap 6) and load errors on the start screen (gap 3) are the runtime's; `fail` is what we use.
- The launcher does not pick a loader or install `mods/mkwmp/plugin/` yet.
- Untested against a real runtime; only the fake-API test has run.

## Version 1 (upstream, landed): how the probe maps onto it

Upstream's v1 differs from our prototype; `backend_plugin.c` (branch `plugin-v1`) is rewritten for it:

| Probe need | v1 entry | How |
|---|---|---|
| SDK overrides (npln.c kRegs 62 + core-mask pair) | `override_symbol` (1 = done), no cap | `g_rt.override` |
| fs log-then-chain | `stock` | `g_rt.fs_*` = `stock(name)` |
| guest symbols, calls, core away/back, log, cfg | `guest_symbol`, `call`, `core_away/back`, `log`, `cfg_get` | as they are |
| game loaded | `guest_modules`, `on_game_loaded`, `module_base("main")`, `game_version` "1.8.0", `game_id` | `g_rt.guest_modules` / `game_code_loaded`; logged at load |
| `rt_dispatch` hook (`profile_dispatch`: error skips, idle-kick guard, BulkGet / Pia / matchmake / room traces) | `override_address` + `call_original` | one override per target address of the build's row (`npln.c` `install_by_address`), from `on_game_loaded` only; the kart and room ones only with their switch on. Matches by caller only (controller next-phase / prepare BLRs) and `MKWMP_TRACE_EDGES` have no target: COFF-only |
| vtable hooks with a fake address (`guesthook.c`, voice UI) | `override_address` on the slot's function | the slot is not written; the function is overridden for every indirect caller, so `voice_ui.c` checks the page's vtable and passes others on (`guesthook_original` = `call_original`) |
| Room ID keypad (`keypad.c`, text hook) | none needed | mkw-recomp draws its own number pad; no text hook in the plugin |
| errors to the player | `const char **message` of `mkw_plugin_init` | `refuse()` |
| notices | `notice` | `notify_set_sink` |
| readable guest memory | not in v1 | `plat_mem_readable` (ours) |

Rules that shaped it: address overrides are allowed only in `mkw_plugin_init` and `on_game_loaded` (the probe used to
hook lazily once online), so `on_game_loaded` installs them when the probe is active (mkwmp.toml or `MKWMP_ENABLE`)
and then clears `g_rt.override_address`. An address where no function starts is fatal in the runtime, so only the
build row's verified function starts are used.

Folders: the probe's home is the mod folder (`mods/mkwmp/`: `mkwmp.log`, `mkwmp.env`, the voice library). `mkwmp.toml`
and the lobby CA are read from there, else from the game executable's folder, where the MKWMP launcher writes them
(`config.c` `mkwmp_config_path`). Only libraries with `mkw_plugin_init` go in `plugin/`: the runtime loads every one.

The mod: `build_plugin.py --package` lays out `mods/mkwmp/` (`mod.json` with `has_plugin`, `plugin/mkwmp.dll` +
`mkwmp.so`, the voice libraries) and zips it; CI uploads it as the `mkwmp-mod` artifact. To try it: copy the folder
into `<game>/mods/` and run `mkw --play --mod mkwmp`, or enable it on the Mods page. Not released.

Not in v1, to ask upstream (generic): a per-mod settings page in the launcher (fields, toggles, buttons, the current
profile's name) so the mod can replace the MKWMP launcher's register / server / voice / consent settings.

## Without the MKWMP launcher: the options page and the plugin's own seat client (branch `plugin-seat`)

Built on the fork's plugin API 2 proposal (anarqz/mkw-recomp branch `mod-page`: `mkw_mod_page`, per-player mod
settings, the profile name and language), vendored as `src/mkw_plugin.h`. A version 1 runtime still loads the
plugin: it then uses `mkwmp.toml` and the launcher only.

- `modpage.c`: MKWMP's page in the start screen's Mods > MKWMP > Options, English and Brazilian Portuguese:
  - the 18+ notice and its checkbox (everything else is greyed out until it is ticked);
  - the server (default `https://mkw.anarqz.dev`);
  - sign-up of the current profile's name (the PIN is stored and shown once), or entering the PIN of a name signed up
    elsewhere (checked by a presence sign-in);
  - voice on or off;
  - the friends list: incoming requests (Accept / Decline), friends with their state (Remove), requests sent (Cancel),
    and a request by name.

  Network calls run on a worker thread of the page.
- Settings (`<player>/mod-settings/mkwmp.ini`): `consent18`, `server`, `pin` + `pin_name` (the profile name it is
  for), `voice`.
- In the game, `backend_plugin.c` reads them. With consent and a PIN for this profile's name, and no `mkwmp.toml`
  naming a launcher, the config becomes direct (`mkwmp_config_use_direct`), and `launcher.c` hands `/local/v1/...`
  to `seatd.c`, which calls the seat server (`seatapi.c`) and answers as the launcher does. So `seat.c`, `friends.c`
  and `voice.c` are unchanged.
  - The seat: join, heartbeat, and join again when the session is lost. It is given back when the game ends
    normally (the fork's API 2 `on_exit`: window closed, Quit, the game's exit; 1.5 s timeouts); a crash leaves it
    to the server's 60 s timeout.
  - The lobby CA is saved in the mod folder and the voice port comes from `/seat/v1/status`.
  - Friends come from a presence.
  - Voice mutes are kept in the mod's settings across games (`mutes.1`..`mutes.3`, up to 64 account ids).

  Not completed: online play stays off, with a notice at game load pointing to the page.
- HTTP: `plat_http.h`: WinHTTP on Windows, libcurl loaded at run time on Linux.
- Tests (`build_plugin.py --test`) run against `tests/fake_seat.py`, a fake seat server:
  - `test_plugin.c` drives the page through a fake `MkwPage`: the consent gate, sign-up, a friend request, a taken
    name with a wrong and then the right PIN, a profile name the server refuses, and a version 2 init signing in by
    itself.
  - `test_seatd.c` covers the in-game local API: refusals, ticket, lobby and CA, heartbeat, rejoin, friends, voice
    and mutes.

Not yet:
- a confirmation before Remove;
- in-game notices for friends coming online.
