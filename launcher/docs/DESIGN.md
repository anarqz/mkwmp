# MKWMP Launcher: design system and shell

Everything a page needs is here. The app is `src/Mkwmp.App` (WPF, .NET 10, assembly `mkwmp-launcher`);
it binds only to `Mkwmp.Core.ILauncher`. Look at the result before reading: `.\run.ps1 --demo --windowed --page gallery`
shows every control and icon on one page.

## 1. Rules of the look

- A console's system menu on a TV or a handheld: light grey background, white rounded cards, near-black
  text, one blue accent for values and selection, thin grey rules, a lot of room, type readable at 3 m.
- **The focus card** is the signature: the focused control becomes a white card with a slowly turning
  blue / cyan / violet / pink border and a faint halo (`c:FocusRing`). Every focusable control style has
  it already. Never draw another focus indicator, never remove it.
- Ours only. No console maker's logos, glyphs, names or box art. The chat is called **Party Chat**. Button
  hints are plain discs with a letter; the controller picture is a generic gamepad. Icons are the vector
  geometries of `Themes/Icons.xaml`, drawn for this launcher. No icon fonts, no system fonts (Nunito, OFL,
  is inside the exe): the launcher must look the same under Wine/Proton on the Steam Deck.
- Cheap to draw: no `Effect` (blur, drop shadow), no `AllowsTransparency`, no bitmap caches of big areas.
  Animate transforms and opacity, briefly (120 to 240 ms). The only endless animations are the focus ring
  and `Spinner`.

## 2. The stage: units, not pixels

`MainWindow` scales its content so that **the stage is always 1080 units high**. A 16:9 screen is 1920
units wide, the Steam Deck (16:10) 1728; a window narrower than 1600 units scales by its width instead.
Lay out in units and never read the window's pixel size. Check a page at `1920x1080`, `1280x800` and
`2560x1440` (section 9): at 1280x800 you have 192 units less width, so let one column stretch (`*`)
instead of fixing every width.

Shell regions (all in `MainWindow.xaml`):

| Region | What | Size |
| --- | --- | --- |
| `c:TopBar` | avatars, clock, server connection, battery. Only when the page's `ShowTopBar` is true (home) | 188 high |
| `c:PageHost` | the current page | the rest |
| `c:HintBar` | input picture at the left, button hints at the right. Always there, above panel and sheet | 112 high |
| friends panel | `Views/FriendsPanel.xaml` in a `c:SlideHost`, from the right, 700 wide, any page (Y / RB / F) | |
| bottom sheet | `c:BottomSheet` showing `Shell.Sheet` | content height |
| `c:ModalHost` | dimmed page and the card of `Shell.Modal` | card 1080 wide |
| `c:OnScreenKeyboard` | the on-screen keyboard of `Shell.Keyboard`, above the modal (section 5) | content height |
| `c:ToastHost` | `Shell.Toasts`, top right | 640 wide |

A page that is not home starts with a `c:PageHeader` (126 high) and has the hint bar's rule under it.
Page margins: 72 units left and right (`Metric.PageMargin`).

## 3. Resource keys

Colours are swapped at runtime (`Shell.SetThemeAsync("dark")`), so **brushes are always
`{DynamicResource …}`**; everything else is `{StaticResource …}`.

### Brushes (`Themes/Colors.Light.xaml`, `Themes/Colors.Dark.xaml`: same keys)

| Key | Use |
| --- | --- |
| `Brush.Background` | the page, sheets, the friends panel |
| `Brush.Surface` | white cards, text boxes |
| `Brush.SurfaceAlt` | quiet cards (`Card` style), the dock pill |
| `Brush.Modal` | the modal card |
| `Brush.Text` / `Brush.TextMuted` / `Brush.TextFaint` | text; captions; disabled |
| `Brush.Accent` / `Brush.OnAccent` / `Brush.AccentSoft` | values, selection, primary fill; text on it; a tinted pill |
| `Brush.Rule` / `Brush.Outline` / `Brush.Bar` | thin rules; control outlines; the bar of a section header |
| `Brush.Good` / `Brush.Warn` / `Brush.Bad` | status |
| `Brush.Dim` | the scrim behind modal, sheet, panel |
| `Brush.Focus.Fill` | the card of the focus ring |
| `Brush.Track` / `Brush.Knob` | switch and slider and progress tracks; knobs |
| `Brush.Hover` / `Brush.Pressed` | pointer feedback |
| `Brush.Glyph` / `Brush.OnGlyph` | hint discs and their letters |
| `Brush.Toast` | toast card |

