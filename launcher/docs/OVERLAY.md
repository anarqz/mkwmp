# The console-style overlay

In the game, the player's overlay shortcut (Settings › Game, "In-Game Overlay"; by default the Guide button, or
Select+Start, or Select held for a second: section 2.1) brings the MKWMP launcher over the game
the way the handheld console's HOME button does: the picture the game was showing shrinks into the Play tile of Home, the
player looks at friends or settings, and "back to the game" grows the thumbnail back to the full screen.
mkw-recomp's own menu does not open on that button.

Status: built and wired end to end (the mod's part, the launcher's Core, the window helpers, the shell's overlay mode
and its animation, the contract, tests): section 8. Nothing here has run against a live game yet: section 9 lists what
must be tested.
Sources quoted as `fork:` are anarqz/mkw-recomp at tag `v0.1.15-mkwmp`; `SDL:` is libsdl-org/SDL `main`;
`gamescope:` is ValveSoftware/gamescope `master` (both read on 2026-11-20).

**The game draws, the launcher decides** (the user's call, 2026-11-27; ported from Infinity's v4 overlay). Two things
are now served to the mod as data, for it to draw inside the game's own frames while the game keeps running, focused
and full screen (docs/contracts/LAUNCHER_API.md, "In-game toasts (v1)" and "In-game overlay (v1)"):

- **Toasts in a race**, at the top right: `GET /local/v1/toasts` (a long poll on `seq`), from
  `Mkwmp.Core/Overlay/ToastBridge.cs`. `Mkwmp.App/Overlay/InGameToasts.cs` publishes what the launcher's ToastHost
  shows while the launcher's window is behind a running game. Until a mod polls (none for 15 s), a small click-through,
  never-activated, topmost window at the top right of the game's monitor shows them instead (Windows only).
- **The overlay page**: `GET /local/v1/overlay/view` and `POST /local/v1/overlay/action`, from
  `Mkwmp.Core/Overlay/GameOverlay.cs`: Game (Back to the Game, Quit Game) and **Current Lobby** (the players of the
  lobby the game is in now, the player's own row, a flag on whoever used more than one name in this online session,
  Report). While a mod polls the view, the launcher's own shortcut watcher stays off for that game.

Both are built on the launcher's side only; the mod's side (the polls and the drawing) is not written yet, so the
window overlay below is still what every player gets. It stays as the way for a game whose mod does not draw. In
overlay mode the launcher's own window opens on its Current Lobby page while the game is in an online lobby (B: home,
with Back to the Game). The launcher stores nothing about other players ("Previous Races" of 1.5.4 is removed).
## 0. Summary of the decisions

| # | Question | Decision |
|---|---|---|
| 1 | Keeping mkw-recomp's menu shut | The mod filters the pad before the runtime's menu shortcut sees it. That needs a small fork change, **plugin API 4** (`input_filter`, `menu_open`, `frame_capture`, then `focus_set` and `quit`; patch in `overlay-plugin-api4.patch`). On the released 0.1.15-mkwmp (API 3) the mod can only watch: it leaves the trigger mkw-recomp uses to mkw-recomp. |
| 2 | Who sees the button | The mod, on the game's own reading of the pad (after Steam Input). Fallback for a game without the mod's overlay: the launcher's `GuideWatcher` (XInputGetStateEx). |
| 3 | The picture | In the game, from the swapchain image (API 4 `frame_capture`), RGBA at most 1280 x 720, in the body of the request. Launcher-side capture only as a Windows-desktop fallback; it cannot work under Proton. |
| 4 | Front and back | One visible window at a time: the launcher's window is hidden while the game is in front, shown for the overlay, hidden again on return. That is what Windows' foreground rules and gamescope's focus choice both accept. |
| 5 | Pause | As a console's HOME menu: the mod tells the runtime the game is out of focus (plugin API 4 `focus_set`), and the runtime suspends the game when the game's own `SetFocusHandlingMode` lets the console do that. MKW asks for it except while it uses the network, so an offline race stops and an online one runs on. "Quit Game" ends the game through the runtime (`quit`). |
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
| The launcher sets `menu_shortcut` to the value we do not use (`IGameTarget.WriteGameSettings`) | Frees one trigger only, and the freed one is not swallowed: with `guide` set, Select+Start reaches the game as Minus and Plus (Plus pauses). Not enough by itself; since 1.4.3 it is done besides the filter (section 1.1), so the two never both claim a press. |
| A host override (`override_symbol`) of the game's input read | The shortcut is decided in the runtime before any guest call; an override of `nn::hid` sees the pad after it. Cannot stop the menu. |
| A new config value (`menu_shortcut = off`) | One line in the fork, but the mod still cannot hold Select back from the game or make the pad idle, and it needs its own second reading of the pad. |
| **A pad filter in the plugin API** | Chosen. The mod sees and edits the reading before the runtime's shortcut and the game do. One mechanism gives the trigger, the swallow, the idle pad and "the B that closed it never reaches the game". |

