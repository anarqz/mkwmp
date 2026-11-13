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

## Crates

```
rust/
  contracts/   generated prost types + tonic stubs (build.rs over ../contracts)
  seat/        seat server binary
  lobby/       lobby manager binary
  gateway/     TLS/h2 front, routing
```

SOLID in practice: each service depends on the contract crate (the abstraction), not on the others. Matchmaking/seat state sit behind traits so the in-memory impl (today) can be swapped for a distributed one without touching handlers. One handler per RPC.

## Today vs. target

The Python server (`server/mkwmp_server.py`) is the throwaway capture/prototype: it answers RPCs so we can learn the contracts. As each contract is verified it is ported to the Rust service that owns it. The Python code is not the product.
