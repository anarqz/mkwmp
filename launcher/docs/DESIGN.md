# MKWMP Launcher: design system and shell

Everything a page needs is here. The app is `src/Mkwmp.App` (WPF, .NET 10, assembly `mkwmp`);
it binds only to `Mkwmp.Core.ILauncher`. Look at the result before reading: `.\run.ps1 --demo --windowed --page gallery`
shows every control and icon on one page.

## 1. Rules of the look

- A console's system menu on a TV or a handheld: light grey background, white rounded cards, near-black
  text, one blue accent for values and selection, thin grey rules, a lot of room, type readable at 3 m.
- **The focus card** is the signature: the focused control becomes a white card with a slowly turning
  blue / cyan / violet / pink border and a faint halo (`c:FocusRing`). Every focusable control style has
  it already. Never draw another focus indicator, never remove it.
- Ours only. No console maker's logos, glyphs, names or box art. There is no chat in the launcher: voice and
  camera exist only inside the game, and the launcher only configures them (Settings › **Voice & Camera**). Button
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
| `c:HintBar` | input picture at the left, button hints at the right. The page's own: a layer that covers it brings its own (below) | 112 high |
| friends panel | `Views/FriendsPanel.xaml` in a `c:SlideHost`, from the right, 700 wide, any page (Y / RB / F). Over the whole height, **above** the page's hint bar, with its own `c:HintBar InPanel="True"` at its bottom (so has the file picker, section 14). Meanwhile the page's bar, dimmed with the page, keeps only its input picture (`ShowHints`) | |
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
`Icon.Home Play User Friends FriendsLocked UserPlus Mic MicOff Camera CameraOff Volume Headset Settings Power
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
| `c:ConsoleButton` | the button | `Kind` = `Outline` (default: accent outline) / `Primary` (filled) / `Secondary` (grey outline) / `Ghost` / `Bottom` (full-width modal bottom) / `Danger` (takes something away: red outline and text); `Icon`; `Content`; `Command` |
| `c:SettingsRow` | a line of a settings pane; it is a Button | `Label`, `Kind` = `Plain` / `Value` / `Toggle` / `Chevron`, `Value`, `IsOn` (two-way; activating a Toggle row flips it), `Caption` (under the rule), `Icon`, `ShowRule`, `IsDestructive` (red label and icon), `Command`; its content sits before the value |
| `c:ConsoleSlider` | a slider row; left / right change it while focused | `Label`, `Value`, `Minimum`, `Maximum` (default 100), `SmallChange` (default 5), `Unit` (default `%`) |
| `c:ToggleSwitch` | the pill switch alone | `IsChecked` |
| `c:ConsoleCheck` | tick box with text | `IsChecked`, `Content` |
| `TextBox`, `PasswordBox` | text entry (implicit styles) | `input:TextEntry.Placeholder` (what the empty box says: drawn by the box itself exactly where its text starts; never lay a TextBlock over a box). The text sits at `Padding` (26) from the edge; a leading icon is an `c:Icon` over the box and a larger left `Padding` |
| `c:SectionHeader` | "▍Title" and the rule under it | `Text`, `ShowRule` |
| `c:PageHeader` | icon, title, rule; content at the right | `Title`, `Icon`, content (e.g. `c:PageDots`) |
| `c:CategoryList` | the left navigation of settings (a ListBox) | `ItemsSource` of `CategoryEntry(Key, Title, SeparatorBefore, Heading, Icon, Locked)` (`Locked`: a small lock after the title, section 12), `SelectedItem` two-way. Focus selects |
| `c:Tile` | carousel card (372 x 372) | `Title`, `Subtitle`, `Icon`, `Seed` (generated art; `#0`…`#7` picks a gradient), `Image` (instead of the art), `Badge`, `Progress` (0..1, NaN hides), `Art`, `Command` |
| `c:DockButton` | round icon button (104); the label shows under it while focused | `Icon`, `Label`, `Tint` (icon colour), `Fill` (disc), `HasDot`, `IsSolid`, `Command` |
| `c:Avatar` | disc with initials on a colour hashed from the name | `DisplayName`, `Presence` = `None` / `Online` / `Playing` / `Offline`, `Count` (text above), `Image`; size by Width/Height (88) |
| `c:PageDots` | dots of a paged flow | `Count`, `Index`, `Accent`, `Foreground` |
| `ProgressBar` | implicit style; `Maximum` is 1 | `Value` 0..1 |
| `c:Spinner` | busy ring (44) | |
| `c:SmoothScrollViewer` | every list: keeps the focus in view, and scrolls by wheel, right stick, finger or mouse drag, all on the render clock (section 5, "The pointer" and "Scrolling") | `Padding` (becomes the content's margin: room for the focus card), `EdgePadding`, `Centered`, `FadeEdges`; style `Carousel` for a horizontal one |
| `c:HintBar` | the hint bar | `InPanel` (the bar of a side panel), `ShowHints`. Hints that do not fit shrink together |
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

### The button layout: one setting, the game's

A and B above are roles. Which physical button gives them is the game's Settings › Controls › Button layout
(`pad_layout` in mkw.ini: `auto`, `nintendo`, `xbox`), and the launcher applies it exactly as the game applies
it to the same controller (`Input/PadLayout.cs`, the only place that decides). The rule, from mkw-recomp 0.1.16
(`runtime/input/input_map.c`, `input.c`; SDL3 reports face buttons by position):

- **Nintendo**: the right (east) button is A: confirm in the launcher, A (accelerate, OK) in the game. The bottom
  one is B (back).
- **Xbox**: the bottom (south) button is A, the right one B.
- **Automatic**: Nintendo when the controller's right button is labelled A (SDL's label: a Switch Pro Controller,
  Joy-Con, Switch 2 controller), Xbox for every other controller (Xbox, PlayStation, Steam Deck, ROG Ally, generic).

