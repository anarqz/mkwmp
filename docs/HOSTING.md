# Hosting mkwmp

A server has two parts. Both can run on one machine:

| Part | What it does | Reached by | Ports |
| --- | --- | --- | --- |
| `mkwmp server` (Rust) | seats (player cap), queue, signed tickets, friends | the launcher, over HTTP(S) | `[seats] bind`, default 127.0.0.1:7000 |
| lobby (Python for now: `server/mkwmp_server.py` + `server/pia_capture.py`) | the game's online services: matchmaking, sessions, Gamesync, TURN relay | the game, directly | TCP 8905, 8906; UDP 10025, 3478, relay range |

How the parts work together: docs/ARCHITECTURE.md. Seat API and ticket format: docs/contracts/SEAT_API.md.

## 1. Run locally (one PC or a LAN)

Needs Rust (stable) and Python 3.12+ with `h2` and `cryptography`.

1. **Start the seat server.** On first run it creates the signing key:

       tools/host/run_seat_local.sh

2. **Start the lobby** from the repo root, in two shells. Use your LAN IPv4 instead of 127.0.0.1 for other devices:

       python -E -s server/mkwmp_server.py --host 0.0.0.0 --public-host 127.0.0.1 --port 8905 \
         --tls-cert scratch/certs/capture-cert.pem --tls-key scratch/certs/capture-key.pem --announce-matches
       python -I server/pia_capture.py --host 0.0.0.0 --reply

3. **Set the address handed to players.** In `config/server.toml`, set `[seats] lobby_endpoint` to the lobby's `IPv4:8905`. The game takes an IP address here, not a hostname (yet).

4. **Set up each player.** Each player edits `client.toml` (server URL, name, up to 8 friends, game dir) and runs `mkwmp-launcher client.toml`:
   - build the launcher with `cargo build --release --manifest-path rust/Cargo.toml -p client`;
   - it waits in the queue if the server is full;
   - it writes `<game>/mkwmp.toml` and starts the game once seated.

## 2. Host on a Linux VM

Any small VM works: 1 vCPU and 1 GB is plenty. Pick a generous transfer allowance, since the TURN relay can carry game traffic.

1. **Open the ports.** In the firewall, open TCP 8905, 8906 and UDP 10025, 3478 plus the TURN relay range. Do not expose 7000.

2. **Create the user and folders:**

       useradd --system --home-dir /opt/mkwmp --shell /usr/sbin/nologin mkwmp
       install -d -o mkwmp -g mkwmp /opt/mkwmp/{bin,config,keys,lobby}

3. **Install the seat server binary.** Build a static Linux binary, for example with `cargo zigbuild --release -p mkwmp --target x86_64-unknown-linux-musl`, and copy it to `/opt/mkwmp/bin/mkwmp`.

4. **Create the signing key and config:**
   - Run `/opt/mkwmp/bin/mkwmp keygen k1`.
   - Save line 2 (the private seed) as `/opt/mkwmp/keys/seat-ed25519.key` (mode 600, owner mkwmp).
   - Copy `config/server.toml` to `/opt/mkwmp/config/` and set:
     - `[server] name`;
     - `[seats] capacity` (default 50);
     - `[seats] lobby_endpoint = "<public IPv4>:8905"`;
     - `[ticket] kid` and `private_key_file`.

5. **Install the lobby:**
   - Copy `server/*.py` to `/opt/mkwmp/lobby/`.
   - Create the venv: `python3 -m venv /opt/mkwmp/venv && /opt/mkwmp/venv/bin/pip install h2 cryptography`.
   - Put the lobby's TLS pair in `/opt/mkwmp/keys/lobby-{cert,key}.pem`.
   - Write `PUBLIC_HOST=<public IPv4>` to `/opt/mkwmp/config/lobby.env`.

6. **Start the services.** Copy `deploy/systemd/*.service` to `/etc/systemd/system/`, then:

       systemctl daemon-reload
       systemctl enable --now mkwmp-seat mkwmp-lobby mkwmp-pia

7. **Put HTTPS in front of the seat API.** Two options:
   - **Cloudflare tunnel.** Create a tunnel routing `https://<your host>` to `http://127.0.0.1:7000`, put `TUNNEL_TOKEN=...` in `/etc/mkwmp/tunnel.env` (mode 600), install `cloudflared`, then `systemctl enable --now mkwmp-cloudflared`.
   - **Reverse proxy.** Use one such as Caddy: `your.host { reverse_proxy 127.0.0.1:7000 }`.

   Players then set `server = "https://<your host>"` in `client.toml`.

**Check that it's up:** `curl https://<your host>/seat/v1/status` shows seats and the queue.

**Restarts:** a restart clears every seat. Launchers notice and rejoin on their own, since nothing is stored.

**Keep these private:** the signing key (`seat-ed25519.key`), lobby tokens, the tunnel token and the lobby TLS key.