Tile art and avatar discs get their colours from `GeneratedArt` (stable per name), not from the palette.

### Type and metrics (`Themes/Typography.xaml`)

`Font.Ui` (Nunito; weights Regular, SemiBold, Bold, ExtraBold).

| TextBlock style | Size | Use |
| --- | --- | --- |
| `Text.Display` | 56 ExtraBold | one big word on a welcome page |
| `Text.Title` | 42 SemiBold | page titles (PageHeader uses 42 Regular) |
| `Text.Heading` | 34 SemiBold | card and modal titles |
| `Text.Body` | 30, wraps | paragraphs |
| `Text.Base` | 30, one line, ellipsis | labels (base of all the others) |
| `Text.Caption` | 25 muted, wraps | explanations |
| `Text.Small` | 21 muted | the smallest text allowed |
| `Text.Value` / `Text.Accent` | 30 / 34 accent | values; the selected title |

Sizes: `Size.Display/Title/Heading/Body/Caption/Small`. Metrics: `Metric.RowHeight` (104),
`Metric.HeaderHeight` (126), `Metric.HintBarHeight` (112), `Metric.PageMargin` (72,0,72,0),
`Radius.Card` (14), `Radius.Tile` (20), `Radius.Control` (12). Rows use 32, hints 34.

### Converters (`Converters.cs`, keys in `Themes/Controls.xaml`)

| Key | Does |
| --- | --- |
| `Conv.Visible` | truthy (true, non-empty string, non-zero number, non-null) → Visible, else Collapsed. `ConverterParameter=!` inverts, `hide` gives Hidden, `'=text'` / `'!=text'` compares the value's text |
| `Conv.Icon` | `"Icon.Mic"` → the Geometry, so a view model names its icon as a string |
| `Conv.Art` | a seed (`"Luna"`, or `"#0"` … `"#7"`) → its gradient brush |
| `Conv.Percent` | 0..1 → `"40%"` |
| `Conv.Not` | bool → the opposite |
| `Conv.Fraction` | MultiBinding (0..1, width) → width, for hand-made bars |

### Icons (`Themes/Icons.xaml`)

Line icons, 24 x 24 grid, stroked by `c:Icon` in the inherited text colour:
`Icon.Home Play User Friends UserPlus Chat Mic MicOff Camera CameraOff Volume Headset Settings Power
Download Refresh Fullscreen Window Globe Signal SignalOff ChevronRight ChevronLeft ChevronDown Check
Close Plus Minus Menu Info Warning Bell Flag Folder Moon Sun Trash Exit Star Lock Search Shield Clock
Block Keyboard Kart Trophy Backspace Shift Stop Tray`. Solid ones (use `Filled="True"`): `IconFill.Gamepad Play Star User`.

```xml
<c:Icon Data="{StaticResource Icon.Mic}" Width="40" Height="40" Foreground="{DynamicResource Brush.Accent}" />
```

A new icon is one `<Geometry x:Key="Icon.Name">` line: 24 x 24, round-capped strokes of width 2, no fills.

## 4. Controls (`xmlns:c="clr-namespace:Mkwmp.App.Controls"`)

Styles are implicit: just use the element.

