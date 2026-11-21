# MKWMP launcher: design kit for the website

Everything here is taken from the WPF launcher (`launcher/src/Mkwmp.App`), so a web page
can look exactly like it. Sources:

| What | File |
|---|---|
| Colours, light / dark | `Themes/Colors.Light.xaml`, `Themes/Colors.Dark.xaml` (same keys, swapped at runtime) |
| Type scale, radii, metrics | `Themes/Typography.xaml` |
| Control looks and states | `Themes/Controls.xaml` |
| Icons | `Themes/Icons.xaml` (drawn by `Controls/Icon.cs`) |
| Focus ring | `Controls/FocusRing.cs` |
| Tile gradients, avatar colours | `Controls/Buttons.cs` (`GeneratedArt`) |
| Hint bar, controller glyphs | `Controls/HintBar.xaml`, `Controls/Display.cs` (`HintGlyph`) |
| Motion | `Controls/PageHost.cs`, `ShellParts.cs`, `Containers.cs`, `ToastHost.xaml`, `MainWindow.Overlay.cs` |
| App mark (logo) | `MainWindow.Harness.cs` (`RenderMark`) |

## 0. Units

The launcher draws on a stage that is always **1080 units high** and scales it to the window.
Every size below is in stage units: at 1920x1080, 1 unit = 1 CSS px. The CSS below multiplies
every size by `--u`; leave it at `1px` for a 1080p-faithful mock-up, or set it on `:root` only
(for example `--u: 0.5px` for a compact page section, or `--u: calc(100vh / 1080)` for a
full-screen replica). Change `--u` on `:root`, not on a descendant: the derived tokens are
computed once, on `:root`.

## 1. Colours

| Token (XAML key) | CSS | Light | Dark | Used for |
|---|---|---|---|---|
| Brush.Background | `--bg` | `#EBEBEB` | `#2A2A2D` | the stage, sheets, panels |
| Brush.Surface | `--surface` | `#FFFFFF` | `#3B3B40` | text fields, cards on a page, modal footer button |
| Brush.SurfaceAlt | `--surface-alt` | `#F6F6F6` | `#333337` | info cards, the dock pill |
| Brush.Modal | `--modal` | `#F0F0F0` | `#37373B` | modal card |
| Brush.Text | `--text` | `#2B2B2B` | `#F3F3F3` | text, line icons |
| Brush.TextMuted | `--text-muted` | `#6C6C6C` | `#B1B1B6` | captions, small text, chevrons |
| Brush.TextFaint | `--text-faint` | `#AEAEAE` | `#737378` | placeholders, disabled rows, scroll thumb |
| Brush.Accent | `--accent` | `#1E64E6` | `#5CB8FF` | values, selected category, primary button, switches |
| Brush.AccentSoft | `--accent-soft` | `#1E64E6` at 12% (`#1F1E64E6`) | `#5CB8FF` at 18% (`#2E5CB8FF`) | pills ("This version"), icon discs |
| Brush.OnAccent | `--on-accent` | `#FFFFFF` | `#0F1A26` | text on the accent |
| Brush.Rule | `--rule` | `#C6C6C6` | `#56565C` | 1.5-unit separators |
| Brush.Bar | `--bar` | `#2B2B2B` | `#F3F3F3` | the bar before a section header |
| Brush.Good | `--good` | `#16A34A` | `#3DD27A` | online dot, "ready" bullets |
| Brush.Warn | `--warn` | `#D98200` | `#FFB933` | warnings, mid latency |
| Brush.Bad | `--bad` | `#E0352B` | `#FF6B61` | errors |
| Brush.Dim | `--dim` | black at 45% (`#73000000`) | black at 60% (`#99000000`) | modal / panel backdrop |
| Brush.Focus.Fill | `--focus-fill` | `#FFFFFF` | `#47474D` | the card under a focused control |
| Brush.Track | `--track` | `#CFCFCF` | `#5C5C62` | switch off, progress track, offline avatar |
| Brush.Knob | `--knob` | `#FFFFFF` | `#FFFFFF` | switch / slider knob |
| Brush.Hover | `--hover` | black at 5% (`#0D000000`) | white at 7% (`#12FFFFFF`) | mouse hover wash |
| Brush.Pressed | `--pressed` | black at 10% (`#1A000000`) | white at 14% (`#24FFFFFF`) | pressed wash |
| Brush.Glyph | `--glyph` | `#2B2B2B` | `#F3F3F3` | controller glyph discs |
| Brush.OnGlyph | `--on-glyph` | `#EBEBEB` | `#2A2A2D` | the letter on a glyph |
| Brush.Toast | `--toast` | `#FFFFFF` | `#47474D` | toasts |
| Brush.Outline | `--outline` | `#8A8A8A` | `#8E8E94` | secondary button, text field, checkbox borders |

XAML colours are `#AARRGGBB`; the CSS below uses `#RRGGBBAA` or `rgb(... / a)`.

### Gradients

**Focus ring** (`FocusRing.cs`), a linear gradient from top-left to bottom-right that turns once
every 5 s: `#2F74FF 0%`, `#58D3FF 22%`, `#2F74FF 42%`, `#9A6BFF 62%`, `#FF8BC2 82%`, `#2F74FF 100%`
(blue, cyan, blue, violet, pink, blue).

