mkwmp {version} for Windows: online play for Mario Kart World on community servers
https://github.com/anarqz/mkwmp

1. Keep this "mkwmp" folder anywhere you like (for example in Documents). The launcher keeps its
   settings (client.toml) and its logs (logs\) here, so don't delete the folder.
2. Open mkwmp-launcher.exe.
3. GAMERTAG: type one and press REGISTER. Write the 6-digit PIN down: it is shown only once.
4. GAME FOLDER: pick the folder that has mkw.exe. The line under it says whether the game patch is
   installed. Press INSTALL; the line turns green: "Patches installed".
5. Press PLAY.

The game patch is game\winmm.dll (plus game\mkwmp-voice.dll for voice chat). INSTALL copies both
into your game folder and saves any winmm.dll that was already there as winmm.dll.bak.
SETTINGS > GAME PATCH > UNINSTALL removes every mkwmp file from the game folder and puts that
file back.

The launcher updates itself and the game patch. Each download is checked against checksums
signed with the release key before it is used.

Bug? SETTINGS > SAVE LOGS FOR A BUG REPORT, then attach the zip to a new issue:
https://github.com/anarqz/mkwmp/issues/new/choose

mkwmp is free software under the GNU GPL, version 3 or later (LICENSE). Third-party licenses:
NOTICE.md and licenses\.
