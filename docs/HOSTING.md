# Hosting mkwmp

An MKWMP server runs the online services that MKW (under mkw-recomp) connects to, in place of the official ones. It is one program, `server` (`server.exe` on Windows), with two parts. The quickest way to run one on the internet is a small cloud VM and one command (section 1). `server host` runs both parts in one process (sections 2 and 3); `server seat` and `server lobby` run them separately (sections 1 and 4):

| Part | What it does | Reached by | Ports |
| --- | --- | --- | --- |
| `server seat` | gamertags + PINs, friends, seats, queue, signed seat tickets | the launcher, over HTTP(S) | TCP 7000 |
| `server lobby` | the game's online services: matchmaking, sessions, Gamesync, TURN relay, NAT check, voice chat and camera relay (friend rooms only) | the game, directly | TCP 8905, 8906; UDP 3478, 8907, 10025, 49152–49407 |

How the parts fit together: [ARCHITECTURE.md](ARCHITECTURE.md). Seat API and ticket format: [contracts/SEAT_API.md](contracts/SEAT_API.md).

Gamertags, hashed PINs and friendships are kept in one SQLite file. Seats and queues live in memory: after a restart, launchers rejoin on their own.

## 1. A cloud VM in one command (Vultr)

About US$5 a month runs a server for dozens of players. You need a [Vultr](https://www.vultr.com) account, an SSH key, and five minutes.

### 1.1 An SSH key

You log in to the VM with a key, not a password. If you don't have one yet, on your own computer (Windows: PowerShell; macOS/Linux: a terminal):

    ssh-keygen -t ed25519 -C "mkwmp server"

Press Enter at each question to accept the defaults. Your public key is now in `~/.ssh/id_ed25519.pub` (Windows: `C:\Users\<you>\.ssh\id_ed25519.pub`). Add it at Vultr: Account → SSH Keys → Add SSH Key, and paste the contents of `id_ed25519.pub`.

Never share `id_ed25519` (without `.pub`): it is the private half.

### 1.2 The VM

Pick a region close to most of your players: lag between players depends on their distance to each other, but the relay and the voice/camera relay run on this VM.

| | |
| --- | --- |
| Where | Deploy → Cloud Compute, **Shared CPU** (Regular) |
| Size | 1 vCPU, 1 GB RAM (about US$5/month) |
| Image | Ubuntu 24.04 LTS x64 |
| Login | your SSH key |
| Extras | IPv6 and automatic backups are optional |

When it's running, note its **public IPv4** address.

### 1.3 The cloud firewall

Vultr can filter traffic before it reaches the VM. Create a firewall with these **inbound** rules and attach it to the VM. The script opens the same ports in the VM's own firewall (ufw), so the two layers agree.

| Protocol | Port(s) | From | What |
| --- | --- | --- | --- |
| TCP | 22 | your IP (or anywhere) | SSH |
| TCP | 7000 | anywhere | the seat API: the launcher signs in, takes seats (use 80 and 443 instead with a domain, 1.5) |
| TCP | 8905 | anywhere | the game's online services (NPLN, TLS) |
| TCP | 8906 | anywhere | Gamesync (the game's match data) |
| UDP | 3478 | anywhere | TURN relay, for players who can't connect to each other directly |
| UDP | 49152–49407 | anywhere | the TURN relay's sockets |
| UDP | 8907 | anywhere | voice chat and camera relay (friend rooms) |
| UDP | 10025 | anywhere | NAT check |

In Vultr: Network → Firewall → Add Firewall Group, add the rules (IPv4, and IPv6 if you enabled it), then on the group's **Linked Instances** tab add your VM.

Outbound traffic must stay open: the VM downloads the release, and the server announces itself to the directory over HTTPS.

### 1.4 Run the script

Log in and run it as root:

    ssh root@<public IPv4>
    curl -fsSL https://anarqz.github.io/mkwmp/deploy-mkwmp-server.sh | sudo bash

It asks for the server name and region, then:

1. downloads the server (`mkwmp-server-<version>.zip`, which holds `server` for Linux and `server.exe` for Windows) from the latest release and checks it against the release's `SHA256SUMS`, whose Ed25519 signature must verify with the MKWMP release key pinned in the script (it stops otherwise);
2. creates the `mkwmp` user and `/var/lib/mkwmp` (keys, `registry.db`, `server.toml`, `privacy.md`);
3. installs two services, `mkwmp-seat` (launchers) and `mkwmp-lobby` (the game), and keeps their logs 7 days;
4. opens the ports above in ufw (SSH stays open);
5. lists your server in the public server directory (`https://beholder.anarqz.dev`), so it shows in the launcher's server browser;
6. checks that `/ping` answers from outside and that the directory lists it.

At the end it prints the address players use: `http://<public IPv4>:7000`.

Without questions (all optional):

    curl -fsSL https://anarqz.github.io/mkwmp/deploy-mkwmp-server.sh | sudo bash -s -- \
      --name "Friday kart" --region eu-west --capacity 24 --yes

| Option | |
| --- | --- |
| `--name` | the name in the launcher and the server list: up to 32 letters, digits, spaces and `_ . -` |
| `--region` | a short tag for the server list, e.g. `sa-east`, `us-east`, `eu-west` |
| `--capacity` | players seated at once (default 50); the rest wait in a queue |
| `--public-ip` | the address players reach the VM at, if it isn't the VM's own (behind NAT) |
| `--domain` | HTTPS with Caddy (1.5) |
| `--privacy-file` | your own privacy policy (Markdown) |
| `--no-announce` | stay out of the public server list (players type your address) |
| `--version` | a given release, e.g. `v1.3.1` |
| `--no-firewall` | leave ufw alone |
| `--uninstall` | remove the services, the program and the ufw rules; keeps `/var/lib/mkwmp` (`--purge` deletes it too) |

**Your privacy policy.** Players see `/var/lib/mkwmp/privacy.md` in the launcher and accept it. The script writes a template the first time: edit it so it's true for your server (who you are, how to reach you, how long you keep logs), then `systemctl restart mkwmp-seat`. Every edit is a new version, and players are asked to accept it again.

### 1.5 A domain and HTTPS (optional, recommended)

Over plain `http://`, PINs travel unencrypted. With a domain:

1. Create a DNS **A** record, e.g. `mkw.example.net` → the VM's public IPv4. If your DNS is at Cloudflare, make it **DNS only** (grey cloud) at first, so the certificate can be issued.
2. In the cloud firewall, open TCP 80 and 443 (and you can close 7000).
3. Run the script again with `--domain mkw.example.net`. It installs Caddy, which gets and renews a Let's Encrypt certificate, binds the seat API to the VM only, and lists `https://mkw.example.net` in the directory.

Players then use `https://mkw.example.net`. The game's own ports (8905, 8906 and the UDP ones) keep going straight to the VM's IP: they don't use the domain.

### 1.6 Updates, backups, logs

- **Update:** run the script again (no options needed: it remembers them in `/var/lib/mkwmp/deploy.env`). It installs the latest release and keeps gamertags, friends and keys. Restarting the lobby doesn't end running games.
- **Backup:** `/var/lib/mkwmp` is everything. `registry.db` holds gamertags, PIN hashes and friends; `keys/` is your server's identity (the ticket key, the lobby's certificate, the directory key). For a consistent copy: `systemctl stop mkwmp-seat && tar czf mkwmp-backup.tgz -C /var/lib mkwmp && systemctl start mkwmp-seat`. Keep backups private, and delete old ones: they hold your players' data. Vultr also offers automatic VM backups.
- **Logs:** `journalctl -u mkwmp-seat -u mkwmp-lobby -f` (kept 7 days).
- **Admin** (gamertags, reports): `sudo -u mkwmp /opt/mkwmp/bin/server admin list --config /var/lib/mkwmp/server.toml`, and the other `admin` commands in section 3.

### 1.7 Troubleshooting

| Symptom | Check |
| --- | --- |
| The script stops at "signature does NOT verify" | the download was altered or incomplete: run it again; don't install by hand |
| `/ping did not answer from this VM` | the cloud firewall (TCP 7000, or 80/443 with a domain) isn't attached or lacks the rule; with `--domain`, the A record doesn't point at the VM yet |
| Not listed in the directory | `journalctl -u mkwmp-seat \| grep announce`: `HTTP 400 ... endpoint` means a private address (use `--public-ip`), `connect` means outbound HTTPS is blocked |
| Launchers connect, but the game stays offline or "can't connect" | TCP 8905/8906 closed in the cloud firewall; `systemctl status mkwmp-lobby` |
| Players can't race or talk together | the UDP ports (3478, 49152–49407, 8907, 10025) closed in the cloud firewall |
| Anything else | `journalctl -u mkwmp-seat -u mkwmp-lobby -n 200`, and open an issue with it (without PINs) |

## 2. On your PC: `server host`

Download `mkwmp-server-<version>.zip` from the [latest release](https://github.com/anarqz/mkwmp/releases/latest) and unzip it into a folder of its own, or build it yourself (see the README). It holds `server.exe` (Windows) and `server` (Linux). Run it with no arguments; on Windows you can double-click `server.exe`. It runs the same as `server host`:

1. **First run:** it asks for the IPv4 address players reach this machine at.
   - **Friends over the internet:** your public IP. Also forward the ports below on your router to this PC.
   - **LAN party:** this PC's LAN address. It suggests it; press Enter.
2. It creates the folder `mkwmp-data` next to itself, holding:
   - `keys/`: the ticket signing key, the lobby's tokens, and the lobby's TLS certificate (self-signed, for the host names the game asks for);
   - `host.toml`: your settings;
   - `server.toml`: the config written from them;
   - `registry.db`: gamertags, PIN hashes, friends and voice-chat and camera reports.
3. It runs the seat server and the lobby together until you close it. Later runs reuse `host.toml`; flags change it:

       server.exe host --public-ip 203.0.113.7 --name "Friday kart" --capacity 12     (Linux: ./server host ...)
       server.exe host --help        # --turn-port, --seat-bind, --data, --init-only

When Windows asks whether to let it through the firewall, allow it. Players set the launcher's **SERVER** to `http://<that IP>:7000`.

Ports: TCP 7000, 8905, 8906 and UDP 3478, 8907, 10025, 49152–49407.

## 3. Linux server: Docker Compose

Docker runs the same `server host`. Use any Linux machine with Docker, for example a small VM (1 vCPU, 1 GB). Host networking is required, so the lobby sees each player's real address. Docker Desktop on macOS or Windows can't provide that; on those systems, use the program from section 2 instead.
    git clone https://github.com/anarqz/mkwmp && cd mkwmp
    cp .env.example .env              # set MKWMP_PUBLIC_IP (and the name / seat count)
    docker compose up -d --build

- `MKWMP_PUBLIC_IP` is the IPv4 address players reach the machine at: your public IP, or the LAN address for a LAN party. The game takes an address, not a host name.
- The first start creates the same files as section 2 in the `mkwmp-data` volume. Later starts keep them, and rewrite `server.toml` from `.env`.
- Open these ports in the firewall: TCP 7000, 8905, 8906 and UDP 3478, 8907, 10025, 49152–49407.
- Players set the launcher's **SERVER** to `http://<MKWMP_PUBLIC_IP>:7000`, register a gamertag on your server, and press PLAY.
- Plain `http://` is fine on a LAN. On the internet, PINs and the lobby certificate would travel unencrypted, so use HTTPS (below).

**Check that it's up:** `curl http://<ip>:7000/seat/v1/status`. Logs: `docker compose logs -f server`.

**HTTPS (optional).** With a domain pointing at the machine and ports 80/443 open:
1. Set `MKWMP_DOMAIN` in `.env`, plus `MKWMP_SEAT_BIND=127.0.0.1:7000` so only Caddy reaches the seat API.
2. Run `docker compose --profile https up -d`.
3. Players use `https://<domain>`, and you can close TCP 7000.

**Admin** (from the machine running the server; for section 2 use `--config mkwmp-data/server.toml`; for section 1, `sudo -u mkwmp /opt/mkwmp/bin/server admin <...> --config /var/lib/mkwmp/server.toml`): gamertags can be listed, reserved, released and their PINs reset:

    docker compose exec server server admin list --config /data/server.toml
    docker compose exec server server admin register <name> --config /data/server.toml   # reserve + new PIN
    docker compose exec server server admin reset-pin <name> --config /data/server.toml
    docker compose exec server server admin release <name> --config /data/server.toml

**Voice-chat and camera reports.** Players report abuse from the launcher (SETTINGS → REPORT VOICE ABUSE) or the MKWMP mod's options (voice or camera); each player has at most one waiting, and `admin reports` shows which kind. Review them from the same machine:

    docker compose exec server server admin reports --config /data/server.toml          # open ones (or: closed, all)
    docker compose exec server server admin close-report <id> warned in DM --config /data/server.toml
    docker compose exec server server admin delete-report <id> --config /data/server.toml

**Bans.** A player whose game claims another name than its profile's (a modified client; docs/contracts/SEAT_API.md §2d) is caught by the lobby. By default (`[lobby] ban_enforce = false`) it only logs `name spoofing (would ban)` (user, ussid, the two names' lengths, whether the claim is "Local", the seat's player count) and others still see the profile's name. With `ban_enforce = true` the lobby bans at once: the profile name for good, and for 14 days the address it played from and the address its seat was taken from. The banned player's launcher only shows "This profile is banned from MKWMP servers." The lobby logs one `name spoofing` line (lobby, user, ussid, registered and claimed name, address); the seat server logs `seat: banned <name>`. Watch the `would ban` lines on real races before turning it on. Bans are kept in `registry.db`. To see them, lift one made by mistake, or ban by hand:

    docker compose exec server server admin bans --config /data/server.toml                 # name bans (permanent) and address bans (with their end)
    docker compose exec server server admin unban <name> --config /data/server.toml         # the name and every address banned with it
    docker compose exec server server admin unban <ip> --config /data/server.toml           # one address only
    docker compose exec server server admin ban <name|ip> [reason...] --config /data/server.toml

An unban reaches the lobby within `roster_secs` (10 s); the profile and its PIN are as they were. If the lobby and the seat server run apart (section 1), set `[lobby] ban_queue_file` (e.g. `lobby-bans-pending.json` next to the config) so a ban made while the seat server is unreachable survives a lobby restart; `server host` sets it.

To lock an abusive player out, `reset-pin` their gamertag (their launcher's PIN stops working; don't hand out the new one) and add it to `[registry] reserved_names` (then restart the server), so it never expires and nobody registers it. Don't `release` it: a free gamertag is registered again by the launcher that still has its PIN. They can still register a new gamertag. Reports are deleted 90 days after they were filed.

**Voice and camera.** Voice chat and camera pictures are relayed only inside friend rooms; matchmade lobbies get none. A camera picture goes only to mutual friends in the room who asked for it (at most 4 each). The relay forwards both without decoding or storing them. `[lobby] video = false` turns cameras off on your server; `voice_port = 0` turns off both.

**Your players' data.** As the host you hold their gamertags, PIN hashes, friends and reports, and your logs can hold their IP addresses. The official server's [privacy policy](https://anarqz.github.io/mkwmp/#privacy) is a starting point for your own: tell your players what you keep and for how long. Unused gamertags are deleted after `[registry] expire_days` (30). Backups you make hold the same data.

**Backup:** everything is in the `mkwmp-data` volume (`registry.db` and `keys/`). For a consistent copy, stop the seat server first:

    docker compose stop server
    docker run --rm -v mkwmp_mkwmp-data:/data -v "$PWD":/out debian tar czf /out/mkwmp-data.tgz -C /data .
    docker compose start server

**Update:** `git pull && docker compose up -d --build`.

## 4. A Linux VM with systemd, by hand

1. **Open the ports** listed above. Keep 7000 closed if a tunnel or proxy fronts it (step 6).
2. **Create the user and folders:**

       useradd --system --home-dir /opt/mkwmp --shell /usr/sbin/nologin mkwmp
       install -d -o mkwmp -g mkwmp /opt/mkwmp/{bin,config,keys}

3. **Build and install the binary.** Build a static Linux binary, for example with `cargo zigbuild --release --manifest-path rust/Cargo.toml -p mkwmp --target x86_64-unknown-linux-musl` (the binary is `rust/target/x86_64-unknown-linux-musl/release/server`), and copy it to `/opt/mkwmp/bin/server`.
4. **Create the keys.** The simplest way is `server host --init-only --data /opt/mkwmp/data --public-ip <IPv4>`. It writes the keys and a complete `server.toml` there, so you can skip to step 6 and point the units at that file. To create them by hand instead:
   - **Ticket signing key:** run `/opt/mkwmp/bin/server keygen k1`. Save line 2 as `/opt/mkwmp/keys/seat-ed25519.key`. Line 4 is the `public_keys` entry.
   - **Lobby token:** `openssl rand -hex 32 > /opt/mkwmp/keys/lobby-tokens.txt`.
   - **Token secret:** `openssl rand 32 > /opt/mkwmp/keys/lobby-token-secret`.
   - **Lobby certificate:** a self-signed RSA 2048 certificate with `basicConstraints=critical,CA:TRUE` and the names `*.lp1.t.npln.srv.nintendo.net`, `gamesync.npln.nintendo.net` and `localhost`, saved as `lobby-cert.pem` and `lobby-key.pem`.
   - `chmod 600` every key and `chown` them to `mkwmp`.
5. **Write the config.** Copy `config/server.toml` to `/opt/mkwmp/config/` and fill it in the way `server host` writes it:
   - `[seats] lobby_endpoint = "<public IPv4>:8905"`;
   - `[lobby] host = "0.0.0.0"`, `public_host`, `relay_ports = "49152:49407"`, `require_ticket = true`, `roster_url` + `roster_token_file`;
   - `[ticket]` key and `public_keys`;
   - `[registry] file = "/var/lib/mkwmp/registry.db"`;
   - `[tls]` = the lobby pair.
6. **Start the services:**

       cp deploy/systemd/mkwmp-seat.service deploy/systemd/mkwmp-lobby-rs.service /etc/systemd/system/
       systemctl daemon-reload
       systemctl enable --now mkwmp-seat mkwmp-lobby-rs

7. **Put HTTPS in front of the seat API** (`127.0.0.1:7000`):
   - **Cloudflare tunnel:** route `https://<your host>` to `http://127.0.0.1:7000`, put `TUNNEL_TOKEN=...` in `/etc/mkwmp/tunnel.env` (mode 600), install `cloudflared`, then `systemctl enable --now mkwmp-cloudflared`.
   - **Reverse proxy:** use, for example, Caddy with the block in `deploy/docker/Caddyfile`. It passes the player's address to the seat server's rate limits.

8. **Optional: your privacy policy and the server list.**
   - `[privacy] file = "/opt/mkwmp/config/privacy.md"`: the policy your players accept in the launcher (`GET /privacy`). Without it they see a generic default. `enforce = true` refuses seats, friends and reports to players who haven't accepted the current text; the 1.3.x launchers can't accept it (only the new launcher, 1.4.0, can), so leave it off until your players have that one. See SEAT_API.md §2c.
   - `[announce] beholder_url` + `endpoint` (your public `https://` seat API URL) lists your server in a beholder's server browser. Set `key_file = "/var/lib/mkwmp/beholder-ed25519.key"`: the unit's `/opt/mkwmp/keys` is read-only and the key is made on first start. See BEHOLDER_API.md.
   - To run a beholder yourself: `config/beholder.toml` + `deploy/systemd/mkwmp-beholder.service`.

## 5. For development

    tools/host/run_seat_local.sh          # seat server on 127.0.0.1:7000, config/server.toml
    rust/target/release/server lobby config/server.toml

The local config uses the development certificate pair in `scratch/certs/` (not in the repo).

## Keep these private

- the ticket signing key (`seat-ed25519.key`);
- the lobby tokens and token secret;
- the admin token;
- the lobby's private key (`lobby-key.pem`);
- the tunnel token;
- the beholder announce key (`beholder-ed25519.key`): whoever holds it can replace your server list entry.

The lobby **certificate** is public: the seat server publishes it (`lobbyCa` in `/seat/v1/status`) so that players' game patches trust it.
