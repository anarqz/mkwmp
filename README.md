# MKWMP

Community online play for the [mkw-recomp](https://github.com/adriadam10/mkw-recomp) PC runtime: gamertags, friends, and race and battle lobbies on servers anyone can host.

**Website and setup guides: <https://anarqz.github.io/mkwmp/>**

> MKWMP is an unofficial community project, not affiliated with or endorsed by Nintendo. You need your own copy of the game.
>
> **Status: developer preview** ([v0.0.1-rc.1](https://github.com/anarqz/mkwmp/releases)). Online races and battles work end to end. The new lobby server and friend rooms are being tested.

![The MKWMP launcher: gamertag and PIN, server, game folder, friends with requests and who is playing, and the PLAY button](site/img/launcher-main.png)

## What it does

- **Gamertags.** Register one in the launcher and get a 6-digit PIN, which lets you use the same gamertag on another PC or a Steam Deck.
- **Friends, kept on the server.** Add friends by gamertag; they accept or decline. You see who is playing, online or offline, and they're your friends in the game too.
- **Matchmaking with any number of lobbies.** Races and battles open new lobbies as players arrive and close them when they empty.
- **Seats and a queue.** Each server has a fixed number of seats. PLAY takes one, or puts you in line. Having the launcher open doesn't take a seat.
- **Background mode.** Close the launcher and it stays in the system tray, with notifications when a friend comes online, starts playing or sends you a request.
- **Signed updates.** The launcher updates itself and the game patch from GitHub releases, and checks each download against the release signature.
- **Host your own** with one Docker Compose command (players need rc.2, not released yet).

![Launcher settings: background mode and notifications](site/img/launcher-settings.png)

## Play

1. Set up **mkw-recomp 0.1.10** (Windows build) with your own copy of the game, version 1.0.0. On a Steam Deck, run it through Proton 10.0.
2. Download the launcher for your system from the [latest release](https://github.com/anarqz/mkwmp/releases):
   - `mkwmp-launcher-x86_64-windows.exe` for Windows;
   - `mkwmp-launcher-x86_64-linux` for SteamOS and Linux.
3. Open it, register a gamertag, pick the game folder and press **PLAY**. The first run installs the game patch (`winmm.dll`).

Step-by-step guides for Windows and the Steam Deck: <https://anarqz.github.io/mkwmp/>.

## Host your own server

You need a Linux machine with Docker: a small VM, or a PC on your LAN.

```sh
git clone https://github.com/anarqz/mkwmp && cd mkwmp
cp .env.example .env              # set MKWMP_PUBLIC_IP
docker compose up -d --build
```

Then open TCP 7000, 8905, 8906 and UDP 3478, 10025, 49152–49407. Players set the launcher's **SERVER** to `http://<your IP>:7000`.

The first start creates the keys, the lobby's TLS certificate, the config and the SQLite registry, all in a Docker volume. To serve the seat API over HTTPS on your own domain, run `docker compose --profile https up -d`.

> **Not playable yet: needs rc.2.** Players need the **rc.2** launcher and game patch, which aren't released yet. They trust the certificate each server publishes; rc.1 can only reach the official server. Over plain `http://`, PINs travel unencrypted, so for a server on the internet use the HTTPS profile.

More in [docs/HOSTING.md](docs/HOSTING.md): HTTPS, admin commands, backups, and running it without Docker.

## Build it yourself

Everything builds from this repository. Releases are built by hand from the same sources; nothing is built by CI.

### Requirements

- **Rust** (stable): <https://rustup.rs>. The protobuf compiler is vendored, so there's nothing else to install.
- **Game patch:** [zig](https://ziglang.org) 0.15 and Python 3.
- **Cross-building** (optional): [cargo-zigbuild](https://github.com/rust-cross/cargo-zigbuild) builds the Windows launcher and a static Linux server from macOS or Linux.

### Launcher

```sh
cargo build --release --manifest-path rust/Cargo.toml -p client
# -> rust/target/release/mkwmp-launcher(.exe)

# Windows build from macOS/Linux:
cargo zigbuild --release --manifest-path rust/Cargo.toml -p client --target x86_64-pc-windows-gnu
```

A launcher you build yourself still updates itself from GitHub releases. To keep your build, turn updates off by setting `update = false` in its `client.toml`.

### Server

```sh
cargo build --release --manifest-path rust/Cargo.toml -p mkwmp
# -> rust/target/release/mkwmp   (mkwmp server | lobby | keygen | admin)

# Static Linux binary for a VM:
cargo zigbuild --release --manifest-path rust/Cargo.toml -p mkwmp --target x86_64-unknown-linux-musl
```

The Docker image builds the same binary: `docker compose build`.

### Game patch (`winmm.dll`)

```sh
python native/probe/build.py --version dev                         # -> native/probe/build/winmm.dll
python native/probe/build.py --install "C:\Games\mkw-recomp"       # build and copy into the game folder
```

`--zig PATH` selects the zig binary; the default is `zig` on your PATH.

### Tests

```sh
cargo test --manifest-path rust/Cargo.toml --workspace
```

Two tests start the real `mkwmp` binary. One plays two clients from sign-in to a shared session (TLS, Gamesync, NAT, TURN); the other covers seat expiry through the roster.

## Repository layout

| Path | What's there |
| --- | --- |
| `rust/client` | the launcher (egui): gamertag, friends, seats, tray, self-update |
| `rust/seat` | seat server: gamertags and PINs (SQLite), friends, presence, seats, queue, signed tickets |
| `rust/lobby` | the game-facing lobby logic: matchmaking, sessions, referee, friend rooms |
| `rust/gateway` | gRPC over HTTP/2 + TLS, TURN relay, NAT responder, rate limits |
| `rust/mkwmp` | the server binary: `mkwmp server`, `mkwmp lobby`, `keygen`, `admin` |
| `rust/{config,contracts,ticket}` | server config, NPLN protobuf types, seat ticket signing |
| `native/probe` | the game patch (`winmm.dll`): redirects the game's online services to an mkwmp server |
| `contracts/` | the game's NPLN service definitions (`.proto`) |
| `config/` | example configs: `server.toml` (the server), `client.toml` (the launcher) |
| `compose.yaml`, `deploy/` | Docker Compose stack, Dockerfile, systemd units |
| `docs/` | [HOSTING](docs/HOSTING.md), [ARCHITECTURE](docs/ARCHITECTURE.md), [seat API](docs/contracts/SEAT_API.md), [launcher API](docs/contracts/LAUNCHER_API.md) |
| `site/` | the website (GitHub Pages) |
| `tools/` | release script (`tools/release`) and a local seat server helper (`tools/host`) |
| `resources/` | research material: reverse-engineering notes and tools, captures, the original Python lobby, handoff notes |

## Support

MKWMP is made by players, for players. If you'd like to help keep the official server running: [Ko-fi](https://ko-fi.com/anarqz).
