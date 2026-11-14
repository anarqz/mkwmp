# Multi-lobby support: what the client does and how the server should do it

Static analysis of `scratch/main.bin` (image offsets) plus the server logs `scratch/run-lan9..11.log`.
Nothing was run live. Date: 2026-11-13. Builds on `docs/RE-MAP.md`. Work files:
`C:\Users\alcmo\.claude\jobs\7b106074\tmp\re-map2\` (`b1.c`, `b2.c` decompiles, `vc*.py` scans).

Marks: **V** = VERIFIED (read in code/data at the address given, or in a log line), **I** = INFERRED.

## Summary

- **Scoping.** Nothing in a Gamesync request names the game session. Document names are always
  `docs/<path>`, the session string is always `userSessions/current`, and every lobby uses the same
  P2P group name `All`. The only per-player inputs are the `IssueToken` request and the bearer token
  on later calls. So the server must map **token -> seat -> lobby** and keep one document store and
  one listener list per lobby. No change to what we send the client is needed for this.
- **Joining a session that already has members** is supported by the SDK layers that were read
  (`__us` member list, `__pus` peer list, Pia join and host migration all handle changes at any
  time). Whether the *game sequence* accepts a player who arrives during a vote or a race was not
  established. The client has an "open / close participation" call that writes `__gs/m.cp`, but it
  was never issued in 11 logged runs and its callers were not found.
- **Recommended rule.** A lobby takes new players only before its first event starts; a player who
  searches while every lobby of that mode is full or busy gets a new lobby. Lobbies are created on
  demand and dropped when empty; there is no pool and no cap on their number. Later joins (between
  races) are a second step gated on two live experiments.
- **Capacity** is 24 players per lobby for Normal24 / Battle24 / Survival24 / Private24. We send
  `maxu = 2` today; it must become the lobby capacity.

Things that need a live check before relying on them are listed in section 5.

---

## 1. Document scoping

### 1.1 What the client sends

| Item | Value on the wire | Mark |
|---|---|---|
| Gamesync endpoint | host = GameSession field 8, port = field 9, taken from the match assignment; a new TLS connection is opened to it right after the assignment (`run-lan11.log` 256-258) | V |
| `:authority` | the field-8 host (`authority=gamesync.npln.nintendo.net` on every Gamesync call in the logs) | V |
| `IssueToken` request | `{1: "userSessions/<id>", 2: "<id>"}` (`run-lan11.log` 259-275). Builder `FUN_0128cb70`: field 1 = `"userSessions" + "/" + s0` (`FUN_012a0c70`), field 2 = `s1`, where `s0`/`s1` are two strings 0x20 apart in the connection info | V |
| Source of `s0` / `s1` | the same struct holds the parsed MatchedPlayer field 2 at +0x68 and the raw field 3 at +0x88 (`docs/NETCODE.md` section 3), so `s0` = last component of the userSession name and `s1` = MatchedPlayer field 3. Both are the user id today, so the log cannot tell them apart | I |
| Own user-session document | `docs/__us/<s1>`: `FUN_0126c3b8` builds it from the string at +0x88 (`FUN_01297914`), relative to the handle's base at `handle+0x10` (`FUN_0125e250`) | V |
| Bearer token | interceptor `FUN_01297258` adds `authorization: bearer <string at token+0x20>` to every Gamesync call except IssueToken / RefreshToken (`FUN_012972fc`) | V |
| Which token that is | the access token (field 2) of our `IssueToken` reply. The server stores the header but never logs it | I |
| Stream session string | always `userSessions/current` (literal `current` at `*0x30f0cc8`; every KeepUserSession frame in the logs) | V |
| Document names | `"docs" + "/" + path` (`FUN_012a0df0`, parser `FUN_012a0a20`). The string `gameSessions` is referenced only by the matchmaking parsers `0x13e9de0` / `0x13ead80`, never by Gamesync code | V |
| `handle+0x10` | the base every document reference is built from (`FUN_0126c3b8`, `FUN_02235f64`). It adds no session component: the names on the wire are `docs/PlayerStatic/1`, `docs/__gs/f`, ... | V (wire) |
| P2P group name | `All` for every lobby: `docs/__pgn/All/__pus/<uid>`, `docs/__pgn/All/__stu/<uid>`, `docs/__stg/All`, and the client writes `pgn: "All"` (`run-lan11.log` 303-339). The `pgn` we put in the `__us` document (the lobby id) is not used for these paths | V |

So two players in different lobbies send byte-identical document names. Real Gamesync can only tell
them apart by the token (**I**; follows from the table).

### 1.2 Options

| Option | Works? | Cost |
|---|---|---|
| Map bearer token -> seat -> lobby | yes, no client-visible change | per-lobby store in the server |
| Distinct host per lobby (field 8) | possible: the host is per assignment and arrives as `:authority` | every name needs DNS redirect and a certificate SAN; a single-label host breaks TLS SNI (NETCODE.md) |
| Distinct port per lobby (field 9) | possible | one listening port per lobby; firewall and hosting work |
| Path prefix | no: there is no session component in any document name | - |

Fields 8/9 remain useful later to spread lobbies over several Gamesync processes, but they are not
needed to scope documents.

### 1.3 Recommended scoping

1. A **seat** is one user in one lobby. A user has at most one live seat that is still waiting for its
   `IssueToken` (the one from its latest assignment).
2. `IssueToken {1: "userSessions/<user id>"}` -> bind to that pending seat and return an access token
   that contains the seat (for example `gs.<lobby id>.<user id>.<nonce>`); keep `token -> seat`.
3. Every other Gamesync call (`KeepUserSession`, `WriteDocuments`, `GetDocument`, `RefreshToken`)
   resolves `bearer -> seat -> lobby`. A `RefreshToken` reply must keep the same seat.
4. Fallback if a call has no known token: the seat bound on the same HTTP/2 connection by an earlier
   `IssueToken`. Do not rely on the connection alone: two local users (split screen) may share one.
5. Old streams keep their old token, so a user who was moved to a new lobby while an old stream is
   still closing stays correctly scoped to the old lobby until that stream ends.

This keeps MatchedPlayer and Member exactly as they are today. Giving each seat its own id in
MatchedPlayer field 3 would make the mapping independent of step 1, but it changes the own
`__us/<id>` document name and possibly the `uid` used in `__pus` / `__stu` paths, so it needs
experiment E0 first.

### 1.4 What is wrong in the server today for more than one lobby

| Place | Problem |
|---|---|
| `DOC_STORE`, `clear_match_documents()` | one global dict, wiped when a lobby forms: a new lobby destroys the running one's `PlayerStatic`, `@@RefereeResult`, ... |
| `H2Protocol.watchers`, `publish()` | global: a write is pushed to every listener of that name in every lobby. `__pgn/All/__pus/<uid>` of a new player would reach all lobbies and each client would try to connect to it |
| `docs/__pgn/`, `docs/__mt/` | kept across lobbies on purpose; must live in the lobby too |
| `lobby_of()` / `lobby_members()` | derived from tickets; the Gamesync connection has no `self.user`, and the Gamesync token is not in `issued_tokens`, so `GetDocument` can resolve to no lobby |
| `gamesync_token_response()` | token does not identify a seat |
| `REFEREE_ANSWERED`, `REFEREE_MATCHES` | global, keyed by request key |
| `lower_members_present()`, `pending` | ordered by the global player number, not by join order in the lobby |
| `game_session_fixed_document()` | `maxu = 2` |
| `LocalMatchmaking.cancel()` | cancelling one ticket of a started lobby deletes the lobby and the other tickets |

---

## 2. Joining a session that already exists

### 2.1 What the lower layers do when the member set changes

| Layer | Behaviour | Mark |
|---|---|---|
| Matchmaking | after the final Track message the stream is dropped; the GameSession is a snapshot that is never refreshed (RE-MAP section 3, 4.3 and 5). Existing members get nothing from matchmaking when someone joins | V |
| `__us` collection | callback `FUN_02254e4c`: a document that exists with `st == 2` is added as a member; a missing document (change kind 3) or `st != 2` erases it; `FUN_0049a5a0` then creates or drops the player node. Works at any time | V |
| `__pus` collection | handler `FUN_01289840` (vtable 0x30f0a70): a removed document is erased from the peer map and reported to the plugin (`vt+0x20`); an existing one is parsed (`FUN_0128b2b4`), accepted only if its `pgn` equals the local one, stored, and reported to the plugin. Works at any time, before or after the initial sync (`FUN_0128a378`) | V |
| Who starts the P2P handshake | in `run-lan11.log` user 1 was already synced, received user 2's `__pus` as a later ADDED push (line 911) and then wrote the first request into `__stu/<user 2>` (line 997); user 2 answered into `__stu/<user 1>` (line 1154). So the member that is already present initiates towards the newcomer | V (2 players) / I (general rule) |
| Pia mesh | `AttachMeshJob` reads `__gs/f` with `GetDocument` (`FUN_012805b4`), hashes `gsid` into the Pia session id (`FUN_0155c500`, `FUN_0162f594` 0x162f97c) and passes `rs` to the network object (`vt+400`). `FUN_01630b64` then chooses `CreateMesh` or `JoinMesh` (`FUN_01527334`). Job states `JoinMeshJob::SendJoinRequest`, `WaitNetworkHostMigration`, `WaitHostMigrationForCreateMesh`, `LeaveMeshWithHostMigrationJob`, `ProcessHostMigrationJob` exist | V |
| `mcn` | copied by the game from `__gs/f` (`FUN_02236468` -> `FUN_01267750`) and handed to the P2P start (`FUN_02236714` -> `FUN_01268248`). Read from the live `__gs` collection snapshot, so a pushed change is seen. With `mcn` = lowest-numbered member the 2-player mesh forms | V (calls, live result) / I (that `mcn` names the member who creates the mesh) |
| `maxu` | parsed into the descriptor (+0x70, `FUN_01267698`) and copied into the P2P plugin state (`FUN_01284278`). Its consumer was not traced | V / I (it is the mesh size limit) |

### 2.2 The participation flag (`__gs/m`)

- `__gs/m` fields (parser `FUN_012673cc`): `cp` bool, `ip` bool, `pw` string, `ebf` bool, `bfmin` int,
  `bfmax` int, `prp` map. **V**
- Session-manager vtable (base 0x325c5b8): slot +0x18 (`0x223c780` -> `FUN_022352cc`) returns
  "`__gs/m` exists and `cp` is false"; slot +0x78 (`0x223c788`) calls `FUN_02235f64(mgr, 0)`;
  slot +0x80 (`0x223c790`) calls `FUN_02235f64(mgr, 1)`. `FUN_02235f64` updates `__gs/m` setting
  `cp` (and `ip` / `ebf` when bits 0 / 1 of `mgr+0x1d10` are set) to that bool, waits for the write
  and posts an error (kind 0xd / 0xe) if it fails. `FUN_022377b4` (slot +0x98) calls slot +0x78 for
  every session slot. **V**
- Reading: `cp` = participation closed, so +0x78 opens and +0x80 closes the session to new players;
  `ebf` / `bfmin` / `bfmax` look like backfill settings. **I** (names only)
- No `__gs/m` write appears in any of `run-lan1..11.log` (two-player public Race and Battle lobbies,
  including a second race in `run-lan11.log`). Our server never authors `__gs/m`. **V**
- The callers of slots +0x78 / +0x80 were not found (virtual calls; four scan patterns tried).
  It is therefore unknown whether the game closes a public lobby at vote or race start, or only
  uses this for friend rooms. **Open**, see E3.

### 2.3 Is there join-in-progress at the game level?

Not established. Evidence for: all the layers above accept late members; the client can close and
reopen participation, which only makes sense if the service adds players to open sessions; the
race has its own participant bitmap event (`RaceSeq_NoticePlayerBitmap`, sent regardless of sender).
Evidence against a free-for-all: `WaitMatchmake` posts `0x8b738f76` when the expected and present
member masks differ, and `PrivateRoomMain` posts `0x11173fe5` when a session member's bit is missing
from its mask (`FUN_01a5b6bc`), so a member who appears at the wrong moment can raise an error on the
others. What a late joiner sees (free roam until the next vote, or an error) was not traced. **Open**,
see E1 and E2.

### 2.4 What the server must send

**When a member joins lobby L** (all pushes go only to L's listeners):

1. To the joiner, in the Track reply: state 3, GameSession (`1` name, `8`/`9` endpoint, `12` every
   current member plus the joiner), and MatchedPlayer for the joiner's local users (all members is
   also accepted, as today).
2. To every existing member with a `docs/__us/` target: `Change{target, kind 1 ADDED,
   Document docs/__us/<joiner ussid> {uid, st: 2, ussid, ucsid, upcsid, pgn}}`.
3. To the joiner's `docs/__us/` listen: one ADDED per member of L including itself; to its
   `docs/__gs/` listen and `GetDocument`: L's `__gs/f`.
4. To the joiner's `docs/__pgn/All/__pus/` listen: every `__pus` document of L, answered only when
   all members who joined L earlier and are still present have written theirs (the existing
   deferral, ordered by join sequence in L instead of the global number).
5. Nothing else is server-driven. The joiner writes `__pus`, `PlayerStatic`, `Companion`; those
   writes fan out to L's listeners as normal ADDED / MODIFIED changes, and the existing members
   start the Pia handshake through `__stu/<joiner>`.

**When a member leaves L** (its KeepUserSession stream half-closes, resets, or the connection dies;
a clean leave also removes its targets and deletes its `__pus`, `run-lan7.log` 16:44:54-55):

1. Run the seat's stored deferments (delete `PlayerStatic`, `Companion`, `DPIS`, `@@KeepAlive`,
   `__pus` documents of that user) and push `Change{kind 3 REMOVED, Document{name}}` for each to L's
   listeners.
2. Push REMOVED for `docs/__us/<ussid>` to L's `docs/__us/` listeners (or MODIFIED with `st != 2`;
   REMOVED is the path read in `FUN_01271c00`).
3. If a referee result is open in L, treat the player as retired (existing fallback in RE-MAP 1.8).
4. If the leaver is L's `mcn` and members remain: set `mcn` to the earliest-joined remaining member
   and push MODIFIED `__gs/f` to L's `docs/__gs/` listeners. Pia migrates the host inside the mesh by
   itself; the server cannot see who became host, so this value is a guess (**I**). Until E4 confirms
   that a later joiner can still attach, mark L as not joinable once its original host has left.
5. Drop the seat's token, listeners and deferred listens. If L has no seats left, drop L.

Crash without a TCP close: `@@KeepAlive/<ussid>` is written every 15 s per local player (**V**); treat
45 s of silence on a seat that has written at least one as a leave (**I**).

---

## 3. Capacity and lifecycle per mode

| Question | Answer | Mark |
|---|---|---|
| Max players | 24 for Normal24, Battle24, Survival24, Private24: player index range 0..23 in the network schema (`S32_player_idx` max 23), 24-entry referee `result` array, config names | V (arrays, schema) / I (the name's number is the cap) |
| What counts | players, not consoles: a ticket carries one Player per local user (ticket field 4 is repeated; split screen adds one) | V (shape) |
| `__gs/f.maxu` | must be the lobby capacity (24); we send 2 | I |
| Minimum to start | not decoded. Two players start a vote and a race in every logged run; a lobby of one was never run | V (2 works) |
| Time from assignment to race start | 77-130 s with two players (`run-lan9/10/11.log`: assignment -> first `@@RefereeRequest`) | V |
| Time from a final verdict to the next race | 127 s (`run-lan11.log` 21:24:39 -> 21:26:46), same members, no matchmaking request | V |
| Vote timer | started by `PrivateRoomMain` / `StartTimeoutCounter` over Pia (`0x44733945`); the server does not take part | V (RE-MAP 1.6) |
| Host leaves | Pia host migration exists (2.1). The room loop's "Parent" is the mesh host. What the server must do is only step 4 of the leave list | V (exists) / I (sufficient) |
| Server-visible "event started" | first `@@RefereeRequest/<ussid>/Request/<key>` write in the lobby | V |
| Server-visible "event ended" | our final (`status 1`) `@@RefereeResult`; Knockout Tour has one request per round (`Surv_...` keys), so it ends when the last open result is final and no new request arrives | V / I |
| Earlier lock signal | `__gs/m.cp = true`, if the client ever writes it (2.2) | open |

Lobby states the server should keep:

| State | Meaning | Takes new players |
|---|---|---|
| GATHERING | tickets are waiting, nothing sent yet; the lobby is only a server-side group | yes |
| OPEN | assignments sent, members connecting or in free roam, no referee request yet | only with `JOIN_WINDOW > 0` (needs E1) |
| IN_EVENT | at least one referee result is open | no |
| INTERMISSION | last result final, members back in the room | only with `REJOIN = true` (needs E2) |
| (dropped) | no seats left | - |

A lobby also stops taking players for good when it is full, when `cp` is written true, or when its
original host has left (until E4).

---

## 4. Server design

### 4.1 Data

```python
CAPACITY = {"Normal24": 24, "Battle24": 24, "Survival24": 24, "Private24": 24}
MIN_START = 2          # players needed before a GATHERING lobby is assigned
GATHER_WAIT = 10.0     # s to keep gathering after MIN_START is reached (0 = assign at once)
JOIN_WINDOW = 0.0      # s after assignment during which OPEN still takes players (0 until E1 passes)
REJOIN = False         # INTERMISSION takes players (False until E2 passes)

