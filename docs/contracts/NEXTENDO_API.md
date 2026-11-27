# Contract: playing on Nextendo Network (launcher ↔ mod)

A player who has linked a Nextendo Network account can play on Nextendo's servers instead of an MKWMP server.

- The link stays on the player's machine. The launcher never sends Nextendo tokens, ids or names to an MKWMP
  server.
- In Nextendo mode the launcher does not contact the MKWMP seat server at all. There is no seat, ticket,
  voice, camera or MKWMP friends.

Owners:
- launcher: `launcher/` (C#), which covers OAuth, storage, the toggle, mkwmp.toml and the local API route;
- mod: `native/probe` (C), which covers the backend switch and the network, certificate and token handling.

Nextendo's own reference: wiki.nextendo.network/developers, sections "Switch 2 emulators and recomps",
"Game login" and "NPLN games". Research notes: `research/docs/NEXTENDO.md` (private repo).

## 1. Nextendo application

| | |
|---|---|
| client_id | `nxc_n38IaT09cMAh` (public client, PKCE S256, no secret) |
| scopes | `identity friends presence game.matchmaking` |
| redirect | `http://127.0.0.1:<any free port>/callback` (loopback, never `localhost`) |
| authorize | `GET https://nextendo.network/api/oauth/authorize?response_type=code&client_id&redirect_uri&code_challenge&code_challenge_method=S256&scope&state` |
| token | `POST https://nextendo.network/api/oauth/token` (form): `grant_type=authorization_code, code, redirect_uri, client_id, code_verifier` / `grant_type=refresh_token, refresh_token, client_id` → `{access_token (1 h), refresh_token (30 d, rotates: keep the newest), expires_in, scope}` |
| revoke | `POST https://nextendo.network/api/oauth/revoke` `token=<refresh_token>` (always 200) |
| me | `GET https://nextendo.network/api/oauth/userinfo` (Bearer access) → `pid, username, friend_code, display_name` |
| game token | `POST https://nextendo.network/api/nex-token` (Bearer access) → `{nex_token:"nx2…", pid, friend_code, username, expires_in:86400}`; 403 = scope missing (link again), 429 honour Retry-After |
| network | `GET https://nextendo.network/api/developers/network-config` (Bearer access) → `{server_host, nat_host, npln:{…}, ca_pem}` |
| online check | `GET https://nextendo.network/api/online-status` (Bearer **nx2**) → `{allow, reason}`, where reason ∈ `unknown, disabled, suspended, unverified, discord_unlinked, elsewhere` |
| sign up | `https://nextendo.network/register.html` (opened in the system browser) |

Every call is HTTPS to nextendo.network only.

## 2. Launcher storage (local only)

- The refresh token is protected with DPAPI (CurrentUser) under the launcher's data folder.
- The cached profile `{pid, username, friendCode}` and the chosen network `play_on = "mkwmp" | "nextendo"` go
  in client.toml or the launcher's settings store (`[nextendo]`).
- The default is `"nextendo"`. A client.toml that names no network and already holds an MKWMP setup (a server, a
  PIN, or a profile kept for a server) is read as `"mkwmp"`: nobody who plays on an MKWMP server is moved off it
  silently. The launcher settles this when it opens the file and writes it at its next save.
- The choice is its own: it stays without an account. On `"nextendo"` with no account linked, PLAY writes the
  offline mkwmp.toml (no `backend`) and no MKWMP server is contacted, whatever `server` holds. The first link of an
  account sets `"nextendo"` (not while a game runs); linking again and unlinking keep the choice.
- MKWMP servers are chosen in the launcher's Settings only; an MKWMP server's consent is asked for when a server
  is chosen there, and nowhere before.
- Never log tokens. Never put them in bug reports, crash records, URLs or mkwmp.toml.

## 3. mkwmp.toml in Nextendo mode

    backend = "nextendo"                # absent or "mkwmp" = MKWMP server (unchanged behaviour)
    name = "<Nextendo username>"
    launcher = "127.0.0.1:47820"
    launcher_token = "<64 hex>"
    nextendo_ca = "nextendo-ca.pem"     # beside mkwmp.toml: ca_pem from network-config, PEM
    launcher_exe = "C:/.../mkwmp.exe"

- `server`, `ticket` and `voice_port` are left out; the mod treats `voice_port` as 0.
- The launcher writes `nextendo-ca.pem` before the toml and refreshes it from network-config at each PLAY.
- A mod older than this contract ignores `backend`. It would try the MKWMP lobby at its default host, and the
  launcher's 1.8.0/mod-version gate keeps that from happening.

## 4. GET /local/v1/nextendo/session

Same bearer (`launcher_token`), same listener as `/local/v1/seat`. The mod calls it in Nextendo mode **instead of
POST /local/v1/seat**:
- when it activates (first Online Play request);
- each time the game loads its id token (`nn::account::LoadNetworkServiceAccountIdTokenCache`), so a long session
  always gets a token with at least 1 h left.

| Status | Body | Meaning |
|---|---|---|
| 200 | `{"state":"ready","nexToken":"nx2…","expiresAt":<ms since 1970>,"pid":123,"username":"bia","friendCode":"SW-…","serverHost":"nex.nextendo.network","natHost":"nat.nextendo.network","caFile":"nextendo-ca.pem"}` | Use it. |
| 409 | `{"state":"refused","reason":"<online-status reason or unlinked or scope>","message":"<localized>"}` | Stay offline. The launcher has already shown the reason. |
| 503 | `{"state":"unavailable","message"}` | Nextendo unreachable. Stay offline. |
| 401 | `{"state":"unauthorized"}` | Wrong token: re-read mkwmp.toml once. |

The launcher keeps the nx2 cached and fetches a new one when less than 1 h is left. The access token is refreshed
on demand. `online-status` is checked before PLAY and again when a session is served (cached 60 s). Nothing about
the session reaches MKWMP servers.

`GET /local/v1/friends` in Nextendo mode answers `200 {"friends":[]}`. Friend ids on Nextendo are derived server
side, so this stays empty until that is settled. Overlay routes are unchanged. Voice answers `enabled:false`.

## 5. What the mod does in Nextendo mode

- Account token (`LoadNetworkServiceAccountIdTokenCache`): an unsigned-looking JWT
  `b64url({"alg":"RS256","typ":"JWT"}).b64url(claims).b64url(<64 bytes>)`.
  - Claims follow REF_CLAIMS of Nextendo's docs: iss, aud, typ, sub, jti, di, sn, hm, app_id, tv:"1.8.0",
    iat/exp, **nnex: <nexToken>**.
  - Only nnex is trusted by Nextendo.
- `nn::account::GetNickname`: the `username`.
- `GetNetworkServiceAccountId`: derived from `pid`, unique per player and never 0.
- `nn::nsd::GetEnvironmentIdentifier`: `lp1` (as today).
- Built-in certificates (`GetBuiltInCertificates`): serve `nextendo_ca` instead of the MKWMP lobby CA.
- Name resolution, following Nextendo's emulator rules:
  - `nncs2-*.n.n.srv.nintendo.net` → the NAT address (`natHost`);
  - every other `*.nintendo.net`, `*.nintendo.com` or `*.nintendo.co.jp` name → the server address (`serverHost`);
  - any other name → real DNS.
  - The two hosts are resolved with the machine's DNS at activation and cached for 10 min; the last good address
    is kept.
- TCP connect: to the address the game asks for (it came from the resolution above). Port 443 stays 443; the
  MKWMP port remap applies only in MKWMP mode. **Gamesync** uses the host and port the game session gives,
  unchanged. The game's own TLS keeps the original SNI.
- Pia UDP: as in LAN mode. Bind to all interfaces, report the real local address, and send to whatever address and
  port Pia gives. NAT probes are not redirected to MKWMP's `:10025`.
- Off in Nextendo mode: seat keepalive, voice, chat rooms and camera, and the MKWMP friends fill.
- Crash records and logs name the backend (`backend: nextendo`), never the token.
- Errors: 2321-2304 means the token or account was refused, and 2321-3072 means the version was refused. These are
  the game's own messages. The mod just logs them.

## 6. Friends and presence (launcher only)

In Nextendo mode the launcher's friends panel, top bar and notifications are the linked account's Nextendo friends.
Nothing of this reaches the mod or an MKWMP server; `/local/v1/friends` stays as §4 says.

| What | Call (Bearer access token, scope `friends` unless noted) |
|---|---|
| lists | `GET /api/friends?images=0` → `{friends, requests, sent?}`; `presence.status` 0 offline / 1 online / 2 playing; `banned:true` entries are shown as closed accounts |
| add | `POST /api/friends {"friend_code":"SW-1234-5678-9012"}` → `{friend, already}`; 404 unknown code, 400 own code, 409 list full (300) |
| accept, decline, remove, block, unblock | `POST /api/friends/<verb> {"pid":N}` |
| favourite | `POST /api/friends/favorite {"pid":N,"favorite":bool}` |
| blocked, met | `GET /api/friends/blocked`, `GET /api/recent-players` (when the panel opens, at most once a minute) |
| presence | `POST /api/presence {"status":1|2,"app_id":"","app_detail":"","platform":"app"}` (scope `presence`) |

- No push channel: the list is polled every 30 s (10 s for a minute after an action), backing off 30 s → 5 min on
  failures; a 429's `Retry-After` is obeyed and nothing is asked before it is over.
- Presence: status 1 while the launcher runs, status 2 with `app_id = 0400c3f00006e000` while the game runs, repeated
  every 60 s (Nextendo forgets it after 90 s); status 0 once on exit, unlink, a switch to MKWMP servers, or when
  `[nextendo] show_online = false` (client.toml; default on).
- A sent request cannot be taken back (Nextendo has no route for it): the panel shows it without a Cancel.
- Friends' names, pids and friend codes are never logged and are overwritten in a report's minidump.