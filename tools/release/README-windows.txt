mkwmp {version} for Windows: online play for Mario Kart World on community servers
https://github.com/anarqz/mkwmp

1. Keep this "mkwmp" folder anywhere you like (for example in Documents). The launcher is portable:
   its settings (client.toml), its logs (logs\) and the game it sets up all live in this folder, so
   don't delete it.
2. Open mkwmp-launcher.exe.
3. The welcome takes you through it: choose a name, point the launcher at your own copy of the game
   (it sets up mkw-recomp and installs the MKWMP mod into it), then choose a server to play online.
   When you register a name, write the 6-digit PIN down: it is shown only once.
4. Press Play.

Coming from MKWMP 1.3 or older? Your old launcher updates itself into this one and keeps your
client.toml (name, PIN, server, game folder). To move by hand, copy your old client.toml into this
folder before the first start.

On SteamOS or Linux this same launcher runs under Proton: see docs/LINUX.md (install-mkwmp.sh).

The launcher updates itself and the game's MKWMP files. Each download is checked against
checksums signed with the release key before it is used.

Bug? Settings > Support > Save Logs for a Bug Report, then attach the zip to a new issue:
https://github.com/anarqz/mkwmp/issues/new/choose

mkwmp is free software under the GNU GPL, version 3 or later (LICENSE). Third-party licenses:
NOTICE.md and licenses\.
