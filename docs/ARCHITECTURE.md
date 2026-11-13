# Architecture

Two small Rust services plus the local probe. Contracts in `contracts/*.proto` are the single source of truth; both services generate code from them (`prost`/`tonic`).

```
 game client (winmm probe)            game client
        │  NPLN gRPC/TLS                     │
        ▼                                    ▼
 ┌─────────────────────────── gateway ───────────────────────────┐
 │  terminates TLS+h2, routes NPLN methods to the services        │
 └───────────────┬───────────────────────────┬───────────────────┘
                 │                             │
        ┌────────▼────────┐          ┌─────────▼─────────┐
        │  seat server    │          │  lobby manager    │
        │  (one, fixed)   │          │  (scales 0..N)    │
        │  username+ticket│          │  match + session  │
        │  seat limit     │          │  Gamesync + Pia    │
        └─────────────────┘          └───────────────────┘
```

## Services

**Seat server** — one instance. Owns the roster: a player picks a username, takes one of a limited number of seats, and gets a ticket (bearer token). Maps NPLN Auth. No accounts, no persistence beyond process memory. Hands matchmaking tickets to the lobby manager.

**Lobby manager** — stateless workers, scaled by demand. Each owns a set of lobbies: pairs tickets into a `GameSession`, issues `userSessions`, then runs the session's Gamesync document store and brokers Pia peer connections. Maps NPLN Matchmaker, GameSessionService and Gamesync. A worker holding no lobbies can exit.

The split matches NPLN: Auth is global (seat server); matchmaking/session/gamesync are per-match (lobby manager). The gateway is the only TLS endpoint; services speak plain gRPC behind it.

## Client

A small Rust binary (`rust/client`), run by the player. It reads `client.toml`
(server, username, game dir), connects to the seat server's `Seat/JoinQueue`,
and prints queue status ("3rd in queue"). When a seat is granted it writes
`<game>\mkwmp.toml` (NPLN endpoint + name + the granted **ticket**) and
launches `mkw.exe`, holding the gRPC stream — and thus the seat — until the
game exits. This is why there is no in-game overlay for the queue: the wait
happens in the client, before the game starts. The probe (DLL) still reads its
own config directly; the client just generates it.

`Seat/JoinQueue` (`contracts/mkwmp/seat/v1`) is our own internal protocol, not
an NPLN/game contract.

## Crates

```
rust/
  contracts/   generated prost types + tonic stubs (build.rs over ../contracts)
  config/      typed server config loader
  seat/        seat server: lib (manager + Seat service) + thin binary
  lobby/       lobby manager binary
  gateway/     TLS/h2 front, routing
  client/      player client: queue UI + game launcher
```

SOLID in practice: each service depends on the contract crate (the abstraction), not on the others. Matchmaking/seat state sit behind traits so the in-memory impl (today) can be swapped for a distributed one without touching handlers. One handler per RPC.

## Config

- `config/server.toml` — server side: instance name, NPLN bind, **seat capacity (N)**, lobby bind/size/scaling, TLS cert+key. Loaded by `seat` and `lobby` (`config::ServerConfig::load`); relative paths resolve against the file.
- `config/client.toml` — per player: server address, username, optional loopback slot. Copied to `<game dir>\mkwmp.toml`; the probe (winmm.dll) reads it directly at startup. No launcher. If absent, the game runs stock/offline. Parsed in C (`native/probe/src/config.c`); env vars (`MKWMP_HOST/PORT/PLAYER/ENABLE`) still override for testing.

Flow the config drives: a connection joins the active-user pool → if a seat is free it gets a ticket → an in-game matchmaking request is validated against the ticket → access to the lobby/world service. All state is in-memory and dies with the process — no database.

## Today vs. target

The Python server (`server/mkwmp_server.py`) is the throwaway capture/prototype: it answers RPCs so we can learn the contracts. As each contract is verified it is ported to the Rust service that owns it. The Python code is not the product.