| Control | What it is | Main properties |
| --- | --- | --- |
| `c:ConsoleButton` | the button | `Kind` = `Outline` (default: accent outline) / `Primary` (filled) / `Secondary` (grey outline) / `Ghost` / `Bottom` (full-width modal bottom); `Icon`; `Content`; `Command` |
| `c:SettingsRow` | a line of a settings pane; it is a Button | `Label`, `Kind` = `Plain` / `Value` / `Toggle` / `Chevron`, `Value`, `IsOn` (two-way; activating a Toggle row flips it), `Caption` (under the rule), `Icon`, `ShowRule`, `Command`; its content sits before the value |
| `c:ConsoleSlider` | a slider row; left / right change it while focused | `Label`, `Value`, `Minimum`, `Maximum` (default 100), `SmallChange` (default 5), `Unit` (default `%`) |
| `c:ToggleSwitch` | the pill switch alone | `IsChecked` |
| `c:ConsoleCheck` | tick box with text | `IsChecked`, `Content` |
| `TextBox`, `PasswordBox` | text entry (implicit styles) | |
| `c:SectionHeader` | "▍Title" and the rule under it | `Text`, `ShowRule` |
| `c:PageHeader` | icon, title, rule; content at the right | `Title`, `Icon`, content (e.g. `c:PageDots`) |
| `c:CategoryList` | the left navigation of settings (a ListBox) | `ItemsSource` of `CategoryEntry(Key, Title, SeparatorBefore)`, `SelectedItem` two-way. Focus selects |
| `c:Tile` | carousel card (372 x 372) | `Title`, `Subtitle`, `Icon`, `Seed` (generated art; `#0`…`#7` picks a gradient), `Image` (instead of the art), `Badge`, `Progress` (0..1, NaN hides), `Art`, `Command` |
| `c:DockButton` | round icon button (104); the label shows under it while focused | `Icon`, `Label`, `Tint` (icon colour), `Fill` (disc), `HasDot`, `IsSolid`, `Command` |
| `c:Avatar` | disc with initials on a colour hashed from the name | `DisplayName`, `Presence` = `None` / `Online` / `Playing` / `Offline`, `Count` (text above), `Image`; size by Width/Height (88) |
| `c:PageDots` | dots of a paged flow | `Count`, `Index`, `Accent`, `Foreground` |
| `ProgressBar` | implicit style; `Maximum` is 1 | `Value` 0..1 |
| `c:Spinner` | busy ring (44) | |
| `c:SmoothScrollViewer` | scrolls the focused control into view with a glide | `Padding` (becomes the content's margin: room for the focus card), `EdgePadding`, `Centered`; style `Carousel` for a horizontal one |
| `c:SlideHost`, `c:BottomSheet` | slide in from an edge while `IsOpen` | `IsOpen`, `Edge`, content |
| `c:FocusRing` | the focus card (already in every style) | `IsActive`, `CornerRadius`, `Thickness`, `Fill` |
| `c:Icon`, `c:BatteryIcon`, `c:HintGlyph` | pictures | |

Named styles: `Dock` (Border: the pill around DockButtons), `AvatarButton` (Button around an Avatar),
`CardButton` (Button: any content in a focusable card), `Card` (Border: a quiet card), `Pill` (Border: a
status pill), `Carousel` (SmoothScrollViewer), `ChromeButton` (Button that is never a focus stop).

```xml
<c:SectionHeader Text="Audio Output" />
<c:SettingsRow Label="Output Device" Kind="Value" Value="{Binding OutputName}" Command="{Binding PickOutputCommand}" />
<c:SettingsRow Label="Noise Suppression" Kind="Toggle" IsOn="{Binding Suppress}" Caption="One or two lines that explain it." />
<c:ConsoleSlider Label="Voice Volume" Value="{Binding Gain, Mode=TwoWay}" Maximum="200" />

<Border Style="{StaticResource Dock}">
  <StackPanel Orientation="Horizontal">
    <c:DockButton Icon="{StaticResource Icon.Friends}" Label="Friends" HasDot="{Binding HasRequests}" Command="{Binding FriendsCommand}" />
  </StackPanel>
</Border>

<Button Style="{StaticResource AvatarButton}" Command="{Binding OpenCommand}">
  <c:Avatar DisplayName="{Binding Name}" Presence="Playing" />
</Button>
```

Rows go in a `StackPanel` inside a `c:SmoothScrollViewer` with `Padding` of at least 12 at the sides: the
focus card is 10 units wider than its row on each side.

## 5. Focus and input

One model for the controller, the keyboard, the mouse and touch:

| Controller | Keyboard | Does |
| --- | --- | --- |
| D-pad / left stick (repeats) | arrows | moves the focus (`FocusNav.Move`) |
| A | Enter, Space | activates the focused stop |
| B | Esc, Backspace | back: top layer closes, else `Page.OnBack()`, else the page stack pops |
| Y, RB | F, E | friends panel |
| Start | F1 | settings ("Options") |
| X, LB, Back | X, Q, F2 | offered to the page (`OnButton`) |
| | F11 | full screen on / off |

The controller is read only while the window is the active one (the game has it otherwise). The hint bar
shows controller glyphs or key caps after what was touched last (`Shell.InputKind`).

**Focus stops** are found by themselves: every visible, enabled `ButtonBase`, `TextBox`, `PasswordBox`,
`Slider`, `ListBoxItem`. The nearest stop in the pressed direction gets the focus; sideways it prefers the
same line. Attached properties (`xmlns:input="clr-namespace:Mkwmp.App.Input"`):

- `input:FocusNav.IsDefault="True"`: the stop that has the focus when its page or layer opens (else the
  first in reading order).
- `input:FocusNav.IsGroup="True"` on a container: it remembers its last focused stop, and entering it
  again from outside returns there (a `CategoryList` returns to its selected entry; a group entered
  sideways for the first time starts at its top). Put it on every list, pane, row of buttons.
- `input:FocusNav.IsStop="True"`: makes any other focusable element a stop. `input:FocusNav.Skip="True"`:
  nothing inside is a stop.

The focus is never lost: when the focused control disappears, a stop of the top layer takes it within a
quarter of a second; a closing modal / sheet / panel gives the focus back to where it was; going back
to a page restores its focus and scroll position (views are kept while their page is on the stack).
Keep it that way: **update items in place** (`ObservableCollection`, or the same list instance while
nothing changed) instead of replacing a list on every `OnStateChanged`, or the focused item is rebuilt.

Chrome that must not take the focus: `Focusable="False"` (or the `ChromeButton` style).

### Text entry and the on-screen keyboard

A player with a controller (the Steam Deck, a TV) has no keyboard, so **every `TextBox` and `PasswordBox`
gets the on-screen keyboard by itself** (`Input/TextEntry.cs`, `Controls/OnScreenKeyboard.xaml`,
`ViewModels/KeyboardViewModel.cs`); a page does nothing for it:

- A on the focused box (the last input was the controller), or a touch on it, opens the keyboard. In the
  Steam Deck's Game Mode and under Wine with a controller connected a click counts as a touch (touch
  arrives as the mouse there). With a real keyboard the box is typed into directly.
