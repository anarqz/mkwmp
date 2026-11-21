# Contract: the seat API (`/seat/v1`) and the seat ticket

Our own protocol. Not an NPLN/game contract. Served by `server seat` (crate `rust/seat`) over plain HTTP + JSON. Put TLS in front of it with a reverse proxy or tunnel. Consumers:

- the launcher (`mkwmp-launcher`, `rust/client`): join, poll/heartbeat, leave; presence and friends;
- the lobby: the roster, and ticket verification at login.

Seats, queues and presences are in memory and die with the process; profiles (gamertag + PIN hash) and friendships persist in SQLite. Names are compared case-insensitively.

## 1. Names

Rules for a player's name and for every friend entry:

- 3-16 characters from `[A-Za-z0-9_-]`.
- A name is unique among live sessions: queued or seated, case-insensitive.
- A friend list has at most 8 entries. Duplicates and the player's own name are dropped silently.
- A bad entry is refused with `400 NAME_INVALID`.

## 1a. Profiles (registered names)

A name must be registered before it can join. Registering returns a 6-digit **PIN**, shown once; the launcher saves it in `client.toml`. Joining needs the name's PIN. Profiles, friendships and voice-chat reports are the only state kept across restarts: `[registry] file`, SQLite, PINs as Argon2id hashes. Names unused for `expire_days` (default 30) are deleted with their friendships; a launcher that still has the name's PIN registers it again with that PIN (`POST /seat/v1/profiles` with `pin`) if the name is still free; `reserved_names` can only be registered by the admin (`server admin register <name>`, loopback + admin token).

Rate limits (client IP = `CF-Connecting-IP` when the request comes through the local tunnel, else the peer): 3 registrations per IP per hour; 10 wrong PINs per IP per 15 min; 10 wrong PINs per name per hour (then that name is locked until the window passes); 30 joins per IP per minute. Over a limit: `429 RATE_LIMITED` with `Retry-After`.

| Route | Body → answer |
| --- | --- |
| `GET /seat/v1/profiles/{name}` | → `{name, registered, reserved}` |
| `POST /seat/v1/profiles` | `{name, pin?}` → `201 {name, pin}`; `409 NAME_REGISTERED`, `403 NAME_RESERVED`, `400 NAME_INVALID` (also a `pin` that isn't 6 digits), `429`. Without `pin` the server picks one. |
| `GET/POST /seat/v1/admin/profiles` | admin only (loopback without CF headers + `Authorization: Bearer <admin token>`): list; `{action: register\|reset-pin\|release, name}` |

## 2. Routes

A session authenticates with `Authorization: Bearer <sessionKey>`. A wrong key and an unknown session look the same: `404 SESSION_NOT_FOUND`.

Errors are `{"error": {"code", "message"}}`. Session answers carry `Cache-Control: no-store`.

| Route | Body → answer | Notes |
| --- | --- | --- |
| `GET /seat/v1/status` | → `{bootId, name, lobbyEndpoint, lobbyCa?, voicePort, seatsTotal, seated, queued, lobbies, reportKinds}` | Public, readable from any origin. `lobbyEndpoint` is the NPLN `host:port` the launcher writes as `server`. `lobbyCa` is the lobby's certificate (PEM, only the `CERTIFICATE` blocks of the `[tls] cert` file, at most 16 KiB; absent if the seat server can't read it). The launcher writes it to `<game>/mkwmp-lobby-ca.pem` (or deletes that file when absent), and the game patch trusts that certificate for the lobby instead of its built-in one. `voicePort` is the UDP port of the lobby's voice relay (`[lobby] voice_port`, default 8907; 0 = voice off), on the lobby's host; the launcher writes it to `mkwmp.toml` as `voice_port` (docs/contracts/VOICE_API.md). `reportKinds` (draft, branch `chat-rooms`) lists what a report can be about, `["voice","camera"]`; a server without it takes no `kind` (voice only). `lobbies` is the lobby's last count of live lobbies per mode, e.g. `{"Normal24": 3}`. It is `{}` until the lobby reports, and again once its report is older than 60 s. Lobbies have no cap: they open and close with the players. |
| `GET /seat/v1/keys` | → `{keys: [{kid, publicKey}]}` | Public. The ticket verification key (base64url, raw 32 bytes). |
| `POST /seat/v1/sessions` | `{name, pin}` → `201 View + sessionKey` | Checks the profile's PIN, then seats the player at once if a seat is free, else queues them. Errors: `404 PROFILE_NOT_FOUND`, `401 PIN_WRONG`, `429 RATE_LIMITED`, `409 NAME_TAKEN` (that profile is already online: its session heartbeated within its poll interval + 5 s; a session silent longer, e.g. a killed launcher, is taken over by this join, which keeps its seat or place in line under a new session id; clients retry 409 later), `503 QUEUE_FULL` (with `Retry-After: 30`). |
| `GET /seat/v1/sessions/{id}` | → `View` | The poll while queued and the heartbeat while seated. Repeat within `nextPollMs`. |
| `DELETE /seat/v1/sessions/{id}` | → `204` | Frees the seat or the place in line. Idempotent. |
| `POST /seat/v1/lobby/roster` | `{sessionIds: string[] (≤256), lobbies?: {mode: count}, rooms?: {sessionId: {lobby, ussid, ussids}}}` → `{bootId, sessions: RosterEntry[]}` | Lobby only. `rooms` (1.3.1): each listed seat's lobby room; a listed seat missing from it is in no room; without `rooms` nothing changes. The seat server shows a room to that seat's own launcher only (`View.room`). `lobbies` takes at most 16 modes, and mode names are `[A-Za-z0-9_-]{1,32}`. `Authorization: Bearer <lobby token>`. The tokens come from `[seats] lobby_tokens_file`. Without that file: `403 ROSTER_DISABLED`. Wrong token: `401`. |