class Seat:            # one user in one lobby
    user: str; ticket: UUID; players: int      # local player count of the ticket
    join_seq: int                              # order inside the lobby (replaces the global slot for ordering)
    ussid: int                                 # keep the global user number (unique across lobbies)
    token: str | None                          # Gamesync access token once issued
    streams: set                               # (protocol, stream id) of KeepUserSession
    deferments: dict[str, list]                # name -> writes to apply when the seat ends
    last_keepalive: float

class Lobby:
    id: UUID; mode: str; state: str; created: float; assigned: float | None
    seats: dict[str, Seat]                     # user id -> seat
    host: str                                  # mcn
    secret: bytes                              # __gs/f rs
    docs: dict[str, bytes]                     # this lobby's Gamesync documents
    watchers: list                             # (protocol, stream, session, target id, name or collection/)
    pending: list                              # deferred __pus listens
    referee: dict                              # result name -> match; answered request keys
    closed: bool                               # full once, cp true, or original host left

lobbies: dict[UUID, Lobby]                     # unbounded; grows and shrinks with the players
tokens: dict[str, tuple[UUID, str]]            # access token -> (lobby id, user id)
pending_seat: dict[str, UUID]                  # user id -> lobby whose IssueToken is expected
```

There is no pool and no per-mode limit on the number of lobbies. The only limits are per lobby
(`CAPACITY[mode]`, counted in players).

### 4.2 Placing a ticket

On `CreateMatchmakingTicket(user, mode, players)`:

1. Close any earlier seat or ticket of this user (a new search means the old session is over for it;
   run the leave steps of 2.4 if a seat still exists).
2. Candidates = lobbies of this mode with `not closed`, `players + ticket.players <= CAPACITY[mode]`
   and state GATHERING, or OPEN with `now - assigned < JOIN_WINDOW`, or INTERMISSION with `REJOIN`.
3. If there are candidates, take the one with the most players (fill before spreading), oldest first
   on a tie. Otherwise create a new GATHERING lobby for this mode.
4. GATHERING lobby: when its players reach `CAPACITY`, or reach `MIN_START` and `GATHER_WAIT` has
   passed since they did, assign every waiting ticket at once (state OPEN, `assigned = now`, host =
   first seat, `join_seq` in ticket order). Until then send each tracker a state-1 ticket message at
   least every 60 s so the 150 s client timer never fires (RE-MAP 3, 1.1).
5. OPEN / INTERMISSION lobby: assign the ticket immediately and do the join pushes of 2.4.

So a player who searches while every lobby of the mode is full or in an event always lands in a new
GATHERING lobby, and waits there for a second player exactly as a lone player does today.

With the defaults (`JOIN_WINDOW = 0`, `REJOIN = False`) every client behaviour used is one already
seen working, except more than two members being assigned at the same time (E5).

### 4.3 State changes

| Trigger | Effect |
|---|---|
| assignment sent | GATHERING -> OPEN |
| first `@@RefereeRequest` write in the lobby | -> IN_EVENT |
| last open result goes final, no new request for 5 s | IN_EVENT -> INTERMISSION |
| `__gs/m` write with `cp` true / false | `closed = True` / re-evaluate (store and fan out the document either way) |
| players == capacity | `closed = True` (do not reopen when someone leaves mid-session unless `REJOIN`) |
| seat ends (2.4) | leave pushes; host handover; if no seats: delete the lobby, its documents, tokens, listeners and referee state |
| ticket cancelled or tracker lost while GATHERING | remove the ticket; delete the lobby if it has none left |
| assigned seat never calls `IssueToken` within 30 s | treat as left |

A lobby with one remaining member stays alive (the client decides what to do; `PrivateRoomMain` has a
15 s remaining-players error, `0x27ca935a`). It is dropped when that member's stream ends.

### 4.4 Gamesync handlers

- `IssueToken`: `user id` from field 1 -> `pending_seat` -> mint the token, record it, clear the
  pending entry.
- `KeepUserSession`, `WriteDocuments`, `GetDocument`: resolve the lobby from the bearer first; use
  `lobby.docs`, `lobby.watchers`, `lobby.pending`, `lobby.referee` everywhere the globals are used
  today. `publish()` iterates only that lobby's watchers.
- `__gs/f` per lobby: `gsid = "gs-<lobby id>"` (distinct ids keep the Pia sessions apart, 2.1),
  `mcn = lobby.host`, `maxu = CAPACITY[mode]`, `rs = lobby.secret`.
- `__us/<ussid>` documents: one per seat of the lobby; `upcsid` can stay the global number.
- Remove `clear_match_documents()`; a lobby's documents die with the lobby.
- Stream end on a KeepUserSession stream -> the leave steps of 2.4 for that seat (only when it was
  the seat's last stream).

### 4.5 Counts to publish

One function over `lobbies`, recomputed on every change and written to the log (and to whatever
status channel the launcher or overlay reads later; none exists in the server today):

```python
{mode: {"lobbies": n_total,            # GATHERING + OPEN + IN_EVENT + INTERMISSION
        "gathering": n, "open": n, "in_event": n, "intermission": n,
        "players": total_players,      # sum over lobbies, local players included
        "joinable": n}                 # lobbies a new ticket of this mode could enter now
 for mode in CAPACITY}
