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

### Captured live 2026-11-13 (both clients reached free roam)

With the Gamesync TLS cert fixed (SNI, see below), the handshake succeeds and:

- **IssueToken request** `{1: "userSessions/<user-id>", 2: {...}}`. Our 139-byte
  Token reply (auth Token shape) is accepted.
- **KeepUserSession** (bidi) opens; the client sends one subscription frame:
  ```
  { 1: "userSessions/current",
    3 { 1 { 1: "userSessions/current/targets/1",
            2 { 1: "docs/__us/<user-id>" } } } }
  ```
  i.e. it **subscribes to the document `docs/__us/<user-id>`** and waits for the
  server to push it back on the stream. That doc is the P2P descriptor (fields
  `gsid`/`addr`/`p`/`mcn`/`maxu`/`rs`, parser FUN_01267698). Both clients then
  sit in free roam ("A PROCURAR JOGADORES") with no error, waiting for it.

**TLS SNI gotcha:** a single-label Gamesync host (e.g. `localhost`) gets
`.localdomain` appended for the TLS SNI (`localhost.localdomain`), which the
capture cert did not cover → silent cert rejection → "failed to connect to all
addresses". Fix: advertise a dotted FQDN in GameSession field 8
(`gamesync.npln.nintendo.net`, already a cert SAN); connects are redirected to
loopback regardless.

**Next:** stream a KeepUserSession response carrying the `docs/__us/<id>`
document so "get my UserSession" completes and the game advances toward P2P.

What is known about the document:
- `docs/__us/<id>` body is a **map<string, Value>** (accessor `FUN_0126661c`
  delegates to a key lookup `FUN_00f0873c`; Value is the ugcstore Value shape:
  int=3, double=5, string=7). Keys (parser `FUN_01267698`): `gsid` (string),
  `addr` (string), `p` (u16 port), `mcn` (string), `maxu` (int), `rs` (list).
  The game side (`FUN_022364b0`) then reads a child key `f`, and `ipc`/`dpc`.
- The game subscribes to **its own** `__us/<id>` (matches the IssueToken user)
  and blocks in "get my UserSession" until the server returns it. So the first
  goal is just to return a well-formed own-doc, not yet peer info.

### KeepUserSession response envelope — RECOVERED (2026-11-13)

From the serializers:
- **Snapshot** (response, main+0x12a6020): `{1: repeated DocumentChange, 2: Timestamp read_time}`.
- **DocumentChange** (main+0x12b0430): `{1: Document, 2: string name, 3: msg}`.
- **Document** (main+0x12af860): `{1: name, 2: fields}` (ugcstore-style).

Sending `Snapshot{1: DocumentChange{1: Document{1:name,2:fields}}, 2: read_time}`
with a flat fields map (gsid/addr/p/mcn/maxu) got the game to process
`companion_state` (main+0x1266e24) — further than ever — then still error with
**0x240141 (2321-4608)** and "Timed out to get my UserSession". So the envelope
is accepted but the **document content** is rejected.

### Document content — must be a nested Struct with child `f`

The game-side consumer `FUN_022364b0` looks up child key **`f`** (string from
`FUN_012679cc`) in the doc via `FUN_02252df8`, validates it (`FUN_01267820`),
then parses THAT with `FUN_01267698` (gsid/addr/p/mcn/maxu/rs) and reads
`ipc`/`dpc` (`FUN_012679d8`). So the doc is a **nested key→Value tree**: the
top object has key `f` → a sub-struct holding the peer descriptor. The gamesync
Value type therefore supports a nested struct (richer than the ugcstore
int/double/string Value); its exact field numbers for struct/list are TBD.

### Value/Struct recovered (2026-11-13)

Value serializer `main+0xf19724` is a **oneof** (discriminator = field number):
`3` int (varint), `5` double (fixed64), `7` string, `6` struct, `10` list.
Struct = `{1: repeated Entry{1:key, 2:Value}}` (same key/Value encoding proven
by ugcstore BulkGetDocuments). gamesync **Document** `main+0x12af860` =
`{1: Value body, 2: name}` (field 1 is a Value, NOT name-first — unlike
ugcstore). So the `__us` body is a Value-struct; `server/mkwmp_server.py`
`keep_user_session_response` now builds `Document{1: Value-struct{"f": struct{
gsid,addr,p,mcn,maxu,ipc,dpc}}, 2: name}` wrapped in the Snapshot envelope.

### Status: stuck at 0x240141, content-invariant

