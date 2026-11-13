**Developer preview: matches do not finish yet.** Matchmaking, lobbies and the start of races and battles work between two players; the end of a match is still being built. Friends rooms, LAN Play and 3+ players are not available.

What's in this release (client only):

- `mkwmp-launcher-x86_64-windows.exe` / `mkwmp-launcher-x86_64-linux` (SteamOS): takes a seat on a server (or queues), keeps it while you play, updates itself and the game patch.
- `mkwmp-probe-winmm.dll`: the game patch. The launcher installs it as `winmm.dll` in your game folder; you don't need to download it by hand.
- `SHA256SUMS` + `SHA256SUMS.sig`: checksums signed with the release key; the launcher verifies them before installing anything.

Known limits in this release:

- Leaving Online Play doesn't free your seat until you close the game (planned for rc.2).
- After the launcher updates itself, the previous version is kept as `*.old` until the next start, and then deleted.
- The official server's lobby isn't open yet. The launcher can take a seat, but there is no match to join until the lobby goes live.

Setup guides: https://anarqz.github.io/mkwmp/
