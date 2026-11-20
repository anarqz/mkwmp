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
Block Keyboard Kart Trophy`. Solid ones (use `Filled="True"`): `IconFill.Gamepad Play Star User`.

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
| `IsFriendsOpen`, `ToggleFriends()` | friends panel |
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
| Design system and shell | `Themes/*`, `Controls/*`, `Input/*`, `MainWindow.*`, `App.xaml*`, `ViewModels/ShellViewModel*.cs`, `ViewModels/Modals.cs`, `Design/*`, `Views/Dev/*` |

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
`--input keyboard`, `--locale pt-BR`, `-NoBuild`. Look at every state at 1920x1080 and 1280x800, light
and dark.

`--demo` uses `Design/DesignLauncher.cs`: server online 12/32, a signed-in player, 8 friends, 2 incoming
and 1 outgoing request, a found mkw-recomp 0.1.15-mkwmp with game 1.8.0, update 1.4.1 available;
Play goes Starting → Joining → Queued #2 → Seated; the update downloads in about 8 s; a friend toast
every 24 s; `Devices` gives moving gradient frames and a sine microphone level. When `ILauncher`
gains a member, add it there too.

Build only the app: `dotnet build src/Mkwmp.App --no-dependencies` (warnings are errors).