```ts
View = {
  bootId,            // random per process start; if it changes, rejoin
  sessionId, name,
  state: "queued" | "seated",
  position?,         // queued: 1 = next
  seatsTotal, seatsFree, queued,
  nextPollMs,        // seated: heartbeat (20 s); queued: poll (5 s)
  ticket?: { token, expiresAt /* unix s */, lobby /* NPLN host:port */ },
  room: { lobby /* lobby uuid */, ussid, ussids /* ascending: the first is the mesh host */, at /* ms, when the lobby said */ } | null,  // 1.3.1; null: no room, unknown, or older than 60 s
}
RosterEntry = {
  sessionId,
  alive,             // still seated; false for unknown, queued or gone
  name?,             // when alive
  friends: [{ sessionId?, name, seated }],  // EVERY accepted friend (registry), seated or not, sorted by name
                     // (case-insensitive), at most 100; sessionId only when seated. The game maps the
                     // lobby's friend users onto its friend list once per sign-in, so offline friends
                     // must already be listed; the lobby's presence says who is online.
}
```

**Liveness:**

- A seated session with no heartbeat for `seat_timeout_secs` (60) is freed.
- A queued session with no poll for `queue_timeout_secs` (30) drops out.
- Freed seats go to the queue in FIFO order, checked on every request and every 5 s.
- A newly seated player gets a full heartbeat window.

**Restart:** a new `bootId` means every old session is gone, and old sessions answer `404`. The launcher rejoins and rewrites `mkwmp.toml` with the fresh ticket. A lobby sees `alive: false` for the old ids.

## 2a. Presence and friends

Friendships are stored in the registry database (`registry.db`), one row per pair: `pending` (a request) or `accepted` (friends both ways). They are deleted with their profile, whichever way it goes: owner delete, admin release, expiry, or someone registering an expired name.

A launcher signs in with its PIN to a **presence**. A presence never takes a seat and never counts in the seat pool. It is what "online" means for friends, and its token authenticates the friends routes. A presence lasts 90 s past its last request (the friends long-poll keeps it alive), up to 4 per profile. It ends on `DELETE`, a new PIN, or the profile going away. It lives in memory: after a seat-server restart the launcher signs in again.

