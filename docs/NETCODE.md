# MKWorld netcode flow (NPLN matchmaking → game session → Gamesync/Pia)

Living reference for the online flow as recovered from `scratch/main.bin`
(raw ARM64 image, base 0; live guest base `0x7000000000`). Addresses are
image offsets. "Verified" = observed live; otherwise derived from code.

## 1. Result codes: `2321-xxxx` are gRPC status codes

`main+0xefcb40` converts a finished call's `grpc::Status` into an nn::Result
(module `0x141` = 321). It first honours an override from trailing metadata
(a decimal description parsed with strtol and added as `n << 9`); otherwise
it indexes the table at `main+0x292f9bc` by status code:

    result = (code == 0) ? 0 : 0x141 | ((code - 1) * 3) << 16
    description = (code - 1) * 384

| Error shown | Result     | gRPC status          |
|-------------|------------|----------------------|
| 2321-3072   | `0x180141` | 9 FAILED_PRECONDITION |
| 2321-4992   | `0x270141` | 14 UNAVAILABLE        |

The NPLN SDK reuses these codes for its own local errors, so a 2321 error
does **not** mean the server returned that status.

## 2. Matchmaking (verified live)

1. `Matchmaker/CreateMatchmakingTicket` — request field 2 = ticket
   `{2: tenants/current/matchmakingConfigs/Normal24, 4: Player}`. Reply must
   carry field 1 `tenants/<tenant>/matchmakingTickets/<uuid>` (an empty reply
   aborts at `main+0x13ea94c`).
2. `Matchmaker/TrackMatchmakingTicket` — server stream; keep it open while
   searching. Client gives up after ~150 s.

### Track reply schema (serializer `main+0x1408a40`)

| Field | Type | Notes |
|---|---|---|
| 1 | string | ticket resource |
| 2 | string | config resource |
| 3 | msg `{1 str, 2 str, 3 varint}` (`0x140a0d8`) | unknown |
| 4 | repeated Player | |
| 5 | varint | state; **3 = session found** |
| 6 | repeated MatchedPlayer (`0x140c708`) | consumed by the join |
| 7 | GameSession | |

- **Player** (`0x140d68c`): 1 user resource, 2 attributes (`{1 entry{1 key, 2 Value}}`), 3 map, 4 string.
- **MatchedPlayer** (`0x140c708`): 1 Player, 2 userSession resource
  `tenants/<t>/gameSessions/<g>/userSessions/<u>`, 3 string (see §4).
- **GameSession** (`0x140b440`): 1 name, 2/3 varint, 4/5 bool, 6 string
  (`"*"` sets a flag), 7 varint, 8 string, 9 varint, 10 Timestamp, 11 attributes,
  12 repeated Member.
- **Member** (`0x1405fec`): 1 **string** user, 2 string, 3 map, 4 Timestamp,
  5 varint, 6 string, 7 attributes.

### Track consumer (`main+0x138fea0`)

After `Finish()`: status 0 → switch on field 5 (3 → convert session via
`0x13dbf00`, success). Status ≠ 0 and not retryable: status 1 (CANCELLED) is
reported as UNAVAILABLE "Deadline exceeded while matchmaking. See
MatchmakingClient::GetMatchmakingExpireDuration()"; other codes pass through.
Other messages: "Unknown error.", "The request has been timed out.",
"The request has been cancelled.", "The match has been declined."

## 3. Session join (`FUN_013b7d20`)

Gets the session (cached from tracking, else an RPC via `0x13b3328`), then
`0x138dea0` checks each requested local user against the converted
MatchedPlayer list (`session+0x58`, 0xa8-byte entries built by `0x13dbda0`):

- entry key = Player field 1, parsed by `0xf183d0` (compared with `0x1393740`);
- entry `+0x68` = parsed MatchedPlayer field 2 (userSession) — must be non-empty;
- entry `+0x88` = raw MatchedPlayer field 3.

Any miss → FAILED_PRECONDITION "The game session was found but the user's
connection info not found…" (**this was 2321-3072 on the earlier runs**,
which lacked MatchedPlayer field 2 and sent a nested message in Member field 1).

## 4. Gamesync (P2P setup) — current frontier

### Endpoint (verified live 2026-11-12 21:46)

The client takes the Gamesync server from **GameSession field 8 (host) and
field 9 (port, u16)** — copied by the session converter `0x13dc0c0`. The
channel builder `FUN_012696a4` formats `host + ":%u"`; host `localhost` or
`localhost.localdomain` is meant to select an insecure channel, anything else
TLS with authority `gamesync.npln.nintendo.net`. With both fields empty the
trace showed `DNS resolution failed for service: :0 (Gamesync Auth RPC
result)` → "Timed out to get my UserSession." → **2321-4992**.

With `localhost:8906` advertised, both clients connected to 8906 right after
the assignment but did not send an h2c preface (likely TLS; server now sniffs
and accepts both), retried for ~10 s, then returned to the menu.

### Flow (static, `0x1250000..0x12c0000`)

1. `Gamesync/IssueToken` (unary). Interceptor `FUN_01297258` adds
   `authorization: bearer <token>` to other calls; `FUN_012972fc` exempts
   IssueToken/RefreshToken. IssueToken has a retry policy (5 attempts,
   UNAVAILABLE retryable).