The launcher reads XInput, which names buttons by the Xbox labels (A south, B east), and identifies the
controller as SDL does: `XInputGetCapabilitiesEx` (xinput1_4 ordinal 108) for its ids and, for Steam Input's
virtual pad on Windows (28de:11ff), the real controller's type from the file in `SteamVirtualGamepadInfo`. With a
Switch controller Steam's "Use Nintendo Button Layout" (its default) sends the button labelled A (east) as XInput's
A; SDL swaps it back to its place (`SDL_joystick.c` `ShouldSwapFaceButtons`), and so does `PadLayout.Position`,
so the swap is undone once, for both. Under Proton the virtual pad has other ids (28de:12fd) and SDL cannot tell it
is Steam's: neither swaps, and both programs see the same buttons. Steam's own setting is not read: like SDL, the
launcher assumes its default.

| Controller (how it reaches the launcher) | Automatic | Nintendo | Xbox |
| --- | --- | --- | --- |
| Xbox controller (Windows) | bottom | right | bottom |
| Steam Deck, ROG Ally, any pad under Proton (Steam's virtual pad, 28de:12fd) | bottom | right | bottom |
| PlayStation controller (Steam Input on Windows) | bottom (cross) | right (circle) | bottom (cross) |
| Switch Pro Controller / Joy-Con (Steam Input on Windows) | right (A) | right (A) | bottom (B) |
| A controller with Nintendo's own ids in XInput | right (A) | right (A) | bottom (B) |

The button that confirms in the launcher is the game's A in every cell. Controllers XInput does not see (a
PlayStation or Switch controller without Steam, Switch 2 controllers over Bluetooth) work in the game only; the
launcher is then used with the keyboard and mouse. Known limit: with Steam's "Use Nintendo Button Layout" turned off,
SDL's swap is wrong for that controller, in the game and (on purpose, to stay the same) in the launcher. The log
says what was decided: `input: button layout Nintendo, Steam's Nintendo buttons swapped back (pad_layout = auto,
controller 28de:11ff (Steam: switchpro))`. Harness: `--pad-layout auto|nintendo|xbox`, `--pad-device vid:pid[:steamtype]`,
`--press east|south` (places).

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

### The pointer: a tap acts when it is let go, a drag never clicks

Touch is the mouse here (WPF promotes it; under Wine and gamescope it already is the mouse), so one rule covers both,
kept in one place (`Input/Tap.cs`, attached to the window; its decision is `Input/TapTracker.cs`, tested):

- A press that stays within the **touch slop (10 DIP of the window, not stage units)** is a tap and acts **when it is let
  go**. One that leaves it is a drag for the rest of that press: the button under it lets go without a click (its
  pressed look ends there), the focus goes back to where it was, and the `SmoothScrollViewer` under it scrolls.
