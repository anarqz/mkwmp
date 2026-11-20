# The console-style overlay

In the game, the Guide button (or Select+Start, or Select held for a second) brings the MKWMP launcher over the game
the way the Switch 2's HOME button does: the picture the game was showing shrinks into the Play tile of Home, the
player looks at friends or settings, and "back to the game" grows the thumbnail back to the full screen.
mkw-recomp's own menu does not open on that button.

Status: built and wired end to end (the mod's part, the launcher's Core, the window helpers, the shell's overlay mode
and its animation, the contract, tests): section 8. Nothing here has run against a live game yet: section 9 lists what
must be tested.
Sources quoted as `fork:` are anarqz/mkw-recomp at tag `v0.1.15-mkwmp`; `SDL:` is libsdl-org/SDL `main`;
`gamescope:` is ValveSoftware/gamescope `master` (both read on 2026-11-20).

## 0. Summary of the decisions

| # | Question | Decision |
|---|---|---|
| 1 | Keeping mkw-recomp's menu shut | The mod filters the pad before the runtime's menu shortcut sees it. That needs a small fork change, **plugin API 4** (`input_filter`, `menu_open`, `frame_capture`; patch in `overlay-plugin-api4.patch`). On the released 0.1.15-mkwmp (API 3) the mod can only watch: it leaves the trigger mkw-recomp uses to mkw-recomp. |
| 2 | Who sees the button | The mod, on the game's own reading of the pad (after Steam Input). Fallback for a game without the mod's overlay: the launcher's `GuideWatcher` (XInputGetStateEx). |
| 3 | The picture | In the game, from the swapchain image (API 4 `frame_capture`), RGBA at most 1280 x 720, in the body of the request. Launcher-side capture only as a Windows-desktop fallback; it cannot work under Proton. |
| 4 | Front and back | One visible window at a time: the launcher's window is hidden while the game is in front, shown for the overlay, hidden again on return. That is what Windows' foreground rules and gamescope's focus choice both accept. |
| 5 | Pause | No. The game keeps running with an idle pad, as it does behind mkw-recomp's own menu. |
| 6 | Contract | `GET /local/v1/overlay/state` (long poll), `POST /local/v1/overlay/open` (with the picture). Closing is the launcher's: the mod's poll returns. No `close` request. |
| 7 | Without the launcher | **(b) pre-warm**: the mod starts the launcher at game start as a windowless overlay host; (a) on demand only as the restart when the host is gone. |

## 1. What opens mkw-recomp's menu, and how it is kept shut

What opens it:

- Keyboard: F1 toggles, Esc opens (`fork: runtime/ui/menu/menu_sdl.cpp:53-61`).
- Pad: the setting `MKW_MENU_SHORTCUT` (mkw.ini `menu_shortcut`), `minus_plus` (the default) or `guide`
  (`fork: runtime/core/base/cfg.c:125`). `minus_plus` is Minus and Plus held together, which on an Xbox pad is
  exactly Select+Start; `guide` is one press of the Guide button. The code is `input_menu_gate`
  (`fork: runtime/input/input_map.c:328-367`), called at every read of player 1's input
  (`fork: runtime/input/input.c:389-398`): it removes the shortcut's buttons from what the game gets and calls
  `menu_toggle()`.
- A value the table does not know is not an "off": `cfg_value` returns the text as it is
  (`fork: runtime/core/base/cfg.c:466-472`) and `strcmp(..., "guide")` fails, so it behaves as `minus_plus`.

So our triggers collide with it whichever value it has: Select+Start in one, Guide in the other. The options:

