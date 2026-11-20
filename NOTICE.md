# NOTICE

mkwmp is free software under the **GNU General Public License, version 3 or later** (`LICENSE`),
the same as ForzaInfinity, whose voice chat it reuses. Copyright (C) 2026 Alexandre Moraes and
contributors.

Every release zip ships this file and `LICENSE` next to the binaries.

## Code from ForzaInfinity

`native/voice/src/` (`voice_audio.cpp`, `voice_audio*.h`, `voice_echo.*`, `voice_spatial.h`,
`voice_volume.h`) is ForzaInfinity's voice engine (`apps/infinity-horizon/native/overlay/src/`),
by the same author, GPL-3.0. The changes are: the log goes to the probe instead of the overlay,
there are 24 seats, and the mix has a per-talker leveler and a limiter (`voice_loudness.h`).
`voice_wire.h`, `mkwmp_voice.*` and `voice_loudness.h` are mkwmp's own code.

## Third-party components

| Component | Where | License |
| --- | --- | --- |
| libopus 1.5.2 (Xiph.Org, Skype, Microsoft, Amazon and others) | compiled into `mkwmp-voice.dll` / `libmkwmp-voice.so` | BSD-3-Clause |
| RNNoise 0.2 (Xiph.Org, Mozilla, Jean-Marc Valin) | compiled into `mkwmp-voice.dll` / `libmkwmp-voice.so` | BSD-3-Clause |
| WebRTC AudioProcessing 2.1, AEC3 (The WebRTC project authors; freedesktop's standalone release) | compiled into `mkwmp-voice.dll` / `libmkwmp-voice.so` | BSD-3-Clause, plus Google's additional IP rights grant (patents) |
| Abseil 20240722.0 (headers only) | compiled into `mkwmp-voice.dll` / `libmkwmp-voice.so` | Apache-2.0 |
| Press Start 2P font (The Press Start 2P Project Authors) | embedded in the launcher | SIL Open Font License 1.1 (`rust/client/assets/PressStart2P-OFL.txt`) |
| Rust crates (eframe/egui, tokio, axum, reqwest, rustls, ed25519-dalek, serde and others) | the launcher and the server | MIT and/or Apache-2.0 (each crate's own license; `cargo about` / `Cargo.lock` lists them) |

The third-party sources are fetched by `native/voice/fetch.ps1` (or `fetch.py`) from their official release
tarballs, pinned by SHA-256. Each tarball's own `COPYING`/`LICENSE` carries the full license text.
The release zip includes those texts under `licenses/`.

## mkw-recomp

Built on mkw-recomp by Adrià (github.com/adriadam10/mkw-recomp), MIT.

The MKWMP launcher downloads and installs mkw-recomp 0.1.15-mkwmp
(https://github.com/anarqz/mkw-recomp, tag v0.1.15-mkwmp), MKWMP's fork of mkw-recomp by Adrià
(https://github.com/adriadam10/mkw-recomp), and runs its game setup. mkw-recomp is under the MIT
License; its notice is below, is installed as `licenses/mkw-recomp-LICENSE` with the notices of
the libraries it ships, and is shown in the launcher under About > Licenses. The fork adds entries
to the plugin API and changes nothing else of the game's behaviour. mkw-recomp contains no game
code or assets: it builds the game on the player's device from the player's own copy. MKWMP is not
affiliated with or endorsed by mkw-recomp's author or by Nintendo.

```
MIT License

Copyright (c) 2026 Adrià

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Mario Kart World is Nintendo's. mkwmp ships none of the game's code or assets, and it is not
affiliated with or endorsed by Nintendo.