| Route | Body → answer | Notes |
|---|---|---|
| `POST /seat/v1/presence` | `{name, pin}` → `{token, name, ttlMs, privacyAccepted, privacyVersion, privacyEnforced}` | Same PIN limits as joins (`401 PIN_WRONG`, `404 PROFILE_NOT_FOUND`, `429`), plus 30 sign-ins per IP per minute. The privacy fields are §2c (servers before 1.3.1 leave them out). |
| `DELETE /seat/v1/presence` | bearer → `204` | The launcher quits: offline at once. |
| `GET /seat/v1/friends[?since=<cursor>]` | bearer → `{cursor, friends: [{name, state}], incoming: [{name, since}], outgoing: [{name, since}]}` | `state`: `playing` (seated), `online` (launcher running, or queued), `offline`. Friends are ordered playing, online, offline, then by name. With `since` (the last `cursor`), the server waits up to 25 s for any change. 120 per minute per token. |
| `POST /seat/v1/friends` | bearer, `{name}` → `{name, state: "pending" \| "accepted"}` | A request; if they had asked you, it accepts instead. Errors: `404 FRIEND_UNKNOWN`, `400 FRIEND_SELF`, `409 FRIEND_EXISTS`, `409 FRIEND_PENDING`, `409 FRIEND_LIMIT` (100 friends, 50 requests waiting). 20 per hour per profile. |
| `POST /seat/v1/friends/{name}/accept` | bearer → `204` | `404 REQUEST_NOT_FOUND`. |
| `DELETE /seat/v1/friends/{name}` | bearer → `204` | Rejects their request, cancels yours, or ends the friendship. Accept and delete share 120 per hour per profile. |

## 2b. Voice-chat abuse reports

A player reports another player's voice chat from the launcher (SETTINGS → REPORT VOICE ABUSE); the server's host reviews reports with `server admin reports`. Reports are stored in `registry.db`. A profile has **one open report at a time**. Reports are not tied to profiles: one about a player stays when that player's profile goes (it records the reported profile's registration time, so a later owner of the gamertag is told apart). The reporter deleting their profile (or an admin release) deletes the reports they filed. Every report is deleted 90 days after it was filed.

| Route | Body → answer | Notes |
| --- | --- | --- |
| `GET /seat/v1/reports` | bearer → `{report: Report \| null}` | The caller's open report. 60 per minute per token. |
| `POST /seat/v1/reports` | bearer, `{name, reason, details?}` → `201 {report: Report}` | `reason`: `harassment`, `hate`, `sexual`, `threats`, `disruption` or `other`. `details`: free text, control characters dropped, cut to 500 characters. Errors: `409 REPORT_ACTIVE` (withdraw the open one first), `404 REPORT_UNKNOWN` (no such live gamertag), `400 REPORT_SELF`, `400 REPORT_INVALID`. Tries, refused ones included: 5 per profile and 10 per IP per day. |
| `DELETE /seat/v1/reports` | bearer → `204` | Withdraws (deletes) the open report. `404 REPORT_NOT_FOUND`. 5 per profile per day. |
| `GET /seat/v1/admin/reports[?status=open\|closed\|all]` | admin → `{reports: Report[]}` | Oldest first; default `open`. |
| `POST /seat/v1/admin/reports` | admin, `{action: close\|delete, id, outcome?}` → `{id, done}` | `close` marks it reviewed with a note of what was done (the reporter may then file again); `delete` removes it. |

`Report` = `{id, reporter, reported, reportedCreated, reason, details, created, status: "open"|"closed", closed, outcome}`. Draft on branch `chat-rooms`: `POST` takes an optional `kind: "voice"|"camera"` (default `"voice"`) and `Report` gains `kind` (VIDEO_API.md 4); times in seconds since 1970. Ids are never reused.

A missing, expired or outdated token answers `401 PRESENCE_NOT_FOUND`: sign in again. Every `429` carries `Retry-After`.

