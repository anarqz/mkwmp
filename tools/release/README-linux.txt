mkwmp {version} for SteamOS and Linux: online play for Mario Kart World on community servers
https://github.com/anarqz/mkwmp

Steam Deck, one command (Desktop Mode, in Konsole): it installs this release to ~/mkwmp and adds
"Mario Kart World (MKWMP)" to Steam:

    curl -fsSL https://anarqz.github.io/mkwmp/install.sh | bash

By hand:

1. Put this "mkwmp" folder in your home folder (~/mkwmp). The launcher keeps its settings
   (client.toml) and logs (logs/) here.
2. Run ./mkwmp-launcher (if your file manager lost the permission: chmod +x mkwmp-launcher).
3. GAMERTAG: type one and press REGISTER. Write the 6-digit PIN down: it is shown only once.
4. GAME FOLDER: pick the folder that has mkw.exe. Press INSTALL under it; the line turns green:
   "Patches installed". The launcher also copies Proton 10.0's winmm.dll in as winmm_host.dll
   (install Proton 10.0 in Steam first).
5. Tick "I start the game from Steam" if Steam starts your game, then press PLAY.

SETTINGS > GAME PATCH > UNINSTALL removes every mkwmp file from the game folder.

The launcher updates itself and the game patch. Each download is checked against checksums
signed with the release key before it is used.

Bug? SETTINGS > SAVE LOGS FOR A BUG REPORT, then attach the zip to a new issue:
https://github.com/anarqz/mkwmp/issues/new/choose

mkwmp is free software under the GNU GPL, version 3 or later (LICENSE). Third-party licenses:
NOTICE.md and licenses/.
