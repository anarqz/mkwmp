# Contract: the seat API (`/seat/v1`) and the seat ticket

Our own protocol. Not an NPLN/game contract. Served by `mkwmp server` (crate `rust/seat`) over plain HTTP + JSON. Put TLS in front of it with a reverse proxy or tunnel. Consumers:

- the launcher (`mkwmp-launcher`, `rust/client`): join, poll/heartbeat, leave; presence and friends;
- the lobby: the roster, and ticket verification at login.

All state is in memory and dies with the process. Names are compared case-insensitively.

## 1. Names

Rules for a player's name and for every friend entry:

- 3-16 characters from `[A-Za-z0-9_-]`.
- A name is unique among live sessions: queued or seated, case-insensitive.
- A friend list has at most 8 entries. Duplicates and the player's own name are dropped silently.
- A bad entry is refused with `400 NAME_INVALID`.

## 1a. Profiles (registered names)

A name must be registered before it can join. Registering returns a 6-digit **PIN**, shown once; the launcher saves it in `client.toml`. Joining needs the name's PIN. Profiles are the only state kept across restarts: `[registry] file`, JSON, PINs as Argon2id hashes. Names unused for `expire_days` (180) become free; `reserved_names` can only be registered by the admin (`mkwmp admin register <name>`, loopback + admin token).

Rate limits (client IP = `CF-Connecting-IP` when the request comes through the local tunnel, else the peer): 3 registrations per IP per hour; 10 wrong PINs per IP per 15 min; 10 wrong PINs per name per hour (then that name is locked until the window passes); 30 joins per IP per minute. Over a limit: `429 RATE_LIMITED` with `Retry-After`.

| Route | Body → answer |
| --- | --- |
| `GET /seat/v1/profiles/{name}` | → `{name, registered, reserved}` |
| `POST /seat/v1/profiles` | `{name}` → `201 {name, pin}`; `409 NAME_REGISTERED`, `403 NAME_RESERVED`, `400 NAME_INVALID`, `429` |
| `GET/POST /seat/v1/admin/profiles` | admin only (loopback without CF headers + `Authorization: Bearer <admin token>`): list; `{action: register\|reset-pin\|release, name}` |

## 2. Routes

A session authenticates with `Authorization: Bearer <sessionKey>`. A wrong key and an unknown session look the same: `404 SESSION_NOT_FOUND`.

Errors are `{"error": {"code", "message"}}`. Session answers carry `Cache-Control: no-store`.

| Route | Body → answer | Notes |
| --- | --- | --- |
| `GET /seat/v1/status` | → `{bootId, name, lobbyEndpoint, lobbyCa?, seatsTotal, seated, queued, lobbies}` | Public, readable from any origin. `lobbyEndpoint` is the NPLN `host:port` the launcher writes as `server`. `lobbyCa` is the lobby's certificate (PEM, only the `CERTIFICATE` blocks of the `[tls] cert` file, at most 16 KiB; absent if the seat server can't read it). The launcher writes it to `<game>/mkwmp-lobby-ca.pem` (or deletes that file when absent), and the game patch trusts that certificate for the lobby instead of its built-in one. `lobbies` is the lobby's last count of live lobbies per mode, e.g. `{"Normal24": 3}`. It is `{}` until the lobby reports, and again once its report is older than 60 s. Lobbies have no cap: they open and close with the players. |
| `GET /seat/v1/keys` | → `{keys: [{kid, publicKey}]}` | Public. The ticket verification key (base64url, raw 32 bytes). |
| `POST /seat/v1/sessions` | `{name, pin}` → `201 View + sessionKey` | Checks the profile's PIN, then seats the player at once if a seat is free, else queues them. Errors: `404 PROFILE_NOT_FOUND`, `401 PIN_WRONG`, `429 RATE_LIMITED`, `409 NAME_TAKEN` (that profile is already online), `503 QUEUE_FULL` (with `Retry-After: 30`). |
| `GET /seat/v1/sessions/{id}` | → `View` | The poll while queued and the heartbeat while seated. Repeat within `nextPollMs`. |
| `DELETE /seat/v1/sessions/{id}` | → `204` | Frees the seat or the place in line. Idempotent. |
| `POST /seat/v1/lobby/roster` | `{sessionIds: string[] (≤256), lobbies?: {mode: count}}` → `{bootId, sessions: RosterEntry[]}` | Lobby only. `lobbies` takes at most 16 modes, and mode names are `[A-Za-z0-9_-]{1,32}`. `Authorization: Bearer <lobby token>`. The tokens come from `[seats] lobby_tokens_file`. Without that file: `403 ROSTER_DISABLED`. Wrong token: `401`. |

```ts
View = {
  bootId,            // random per process start; if it changes, rejoin
  sessionId, name,
  state: "queued" | "seated",
  position?,         // queued: 1 = next
  seatsTotal, seatsFree, queued,
  nextPollMs,        // seated: heartbeat (20 s); queued: poll (5 s)
  ticket?: { token, expiresAt /* unix s */, lobby /* NPLN host:port */ },
}
RosterEntry = {
  sessionId,
  alive,             // still seated; false for unknown, queued or gone
  name?,             // when alive
  friends: [{ sessionId, name }],  // accepted friends (registry) seated now, sorted by name (case-insensitive)
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
| `POST /seat/v1/presence` | `{name, pin}` → `{token, name, ttlMs}` | Same PIN limits as joins (`401 PIN_WRONG`, `404 PROFILE_NOT_FOUND`, `429`), plus 30 sign-ins per IP per minute. |
| `DELETE /seat/v1/presence` | bearer → `204` | The launcher quits: offline at once. |
| `GET /seat/v1/friends[?since=<cursor>]` | bearer → `{cursor, friends: [{name, state}], incoming: [{name, since}], outgoing: [{name, since}]}` | `state`: `playing` (seated), `online` (launcher running, or queued), `offline`. Friends are ordered playing, online, offline, then by name. With `since` (the last `cursor`), the server waits up to 25 s for any change. 120 per minute per token. |
| `POST /seat/v1/friends` | bearer, `{name}` → `{name, state: "pending" \| "accepted"}` | A request; if they had asked you, it accepts instead. Errors: `404 FRIEND_UNKNOWN`, `400 FRIEND_SELF`, `409 FRIEND_EXISTS`, `409 FRIEND_PENDING`, `409 FRIEND_LIMIT` (100 friends, 50 requests waiting). 20 per hour per profile. |
| `POST /seat/v1/friends/{name}/accept` | bearer → `204` | `404 REQUEST_NOT_FOUND`. |
| `DELETE /seat/v1/friends/{name}` | bearer → `204` | Rejects their request, cancels yours, or ends the friendship. Accept and delete share 120 per hour per profile. |

A missing, expired or outdated token answers `401 PRESENCE_NOT_FOUND`: sign in again. Every `429` carries `Retry-After`.

## 3. The seat ticket (v1)

    mkw1.<payload>.<sig>

- `payload`: base64url (no padding) of compact JSON `{"v":1,"iss","kid","sid","name","iat","exp"}`.
- `sig`: base64url (no padding) of the Ed25519 signature over the ASCII bytes `mkw1.<payload>`.
- Alphabet `[A-Za-z0-9._-]`, at most **512** bytes (about 255 today).
- `exp = iat + ttl_secs` (12 h by default).

**Who holds which key:**

- The seat server signs with its private key: `[ticket] private_key_file`, created with `mkwmp keygen <kid>`.
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
