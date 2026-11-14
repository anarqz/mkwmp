**Developer preview.** Online play works end to end on the official server. Some parts are still being tested; they are marked below.

New in rc.3:

- **Reserved gamertags can sign in.** Registering a reserved gamertag now asks for its PIN, the same as a gamertag that is already taken. In rc.2 it only said the name was reserved.
- **`launch_command`** in `client.toml`: an optional command the launcher runs to start the game (for example through Proton when the launcher is your Steam shortcut).
- The game patch is the same as in rc.2 and is not reinstalled.

From rc.2:

- **Host your own server.** `mkwmp-server` is the whole server in one program, for Windows or Linux: run it, answer one question (the address your friends reach you at), and it sets itself up. Steps: [README](https://github.com/anarqz/mkwmp#host-your-own-server).
- **Each server's own certificate.** The launcher fetches the certificate of the server you play on, and the game patch trusts it.

Still in this release:

- **Gamertags.** Register a gamertag in the launcher and get a 6-digit PIN; use the PIN to play with the same gamertag on another device. Gamertags belong to each server. Unused gamertags expire after 6 months.
- **Matchmaking with any number of lobbies.** Races and battles open new lobbies as players arrive and close them when they empty. The new lobby server is *being tested*.
- **Friends, kept on the server.** Add friends by gamertag; they accept or decline. The launcher shows each friend as playing, online or offline, and they are your friends in the game too.
- **Friend rooms** with a Room ID and password: *being tested*.
- **Background mode.** Closing the launcher keeps it in the system tray, with notifications when a friend comes online, starts playing or sends you a request. Having the launcher open doesn't take a seat on the server.

Files:

- `mkwmp-launcher-x86_64-windows.exe` / `mkwmp-launcher-x86_64-linux` (SteamOS): the launcher. It updates itself and the game patch.
- `mkwmp-probe-winmm.dll`: the game patch. The launcher installs it as `winmm.dll` in your game folder.
- `mkwmp-server-x86_64-windows.exe` / `mkwmp-server-x86_64-linux`: the server, for hosting your own (`mkwmp-server host --help`).
- `SHA256SUMS` + `SHA256SUMS.sig`: checksums signed with the release key. The launcher verifies them before installing anything.

Known limits:

- After pressing PLAY, your seat is held until you close the game or press STOP, even if you leave Online Play.
- Self-hosted servers have only been tested without the real game (sign-in, matchmaking, relay); the first real-game runs are coming.
- Over plain `http://`, PINs cross the network unencrypted. That's fine on a LAN; a public server should use HTTPS (see the README).

Setup guides: https://anarqz.github.io/mkwmp/
