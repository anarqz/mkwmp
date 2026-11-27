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
    launcher_exe = "C:/.../mkwmp.exe"   # the launcher's executable, forward slashes: the mod starts it as its
                                        # overlay host when no launcher runs ("The overlay host" below)

The file is replaced atomically, and rewritten when the ticket or token changes. The probe is dormant until the game's first network request (Online Play).

## POST /local/v1/seat

Send `Authorization: Bearer <launcher_token>` with no body. The listener is bound to 127.0.0.1 only. The call is idempotent: the first call joins the seat server, and later calls return the current state.

| Answer | Meaning |
| --- | --- |
| `200 {"state":"seated","ticket","lobby","name","room"}` | Use `ticket` as the account token. `lobby` overrides `server`. `room` is below. |
| `200 {"state":"queued","position","seatsTotal"}` | Poll every 2 s and show the position. |
| `409 {"state":"refused","message"}` | Show the message. Retrying won't help; the player fixes `client.toml` (name taken, invalid), or nothing can be fixed: a banned profile or address gets the server's own message, "This profile is banned from MKWMP servers." (SEAT_API.md §2d: `403 BANNED`; no reason, address or date is shown). |
| `503 {"state":"unavailable","message"}` | The seat server is unreachable; stay offline. |
| `401` | Wrong or missing token: re-read mkwmp.toml once. |
| connection refused | The launcher isn't running; stay offline. |

**`room`** (launcher 1.4.0): the lobby room the seat is in, as the seat server's `View.room` gives it (SEAT_API.md §2),
or `null` when there is none (no room, the lobby has not said, its report is older than 60 s, or a seat server older
than rooms). Shape: `{"lobby":"<lobby uuid>","ussid":12,"ussids":[3,12,16],"at":<ms since 1970>}`. `ussids` is
ascending, so `ussids[0]` is the mesh host; `at` is when the lobby reported the room. The launcher refreshes it with
its seat heartbeat (20 s), so it can lag the game by that much. The mod uses it only for its crash records (`room:`
line: lobby, ussid, host = `ussid == ussids[0]`, players = the count, `age_ms` = crash time minus `at`). A launcher
older than 1.4.0 leaves the key out: read absent as `null`.

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
| 200 | `{"friends":[{"name":"Bia","online":true}, ...]}` | Every accepted friend (stored on the server): playing, then online, then offline, each by name. At most 100 entries; never the caller. The game reads its system list once per sign-in, so offline friends are included; the lobby's presence says who is online in the game. `online` = the friend's launcher or game is running. |
| 401 | `{"state":"unauthorized"}` | Wrong or missing token. |
| 503 | `{"state":"unavailable","message"}` | PLAY not pressed yet. |
| 503 | `{"state":"loading","message"}` | The launcher's first friends fetch has not landed within 3 s (an empty list would hide every friend for the whole game session). Retry. |

Served from the launcher's friends cache, which the launcher keeps current with the seat server's friends long-poll (`GET /seat/v1/friends`). Account ids follow the lobby's rule: user id = uuid5(NAMESPACE_URL, "mkwmp-user/" + lower(name)); account id = its first 8 bytes big-endian, top bit cleared, 0 → 1.

## Voice chat: GET /local/v1/voice, POST /local/v1/voice/devices, POST /local/v1/voice/mute

Same bearer. Specified in docs/contracts/VOICE_API.md §4; work without PLAY. `GET` answers
`{"enabled","input","output","inputGain","outputGain","mutes":[{"account","name"}]}` (gains 1-100,
account a u64 in decimal); the two POSTs answer `204`, `400` for a malformed body. Limits: GET burst
10 then 2/s; the POSTs share a bucket, burst 20 then 2/s. Saved in client.toml `[voice]`; the
launcher keeps at most 64 devices per list and 500 mutes.

## The overlay: POST /local/v1/overlay/open, GET /local/v1/overlay/state

Same bearer. The MKWMP mod inside the game asks the launcher to come over the game (the Guide
button, Select+Start, Select held for a second, Shift+Tab) and waits to be told "back to the
game". Design and what each side does: launcher/docs/OVERLAY.md. Both work without PLAY.

`GET /local/v1/overlay/state[?wait=S&seq=N]`