Sending the Snapshot response reaches `companion_state` (main+0x1266e24) but
always errors `0x240141 (2321-4608)` regardless of document content (flat vs
nested `f`). Two live hypotheses:
1. **Wrong response TYPE/framing** for the KeepUserSession bidi stream — the
   client's reader can't parse what we send (INTERNAL). The exact response type
   is not yet identified; the stub (main+0x12bae9c, slot 0x118) and the client
   reader are obfuscated/indirect. "Snapshot" (0x12a6020) may actually be the
   ReadDocuments/ListDocuments batch type, not KeepUserSession's.
2. **Companion/peer requirement** — the doc parses, but the game needs the
   OTHER player's user-session to exist before "my UserSession" resolves,
   which ties into the Pia P2P mesh (both peers must actually connect).

### KeepUserSession contract — SOLVED by live probing (2026-11-13, runs p1–p5b)

Method: per-player probe sequences on the open stream (`--kus-probe`), 5–8 s
apart; the client's error timestamp identifies the probe that killed it.
Protobuf skips wrong-wire-type fields, so the oracles were **invalid UTF-8**
(kills only a string field) and **valid-UTF-8 non-message bytes** like `é`
(kills only a message field).

Result: the stream uses **class 8 in both directions**:
`SessionMessage {1 string session, 2 Change, 3 repeated Op}`;
`Op {1 bytes = serialized Target{1 name, 2 {1 document}}, 2 varint, 3 Status}`;
`Change {1 string target, 2 varint kind, 3 Document}`;
`Document {1 name, 2 fields, 3 create_time, 4 update_time}` (class 11).
Change **kind 3 = CURRENT completes the listen** (P2 then entered the
user-session handler, `main+0x1266824/0x1266890`); kinds 1/2 only deliver the
document (`main+0x12a095c` parses its name). Earlier failures (0x240141) were
our documents landing in field 1, a proto3 string, with non-UTF-8 bytes.
Contract: `contracts/nn/npln/gamesync/v1/gamesync.proto`; server answers each
subscribed target with ADDED + CURRENT.

Also: `MKW_ASSUME_FOCUS=1` keeps background windows running (focus ruled out
as a cause); two instances need `MKW_RESOLUTION=960x540 MKW_FPS=30` or the GPU
resets (`vkQueueSubmit2 ... -4`). `tools/arrange_windows.ps1` docks P1/P2.

### Gamesync documents (2026-11-13, runs d1–d4)

The server authors these; the client only reads them (no writer references).
Doc names arrive as `docs/<path>`; the client strips `docs/` (`0x12a095c`).

| Document | Fields | Parser / user |
|---|---|---|
| `__us/<user-id>` user session | `uid` str, `ussid`/`ucsid`/`upcsid` int32, `pgn` str (P2P group) | `FUN_0128b2b4` |
| `__gs/f` game-session fixed data | `gsid`, `addr`, `p` u16, `mcn`, `maxu`, `rs` list | `FUN_01267698`; name checked by `FUN_01267820` vs `"__gs"+"/"+"f"` (`FUN_01267910`); game copies `mcn` (`FUN_01267750`) |
| `__pgn/<pgn>/__pus/…` group user sessions (inferred path) | `suid`, `susid` str, `sussid`, `suscid` int, `pl` list, `mp` map, `__stg` | `FUN_0128bc00`; path `FUN_0128b4dc` |
| `__pgn/<pgn>/__stu…` (inferred) | ? | path `FUN_0128bf1c` |

Server sends, per `__us` subscription on target 1: ADDED `__us/<id>`, ADDED
`__gs/f` (shared per lobby), CURRENT. Live: both clients resolve their user
session (`0x1266824/0x1266890`), and the game's session manager reads `__gs/f`
(`caller=main+0x2232780`). No error.

**Still waiting:** the matchmaking operation stays pending (`prepare op=3`,
never `complete`), "A procurar jogadores". No new subscription, RPC or Pia
traffic. The Pia mesh jobs (AttachMeshJob `0x162e000–0x1632000`, JoinMeshJob
`0x1576000–`) never start after resolve; they are driven by Pia's session layer
`0x1626000–0x162e000`, called from the game at `0x2235000–0x223a000`.
`TrackAutomatch`/`TrackCreateSession`/`TrackJoinSession` there are telemetry.

### Remaining to a race (large, multi-cycle)
1. ~~Identify the real KeepUserSession response type~~ done. ~~User session and
   fixed data~~ resolving. Next: what starts the mesh. Leading hypothesis: the
   server must also push the group documents (`__pgn/<pgn>/__pus…`,
   `__pgn/<pgn>/__stu…`) listing the members/stations; recover their exact
   paths and fields from `FUN_0128b4dc`/`FUN_0128bf1c`/`FUN_0128bc00` callers.
