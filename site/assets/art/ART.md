# MKWMP art for the website

Original art (made for MKWMP; no Nintendo IP). Made from the source pictures by `launcher/tools/make_art.py --site`
(branch launcher-art). WebP at quality 85; PNG where a crawler or browser needs it. Sizes are on disk.

| File | Pixels | Size | Suggested use |
|---|---|---|---|
| `apple-touch-icon.png` | 180x180 | 49 KB | `<link rel="apple-touch-icon">` 180x180, opaque (iOS rounds it) |
| `banner-handheld-2x1.webp` | 1536x768 | 78 KB | section header: "Play on a handheld" (Steam Deck / handheld PCs), 2:1 |
| `banner-handheld-3x1.webp` | 1536x512 | 69 KB | section header: "Play on a handheld", 3:1 strip |
| `banner-mods-2x1.webp` | 1536x768 | 96 KB | section header: "Mods", 2:1 |
| `banner-mods-3x1.webp` | 1536x512 | 72 KB | section header: "Mods", 3:1 strip |
| `banner-server-2x1.webp` | 1536x768 | 120 KB | section header: "Host your own server", 2:1 |
| `banner-server-3x1.webp` | 1536x512 | 93 KB | section header: "Host your own server", 3:1 strip |
| `bg-dark.webp` | 1536x1024 | 26 KB | section background, dark theme (navy, neon road on the right); keep text on the left |
| `bg-light.webp` | 1536x1024 | 23 KB | section background, light theme (lavender, pastel road on the right) |
| `empty-crash.webp` | 512x512 | 47 KB | empty state: crash report (kart, gear and wrench); bug report / support |
| `empty-friends.webp` | 512x512 | 37 KB | empty state: no friends yet (two karts linked); friends feature |
| `empty-mods.webp` | 512x512 | 47 KB | empty state: no mods (open toolbox); mods section |
| `empty-offline.webp` | 512x512 | 47 KB | empty state: offline (cloud without Wi-Fi, unplugged kart); offline play / FAQ |
| `empty-servers.webp` | 512x512 | 38 KB | empty state: no servers found (satellite dish); server list / hosting |
| `favicon.ico` | 16, 32, 48 | 15 KB | favicon 16/32/48 from the flat glyph (blue on transparent) |
| `glyph-white.png` | 1024x1024 | 51 KB | the flat mark, white, 1024 px: on dark or photo backgrounds |
| `glyph.png` | 1024x1024 | 120 KB | the flat mark, blue, 1024 px |
| `glyph.svg` | vector | 8 KB | the flat single-colour mark as a vector (traced from the cleaned glyph): favicon.svg, nav, footer; `fill` is #1553FA |
| `hero-wide.webp` | 2560x827 | 123 KB | wide hero / page banner (the Steam hero art): sunset road with floating islands; text needs a scrim |
| `hero.webp` | 1536x1024 | 71 KB | home page hero: kart out of a glowing portal, open sky on the LEFT for the headline (light art; dark text) |
| `icon-192.png` | 192x192 | 58 KB | web app manifest icon 192 |
| `icon-512.png` | 512x512 | 301 KB | web app manifest icon 512 (full-colour app icon, rounded, transparent corners) |
| `logo.png` | 1280x289 | 63 KB | the MKWMP wordmark, transparent: PNG fallback (email, README, `<img>` without WebP) |
| `logo.webp` | 1280x289 | 39 KB | the MKWMP wordmark, transparent: page header / hero title |
| `og.png` | 1200x630 | 937 KB | Open Graph / Twitter card (`og:image`, `twitter:image`): PNG because social crawlers need PNG/JPEG |
| `og.webp` | 1200x630 | 90 KB | the same card for on-page use |
| `step-consent.webp` | 512x512 | 49 KB | welcome step 5, privacy and consent (shield and lock): privacy section |
| `step-done.webp` | 512x512 | 56 KB | welcome step 7, all set (chequered flag): call to action / download section |
| `step-game.webp` | 512x512 | 61 KB | welcome step 2, game setup (cartridge and gears) |
| `step-name.webp` | 512x512 | 44 KB | welcome step 1, your name (helmet): how-it-works list |
| `step-online.webp` | 512x512 | 55 KB | welcome step 3, play online (globe with karts) |
| `step-pin.webp` | 512x512 | 46 KB | welcome step 6, your PIN (PIN field) |
| `step-server.webp` | 512x512 | 40 KB | welcome step 4, choose a server (server racks) |

## Controller glyphs (`pad/`)

Button glyphs for instructions ("press A to start"). `<name>.png`: dark shape with white letter, for light
backgrounds; `<name>-light.png`: white shape with dark letter, for dark backgrounds. Trimmed, 256 px on the long side;
show them at text height (about 1.2em). Start and Select carry English words: prefer the menu (Start) glyph only
where English is fine.

| File | Pixels | Size | Suggested use |
|---|---|---|---|
| `pad/a-light.png` | 254x256 | 7 KB | dark backgrounds |
| `pad/a.png` | 254x256 | 5 KB | light backgrounds |
| `pad/b-light.png` | 254x256 | 7 KB | dark backgrounds |
| `pad/b.png` | 254x256 | 5 KB | light backgrounds |
| `pad/dpad-light.png` | 255x256 | 7 KB | dark backgrounds |
| `pad/dpad.png` | 255x256 | 5 KB | light backgrounds |
| `pad/lb-light.png` | 256x147 | 5 KB | dark backgrounds |
| `pad/lb.png` | 256x147 | 4 KB | light backgrounds |
| `pad/rb-light.png` | 256x149 | 5 KB | dark backgrounds |
| `pad/rb.png` | 256x149 | 3 KB | light backgrounds |
| `pad/select-light.png` | 256x139 | 6 KB | dark backgrounds |
| `pad/select.png` | 256x139 | 4 KB | light backgrounds |
| `pad/start-light.png` | 256x140 | 5 KB | dark backgrounds |
| `pad/start.png` | 256x140 | 3 KB | light backgrounds |
| `pad/x-light.png` | 256x256 | 6 KB | dark backgrounds |
| `pad/x.png` | 256x256 | 5 KB | light backgrounds |
| `pad/y-light.png` | 254x256 | 6 KB | dark backgrounds |
| `pad/y.png` | 254x256 | 5 KB | light backgrounds |
