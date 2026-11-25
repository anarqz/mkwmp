**mkwmp 1.4.8**: mkw-recomp's start screen is yours again.

The launcher installs this update by itself from 1.4.4. From 1.4.3, accept the update when it offers it.

- **mkw-recomp's start screen is no longer locked on Skip** (Settings › General). It is back to mkw-recomp's default, Automatic: starting mkw.exe yourself on a PC shows it again, and you can pick Always, Automatic or Skip. Play from the launcher still goes straight into the game.

---
**mkwmp 1.4.7**: mkw-recomp 0.1.18 is required, the launcher now updates an mkw-recomp you installed yourself, and Home is tidier.

The launcher installs this update by itself from 1.4.4. From 1.4.3, accept the update when it offers it.

- **mkw-recomp 0.1.18 is required.** MKWMP runs only on mkw-recomp 0.1.18. Play is refused on an earlier version, and the MKWMP mod turns itself off on one, also when the game is started outside the launcher.
- **Your own mkw-recomp is updated too, where it is.** If you picked an mkw-recomp you installed yourself (0.1.15, 0.1.16 or 0.1.17), the launcher now brings it to 0.1.18 in that same folder. Your game, its setup, saves, mods and settings stay, and the game code is not rebuilt. Before, such an install stayed on 0.1.17.
- **The update is not optional.** When mkw-recomp is not 0.1.18, the launcher opens the update card by itself and runs it; Play opens it too. It asks again whenever the version goes wrong, also when mkw-recomp's own updater moved it.
- **mkw-recomp's update check is on again** (Settings › General). The launcher turns it back on once; after that it is your choice.
- **Open mkw-recomp.** A new row in Settings › Game opens mkw-recomp's own start screen instead of the game: its settings, game files, mods and tools.
- **Home: Play, Mods, What's New.** The cards are Play, Mods and What's New; Friends and the checklist stay in the bar below, which no longer has Mods and Updates. A new MKWMP shows on the What's New card with a pink badge (New, the download's percentage, Ready or Failed) and a progress bar, and the card opens the update.

---
**mkwmp 1.4.6**: mkw-recomp 0.1.18, which fixes the game closing silently on some Windows 11 PCs, and DLSS, FSR 3.1 and HD vibration that download themselves.

The launcher installs this update by itself from 1.4.4. From 1.4.3, accept the update when it offers it. It then updates mkw-recomp 0.1.17 to 0.1.18 in place: the game code is not rebuilt.