- That holds for every control with no code of its own: `ButtonBase` (WPF clicks on release; `Tap` takes the capture
  away at the drag), `ListBoxItem` (focused and selected when the tap ends: WPF's own selects on the press).
- Anything else that reacts to the pointer handles **`MouseLeftButtonUp`** and asks `Tap.IsTap(sender)` (a scrim that
  closes its layer) or `Tap.Dragged` (text boxes opening the keyboard). Never act on `MouseLeftButtonDown`, never set
  `ClickMode="Press"`.
- A drag stays with what it is for: a slider's track, a scroll bar, text (`Tap.OwnsDrag`).

### Scrolling

`Input/ScrollMotion.cs` (plain math, tested) moves each axis; `SmoothScrollViewer` steps it once per rendered frame
(`CompositionTarget.Rendering`, with the frame's own time). The rule: **the velocity never jumps**. The glide is a
critically damped spring that keeps its speed when its target moves (a held D-pad, a spun wheel: one movement, not a
push per row); a flick coasts with friction; the right stick gives a *speed* (`ScrollAt`), integrated every frame
(never add a distance per timer tick: the gamepad timer is 60 Hz at best and uneven). The offset moves in fractions
of a unit while it glides (a render transform carries what the rounded layout cannot) and rests on whole units, so
text is sharp at rest. Speeds: 80 units a wheel notch, 1600 to 3520 units a second on the stick, a flick travels its
speed times 0.4 s. What WPF does not give: the scrolled content is laid out and drawn by the UI thread (no
compositor-side scrolling as in a browser), so a frame the UI thread misses is a frame the list does not move.

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
| `input:TextEntry.Placeholder` | what the empty box says; the box draws it where its text starts (a centred box hides it while it has the keyboard focus) |
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
| `OpenSettings(category?)` | settings on a category (`online`, `game`, `voice`, `notifications`, `appearance`, `updates`, `about`) |
| `OnlineEnabled`, `ConfigureOnline()` | a server is set; Settings › Online from wherever a lock was (section 12) |
| `OpenKeyboard(KeyboardViewModel)`, `CloseKeyboard()`, `IsKeyboardOpen` | the on-screen keyboard (text boxes open it by themselves, section 5) |
| `TrayAvailable`, `HideToTray()`, `IsQuitting`, `ToastSink` | the desktop integration's (section 10) |
| `IsOverlay`, `OverlayThumb`, `OverlayHasMenu`, `EnterOverlay(frame, again)`, `LeaveOverlay(toMenu?)`, `EndOverlay()` | overlay mode: the launcher over a running game (section 11) |
| `ShowToast(title, body, kind)` | toast (the launcher's own `Toasted` events arrive by themselves) |
| `SetThemeAsync("light" / "dark")`, `IsDark` | theme, saved to `UiSettings.Theme` |
| `SetFullscreenAsync(bool)`, `IsFullscreen` | saved to `UiSettings.Fullscreen` |
| `UpdateSettingsAsync(s => s with { … })` | saves a changed copy of `LauncherSettings` |
| `StateChanged` | launcher changed; UI thread, coalesced. Pages get `OnStateChanged()` instead |
| `AboutTitle`, `AboutLicense`, `AboutRuntimeCredit`, `AboutText` | the About lines (no policy of its own: the only privacy policy is the server's) |
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

`i18n/<locale>.json`, one per language: flat `"key": "text"` with `{name}` placeholders, copied beside the
exe. A key missing in a language falls back to English, then shows the key. In XAML:
`{Binding S[settings.title]}` (pages and layers expose `S`).

**Languages** (`Mkwmp.Core/Locales.cs`, the one list): en, de, es, fr, it, nl, pl, pt-BR, pt-PT, tr, ru, ja,
ko, zh-Hans, zh-Hant. The language is `UiSettings.Locale`, else the closest to the system's
(`Locales.Closest`: pt-BR only for Brazil, zh-Hant for Taiwan / Hong Kong / Macao, every Spanish is es),
else English; `--locale ja` forces one for a run. Arabic is not there: the stage has no right-to-left layout.

**Everything the player reads is in the catalogs, the core's texts too.** `Mkwmp.Core.Strings` holds no
English: each text is a key (`core.*`), looked up when it is asked for. The English catalog is embedded in
Mkwmp.Core (so headless and the tests speak English, and the tests pin it); the window gives the core the
player's language with `CoreStrings.Use(S.Find, culture)`. The game's own settings (`gs.*`) are the table
in `RecompSettings.Build` (ids and English there, pinned by the tests) localized on the way out; their
translations come from mkw-recomp's own `i18n/ui/*.po` where it has them. Not localized on purpose: the
session log and bug reports (for the maintainers), and what the game's mod shows from the local API
(plain ASCII). The server's privacy policy is shown as served.

**A change of language needs no restart** (`ShellViewModel.Language.cs`): `S` is one object that switches
in place and raises `Item[]`, so every `S[key]` binding reads again; the core raises `Strings.Changed` and
the launcher rebuilds its views; the open pages are made again (`PageViewModel.Recreate()`: return the
same page in the same place, or null to drop it), because view models hold texts they built. So: never
keep a text in a `static` or compare a core text with `is` (they are not constants); a page that can be
open under Settings or the welcome implements `Recreate`.

**Formats.** Numbers, dates and times use `S.Culture` (also the thread's culture): `S.Number(n)`,
`CoreStrings.Size(bytes)`, `ToString("t", S.Culture)`. Counts have no plural forms: write the text so it
reads for any number ("Online: {n}").

**Fonts** (`Themes/Fonts.cs`, `Assets/Fonts`): Nunito covers Latin and Cyrillic; behind it Noto Sans JP /
KR / SC / TC (OFL; subsets made by `tools/make-cjk-fonts.py`: the everyday characters of each language
plus every character of the catalogs, Regular and Bold) so Japanese, Korean and Chinese read the same on
Windows and under Wine/Proton. The chain's order follows the language (Han characters differ by region),
so use `{DynamicResource Font.Ui}`, never `StaticResource`. Run the script again when a catalog gains
characters outside the everyday sets (it reads `i18n/`).

**Adding keys:** add them to en.json and to **every** other catalog (`CatalogTests` fails on a missing key,
another set of placeholders or an empty text); prefix them with your area (`home.`, `settings.`, `setup.`,
`friends.`, `online.`, `core.` for Mkwmp.Core). **Adding a language:** a line in `Locales.All`, its
`i18n/<id>.json`, and the font chain if it needs glyphs Nunito lacks.

## 8. Who owns what

| Area | Files |
| --- | --- |
| Home | `ViewModels/HomeViewModel.cs`, `Views/HomePage.xaml(.cs)` |
| Friends panel | `ViewModels/FriendsPanelViewModel.cs`, `Views/FriendsPanel.xaml(.cs)` |
| Welcome flow, Settings › Online (section 13) | `ViewModels/SetupViewModel*.cs`, `Views/Setup/*`, `ViewModels/SettingsViewModel.Online.cs`, `Views/Settings/OnlinePane.xaml`, `ViewModels/ReportViewModel.cs`, `Views/Settings/ReportPage.xaml` (Report Abuse), `Parts/ProfileForm.cs`, `Parts/ServerPicker.cs`, `ViewModels/PrivacyViewModel.cs`, `Views/Settings/{PinField,PinEntry,PinReveal,ServerList,LatencyBars,MarkdownView,PrivacyPage}*` |
| Settings | `ViewModels/SettingsViewModel.cs`, `Views/Settings/*` |
| Voice & Camera (a settings pane) | `ViewModels/SettingsViewModel.Voice.cs`, `Views/Settings/VoicePane.xaml`, `ViewModels/CameraTestViewModel.cs`, `Views/Settings/CameraTestPage.xaml`, `ViewModels/Parts/DeviceTests.cs` |
| Locked online (section 12) | `Views/OnlineLocked.xaml(.cs)`, `Shell.ConfigureOnline()` (`ShellViewModel.Desktop.cs`), `OnlineOnly` (`SettingsViewModel.Voice.cs`) |
| Shared (small, additive edits only) | `Views/ViewMap.xaml`, `ViewModels/Navigation.cs` (`Routes`), `i18n/*.json`, `Themes/Icons.xaml` |
| On-screen keyboard | `Controls/OnScreenKeyboard.xaml(.cs)`, `ViewModels/KeyboardViewModel.cs`, `Input/TextEntry.cs` |
| Desktop (tray, close-to-tray, notifications) | `Desktop/*`, `ViewModels/ShellViewModel.Desktop.cs` |
| Design system and shell | `Themes/*`, `Controls/*`, `Input/*` (`Tap`, `ScrollMotion`, `FocusNav`, `TextEntry`), `MainWindow.*`, `App.xaml*`, `ViewModels/ShellViewModel*.cs`, `ViewModels/Modals.cs`, `Design/*`, `Views/Dev/*` |
| The overlay (section 11) | `Overlay/*`, `MainWindow.Overlay.cs`, `ViewModels/ShellViewModel.Overlay.cs` |

The stub pages already use the design system and work in the demo; replace their content freely.
If a control lacks something, add a property to it rather than restyling it in a page.

## 9. Checking your page (the harness)

```powershell
.\run.ps1 --demo --windowed                 # the fake launcher in a window (F11: full screen)
.\run.ps1 --demo --windowed --page settings
.\shot.ps1 out.png                          # home, 1920x1080, rendered off-screen, then exits
.\shot.ps1 out.png --page settings --category voice --press right,down --size 1280x800 --theme dark
.\shot.ps1 out.png --modal update           # also: available, confirm, message, progress
.\shot.ps1 out.png --friends      .\shot.ps1 out.png --toast      .\shot.ps1 out.png --demo-offline --friends
```

`shot.ps1` builds, runs `mkwmp --demo --shot <png> …`, waits about 2 s for animations to
settle and writes the PNG. Options: `--page home|settings|settings/online|setup|connect|privacy|whatsnew|gallery` (section 13 for connect and privacy; whatsnew: the changelog, `CHANGELOG.md` built in, with the update banner), `--category <key>`
(settings), `--press a,b,x,y,up,down,left,right,start` (pressed in order before the shot: the way to
put the focus somewhere or to walk into a sub-step), `--size WxH`, `--theme dark`, `--battery 93`,
`--input keyboard`, `--locale pt-BR`, `-NoBuild`; `--mod camera=0,camera_view=1` (the game's camera switches as its file has them, for Voice & Camera); `--then a,down,…` presses after the panel / modal is open
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

While a game runs, the launcher's window is hidden. The player's overlay shortcut (Settings › Game; by default the Guide button, Select+Start or Select held; OVERLAY.md §2.1) asks
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
| Everything else | as always: friends panel, Settings, toasts. The game's settings show "close the game" (the target sees it running) |

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

## 12. Locked online: before a server is chosen

A fresh install has no server (`ILauncher.OnlineEnabled` is false, `Shell.OnlineEnabled` for bindings): the launcher
is a shell for the game, and everything that needs a server is **locked**. The rule: a locked thing stays where it
is, shows the lock (`Icon.Lock`), says in one line what lives on a server, and offers exactly one way out,
**"Configure Online"** (`online.configure`), which is always `Shell.ConfigureOnline()` → Settings › Online
(`Routes.OnlineCategory`). Nothing locked is hidden without a trace, nothing locked shows stale data, and nothing
asks the server anything.

| Place | Locked (`!OnlineEnabled`) |
| --- | --- |
| Home, Play tile | "Play", badge "Offline": starts the game with no seat. Overlay mode is unchanged ("Back to the Game") |
| Home, Friends tile | the same tile (it keeps its place and the focus) becomes "Configure Online" with the lock and "Play with friends on an MKWMP server" |
| Home, status line | the session's status only: no rule, no server part |
| Home, dock | the Friends button draws `Icon.FriendsLocked`, is labelled "Configure Online" and goes there; no request dot |
| Top bar | only the player's own disc, from the local name (`Settings.Name`), with no request count; no friends |
| Friends panel (Y / RB / F) | header, then `v:OnlineLocked` ("Friends live on an MKWMP server"): no add field, no list |
| Settings, a category of `OnlineOnly` (Voice & Camera, with the reports) | a lock after its title (`CategoryEntry.Locked`); `CategoryKey` is `"locked"`, so its pane is replaced by `v:OnlineLocked` ("Configure online first") |
| Settings, Notifications | the three friend switches are replaced by one locked row that goes to Online |
| Toasts, system notifications | none of the friend kinds (`ToastKind.Friend*`) are shown |
| Power menu | no "your seat is given back" line |

With a server everything is as before; the lock follows `Changed` (choosing a server in Settings › Online unlocks
the rest at once, the chosen category stays chosen).

To lock something of your own:

```xml
<!-- xmlns:v="clr-namespace:Mkwmp.App.Views"; the DataContext needs S and ConfigureOnlineCommand -->
<v:OnlineLocked Title="{Binding S[friends.locked.title]}" Text="{Binding S[friends.locked.text]}"
                Visibility="{Binding Locked, Converter={StaticResource Conv.Visible}}" />
```

```csharp
public bool Locked => !Launcher.OnlineEnabled;                       // re-read on every change
ConfigureOnlineCommand = new RelayCommand(Shell.ConfigureOnline);
```

A whole settings category: add its key to `OnlineOnly` in `SettingsViewModel.Voice.cs` (the lock in the list and the
locked pane come by themselves). A single row or button: keep it, swap its icon for the lock, and send it to
`Shell.ConfigureOnline()`.

**Voice & Camera** is the one place that says what voice and camera are, in a card at its top: they work only inside
Mario Kart World, in **private rooms**, with **friends added on the server** (never in public lobbies), with **up to
4 friends** at a time, live and never recorded, the camera only for mutual friends. The launcher never opens a voice
or camera session itself: the pane sets the switch, devices and volumes the game reads, and tests them locally.
The pane is two sections built the same way (section 15, "A feature that can be off"): **Voice**: "Use voice in
private rooms", then the microphone, its volume, its test, then the output, its volume, its test. **Camera**: "Use
camera in private rooms" (one switch for the mod's two settings, `camera` and `camera_view`: on when either is, so an
older launcher's file reads as it was meant; turning it on sets both, off clears both), "Show My Camera" under it
while it is on (the one finer choice: see friends without being seen), then the camera and its test. Mirroring is
on the Test Camera page (X), where it can be seen. Then the muted players; reporting one is Settings › Online ›
Report Abuse.
The microphone is opened only while "Test microphone" is on, the camera only while the Test Camera page shows; both
stop when the page is left, the window loses the focus or the overlay goes back to the game
(`PageViewModel.OnWindowDeactivated`). The audio devices are listed only when Voice & Camera is opened and the game
has not reported its own lists; the cameras only when one is picked or tested (the name is kept as
`[voice] camera_name`). The game's own rule is in docs/contracts/VOICE_API.md §6, "When the devices are open".

Checking it: `--demo-offline` is the demo set up but with no server (`--demo-fresh`: a first start).

```powershell
.\shot.ps1 out.png --demo-offline                                        # home, locked
.\shot.ps1 out.png --demo-offline --friends                              # the friends panel, locked
.\shot.ps1 out.png --demo-offline --page settings --category voice       # a locked category
.\shot.ps1 out.png --page settings --category voice --press right,down   # Voice & Camera, with a server
```
## 13. Offline first: the welcome flow, Settings › Online, PINs

A fresh install has no server and is complete that way: PLAY starts the game alone (status "READY · PLAY OFFLINE"),
no server is contacted, and section 12's locks lead to **Settings › Online** (`Routes.SettingsOnline`, category key
`Routes.OnlineCategory` = `online`, `Shell.ConfigureOnline()`).

**The welcome flow** (`SetupViewModel`, shown while `Ui.SetupDone` is false; it resumes at the first open step, and
Core marks a working online profile as done so an existing player never meets it):

| Step (`SetupStep`) | What |
| --- | --- |
| `Welcome` | what MKWMP is; "Built on mkw-recomp (MIT)" |
| `Name` | the local name (gamertag rules), saved locally; nothing is asked of a server |
| `Game` | `GameStage`: download mkw-recomp (`RuntimeModal`: bytes, speed, time left, Cancel) or "I already have it"; the player's own copy (file, folder, or a typed path; the legal line); the setup (step, bar, time left, Stop; Try Again and Open Log when it failed); MKWMP's files (by itself). "Later" always works |
| `Online` | "Want to go online?": **Not Now** → `Done` → home, locked; **Go Online** → the next four |
| `Server` | two columns: the list at the left, scrolling by itself; at the right the address (typed, or put there by a row), the server's answer and the page's button (Connect, then Next: the focus goes to it when the server answered), and "Show closest to me" under them. The beholder's list (`ServerPicker` / `ServerList`: latency bars and ms measured to each server, name, region, version, players, queue; the five nearest first and five more with each "Show More"; Refresh; re-measured every 15 s while shown; Y on a row, or its "…", hides that server on this PC and the next takes its place, Settings › Online › Hidden Servers shows it again) and a typed address; Connect asks the server who it is ("MKWMP server · 12/32 players", or why not) |
| `Register` | sign in: register the name (a new PIN), or, for a name that exists, "It's Mine" with its PIN (masked) |
| `Consent` | only **after** signing in (the server keeps the consent): the three paragraphs, "Read the Privacy Policy" (`PrivacyViewModel`: the server's Markdown rendered by `MarkdownView`, never a browser; a server without `/privacy` gets "This server doesn't publish a privacy policy", never another page), the 18+ box; skipped when the sign-in said the server already has it |
| `Pin` | the PIN, masked, with its eye (and X) |
| `Done` | what is ready and what waits |

The same class with only the online steps is Settings › Online's connect pages: `SetupViewModel.Connect(shell, address?)`
(pushed over Settings, ends back there) and `SetupViewModel.ConsentOnly(shell)`.

**A PIN is never bound to text.** Use `settings:PinField` (`Pin`, `IsRevealed` two-way, `DigitSize`): dots drawn as
shapes, an eye button, hidden again whenever it leaves the screen. Typing one is `settings:PinEntry` (a password box,
dots also on the on-screen keyboard, the same eye). A page that shows a PIN adds `PinHint.For(S, revealed)` to its
hints and toggles on X; a modal that shows one is a `PinModal`.

Harness (all with a demo start: `--demo` online, `--demo-offline`, `--demo-fresh` a bare machine):

```powershell
.\shot.ps1 out.png --demo-fresh --page setup --state named,need-dump --step game
.\shot.ps1 out.png --demo-fresh --page setup --state named,game-ready --step server=mkw.example.net
.\shot.ps1 out.png --demo-fresh --page setup --state named,game-ready,registered --step pin --press x
.\shot.ps1 out.png --demo-offline --page settings/online        # also --page connect, --page privacy
```

`--step welcome|name|game|online|server|register|consent|pin|done[=text]` jumps to that page (`=text` types into it:
`name=Alex`, `server=down.example.net` for an error). `--state` takes several, comma-separated: `named`,
`downloading`, `download-failed`, `need-dump`, `setting-up`, `setup-failed`, `need-mod`, `game-ready`, `connected`
(a server, no profile), `taken`, `signed-in` (a profile, no consent yet), `registered`, `no-list` (no beholder),
`list-cached`. In the demo a name of the demo's friends ("Luna") is taken, and an address with `down`, `other` or
`old` in it fails in that way.

## 14. Choosing a folder or a file: the launcher's own picker

**Every folder or file the launcher asks for is chosen in its own picker, never in the system's dialog** (Infinity
Launcher's rule, 2026-10-28): the system's is another window with another look on Windows and under Proton, shows the
prefix's empty `C:\users\steamuser` on a Steam Deck, and is no use with a controller. "Show in folder" (a saved log, a
report) still opens the file manager: that is not a pick.

```csharp
var folder = await Shell.PickFolderAsync(new PickerRequest(S["mods.pick_title"], "mods_folder")
{
    Caption = S["mods.pick_caption"],                         // under the list: what a right choice looks like
    Filter = new PickFilter { FolderMatches = f => File.Exists(Path.Combine(f, "mod.toml")) },   // marks such folders
    Validate = path => Task.Run(() => Launcher.ModFolderProblem(path)),   // why not, or null; the picker stays open on a refusal
    MatchBadge = "Mod", MatchNote = S["mods.pick_here"], InitialFolder = current,
});
if (folder is not null) { … }                                 // null: closed without a pick
```

| Piece | |
| --- | --- |
| `Shell.PickAsync` / `PickFolderAsync` / `PickFileAsync(PickerRequest)` → `Task<string?>` | `ShellViewModel.Picker.cs`. `PickKind.Either`: a file from the list, or the open folder. A second pick closes the first (its caller gets null) |
| `PickerRequest(Title, Purpose, Kind)` | `Purpose` is the key the last folder is kept under in client.toml's `[picker]` (`LauncherSettings.Picker`): the next pick for it starts there. `Filter` (`PickFilter`: `Files`, `Extensions`, `FolderMatches`), `Validate`, `InitialFolder`, `Caption`, `MatchBadge`, `MatchNote`, `Icon` |
| `PickerRequests.GameCopy` / `GameFolder` | the launcher's two picks, judged by the target itself (`ILauncher.GameCopyProblem` → `RecompSetup.DumpProblem`; `GameFolderProblem` → a target's `Detect`) |
| `PickerViewModel`, `Views/PickerSidebar.xaml` | the panel: 1180 wide from the right (`PickerLayer` of `MainWindow.xaml`: above a modal, under the on-screen keyboard, with its own copy of the hint bar) |
| `Mkwmp.Core.Files.FileBrowser` / `FileHost` / `PickerFolders` | the logic, with no window: places, listings, typed paths, crumbs, the loop guard. `FilePickerTests` |
| `c:EntryList` | a virtualized list whose lines are focus stops: up and down go by index (`Step`), A / click / tap raise `Activated` |

**In the panel:** quick places at the left (the last folder of this purpose, Home, Downloads, Desktop, Documents, cards
and USB drives, Steam's libraries, the launcher's folder, drives with their label and free space, "This Device"); Up,
the path's steps, and a box to type or paste a path (the on-screen keyboard opens on it; Enter or Go); the list (folders
first, then by name the way people count; modified and size; a mark on a folder that holds what is looked for); under
it what is asked for, "this folder is a right one", or why the last choice was refused, and **Use This Folder**.

| Controller | Keyboard | In the picker |
| --- | --- | --- |
| A | Enter, click, tap | opens a folder, picks a file |
| B | Esc, Backspace | back to the folder the player came from; at the start, closes |
| X | X | Use This Folder |
| Y | F | every file and the hidden ones / only what can be picked |
| LB / RB | Q / E | to the places / back to the list (left and right do it too) |

**What it handles** (each one learnt in Infinity Launcher or asked for here; `FileBrowser`'s summary has the why):
Linux places under Proton through `Z:\` (home from `WINEHOMEDIR` / `HOME` / `USER`; `/run/media/<user>/<card>` and
the older `/run/media/<card>`; `libraryfolders.vdf`; `/mnt`, `/media`), paths read and typed the Linux way, `Z:` and
the prefix's `C:` never offered as drives (the prefix only with Y); a refused, vanished or unreadable folder says so
and stays shown, a card taken out leads to the nearest folder still there; links are followed but a link back to an
ancestor opens that ancestor (no endless path), and a path deeper than 96 steps is not opened; a huge folder is read
up to 20,000 entries and shown up to 2,000, off the window's thread, with a line that says so; hidden and system files
and Linux dot files only with Y; names keep the disk's letter case, a typed path takes it; long paths, `\\?\`, shares,
`file://`, `%NAME%`, `~`; the second click of a double-click is not a click on what the first one opened.

```powershell
.\shot.ps1 out.png --picker copy --fake-wine                                  # the game copy, as on a Steam Deck (a made-up /home/deck, SD card, Steam libraries)
.\shot.ps1 out.png --picker game --fake-wine --picker-at /home/deck/Games --then down,a
.\shot.ps1 out.png --picker copy-folder --picker-at D:\Dumps --then y         # this PC's real places; Y: every file
```

## 15. Layout and behaviour: the same thing looks and acts the same everywhere

| Rule | |
| --- | --- |
| A switch switches | `SettingsRow Kind="Toggle"`: A / tap flips it at once, nothing else happens. Its caption says what **off** means |
| A chevron navigates | `Kind="Chevron"` only for a row that opens something (a page, a list to choose from). An action that happens in place (play a test sound, check for updates) is `Kind="Plain"` with an icon |
| What takes something away looks it | `ConsoleButton Kind="Danger"` (Disconnect), `SettingsRow IsDestructive="True"` (delete a profile, uninstall); and it asks first (`Shell.ConfirmAsync`) |
| A status is not a button | What only informs (the server row of Settings › Online: address, light, players) is a `Card`, not a row that acts when pressed. The action beside it is a button that says what it does ("Disconnect"), and the other action is its own row ("Connect to Another Server") |
| A feature that can be off | Its section starts with its switch ("Use voice in private rooms", "Use camera in private rooms"); under it, in this order, the device and then its test. What depends on the switch is greyed out or absent while it is off. Two features of one pane are built in parallel, row for row |
| Rarely needed | Not a row of the pane: on the page where its effect shows (the camera''s mirror on Test Camera), or as the caption of the list it explains (where the device names come from) |
| One list, one column | A list that can grow (servers) gets a column that scrolls by itself (`SmoothScrollViewer`, `FocusNav.IsColumn`); what acts on one entry (an address, its answer, the button) is the other column, a `Card`, 640 wide; the list''s column is the `*` one. Use two columns when a page would otherwise put a form under a list; a plain run of settings rows stays one column (Settings already is two) |
| The page''s button | Under the page (the welcome flow), or, on a two-column page, in the column of what it acts on. Never both |
| Hints belong to the layer | The page''s hint bar is the page''s; a layer over it shows its own inside itself (section 2) |
| Text boxes | Placeholder by `input:TextEntry.Placeholder` only; the label is beside or above the box, never only the placeholder |