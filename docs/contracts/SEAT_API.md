# Contract: the seat API (`/seat/v1`) and the seat ticket

Our own protocol. Not an NPLN/game contract. Served by `mkwmp server` (crate `rust/seat`) over plain HTTP + JSON. Put TLS in front of it with a reverse proxy or tunnel. Consumers:

- the launcher (`mkwmp-launcher`, `rust/client`): join, poll/heartbeat, friends, leave;
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
| `GET /seat/v1/status` | → `{bootId, name, lobbyEndpoint, seatsTotal, seated, queued}` | Public, readable from any origin. `lobbyEndpoint` is the NPLN `host:port` the launcher writes as `server`. |
| `GET /seat/v1/keys` | → `{keys: [{kid, publicKey}]}` | Public. The ticket verification key (base64url, raw 32 bytes). |
| `POST /seat/v1/sessions` | `{name, pin, friends?: string[]}` → `201 View + sessionKey` | Checks the profile's PIN, then seats the player at once if a seat is free, else queues them. Errors: `404 PROFILE_NOT_FOUND`, `401 PIN_WRONG`, `429 RATE_LIMITED`, `409 NAME_TAKEN` (that profile is already online), `503 QUEUE_FULL` (with `Retry-After: 30`). |
| `GET /seat/v1/sessions/{id}` | → `View` | The poll while queued and the heartbeat while seated. Repeat within `nextPollMs`. |
| `PUT /seat/v1/sessions/{id}/friends` | `{friends}` → `View` | Replaces the friend list. Also counts as a heartbeat. |
| `DELETE /seat/v1/sessions/{id}` | → `204` | Frees the seat or the place in line. Idempotent. |
| `POST /seat/v1/lobby/roster` | `{sessionIds: string[] (≤256)}` → `{bootId, sessions: RosterEntry[]}` | Lobby only, `Authorization: Bearer <lobby token>`. The tokens come from `[seats] lobby_tokens_file`. Without that file: `403 ROSTER_DISABLED`. Wrong token: `401`. |

```ts
View = {
  bootId,            // random per process start; if it changes, rejoin
  sessionId, name,
  state: "queued" | "seated",
  position?,         // queued: 1 = next
  seatsTotal, seatsFree, queued,
  nextPollMs,        // seated: heartbeat (20 s); queued: poll (5 s)
  ticket?: { token, expiresAt /* unix s */, lobby /* NPLN host:port */ },
  friends: [{ name, online /* seated now */, mutual /* lists you too */ }],  // the player's own list, in order
}
RosterEntry = {
  sessionId,
  alive,             // still seated; false for unknown, queued or gone
  name?,             // when alive
  friends: [{ sessionId, name }],  // mutual friends seated now, sorted by name (case-insensitive)
}
```

**Liveness:**

- A seated session with no heartbeat for `seat_timeout_secs` (60) is freed.
- A queued session with no poll for `queue_timeout_secs` (30) drops out.
- Freed seats go to the queue in FIFO order, checked on every request and every 5 s.
- A newly seated player gets a full heartbeat window.

**Restart:** a new `bootId` means every old session is gone, and old sessions answer `404`. The launcher rejoins and rewrites `mkwmp.toml` with the fresh ticket. A lobby sees `alive: false` for the old ids.

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

**Reference implementations:** `rust/ticket` and `tools/ticket/verify_ticket.py`. Both must pass `tools/ticket/vectors.json`.

**On the game side:**

- The launcher writes the ticket as `ticket = "<token>"` in `<game>/mkwmp.toml`.
- The probe sends it as the bearer of `IssuePrearrangedUserToken`.
- The `name` claim is the in-game display name.
