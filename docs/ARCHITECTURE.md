# Architecture

There is **one server binary, `mkwmp`, with two parts**. Both run on one machine by default, and anyone can host one. The source of truth for each interface:

- `contracts/*.proto`: NPLN, the game-facing protocol.
- `docs/contracts/SEAT_API.md`: the seat API (ours).
- `docs/contracts/LAUNCHER_API.md`: the API between the launcher and the game patch.

```
 player PC                                   the server
 ┌──────────────────┐   HTTP(S) seat API   ┌───────────────────────────────┐
 │ mkwmp-launcher   │ ───────────────────► │ mkwmp server  (gamertags,     │
 │  gamertag + PIN  │ ◄─ queue / ticket ── │   friends, seats, tickets)    │
 │  friends, tray   │                      │   SQLite registry             │
 │  writes mkwmp.toml + lobby cert         │        ▲ roster (localhost)   │
 │  launches mkw.exe│                      │        │                      │
 │   └ winmm patch ─┼── NPLN gRPC/TLS ────►│ mkwmp lobby   (matchmaking,   │
 │                  │◄── Pia UDP / TURN ──►│   sessions, Gamesync, TURN)   │
 └──────────────────┘                      └───────────────────────────────┘
```

    mkwmp host   [--public-ip IP ...]   both parts in one process; sets itself up on first run
    mkwmp server [config/server.toml]   gamertags, friends, seats, queue, tickets (HTTP)
    mkwmp lobby  [config/server.toml]   the game-facing lobby
    mkwmp keygen [kid]                  new ticket signing key
    mkwmp admin  <list|register|reset-pin|release> [name]

## The seat server (`mkwmp server`, crate `rust/seat`)

- **Gamertags.** A player registers a gamertag and gets a 6-digit PIN. Gamertags, PIN hashes (Argon2) and friendships are stored in SQLite (`[registry] file`). Unused gamertags expire after `expire_days`.
- **Friends.** Requests are accepted or declined, then stored on the server. Removing a friend, or deleting a gamertag, removes the friendship on both sides.
  - A friend is **playing** (seated), **online** (launcher running: a *presence* signed in with the PIN) or **offline**.
  - The launcher long-polls `GET /seat/v1/friends` for changes.
- **Seats.** There are `capacity` seats (default 50). PLAY takes one, or joins a FIFO queue.
  - A seated player receives a **signed ticket** (Ed25519).
  - The launcher heartbeats to keep its seat; silence frees the seat for the next player in line.
  - Seats are in memory. After a restart the `bootId` changes and launchers rejoin.
- **Status.** `GET /seat/v1/status` is public. It shows seats, the queue and live lobbies, plus the lobby endpoint and the lobby's certificate (`lobbyCa`) for the launcher.
- **Rate limits** cover every route (per IP, per session or per token). Every 429 carries `Retry-After`.

## The lobby (`mkwmp lobby`, crates `rust/lobby` + `rust/gateway`)

This is the game-facing side: NPLN Auth, Matchmaker, GameSessionService and Gamesync over gRPC/HTTP2/TLS, plus Pia NAT checks and a TURN relay over UDP.

- **Sign-in.** It accepts a game only with a seat ticket that verifies against the seat server's public key (`require_ticket`).
- **Roster.** Every `roster_secs` it asks the seat server which seats are still live, and drops players whose seat is gone. Friends for the game's ONLINE → FRIENDS menu come from the same call.
- **Lobbies are unlimited.** Each matchmaking mode opens a lobby as players arrive (up to `capacity` per lobby), starts it after `gather_secs` with at least `min_start` players, and closes it when empty.
- **Limits.** `rust/gateway/src/limits.rs` caps connections, streams, TURN allocations and bandwidth, and NAT replies.

**Why the ticket is signed** even on one machine: the lobby holds only the public key. The two parts can be split across machines, or a lobby can be run by someone else, without that lobby being able to create seats.

**TLS trust.** The game verifies the lobby's certificate and host name, and the patch supplies the trust root:
- From rc.2, that root is the certificate the server publishes as `lobbyCa`, which the launcher writes to `<game>/mkwmp-lobby-ca.pem`.
- Without that file, the patch trusts its built-in certificate.

A self-hosted lobby uses a self-signed certificate for the host names the game asks for; `mkwmp host` creates one on its first run.

**Networking:**
- The seat API (`[seats] bind`, HTTP) runs directly, or behind a TLS reverse proxy or a Cloudflare tunnel.
- NPLN TLS (8905), Gamesync (8906), and UDP 10025 (Pia NAT), 3478 (TURN) and the relay range must be open directly on the public IP. A tunnel cannot carry the game's UDP.

## The launcher (`mkwmp-launcher`, crate `rust/client`)

1. It reads `client.toml`: server URL, gamertag, PIN, game folder and background settings.
2. With the window open, or from the tray, it signs in a *presence* and follows friends. It never takes a seat this way.
3. On PLAY it updates itself and the game patch from GitHub releases (signed `SHA256SUMS`), then takes a seat or queues.
   - It writes `<game>/mkwmp.toml` (lobby endpoint, gamertag, ticket, its local API address) and `<game>/mkwmp-lobby-ca.pem`, then launches `mkw.exe`.
4. It heartbeats until the game exits or the player presses STOP, then frees the seat.

The game patch (`native/probe`, `winmm.dll`) stays dormant until the player opens Online Play. It then redirects the game's online services to the lobby and asks the launcher's local API (`127.0.0.1:47820`) for its seat and friends.

## Crates

```
rust/
  mkwmp/       the server binary: host, server, lobby, keygen, admin
  seat/        seat server: registry (SQLite), friends, presence, roster, HTTP API
  lobby/       lobby core: matchmaking, sessions, documents, referee, friend rooms
  gateway/     gRPC/HTTP2 + TLS front, TURN relay, NAT responder, rate limits
  ticket/      ticket v1 sign/verify
  contracts/   generated prost types for the NPLN protos
  config/      typed server config loader
  client/      the launcher (binary `mkwmp-launcher`)
```

## Config

- **`config/server.toml`** is the server side. Relative paths resolve against the file.
  - `[seats]`: capacity, bind, timeouts, and the `lobby_endpoint` handed to players.
  - `[registry]`: the SQLite file and reserved names.
  - `[ticket]`: the signing key and trusted public keys.
  - `[lobby]` and `[lobby.policy]`: ports, the relay range, the roster, and the lobby rules.
  - `[tls]`: the lobby's certificate pair.
- **`config/client.toml`** is per player. The launcher window edits it.
