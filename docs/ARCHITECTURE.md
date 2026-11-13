# Architecture

**One server binary, `mkwmp`, with two parts.** Both run on one VM by default; anyone can host. Contracts in `contracts/*.proto` (NPLN, game-facing) and `docs/contracts/SEAT_API.md` (ours) are the source of truth.

```
 player PC                                   the VM
 ┌──────────────────┐   HTTPS (seat API)   ┌───────────────────────────────┐
 │ mkwmp-launcher   │ ───────────────────► │ mkwmp server  (seats, queue,  │
 │  name + 8 friends│ ◄─ queue / ticket ── │   signed tickets, friends)    │
 │  writes mkwmp.toml                      │        ▲ roster (localhost)   │
 │  launches mkw.exe│                      │        │                      │
 │   └ winmm probe ─┼── NPLN gRPC/TLS ────►│ mkwmp lobby   (matchmaking,   │
 │                  │◄── Pia UDP / TURN ──►│   sessions, Gamesync, TURN)   │
 └──────────────────┘                      └───────────────────────────────┘
```

    mkwmp server [config/server.toml]   seats, queue, signed tickets (HTTP)
    mkwmp lobby  [config/server.toml]   the game-facing lobby
    mkwmp keygen [kid]                  new ticket signing key

## The two parts

**`mkwmp server`** (crate `rust/seat`) is the seat server. It owns the roster:

- A player connects with a name and up to 8 friend names.
- If one of the `capacity` seats (default 50) is free, the player gets one. Otherwise they wait in a FIFO queue.
- A seated player receives a **signed ticket** (Ed25519).
- The launcher heartbeats to keep the seat. Silence frees it for the next player in line.
- Friends exist only while both players are online. Two players who list each other are mutual friends, and the lobby serves them to the game's ONLINE → FRIENDS menu.
- There is no database: a restart clears every session. The `bootId` tells clients to rejoin.

**`mkwmp lobby`** (crate `rust/lobby`) is the game-facing side: NPLN Auth/Matchmaker/GameSessionService/Gamesync, plus Pia NAT/TURN.

- It accepts a game only with a ticket that verifies against the seat server's public key.
- It drops players the roster reports as no longer alive.
- **Today this is the Python reference server** (`server/mkwmp_server.py`). Handlers move to Rust as each is verified in game.

**Why the ticket is signed** even on one VM: the lobby holds only the public key, so the two parts can still be split across machines (or a lobby hosted by someone else) without the lobby being able to mint seats. The seat cap holds because every lobby login needs a ticket the seat server issued.

**Networking on the VM:**

- Seat API (`[seats] bind`, HTTP): behind a TLS reverse proxy or a Cloudflare tunnel.
- NPLN TLS (8905), Gamesync (8906), and UDP 10025 (Pia NAT) + 3478 (TURN) + relay ports: open directly on the public IP. A Cloudflare tunnel cannot carry the game's UDP.

## Launcher

`mkwmp-launcher` (crate `rust/client`) is a small binary the player runs. It works like this:

1. It reads `client.toml`: the server URL, name, friends and game dir.
2. It joins the seat server and shows the queue position and friends' status.
3. When the player is seated, it writes `<game>\mkwmp.toml` with the lobby endpoint, name and **ticket**, then launches `mkw.exe`.
4. It heartbeats until the game exits, then frees the seat.

While the game runs:

- Edits to `friends` in `client.toml` are sent on the next heartbeat.
- If the server restarts, the launcher rejoins and rewrites `mkwmp.toml`.

## Crates

```
rust/
  mkwmp/       the server binary: `server`, `lobby`, `keygen` subcommands
  seat/        seat server: names, roster (seats/queue/friends), HTTP API
  ticket/      ticket v1 sign/verify (Python twin: tools/ticket/verify_ticket.py)
  lobby/       lobby (Rust port pending; matchmaking core only)
  contracts/   generated prost types + tonic stubs for the NPLN protos
  config/      typed server config loader
  gateway/     TLS/h2 front (pending)
  client/      the player launcher (binary `mkwmp-launcher`)
```

## Config

- `config/server.toml` — server side:
  - `[seats]`: capacity, bind, timeouts, `lobby_endpoint` handed to players, `lobby_tokens_file` for the roster;
  - `[ticket]`: kid, private key file, trusted public keys, ttl;
  - `[lobby]`, `[tls]`.
  - Relative paths resolve against the file.
- `config/client.toml` — per player: server URL, name, `friends`, game dir, optional loopback slot.
  - The launcher turns it into `<game dir>\mkwmp.toml`, which the probe (winmm.dll) reads at startup. That file is parsed in C (`native/probe/src/config.c`).
  - If `mkwmp.toml` is absent, the game runs stock/offline.
  - Env vars (`MKWMP_HOST/PORT/PLAYER/ENABLE`) still override for testing.

## Today vs. target

The Python server (`server/mkwmp_server.py`) is the throwaway capture/prototype: it answers RPCs so we can learn the contracts. As each contract is verified it is ported to the Rust service that owns it. The Python code is not the product.
