# Contract: the launcher's local API (`/local/v1`)

The game patch (probe, `winmm.dll`) asks the launcher for a seat. Owners:

- launcher side: `rust/client` (`src/local.rs`, `src/seat.rs`), MACBOOK-MKWMP;
- probe side: `native/probe`, WINDOWS-MKWMP.

## mkwmp.toml (written by the launcher into the game folder)

    server = "<lobby host:port>"        # from GET /seat/v1/status lobbyEndpoint; a hostname is fine
    name = "<username>"
    launcher = "127.0.0.1:47820"        # client.toml local_port
    launcher_token = "<64 hex>"         # random per launcher start
    ticket = "mkw1..."                  # seat = "start" only, once seated
    voice_port = 8907                   # seat status voicePort: the voice relay on the lobby host (0 = off)
    slot = 2                            # loopback testing only

The file is replaced atomically, and rewritten when the ticket or token changes. The probe is dormant until the game's first network request (Online Play).

## POST /local/v1/seat

Send `Authorization: Bearer <launcher_token>` with no body. The listener is bound to 127.0.0.1 only. The call is idempotent: the first call joins the seat server, and later calls return the current state.

| Answer | Meaning |
| --- | --- |
| `200 {"state":"seated","ticket","lobby","name"}` | Use `ticket` as the account token. `lobby` overrides `server`. |
| `200 {"state":"queued","position","seatsTotal"}` | Poll every 2 s and show the position. |
| `409 {"state":"refused","message"}` | Show the message. Retrying won't help; the player fixes `client.toml` (name taken, invalid). |
| `503 {"state":"unavailable","message"}` | The seat server is unreachable; stay offline. |
| `401` | Wrong or missing token: re-read mkwmp.toml once. |
| connection refused | The launcher isn't running; stay offline. |

**Keepalive.** Each POST also keeps the seat. While online, the probe POSTs at least every 20 s.

**Seat release:**
- `seat = "online"`: after 90 s without a POST. The next POST rejoins.
- Both modes: on game exit (when the launcher started the game), or Ctrl-C.
- Otherwise: the seat server's 60 s heartbeat timeout.

**Seat modes** (`client.toml` `seat`):
- `"start"` (default in 0.0.1-rc.1): joins when the launcher starts, and also writes `ticket` into mkwmp.toml.
- `"online"`: joins on the first POST.

**Rejoining.** After a seat-server restart, the launcher rejoins on its own. A renewed or re-issued ticket shows up on the next POST, so re-POST after a lobby auth error.

## GET /local/v1/friends

Same `Authorization: Bearer <launcher_token>` as `/seat`. The probe polls it (at most every 15 s while online) and fills `nn::friends::GetFriendList` with the names, so the game shows the lobby's `SubscribeFriendUsers` entries.

| Status | Body | Meaning |
|---|---|---|
| 200 | `{"friends":[{"name":"Bia"}, ...]}` | Every accepted friend (stored on the server): playing, then online, then offline, each by name. At most 100 entries; never the caller. The game reads its system list once per sign-in, so offline friends are included; the lobby's presence says who is online in the game. |
| 401 | `{"state":"unauthorized"}` | Wrong or missing token. |
| 503 | `{"state":"unavailable","message"}` | PLAY not pressed yet. |

Served from the launcher's friends cache, which the launcher keeps current with the seat server's friends long-poll (`GET /seat/v1/friends`). Account ids follow the lobby's rule: user id = uuid5(NAMESPACE_URL, "mkwmp-user/" + lower(name)); account id = its first 8 bytes big-endian, top bit cleared, 0 → 1.

## Voice chat: GET /local/v1/voice, POST /local/v1/voice/devices, POST /local/v1/voice/mute

Same bearer. Specified in docs/contracts/VOICE_API.md §4; work without PLAY. `GET` answers
`{"enabled","input","output","inputGain","outputGain","mutes":[{"account","name"}]}` (gains 1-100,
account a u64 in decimal); the two POSTs answer `204`, `400` for a malformed body. Limits: GET burst
10 then 2/s; the POSTs share a bucket, burst 20 then 2/s. Saved in client.toml `[voice]`; the
launcher keeps at most 64 devices per list and 500 mutes.