```

Per lobby, for the log line: id, mode, state, players / capacity, host, age.

---

## 5. Live experiments needed

| Id | Question | How | Unblocks |
|---|---|---|---|
| E0 | Is the bearer on Gamesync calls our IssueToken access token? Which MatchedPlayer field feeds IssueToken field 1, field 2 and the own `__us/<id>` name? | log the `authorization` header per Gamesync call; send a distinct value in MatchedPlayer field 3 once | 1.3 (confirm), per-seat ids |
| E5 | Do three or more players assigned at the same time form one mesh and race, with `maxu = 24`? | three clients, default settings | the base design |
| E1 | Can a player join a lobby that is in free roam before the vote? | third client searches 5-20 s after two were assigned, `JOIN_WINDOW = 30` | `JOIN_WINDOW > 0` |
| E2 | Can a player join during INTERMISSION, and during a race (waits in free roam or errors)? | third client searches after the final verdict, then during a race | `REJOIN`, true join-in-progress |
| E3 | Does the client write `__gs/m.cp`? When? | author `__gs/m {cp: false, ip: false, ebf: false, prp: {rule: 3}}` in each lobby and watch for update writes around vote and race start; accept the write (a failed write posts an error) | an exact "closed" signal instead of timers |
| E4 | After the `mcn` member leaves, does the mesh survive and can a new player attach with the updated `mcn`? | three players, host quits between races, fourth joins | lifting "closed after host left" |
| E6 | Is a lobby of one player stable (no timeout) while it waits? | `MIN_START = 1` | assigning lone players at once |

## 6. Not resolved

- Callers of the open / close participation slots (session-manager vtable +0x78 / +0x80).
- The consumer of `maxu`, and what `ip`, `ebf`, `bfmin`, `bfmax`, `__gs/r.ipc` / `dpc` mean.
- The minimum player count and the exact moment the vote locks (`PrivateRoomMain` `FUN_01a5a444` was
  read only in part).
- How `AttachMeshJob` learns the host for `JoinMesh` (state `WaitHostConstantId`), hence whether
  `mcn` must track Pia's current host.
- Whether the shared TURN relay (`server/turn_relay.py`) copes with many meshes at once (not read).
