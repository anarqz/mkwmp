# Contract: the beholder (`/beholder/v1`), the public server directory

The beholder lists mkwmp seat servers, official and self-hosted, so the launcher can offer a server
browser. Servers announce themselves; launchers read the list. Modelled on ForzaInfinity's beholder
(its ANNOUNCER_API), smaller: **in memory only**, servers only.

- Service: `mkwmp beholder [config/beholder.toml]` (`rust/beholder`), one small process behind HTTPS
  (a Cloudflare tunnel or a reverse proxy).
- It holds no player data, never sees a token, never contacts an announced endpoint and never
  measures latency. **A listing is a hint, not a trust decision**: the launcher talks to the server it
  picks (`GET {endpoint}/seat/v1/status`, `GET {endpoint}/ping` for latency) and trusts only that.
- A restart empties the list. Servers re-announce within 30 s.

## 1. Identity

Each seat server owns an Ed25519 key pair, made on its first start (`[announce] key_file`, default
`beholder-ed25519.key` next to the ticket key; base64url seed, mode 0600). It is separate from the
ticket key. The public key (32 bytes, **base64 standard with padding**) is the server's identity in
the directory: an announce for a key replaces that key's listing.

## 2. `POST /beholder/v1/announce`

Body: JSON, at most 4 KiB, unknown fields refused.

| Field | Type | Rule |
| --- | --- | --- |
| `kind` | string | `"server"` |
| `publicKey` | string | base64 of 32 bytes |
| `endpoint` | string | the seat API base URL players use: `http(s)://host[:port]`, no path (not even `/`), query or user info. Host: a dotted ASCII DNS name or a public IPv4 literal (no `localhost`, private, loopback, link-local or documentation address) |
| `name` | string | 1-48 characters, no control characters, no leading or trailing space |
| `region` | string | 0-16 of `[A-Za-z0-9-]` (e.g. `sa-east`) |
| `version` | string | 1-32 of `[A-Za-z0-9.+-]` (the mkwmp release) |
| `capacity` | integer | 1-100000: seats in total (`[seats] capacity`) |
| `seated` | integer | 0-`capacity` |
| `queued` | integer | 0-100000 |
| `issuedAt` | integer | Unix seconds, within 120 s of the beholder's clock |
| `signature` | string | base64 of the 64-byte Ed25519 signature over the message below |

Signed message (UTF-8, fields joined by `\n`, no trailing newline):

```
mkwmp-beholder-announce-v1
<kind>
<publicKey>
<endpoint>
<name>
<region>
<version>
<capacity>
<seated>
<queued>
<issuedAt>
```

Numbers are decimal without padding.

| Status | Code | When |
| --- | --- | --- |
| 204 | | listed or refreshed |
| 400 | `REQUEST_INVALID` + `field` | a field breaks its rule; `field: "body"` when the body isn't the JSON shape (unknown field, wrong type, negative number) |
| 401 | `SIGNATURE_INVALID` | the signature doesn't verify |
| 401 | `STALE` | `issuedAt` more than 120 s off |
| 403 | `TOO_MANY_SERVERS` | this client address already has 4 live listings |
| 409 | `REPLAYED` | `issuedAt` older than this key's latest announce |
| 413 | `TOO_LARGE` | body over 4 KiB |
| 429 | `RATE_LIMITED` + `Retry-After` | over 12 announces a minute for the key (verified announces only, so nobody can spend another server's budget), 60 a minute from the address (counted before parsing), or 1500 a minute in all |
| 503 | `DIRECTORY_FULL` + `Retry-After` | 500 servers listed |

Errors are `{"error": {"code", "message", "field"?}}`, as in the seat API.

A listing whose last announce is more than 90 s old is gone.

## 3. `GET /beholder/v1/servers`

Public, no body. `200 {"items": [Listing]}`, at most 500, the most seated first, then by name.
`Cache-Control: public, max-age=10`, CORS `*`. 60 per minute per client address, 6000 in all;
`429 RATE_LIMITED` with `Retry-After` past that.

`Listing` = `{publicKey, name, endpoint, region, version, capacity, seated, queued, firstSeen, lastSeen}`.
`firstSeen` and `lastSeen` are RFC 3339 UTC with whole seconds (`2026-11-20T14:00:00Z`). `firstSeen`
is when this beholder process first saw the key (it resets on a beholder restart).

`GET /health` → `200 ok`.

## 4. Operations

`config/beholder.toml`:

| Setting | Default | |
| --- | --- | --- |
| `bind` | `127.0.0.1:7100` | |
| `trust_cf_connecting_ip` | `false` | `true` only behind a Cloudflare tunnel: the per-address limits then use `CF-Connecting-IP`. Anyone can send that header to a beholder reachable directly. |

Logs carry the number of listings when it changes, never a key, endpoint or address.

## 5. Announcing (the seat server)

`[announce]` in `server.toml`: `beholder_url` (empty = don't announce), `endpoint` (required to
announce), `name` (default `[server] name`), `region`, `key_file`. The seat server refuses to start
on a setting that breaks a §2 rule. It announces at start, then every 30 s ± 3 s (10 s timeout per
post). A `429` waits its `Retry-After` (default 60 s, at most 300 s); any other answer or a failure
keeps the cadence. It logs the outcome when it changes (`listed`, `429`, `HTTP <code> <error code>
<field>`, `timeout`, `connect`, `other`). `capacity`, `seated` and `queued` are the seat pool's
live numbers (`GET /seat/v1/status`); `version` is the release.

Under systemd (`ProtectSystem=strict`), put `key_file` in the state directory
(`/var/lib/mkwmp/beholder-ed25519.key`) or create it beforehand: the default next to the ticket key
is read-only there.

## 6. The launcher

1. `GET {beholder}/beholder/v1/servers`.
2. For each item it shows, time `GET {endpoint}/ping` (204, no body) or `GET {endpoint}/seat/v1/status`.
   Servers before 1.4.0 have no `/ping`: fall back to `status`.
3. On pick, read `status` from the server itself and use only what the server says. The listing's
   `name`, counts and `version` are claims of whoever holds the key.
