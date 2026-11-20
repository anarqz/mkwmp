# Hosting mkwmp

An mkwmp server is one binary, `mkwmp`, with two parts. `mkwmp host` runs both in one process (sections 1 and 2); `mkwmp server` and `mkwmp lobby` run them separately (section 3):

| Part | What it does | Reached by | Ports |
| --- | --- | --- | --- |
| `mkwmp server` | gamertags + PINs, friends, seats, queue, signed seat tickets | the launcher, over HTTP(S) | TCP 7000 |
| `mkwmp lobby` | the game's online services: matchmaking, sessions, Gamesync, TURN relay, NAT check, voice chat and camera relay (friend rooms only) | the game, directly | TCP 8905, 8906; UDP 3478, 8907, 10025, 49152–49407 |

How the parts fit together: [ARCHITECTURE.md](ARCHITECTURE.md). Seat API and ticket format: [contracts/SEAT_API.md](contracts/SEAT_API.md).

Gamertags, hashed PINs and friendships are kept in one SQLite file. Seats and queues live in memory: after a restart, launchers rejoin on their own.

> **Needs rc.2 or later.** A server with its own lobby certificate (every server except the official one) needs players on the **rc.2** launcher and game patch. Those versions trust the certificate each server publishes; rc.1 can only reach the official server.

## 1. On your PC: `mkwmp host`

