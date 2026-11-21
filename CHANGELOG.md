# MKWMP changelog

What changed in each MKWMP release, newest first. The launcher shows this file on its What's New page.

## 1.4.1 — 2026-11-21

- **No more "location in use".** Windows no longer shows the location icon for the launcher. The Wi-Fi bars no longer read the network's name; they use Windows' signal bars instead.
- **"Show closest to me".** A new switch over the server list, in Settings › Online and in the first setup. On, the launcher measures the delay to each listed server while the list is on screen and shows the nearest first. Off, no server is measured and the list keeps the directory's order. Your location is never used.
- **Microphone and camera only in friend rooms.** The game opens your microphone and camera only in a friend room with voice or camera on, and closes them when you leave. Before, the camera could open for the game's "Use the camera?" question in the menus. The launcher's microphone and camera tests stop when you leave the page or switch to another window.
- **The server line counts players.** Home now shows the players on the server out of its seats, "ONLINE · 0/50 PLAYERS", instead of the free seats.

## 1.4.0 — 2026-11-21

**A new launcher.** MKWMP 1.4.0 replaces the launcher with a full-screen app made to be used from the couch, and it sets the game up for you.

- **Full-screen, console-style launcher.** Home, Friends and Settings are made for a TV: play it with a controller (D-pad, A, B, the shoulder buttons), a keyboard or the mouse. It can also run in a window.
- **Offline first.** The launcher no longer needs a server to open: play offline right away, and connect to a server only when you want to play online.
- **A welcome for the first start.** A short first setup picks your language, your name and the game, and can connect you to a server.
- **It installs mkw-recomp for you.** The launcher downloads and sets up MKWMP's build of mkw-recomp (0.1.16-mkwmp) from your own copy of the game, installs the MKWMP mod into it, and keeps both up to date.
- **An in-game overlay.** Press the HOME (Guide) button during a race to open the launcher over the game, like a console's HOME menu: the game pauses, and you can go back to it or quit it from there.
- **Server list.** Pick a community server from a list with its region, players and latency, or type an address. Hide the servers you don't want to see.
- **Online profiles, with consent kept by the server.** Your gamertag and PIN live on the server you choose. That server shows its own privacy policy inside the launcher and keeps your 18+ consent with your profile.
- **Friends.** A friends panel you can open from anywhere: who's online or racing, and your friend requests.
- **Voice and camera settings.** Choose and test your microphone, speakers and camera in Settings › Voice & Camera.
- **15 languages.** English, Deutsch, Español, Français, Italiano, Nederlands, Polski, Português (Brasil), Português (Portugal), Русский, Türkçe, 日本語, 한국어, 简体中文 and 繁體中文.
- **Automatic updates.** The launcher downloads its new versions in the background, checks their signature, and installs them when you say so.
- **Crash reports.** If the game crashes, the launcher says so and helps you report it: Report saves the details and the logs as a zip on your Desktop and opens a GitHub issue. Nothing is sent by itself.
- **Use another copy of the game.** Settings › Game can switch the game to another copy of yours (a .nup or .nx file, or a folder), read where it is or copied in: only what depends on it is set up again, usually in seconds.
- **Game settings.** Change mkw-recomp's screen, graphics, sound and controls settings from the launcher, in Settings › Game.
- **Your game's real cover.** Home shows the cover art of your own copy of the game.
- **Steam Deck and Proton.** The launcher runs on the Steam Deck and other Linux PCs through Proton, in Game Mode and Desktop Mode.
- **Coming from 1.3: download 1.4.0 by hand.** The 1.3 launcher does not update itself to this one. On Windows, download `mkwmp-1.4.0-windows-x64.zip` and unzip it into the same place as your old one, so its `mkwmp` folder goes over the old folder: your settings (name, PIN, server, game folder) stay. Then delete the old `mkwmp-launcher.exe` and open `mkwmp.exe`. On SteamOS and Linux, run `curl -fsSL https://anarqz.github.io/mkwmp/install.sh | bash` once in Desktop Mode: it installs the new launcher under Proton and brings your name and PIN over from the old one. The old Linux launcher keeps working, but it gets no more updates.
- **New file names.** The launcher is `mkwmp.exe` and its update helper `updater.exe`; the server is `server` (Linux) and `server.exe` (Windows), in `mkwmp-server-1.4.0.zip`. A release is now two zips and their signed checksums, nothing else.

## 1.3.1 — 2026-11-21

- mkw-recomp 0.1.16-mkwmp.1 recommended (its -noavx2 build accepted); official 0.1.16 and 0.1.16-mkwmp supported; the mod runs on 0.1.15+.
- The Rust launcher passes the server's room (lobby, ussid, ussids, at) to the game for crash records.
- Server: GET /privacy and recorded consent ([privacy], enforce off by default), the beholder server directory (`mkwmp beholder`, [announce]), GET /ping, and the lobby's per-seat rooms and `room order` log.

## 1.3.0 — 2026-11-19

Camera and voice chat in friend rooms, the MKWMP mod for mkw-recomp, and mkw-recomp 0.1.15.