2. Populate each player's doc so the OTHER learns its Pia addr/port.
3. Pia P2P mesh over loopback — exchange game packets through the bridge
   (currently only echoes NAT probes). A whole subsystem.
4. Session sync + race start.

### All Gamesync message classes (vtables)

`tools/re/pb_vtables.py` finds 35 message vtables at `0x30f1cd0 + 0x80*i`
(i = 0..32) plus `0x30f2d50`, `0x30f2e98`; `_InternalSerialize` is slot 12
(`vtable+0x60`). Shapes from `tools/re/pb_serializers.py` (heuristic: misses
fields written in repeated loops; "msg" = length-delimited non-string):

| # | Serializer | Fields |
|---|---|---|
| 0 | `0x12a1ee0` | 1 str, 2 str (IssueTokenRequest) |
| 1 | `0x12a26fc` | 1 Token (IssueTokenResponse) |
| 2 | `0x12a3120` | 1 str, 2 str (RefreshTokenRequest) |
| 3 | `0x12a393c` | 1 Token (RefreshTokenResponse) |
| 4 | `0x12a4510` | 1 str, 2 msg, 3 str (GetDocumentRequest) |
| 5 | `0x12a5420` | none detected |
| 6 | `0x12a6020` | 1 repeated #18, 2 Timestamp |
| 7 | `0x12a6f98` | 1 str, 2 str, 3 msg, 4 msg #10 |
| 8 | `0x12a8064` | 1 str, 2 msg, 3 msg #31 |
| 9 | `0x12a8bfc` | 1 msg |
| 10 | `0x12a9198` | 1 str |
| 11 | `0x12ab0e8` | 1 str, 2 msg, 3 msg, 4 msg |
| 12 | `0x12abfa0` | 1 str |
| 13 | `0x12ace70` | 1 str, 2 msg, 3 msg #29 |
| 14 | `0x12ae3a4` | 1 msg, 2 msg, 3 msg, 4 msg #22 |
| 15–17 | `0x12aef20`, `0x12af280`, `0x12af860` | none detected |
| 18 | `0x12b0430` | 1 msg, 2 str, 3 msg |
| 19 | `0x12b137c` | 1 msg, 2 msg (`0x12012a0`), 3 msg |
| 20 | `0x12b1dbc` | 1 msg |
| 21 | `0x12b26a0` | 1 str |
| 22 | `0x12b2f60` | 1 str, 2 msg |
| 23 | `0x12b38dc` | 1 varint, 2 Timestamp |
| 24 | `0x12b435c` | 1 str, 2 varint, 3–5 msg, 6–8 str |
| 25 | `0x12b58a4` | 1 msg, 2 msg #27 |
| 26 | `0x12b625c` | 1 msg |
| 27 | `0x12b68f8` | 1 str |
| 28, 29 | `0x12b6f40`, `0x12b7558` | 1 str |
| 30 | `0x12b7d00` | 1 str, 2 varint, 3 msg |
| 31 | `0x12b8840` | 1 str, 2 varint, 3 msg |
| 32 | `0x12b94c4` | Token: 1 str, 2 str, 3 str, 4 Duration |
| 33, 34 | `0x12bf498`, `0x12bc0c0` | none detected |

Use this to match captured KeepUserSession frames by shape; then read the
matching class's parser (vtable neighbours) for field semantics.

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
| 21:46 b | 8906 sniffs TLS/h2c; IssueToken answered; KeepUserSession held open | TLS SNI localhost.localdomain rejected → 2321-4992 |
| 2026-11-13 08:05 | Gamesync host = gamesync.npln.nintendo.net (cert SAN) | **both clients in free roam**; Gamesync auth OK; KeepUserSession __us subscription captured |
| next | stream the `docs/__us/<id>` UserSession document on KeepUserSession | pending |

## Live tracing (probe)

`MKWMP_TRACE_EDGES=1` makes the probe log, via the runtime's `rt_dispatch`:

- `edge t=<ms> caller=main+X target=main+Y x0 x1 x2` — first occurrence of
  each indirect call from `main+0x1200000..0x1440000`;
- `sdk-msg t=<ms> caller=main+X "<text>"` — strings constructed by
  `main+0x46966a0` from that range (error messages, resource names).

Logs rotate: `logs/mkw.log` is the current run, `mkw.1.log` the previous.
Grep for `controller event kind=2` to find a failure and read the strings
before it.
