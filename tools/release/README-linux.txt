mkwmp {version} for Linux: online play for Mario Kart World on community servers
https://github.com/anarqz/mkwmp

Running the game through Proton is no longer supported. Steam Deck and Linux will come back
natively through mkw-recomp's planned plugin / mod-loader API; until then, play on Windows.

This Linux build of the launcher still runs (gamertags, friends, lobby voice chat, and hosting
your own server with mkwmp-server), and is ready for the native game build when it lands.

1. Put this "mkwmp" folder in your home folder (~/mkwmp). The launcher keeps its settings
   (client.toml) and logs (logs/) here.
2. Run ./mkwmp-launcher (if your file manager lost the permission: chmod +x mkwmp-launcher).
3. GAMERTAG: type one and press REGISTER. Write the 6-digit PIN down: it is shown only once.

SETTINGS > GAME PATCH > UNINSTALL removes every mkwmp file from the game folder.

The launcher updates itself and the game patch. Each download is checked against checksums
signed with the release key before it is used.

Bug? SETTINGS > SAVE LOGS FOR A BUG REPORT, then attach the zip to a new issue:
https://github.com/anarqz/mkwmp/issues/new/choose

mkwmp is free software under the GNU GPL, version 3 or later (LICENSE). Third-party licenses:
NOTICE.md and licenses/.