**Tiles** (`GeneratedArt.For`), all at 135deg (top-left to bottom-right). A tile picks one by a
hash of its name; Home uses fixed ones: Play `#0`, Friends `#3`, Game `#2`, What's New `#5`.

| # | From | To |
|---|---|---|
| 0 | `#2F6BFF` | `#19C3F2` |
| 1 | `#7A4DFF` | `#FF6FB5` |
| 2 | `#FF7A1A` | `#FFC233` |
| 3 | `#10B981` | `#22D3EE` |
| 4 | `#F0443A` | `#FF9A3D` |
| 5 | `#4338CA` | `#3B9BFF` |
| 6 | `#EC4899` | `#FFA24D` |
| 7 | `#0EA5A0` | `#9BDB3A` |

Over the gradient sit three white discs (on a 372 tile: 360 at (150,-170) 14% white, 300 at
(-140,210) 10% white, 120 at (250,170) 8% white). A tile with a picture gets a scrim instead:
`linear-gradient(180deg, #00000040 0%, transparent 28%, transparent 50%, #000000C8 100%)`.

**App mark**: `#2F74FF` to `#8A55FF`, 135deg, radius 58 on 256 (see `mkwmp-mark.svg`).

**Avatar discs** (`GeneratedArt.Disc`, by name hash): `#2F6BFF #7A4DFF #E2498A #F0443A #F08A1A
#12A86B #0E9DB8 #5B5BD6 #C0399F #4E8F2D`.

**Dock icon tints** (Home): Play = accent disc with `--on-accent` icon; Game menu `#5B5BD6`,
Friends `#F08A1A`, Updates `#E2498A`, Full screen `#0E9DB8`, Mods `#2F9E5B`, Quit game `#E5484D`,
Settings / Power `--text`.

### Shadows

None. The launcher draws no drop shadows anywhere (cheap under Wine/Proton); depth comes from
surface colours, the dim backdrop and the focus halo. Don't add `box-shadow` to cards.

## 2. Type

**Nunito** (SIL OFL 1.1, Copyright 2014 The Nunito Project Authors) in four weights:
Regular 400, SemiBold 600, Bold 700, ExtraBold 800. The `.ttf` files are embedded in the exe
(`launcher/src/Mkwmp.App/Assets/Fonts/`); this kit ships them as WOFF2 in `fonts/` with the
licence (`fonts/OFL.txt`). For Japanese, Korean and Chinese the launcher falls back to Noto Sans
JP / KR / SC / TC (OFL, `NotoSansCJK-LICENSE.txt`), language's own first; a site can use Google
Fonts' Noto Sans JP etc. for those.

| Style (XAML) | Size | Weight | Line height | Colour |
|---|---|---|---|---|
| Text.Display | 56 | 800 | - | text |
| Text.Title | 42 | 600 (page headers use 400) | - | text |
| Text.Heading | 34 | 600 (modal titles use 400) | - | text |
| Text.Body | 30 | 400 | 42 | text |
| Text.Caption | 25 | 400 | 36 | muted |
| Text.Small | 21 | 400 | - | muted |
| Settings row label | 32 | 400 | - | text |
| Section header | 28 | 400 | - | text |
| Category item | 32 | 400 (selected: accent) | - | text |
| Category group heading | 23 | 600 | - | muted |
| Button | 30 | 600 | - | per kind |
| Hint label | 34 | 400 | - | text |
| Tile title | 38 | 800 | 42 | white |
| Tile subtitle | 23 | 600 | 29 | white at 90% |
| Tile badge | 22 | 700 | - | `#2B2B2B` on white 95% |
| Dock caption | 28 | 600 | - | accent |
| Status line ("READY . PRESS PLAY") | uppercase body, muted | | | |

Text never wraps in rows (ellipsis); body and captions wrap.

## 3. Radii, spacing, metrics

| Token | Value |
|---|---|
| Radius.Control (buttons, fields, rows, focus ring) | 12 |
| Radius.Card (cards, modal) | 14 |
| Radius.Tile | 20 (focused tile ring: 28) |
| Pill | 24 (48 high) |
| Dock | 70 |
| Sheets (bottom sheet, keyboard) | 30 30 0 0 |
| Toast | 18 |
| Checkbox | 7 |
| Page margin | 72 left/right |
| Page header height | 126 (title at 48 + 72 from the left, 1.5 rule at the bottom) |
| Hint bar height | 112 (1.5 rule at the top, content inset 98 left / 88 right) |
| Settings row min height | 104 (inner padding 20 x 12) |
| Category item | 110 high, 6-unit gaps |
| Button | 88 high, min 280 wide, 44 side padding, 2.5 border |
| Text field | 88 high, 26 side padding, 2.5 border |
| Tile | 372 x 372, 28 between tiles |
| Dock button | 104 circle, 16 apart; dock padding 30 x 18 |
| Avatar | 88, 3-unit surface stroke; presence dot 26 with a 4-unit background stroke |
| Modal card | 1080 wide, padding 88 top / 112 sides / 56 bottom; footer button 112 high |
| Card (info) | padding 32 x 26 |
| Section header | 44 above, 5 x 34 bar (radius 1.5), 16 gap, 16 below, then a 1.5 rule |
| Rules | 1.5 thick |
| Scroll bar | 8 wide, thumb radius 4, colour text-faint, no track |

