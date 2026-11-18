**mkwmp 1.1.1**: launcher fix. No game or lobby change.

- If another MKWMP launcher is still running (for example the old one right after an update), the new launcher waits for it to close instead of running beside it. Until the old one is gone, PLAY says "Another MKWMP launcher is running: close it". Before this fix, Online Play could bounce back to the menu until you restarted.

---

**From 1.1.0** (**mkwmp 1.1.0**: game 1.8.0 support, and mkw-recomp 0.1.13 (recommended).)

- **Game 1.8.0.** MKWMP now plays game version 1.8.0 as well as 1.0.0. Players only meet others on the same game version: matchmaking, friend rooms and Room IDs keep 1.0.0 and 1.8.0 apart, because the two versions can't play together. To move to 1.8.0, apply the 1.8.0 update to your own game in mkw-recomp; MKWMP doesn't include any game data. The new 1.8.0 modes Clutch 24 and Clutch Survival 24 aren't available yet.
- **mkw-recomp 0.1.13** is the recommended version. 0.1.10 and 0.1.12 still work.
- The launcher refuses to start a game version MKWMP doesn't support, instead of letting the game patch run on it.
- SAVE LOGS FOR A BUG REPORT also collects `mkwmp-game.log`, which the game writes when mkw-recomp's start screen is open.

---

**From 1.0.2** (**mkwmp 1.0.2**: mkw-recomp 0.1.12 support.)

- **mkw-recomp 0.1.10 or 0.1.12.** The launcher now also accepts mkw-recomp 0.1.12. Support for it is new: online matches between two players on 0.1.12 work, but a match between 0.1.12 and 0.1.10 players hasn't been tested yet, so play with friends on the same version. If a match goes out of sync, tell us in an issue. 0.1.11 isn't supported.
- If you use mkw-recomp 0.1.12's "choose game folder", SAVE LOGS FOR A BUG REPORT finds the game's log in that folder.

---

**From 1.0.0** (**mkwmp 1.0.0**: online races, battles and friend rooms for Mario Kart World on community servers, with lobby voice chat.)

Download one zip and unzip it anywhere:

- **Windows:** `mkwmp-1.1.1-windows-x64.zip`. Open `mkwmp/mkwmp-launcher.exe`.
- **Steam Deck / SteamOS / Linux:** `mkwmp-1.1.1-linux-x64.zip`. On a Steam Deck, one command in Desktop Mode installs it and adds it to Steam: `curl -fsSL https://anarqz.github.io/mkwmp/install.sh | bash`.

`README.txt` inside each zip has the steps.

What's in it:

- **Matchmade races and battles**, start to finish, with any number of lobbies: they open as players arrive and close when they empty. After a match you go back into your lobby.
- **Friend rooms.** Create a room and have friends join it from their friend card, or by its Room ID with the on-screen keypad (SEARCH GROUP ID).
- **Voice chat** with the players in your lobby, with echo cancelling and noise suppression. Pick your microphone and output in the launcher's SETTINGS. Mute players from their menu in the game's lobby.
- **Gamertags and friends.** Register a gamertag and get a 6-digit PIN, and use the PIN to play with the same gamertag on another device. Add friends by gamertag. The launcher shows each friend as playing, online or offline, and notifies you when they come online.
- **Game patch check.** The line under GAME FOLDER says whether the game patch is installed and is this version, checked by file content against signed checksums. PLAY waits until it is. SETTINGS > GAME PATCH has INSTALL and UNINSTALL. UNINSTALL removes every mkwmp file from the game folder.
- **Bug reports.** SETTINGS > SAVE LOGS FOR A BUG REPORT collects the launcher's and the game's logs, with no PIN or ticket in them, into a zip for a GitHub issue.
- **Host your own server.** `mkwmp-server` is the whole server in one program, for Windows or Linux ([README](https://github.com/anarqz/mkwmp#host-your-own-server)).

Since rc.3:

- The connection error right before each race is fixed.
- Friends' rooms show on the friend card, even when your friend came online after you.
- You go back into your lobby after a match.
- The Room ID field in the launcher is gone; type the Room ID in the game.
- The Steam Deck runs smoothly: the game patch logs much less.

Files:

- `mkwmp-1.1.1-windows-x64.zip` / `mkwmp-1.1.1-linux-x64.zip`: the launcher, the game patch, licenses and a README.
- `mkwmp-launcher-x86_64-*`, `winmm.dll`, `mkwmp-voice.dll`: the same files loose, for launchers that update themselves.
- `mkwmp-server-x86_64-windows.exe` / `mkwmp-server-x86_64-linux`: the server, for hosting your own (`mkwmp-server host --help`).
- `SHA256SUMS` + `SHA256SUMS.sig`: checksums signed with the release key. The launcher verifies them before installing anything.

mkwmp is free software under the GNU GPL, version 3 or later. Setup guides: https://anarqz.github.io/mkwmp/
