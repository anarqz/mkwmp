# Interoperability and legal notes

MKWMP is an unofficial, community-made online service for the mkw-recomp PC runtime of Mario Kart World. It is
not affiliated with, endorsed by or sponsored by Nintendo. Mario Kart World, Nintendo, Nintendo Switch 2 and related
names are Nintendo's trademarks; MKWMP uses them only to say which game it works with.

This page describes what the project is and is not. It is not legal advice.

## What MKWMP contains

- **Its own code only.** The launcher, the server, the game patch (`winmm.dll`), the mkw-recomp mod
  (`mkwmp-mod.zip`) and the voice and camera libraries (`mkwmp-voice`, `mkwmp-camera`) are written by MKWMP contributors and published under GPL-3.0-or-later. The third-party
  components (all open source) are listed in [NOTICE.md](../NOTICE.md).
- **No game content.** The current source tree and the release downloads contain no game code, decompiled code,
  game assets, art, music, fonts, game files, encryption keys or firmware.
- **Specifications, not copies.** The `.proto` files in `contracts/` describe the messages the game client sends
  and accepts, so that an independent server can interoperate with it. They are written by MKWMP.

## What you need, and what MKWMP doesn't do

- You need your **own legally obtained copy** of Mario Kart World for Nintendo Switch 2, already set up with mkw-recomp, which is a separate
  project.
- MKWMP does **not** obtain, dump, decrypt or distribute games, and it doesn't explain how to. It doesn't provide
  or ask for keys or firmware.
- The game patch **adds its own files** next to the game and doesn't modify the game's own files. SETTINGS → GAME
  PATCH → UNINSTALL removes them (a `winmm.dll` that was there before is kept as a backup and put back).

## Where it connects

- The game patch sends the game's online connections to the **MKWMP server the player picked** (the official one,
  or one they or a friend host). MKWMP's components do not connect to Nintendo's servers.
- The game still asks for its usual server names. The patch resolves them to the MKWMP server, and that server's
  own certificate, which only the patched game trusts, answers for those names.
- The launcher talks to that MKWMP server and to GitHub (signed updates and the project's star count).

## How it was made

The online layer was reimplemented to interoperate with the game client, using two methods: analysis of the
game program running under mkw-recomp, to find the message formats and the functions the patch hooks, and
observation of what the client sends to a local test server and which replies it accepts. The research notes are
kept out of the source tree.

## Out of scope

These are refused as issues and pull requests:

- anything that helps obtain, copy or decrypt games, keys or firmware, or links to pirated copies;
- unlocking paid or not-yet-released content, or getting around purchases or entitlements;
- connecting to Nintendo's own online services, or using Nintendo accounts or credentials;
- cheats that affect other players.

## Rights holders

If you represent a rights holder and have a concern, please open an issue or contact the maintainer through the
GitHub profile of the repository owner. We'll respond and work with you in good faith.