- **mkw-recomp 0.1.18** by Adrià. It fixes the game closing silently on some Windows 11 PCs right after it starts (error 0xC0000409). Your game, its setup, saves, mods and settings stay. 0.1.17 still plays online.
- **DLSS, FSR 3.1 and HD vibration download themselves.** Picking NVIDIA DLSS, AMD FSR 3.1 or their frame generation in Settings › Graphics, or HD vibration in Settings › Controls, downloads the library it needs next to mkw.exe from its official source (NVIDIA, AMD or RenHDrum), checked against mkw-recomp's own list. It works at the game's next start. DLSS is downloaded only on a PC with an NVIDIA card. A library you already put there is kept.
- **New game settings from 0.1.18.** Settings › Controls: HD vibration (RenHDrum), the game's two-band vibration on player 1's DualSense, Pro Controller, Xbox One S or Steam Controller. Settings › Advanced: VRAM pressure eviction and Video memory log, the two settings mkw-recomp asks players to try if the game gets slower the longer they play (its issue #481). All three are off by default.

---

**mkwmp 1.4.5**: after a failed update you can still play, offline.

The launcher installs this update by itself from 1.4.4. From 1.4.3, accept the update when it offers it.

- **After a failed update, play offline.** If an update can't be downloaded or installed, the card offers Try Again, Continue Offline and Quit. Continue Offline lets you play offline until you close the launcher; online play waits for the update ("Update MKWMP to play online"), and Try the Update Again is on Home, in the friends panel and in Settings. The launcher asks again the next time it opens. Your server, PIN and choices are kept.

---

**mkwmp 1.4.4**: touch that works, smoother scrolling, tidier Online and Voice & Camera settings, and updates that install themselves.

The launcher updates itself from 1.4.x: accept the update when it offers it. From this version on you won't be asked again: a new version is downloaded and installed by itself.

- **Updates install themselves.** When a new MKWMP is out, the launcher downloads it at once and installs it before you play: Install and Restart, or it does it by itself after a few seconds. It never restarts under a running game (it installs when the game closes), and without a connection you play as before. If an install fails you can try again or carry on for now. "Update Automatically" is gone from Settings.
- **Touch: a drag scrolls, a tap taps.** Buttons and list entries act when you lift your finger, and only if you didn't drag: starting a drag on a button now scrolls the list instead of pressing it. The file picker's list can be dragged on a Steam Deck too.
- **Smoother, faster scrolling.** Holding a direction, spinning the wheel or using the right stick moves a list in one even motion instead of small jumps, and everything scrolls a bit faster.
- **Choose a Server in two columns.** The server list scrolls on its own at the left; the address, the server's answer and Connect are at the right. With a controller: A on a server, A again to go on.
- **Settings › Online, reorganized.** The server is one row with its status and Disconnect; right below it are Privacy Policy, Report Abuse and a new Connect to Another Server.
- **Report Abuse** (it was "Report Voice Abuse" under Voice & Camera) is now a page of its own under Settings › Online.
- **One privacy page.** The Privacy Policy row shows whether you accepted it (a mark and "Accepted" or "Not accepted") and opens the same page as the first setup: what the server keeps, its policy, and under them the switch to accept or withdraw.
- **Settings › Voice & Camera, reorganized.** Voice and camera each start with one switch ("Use voice in private rooms", "Use camera in private rooms") with the device and its test under it. "See Friends' Cameras" is part of the camera switch; "Show My Camera" stays for seeing friends without being seen; mirroring your picture is on the Test Camera page.
- **What a switch turns off is hidden**, not greyed out: with voice or camera off, their devices and tests are not shown.
- **Text boxes**: the grey hint is exactly where your typing starts.
- **Friends panel and file picker** no longer sit behind the bottom bar; each shows its own button hints.

---
**mkwmp 1.4.3**: the official mkw-recomp 0.1.17, new graphics options, and the fix for the crash when a 4th player joins.

The launcher updates itself from 1.4.x: accept the update when it offers it, then accept the mkw-recomp update and the quick Set Up Again (the game code is not rebuilt).

- **Fixed: the crash when a 4th player joins a room.** MKWMP's own pause-menu voice marks read the game's player list wrongly from the 4th player on; they are gone.
- **MKWMP no longer draws anything in the game**: no voice marks or mute keys in the pause menu, no pop-ups, no tabs in mkw-recomp's menu, no camera name tiles. Everything is set in the launcher. There is no muting for now (players muted before stay muted; unmute them in Settings › Voice & Camera).
- **mkw-recomp: the official 0.1.17** by Adrià, in place of MKWMP's build: what online play needed is now in it. Your game, saves, mods and settings stay.
- **New game settings from 0.1.17**: AI upscaling (NVIDIA DLSS on RTX cards or AMD FSR 3.1), upscaling quality, frame generation, NVIDIA Reflex, shadow map, and "first controller to player 2". All off by default. DLSS and FSR 3.1 need their libraries next to mkw.exe, which mkw-recomp does not ship.
- **Steam Deck and Linux**: the controller is found at start and NVIDIA cards no longer crash at loading under Proton (both handled by the launcher and the mod now); the overlay comes to the front on KDE Plasma.
- **Controls**: the launcher and the game agree on the button layout (Nintendo, Xbox, Automatic), and mkw-recomp's own menu shortcut never takes your overlay shortcut's buttons.

---

**mkwmp 1.4.2**: a Ready to Play checklist, your own overlay button, smoother scrolling and touch, and a tidier Mods list.

The launcher updates itself from 1.4.0 and 1.4.1: accept the update when it offers it. Coming from 1.3, follow the 1.4.0 instructions further below.

- **Ready to Play checklist.** Home's dock, Settings › Game, and Play when something is missing open a page listing what the game needs: mkw-recomp in the version MKWMP needs, your copy of the game, game version 1.8.0, the game set up on this device, MKWMP's files in the game, launcher updates and, online, server and sign-in. Each row has a button that fixes it. Play stays off until every row is ready.
- **Set the game up again.** Rebuild the game from your copy from Settings › Game or the checklist (saves, mods and settings stay). The launcher also notices when your copy changed since it was set up and asks you to set it up again.
- **Choose the button that opens MKWMP in the game.** Settings › Game › Overlay Shortcut: Guide or Select + Start (as before), Guide, Select + Start, hold Select, hold Start, L3 + R3, LB + RB + Start, Select + LB, Select + RB. Each choice says what it may clash with (Guide opens Steam's menu or Xbox Game Bar). On a Steam Deck, hold Select is a good pick.
- **Smoother, faster scrolling, and touch.** About a third faster per wheel step with a smooth glide, touchpads in small steps, the right stick scrolls; drag and flick lists with a finger.
- **Nintendo button layout in the launcher too**: with the game's Nintendo layout, the right button confirms.
- **Contrast**: the dark theme stands out more, and Settings › Appearance › Contrast sets it.
- **Mods list**: one compact line per mod with its size, a switch on each row, and Turn All Off / Turn All Back On.
- **Credits**: Settings › About links to mkw-recomp, its creator Adrià, its license and the source of MKWMP's build.
- **Server**: the crash some players saw about an hour into a session (often noticed when a 4th player joined) was fixed on the official server on 2026-11-21; servers you host get the same fix with this release's server.

---

**mkwmp 1.4.1**: privacy fixes for the new launcher and the mod.

The 1.4.0 launcher updates itself to 1.4.1: accept the update when it offers it. Coming from 1.3, follow the 1.4.0 instructions below.

- **No more "location in use".** Windows no longer shows the location icon for the launcher. The Wi-Fi bars in the top bar used to read the network's name, which Windows counts as location; they now use Windows' signal bars.
- **"Show closest to me"** over the server list (Settings › Online and the first setup). On, the launcher measures the delay to each listed server while the list is on screen and shows the nearest first. Off, no server is measured and the list keeps the directory's order. Your location is never used.
- **Microphone and camera only during friend-room races.** The mod opens your microphone and camera only in a friend room with voice or camera on, and closes them when you leave the room or the game. Before, the camera could open for the game's "Use the camera?" question in the menus. The launcher's own microphone and camera tests stop when you leave the page or switch to another window.
- **The server line counts players.** Home shows the players out of the seats, 0/50, not the free seats.

---

**From 1.4.0**

**mkwmp 1.4.0**: a new launcher. It sets the game up for you, plays offline first, and has a server browser, mods and an in-game overlay.

**Coming from 1.3: download this one by hand.** 1.3 can't update itself to the new launcher. Unzip `mkwmp-1.4.0-windows-x64.zip` into the same folder as your 1.3: it keeps your gamertag, PIN, server and choices. Then delete the old `mkwmp-launcher.exe`. On a Steam Deck, run `curl -fsSL https://anarqz.github.io/mkwmp/install.sh | bash` in Desktop Mode (Konsole) with Steam closed: it does all of this and keeps your 1.3 settings.

- **Game setup built in.** Point the launcher at your own copy of the game (1.8.0: a .nup, an .nx or a folder). It downloads mkw-recomp 0.1.16-mkwmp.1 (checked by its hash; the no-AVX2 build on CPUs that need it), builds the game (about 4 minutes of compiling, then shaders), installs the MKWMP mod and keeps everything next to itself (portable: nothing in AppData). Settings › Game can switch to another copy of yours later, usually in seconds.
- **Offline first.** Once it's set up, the game plays without an account. Online is one choice away.
- **Server browser.** Public servers list themselves in the server directory; the launcher shows them nearest first, with latency and free seats. Or type the address a host gave you.
- **Friends, voice and camera** in private friend rooms only, as in 1.3.
- **In-game overlay** (mkw-recomp 0.1.16-mkwmp with the mod): Guide (Home) or Select + Start pauses the game and opens the launcher over it: friends, settings, Quit game.
- **Mods:** browse and install from GameBanana, or from a .zip, .7z or .rar; switch mods on and off; mods with options get their own page. Plugin mods ask before they run code; mods that change the game's code stay on mkw-recomp's own Mods page. Every player's save is backed up before you play with a new set of mods.
- **Privacy on the server.** After you sign in, the launcher shows the server's own privacy policy, and the server records which version you accepted. You're asked again when it changes.
- **Made for a controller**, a keyboard or a mouse, in 15 languages, light or dark, full screen or in a window. The launcher (`mkwmp.exe`) updates itself with `updater.exe`.
- **Crash reports.** After a crash the launcher offers a report: a zip on your Desktop (no PIN or keys) and a prefilled GitHub issue.

**This release has two downloads:** `mkwmp-1.4.0-windows-x64.zip` (the launcher, also for the Steam Deck through Proton) and the server, `mkwmp-server-1.4.0.zip` (`server` for Linux, `server.exe` for Windows). `SHA256SUMS` and `SHA256SUMS.sig` sign both.

For people hosting a server:
- The program is now called `server` (`server.exe`): `server host`, `server seat` (the seat API, was `mkwmp server`; `server server` still works), `server lobby`, `server beholder`, `server admin`.
- **One command sets up a Vultr VM:** `curl -fsSL https://anarqz.github.io/mkwmp/deploy-mkwmp-server.sh | sudo bash` installs the signed server, the services, the firewall and your privacy policy, and lists the server in the directory. Re-run it to update. Guide: https://anarqz.github.io/mkwmp/host.html
- `[privacy] enforce = true` now makes sense: 1.4.0 launchers accept the policy on the server. Leave it off until your players have 1.4.0.

---

**From 1.3.1** (**mkwmp 1.3.1**: mkw-recomp 0.1.16, and the server side of what's coming next.)

- **mkw-recomp [0.1.16-mkwmp.1](https://github.com/anarqz/mkw-recomp/releases/tag/v0.1.16-mkwmp.1)** is the recommended version: MKWMP's build of mkw-recomp 0.1.16, with the camera. Its setup now also runs under Wine/Proton. If your CPU has no AVX2, take the `-noavx2` zip. The official **0.1.16** is supported too, as well as 0.1.15-mkwmp, 0.1.15, 0.1.13, 0.1.12 and 0.1.10. The MKWMP mod works with 0.1.15 and newer. Coming from 0.1.15-mkwmp, open mkw-recomp once and let it finish its setup: the launcher tells you when that's needed.
- The launcher passes the server's room details (which lobby, who's in it) to the game, so crash reports can say which room a crash happened in.

For people hosting a server:
- `GET /privacy` serves your privacy policy (`[privacy] file`; a generic default otherwise), and the server records which version each player accepted. The next launcher (1.4.0) asks players to accept it; `[privacy] enforce = true` then refuses seats to players who haven't. Leave it off while your players are on 1.3.x.
- `[announce]` lists your server in a server directory (a "beholder"), which the next launcher shows as a server browser. `mkwmp beholder` runs a directory yourself. See [HOSTING](https://github.com/anarqz/mkwmp/blob/main/docs/HOSTING.md).
- `GET /ping` for the launcher's latency check.
---

**From 1.3.0** (**mkwmp 1.3.0**: camera and voice chat in friend rooms, the MKWMP mod for mkw-recomp, and mkw-recomp 0.1.15.)

- **Voice chat now works only in friend rooms** (ONLINE → FRIENDS), for everyone in the room. Matchmade lobbies have no voice chat any more. This is a server change, so it applies to every launcher version, 1.2.0 included.
- **Camera in friend rooms.** With the MKWMP mod on game 1.8.0, turn your camera on and the mutual friends in your room (each of you on the other's friend list) see you above your kart, up to 4 faces each. It's off by default: turn it on under Mods → MKWMP → Options (or the in-game settings, Camera tab), then answer yes when the game asks at Online Play. Pictures are relayed live and never recorded. Muting a player hides their camera and stops yours going to them. Report camera abuse from the same Camera settings. On Linux (the mod's native build) the camera can only send a test pattern for now: webcam capture is Windows-only.
- **The MKWMP mod.** `mkwmp-mod.zip` is MKWMP as a mod for mkw-recomp 0.1.15 and 0.1.15-mkwmp: unzip it into the `mods/` folder of mkw-recomp's data folder (you get `mods/mkwmp/`), then on the start screen open Mods, tick MKWMP, and in its Options confirm you're 18+ and sign in or register. When the mod is enabled, the launcher takes its own game patch (`winmm.dll`) out of the game folder and says "MKWMP runs as a mod in this game": the mod wins. You can keep using the launcher for your gamertag, friends and PLAY.
- **mkw-recomp [0.1.15-mkwmp](https://github.com/anarqz/mkw-recomp/releases/tag/v0.1.15-mkwmp)** is the recommended version: MKWMP's build of mkw-recomp 0.1.15, which the camera needs. The official **0.1.15** is supported too, as well as 0.1.10, 0.1.12 and 0.1.13 (without the mod or the camera). Coming from the official 0.1.15, or moving to it, open mkw-recomp once and let it finish its setup: the launcher tells you when that's needed.
- Players on 1.2.0 and 1.3.0 play and talk together in friend rooms. Only players with the mod see cameras.
- The [privacy policy](https://anarqz.github.io/mkwmp/#privacy) now covers the camera.

For people hosting a server: update it to turn matchmade voice off and relay friend-room cameras (`[lobby] video = false` turns cameras off). `mkwmp admin reports` shows whether a report is about voice or camera.

---

**From 1.2.0** (**mkwmp 1.2.0**: privacy, 18+, voice-chat reports. No game change.)

- **For adults (18+).** The launcher asks you to confirm you're 18 or older and accept the [privacy policy](https://anarqz.github.io/mkwmp/#privacy) before it registers a gamertag, signs in or plays. Until you do, it doesn't contact the server. The box stays in SETTINGS > PRIVACY.
- **Report voice-chat abuse.** SETTINGS > REPORT VOICE ABUSE (or REPORT next to a player you muted): pick what happened, add a few words, send. The server's host reviews it. You can have one report waiting at a time, and withdraw it while it waits.
- **DELETE MY ACCOUNT** in SETTINGS > PRIVACY deletes your gamertag, PIN, friends and the reports you filed from the server.
- **Unused gamertags are deleted after a month** (was 6 months), with their friends. If yours was and the name is still free, the launcher registers it again with your PIN the next time you open it.
- Ctrl+V pastes into the launcher's text fields (gamertag, PIN, server, friends). Thanks, Fraustiz (#12).
- The DONATE button is gone.

For people hosting a server: `mkwmp admin reports`, `close-report` and `delete-report` review voice-chat reports ([HOSTING](https://github.com/anarqz/mkwmp/blob/main/docs/HOSTING.md)). Update your server before your players update their launchers.

---

**From 1.1.1** (**mkwmp 1.1.1**: launcher fix.)

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

- **Windows:** `mkwmp-1.3.0-windows-x64.zip`. Open `mkwmp/mkwmp-launcher.exe`.
- **Steam Deck / SteamOS / Linux:** `mkwmp-1.3.0-linux-x64.zip`. On a Steam Deck, one command in Desktop Mode installs it and adds it to Steam: `curl -fsSL https://anarqz.github.io/mkwmp/install.sh | bash`.

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

- `mkwmp-1.3.0-windows-x64.zip` / `mkwmp-1.3.0-linux-x64.zip`: the launcher, the game patch, licenses and a README.
- `mkwmp-launcher-x86_64-*`, `winmm.dll`, `mkwmp-voice.dll`: the same files loose, for launchers that update themselves.
- `mkwmp-mod.zip`: MKWMP as a mod for mkw-recomp 0.1.15 / 0.1.15-mkwmp (`mkwmp/`, for the game's `mods/` folder).
- `mkwmp-server-x86_64-windows.exe` / `mkwmp-server-x86_64-linux`: the server, for hosting your own (`mkwmp-server host --help`).
- `SHA256SUMS` + `SHA256SUMS.sig`: checksums signed with the release key. The launcher verifies them before installing anything.

mkwmp is free software under the GNU GPL, version 3 or later. Setup guides: https://anarqz.github.io/mkwmp/