## 4. The focus ring

The one focus indicator (`FocusRing.cs`), used by every focusable control. When a control is
focused it becomes a card (`--focus-fill`: white in light, `#47474D` in dark) with:

- a **5-unit gradient border** (6 on tiles), inset inside the bounds, inner radius = radius - 2.5;
- a **halo**: two strokes of the same gradient centred on the outer edge, 19 units wide at 13%
  opacity and 11 units wide at 22% opacity (so it glows 9.5 and 5.5 units outside);
- the gradient **turns** 360deg every 5 s, linearly, forever; the ring fades in from 20% to 100%
  opacity in 140 ms.

Where the ring sits: buttons, fields, checkboxes: on the bounds, radius 12. Settings rows:
10 wider each side and 3 taller (`margin: -3 -10`). Tiles: 11 outside, radius 28, and the tile
scales to 1.045. Card buttons: 4 outside, radius 16. Switches: 9 outside, radius 30. Dock buttons
and avatars: a circle. A focused element is drawn above its neighbours (z-index 1). The mouse
gets a hover wash (`--hover`), never the ring.

## 5. Controls and their states

| Control | Normal | Focused | Pressed | Disabled |
|---|---|---|---|---|
| Button, Default | transparent, 2.5 accent border, accent 30/600 text | focus card + ring (text keeps its colour) | scale 0.98 | opacity 0.38 |
| Button, Primary | accent fill, on-accent text | ring, fill stays accent | scale 0.98 | 0.38 |
| Button, Secondary | transparent, 2.5 outline border, text colour | focus card + ring | scale 0.98 | 0.38 |
| Button, Ghost | no border, accent text | focus card + ring | scale 0.98 | 0.38 |
| Button, Bottom (modal footer) | surface fill, radius 0 0 14 14, 112 high, 34/400 accent | ring 3 outside, radius 14 | scale 0.98 | 0.38 |
| Settings row | transparent; label left, value (accent) / switch / chevron (muted, 34) right; rule below; caption (25 muted) under the rule | focus card + ring (-10 x -3) | - | text and value faint |
| Category item | label 32 (icon 40, stroke 1.8) | focus card + ring | - | - |
| Category item, selected | accent text, a 6 x 72 accent bar at 32 from the left | | | |
| Switch | 76 x 42 track (track colour), 32 knob 5 from the left | ring 9 outside | knob slides 34 in 120 ms | 0.4 |
| Switch, on | accent track | | | |
| Checkbox | 46 box, 2.5 outline border, surface fill | ring on the whole line | - | 0.4 |
| Checkbox, checked | accent box, on-accent tick (stroke 3) | | | |
| Text field | surface, 2.5 outline border, radius 12; caret accent | the focus card covers it with the ring | - | 0.45 |
| Slider | label, 420-wide track (10 high, accent / track), 36 knob (knob fill, 5 accent stroke), value in accent | ring like a row | - | 0.4 |
| Tile | gradient, 150 icon (stroke 1.6) top-left at (30,34), title / subtitle bottom-left (32 / 34 in), badge top-right | scale 1.045 in 130 ms + ring | 8% black shade | 0.5 |
| Dock button | 104 circle, 58 icon stroke 1.8 in its tint; 20 accent dot top-right for news | focus card circle + ring, caption below in accent | opacity 0.8 | 0.35 |
| Card button | surface, radius 14, padding 28 x 22 | ring 4 outside, radius 16 | - | 0.45 |
| Progress | 12 high, radius 6, track / accent | | | |
| Spinner | 44, 4-stroke track circle with a quarter arc in muted, 0.9 s a turn | | | |
| Pill | accent-soft, 48 high, radius 24, padding 22 | | | |

Hover (mouse only): a `--hover` wash with the control's radius.

## 6. Hint bar and controller glyphs

The bar at the bottom of every screen (`HintBar.xaml`): 112 high, a 1.5 rule along its top
(inset by the page margin), the input device at the left (the solid gamepad icon 66, with a
12 x 12 green square, radius 2.5, at its top-left when a pad is connected; a keyboard icon
otherwise), and the hints at the right, each a glyph + a 34 label, 14 between them, 6 + 28
(padding) between hints. Hints are mouse-clickable (hover wash, radius 12), never focusable.

Glyphs (`HintGlyph`): a **42 disc** in `--glyph` with the letter in `--on-glyph`, Nunito
ExtraBold 26 (22 for two letters). LB / RB are 68 x 42 pills. Start is three 18-wide lines
(stroke 3.2, 7 apart); Back is two overlapping 13 x 11 rounded squares; D-pad directions are
a filled triangle. With a keyboard the glyph becomes a key cap: 2.5 outline in `--glyph`,
radius 9, the key name (Enter, Esc, F, Q, E, F1, F2, arrows) in ExtraBold 22.
Button labels are generic (A B X Y LB RB): no console maker's symbols.

## 7. Motion