The fork patch (`launcher/docs/overlay-plugin-api4.patch`, the diff of the fork's branch `overlay-api4` against
`v0.1.15-mkwmp`; the same two commits are on `overlay-api4-nup`, both built by the fork's CI): plugin API version 4
with five entries at the end of the table, the last two added after the first live test (section 5).

- `int input_filter(MkwPadFn filter, void *user)`: `filter(MkwPad *pad, user)` at every read of a player's input,
  before `input_menu_gate` and before the game (`input.c` `poll_merged` and `input_poll_player`). `MkwPad` has the 16
  buttons as the game gets them, the sticks, `guide` and the player index.
- `void menu_open(int open)`: opens or closes the runtime's menu (`menu_toggle` when the state differs).
- `int frame_capture(int max_w, int max_h, MkwFrameFn cb, void *user)`: section 3.
- `int focus_set(int in_focus, int suspend)` and `void quit(void)`: section 5.

The mod's header `native/probe/src/mkw_plugin.h` is the patched one; the entries are used only when the runtime's
table reaches them (`api->size`, each checked by itself), so the same mod runs on API 3 and on an API 4 runtime built
before `focus_set` and `quit` (there the game runs on behind the overlay, and "Quit Game" is the launcher closing the
game's window).

**The player still reaches mkw-recomp's menu**: F1 and Esc are untouched; with API 4 the overlay can offer a
"Game settings (mkw-recomp)" button that calls `OverlayBridge.ReturnToGame(OverlayReturn.Menu)`, and the mod then
calls `menu_open(1)`; and whenever the launcher cannot open (`409`, no answer) the mod opens the menu itself for a
Guide or Select+Start press it had taken. With API 3 nothing is taken from mkw-recomp, so its own shortcut still works.

### 1.1 mkw-recomp's `menu_shortcut` is the launcher's (1.4.3)

The player's call (2026-11-21): the overlay is the only controller menu. The launcher owns `menu_shortcut`
(`IGameTarget.OverlayOwnedSettings`, `RecompSettings.MenuShortcutFor`): it is written to mkw.ini before every start,
online or offline, and as soon as the overlay shortcut changes in Settings (when the game is closed: the runtime reads
mkw.ini only at start, its "live" means its own menu's in-memory change). The row is not shown in Settings ›
Controls, and it is not part of "reset all".

mkw-recomp has no "off" (`fork: runtime/core/base/cfg.c:135`, `minus_plus|guide`; any other text acts as
`minus_plus`), so the value is the one that leaves the overlay's buttons alone:

| Overlay shortcut | `menu_shortcut` | Why |
|---|---|---|
| Guide | `minus_plus` | the overlay has Guide; Select+Start stays mkw-recomp's |
| Guide or Select + Start (default) | `guide` | both overlap; Guide is the harmless one: the mod's filter clears Guide from every reading before `input_menu_gate`, and Steam or the Game Bar usually take it first. As `minus_plus`, a short Select given back to the game while Start is held would open mkw-recomp's menu |
| every other entry | `guide` | none of them uses Guide; Select or Start are in most of them |

The default overlay shortcut is unchanged. mkw-recomp's own menu stays on F1 / Esc and on the overlay's
"mkw-recomp menu" entry (`menu_open`); the shortcut picker says so. `MKW_MENU_SHORTCUT` in the environment still wins
(the write is refused and logged). L3+R3 is not a `menu_shortcut` value: it is mkw-recomp's fixed performance-overlay
chord (`input_overlay_gate`), which the `l3_r3` entry takes over (its note says so).

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

"Usually" and "may" are to be confirmed live (section 9).

### 2.1 The player's shortcut

Which combination opens the overlay is the player's choice (Settings › Game › "In-Game Overlay", a list picker),
because no single one reaches the game everywhere. One entry is one combination:

| Entry (`overlay_shortcut`) | Triggers | Taken from the game | Conflicts the picker names |
|---|---|---|---|
| Guide or Select + Start (`guide_or_select_start`, default) | Guide, Select+Start, Select held 1 s: what the mod always did | Guide; Select (given back as a short press on release) | Steam / Game Bar take Guide; some Steam layouts take Select+Start |
| Guide (`guide`) | Guide | Guide | Steam's menu, Xbox Game Bar |
| Select + Start (`select_start`) | Select, then Start (either order) | Select (given back if no Start came) | Steam on some layouts; Steam Deck chords of the … button |
| Hold Select (`select_hold`) | Select held 1 s | Select (given back as a short press) | none known |
| Hold Start (`start_hold`) | Start held 1 s | Start (a short Start pauses on release, 60 ms late) | none known |
| L3 + R3 (`l3_r3`) | both sticks pressed | nothing until both are down; then both | mkw-recomp's own L3+R3 (it steps the performance overlay, F10 still does) |
| LB + RB + Start (`lb_rb_start`) | Start while LB and RB are held | nothing until all three are down (LB/RB already reached the game) | none known |
| Select + LB / Select + RB (`select_lb`, `select_rb`) | the bumper while Select is held | Select (given back if no bumper came) | none known |

The picker shows the generic warning ("Some combinations may also open Steam's or Xbox's own menus…") and, per entry,
a one-line note; the chosen entry's note is the row's caption. Shift+Tab always works besides.

Game controls (no control map of MKW is in this repo; this is the console's default layout as generally known, to
be confirmed live): the face buttons, both bumpers and triggers while driving (items, drift/hop), Plus to pause,
Minus in menus; stick presses have no driving role. So a combination built on Select or Start is held
back from the game only as long as it is incomplete, a bumper alone passes at once, and L3 or R3 alone passes at
once. Nothing here depends on the face buttons, so the Switch / Xbox layout swap (A and B, X and Y exchanged by
mkw-recomp's `pad_layout` or by the launcher's own pad layout) does not touch any entry.

How it travels: client.toml `[ui] overlay_shortcut = "<name>"`; `OverlayBridge.Shortcut` (set by
`OverlayController` from the settings) adds `"shortcut":"<name>"` to every overlay state answer when it is not the
default (docs/contracts/LAUNCHER_API.md). The mod reads it at every answer (`overlay_parse_state`; the 10 s probe,
and the long poll while the overlay is up) and the pad filter switches at its next read (`overlay_gate_set_combo`,
which also makes sure buttons already down do not fire the new combination). **A change applies to the running
game within about 10 s**, no restart. A mod without the choice ignores the key and keeps the default; a name the mod
does not know is the default too. The matcher is `overlay_gate` with the table `kCombos` (`overlay.c`), tested in
`tests/test_overlay.c` (each entry, the switch, the parser).

On plugin API 3 the mod only watches (no filter): the chosen combination still opens the overlay, but its buttons
also reach the game (a Start hold pauses first), and Select+Start stays mkw-recomp's menu as before.

Fallback, for a game without the mod's overlay (the winmm probe, an older mod): the launcher's
`Overlay/GuideWatcher.cs` polls XInputGetStateEx (xinput1_4 ordinal 100, which reports Guide even unfocused) and the
shell opens the overlay itself with `OverlayBridge.RequestFromLauncher` when the game's window is the foreground one.
It looks for the player's shortcut (`OverlayShortcutMatcher` in Core; for the default entry only Guide, since
Select+Start is mkw-recomp's own menu there), but only sees it: XInput without a hook cannot take the buttons from
the game, mkw-recomp's menu is not kept shut, and it does nothing under Steam. With the overlay up, Guide (and B on
home) goes back whatever the shortcut. The winmm probe gets no trigger of its own: the mod wins, and the probe would need a detour
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

### 4.1 A Linux desktop under Proton (KDE Plasma, Steam Deck Desktop Mode)

Seen on 2026-11-21 (Debian 12, KDE Plasma on Wayland with Xwayland, NVIDIA): the chord, the picture and the suspend
worked (`mkwmp.log`: `out of focus and suspended`, `opened by chord (with the picture)`), but the launcher's window
opened **behind** the full-screen game, so the suspended game looked frozen; Alt+Tab showed the overlay.

Why: on a desktop the window manager decides who is in front, and KWin's focus-stealing prevention (default level
Low) refuses a window of an application the player has not touched since the game took the front.
`AllowSetForegroundWindow` is Windows' rule and means nothing to it. What Wine asks for depends on its version
(`wine: dlls/winex11.drv/window.c`):

- Proton 9 and older: `SetForegroundWindow` of a managed window sends nothing to the window manager; the map alone
  asks, with the launcher process's own `_NET_WM_USER_TIME` (its last X input: the click on PLAY, older than the
  game's activation). KWin maps it under the active window and marks it "demands attention".
- Proton 10: activation sends `_NET_ACTIVE_WINDOW` with source 2 (pager) and timestamp 0, which KWin treats as a
  tool's request and grants; but a pending map defers it, and `X11DRV_GetWindowStateUpdates` can take the foreground
  back from what the window manager reports. Not seen to fail; not proven to work either.

The fix (`OverlayFocus` in Core, `OverlayController.ToTheFront`, `GameWindow.StepAside` / `BringBack`): under Wine,
not in Game Mode, 150 ms after our window is shown (mapped, so managed), the **game's window is minimised**
(`ShowWindowAsync(SW_MINIMIZE)`, posted to the game's window thread, which runs also while the game is suspended).
Every stacking window manager lets the active window go away and then focuses the next window of its focus chain,
without asking focus-stealing prevention: ours, which KWin inserted just behind the active one. On the way back the
game is **restored first** (`SW_RESTORE`, waited for up to 300 ms + 60 ms), then ours is hidden: the game is then the
next in the chain. Hidden first, the focus would go to another window and the restored game could be refused the
front the same way. Every way back restores it: the animation, Alt+Tab or the tray (`QuickReturn`), quitting from
the overlay. The picture was taken before (the mod's `frame_capture`), and the suspend does not depend on the window.

Not on Windows (the foreground rules already let the launcher in) and not in gamescope (it shows the window mapped
last; minimising is not its notion). `MKWMP_OVERLAY_MINIMIZE_GAME=0` turns it off, `=1` forces it under Wine also in
Game Mode (Steam launch options, or the environment of the launcher's process).

Under Wine the launcher also looks, 1.5 s after the show, whether it is in front (`OverlayFocus.KeptBehind`: the
game still the foreground window, or ours not active); deactivations before that are the window manager's, not the
player's Alt+Tab (`_settling`). If it was kept behind it logs `overlay: not in front after 1.5 s (...)` and, when the
player reaches the window by hand, shows once per run the hint `overlay.kept_behind`: the KWin window rule.

**The fallback for the player**, when the game still stays in front (a window manager that does not focus the next
window, an online race where the game keeps presenting): System Settings › Window Management › Window Rules › Add New,
"Detect Window Properties" and click the launcher (under Proton the class is Wine's `steam_app_<id>`, shared with the game when
both run in one app: harmless), then Add Property › "Focus stealing prevention", Force, None. Steam Deck Desktop
Mode is KDE too: the same rule.

Risks: a minimised game that keeps running (online, the mod's `overlay_pause = 0`) presents to an iconic window: the
swapchain is out of date until the restore, as on Windows when a full-screen game is minimised. A launcher that dies
while the overlay is up leaves the game minimised: the mod gives the pad back after three polls, and the player
restores the window from the task bar.

The game's input while the overlay is up:

- SDL records no pad press for an application whose windows do not have the keyboard focus, unless
  `SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS` is set (`SDL: src/joystick/SDL_joystick.c`,
  `SDL_PrivateJoystickShouldIgnoreEvent`; releases still pass). The fork never sets the hint, and the keyboard is
  already ignored without focus (`fork: runtime/input/input_sdl.c`, `SDL_EVENT_WINDOW_FOCUS_LOST`). So an unfocused
  game reads nothing new, on Windows and under Wine.
- That is the only protection on API 3. With API 4 the mod's filter makes every player's pad idle from the trigger
  until the return and the release of every button, whether the focus moved or not.
- The launcher reads XInput only while its window is active (`Input/GamepadInput.cs`), so the two never both react.

## 5. Pause, and Quit Game

### Why the game ran on in the first live test

The runtime did tell the game. A window that loses the focus becomes `nn::oe::GetCurrentFocusState() == 2` and the
`FocusStateChanged` message (15) (`fork: runtime/gfx/nvn/host/gfx_window_sdl.c:202-205` →
`gfx_present.c` `gfx_window_focus_changed` → `runtime/sdk/ov/ov_system.c` `oe_set_focus`), from the end of the game's
startup logo on. `MKW_ASSUME_FOCUS=1` pins it to "in focus" (the site's Linux start line sets it; nothing on Windows
does), and the user's mkw.ini has no such key: the message was delivered.

What the game does with it (1.8.0 `main`, read from the disassembly, 2026-11-20):

- One thread (`main+0x2995710`) pops the notification messages. For message 15 it only stores the state under a
  mutex: out of focus sets two flags, back in focus sets a one-shot "resumed" flag. It calls nothing of the scene,
  the pause menu or the audio.
- The per-frame consumer (`main+0x36ac10`, from the root update `main+0x36ab50`) copies those flags, on "resumed"
  re-checks the network service account, and **sets `nn::oe::SetFocusHandlingMode` itself**: mode 1 when the predicate
  `main+0x36adb0` is true, else mode 2 (`main+0x872854` only sets the first 2: the log's `oe: focus handling mode 2`).
  The predicate reads the object whose code calls `nn::nifm::HandleNetworkRequestErrorResult` and
  `nn::account::EnsureNetworkServiceAccountAvailable`: the network connection's state.
- Read as the SDK names them, 2 is SuspendAndNotify and 1 is Notify: **on the console the pause behind HOME is the
  system suspending the application**, not the game pausing itself, and MKW switches that off while it uses the
  network. Offline, HOME freezes the game mid-race; online, the race runs on. mkw-recomp answered the mode call and
  suspended nothing, so the game ran on everywhere.

Established: the handler and the mode switch. Inferred: that the predicate's object is the network connection
manager, and the meaning of its field values. Not searched: whether anything else reads the copied out-of-focus flag
(an audio duck, say); nothing visible happened in the live test.

### What is built

Plugin API 4 gains two entries at the end of the table (`fork: runtime/plugin/mkw_plugin.h`, `plugin.c`):

- `int focus_set(int in_focus, int suspend)`. `focus_set(0, s)` is the HOME press: the game is told out of focus
  whatever the window says and also with `MKW_ASSUME_FOCUS` (`ov_system.c` `oe_hold_focus`; a window change while held
  is told when the hold ends), and the output is silent (`audio_mixer.c`, as `MKW_BACKGROUND_MUTE` does it). With
  `suspend` the runtime does the console's part: when the game's focus handling mode is not 1 the game thread waits
  in its present (`freeze_set`, `runtime/core/hacks/freeze.c`: the picture stays, the game's clock counts presents
  and stands still, audio and loading threads go on), decided at the hold and again at every `SetFocusHandlingMode`
  of the game. `focus_set(1, 0)` gives the focus back to the window and the game runs. It returns 1 when the game is
  suspended now.
- `void quit(void)`: `gfx_window_request_close()`, what the menu's Quit calls (`runtime/ui/menu/menu.cpp:348`):
  `plugins_exit` (the mod gives its seat back), `fs_save_flush`, the pipeline cache, `plat_exit(0)`
  (`gfx_present.c` `gfx_window_closed`). It runs on the window thread, so also while the game is suspended.

So the decision "pause or not" is the game's own, as on the console: nobody has to know whether a race is online.
The mod does not look at its seat for it. The mod's setting `overlay_pause = 0` asks for no suspension at all.

The mod (`native/probe/src/overlay.c`): `hold(1)` after the picture is taken and before the request (a suspended
game presents nothing); `hold(0)` in `closed()`, after the gate was told to keep the pad idle, on every way out (the
return, a refused request, a launcher that stopped answering). The B that returns and whatever is held never reach
the game: suspended, the game reads no input at all, and the first reading after the resume is already behind the
gate's "idle until every button is up". On a runtime without `focus_set` nothing changes: an idle pad, the game
running.

Limits: while the game is suspended the pad filter is not called, so a second trigger cannot bring the launcher back
if the game's window took the front by itself; the launcher notices that (`OnDeactivated`) and returns. mkw-recomp's
own menu is drawn by the present, so `"action":"menu"` resumes first. The launcher is not told whether the game is
suspended: home says "the game is running" either way.

### Quit Game

In overlay mode the dock has "Quit Game" and the Power menu lists it above "Quit MKWMP"; both ask first ("Quit MKW? Progress since the last save is lost.", plus the seat sentence when a server is set). Then
(`OverlayController.QuitGameAsync`):

1. `OverlayBridge.ReturnToGame(OverlayReturn.Quit)`: the mod's wait ends with `"action":"quit"` and it calls the
   runtime's `quit()`. The game stays held meanwhile (no frame, no sound). The launcher shows "Closing the game…".
2. After 8 s (at once when the game did not say `quit=1`, or the overlay is not open): WM_CLOSE to the game's window
   (`GameWindow.RequestClose`), which ends mkw-recomp the same clean way; 5 s more.
3. Still running: "The game is not closing" with Force Quit (`GameWindow.Kill`: the process tree, by the pid the mod
   told, which under Wine is the Wine-side one) or Keep Waiting.
4. Gone: the bridge forgets the game, overlay mode ends and the window is the ordinary launcher's again. A host the
   mod started (no window before the trigger) exits with its game, as it always does (`OverlayStartup`). The seat is
   given back by whoever held it: the mod's `on_exit`, or the launcher's session seeing the game end.

"Quit MKWMP" while a game runs (the overlay, or a game the launcher sees beside it) asks which: "Quit the Game Too"
(the same steps, then the launcher quits) or "Quit MKWMP Only" (the game runs on, the overlay stops working). The
tray's Quit still quits the launcher alone. A game of another Proton prefix has no pid in this prefix: it gets no
overlay (section 8) and so no Quit Game.

## 6. The contract

`docs/contracts/LAUNCHER_API.md`, sections "The overlay" and "The overlay host". In short:

- `GET /local/v1/overlay/state` answers `{"open","seq","enabled"[,"action"]}`; with `?wait=S&seq=N` it is held until
  the state changes. The mod asks it every 10 s to know whether to take any button at all (`enabled`), and in a loop
  of `wait=2` while the overlay is up.
- `POST /local/v1/overlay/open?by=..&pid=..[&menu=1][&w=..&h=..&format=rgba]`, body the picture. `200` open, `409`
  not now. `menu=1` (the mod sends it when the runtime has `menu_open`) is `OverlayBridge.GameHasMenu`: the shell
  shows its "mkw-recomp menu" button only then.
- Closing: `OverlayBridge.ReturnToGame()` ends the mod's wait. `action` `menu` asks for mkw-recomp's menu, `quit`
  for the game's end (`quit=1` in the request: the runtime can do it itself).
- Same bearer as every route; a picture is read only from a caller with the token; buckets of 10 and 20.
- If the launcher stops answering (three polls), the mod gives the game back by itself.

## 7. Without the launcher

Mod-only players (mkwmp-mod.zip in `mods/`, the mod signs in itself) have no launcher running. Options:

| | (a) start on demand | **(b) pre-warm** | (c) composite in the game | (d) UI in the mod |
|---|---|---|---|---|
| How | the trigger starts `mkwmp.exe` | the mod starts it at game start, windowless; the trigger only shows it | the launcher renders offscreen, the mod draws it in the game's window and forwards input | ImGui-like pages in the mod |
| Latency of the trigger | the launcher's cold start: an estimate of 1-2 s on Windows and 4-8 s under Proton (self-contained single-file WPF: unpack, runtime start, WPF's first frame; **not measured**) | the time to show a window: one frame | one frame | one frame |
| The shrink animation | starts seconds late, on a stale picture | as designed | possible, but at the frame rate of the offscreen render | none of the launcher's look |
| Windows | works | works | works | works |
| Deck Game Mode | works if the new window gets the focus | the same window rule as section 4 | the only one with a single window | single window |
| Cost | small | small (built) | very high: WPF's `RenderTargetBitmap` is software rendering (a 1280 x 720 frame of this UI is tens of ms, so 15-25 fps at best), a new API entry to draw a full-screen texture, every input forwarded, text entry, focus | the whole launcher UI twice, in two toolkits |
| Memory while playing | none | one idle WPF process (about 100-150 MB) | the same plus frames | none |

Decision (the user's): **(b)**, with (a) only as the restart when the host is gone. (c) and (d) are not built. The mod's
small (d) in mkw-recomp's menu (the MKWMP, Voice chat and Camera tabs) is gone since 1.4.3: everything is set in the
launcher, which writes the mod's settings while the game is closed, also right after a game it stood by for ends (so
an account signed in over the game reaches the mod at its next start). Without a launcher to find, a mod-only player
has no way to sign in.

How it works:

- **When.** `overlay_install` runs in `mkw_plugin_init`; its thread starts the host at once, so the overlay also works
  offline. Not when mkwmp.toml names a launcher that answers: that launcher started the game and is the overlay
  (`backend_plugin.c` has already dropped a leftover mkwmp.toml whose launcher is not running).
- **Where the launcher is**, first hit: the mod's setting `launcher_path`; `launcher_exe` of mkwmp.toml (the launcher
  writes it beside `launcher`; a leftover file still tells); `"exe"` of `%LOCALAPPDATA%\mkwmp\launcher.json`, which
  the launcher writes at every start (so one run of the launcher is enough for a mod-only player);
  `mkwmp.exe` in the mod's folder. None: the overlay is off, said once in `mkwmp.log`, and every button stays
  the game's.
- **Under Proton** every one of these is a path of the game's prefix, and `%LOCALAPPDATA%` is the prefix's. If the
  launcher was only ever run in another prefix (its own Steam shortcut), the game's prefix has no `launcher.json` and
  the mod finds nothing: the overlay is off there. The ways out: start the game from the launcher (one app, one
  prefix; mkwmp.toml then names it), or set `launcher_path` to the executable through `Z:\`, or put the launcher's
  folder into the mod's. The launcher can write that setting itself when it installs the mod
  (`Targets/MkwRecomp/ModSettings.cs`).
- **The command.** `mkwmp.exe --overlay-host --parent-pid <pid> --session "<mod folder>/mkwmp-overlay.toml"`,
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
| `tests/test_overlay.c` | the gate, every shortcut entry, and the parser |

In the launcher:

| File | What |
|---|---|
| `src/Mkwmp.Core/Overlay/OverlayBridge.cs` | `OverlayBridge`, `OverlayRequest`, `OverlayFrame`, `OverlayTrigger`, `OverlayReturn` |
| `src/Mkwmp.Core/Overlay/OverlayHost.cs` | `OverlayHostArgs`, `LauncherRecord`, `SingleInstance`, `OverlayHost` |
| `src/Mkwmp.Core/Launcher.Overlay.cs` | `Launcher.OverlayBridge`, `.OverlayHost`, `.PublishRecord()`, `.AttachOverlayHost()` |
| `src/Mkwmp.Core/Net/LocalApi.cs` | the four routes, the query string, the picture's body limit (additive) |
| `src/Mkwmp.App/Overlay/GameWindow.cs` | `Find`, `IsForeground`, `ShowOver`, `GiveBack`, `ToBitmap`, `Capture`; `Alive`, `RequestClose`, `Kill` (Quit Game) |
| `src/Mkwmp.Core/Overlay/OverlayShortcut.cs` | `OverlayShortcut` (the choices, their wire names), `OverlayShortcutMatcher` (the launcher's own reading) |
| `src/Mkwmp.Core/Overlay/OverlayFocus.cs` | when the game's window steps aside on a Linux desktop (section 4.1), and the "kept behind" check; tests in `OverlayFocusTests.cs` |
| `src/Mkwmp.App/Overlay/GuideWatcher.cs` | the Guide button and the player's shortcut by XInputGetStateEx |
| `src/Mkwmp.App/ViewModels/SettingsViewModel.Shortcut.cs` | the Settings › Game row and its picker |
| `src/Mkwmp.App/Overlay/OverlayStartup.cs` | single instance and host mode for `App.xaml.cs` |
| `tests/Mkwmp.Core.Tests/OverlayTests.cs` | the routes over `HandleAsync` and a socket, the host, the record, the instance |

The shell (what is wired; the screenshots of every moment come from the harness, DESIGN.md §11):

| File | What |
|---|---|
| `src/Mkwmp.App/App.xaml.cs` | `OverlayStartup.Begin` first (single instance, hand-off); host mode: no window, no tray, `LauncherOptions.OverlayHost`; `startup.Attach` once the local API is up; a host the player opens becomes the ordinary launcher (`Launcher.LeaveOverlayHostMode`, the tray) |
| `src/Mkwmp.App/Overlay/OverlayController.cs` | the bridge's events on the UI thread: request → picture → `EnterOverlay` → the window over the game → the shrink; the way back; `bridge.Enabled`; the launcher's window hidden while its game is in front and shown again when the game ends; the Guide watch |
| `src/Mkwmp.App/MainWindow.Overlay.cs`, `MainWindow.xaml` (`OverlayFx`) | the animation, and the window borderless over the game's picture (`GameWindow.ClientPixels` / `Place`) |
| `src/Mkwmp.App/Overlay/OverlayArt.cs` | the card drawn when the game sent no picture; the demo's frame |
| `src/Mkwmp.App/ViewModels/ShellViewModel.Overlay.cs` | `IsOverlay`, `OverlayThumb`, `OverlayHasMenu`, `EnterOverlay(frame, again)`, `LeaveOverlay(toMenu)`, `EndOverlay()`; `GameRuns`, `QuitGameAsync(thenQuit, confirmed)` |
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
- **Behind the overlay** the game is held as behind a console's HOME menu, and **Quit Game** (the dock, the Power
  menu) ends it: section 5. Harness: `--overlay --modal quitgame | quitgame-closing | quitgame-stuck | power |
  quit-which`.
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
- **mkwmp.toml** has `launcher_exe = "C:/.../mkwmp.exe"` after `launcher_token` (`Probe.SessionText`), left
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

0. **Pause and Quit Game** (section 5; built after the first live test on Windows, which showed the triggers, the
   shrink and the return working; `focus_set` / `quit` compile in the fork's CI and have not run against a game):
   - offline (a race against the computer, a menu): the overlay opens, the game's picture and sound stop
     (`mkw.log`: `oe: the game is suspended`; `mkwmp.log`: `out of focus and suspended`); the return resumes on the
     same frame and neither B nor a held button reaches the game;
   - online (after Online Play was opened: a lobby, a race): `mkw.log` says `oe: focus handling mode 1` before the
     trigger and `the game runs on out of focus`; the race goes on and nobody is dropped. If the game is still in
     mode 2 anywhere a session is alive, that is the risk of the design (the predicate's meaning is inferred): the
     mod's setting `overlay_pause = 0` is the way out until it is understood;
   - leaving Online Play while the overlay is not up, then opening it: suspended again;
   - Quit Game from the dock and from the Power menu: the game ends within a second or two, `mkwmp.log` has
     `seat: given back`, `mkw.log` ends with `gfx: window closed; exiting`, the hosted launcher is gone with it, a
     launcher the player had opened shows Home;
   - Quit MKWMP while the game runs: both answers;
   - an older runtime (the API 4 build without the two entries): the game runs on, Quit Game closes the window.
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
4b. **KDE Plasma under Proton** (section 4.1; also Steam Deck Desktop Mode): the overlay in front at once, the game
   minimised (the launcher's `logs/launcher-ui.log`: `the game's window is minimised`), the way back restores it full screen and
   focused, by the animation, by B, and after Alt+Tab; no `not in front` line. Offline (suspended) and online (the
   game presents while minimised).
5. **Exclusive full screen on Windows**: what the game does when it loses the foreground and whether it comes back
   by `GiveBack` alone.
6. **The host's cold start** on Windows and under Proton (the numbers in section 7 are estimates).
7. **A second sign-in** by the host beside the mod's seat (section 7, last paragraph).