| Option | Verdict |
|---|---|
| The launcher sets `menu_shortcut` to the value we do not use (`IGameTarget.WriteGameSettings`) | Frees one trigger only, and the freed one is not swallowed: with `guide` set, Select+Start reaches the game as Minus and Plus (Plus pauses). It also changes a player's setting behind their back, and is refused while the game runs. Not used. |
| A host override (`override_symbol`) of the game's input read | The shortcut is decided in the runtime before any guest call; an override of `nn::hid` sees the pad after it. Cannot stop the menu. |
| A new config value (`menu_shortcut = off`) | One line in the fork, but the mod still cannot hold Select back from the game or make the pad idle, and it needs its own second reading of the pad. |
| **A pad filter in the plugin API** | Chosen. The mod sees and edits the reading before the runtime's shortcut and the game do. One mechanism gives the trigger, the swallow, the idle pad and "the B that closed it never reaches the game". |

The fork patch (`launcher/docs/overlay-plugin-api4.patch`, made against `v0.1.15-mkwmp`, 145 lines added, **not
compiled, not pushed**): plugin API version 4 with three entries at the end of the table.

- `int input_filter(MkwPadFn filter, void *user)`: `filter(MkwPad *pad, user)` at every read of a player's input,
  before `input_menu_gate` and before the game (`input.c` `poll_merged` and `input_poll_player`). `MkwPad` has the 16
  buttons as the game gets them, the sticks, `guide` and the player index.
- `void menu_open(int open)`: opens or closes the runtime's menu (`menu_toggle` when the state differs).
- `int frame_capture(int max_w, int max_h, MkwFrameFn cb, void *user)`: section 3.

The mod's header `native/probe/src/mkw_plugin.h` is the patched one; the entries are used only when the runtime's
table reaches them (`api->size`), so the same mod runs on API 3.

**The player still reaches mkw-recomp's menu**: F1 and Esc are untouched; with API 4 the overlay can offer a
"Game settings (mkw-recomp)" button that calls `OverlayBridge.ReturnToGame(OverlayReturn.Menu)`, and the mod then
calls `menu_open(1)`; and whenever the launcher cannot open (`409`, no answer) the mod opens the menu itself for a
Guide or Select+Start press it had taken. With API 3 nothing is taken from mkw-recomp, so its own shortcut still works.

### What works on which runtime

