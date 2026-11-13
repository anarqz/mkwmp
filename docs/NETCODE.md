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

`FUN_0126c3b8` takes the current participant's MatchedPlayer field 3 and
builds the Gamesync document key `"__us/" + field3` (`0x1297914`), then reads
it via the Gamesync client. Failure → UNAVAILABLE "Timed out to get my
UserSession." (`main+0x126c6bc`). With field 2 present but field 3 empty the
run of 2026-11-12 21:14 failed with **2321-4992** on both clients and sent no
further RPC.

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
| next | typed: own Player in 4, MatchedPlayer {Player, userSession, id}, Member field 1 string, Timestamps | pending |