Download `mkwmp-server-x86_64-windows.exe` or `mkwmp-server-x86_64-linux` from the [releases](https://github.com/anarqz/mkwmp/releases) (rc.2 onward), or build it yourself (see the README). Run it with no arguments; on Windows you can double-click it. It runs the same as `mkwmp host`:

1. **First run:** it asks for the IPv4 address players reach this machine at.
   - **Friends over the internet:** your public IP. Also forward the ports below on your router to this PC.
   - **LAN party:** this PC's LAN address. It suggests it; press Enter.
2. It creates the folder `mkwmp-data` next to itself, holding:
   - `keys/`: the ticket signing key, the lobby's tokens, and the lobby's TLS certificate (self-signed, for the host names the game asks for);
   - `host.toml`: your settings;
   - `server.toml`: the config written from them;
   - `registry.db`: gamertags, PIN hashes, friends and voice-chat and camera reports.
3. It runs the seat server and the lobby together until you close it. Later runs reuse `host.toml`; flags change it:

       mkwmp-server host --public-ip 203.0.113.7 --name "Friday kart" --capacity 12
       mkwmp-server host --help        # --turn-port, --seat-bind, --data, --init-only

When Windows asks whether to let it through the firewall, allow it. Players set the launcher's **SERVER** to `http://<that IP>:7000`.

Ports: TCP 7000, 8905, 8906 and UDP 3478, 8907, 10025, 49152–49407.

## 2. Linux server: Docker Compose

Docker runs the same `mkwmp host`. Use any Linux machine with Docker, for example a small VM (1 vCPU, 1 GB). Host networking is required, so the lobby sees each player's real address. Docker Desktop on macOS or Windows can't provide that; on those systems, use the program from section 1 instead.
    git clone https://github.com/anarqz/mkwmp && cd mkwmp
    cp .env.example .env              # set MKWMP_PUBLIC_IP (and the name / seat count)
    docker compose up -d --build

- `MKWMP_PUBLIC_IP` is the IPv4 address players reach the machine at: your public IP, or the LAN address for a LAN party. The game takes an address, not a host name.
- The first start creates the same files as section 1 in the `mkwmp-data` volume. Later starts keep them, and rewrite `server.toml` from `.env`.
- Open these ports in the firewall: TCP 7000, 8905, 8906 and UDP 3478, 8907, 10025, 49152–49407.
- Players set the launcher's **SERVER** to `http://<MKWMP_PUBLIC_IP>:7000`, register a gamertag on your server, and press PLAY.
- Plain `http://` is fine on a LAN. On the internet, PINs and the lobby certificate would travel unencrypted, so use HTTPS (below).

**Check that it's up:** `curl http://<ip>:7000/seat/v1/status`. Logs: `docker compose logs -f server`.

**HTTPS (optional).** With a domain pointing at the machine and ports 80/443 open:
1. Set `MKWMP_DOMAIN` in `.env`, plus `MKWMP_SEAT_BIND=127.0.0.1:7000` so only Caddy reaches the seat API.
2. Run `docker compose --profile https up -d`.
3. Players use `https://<domain>`, and you can close TCP 7000.

**Admin** (from the machine running the server; for section 1 use `--config mkwmp-data/server.toml`): gamertags can be listed, reserved, released and their PINs reset:

    docker compose exec server mkwmp admin list --config /data/server.toml
    docker compose exec server mkwmp admin register <name> --config /data/server.toml   # reserve + new PIN
    docker compose exec server mkwmp admin reset-pin <name> --config /data/server.toml
    docker compose exec server mkwmp admin release <name> --config /data/server.toml

**Voice-chat and camera reports.** Players report abuse from the launcher (SETTINGS → REPORT VOICE ABUSE) or the MKWMP mod's options (voice or camera); each player has at most one waiting, and `admin reports` shows which kind. Review them from the same machine:

    docker compose exec server mkwmp admin reports --config /data/server.toml          # open ones (or: closed, all)
    docker compose exec server mkwmp admin close-report <id> warned in DM --config /data/server.toml
    docker compose exec server mkwmp admin delete-report <id> --config /data/server.toml

To lock an abusive player out, `reset-pin` their gamertag (their launcher's PIN stops working; don't hand out the new one) and add it to `[registry] reserved_names` (then restart the server), so it never expires and nobody registers it. Don't `release` it: a free gamertag is registered again by the launcher that still has its PIN. They can still register a new gamertag. Reports are deleted 90 days after they were filed.

**Voice and camera.** Voice chat and camera pictures are relayed only inside friend rooms; matchmade lobbies get none. A camera picture goes only to mutual friends in the room who asked for it (at most 4 each). The relay forwards both without decoding or storing them. `[lobby] video = false` turns cameras off on your server; `voice_port = 0` turns off both.

**Your players' data.** As the host you hold their gamertags, PIN hashes, friends and reports, and your logs can hold their IP addresses. The official server's [privacy policy](https://anarqz.github.io/mkwmp/#privacy) is a starting point for your own: tell your players what you keep and for how long. Unused gamertags are deleted after `[registry] expire_days` (30). Backups you make hold the same data.

**Backup:** everything is in the `mkwmp-data` volume (`registry.db` and `keys/`). For a consistent copy, stop the seat server first:

    docker compose stop server
    docker run --rm -v mkwmp_mkwmp-data:/data -v "$PWD":/out debian tar czf /out/mkwmp-data.tgz -C /data .
    docker compose start server

**Update:** `git pull && docker compose up -d --build`.

## 3. A Linux VM with systemd, by hand

1. **Open the ports** listed above. Keep 7000 closed if a tunnel or proxy fronts it (step 6).
2. **Create the user and folders:**

       useradd --system --home-dir /opt/mkwmp --shell /usr/sbin/nologin mkwmp
       install -d -o mkwmp -g mkwmp /opt/mkwmp/{bin,config,keys}

3. **Build and install the binary.** Build a static Linux binary, for example with `cargo zigbuild --release --manifest-path rust/Cargo.toml -p mkwmp --target x86_64-unknown-linux-musl`, and copy it to `/opt/mkwmp/bin/mkwmp`.
4. **Create the keys.** The simplest way is `mkwmp host --init-only --data /opt/mkwmp/data --public-ip <IPv4>`. It writes the keys and a complete `server.toml` there, so you can skip to step 6 and point the units at that file. To create them by hand instead:
   - **Ticket signing key:** run `/opt/mkwmp/bin/mkwmp keygen k1`. Save line 2 as `/opt/mkwmp/keys/seat-ed25519.key`. Line 4 is the `public_keys` entry.
   - **Lobby token:** `openssl rand -hex 32 > /opt/mkwmp/keys/lobby-tokens.txt`.
   - **Token secret:** `openssl rand 32 > /opt/mkwmp/keys/lobby-token-secret`.
   - **Lobby certificate:** a self-signed RSA 2048 certificate with `basicConstraints=critical,CA:TRUE` and the names `*.lp1.t.npln.srv.nintendo.net`, `gamesync.npln.nintendo.net` and `localhost`, saved as `lobby-cert.pem` and `lobby-key.pem`.
   - `chmod 600` every key and `chown` them to `mkwmp`.
5. **Write the config.** Copy `config/server.toml` to `/opt/mkwmp/config/` and fill it in the way `mkwmp host` writes it:
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
   - `[privacy] file = "/opt/mkwmp/config/privacy.md"`: the policy your players accept in the launcher (`GET /privacy`). Without it they see a generic default. `enforce = true` refuses seats, friends and reports to players who haven't accepted the current text; launchers up to 1.3.0 can't accept it, so leave it off until your players have 1.4.0. See SEAT_API.md §2c.
   - `[announce] beholder_url` + `endpoint` (your public `https://` seat API URL) lists your server in a beholder's server browser. Set `key_file = "/var/lib/mkwmp/beholder-ed25519.key"`: the unit's `/opt/mkwmp/keys` is read-only and the key is made on first start. See BEHOLDER_API.md.
   - To run a beholder yourself: `config/beholder.toml` + `deploy/systemd/mkwmp-beholder.service`.

## 4. For development

    tools/host/run_seat_local.sh          # seat server on 127.0.0.1:7000, config/server.toml
    rust/target/release/mkwmp lobby config/server.toml

The local config uses the development certificate pair in `scratch/certs/` (not in the repo).

## Keep these private

- the ticket signing key (`seat-ed25519.key`);
- the lobby tokens and token secret;
- the admin token;
- the lobby's private key (`lobby-key.pem`);
- the tunnel token;
- the beholder announce key (`beholder-ed25519.key`): whoever holds it can replace your server list entry.

The lobby **certificate** is public: the seat server publishes it (`lobbyCa` in `/seat/v1/status`) so that players' game patches trust it.