2. `FUN_0126c3b8` builds a document reference `"__us/" + MatchedPlayer.field3`
   (`0x1297914`, `0x125e250`) and **registers a snapshot listener**
   (`0x125fe30` → `0x12707c0`, two callbacks), then waits (`0x126dc00`) for a
   flag set by the listener. Not set → UNAVAILABLE "Timed out to get my
   UserSession." (`main+0x126c6bc`); also "Failed to get my UserSession."
3. Listener data presumably arrives over **KeepUserSession** (bidi). Trace
   strings: `userSessions/current`, `userSessions/current/targets/1`.
4. The user-session/P2P document is parsed by `FUN_01267698` with keys
   `gsid` (string), `addr` (string), `p` (u16), `mcn` (string), `maxu` (int),
   `rs` (list) — a server/relay descriptor. Game side (`0x22364b0`) looks up
   child key `f`, validates it (`0x1267820`), parses it with the same function;
   `0x12679d8` reads `ipc`/`dpc`. Other Gamesync keys seen: `ussid`, `ucsid`,
   `upcsid`, `suid`, `susid`, `sussid`, `suscid`, `__pgn/`, `/__pus`, `__stg`,
   `__stu`, `__upcsidn`.

### Gamesync stub (`0x12bae9c`) and schemas

RpcMethod table (name, type): IssueToken +0x28, RefreshToken +0x40,
GetDocument +0x58, ReadDocuments +0x70, ListDocuments +0x88,
QueryCollectionIds +0xa0, WriteDocuments +0xb8, BeginTransaction +0xd0,
CommitTransaction +0xe8, RollbackTransaction +0x100, **KeepUserSession +0x118
(type 3 = bidi)**, CreateRound +0x130. The API mirrors Firestore.

Recovered with `tools/re/pb_serializers.py` (declaration order; field types
from tag bytes):

| Serializer | Shape | Identification |
|---|---|---|
| `0x12a1ee0` | 1 str, 2 str | IssueTokenRequest |
| `0x12a26fc` | 1 Token | IssueTokenResponse |
| `0x12b94c4` | 1 str, 2 str, 3 str, 4 Duration (`0x11b2768`) | Token (same as Auth) |
| `0x12a3120` / `0x12a393c` | as above | RefreshToken req/resp |
| `0x12a4510` | 1 name, 2 msg (`0x12012a0`, mask?), 3 str (transaction) | GetDocumentRequest |
| `0x12a6020` | 1 repeated `0x12b0430`, 2 Timestamp | batch read response? |
| `0x12b0430` | 1 msg, 2 str, 3 msg | found/missing/read_time-like |

The server answers IssueToken/RefreshToken with a Token and holds
KeepUserSession open, logging each client frame.

Gamesync request types beyond these, and the KeepUserSession response
schema, are not recovered yet: capture the client frames first.

Gamesync RPCs present in the binary: IssueToken, RefreshToken,
KeepUserSession, GetDocument, ReadDocuments, ListDocuments, WriteDocuments,
Begin/Commit/RollbackTransaction, CreateRound, QueryCollectionIds. Related
strings: "(Gamesync Auth RPC result)", "Failed to establish P2P session.
Disconnected from the Gamesync server during initial sequence 2.",
`gamesync.npln.nintendo.net`, `P2pPluginInterface`. GameSessionService also
offers JoinGameSession, GetUserSession, ListUserSessions, AllocateIceServerSet.

## 5. Pia NAT probe (verified live)

UDP 16-byte probes (four big-endian words) to `nncs1/nncs2-%.n.n.srv.nintendo.net:10025`.
Reply types 101–103 with type, sender port, 127.0.0.1, 0. Parser
`main+0x1624314`. The local address gate (`main+0x150dc04`) needs
`GetCurrentIpConfigInfo` to return 127.0.0.1 (the probe supplies it).

## Experiment log

| When (2026-11-12) | Assignment sent | Result |
|---|---|---|
| 19:xx–20:59 | name only; + roster; + field 6 `{1:{1:user}}` | 2321-3072 both (precondition) |
| 21:14 | + MatchedPlayer field 2 userSession | 2321-4992 both, no new RPC |
| 21:29 | typed: own Player in 4, MatchedPlayer {Player, userSession, id}, Member field 1 string, Timestamps | 2321-4992 both, immediate |
| 21:32–21:38 | same, with edge + string tracing | trace: Gamesync target `:0`, IssueToken DNS failure |
| 21:46 | + session 8/9 = `localhost:8906` | clients connect to 8906 (not h2c), ~10 s of retries, menu |
| next | 8906 sniffs TLS/h2c; IssueToken answered; KeepUserSession held open | pending |

## Live tracing (probe)

`MKWMP_TRACE_EDGES=1` makes the probe log, via the runtime's `rt_dispatch`:

- `edge t=<ms> caller=main+X target=main+Y x0 x1 x2` — first occurrence of
  each indirect call from `main+0x1200000..0x1440000`;
- `sdk-msg t=<ms> caller=main+X "<text>"` — strings constructed by
  `main+0x46966a0` from that range (error messages, resource names).

Logs rotate: `logs/mkw.log` is the current run, `mkw.1.log` the previous.
Grep for `controller event kind=2` to find a failure and read the strings
before it.
