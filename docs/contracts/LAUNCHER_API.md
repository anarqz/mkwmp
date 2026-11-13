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
