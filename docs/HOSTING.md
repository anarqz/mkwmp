# Hosting mkwmp

An mkwmp server is one binary, `mkwmp`, run twice:

| Part | What it does | Reached by | Ports |
| --- | --- | --- | --- |
| `mkwmp server` | gamertags + PINs, friends, seats, queue, signed seat tickets | the launcher, over HTTP(S) | TCP 7000 |
| `mkwmp lobby` | the game's online services: matchmaking, sessions, Gamesync, TURN relay, NAT check | the game, directly | TCP 8905, 8906; UDP 3478, 10025, 49152–49407 |

How the parts fit together: [ARCHITECTURE.md](ARCHITECTURE.md). Seat API and ticket format: [contracts/SEAT_API.md](contracts/SEAT_API.md).

Gamertags, hashed PINs and friendships are kept in one SQLite file. Seats and queues live in memory: after a restart, launchers rejoin on their own.

> **Game patch version.** A server with its own lobby certificate (every server except the official one) needs players on the **rc.2** game patch or later. That patch trusts the certificate each server publishes. The launcher installs the patch.

## 1. Docker Compose (recommended)

Use any Linux machine with Docker: a small VM (1 vCPU, 1 GB) or a PC on your LAN. Host networking is required, so the lobby sees each player's real address, and Docker Desktop on macOS or Windows won't do.

    git clone https://github.com/anarqz/mkwmp && cd mkwmp
    cp .env.example .env              # set MKWMP_PUBLIC_IP (and the name / seat count)
    docker compose up -d --build

- `MKWMP_PUBLIC_IP` is the IPv4 address players reach the machine at: your public IP, or the LAN address for a LAN party. The game takes an address, not a host name.
- The first start creates everything in the `mkwmp-data` volume:
  - the ticket signing key;
  - the lobby's tokens;
  - the lobby's TLS certificate (self-signed, for the host names the game asks for);
  - `server.toml`;
  - `registry.db`.

  Later starts keep all of these. They rewrite only `server.toml`, from `.env`.
- Open these ports in the firewall: TCP 7000, 8905, 8906 and UDP 3478, 10025, 49152–49407.
- Players set the launcher's **SERVER** to `http://<MKWMP_PUBLIC_IP>:7000`, register a gamertag on your server, and press PLAY.

**Check that it's up:** `curl http://<ip>:7000/seat/v1/status`. Logs: `docker compose logs -f lobby seat`.

**HTTPS (optional).** With a domain pointing at the machine and ports 80/443 open:
1. Set `MKWMP_DOMAIN` in `.env`, plus `MKWMP_SEAT_BIND=127.0.0.1:7000` so only Caddy reaches the seat API.
2. Run `docker compose --profile https up -d`.
3. Players use `https://<domain>`, and you can close TCP 7000.

**Admin:** gamertags can be listed, reserved, released and their PINs reset:

    docker compose exec seat mkwmp admin list --config /data/server.toml
    docker compose exec seat mkwmp admin register <name> --config /data/server.toml   # reserve + new PIN
    docker compose exec seat mkwmp admin reset-pin <name> --config /data/server.toml
    docker compose exec seat mkwmp admin release <name> --config /data/server.toml

**Backup:** everything is in the `mkwmp-data` volume (`registry.db` and `keys/`). For a consistent copy, stop the seat server first:

    docker compose stop seat
    docker run --rm -v mkwmp_mkwmp-data:/data -v "$PWD":/out debian tar czf /out/mkwmp-data.tgz -C /data .
    docker compose start seat

**Update:** `git pull && docker compose up -d --build`.

## 2. Without Docker (systemd on a Linux VM)

1. **Open the ports** listed above. Keep 7000 closed if a tunnel or proxy fronts it (step 6).
2. **Create the user and folders:**

       useradd --system --home-dir /opt/mkwmp --shell /usr/sbin/nologin mkwmp
       install -d -o mkwmp -g mkwmp /opt/mkwmp/{bin,config,keys}

3. **Build and install the binary.** Build a static Linux binary, for example with `cargo zigbuild --release --manifest-path rust/Cargo.toml -p mkwmp --target x86_64-unknown-linux-musl`, and copy it to `/opt/mkwmp/bin/mkwmp`.
4. **Create the keys.** The steps in `deploy/docker/setup.sh` show what each key is:
   - **Ticket signing key:** run `/opt/mkwmp/bin/mkwmp keygen k1`. Save line 2 as `/opt/mkwmp/keys/seat-ed25519.key`. Line 4 is the `public_keys` entry.
   - **Lobby token:** `openssl rand -hex 32 > /opt/mkwmp/keys/lobby-tokens.txt`.
   - **Token secret:** `openssl rand 32 > /opt/mkwmp/keys/lobby-token-secret`.
   - **Lobby certificate:** use the `openssl req` line from `setup.sh` to create `lobby-cert.pem` and `lobby-key.pem`.
   - `chmod 600` every key and `chown` them to `mkwmp`.
5. **Write the config.** Copy `config/server.toml` to `/opt/mkwmp/config/` and fill it in the way `setup.sh` writes it:
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

## 3. On one PC, for development

    tools/host/run_seat_local.sh          # seat server on 127.0.0.1:7000, config/server.toml
    rust/target/release/mkwmp lobby config/server.toml

The local config uses the development certificate pair in `scratch/certs/` (not in the repo).

## Keep these private

- the ticket signing key (`seat-ed25519.key`);
- the lobby tokens and token secret;
- the admin token;
- the lobby's private key (`lobby-key.pem`);
- the tunnel token.

The lobby **certificate** is public: the seat server publishes it (`lobbyCa` in `/seat/v1/status`) so that players' game patches trust it.