| | Plugin API 4 (the patch) | Plugin API 3 (0.1.15-mkwmp as released) |
|---|---|---|
| Guide | opens the overlay, never reaches the game or the menu | opens the overlay unless `menu_shortcut = guide` (then it is mkw-recomp's) |
| Select+Start | opens the overlay | mkw-recomp's menu, unless `menu_shortcut = guide`: then the overlay, but the game also sees Minus and Plus |
| Select held 1 s | opens the overlay; a short press reaches the game on release | opens the overlay; the game sees Select the whole time |
| Shift+Tab | opens the overlay | opens the overlay |
| Idle pad while open | by the filter, for all players | only because SDL drops the pad of an unfocused window (section 4) |
| The picture | yes | no (a Windows-desktop screen copy by the launcher, section 3) |
| "Game settings" from the overlay | yes | no |

## 2. Who detects the button

The mod, in the game. Rules taken from Infinity Launcher's overlay (`InfinityHorizon/native/overlay/src/input.cpp`),
which already lives with Steam Input:

- Steam and the Xbox Game Bar take the Guide button, and Steam's desktop layout takes View+Menu, before a game sees
  them. mkw-recomp says the same under its Guide option (`fork: runtime/ui/menu/settings_tabs.cpp:403-408`).
- So the trigger that always works is **Select held for 1 s**. The gate owns the button: Select reaches the game only
  when it is released sooner, as a 60 ms press given back then.
- While the overlay is up the game reads an idle pad (connected and neutral); after it closes, until every button and
  stick is at rest.
- The overlay reads the same reading the game does. Infinity hooks WGI's `GetCurrentReading`; here it is mkw-recomp's
  own merged reading, handed to the filter (API 4), or the game's SDL3 gamepads read through `SDL3.dll` (API 3).

All of it is one pure function, `overlay_gate` (`native/probe/src/overlay.c`), tested in `tests/test_overlay.c`.

Which trigger reaches the game where:

| Setup | Guide | Select+Start | Select held 1 s | Shift+Tab |
|---|---|---|---|---|
| Windows, no Steam, Game Bar off | yes | yes | yes | yes |
| Windows, Game Bar on | Game Bar takes it | yes | yes | yes |
| Windows, started from Steam (desktop layout, Steam Input on) | Steam takes it | Steam's chord layer may take it | **yes** | Steam's overlay takes it when the overlay is enabled |
| Steam Deck, Game Mode | never (the STEAM button) | usually yes (View+Menu have no Steam meaning in a game) | **yes** | no keyboard |
| Steam Deck, Desktop Mode | Steam takes it | may be taken | **yes** | yes |

"Usually" and "may" are to be confirmed live (section 9). The hint bar should name Select-hold where
`DesktopEnvironment.IsGameMode` or Steam is the parent, Guide otherwise.

Fallback, for a game without the mod's overlay (the winmm probe, an older mod): the launcher's
`Overlay/GuideWatcher.cs` polls XInputGetStateEx (xinput1_4 ordinal 100, which reports Guide even unfocused) and the
shell opens the overlay itself with `OverlayBridge.RequestFromLauncher` when the game's window is the foreground one.
It is Guide only (XInput without a hook cannot take Select from the game), mkw-recomp's menu is not kept shut, and it
does nothing under Steam. The winmm probe gets no trigger of its own: the mod wins, and the probe would need a detour
of `input_menu_gate` for what one API entry does.

## 3. The picture

In the game, from the image that is shown.

- The runtime already copies its swapchain image to a mapped buffer for its own screenshots: `vk_present_capture`,
  `ensure_capture_buffer`, `save_capture` (`fork: runtime/gfx/nvn/host/gfx_present.c:544-585`, used at `:666` and
  `:789-795`; the debug command `screen PATH` of `fork: runtime/core/base/ctl.c:5-6`). It is what the player sees
  (menu, notices), in sRGB also on an HDR display (`gfx_hdr_to_srgb8`).
- API 4 `frame_capture` reuses exactly that path for one frame, box-filters it down to fit the request and calls the
  plugin on the present thread. The other reader, `MKW_SHOTS` / `read_texels` of the game's texture
  (`fork: runtime/core/boot/boot.c:256-311`), is noted in the fork as "black on some machines"
  (`fork: runtime/gfx/vk/device/vk_framegen.c:410`), so it was not chosen.
- It is Vulkan inside the game's own process: the same on Windows, under Proton and on native Linux. Nothing of the
  window system is asked.
- The mod asks for at most 1280 x 720, waits up to 300 ms, and sends the RGBA rows as the body of
  `POST /local/v1/overlay/open` (3.7 MB over loopback, a few ms; no encoder in the mod, no shared memory to map under
  Wine). The launcher turns it into BGRA once (`OverlayFrame`), and `GameWindow.ToBitmap` makes the `BitmapSource`.
  A black picture is treated as none.

Launcher-side capture, evaluated:

| Method | Windows | Proton / gamescope |
|---|---|---|
| Windows.Graphics.Capture | works, needs Windows 10 1903+, a yellow border before Windows 11, and WinRT interop from WPF | not implemented in Wine |
| DXGI desktop duplication | works for the whole output | not implemented in Wine |
| PrintWindow / BitBlt of the window | black for a Vulkan swapchain window | black |
| BitBlt of the screen | works for windowed and borderless (DWM composes); black for exclusive full screen | Wine copies the X root window; gamescope redirects every window, so the root does not hold the game's picture |

So launcher-side capture cannot be the mechanism. `GameWindow.Capture` (a screen `StretchBlt`) exists only so the
animation has a picture on Windows desktop with API 3 or the probe; it returns null under Wine.

## 4. To the front and back

**One visible window at a time.** While the game is in front the launcher's window is hidden (not behind, not
minimised); the overlay shows it; the return hides it again.

Windows:

- A background process may not take the foreground. The game is the foreground process when the trigger fires, so the
  mod calls `AllowSetForegroundWindow(ASFW_ANY)` right before the request (`plat_allow_foreground`), and the
  launcher's `SetForegroundWindow` is then allowed. `GameWindow.ShowOver` also joins the input queues
  (`AttachThreadInput`) when that failed, for the launcher's own Guide fallback where no mod made the call.
- Over a borderless or windowed game, showing and activating is enough. Over an exclusive full-screen swapchain the
  game is minimised or loses the mode when it loses the foreground (the driver's doing); `GiveBack` restores an
  iconic window. mkw-recomp's full screen is SDL's desktop full screen by default, so this should be the rare case.
- Back: the launcher has the foreground, so `SetForegroundWindow(game)` is allowed; then it hides.

gamescope (Steam Deck Game Mode), from the source:

- gamescope shows one focused window. Among the candidates of the focused app it sorts with
  `is_focus_priority_greater` (`gamescope: src/steamcompmgr.cpp`): windows with the app's game id first, then
  non-override-redirect, non-tiny, non-dropdown, enabled ones, and between two such windows of the game **the one
  mapped last wins** (`map_sequence`), then the one damaged last.
- So `launcher.Show()` (an X11 map under Wine) puts the launcher in front, and `launcher.Hide()` (unmap) leaves the
  game as the only candidate. No atom, no `_NET_ACTIVE_WINDOW` request, no topmost is needed, and none would help.
- Both windows must belong to the same Steam app. That holds when one is the child of the other: the launcher as the
  Steam shortcut starting the game, or the game starting the launcher as its overlay host (section 7). Both then run
  in one Proton prefix under one app id.
- **It cannot work** when the launcher and the game are two Steam shortcuts: two app ids (and two prefixes). gamescope
  shows the app Steam focused, and no window can raise another app. The best alternative is the overlay host: the
  game's own mod starts its own launcher process inside the game's app, whatever else runs.

The game's input while the overlay is up:

- SDL records no pad press for an application whose windows do not have the keyboard focus, unless
  `SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS` is set (`SDL: src/joystick/SDL_joystick.c`,
  `SDL_PrivateJoystickShouldIgnoreEvent`; releases still pass). The fork never sets the hint, and the keyboard is
  already ignored without focus (`fork: runtime/input/input_sdl.c`, `SDL_EVENT_WINDOW_FOCUS_LOST`). So an unfocused
  game reads nothing new, on Windows and under Wine.
- That is the only protection on API 3. With API 4 the mod's filter makes every player's pad idle from the trigger
  until the return and the release of every button, whether the focus moved or not.
- The launcher reads XInput only while its window is active (`Input/GamepadInput.cs`), so the two never both react.

## 5. Pause

No pause. Online races cannot pause, and mkw-recomp's own menu does not pause either: "While it is open the game gets
no input and keeps running" (`fork: runtime/ui/menu/menu.h:7-8`). The overlay is the same thing to the game: an idle
pad. Offline, the player pauses with Start before opening the overlay, as before opening the F1 menu.

A real suspend exists in the runtime (`freeze_set`: the game thread waits in its present, audio and loading go on;
`fork: runtime/core/hacks/freeze.c:1-20`, the debug commands `pause` / `resume`). It could become an API entry for
offline play later; it is left out because it must never happen online and the mod's "am I in a room" is not the
runtime's to know.

## 6. The contract

`docs/contracts/LAUNCHER_API.md`, sections "The overlay" and "The overlay host". In short:

- `GET /local/v1/overlay/state` answers `{"open","seq","enabled"[,"action"]}`; with `?wait=S&seq=N` it is held until
  the state changes. The mod asks it every 10 s to know whether to take any button at all (`enabled`), and in a loop
  of `wait=2` while the overlay is up.
- `POST /local/v1/overlay/open?by=..&pid=..[&menu=1][&w=..&h=..&format=rgba]`, body the picture. `200` open, `409`
  not now. `menu=1` (the mod sends it when the runtime has `menu_open`) is `OverlayBridge.GameHasMenu`: the shell
  shows its "mkw-recomp menu" button only then.
- Closing: `OverlayBridge.ReturnToGame()` ends the mod's wait. `action` `menu` asks for mkw-recomp's menu.
- Same bearer as every route; a picture is read only from a caller with the token; buckets of 10 and 20.
- If the launcher stops answering (three polls), the mod gives the game back by itself.

## 7. Without the launcher

Mod-only players (mkwmp-mod.zip in `mods/`, the mod signs in itself) have no launcher running. Options:

| | (a) start on demand | **(b) pre-warm** | (c) composite in the game | (d) UI in the mod |
|---|---|---|---|---|
| How | the trigger starts `mkwmp-launcher.exe` | the mod starts it at game start, windowless; the trigger only shows it | the launcher renders offscreen, the mod draws it in the game's window and forwards input | ImGui-like pages in the mod |
| Latency of the trigger | the launcher's cold start: an estimate of 1-2 s on Windows and 4-8 s under Proton (self-contained single-file WPF: unpack, runtime start, WPF's first frame; **not measured**) | the time to show a window: one frame | one frame | one frame |
| The shrink animation | starts seconds late, on a stale picture | as designed | possible, but at the frame rate of the offscreen render | none of the launcher's look |
| Windows | works | works | works | works |
| Deck Game Mode | works if the new window gets the focus | the same window rule as section 4 | the only one with a single window | single window |
| Cost | small | small (built) | very high: WPF's `RenderTargetBitmap` is software rendering (a 1280 x 720 frame of this UI is tens of ms, so 15-25 fps at best), a new API entry to draw a full-screen texture, every input forwarded, text entry, focus | the whole launcher UI twice, in two toolkits |
| Memory while playing | none | one idle WPF process (about 100-150 MB) | the same plus frames | none |