All eases are cubic. CSS: ease-out cubic = `cubic-bezier(0.33, 1, 0.68, 1)`, ease-in cubic =
`cubic-bezier(0.32, 0, 0.67, 0)`.

| What | Duration | Easing |
|---|---|---|
| Focus ring appears | 140 ms, opacity 0.2 to 1 | linear |
| Focus ring gradient turn | 5 s per turn, forever | linear |
| Tile focus pop | 130 ms, scale 1 to 1.045 and back | linear |
| Button press | instant scale 0.98 | - |
| Switch knob | 120 ms | linear |
| Page change | 220 ms slide of 56 (forward from the right, back from the left) + 180 ms fade in | ease-out |
| Modal open | backdrop fade 140 ms; card fade 160 ms + scale 0.96 to 1 in 200 ms | ease-out |
| Side panel / sheet (friends, picker, keyboard) | in 240 ms ease-out, out 180 ms ease-in, slides its own width | cubic |
| Scrolling to the focused item | 190 ms | ease-out |
| Toast | in: slide 120 to 0 in 240 ms ease-out + fade 200 ms; out: fade 220 ms | |
| Spinner | 0.9 s per turn | linear |
| In-game overlay | open 480 ms, return 350 ms (the game picture shrinks into the Play tile) | smoothstep |
| Gamepad repeat | first after 380 ms, then every 105 ms | - |

Honour `prefers-reduced-motion`: the launcher skips glides when Windows animations are off.

## 8. Icons

