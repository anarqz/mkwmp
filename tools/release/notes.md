**Developer preview.** Online play works end to end between players on the official server; some parts are still being tested (marked below).

What works:

- **Gamertags.** Register a gamertag in the launcher and get a 6-digit PIN; use the PIN to play with the same gamertag on another device. Unused gamertags expire after 6 months.
- **Matchmaking with any number of lobbies.** Races and battles open new lobbies as players arrive and close them when empty. Full matches, from the vote to the results and back to the lobby, were verified on the previous lobby server; on the new server they are *being tested*.
- **Friends, kept on the server.** Add friends by gamertag; they accept or decline. The launcher shows each friend as playing, online or offline, and they are your friends in the game too.
- **Friend rooms** with a Room ID and password: *being tested*.
- **Background mode.** Closing the launcher keeps it in the system tray, with notifications when a friend comes online, starts playing, or sends you a request. Having the launcher open doesn't take a seat on the server.

What's in this release (client only):

- `mkwmp-launcher-x86_64-windows.exe` / `mkwmp-launcher-x86_64-linux` (SteamOS): your gamertag, friends and settings; takes a seat on the server (or queues) while you play; updates itself and the game patch.
- `mkwmp-probe-winmm.dll`: the game patch. The launcher installs it as `winmm.dll` in your game folder; you don't need to download it by hand.
- `SHA256SUMS` + `SHA256SUMS.sig`: checksums signed with the release key; the launcher verifies them before installing anything.

Known limits in this release:

- Launchers from before this release can't sign in: update, then register your gamertag.
- After pressing PLAY, your seat is held until you close the game or press STOP, even if you leave Online Play.
- After the launcher updates itself, the previous version is kept as `*.old` until the next start, and then deleted.

Setup guides: https://anarqz.github.io/mkwmp/