- **Voice chat now works only in friend rooms** (ONLINE → FRIENDS), for everyone in the room. Matchmade lobbies have no voice chat any more. This is a server change, so it applies to every launcher version, 1.2.0 included.
- **Camera in friend rooms.** With the MKWMP mod on game 1.8.0, turn your camera on and the mutual friends in your room (each of you on the other's friend list) see you above your kart, up to 4 faces each. It's off by default: turn it on under Mods → MKWMP → Options (or the in-game settings, Camera tab), then answer yes when the game asks at Online Play. Pictures are relayed live and never recorded. Muting a player hides their camera and stops yours going to them. Report camera abuse from the same Camera settings. On Linux (the mod's native build) the camera can only send a test pattern for now: webcam capture is Windows-only.
- **The MKWMP mod.** `mkwmp-mod.zip` is MKWMP as a mod for mkw-recomp 0.1.15 and 0.1.15-mkwmp: unzip it into the `mods/` folder of mkw-recomp's data folder (you get `mods/mkwmp/`), then on the start screen open Mods, tick MKWMP, and in its Options confirm you're 18+ and sign in or register. When the mod is enabled, the launcher takes its own game patch (`winmm.dll`) out of the game folder and says "MKWMP runs as a mod in this game": the mod wins. You can keep using the launcher for your gamertag, friends and PLAY.
- **mkw-recomp [0.1.15-mkwmp](https://github.com/anarqz/mkw-recomp/releases/tag/v0.1.15-mkwmp)** is the recommended version: MKWMP's build of mkw-recomp 0.1.15, which the camera needs. The official **0.1.15** is supported too, as well as 0.1.10, 0.1.12 and 0.1.13 (without the mod or the camera). Coming from the official 0.1.15, or moving to it, open mkw-recomp once and let it finish its setup: the launcher tells you when that's needed.
- Players on 1.2.0 and 1.3.0 play and talk together in friend rooms. Only players with the mod see cameras.
- The privacy policy now covers the camera.

For people hosting a server: update it to turn matchmade voice off and relay friend-room cameras (`[lobby] video = false` turns cameras off). `mkwmp admin reports` shows whether a report is about voice or camera.

## 1.2.0 — 2026-11-19

Privacy, 18+, voice-chat reports. No game change.

- **For adults (18+).** The launcher asks you to confirm you're 18 or older and accept the privacy policy before it registers a gamertag, signs in or plays. Until you do, it doesn't contact the server. The box stays in SETTINGS > PRIVACY.
- **Report voice-chat abuse.** SETTINGS > REPORT VOICE ABUSE (or REPORT next to a player you muted): pick what happened, add a few words, send. The server's host reviews it. You can have one report waiting at a time, and withdraw it while it waits.
- **DELETE MY ACCOUNT** in SETTINGS > PRIVACY deletes your gamertag, PIN, friends and the reports you filed from the server.
- **Unused gamertags are deleted after a month** (was 6 months), with their friends. If yours was and the name is still free, the launcher registers it again with your PIN the next time you open it.
- Ctrl+V pastes into the launcher's text fields (gamertag, PIN, server, friends). Thanks, Fraustiz (#12).
- The DONATE button is gone.

For people hosting a server: `mkwmp admin reports`, `close-report` and `delete-report` review voice-chat reports (see HOSTING in the repository). Update your server before your players update their launchers.

## 1.1.1 — 2026-11-18

Launcher fix. No game or lobby change.

- If another MKWMP launcher is still running (for example the old one right after an update), the new launcher waits for it to close instead of running beside it. Until the old one is gone, PLAY says "Another MKWMP launcher is running: close it". Before this fix, Online Play could bounce back to the menu until you restarted.

## 1.1.0 — 2026-11-18

Game 1.8.0 support, and mkw-recomp 0.1.13 (recommended).

- **Game 1.8.0.** MKWMP now plays game version 1.8.0 as well as 1.0.0. Players only meet others on the same game version: matchmaking, friend rooms and Room IDs keep 1.0.0 and 1.8.0 apart, because the two versions can't play together. To move to 1.8.0, apply the 1.8.0 update to your own game in mkw-recomp; MKWMP doesn't include any game data. The new 1.8.0 modes Clutch 24 and Clutch Survival 24 aren't available yet.
- **mkw-recomp 0.1.13** is the recommended version. 0.1.10 and 0.1.12 still work.
- The launcher refuses to start a game version MKWMP doesn't support, instead of letting the game patch run on it.
- SAVE LOGS FOR A BUG REPORT also collects `mkwmp-game.log`, which the game writes when mkw-recomp's start screen is open.

## 1.0.2 — 2026-11-17

mkw-recomp 0.1.12 support (new).

- **mkw-recomp 0.1.10 or 0.1.12.** The launcher now also accepts mkw-recomp 0.1.12. Support for it is new: online matches between two players on 0.1.12 work, but a match between 0.1.12 and 0.1.10 players hasn't been tested yet, so play with friends on the same version. If a match goes out of sync, tell us in an issue. 0.1.11 isn't supported.
- If you use mkw-recomp 0.1.12's "choose game folder", SAVE LOGS FOR A BUG REPORT finds the game's log in that folder.

## 1.0.1 — 2026-11-15

The launcher now checks your mkw-recomp version.

- **mkw-recomp 0.1.10 only.** The game patch is built for mkw-recomp 0.1.10, and newer releases (0.1.11, 0.1.12) change what it hooks. If the game folder has another version, the line under GAME FOLDER says so, PLAY stays off, and **GET 0.1.10** opens its download page. Support for 0.1.12 is being worked on.
- The game patch check's per-file lines say "verified" (1.0.0 showed empty boxes there), and the launcher's log records the check's result and the mkw-recomp version.
- The game patch and voice chat are the same as in 1.0.0.

## 1.0.0 — 2026-11-15

Online races, battles and friend rooms for Mario Kart World on community servers, with lobby voice chat.

- **Matchmade races and battles**, start to finish, with any number of lobbies: they open as players arrive and close when they empty. After a match you go back into your lobby.
- **Friend rooms.** Create a room and have friends join it from their friend card, or by its Room ID with the on-screen keypad (SEARCH GROUP ID).
- **Voice chat** with the players in your lobby, with echo cancelling and noise suppression. Pick your microphone and output in the launcher's SETTINGS. Mute players from their menu in the game's lobby.
- **Gamertags and friends.** Register a gamertag and get a 6-digit PIN, and use the PIN to play with the same gamertag on another device. Add friends by gamertag. The launcher shows each friend as playing, online or offline, and notifies you when they come online.
- **Game patch check.** The line under GAME FOLDER says whether the game patch is installed and is this version, checked by file content against signed checksums. PLAY waits until it is. SETTINGS > GAME PATCH has INSTALL and UNINSTALL. UNINSTALL removes every mkwmp file from the game folder.
- **Bug reports.** SETTINGS > SAVE LOGS FOR A BUG REPORT collects the launcher's and the game's logs, with no PIN or ticket in them, into a zip for a GitHub issue.
- **Host your own server.** `mkwmp-server` is the whole server in one program, for Windows or Linux.

### Since rc.3

- The connection error right before each race is fixed.
- Friends' rooms show on the friend card, even when your friend came online after you.
- You go back into your lobby after a match.
- The Room ID field in the launcher is gone; type the Room ID in the game.
- The Steam Deck runs smoothly: the game patch logs much less.

## 0.0.1-rc.3 — 2026-11-14

Developer preview. Online play works end to end on the official server.

- **Reserved gamertags can sign in.** Registering a reserved gamertag now asks for its PIN, the same as a gamertag that is already taken. In rc.2 it only said the name was reserved.
- **`launch_command`** in `client.toml`: an optional command the launcher runs to start the game (for example through Proton when the launcher is your Steam shortcut).
- The game patch is the same as in rc.2 and is not reinstalled.

### Known limits

- After pressing PLAY, your seat is held until you close the game or press STOP, even if you leave Online Play.
- Self-hosted servers have only been tested without the real game (sign-in, matchmaking, relay); the first real-game runs are coming.
- Over plain `http://`, PINs cross the network unencrypted. That's fine on a LAN; a public server should use HTTPS.

## 0.0.1-rc.2 — 2026-11-14

Developer preview. Online play works end to end on the official server.

- **Host your own server.** `mkwmp-server` is the whole server in one program, for Windows or Linux. Run it (on Windows, double-click it) and answer one question: the address your friends reach you at. It creates its keys, certificate, config and gamertag database by itself, then runs. Players set the launcher's SERVER to `http://<that address>:7000`. For a Linux server, Docker Compose is in the repository.
- **Each server's own certificate.** The launcher fetches the certificate of the server you play on, and the game patch trusts it. That's what lets your game join a self-hosted server. On the official server nothing changes.
- **Launcher:** the settings dialog no longer shows a star count of 0.

## 0.0.1-rc.1 — 2026-11-14

Developer preview, the first one. Online play works end to end between players on the official server; some parts are still being tested.

- **Gamertags.** Register a gamertag in the launcher and get a 6-digit PIN; use the PIN to play with the same gamertag on another device. Unused gamertags expire after 6 months.
- **Matchmaking with any number of lobbies.** Races and battles open new lobbies as players arrive and close them when empty. Full matches, from the vote to the results and back to the lobby, were verified on the previous lobby server; on the new server they are *being tested*.
- **Friends, kept on the server.** Add friends by gamertag; they accept or decline. The launcher shows each friend as playing, online or offline, and they are your friends in the game too.
- **Friend rooms** with a Room ID and password: *being tested*.
- **Background mode.** Closing the launcher keeps it in the system tray, with notifications when a friend comes online, starts playing, or sends you a request. Having the launcher open doesn't take a seat on the server.

### Known limits

- Launchers from before this release can't sign in: update, then register your gamertag.
- After pressing PLAY, your seat is held until you close the game or press STOP, even if you leave Online Play.
- After the launcher updates itself, the previous version is kept as `*.old` until the next start, and then deleted.