| Status | Body | Meaning |
|---|---|---|
| 200 | `{"open":false,"seq":0,"enabled":true}` | Closed. `enabled`: the launcher can show itself over a game now; while `false` the mod leaves every button to the game and mkw-recomp. `seq` goes up at every change of `open`. |
| 200 | `{"open":true,"seq":5,"enabled":true}` | The overlay is up: the mod keeps the game's pad idle and holds the game as behind a console's HOME menu (plugin API 4 `focus_set`: out of focus, silent, suspended unless the game uses the network). |
| 200 | `{"open":false,"seq":6,"enabled":true,"action":"resume"}` | The player left the overlay (`action`, after a close): `"resume"` = back to the game; `"menu"` = also open mkw-recomp's own menu (plugin API 4 `menu_open`; otherwise as `"resume"`); `"quit"` = "Quit Game": the mod ends the game through the runtime (plugin API 4 `quit`: the mods' goodbyes, the seat given back, the saves finished). A mod or runtime without that entry treats it as `"resume"` and the launcher closes the game's window. |
| 200 | `{"open":false,"seq":0,"enabled":true,"shortcut":"select_hold"}` | Additive, in any 200 answer here and of `open`: the player's overlay shortcut (launcher Settings › Game), one of `guide_or_select_start` (the default, then left out), `guide`, `select_start`, `select_hold`, `start_hold`, `l3_r3`, `lb_rb_start`, `select_lb`, `select_rb`. The mod's pad filter uses it from its next read (launcher/docs/OVERLAY.md §2.1); a name it does not know, or none, is the default. |
| 400 | `{"state":"invalid","message"}` | `wait` or `seq` is not a number. |
| 401 | `{"state":"unauthorized"}` | Wrong or missing token. |

Without `wait` it answers at once: the mod asks every 10 s (every 3 s while there is no answer)
to know whether there is an overlay at all; a launcher older than this answers 404, and the mod
then takes no button. With `wait=S&seq=N` (S at most 25) the answer is held until the state is
no longer N, or S seconds passed (then the same state again): the mod's wait while the overlay
is up (`wait=2`), repeated. Three failed polls in a row and the mod gives the game back by
itself.

`POST /local/v1/overlay/open?by=<guide|chord|hold|keys>&pid=<game pid>[&menu=1][&quit=1][&w=<W>&h=<H>&format=rgba]`

`menu=1`: this game opens mkw-recomp's own menu when the close says `"action":"menu"` (plugin
API 4). Only then does the launcher offer "mkw-recomp menu" in the overlay; without it (plugin
API 3, an older mod) it never answers `"menu"`.

`quit=1`: this game ends itself when the close says `"action":"quit"` (the runtime has the
`quit` entry). The launcher then waits 8 s for the process to end before it sends WM_CLOSE to
the game's window; without `quit=1` it sends WM_CLOSE at once. Either way, a game that still
runs 5 s after the WM_CLOSE is offered to the player for a forced end (the process tree).

The body is the game's picture when the request has one: `W*H*4` bytes, RGBA8, rows top to
bottom, no padding, at most 1920 x 1080 and at least 16 x 16 (the mod sends at most 1280 x 720,
3.7 MB); `Content-Type: application/octet-stream`. No picture: no body and no `w`/`h`/`format`
(plugin API 3, a minimised window). A body over 256 KiB is read only when the token is right
(else `413` and the connection is closed). An all-black picture counts as none.

Before it sends, the game process calls `AllowSetForegroundWindow(ASFW_ANY)` so the launcher may
take the foreground on Windows.

| Status | Body | Meaning |
|---|---|---|
| 200 | `{"open":true,"seq":5,"enabled":true}` | Opening. Sent again while it is open (the game took the focus back and the player pressed again) the launcher comes to the front again; `seq` is unchanged and a picture, if any, replaces the thumbnail. |
| 400 | `{"state":"invalid","message"}` | `by` missing or unknown; `pid` not a number; a body that is not `format=rgba` with a matching `w` and `h`. |
| 401 | `{"state":"unauthorized"}` | Wrong or missing token. |
| 409 | `{"open":false,"seq":N,"enabled":false}` | Not now (setup, an update being applied, no window to show). The mod gives the button back: mkw-recomp's own menu opens where the mod can ask for it. |
| 413 | | A body over the limit. |

Closing is the launcher's: its shell calls `OverlayBridge.ReturnToGame()` (or
`ReturnToGame(OverlayReturn.Menu)` / `ReturnToGame(OverlayReturn.Quit)`), the waiting
`GET .../state` answers `open:false`, and the game runs again and reads the pad once every
button is up (after `"quit"` it ends instead).
There is no `POST /local/v1/overlay/close`: the game is an HTTP client only, and a second
trigger while the overlay is up means "come to the front again", never "close".

Limits: `open` burst 10 then 2/s; `state` burst 20 then 2/s.

## The overlay host: mkwmp-overlay.toml, launcher.json, POST /local/v1/instance/show, POST /local/v1/instance/host

