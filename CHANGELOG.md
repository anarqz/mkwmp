# MKWMP changelog

What changed in each MKWMP release, newest first. The launcher shows this file on its What's New page.

## 1.5.3 — 2026-11-27

- **Nextendo Network support is still experimental in MKWMP.**
- **Players in lobbies on the Play card.** Home shows "In lobbies: N", the players racing right now on Nextendo (or seated on your MKWMP server).
- **Controllers in the overlay.** A Switch Pro Controller, Joy-Con or DualSense now drives the launcher over a running game too; the Home button goes back to the game, and the button hints show your controller's own buttons.
- **Debug mode** (Settings › Support). Records a detailed trace of the next games you start, for bug reports; PLAY asks each time whether to keep it on.

## 1.5.3-rc.1 — 2026-11-27

- **Test version, Alpha channel only.** Nextendo Network support is still experimental in MKWMP.
- **Players in lobbies on the Play card.** Home shows "In lobbies: N", the players racing right now on Nextendo (or seated on your MKWMP server).
- **Controllers in the overlay.** A Switch Pro Controller, Joy-Con or DualSense now drives the launcher over a running game too; the Home button goes back to the game like Xbox's Guide, and the button hints show your controller's own buttons (A/B/X/Y and +/− on Switch controllers, the shapes on PlayStation's).
- **Debug mode** (Settings › Support). Records a detailed trace of the next games you start, for bug reports; PLAY asks each time whether to keep it on.

## 1.5.2 — 2026-11-27

- **Nextendo Network support is still experimental in MKWMP.**
- **Real friend names inside the game on Nextendo**, instead of "Friend 1", "Friend 2"…
- **Nextendo profile pictures** for your friends and yourself in the friends panel, on Home and on your account card.
- **More controllers in the launcher.** A Switch Pro Controller, Joy-Con or DualSense that Windows does not show as an Xbox controller now works in the launcher too; the Checklist names the controller it found.

## 1.5.2-rc.1 — 2026-11-27

- **Test version, Alpha channel only.** Nextendo Network support is still experimental in MKWMP.
- **Real friend names inside the game on Nextendo.** Your Nextendo friends now show with their names in the game's friend list instead of "Friend 1", "Friend 2"…
- **Nextendo profile pictures.** Your friends' pictures and your own replace the letter circles in the friends panel, on Home and on your account card. Without a picture, the circle keeps the colour chosen on Nextendo.

## 1.5.1 — 2026-11-27

- **Nextendo Network support is still experimental in MKWMP.** Races work; friends inside the game, groups and ghosts are new and not fully tested yet. MKWMP servers remain available in Settings › Online.
- **Nextendo friends in the friends panel.** Your friend code with Copy, add a friend by their code, accept or decline requests, see who is online or playing, favorite, remove or block a friend, and add "Players you met" in recent races. Blocked players are in Settings › Nextendo.
- **Friend notifications on Nextendo.** "Is online", "is playing", "sent you a friend request", "accepted your request". Opening the launcher shows one summary instead of one per friend.
- **Your presence on Nextendo.** Friends see you online while the launcher is open and playing while the game runs (shown as an unofficial app). Settings › Nextendo › "Show Me Online to Friends" turns it off.
- **Friends inside the game on Nextendo.** Your Nextendo friends now appear in the game's friend list (as "Friend 1", "Friend 2"… for now), so their status, rooms and groups can work.
- **Ghosts on Nextendo.** Downloading and uploading time trial ghosts now has what it needs on PC.
- **Update channel.** Settings › Updates › Update Channel: Stable (finished versions) or Alpha (also test versions before they are released). Leaving Alpha offers "Return to Stable".

## 1.5.0 — 2026-11-27

- **Play online on Nextendo Network.** Sign in with your Nextendo account (or create one) and race online on Nextendo Network. You sign in on nextendo.network in your browser: your password never goes through MKWMP, and your Nextendo sign-in stays on this device, never sent to MKWMP servers.
- **Nextendo first.** The welcome now starts with "Play online with Nextendo Network": Sign in with Nextendo, Create a Nextendo Account, or Skip for Now. Home shows your Nextendo account, or a "Sign in with Nextendo to play online" button.
- **MKWMP servers are still there.** Settings › Online › "Use MKWMP Servers Instead" brings back the server list, seats, friends, voice and camera. If you already play on an MKWMP server, you stay on it, and Home invites you to try Nextendo.
- **On Nextendo**, voice, camera and MKWMP friends are not available: they are MKWMP-server features.

## 1.4.9 — 2026-11-25

- **Mods page, more of it is your mods.** Get Mods, From Link and From Zip share one line at the top, so the list starts right under them. "Installed" has chips to show All, On, Off, or the mods with an Update.
- **Update and Options on each mod's line.** A mod with a newer file on GameBanana has an Update button right on its line; a mod with options has an Options button. Turn All Off is Y in the bar at the bottom.
- **A mod opens beside the list.** Its switch, options and details slide in from the right over the list, which stays where you were; Y uninstalls it, B closes it. A mod you just installed opens there too.

## 1.4.8 — 2026-11-25

- **mkw-recomp's start screen is yours again.** Settings › General › "mkw-recomp start screen" is no longer locked on Skip. It is back to mkw-recomp's own default, Automatic, which shows the screen when you start mkw.exe yourself on a PC, and you can pick Always, Automatic or Skip. The launcher turns it back once (older launchers had kept it on Skip); after that it is your choice. Play from the launcher still goes straight into the game.
- **Home: four cards.** Play, Get Mods (the mod store, straight to GameBanana), Checklist (ready to play, or what is in the way, with the mkw-recomp download or the game's setup live on it) and What's New. The bar below has Mods again, your own mod list, with a dot when GameBanana has an update for one of your mods; the checklist moved from the bar to its card.
- **Mods page.** Get Mods, Install from a Link and Install from a File are now at the top, above your mods, and the page opens on Get Mods.
- **Get Mods page.** A Mods button takes you to your own mods (back to the list when you came from it), and the mature-pictures button is an eye: open when they are shown, crossed out when they are blurred.

## 1.4.7 — 2026-11-25

- **mkw-recomp 0.1.18 is required.** MKWMP now runs only on mkw-recomp 0.1.18: Play is refused on any earlier version, and the MKWMP mod turns itself off on one too, also when the game is started outside the launcher.
- **Your own mkw-recomp is updated too, where it is.** If you picked an mkw-recomp you installed yourself (0.1.15, 0.1.16 or 0.1.17), the launcher now updates it to 0.1.18 in that same folder, like the one it installs. Your game, its setup, saves, mods and settings stay, the game code does not need rebuilding, and the files it replaces are kept in .mkwmp-backup. Before, such an install stayed on 0.1.17 and said "your own build, one MKWMP supports".
- **The update is not optional.** When mkw-recomp is not 0.1.18, the launcher opens the update card by itself and runs it (only the download can wait). It asks again whenever the version goes wrong, also while it is open, and Play opens the update instead of the checklist. If mkw-recomp's own updater moves an install the launcher keeps to a release MKWMP does not know, the launcher puts 0.1.18 back in the same folder.
- **mkw-recomp's update check is on again.** Settings › General › "mkw-recomp update check" is no longer locked off. The launcher turns it back on once (older launchers had switched it off), and after that it is your choice.
- **Open mkw-recomp.** Settings › Game has a new row that opens mkw-recomp's own start screen instead of the game: its settings, game files, mods and tools.
- **Home: Play, Mods, What's New.** The cards are now Play, Mods (how many are on, and the updates GameBanana has for them) and What's New. Friends and the Game card are gone from the cards: Friends and the checklist are in the bar below, which no longer has Mods and Updates. While a new MKWMP is on its way, the What's New card says so with a pink badge (New, the download's percentage, Ready or Failed) and a progress bar, and opens the update.
## 1.4.6 — 2026-11-24

- **mkw-recomp 0.1.18.** The launcher now installs Adrià's mkw-recomp 0.1.18 and updates an install of 0.1.17 it made in place: your game, its setup, saves, mods and settings stay, and the game code does not need rebuilding. 0.1.18 fixes the game closing silently on some Windows 11 PCs right after it starts (error 0xC0000409). 0.1.17 still plays online.
- **DLSS, FSR 3.1 and RenHDrum download themselves.** Picking NVIDIA DLSS, AMD FSR 3.1 or their frame generation in Settings › Graphics, or HD vibration in Settings › Controls, now downloads the library it needs next to mkw.exe from NVIDIA's, AMD's or RenHDrum's official release, checked by mkw-recomp's own list. Settings shows the progress; it works at the game's next start. DLSS is downloaded only on a PC with an NVIDIA card. A library you already put there is kept.
- **New game settings from mkw-recomp 0.1.18.** Settings › Controls: HD vibration (RenHDrum), the game's own two-band vibration on player 1's DualSense, Pro Controller, Xbox One S or Steam Controller (off by default). Settings › Advanced: VRAM pressure eviction and Video memory log, the two mkw-recomp asks players to try if the game gets slower the longer they play (its issue #481). Both are off by default.
- **"off" and "no" in mkw.ini.** With mkw-recomp 0.1.18, a setting written by hand as off, false or no (or on, true, yes) shows as Off or On in Settings, as the game now reads it.

## 1.4.5 — 2026-11-22

- **After a failed update, play offline.** When an update can't be downloaded or installed, the card now offers Try Again, Continue Offline and Quit. Continue Offline lets you play offline until you close the launcher; going online (signing in, friends, reports, the server list, and the game asking for a seat) waits for the update and says "Update MKWMP to play online". Try the Update Again is on Home, in the friends panel and in Settings, and the launcher asks again at its next start. Your server, PIN and choices are kept.

## 1.4.4 — 2026-11-22

- **Updates install themselves.** A new MKWMP is no longer an offer. When the launcher finds one at start (or with Check Now) it downloads it at once and shows the update card, which stays until the update is in: Install and Restart, or the card does it by itself after ten seconds. Play waits for it. It never restarts under a running game: the update is then installed as soon as the game closes. Without a connection nothing changes and you play as before. If the download or the install fails, the card says why and offers Try Again, Continue Without Updating (for this time; the launcher asks again at its next start) and Quit. The "Update Automatically" switch is gone from Settings › Updates. This applies from 1.4.4 on: 1.4.3 and older still ask first.
- **Touch: a drag scrolls, a tap taps.** Buttons, list entries and everything else you can press now act when you lift your finger, and only if it did not move: a drag that starts on a button scrolls the list and no longer presses the button. Before, a drag that began on Settings' categories changed the category and stopped the scroll. The file picker's list can now be dragged on a Steam Deck too.
- **Smoother, faster scrolling.** A held direction, a spun wheel or the right stick now move a list in one even motion; before, each row or notch was a small push of its own. The wheel, a flick, the right stick and a held direction are all a bit faster.
- **Choose a Server in two columns.** The list of servers has the left side and scrolls by itself however long it is; the right side has the address (typed, or filled in when you pick a server), what the server answered, the Connect button and "Show Closest to Me". With a controller, A on a server and A again continues.
- **Settings › Online, reorganized.** The server is one row: its address, its status (online, players, queue) and a Disconnect button. Right below it: Privacy Policy, Report Abuse and Connect to Another Server, which used to be hidden behind pressing the server's address. Deleting your profile and uninstalling are shown in red.
- **Report Abuse.** "Report Voice Abuse" is now "Report Abuse", a page of its own opened from Settings › Online. Report beside a muted player (Settings › Voice & Camera) opens it with the name filled in.
- **One privacy page.** Settings › Online has one Privacy Policy row that shows whether you accepted the server's policy (a green check and "Accepted", or a warning and "Not accepted"). It opens the same page the first setup shows: what the server keeps, the server's own policy, and under them, always on screen, the switch "I'm 18 or older and I accept the privacy policy". Turning it off withdraws your consent at once.
- **Settings › Voice & Camera, reorganized.** Two parts built the same way. Voice: "Use voice in private rooms", then the microphone with its volume and test, then the output with its volume and test. Camera: a new "Use camera in private rooms", then "Show My Camera", the camera and its test. "See Friends' Cameras" is now part of the camera switch (your earlier choice is kept: the switch is on if either was on). "Mirror My Picture" moved to the Test Camera page (X), where you can see it.
- **What a switch turns off is hidden.** With voice off, the microphone, volumes, tests and output are not shown; with the camera off, neither are Show My Camera, the camera and its test. Your choices are kept and come back with the switch.
- **Text boxes.** The grey hint in an empty box is now exactly where your typing starts; before, the cursor stood further to the right.
- **Friends panel and file picker.** They no longer sit behind the bottom bar: each has its own button hints at its bottom, and the file picker's hints no longer run off its panel.

## 1.4.3 — 2026-11-21

- **mkw-recomp: the official 0.1.17.** The launcher now installs Adrià's own mkw-recomp release instead of MKWMP's build of it: what online play needed from that build is in 0.1.17. An install of 0.1.16-mkwmp.1 is updated in place; your game, its setup, saves, mods and settings stay, and the game code does not need rebuilding (the launcher may still ask for a quick Set Up Again). On Linux and the Steam Deck the launcher and the MKWMP mod keep the two Proton fixes of that build: shadow memory on (it stops a crash on NVIDIA cards) and the controller found at start.
- **New game settings from mkw-recomp 0.1.17.** Settings › Graphics: AI upscaling (NVIDIA DLSS on RTX cards, or AMD FSR 3.1), Upscaling quality, DLSS model, CAS sharpening and its strength, Frame generation by (mkw-recomp's own, DLSS or FSR 3.1), NVIDIA Reflex (NVIDIA cards only), and Shadow map (leave the shadows out in 3-4 player split screen, or everywhere). Settings › Controls: First controller to player 2, for local play with a keyboard and a controller (keep it off online). All off by default, as in the game. DLSS and FSR 3.1 need their libraries next to mkw.exe (nvngx_dlss.dll; amd_fidelityfx_*_vk.dll), which mkw-recomp does not ship.
- **One controller menu in the game.** The overlay shortcut and mkw-recomp's own menu shortcut no longer fight over the same press. The launcher now sets mkw-recomp's menu shortcut itself, to the button your overlay shortcut doesn't use (Select + Start when the overlay is on Guide, Guide otherwise), and the Menu Shortcut row is gone from Settings › Controls. mkw-recomp's menu still opens with F1 / Esc and from the overlay's mkw-recomp menu entry.
- **The launcher and the game agree on the button layout.** Settings › Controls › Button layout now does the same in both, on every controller: Switch confirms and accelerates with the right button, Xbox with the bottom one, and Automatic picks Switch for Switch controllers and Xbox for all others. Before, a Switch controller through Steam Input confirmed with the other button in the launcher when Switch or Xbox was chosen, and Automatic was always Xbox in the launcher. The setting's description says what each choice does.
- **The overlay comes to the front on a Linux desktop.** On KDE Plasma (and other Linux desktops) under Proton, the overlay could open behind the full-screen game, which then looked frozen until you Alt+Tabbed. The game's window now steps aside (minimised) while the overlay is up and comes back when you return. If your desktop still keeps the game in front, the launcher tells you how to allow it.
- **MKWMP no longer draws anything in the game.** The voice marks next to the players' names in the online pause menu, the mute toggle there (L / R and Y, PageUp / PageDown and M) and the mod's pop-up notices (connected, in line, friends online, set-up reminder) are gone, and so is the Room ID keypad of older mkw-recomp versions: the game and mkw-recomp show their own menus as they are. A friend whose camera shows no picture now gets nothing of MKWMP's in the game's camera spot (no more dark tile with their name), and the Test pattern camera has no text on it. The only button MKWMP still listens for in the game is your overlay shortcut. There is no muting for now: players you muted before stay muted, and you can unmute them in Settings › Voice & Camera. This also fixes the crash when a 4th player joined your room.
- **Everything is set in the launcher.** The MKWMP, Voice chat and Camera tabs in mkw-recomp's menu and MKWMP's Options page on mkw-recomp's start screen are gone. The launcher hands the game your account, voice and camera settings whenever the game is closed, so starting the game without the launcher (from mkw-recomp or Steam) still plays online with the account you signed in with in the launcher. Signed in over the game with the overlay? Restart the game once to go online.

## 1.4.2 — 2026-11-21

- **Ready to Play checklist.** A new page (Home's dock, Settings › Game, and Play when something is missing) lists what the game needs: mkw-recomp in the version MKWMP needs, your copy of the game, game version 1.8.0, the game set up on this device, MKWMP's files in the game, launcher updates and, online, server and sign-in. Each row has a button that fixes it. Play stays off until every row is ready.
- **Set the game up again.** Settings › Game and the checklist can rebuild the game from your copy (saves, mods and settings stay). The launcher also notices when your copy changed since it was set up, for example after you updated it somewhere else, and asks you to set it up again.
- **Smoother, faster scrolling.** About a third faster per wheel step, with a smooth glide; touchpads scroll in small steps; the right stick scrolls lists and speeds up when held.
- **Touch.** Drag any list with a finger and flick it; a drag never clicks the row under your finger. Swipe the Home tiles.
- **Switch button layout in the launcher too.** With the game's Switch layout, the right button confirms and the bottom one goes back in the launcher.
- **Contrast.** The dark theme stands out more from its background, and Settings › Appearance › Contrast sets it from Low to Higher.
- **Mods list.** One compact line per mod with its size on the right, a switch on each row, and Turn All Off / Turn All Back On (MKWMP itself always stays on).
- **Credits.** Settings › About links to mkw-recomp, its creator Adrià, its license and the source of MKWMP's build.
- **Choose the button that opens MKWMP in the game.** Settings › Game › Overlay Shortcut lists the combinations: Guide or Select + Start (as before), Guide, Select + Start, hold Select, hold Start, L3 + R3, LB + RB + Start, Select + LB and Select + RB. Each says what it may run into: Guide opens Steam's menu or Xbox Game Bar, and some Steam layouts take Select + Start. The game picks up a change within a few seconds, with no restart.

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

Online races, battles and friend rooms for MKW on community servers, with lobby voice chat.

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