68 icons of MKWMP's own on a 24 x 24 grid, defined as WPF path data in
`Themes/Icons.xaml`: `Icon.*` are line icons (stroke 2 by default, round caps and joins, in the
text colour; page headers use 1.8, tiles 1.6), `IconFill.*` are solid. The path data is the
same syntax as SVG's `d`, so export is a copy: `icons/*.svg` here is every icon, one file per
key (`Icon.ChevronRight` -> `chevron-right.svg`, `IconFill.Gamepad` -> `gamepad-fill.svg`),
`stroke="currentColor"` / `fill="currentColor"`, so CSS `color` tints them. Filled icons use
`fill-rule="evenodd"` (WPF's default), which cuts the gamepad's buttons out. To re-export after
the XAML changes, read every `<Geometry x:Key="Icon.Name">DATA</Geometry>` and write
`<svg viewBox="0 0 24 24"><path d="DATA" fill="none" stroke="currentColor" stroke-width="2"
stroke-linecap="round" stroke-linejoin="round"/></svg>`.

To tint an icon from CSS, inline the SVG (it follows `color`), or use the file as a mask:
`.ico { display: inline-block; width: 1em; height: 1em; background: currentColor;
mask: url(icons/globe.svg) center / contain no-repeat; }`. An `<img>` keeps the SVG's own
colour (black). Stroke widths scale with the icon: 2 on the 24 grid is 2/24 of its size.

Most useful for the site: `kart` (Play), `friends`, `folder` (Game), `download` (What's New),
`package` (Mods), `settings`, `power`, `globe`, `mic`, `camera`, `headset`, `shield`, `lock`,
`gamepad-fill`, `keyboard`, `trophy`, `flag`, `check`, `chevron-right`, `monitor`, `refresh`.

`mkwmp-mark.svg` is the app icon (a chequered flag on the blue-violet gradient), the same
drawing as `--icon` writes.

## 9. CSS

```css
@font-face { font-family: Nunito; font-weight: 400; font-display: swap; src: url(fonts/Nunito-Regular.woff2) format("woff2"); }
@font-face { font-family: Nunito; font-weight: 600; font-display: swap; src: url(fonts/Nunito-SemiBold.woff2) format("woff2"); }
@font-face { font-family: Nunito; font-weight: 700; font-display: swap; src: url(fonts/Nunito-Bold.woff2) format("woff2"); }
@font-face { font-family: Nunito; font-weight: 800; font-display: swap; src: url(fonts/Nunito-ExtraBold.woff2) format("woff2"); }

@property --ring-angle { syntax: "<angle>"; inherits: false; initial-value: 135deg; }

:root {
  --u: 1px;                                   /* 1 stage unit; 1px = the launcher at 1080p */
  --font: Nunito, "Noto Sans JP", "Noto Sans KR", "Noto Sans SC", "Noto Sans TC", system-ui, sans-serif;

  /* colours: light */
  --bg: #EBEBEB;  --surface: #FFFFFF;  --surface-alt: #F6F6F6;  --modal: #F0F0F0;
  --text: #2B2B2B;  --text-muted: #6C6C6C;  --text-faint: #AEAEAE;
  --accent: #1E64E6;  --accent-soft: #1E64E61F;  --on-accent: #FFFFFF;
  --rule: #C6C6C6;  --bar: #2B2B2B;
  --good: #16A34A;  --warn: #D98200;  --bad: #E0352B;
  --dim: #00000073;  --focus-fill: #FFFFFF;  --track: #CFCFCF;  --knob: #FFFFFF;
  --hover: #0000000D;  --pressed: #0000001A;
  --glyph: #2B2B2B;  --on-glyph: #EBEBEB;  --toast: #FFFFFF;  --outline: #8A8A8A;

  /* focus ring */
  --ring-stops: #2F74FF 0%, #58D3FF 22%, #2F74FF 42%, #9A6BFF 62%, #FF8BC2 82%, #2F74FF 100%;
  --ring-w: calc(5 * var(--u));
  --halo-outer: #2F74FF21 0%, #58D3FF21 22%, #2F74FF21 42%, #9A6BFF21 62%, #FF8BC221 82%, #2F74FF21 100%; /* 13% */
  --halo-inner: #2F74FF38 0%, #58D3FF38 22%, #2F74FF38 42%, #9A6BFF38 62%, #FF8BC238 82%, #2F74FF38 100%; /* 22% */

  /* tile gradients (GeneratedArt) */
  --tile-0: linear-gradient(135deg, #2F6BFF, #19C3F2);
  --tile-1: linear-gradient(135deg, #7A4DFF, #FF6FB5);
  --tile-2: linear-gradient(135deg, #FF7A1A, #FFC233);
  --tile-3: linear-gradient(135deg, #10B981, #22D3EE);
  --tile-4: linear-gradient(135deg, #F0443A, #FF9A3D);
  --tile-5: linear-gradient(135deg, #4338CA, #3B9BFF);
  --tile-6: linear-gradient(135deg, #EC4899, #FFA24D);
  --tile-7: linear-gradient(135deg, #0EA5A0, #9BDB3A);
  --mark: linear-gradient(135deg, #2F74FF, #8A55FF);

  /* type */
  --fs-display: calc(56 * var(--u));  --fs-title: calc(42 * var(--u));  --fs-heading: calc(34 * var(--u));
  --fs-body: calc(30 * var(--u));  --fs-caption: calc(25 * var(--u));  --fs-small: calc(21 * var(--u));
  --fs-row: calc(32 * var(--u));  --fs-section: calc(28 * var(--u));  --fs-hint: calc(34 * var(--u));
  --lh-body: calc(42 * var(--u));  --lh-caption: calc(36 * var(--u));

  /* radii and metrics */
  --r-control: calc(12 * var(--u));  --r-card: calc(14 * var(--u));  --r-tile: calc(20 * var(--u));
  --r-pill: calc(24 * var(--u));  --r-dock: calc(70 * var(--u));  --r-sheet: calc(30 * var(--u));
  --rule-w: calc(1.5 * var(--u));  --border-w: calc(2.5 * var(--u));
  --page-margin: calc(72 * var(--u));  --header-h: calc(126 * var(--u));  --hintbar-h: calc(112 * var(--u));
  --row-h: calc(104 * var(--u));  --button-h: calc(88 * var(--u));  --tile: calc(372 * var(--u));

  /* motion */
  --ease-out: cubic-bezier(0.33, 1, 0.68, 1);
  --ease-in: cubic-bezier(0.32, 0, 0.67, 0);
  --t-ring: 140ms;  --t-pop: 130ms;  --t-switch: 120ms;  --t-page: 220ms;  --t-modal: 200ms;
  --t-panel-in: 240ms;  --t-panel-out: 180ms;  --t-scroll: 190ms;  --t-toast: 240ms;
}

[data-theme=dark] {
  --bg: #2A2A2D;  --surface: #3B3B40;  --surface-alt: #333337;  --modal: #37373B;
  --text: #F3F3F3;  --text-muted: #B1B1B6;  --text-faint: #737378;
  --accent: #5CB8FF;  --accent-soft: #5CB8FF2E;  --on-accent: #0F1A26;
  --rule: #56565C;  --bar: #F3F3F3;
  --good: #3DD27A;  --warn: #FFB933;  --bad: #FF6B61;
  --dim: #00000099;  --focus-fill: #47474D;  --track: #5C5C62;  --knob: #FFFFFF;
  --hover: #FFFFFF12;  --pressed: #FFFFFF24;
  --glyph: #F3F3F3;  --on-glyph: #2A2A2D;  --toast: #47474D;  --outline: #8E8E94;
}

body { background: var(--bg); color: var(--text); font: 400 var(--fs-body)/var(--lh-body) var(--font); }

/* ── the focus ring: add .is-focused (or rely on :focus-visible) on any control below ──
   Per control: --r (card radius), --ring-x / --ring-y (how far the card reaches outside the
   control), --ring-w (ring thickness), --focus-fill (the card's colour). */
.focusable { position: relative; --r: var(--r-control); --ring-x: 0px; --ring-y: 0px; }
.focusable:is(.is-focused, :focus-visible) { outline: none; z-index: 1; }
/* the card and its gradient ring, behind the content */
.focusable:is(.is-focused, :focus-visible)::after {
  content: ""; position: absolute; z-index: -1; pointer-events: none;
  inset: calc(-1 * var(--ring-y)) calc(-1 * var(--ring-x));
  border: var(--ring-w) solid transparent; border-radius: var(--r);
  background:
    linear-gradient(var(--focus-fill), var(--focus-fill)) padding-box,
    linear-gradient(var(--ring-angle), var(--ring-stops)) border-box;
  animation: ring-in var(--t-ring) linear, ring-turn 5s linear infinite;
}
/* the halo: two faint strokes of the gradient around the card (13% over 9.5 units, 22% over 5.5) */
.focusable:is(.is-focused, :focus-visible)::before {
  content: ""; position: absolute; z-index: -1; pointer-events: none;
  inset: calc(-1 * var(--ring-y) - 9.5 * var(--u)) calc(-1 * var(--ring-x) - 9.5 * var(--u));
  border: calc(4 * var(--u)) solid transparent; padding: calc(5.5 * var(--u));
  border-radius: calc(var(--r) + 9.5 * var(--u));
  background:
    linear-gradient(var(--ring-angle), var(--halo-inner)) padding-box,
    linear-gradient(var(--ring-angle), var(--halo-outer)) border-box;
  -webkit-mask: linear-gradient(#000 0 0) content-box, linear-gradient(#000 0 0);
  -webkit-mask-composite: xor;
          mask: linear-gradient(#000 0 0) content-box exclude, linear-gradient(#000 0 0);
  animation: ring-turn 5s linear infinite;
}
@keyframes ring-turn { to { --ring-angle: 495deg; } }
@keyframes ring-in { from { opacity: .2; } }
@media (prefers-reduced-motion: reduce) { .focusable::before, .focusable::after { animation: none !important; } }
```

`::after` is the focus card (fill + 5-unit ring inside its edge), `::before` the halo, masked so
it only shows outside the card. Both sit at `z-index: -1` inside the focused control's own
stacking context: above the control's background and border, below its content, which is the
launcher's drawing order. A control whose look is its background (a tile) keeps that look on a
child element. Browsers without `@property` show a still ring.

### Button

```html
<button class="btn focusable">Next</button>
<button class="btn btn--primary focusable">Download</button>
<button class="btn btn--secondary focusable">Not Now</button>
```

```css
.btn {
  height: var(--button-h); min-width: calc(280 * var(--u)); padding: 0 calc(44 * var(--u));
  display: inline-flex; align-items: center; justify-content: center; gap: calc(16 * var(--u));
  font: 600 var(--fs-body) var(--font); color: var(--accent);
  background: transparent; border: var(--border-w) solid var(--accent); border-radius: var(--r-control);
  cursor: pointer; transition: transform 60ms;
}
.btn svg { width: calc(38 * var(--u)); height: calc(38 * var(--u));
  fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
.btn--primary { background: var(--accent); color: var(--on-accent); }
.btn--primary:is(.is-focused, :focus-visible) { --focus-fill: var(--accent); }
.btn--secondary { border-color: var(--outline); color: var(--text); }
.btn:hover:not(.is-focused, :focus-visible) { background-image: linear-gradient(var(--hover), var(--hover)); }
.btn:active { transform: scale(.98); }
.btn:disabled { opacity: .38; cursor: default; }
```

### Card button and tile

```html
<button class="card-btn focusable">
  <strong>Neon Karts</strong><small>pixelfox · Skins</small>
</button>

<button class="tile focusable" style="--art: var(--tile-0)">
  <span class="tile__art">
    <svg viewBox="0 0 24 24"><!-- the paths of icons/kart.svg --></svg>
    <span class="tile__badge">12/32 racing</span>
    <span class="tile__title">Play Online</span>
    <span class="tile__sub">Ready to race</span>
  </span>
</button>
```

```css
.card-btn { --r: calc(16 * var(--u)); --ring-x: calc(4 * var(--u)); --ring-y: calc(4 * var(--u));
  display: grid; gap: calc(6 * var(--u)); text-align: left; padding: calc(22 * var(--u)) calc(28 * var(--u));
  background: var(--surface); color: var(--text); border: 0; border-radius: var(--r-card); font: inherit; }
.card-btn small { font-size: var(--fs-small); color: var(--text-muted); }
.card-btn:disabled { opacity: .45; }

/* the button is the focus target; the art is a child, so the focus card can sit between them */
.tile { --r: calc(28 * var(--u)); --ring-x: calc(11 * var(--u)); --ring-y: calc(11 * var(--u)); --ring-w: calc(6 * var(--u));
  position: relative; width: var(--tile); height: var(--tile); margin: 0 calc(14 * var(--u)); padding: 0; border: 0; background: none;
  font-family: var(--font); text-align: left; cursor: pointer; transition: transform var(--t-pop) linear; }
.tile:is(.is-focused, :focus-visible) { transform: scale(1.045); }
.tile__art { position: absolute; inset: 0; overflow: hidden; border-radius: var(--r-tile); color: #fff;
  background:                                               /* the soft discs over the gradient */
    radial-gradient(circle at calc(330 * var(--u)) calc(10 * var(--u)), #FFFFFF24 calc(180 * var(--u)), transparent 0),
    radial-gradient(circle at calc(10 * var(--u)) calc(360 * var(--u)), #FFFFFF1A calc(150 * var(--u)), transparent 0),
    radial-gradient(circle at calc(310 * var(--u)) calc(230 * var(--u)), #FFFFFF14 calc(60 * var(--u)), transparent 0),
    var(--art); }
.tile:active .tile__art { box-shadow: inset 0 0 0 100vmax #00000014; }   /* pressed: 8% shade */
.tile:disabled { opacity: .5; }
.tile svg { position: absolute; left: calc(30 * var(--u)); top: calc(34 * var(--u));
  width: calc(150 * var(--u)); height: calc(150 * var(--u));
  fill: none; stroke: currentColor; stroke-width: 1.6; stroke-linecap: round; stroke-linejoin: round; }
.tile__title { position: absolute; left: calc(32 * var(--u)); right: calc(28 * var(--u)); bottom: calc(69 * var(--u));
  font-weight: 800; font-size: calc(38 * var(--u)); line-height: calc(42 * var(--u)); }
.tile__sub { position: absolute; left: calc(32 * var(--u)); right: calc(28 * var(--u)); bottom: calc(34 * var(--u));
  font-weight: 600; font-size: calc(23 * var(--u)); line-height: calc(29 * var(--u)); color: #FFFFFFE6;
  white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.tile__badge { position: absolute; top: calc(22 * var(--u)); right: calc(22 * var(--u)); height: calc(44 * var(--u));
  padding: 0 calc(18 * var(--u)); border-radius: calc(22 * var(--u)); background: #FFFFFFF2; color: #2B2B2B;
  font: 700 calc(22 * var(--u))/calc(44 * var(--u)) var(--font); }
```

### Settings row

```html
<div class="section"><span>Server</span></div>
<button class="row focusable">
  <svg class="row__icon"><!-- icons/globe.svg --></svg>
  <span class="row__label">Server Address</span>
  <span class="row__value">mkw.example.net</span>
  <svg class="row__chevron"><!-- icons/chevron-right.svg --></svg>
</button>
<p class="row__caption">Choose another server. The PIN you have on this one is kept for when you come back.</p>

<button class="row focusable" role="switch" aria-checked="true">
  <span class="row__label">Fullscreen</span>
  <span class="switch is-on"></span>
</button>
```

```css
.section { margin-top: calc(44 * var(--u)); padding-bottom: calc(16 * var(--u)); border-bottom: var(--rule-w) solid var(--rule);
  display: flex; align-items: center; gap: calc(16 * var(--u)); font-size: var(--fs-section); }
.section::before { content: ""; width: calc(5 * var(--u)); height: calc(34 * var(--u)); border-radius: calc(1.5 * var(--u)); background: var(--bar); }

.row { --ring-x: calc(10 * var(--u)); --ring-y: calc(3 * var(--u));   /* the card is 10 wider each side, 3 taller */
  width: 100%; min-height: var(--row-h); display: flex; align-items: center;
  padding: calc(12 * var(--u)) calc(20 * var(--u)); gap: 0; text-align: left;
  font: 400 var(--fs-row) var(--font); color: var(--text); background: transparent;
  border: 0; border-bottom: var(--rule-w) solid var(--rule); border-radius: 0; cursor: pointer; }
.row:hover:not(.is-focused, :focus-visible) { background: var(--hover); border-radius: var(--r-control); }
.row svg { fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
.row__icon { width: calc(44 * var(--u)); height: calc(44 * var(--u)); margin-right: calc(24 * var(--u)); flex: none; }
.row__label { flex: 1; min-width: 0; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.row__value { margin-left: calc(32 * var(--u)); color: var(--accent); max-width: calc(560 * var(--u));
  white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.row__chevron { width: calc(34 * var(--u)); height: calc(34 * var(--u)); margin: 0 calc(-6 * var(--u)) 0 calc(14 * var(--u)); color: var(--text-muted); }
.row:disabled, .row:disabled .row__value { color: var(--text-faint); }
.row__caption { margin: calc(14 * var(--u)) calc(20 * var(--u)) calc(20 * var(--u));
  font-size: var(--fs-caption); line-height: var(--lh-caption); color: var(--text-muted); }

.switch { position: relative; width: calc(76 * var(--u)); height: calc(42 * var(--u)); border-radius: calc(21 * var(--u));
  background: var(--track); flex: none; margin-left: calc(32 * var(--u)); }
.switch::after { content: ""; position: absolute; top: calc(5 * var(--u)); left: calc(5 * var(--u));
  width: calc(32 * var(--u)); height: calc(32 * var(--u)); border-radius: 50%; background: var(--knob);
  transition: transform var(--t-switch) linear; }
.switch.is-on { background: var(--accent); }
.switch.is-on::after { transform: translateX(calc(34 * var(--u))); }
```

### Category list (Settings' left column)

```css
.cat { position: relative; height: calc(110 * var(--u)); margin: calc(6 * var(--u)) 0;
  display: flex; align-items: center; gap: calc(18 * var(--u)); padding: 0 calc(24 * var(--u)) 0 calc(68 * var(--u));
  font-size: var(--fs-row); color: var(--text); }
.cat svg { width: calc(40 * var(--u)); height: calc(40 * var(--u));
  fill: none; stroke: currentColor; stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }
.cat.is-selected { color: var(--accent); }
.cat.is-selected::after { content: ""; position: absolute; left: calc(32 * var(--u)); top: 50%; translate: 0 -50%;
  width: calc(6 * var(--u)); height: calc(72 * var(--u)); border-radius: calc(1.5 * var(--u)); background: var(--accent); }
```

### Hint bar

```html
<footer class="hintbar">
  <span class="hintbar__device"><svg><!-- icons/gamepad-fill.svg --></svg></span>
  <span class="hint"><b class="glyph">Y</b>Friends</span>
  <span class="hint"><b class="glyph">B</b>Back</span>
  <span class="hint"><b class="glyph">A</b>OK</span>
</footer>
```

```css
.hintbar { position: relative; height: var(--hintbar-h); display: flex; align-items: center; justify-content: flex-end;
  padding: 0 calc(88 * var(--u)) 0 calc(98 * var(--u)); }
.hintbar::before { content: ""; position: absolute; top: 0; left: var(--page-margin); right: var(--page-margin);
  height: var(--rule-w); background: var(--rule); }
.hintbar__device { margin-right: auto; position: relative; color: var(--glyph); }
.hintbar__device svg { width: calc(66 * var(--u)); height: calc(66 * var(--u)); display: block; }
.hintbar__device::after { content: ""; position: absolute; left: calc(4 * var(--u)); top: calc(-4 * var(--u));   /* pad connected */
  width: calc(12 * var(--u)); height: calc(12 * var(--u)); border-radius: calc(2.5 * var(--u)); background: var(--good); }
.hint { display: inline-flex; align-items: center; gap: calc(14 * var(--u)); margin-left: calc(6 * var(--u));
  padding: calc(10 * var(--u)) calc(14 * var(--u)); border-radius: var(--r-control); font-size: var(--fs-hint); cursor: pointer; }
.hint:hover { background: var(--hover); }
.hint:active { background: var(--pressed); }
.glyph { display: inline-grid; place-items: center; min-width: calc(42 * var(--u)); height: calc(42 * var(--u));
  border-radius: calc(21 * var(--u)); background: var(--glyph); color: var(--on-glyph);
  font: 800 calc(26 * var(--u))/1 var(--font); }
.glyph--wide { min-width: calc(68 * var(--u)); font-size: calc(22 * var(--u)); }       /* LB, RB */
.glyph--key { background: none; color: var(--glyph); border: var(--border-w) solid var(--glyph);
  border-radius: calc(9 * var(--u)); padding: 0 calc(13 * var(--u)); font-size: calc(22 * var(--u)); }  /* Enter, Esc */
```

## 10. Screenshots

All rendered by the launcher's own off-screen harness (`mkwmp.exe --demo --shot ...`,
see `launcher/shot.ps1`) from the demo data, at 1920x1080 (`*-light.webp`, `*-dark.webp`) and
1280x800 for the Steam Deck (`deck-home-*.webp`); WebP quality 85. The demo has no game cover
and no game art: the Play tile is a generated gradient, the overlay's "game" is a drawn
placeholder (sky, hills, a road, a box kart), and the mod store thumbnails are generated
gradients. The welcome and crash pictures and page backgrounds are MKWMP's own art.

English is in this folder; the same set in Brazilian Portuguese (`--locale pt-BR`) is in
`pt-BR/`, with the same file names. Some text is still English there, as the launcher shows
it today: a server's privacy policy, the changelog, a mod's own options, the Game Settings
pages (Screen, Graphics, ...) and part of About.

| File | Screen |
|---|---|
| `home` | Home: tiles, dock, status line, hint bar |
| `home-friends` | Home with the friends panel open (requests, friends) |
| `welcome` | Welcome flow, first page (language, Next) |
| `welcome-name` | Welcome: your name |
| `welcome-game` | Welcome: your game (ready) |
| `welcome-play-online` | Welcome: "Want to go online?" |
| `welcome-server` | Welcome: choose a server (list with latency bars, address field) |
| `welcome-sign-in` | Welcome: register your profile (gamertag) |
| `welcome-privacy` | Welcome: before you play online (18+, privacy policy checkbox) |
| `welcome-pin` | Welcome: your PIN, masked |
| `welcome-done` | Welcome: all set |
| `game-setup-progress` | Game setup in progress (modal with progress bar, step, time left) |
| `settings-online` | Settings > Online (profile card with masked PIN, server, status) |
| `settings-game` | Settings > Game (runtime, folder, checks, game setup) |
| `settings-game-options` | Settings > Game Settings > Screen (the game's own options) |
| `settings-voice` | Settings > Voice & Camera |
| `settings-appearance` | Settings > Appearance (theme, full screen, language) |
| `settings-language` | Settings > Appearance > Language picker (modal list) |
| `settings-about` | Settings > About (licences, links) |
| `privacy-policy` | A server's privacy policy as the launcher renders it |
| `mods` | Mods: installed mods list |
| `mods-gamebanana` | Get Mods: the GameBanana grid (demo data) |
| `mods-options` | A mod's page with its options (Lap Timer HUD) |
| `whats-new` | What's New: update banner and changelog |
| `update-available` | The update modal (Download / Later) |
| `overlay` | The in-game overlay (Home over a running game) |
| `crash` | The crash screen after the game stopped |
| `crash-report` | The crash report card (zip on the Desktop, GitHub issue) |
| `file-picker` | The launcher's own file picker, as on a Steam Deck (/home/deck) |
| `deck-home` | Home at 1280x800 |