## 2c. Privacy policy and consent (servers 1.3.1, launcher 1.4.0)

Each server serves its own privacy policy and records, per profile, which version the player accepted. The launcher signs in (§2a), then shows the policy when `privacyAccepted` is `false` and sends the answer.

| Route | Body → answer | Notes |
|---|---|---|
| `GET /privacy` | → `200` Markdown | `Content-Type: text/markdown; charset=utf-8`, no auth, `ETag: "<version>"` (quoted, as HTTP wants), `Cache-Control: no-cache`, CORS `*`. `If-None-Match: "<version>"` → `304`. The text is `[privacy] file`, or the built-in policy for self-hosted servers. Shares the 120-per-minute read budget with `status`. |
| `PUT /seat/v1/privacy` | bearer (presence), `{accepted: true, version}` or `{accepted: false}` → `204` | Accept stores the version and the time on the profile; withdraw clears them. `version` is the one the launcher showed: another one → `409 PRIVACY_VERSION` (the error carries `privacyVersion`, the current one: fetch `/privacy` again). `accepted: true` without `version` → `400 REQUEST_INVALID`. Unknown fields → `422`. 10 per minute per token. |
| `GET /ping` | → `204` | Empty, no limit: for the launcher's latency measurement. |

- **Version** = lower-case hex SHA-256 of the policy's bytes. Editing the file (and restarting the server) makes a new version, so every profile is `privacyAccepted: false` again.
- `privacyAccepted` is `true` only when the profile accepted the current version. A deleted, expired or re-registered gamertag starts without consent. A new PIN (admin reset) keeps it.
- `GET /seat/v1/status` gains `privacy: {version, enforced}`, so a launcher can tell before signing in whether a server enforces consent.
- **Enforcement** (`[privacy] enforce = true`, default `false`): until the profile accepted the current version, `POST /seat/v1/sessions` (after the PIN check), `GET`/`POST /seat/v1/friends`, `POST /seat/v1/friends/{name}/accept` and `GET`/`POST /seat/v1/reports` answer `403 PRIVACY_REQUIRED` (with `privacyVersion`). Removing data stays open: `DELETE /seat/v1/friends/{name}`, `DELETE /seat/v1/reports` and `DELETE /seat/v1/profiles/{name}`. Presence sign-in, `PUT /seat/v1/privacy` and registration are never gated. The 1.3.x launchers only ask for consent locally (accepting on the server comes with the new launcher, 1.4.0), so a server enforcing it refuses them seats.

## 3. The seat ticket (v1)

    mkw1.<payload>.<sig>

- `payload`: base64url (no padding) of compact JSON `{"v":1,"iss","kid","sid","name","iat","exp"}`.
- `sig`: base64url (no padding) of the Ed25519 signature over the ASCII bytes `mkw1.<payload>`.
- Alphabet `[A-Za-z0-9._-]`, at most **512** bytes (about 255 today).
- `exp = iat + ttl_secs` (12 h by default).

**Who holds which key:**

- The seat server signs with its private key: `[ticket] private_key_file`, created with `server keygen <kid>`.
- Lobbies hold only public keys: `[ticket] public_keys = ["kid:base64url"]`.
- So a ticket made by anyone else fails verification.

**The ticket is not the liveness check.** It proves the seat server issued session `sid` to `name`. Whether the seat is still held is checked with the roster:

1. The lobby verifies the ticket when the game logs in.
2. It then asks the roster about that `sid`.
3. It polls the roster about every 10 s for its connected players and drops the ones that are `alive: false`.

**Reference implementations:** `rust/ticket` and `resources/tools/ticket/verify_ticket.py`. Both must pass `resources/tools/ticket/vectors.json`.

**On the game side:**

- The launcher writes the ticket as `ticket = "<token>"` in `<game>/mkwmp.toml`.
- The probe sends it as the bearer of `IssuePrearrangedUserToken`.
- The `name` claim is the in-game display name.