- The keyboard is the top layer (above a modal): the D-pad moves over its keys with the focus card;
  **X** backspace, **Y** space, **LB** shift (twice: caps lock), **Start** OK, **B** cancel. It edits its own
  copy: OK writes the text back through the box's binding, cancel leaves the box as it was.
- Enter in the box (keyboard) and OK (on-screen keyboard) both raise the bubbling event
  `input:TextEntry.Submitted`: handle it to act on the text ("add this friend", "next step").

Optional attached properties on the box:

| Property | |
| --- | --- |
| `input:TextEntry.Title` | what is being typed, shown above the keyboard's text ("Your gamertag") |
| `input:TextEntry.Charset` | `Any` (default) / `Gamertag` (A-Z a-z 0-9 _ -) / `Digits` (a PIN): the other keys are greyed out |
| `MaxLength` (the box's own) | the keyboard's limit, with a counter |

```xml
<TextBox Text="{Binding Name, UpdateSourceTrigger=PropertyChanged}" MaxLength="16"
         input:TextEntry.Title="{Binding S[setup.name.title]}" input:TextEntry.Charset="Gamertag"
         input:TextEntry.Submitted="OnNameSubmitted" />
```

No launcher action may depend on the Guide button or on View+Menu together (Steam and the Xbox Game Bar
take them).

## 6. Shell API (`ViewModels/ShellViewModel.cs`)

A page view model derives from `PageViewModel` and gets the shell in its constructor.

```csharp
public sealed class RoomsViewModel : PageViewModel
{
    public RoomsViewModel(ShellViewModel shell) : base(shell) { }

    public string Seats => $"{Launcher.Server.Seated}/{Launcher.Server.SeatsTotal}";   // re-read on every change

    public override IReadOnlyList<Hint> Hints =>
        [new Hint(PadButton.X, S["rooms.refresh"]), new Hint(PadButton.B, S["hint.back"]), new Hint(PadButton.A, S["hint.ok"])];

    public override bool OnButton(PadButton button) { /* X, LB, Start, Back */ return false; }
    public override bool OnBack() => false;          // true: used it (closed a sub-step)
    public override void OnEnter() { }               // became visible (also when coming back)
    public override void OnLeave() { }
    public override void OnStateChanged() => base.OnStateChanged();   // launcher changed; base re-reads every binding
}
```

| Member | |
| --- | --- |
| `Launcher`, `S` | the `ILauncher`; the strings (`S["key"]`, `S.Format("key", ("n", "3"))`) |
| `Navigate(Routes.Settings)`, `Navigate(new RoomsViewModel(Shell))`, `GoBack()`, `GoHome()` | page stack |
| `ConfirmAsync(title, body, yes?, no?)` → `Task<bool>` | yes / no modal (B is no) |
| `MessageAsync(title, body, close?)` | one-button modal |
| `ShowProgress(title, body)` → `ProgressModal` (`Report(0..1, detail)`, `Close()`) | cannot be dismissed |
| `ShowModal(ModalViewModel)`, `CloseModal(modal?)` | any modal: `Title`, `Body`, `Content` (a view model with a DataTemplate), `ShowProgress`, `Progress`, `Detail`, `ShowSpinner`, `Actions` (centred buttons), `Bottom` (the full-width one), `CanDismiss`, `Dismissed` |
| `ShowUpdateModal()` | the update card (follows `Launcher.Update`) |
| `OpenSheet(LayerViewModel)`, `CloseSheet()` | bottom sheet; its view is a DataTemplate in `Views/ViewMap.xaml` |
| `IsFriendsOpen`, `ToggleFriends()`, `OpenFriends(name?)` | friends panel (with the focus on that friend's line) |
| `OpenSettings(category?)` | settings on a category (`profile`, `game`, `chat`, `notifications`, `appearance`, `updates`, `about`) |
| `OpenKeyboard(KeyboardViewModel)`, `CloseKeyboard()`, `IsKeyboardOpen` | the on-screen keyboard (text boxes open it by themselves, section 5) |
| `TrayAvailable`, `HideToTray()`, `IsQuitting`, `ToastSink` | the desktop integration's (section 10) |
| `IsOverlay`, `OverlayThumb`, `OverlayHasMenu`, `EnterOverlay(frame, again)`, `LeaveOverlay(toMenu?)`, `EndOverlay()` | overlay mode: the launcher over a running game (section 11) |
| `ShowToast(title, body, kind)` | toast (the launcher's own `Toasted` events arrive by themselves) |
| `SetThemeAsync("light" / "dark")`, `IsDark` | theme, saved to `UiSettings.Theme` |
| `SetFullscreenAsync(bool)`, `IsFullscreen` | saved to `UiSettings.Fullscreen` |
| `UpdateSettingsAsync(s => s with { … })` | saves a changed copy of `LauncherSettings` |
| `StateChanged` | launcher changed; UI thread, coalesced. Pages get `OnStateChanged()` instead |
| `AboutTitle`, `AboutBasedOn`, `AboutLicense`, `AboutText` | the About lines |
| `InputKind`, `GamepadConnected`, `HasBattery`, `BatteryPercent`, `ClockText`, `TopAvatars` | chrome state |
| `Quit()` | closes the window |

`ILauncher` raises `Changed` on any thread; the shell brings it to the UI thread. Never touch
`Launcher.Changed` from a page. Launcher methods are async and never throw for expected failures; wrap
them with `RelayCommand.Async(() => Launcher.PlayAsync())` (a fault is logged and shown as a toast).

Sheets, the friends panel and modals derive from `LayerViewModel` (`Hints`, `OnButton`, `OnStateChanged`,
`OnOpened`, `OnClosed`).

### Adding a page

1. `ViewModels/XxxViewModel.cs : PageViewModel`.
2. `Views/…/XxxPage.xaml` (a `UserControl`, `Focusable="False"`; its DataContext is the view model).
3. One `DataTemplate` in `Views/ViewMap.xaml`.
4. Top-level pages: a route in `Routes` (`ViewModels/Navigation.cs`). Sub-pages are just pushed:
   `Shell.Navigate(new XxxViewModel(Shell))`.

## 7. Strings

`i18n/en.json` and `i18n/pt-BR.json`: flat `"key": "text"` with `{name}` placeholders, copied beside the
exe. A key missing in pt-BR falls back to English, then shows the key. In XAML: `{Binding S[settings.title]}`
(pages and layers expose `S`). The locale is `UiSettings.Locale`, else the OS culture (`--locale pt-BR`
forces one). Texts that come from `ILauncher` (status, notes, problems) are shown verbatim.
Add your keys to **both** files; prefix them with your area (`home.`, `settings.`, `chat.`, `setup.`,
`friends.`).

## 8. Who owns what

| Area | Files |
| --- | --- |
| Home | `ViewModels/HomeViewModel.cs`, `Views/HomePage.xaml(.cs)` |
| Friends panel | `ViewModels/FriendsPanelViewModel.cs`, `Views/FriendsPanel.xaml(.cs)` |
| First-run setup | `ViewModels/SetupViewModel.cs`, `Views/Setup/*` |
| Settings | `ViewModels/SettingsViewModel.cs`, `Views/Settings/*` |
| Party Chat | `ViewModels/ChatViewModel.cs` (also holds `ChatSheetViewModel`), `Views/Chat/*` |
| Shared (small, additive edits only) | `Views/ViewMap.xaml`, `ViewModels/Navigation.cs` (`Routes`), `i18n/*.json`, `Themes/Icons.xaml` |
| On-screen keyboard | `Controls/OnScreenKeyboard.xaml(.cs)`, `ViewModels/KeyboardViewModel.cs`, `Input/TextEntry.cs` |
| Desktop (tray, close-to-tray, notifications) | `Desktop/*`, `ViewModels/ShellViewModel.Desktop.cs` |
| Design system and shell | `Themes/*`, `Controls/*`, `Input/*`, `MainWindow.*`, `App.xaml*`, `ViewModels/ShellViewModel*.cs`, `ViewModels/Modals.cs`, `Design/*`, `Views/Dev/*` |
| The overlay (section 11) | `Overlay/*`, `MainWindow.Overlay.cs`, `ViewModels/ShellViewModel.Overlay.cs` |

The stub pages already use the design system and work in the demo; replace their content freely.
If a control lacks something, add a property to it rather than restyling it in a page.

## 9. Checking your page (the harness)

```powershell
.\run.ps1 --demo --windowed                 # the fake launcher in a window (F11: full screen)
.\run.ps1 --demo --windowed --page settings
.\shot.ps1 out.png                          # home, 1920x1080, rendered off-screen, then exits
.\shot.ps1 out.png --page settings --category chat --press right,down --size 1280x800 --theme dark
.\shot.ps1 out.png --modal update           # also: available, confirm, message, progress
.\shot.ps1 out.png --friends      .\shot.ps1 out.png --page chat --sheet      .\shot.ps1 out.png --toast
```

`shot.ps1` builds, runs `mkwmp-launcher --demo --shot <png> …`, waits about 2 s for animations to
settle and writes the PNG. Options: `--page home|settings|chat|setup|gallery`, `--category <key>`
(settings), `--press a,b,x,y,up,down,left,right,start` (pressed in order before the shot: the way to
put the focus somewhere or to walk into a sub-step), `--size WxH`, `--theme dark`, `--battery 93`,
`--input keyboard`, `--locale pt-BR`, `-NoBuild`; `--then a,down,…` presses after the panel / sheet / modal is open
(`--friends --then a`: the on-screen keyboard on the friend field); `--state blocked|offline|signedout|nofriends|uptodate|queued|seated`
puts the demo in another situation; `--with-tray` shows what Windows with a tray shows ("Hide to Tray"); `--overlay` is overlay mode
(section 11). Look at every state at 1920x1080 and 1280x800, light and dark.

`--demo` uses `Design/DesignLauncher.cs`: server online 12/32, a signed-in player, 8 friends, 2 incoming
and 1 outgoing request, a found mkw-recomp 0.1.15-mkwmp with game 1.8.0, update 1.4.1 available;
Play goes Starting → Joining → Queued #2 → Seated; the update downloads in about 8 s; a friend toast
every 24 s; `Devices` gives moving gradient frames and a sine microphone level. When `ILauncher`
gains a member, add it there too.

Build only the app: `dotnet build src/Mkwmp.App --no-dependencies` (warnings are errors).

## 10. The desktop: tray, closing, notifications (`Desktop/*`)

`DesktopIntegration.Attach(window, shell, …)` (called by `App`) is the port of the Rust launcher's tray.rs /
notify.rs; the shell only exposes `TrayAvailable`, `HideToTray()`, `Quit()` and `ToastSink`.

| Where | Tray | Closing the window | `--tray` | A friend's news (`Toasted`) |
| --- | --- | --- | --- | --- |
| Windows | icon "MKWMP": left click opens, menu Open MKWMP / Quit | hides into the tray when "close to tray" is on (the first time a notification says so), else quits | starts hidden | window active: in-app toast; hidden or minimized: a Windows notification from the tray icon; behind another window: both |
| Wine / Proton, Linux desktop | none (Wine's tray may never show; a hidden window would be lost) | quits | starts minimized | always the in-app toast |
| Steam Deck Game Mode (`SteamGamepadUI`, `GAMESCOPE_WAYLAND_DISPLAY`, `XDG_CURRENT_DESKTOP=gamescope`) | none | quits | ignored | always the in-app toast |

Power (home's dock) offers Hide to Tray (Windows) or Minimize (Wine desktop), Exit Full Screen (not in Game
Mode) and Quit; quitting asks first only while a session holds a seat. Every quit path ends in
`App.OnExit`, which disposes the launcher (the seat is given back). Wine is `Mkwmp.Core.Platform.WineHost.Detected`.

## 11. Overlay mode: the launcher over a running game

The design and the wiring are in `docs/OVERLAY.md` (section 8); this is what a page needs to know.

While a game runs, the launcher's window is hidden. The game's Guide button (or Select+Start, or Select held) asks
for the launcher: the window comes up over the game showing the picture the game had, the picture shrinks into home's
Play tile, and the shell is in **overlay mode** (`Shell.IsOverlay`) until the player goes back.

| In overlay mode | |
| --- | --- |
| Home's Play tile | "Back to the Game": 661 wide (16:9) instead of 372, the game's picture on it (`Shell.OverlayThumb`; a drawn card when the game sent none), the badge "In game". A on it goes back |
| B on home, the dock's first button, the Guide button | back to the game (`Shell.LeaveOverlay()`) |
| Dock: "mkw-recomp menu" | only when `Shell.OverlayHasMenu` (the game can open its own menu when told to): `Shell.LeaveOverlay(toMenu: true)` |
| PLAY / STOP | not offered: nothing starts or stops a session from the overlay |
| Status line | "THE GAME IS RUNNING", unless the launcher's own session has something to say |
| Full screen switch | hidden: the window covers the game |
| Everything else | as always: friends panel, Settings, Party Chat, toasts. The game's settings show "close the game" (the target sees it running) |

A page needs nothing for it. A page that offers something which makes no sense beside a running game checks
`Shell.IsOverlay`; one that wants to send the player back calls `Shell.LeaveOverlay()`. Never call `Launcher.PlayAsync`
on the player's behalf while `Shell.IsOverlay`.

`c:Tile` got two things for it: `Width` can be bound (the card's clip follows), and a tile with an `Image` draws a
dark gradient under its title, so the text reads on any picture.

**The animation** (`MainWindow.Overlay.cs`, the `OverlayFx` canvas of `MainWindow.xaml`) is driven by one number and
changes only a clip rectangle, transforms and opacities, so it costs no layout. It moves home's parts through
`HomePage.SetOverlayMotion`; if you add a part to home, give it a slot there or it will sit still while the rest
comes in. Buttons are ignored while it runs.

**Checking it** (the harness; each moment is a still):

```powershell
.\shot.ps1 out.png --overlay                                   # home in overlay mode
.\shot.ps1 out.png --overlay --overlay-progress 0.3            # that moment of the open (0: the game's picture, 1: home)
.\shot.ps1 out.png --overlay --overlay-return 0.5              # that moment of the way back
.\shot.ps1 out.png --overlay --overlay-frame shot.png          # a PNG as the game's picture (default: a drawn race)
.\shot.ps1 out.png --overlay --overlay-frame none              # the game sent no picture: the drawn card
.\shot.ps1 out.png --overlay --overlay-no-menu                 # a game that cannot be told to open its menu
.\shot.ps1 out.png --overlay --friends                         # any other option works in overlay mode
```

Live, in the demo: `.\run.ps1 --demo --windowed`, then **F9** opens the overlay with the drawn race (the picture
fills the window and shrinks) and F9 again goes back.