Decision (the user's): **(b)**, with (a) only as the restart when the host is gone. (c) and (d) are not built. The mod
already has a small (d) in mkw-recomp's menu (the MKWMP, Voice chat and Camera tabs), which stays the fallback when
no launcher can be found.

How it works:

- **When.** `overlay_install` runs in `mkw_plugin_init`; its thread starts the host at once, so the overlay also works
  offline. Not when mkwmp.toml names a launcher that answers: that launcher started the game and is the overlay
  (`backend_plugin.c` has already dropped a leftover mkwmp.toml whose launcher is not running).
- **Where the launcher is**, first hit: the mod's setting `launcher_path`; `launcher_exe` of mkwmp.toml (the launcher
  writes it beside `launcher`; a leftover file still tells); `"exe"` of `%LOCALAPPDATA%\mkwmp\launcher.json`, which
  the launcher writes at every start (so one run of the launcher is enough for a mod-only player);
  `mkwmp-launcher.exe` in the mod's folder. None: the overlay is off, said once in `mkwmp.log`, and every button stays
  the game's.
- **Under Proton** every one of these is a path of the game's prefix, and `%LOCALAPPDATA%` is the prefix's. If the
  launcher was only ever run in another prefix (its own Steam shortcut), the game's prefix has no `launcher.json` and
  the mod finds nothing: the overlay is off there. The ways out: start the game from the launcher (one app, one
  prefix; mkwmp.toml then names it), or set `launcher_path` to the executable through `Z:\`, or put the launcher's
  folder into the mod's. The launcher can write that setting itself when it installs the mod
  (`Targets/MkwRecomp/ModSettings.cs`).
- **The command.** `mkwmp-launcher.exe --overlay-host --parent-pid <pid> --session "<mod folder>/mkwmp-overlay.toml"`,
  started detached in the launcher's folder with the game's environment (so Steam's app id under Proton).
- **Host mode.** No window, no tray icon, no seat (the mod holds the account's seat), no update prompt, no autostart
  of a game. It serves `/local/v1/overlay/...` and exits when the parent game does (`OverlayHost.ParentExited`).
- **Ready.** The launcher writes the session file (the local API's address and token) when its API is up; the mod
  reads it at its next probe (every 3 s until armed) and is armed by a `state` answer with `enabled:true`.
- **Single instance.** A named mutex. A second start reads `launcher.json` and hands over to the first:
  `POST /local/v1/instance/show` (the player opened the launcher: the host shows its window and is from then on the
  ordinary launcher, staying after the game) or `POST /local/v1/instance/host` (a mod asked for a host while the
  launcher is open: that launcher writes the session file and serves the game). `OverlayStartup.Begin` does both.
- **A host that died.** A refused trigger starts it again (at most once in 15 s) and, meanwhile, hands the button to
  mkw-recomp's menu.
- The mod's setting `overlay = 0` turns all of it off.

Open for the coordinator / Core: what the launcher shows in host mode needs the player's account. The mod's settings
(`mod-settings/mkwmp.ini`: `server`, `pin`, `pin_name`) are readable with `ModSettings`; whether a second presence
sign-in beside the mod's seat is accepted by the seat server is not checked here.

## 8. What is built, and the shell's wiring

In the game (`native/probe`):

| File | What |
|---|---|
| `src/overlay.h`, `src/overlay.c` | the gate (pure), the state parser, the pad filter, the picture, the request and the wait, the SDL3 watch for API 3, the overlay host's start |
| `src/mkw_plugin.h` | the header of plugin API 4 (the fork patch's) |
| `src/backend_plugin.c` | `overlay_install(api)` |
| `src/plat.h`, `plat_win.c`, `plat_posix.c` | `PLAT_KEY_SHIFT` / `PLAT_KEY_TAB`, `plat_process_id`, `plat_allow_foreground`, `plat_spawn` |
| `src/config.h`, `config.c` | `launcher_exe` |
| `build_plugin.py` | `overlay.c` in the plugin's sources |
| `tests/test_overlay.c` | the gate and the parser |

In the launcher:

| File | What |
|---|---|
| `src/Mkwmp.Core/Overlay/OverlayBridge.cs` | `OverlayBridge`, `OverlayRequest`, `OverlayFrame`, `OverlayTrigger`, `OverlayReturn` |
| `src/Mkwmp.Core/Overlay/OverlayHost.cs` | `OverlayHostArgs`, `LauncherRecord`, `SingleInstance`, `OverlayHost` |
| `src/Mkwmp.Core/Launcher.Overlay.cs` | `Launcher.OverlayBridge`, `.OverlayHost`, `.PublishRecord()`, `.AttachOverlayHost()` |
| `src/Mkwmp.Core/Net/LocalApi.cs` | the four routes, the query string, the picture's body limit (additive) |
| `src/Mkwmp.App/Overlay/GameWindow.cs` | `Find`, `IsForeground`, `ShowOver`, `GiveBack`, `ToBitmap`, `Capture` |
| `src/Mkwmp.App/Overlay/GuideWatcher.cs` | the Guide button by XInputGetStateEx |
| `src/Mkwmp.App/Overlay/OverlayStartup.cs` | single instance and host mode for `App.xaml.cs` |
| `tests/Mkwmp.Core.Tests/OverlayTests.cs` | the routes over `HandleAsync` and a socket, the host, the record, the instance |

The shell (what is wired; the screenshots of every moment come from the harness, DESIGN.md §11):

| File | What |
|---|---|
| `src/Mkwmp.App/App.xaml.cs` | `OverlayStartup.Begin` first (single instance, hand-off); host mode: no window, no tray, `LauncherOptions.OverlayHost`; `startup.Attach` once the local API is up; a host the player opens becomes the ordinary launcher (`Launcher.LeaveOverlayHostMode`, the tray) |
| `src/Mkwmp.App/Overlay/OverlayController.cs` | the bridge's events on the UI thread: request → picture → `EnterOverlay` → the window over the game → the shrink; the way back; `bridge.Enabled`; the launcher's window hidden while its game is in front and shown again when the game ends; the Guide watch |
| `src/Mkwmp.App/MainWindow.Overlay.cs`, `MainWindow.xaml` (`OverlayFx`) | the animation, and the window borderless over the game's picture (`GameWindow.ClientPixels` / `Place`) |
| `src/Mkwmp.App/Overlay/OverlayArt.cs` | the card drawn when the game sent no picture; the demo's frame |
| `src/Mkwmp.App/ViewModels/ShellViewModel.Overlay.cs` | `IsOverlay`, `OverlayThumb`, `OverlayHasMenu`, `EnterOverlay(frame, again)`, `LeaveOverlay(toMenu)`, `EndOverlay()` |
| `src/Mkwmp.App/ViewModels/HomeViewModel.cs`, `Views/HomePage.xaml(.cs)` | the Play tile as "Back to the Game" (wide, the picture, "In game"), B on home, the "mkw-recomp menu" dock button, the parts the animation moves |
| `src/Mkwmp.Core/Launcher.Overlay.cs`, `Launcher.Background.cs`, `LauncherOptions.cs` | host mode in Core (below); `FindGamePid`, `IsGameProcess`; `launcher_exe` in mkwmp.toml |

How it behaves:

- **A request** (`bridge.Requested`, any trigger): the picture is the mod's, else `GameWindow.Capture` (Windows
  desktop only, taken before the window shows), else a drawn card with the game's name. The shell goes home in overlay
  mode, the window becomes borderless exactly over the game's client area (the whole screen when that is unknown), the
  picture is the first thing drawn, `GameWindow.ShowOver`, then the shrink. Under Wine a window that is still visible
  is hidden first, so that the show is a map (gamescope's rule, section 4).
- **Overlay mode**: home's Play tile is "Back to the Game" with the picture; A on it, B on home, the dock's first
  button or Guide (`GuideWatcher`) go back. PLAY / STOP start and stop nothing. "mkw-recomp menu" (dock) is there when
  the game said `menu=1` and returns with `OverlayReturn.Menu`. Friends and Settings (with Voice & Camera) work as always; the
  game's settings say "close the game" because the target sees it running.
- **Back**: the picture grows from the tile over the stage, `GameWindow.GiveBack` hides the launcher and focuses the
  game, `bridge.ReturnToGame`. The launcher's window is then hidden, on Windows too.
- **Without the animation** the game still gets its pad back: the window was hidden by something else (the tray),
  minimised, closed (quit from the overlay), or the game took the front by itself (Alt+Tab).
- **A game the launcher started with PLAY**: once its window is up the launcher's window hides (one visible window at a
  time); when the game ends it shows again, and `bridge.GameExited()` is called. The tray icon and a second start of
  the launcher bring the window back meanwhile, and it then stays.
- **`bridge.Enabled`** is true once the first-run setup is done and the 18+ answer given, and false on the setup page
  and while an update downloads or restarts. A host for a player who never set the launcher up is therefore disabled:
  the mod leaves every button to mkw-recomp (open question of section 7).
- **A game of another Proton prefix** (its own Steam shortcut, the launcher only holding the seat) can reach the local
  API but no window of ours can be shown over it. Under Wine a request whose pid is no process of this prefix is
  answered by an immediate `ReturnToGame` and the overlay is disabled until that game ends.
- **Host mode** (`--overlay-host`, section 7): no window (`ShutdownMode.OnExplicitShutdown`), no tray, no
  notifications. Core with `LauncherOptions.OverlayHost`: no update check, no sync of the game's integration (the
  game runs), no start-up entry; the local API, the server's status and the friends list run as always, since that is
  what the overlay shows. PLAY is refused while the launcher stands by for a game (`Strings.GameRunsAlready`: that
  game holds its own seat). `--session <...>.toml` and `--parent-pid` are kept out of Core's arguments
  (`App.CoreArguments`: the session file ends in .toml too). The player starting the launcher meanwhile:
  `instance/show`, the window appears, the tray is made, and what was held back runs once
  (`Launcher.LeaveOverlayHostMode`); it then stays when the game ends.
- **mkwmp.toml** has `launcher_exe = "C:/.../mkwmp-launcher.exe"` after `launcher_token` (`Probe.SessionText`), left
  out when the launcher runs from the dotnet host.

The animation (`MainWindow.Overlay.cs`): one number, 0 to 1, drawn by `ApplyFx` from a `CompositionTarget.Rendering`
handler whose clock starts at the first frame drawn. Per frame it sets the rectangle and corner radius of one clip
(`RectangleGeometry`), a scale and a translation on the picture, and the opacity and translation of the top bar, the
other tiles (60 ms apart), the status line, the dock and the hint bar: no layout pass. The target rectangle is the
Play tile's card as it is drawn at that moment (`TransformToAncestor` to the stage, so the stage's scale, the
carousel's glide and the tile's focus pop are in it). Open: 480 ms, of which 380 ms of travel with a cubic ease-out
to 98 % of the tile and 100 ms settling to its size; the tile and its focus ring come up under the picture at two
thirds, then the picture fades off it, so title and badge appear without a pop. Back: 350 ms, cubic ease-in-out,
home fades in the first 60 %. With something over home (a page, the friends panel) the picture grows from a card in
the middle. A watchdog ends either animation if nothing is rendered: the game is always given back.

## 9. To test live, and the open risks

Nothing below has been run. The mod builds (`build_plugin.py --target all`, both libraries, `-Werror`),
`tests/test_overlay.c` passes on Windows, and the launcher's tests pass; `test_plugin.c` and the other Linux tests
were not run here (they need a Linux host).

What was run for the shell, without a game: the animation's moments rendered by the harness (DESIGN.md §11), and
once a host beside a stand-in window (`--overlay-host --parent-pid` of a small WinForms process, a request over the
local API): no window while standing by, the session file written, `enabled:true`, the window borderless exactly
on the stand-in's client area after the request, and the launcher gone (session file deleted) when the stand-in was
killed. Not seen there: the foreground switch (the stand-in was not the foreground window), the way back by a
button, the motion on a real screen.

1. **The fork patch.** It was written against the source and never compiled. Build the fork with it, then check: the
   filter sees every read; the menu does not open on Guide or Select+Start; a short Select still does what it did in
   the game; `frame_capture` returns a picture in SDR and HDR, windowed and full screen.
2. **Windows desktop, API 3** (the installed 0.1.15-mkwmp, no fork build needed): Guide and Select-hold open the
   launcher; the launcher takes the foreground over a borderless and a full-screen game; the game reads nothing while
   it is up; the return focuses the game. No picture is expected from the mod (`GameWindow.Capture` gives one).
3. **Steam Input on Windows**: which of Guide, Select+Start and Shift+Tab reach the game (the table of section 2).
4. **Steam Deck, Game Mode** (the hard requirement, and the largest risk):
   - the launcher's window, shown, wins gamescope's focus over the game, and hiding it gives the game back. The
     source says so (`map_sequence`); a WPF window under Proton has not been seen doing it. If the launcher's window
     is not given the app's game id, or WPF's `Hide()` does not unmap under Wine, (b) fails there and (c) is the only
     single-window answer;
   - the game ignores the pad while the launcher is shown (SDL's focus rule under Wine) on API 3;
   - Select+Start reaches the game there; Select-hold does;
   - the overlay host: started by the mod, found through `launcher.json` in the same prefix, same app id, ready in a
     few seconds, gone with the game;
   - whether an XInput read by the launcher (WPF) works in the same prefix while the game holds the pad through SDL.
5. **Exclusive full screen on Windows**: what the game does when it loses the foreground and whether it comes back
   by `GiveBack` alone.
6. **The host's cold start** on Windows and under Proton (the numbers in section 7 are estimates).
7. **A second sign-in** by the host beside the mod's seat (section 7, last paragraph).