For a game no launcher started (the mod alone, with its own account). The mod starts the
launcher at game start as `mkwmp.exe --overlay-host --parent-pid <game pid> --session
"<mod folder>/mkwmp-overlay.toml"`. In that mode the launcher shows no window and takes no seat,
answers `/local/v1/overlay/...` only, and exits when the game does. Ready is the session file,
which it writes once its local API is up and deletes when it leaves:

```toml
launcher = "127.0.0.1:47820"
launcher_token = "3f1c9a..."
```

The mod reads it as it reads mkwmp.toml's two keys, and is armed by the first
`GET /local/v1/overlay/state` that answers 200 with `enabled:true`. Not started when mkwmp.toml
names a launcher that answers (that launcher started the game and is the overlay).

Where the mod finds the executable, first hit wins: the mod's setting `launcher_path`;
`launcher_exe = "C:/.../mkwmp.exe"` in mkwmp.toml (forward slashes; written by the
launcher beside `launcher`); `"exe"` of `%LOCALAPPDATA%\mkwmp\launcher.json`;
`mkwmp.exe` in the mod's folder. The mod's setting `overlay = 0` turns the overlay off.

`%LOCALAPPDATA%\mkwmp\launcher.json`, written by the launcher at every start:
`{"exe":"C:/.../mkwmp.exe","pid":1234,"port":47820,"token":"3f1c9a..."}`. One launcher
per user (named mutex `Local\mkwmp-launcher`): a second one reads the record and hands its
request to the first, same bearer, then exits.

| Request | Answer | Meaning |
|---|---|---|
| `POST /local/v1/instance/show` | 204 | The player started the launcher while one runs (perhaps as a host without a window): show the window. A host becomes the ordinary launcher and stays when the game ends. |
| `POST /local/v1/instance/host?pid=<game pid>&session=<file>` | 204 | A mod asked for a host while a launcher already runs: that one writes the session file and stands by for the game. |
| | 400 `{"state":"invalid","message"}` | No `pid`, or `session` is not a file named `mkwmp-overlay.toml` in an existing folder. |
| | 409 `{"state":"refused","message"}` | The game is not running, or the file cannot be written. |

Limit: burst 5 then 1/s for the two together.

## Previous races: POST /local/v1/roster

Who was in each online lobby, by the id the **server** gave them, next to the name their game showed. Research:
research/docs/LOBBY-ROSTER-1.8.0.md (the game's member tree). Game 1.8.0 only; both backends (MKWMP, Nextendo).
Same `Authorization: Bearer <launcher_token>`; JSON body; answers 204 (stored), 400 `{"state":"invalid"}`, 401.
Limit: burst 10 then 1/s.

    {"event":"change"|"match"|"left",
     "match":"<referee key, \"%u_%d_%d_%d_%x\">"|null,     // "match" only: the key of the match that starts
     "members":[{"ussid":12,"uid":"<__us.uid, max 1024 bytes>","name":"<PlayerStatic.name, UTF-8, max 64 bytes>",
                 "local":true|false,"lid":0,"sid":"<p2p_sid, 16 hex>"|null}, ...]}  // st == 2 nodes, at most 32

- **When the mod sends it.** `change`: the set of `{ussid, uid, name}` differs from the last one sent (a member
  joined, left, or renamed); at most once a second, the latest set wins. `match`: once per match, at the referee
  request (members complete). `left`: the player left online (members `[]`). Nothing while offline.
- **Trust.** `uid` comes from the server-written `__us` document: the id to identify a player by. `name`, `sid`
  and `lid` are what that member's own game wrote (`PlayerStatic`): shown, never trusted. A row without a `uid` is
  dropped by the launcher.
- **What the launcher keeps.** One file per online session (from the first roster to `left`, or 10 minutes
  without one) in `races\` beside client.toml: `lobby-YYYY-MM-DD-HH-MM-SS.json` (local time of the first roster),
  holding the backend, the server host, every `match` (time, key, members) and, per `uid`, each distinct name with
  first/last seen time. Kept 30 days, 200 files at most. **Local only**: never in a bug report, a log line, a
  crash record or anything sent to an MKWMP server; the player chooses to share it (Previous races › Report).
- The mod logs neither uids nor names (logs go into bug reports): only counts.

## Room ID (removed in 1.0.0)

`/local/v1/room` is gone: mkw-recomp's own number pad (0.1.15 on) answers the game's "search group ID"
keyboard (the probe's keypad of 1.0.0 to 1.4.2 is gone too: the mod draws nothing into the game). The launcher
answers 404, and the probe stops asking after four 404s.
