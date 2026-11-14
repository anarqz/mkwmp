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
| libopus 1.5.2 (Xiph.Org, Skype, Microsoft, Amazon and others) | compiled into `mkwmp-voice.dll` | BSD-3-Clause |
| RNNoise 0.2 (Xiph.Org, Mozilla, Jean-Marc Valin) | compiled into `mkwmp-voice.dll` | BSD-3-Clause |
| WebRTC AudioProcessing 2.1, AEC3 (The WebRTC project authors; freedesktop's standalone release) | compiled into `mkwmp-voice.dll` | BSD-3-Clause, plus Google's additional IP rights grant (patents) |
| Abseil 20240722.0 (headers only) | compiled into `mkwmp-voice.dll` | Apache-2.0 |
| Press Start 2P font (The Press Start 2P Project Authors) | embedded in the launcher | SIL Open Font License 1.1 (`rust/client/assets/PressStart2P-OFL.txt`) |
| Rust crates (eframe/egui, tokio, axum, reqwest, rustls, ed25519-dalek, serde and others) | the launcher and the server | MIT and/or Apache-2.0 (each crate's own license; `cargo about` / `Cargo.lock` lists them) |

The third-party sources are fetched by `native/voice/fetch.ps1` from their official release
tarballs, pinned by SHA-256. Each tarball's own `COPYING`/`LICENSE` carries the full license text.
The release zip includes those texts under `licenses/`.

Mario Kart World is Nintendo's. mkwmp ships none of the game's code or assets, and it is not
affiliated with or endorsed by Nintendo.
