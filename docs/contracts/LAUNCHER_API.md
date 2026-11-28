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

## The current lobby: POST /local/v1/roster

Who is in the game's online lobby **now**, by the id the **server** gave them, next to the name their game shows.
Research: research/docs/LOBBY-ROSTER-1.8.0.md (the game's member tree). Game 1.8.0 only; both backends (MKWMP,
Nextendo). Same `Authorization: Bearer <launcher_token>`; JSON body; answers 204 (taken), 400 `{"state":"invalid"}`,
401. Limit: burst 10 then 1/s. The request is unchanged since 1.5.4 (the mod needs no change for it).

    {"event":"change"|"match"|"left",
     "match":"<referee key, \"%u_%d_%d_%d_%x\">"|null,     // "match" only: the key of the match that starts
     "members":[{"ussid":12,"uid":"<__us.uid, max 1024 bytes>","name":"<PlayerStatic.name, UTF-8, max 64 bytes>",
                 "local":true|false,"lid":0,"sid":"<p2p_sid, 16 hex>"|null}, ...]}  // st == 2 nodes, at most 32

- **When the mod sends it.** `change`: the set of `{ussid, uid, name}` differs from the last one sent (a member
  joined, left, or renamed); at most once a second, the latest set wins. `match`: once per match, at the referee
  request (members complete). `left`: the player left online (members `[]`). Nothing while offline.
- **Trust.** `uid` comes from the server-written `__us` document: the id to identify a player by. `name`, `sid`
  and `lid` are what that member's own game wrote (`PlayerStatic`): shown, never trusted. A row without a `uid` is
  dropped by the launcher; a name loses what cannot be shown (control characters, direction marks).
- **What the launcher holds, in memory only** (`Mkwmp.Core/Lobby.cs`, `CurrentLobby`): the members of the last
  roster (this PC's player first), and for this online session (from the first roster to `left`, the game's end, or
  a roster on another network) each distinct name seen under a `uid`, with when it was first seen: a member with more
  than one is flagged as renamed. `left` forgets all of it. **Nothing is written to disk**, logged (only counts) or
  sent to any server; the player shares it by copying a report, or on an MKWMP server by opening Report Abuse filled
  in. It is shown by the launcher's Current Lobby page and, in the game, by the overlay page's Lobby pane (below).
- **"Previous Races" (1.5.4) is gone.** The launcher no longer stores any history: at start it deletes a leftover
  `races\` folder beside client.toml (the old `lobby-*.json` files) and logs only how many files went.
- The mod logs neither uids nor names (logs go into bug reports): only counts.

## In-game toasts (v1): GET /local/v1/toasts, POST /local/v1/toasts/{id}/action

Ported from Infinity's v4 overlay: **the game draws, the launcher decides.** The launcher's toasts (a friend is
online, a request, an answer to something the player did, a warning) are drawn by the MKWMP mod **inside the game's
own frame, at the top right**, while the game keeps running, focused and full screen; no window is put over it. The
launcher decides everything (which toasts, their order, their times, their words, the colours); the mod only draws
what it is told. Launcher: `Mkwmp.Core/Overlay/ToastBridge.cs`, `Mkwmp.App/Overlay/InGameToasts.cs`. Same bearer as
every route; both routes are additive (a launcher without them answers 404, and the mod then draws nothing).

`GET /local/v1/toasts[?wait=S&seq=N]`

What the launcher's ToastHost shows now, in its order (oldest first, at most four). Empty while the launcher's own
window is visible (it shows them itself) and when no toast is up.

- Without `wait`, or when `seq` is not the current one: the answer at once.
- With `wait=S` (seconds, at most 25) and `seq=N` equal to the current one: held until what is shown changes, or `S`
  seconds, whichever comes first; then the answer.
- `seq` moves on at every change of the list, its texts, its times or the colours. Nothing else wakes the poll.
- Limit: burst 20 then 5/s. The mod asks with `wait=10` in a loop, never more than ten times a second.
- 400 `{"state":"invalid","message"}`: `wait` or `seq` is not a number. 401 `{"state":"unauthorized"}`.

```json
{
  "v": 1,                                // the version of this answer's shape
  "seq": 12,
  "toasts": [
    {
      "id": "t7",                        // "t<n>": once per launcher run
      "kind": "friend",                  // info | success | warning | error | friend
      "title": "Luna is racing online now",
      "body": "Nextendo Network",        // may be ""
      "icon": "play",                    // info | check | warning | error | user | play | userPlus (Themes/Icons.xaml)
      "expiresInMs": 4200,               // it starts to fade then (counted from this answer's arrival)
      "stayMs": 5000                     // its whole time: 5 s news, 6 s warnings and answers, 8 s errors
    }
  ],
  "suppress": [],                        // reserved (Infinity: keys a hold-to-accept toast keeps from the game); always []
  "palette": {                           // the player's theme, #AARRGGBB, by role
    "toast": "#FF4E4E55", "rule": "#FF5E5E64", "accent": "#FF5CB8FF", "good": "#FF3DD27A", "warn": "#FFFFB933",
    "bad": "#FFFF6B61", "text": "#FFF3F3F3", "textMuted": "#FFB1B1B6", "track": "#FF646469", "glyph": "#FFF3F3F3",
    "onGlyph": "#FF2A2A2D", "onAccent": "#FF0F1A26", "surface": "#FF434349"
  }
}
```

(The comments are this document's; the answer is plain JSON on one line.) Every text is plain UTF-8: only `\"`, `\\`
and a control character (`\u00XX`) are escaped, so a name with an emoji arrives as the emoji. The mod:

- counts each toast down from the answer's arrival and fades it out at `expiresInMs` (220 ms; gone at 260 ms);
- slides new cards in (240 ms from 120 units to the right, with a 200 ms fade in) and takes out those the launcher
  no longer lists the same way; a card that stays keeps its place;
- draws in the launcher's stage units (a 640-wide column, 48 from the right edge, from 40 down, 14 between cards,
  corner radius 18; 1080 units high) scaled to the frame's height. A card: `palette.toast` with a 1.5-unit
  `palette.rule` outline (`palette.bad` for `error`), a 68-unit disc tinted by kind at 13 % (`accent` for info and
  friend, `good` success, `warn` warning, `bad` error) with the icon in that colour, the title (bold, 28 units,
  `palette.text`, wrapped) and the body (24 units, `palette.textMuted`, wrapped; none when ""); padding 24 x 20,
  20 between the disc and the text. This is the card of the launcher's `Controls/ToastHost.xaml`;
- ignores keys it does not know, keeps a default for a missing one, and draws at most 12 toasts; no sound;
- after three failed polls drops what it holds (the cards fade). Back-off: 404 / 401: 30 s, 429: 1 s, else 2 s.

**Which side shows the toasts.** The mod's polls are the signal that the game draws them: while the mod is waiting
in a poll, or asked within the last 15 s (`ToastBridge.GameDraws`), the launcher shows nothing over the game. When no
poll came for 15 s (no mod, a mod without this), the launcher's fallback shows them: a small window at the top right
of the game's monitor, borderless, transparent, topmost, click-through and never activated (`WS_EX_NOACTIVATE |
WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED`, shown without activation). Windows only (off under
Wine/Proton unless `MKWMP_INGAME_TOASTS=1`); an exclusive full-screen game draws over it. While the launcher is behind
a running game its news goes to these toasts, not to the system's notifications.

**A launcher without a window** (the overlay host before the player opened it) serves the core's own toasts here,
with the same kinds and times, four at most: the game is then the only place they show.

`POST /local/v1/toasts/{id}/action`

Body `{"action": "dismiss" | "accept" | "decline"}`; `{id}` is the toast's id, percent-encoded (at most 64
characters).

| Status | Body | Meaning |
|---|---|---|
| 200 | `{"ok":true}` | `dismiss`: the toast goes now, in the launcher too (the list changes: the poll answers). |
| 404 | `{"state":"gone","message"}` | No such toast is shown (it went meanwhile). |
| 409 | `{"state":"refused","message"}` | `accept`, `decline`: reserved names (Infinity's invite toasts); no toast of this launcher takes them. |
| 400 | `{"state":"invalid","message"}` | Not JSON, or another action. |
| 401 | `{"state":"unauthorized"}` | Wrong or missing token. |

Limit: burst 10 then 2/s. The mod needs no input for the toasts: `dismiss` is there for a later step.

## In-game overlay (v1): GET /local/v1/overlay/view, POST /local/v1/overlay/action

Ported from Infinity's v4 overlay: the overlay **page** drawn by the mod inside the game's own frames, in the
launcher's look, while **the game keeps running** (it is not paused, and keeps the screen, the focus and its full
screen). The player's overlay shortcut opens it; B, the same shortcut or Esc close it; while it is open the game
reads an idle pad, and after it closes until every button is up; the pad drives the page. The launcher decides (the
entries, every text in the player's language, the colours, what can be done now) and does the actions; the mod draws
and sends what the player chose. The outcomes are the launcher's toasts (above), which the mod keeps drawing over the
page. Launcher: `Mkwmp.Core/Overlay/GameOverlay.cs`.

These routes live **beside** `GET /local/v1/overlay/state` and `POST /local/v1/overlay/open` (the launcher's window
over the game), which are unchanged and still serve the mod of 1.5.4. **Which overlay the player gets:** while the
mod polls `view` (it is waiting in a poll, or asked within the last 15 s: `GameOverlay.GameDraws`), the launcher's
own shortcut watcher never opens the launcher's window over that game: the shortcut is the mod's. A mod that draws
the page must not also call `overlay/open`. A mod that gets 404 on `view` (an older launcher) keeps the window overlay.

`GET /local/v1/overlay/view[?wait=S&seq=N]`

The whole page as data. Without `wait`, or when `seq` is not the current one: at once. With `wait=S` (at most 25)
and the current `seq`: held until the view changes, or `S` seconds, or the next minute (the clock on the page),
whichever comes first. `seq` moves on at every change of anything below (the lobby, the language, the theme, an
action running). Limit: burst 20 then 5/s. 503 `{"state":"unavailable"}` while the launcher is starting; 400 for a
`wait` or `seq` that is not a number; 401.

```json
{
  "seq": 41,
  "v": 1,                                         // the version of this view's shape and of the actions
  "shortcut": "guide_or_select_start",            // the player's overlay_shortcut (as in overlay/state; here always sent)
  "clock": "10:21 PM",                            // the header's clock, as the launcher writes it
  "status": null,                                 // reserved (Infinity: the header's network and battery)
  "start": "lobby",                               // the pane the page opens on: "lobby" while in a lobby (its players
                                                  // right away), else "game"
  "palette": { "background": "#FFEBEBEB", "surface": "#FFFFFFFF", "surfaceAlt": "#FFF6F6F6", "modal": "#FFF0F0F0",
               "text": "#FF2B2B2B", "textMuted": "#FF6C6C6C", "textFaint": "#FFAEAEAE", "accent": "#FF1E64E6",
               "accentSoft": "#1F1E64E6", "onAccent": "#FFFFFFFF", "rule": "#FFC6C6C6", "bar": "#FF2B2B2B", "good": "#FF16A34A",
               "warn": "#FFD98200", "bad": "#FFE0352B", "dim": "#73000000", "focusFill": "#FFFFFFFF", "track": "#FFCFCFCF",
               "knob": "#FFFFFFFF", "hover": "#0D000000", "pressed": "#1A000000", "glyph": "#FF2B2B2B", "onGlyph": "#FFEBEBEB",
               "toast": "#FFFFFFFF", "outline": "#FF8A8A8A" },   // the player's theme with its contrast (no window: light or dark)
  "text": { "title": "MKWMP", "ok": "OK", "back": "Back", "backToGame": "Back to the Game", "close": "Close",
            "cancel": "Cancel", "select": "Select" },
  "entries": [ { "key": "game", "title": "Game", "icon": "gamepad", "separator": false, "locked": false, "soon": false },
               { "key": "lobby", "title": "Current Lobby", "icon": "friends", "separator": false, "locked": false, "soon": false } ],
  "busy": [ "report" ],                           // actions running now (their buttons are greyed)
  "game": { "name": "MKW", "status": "mkw.example.net",       // where it plays: the server, "Playing on Nextendo Network", "Offline"
            "backToGame": "Back to the Game",
            "quit": { "label": "Quit Game", "title": "Quit MKW?", "body": "…", "yes": "Quit Game", "no": "Cancel" } },
  "lobby": {
    "title": "Current Lobby",
    "subtitle": "PLAYERS · 6 · MKWMP server · mkw.example.net",   // "" out of a lobby
    "inLobby": true,
    "network": "mkwmp",                           // mkwmp | nextendo
    "note": "",                                   // out of a lobby, the pane's one line ("You're not in an online lobby. …")
    "you": "You",                                 // the pill on the player's own row
    "players": [                                  // this PC's player first, then the game's order; at most 32
      { "id": "p5",                               // the member's handle for this online session: what an action names
        "name": "NotTurbo",                       // what their game shows now ("(no name)" in the player's language for none)
        "initials": "NT", "disc": "#FF2F6BFF",    // the launcher's avatar: one or two letters on a disc of that colour
        "you": false,                             // true on the player's own row
        "renamed": true,                          // more than one name under the same Player ID in this online session
        "renamedLabel": "Used 3 names",           // the row's flag, in the warning colour ("" when not renamed)
        "renamedBody": "This player used 3 different names with the same Player ID.",   // the player pane's warning card
        "names": [ "TurboShell", "xX_Drift_Xx", "NotTurbo" ],   // every name of this session, oldest first
        "playerId": "0000000f-4c1e-…",            // the id the server gave them (at most 128 characters here)
        "canReport": true,                        // Report to the server's host is offered (see "report")
        "canCopyReport": true } ],                // Copy Report is offered (everyone but the player)
    "player": { "namesHeader": "NAMES · {n}",     // the mod puts the count in place of {n}
                "idLabel": "Player ID", "idCaption": "Given by the server. …",
                "report": "Report", "copyReport": "Copy Report", "copyReportCaption": "Copies a text report …",
                "no": "Cancel" }
  },
  "report": { "label": "Report", "caption": "The server's host reviews reports. …", "title": "Report Abuse",
              "what": "What Happened", "send": "Send Report", "no": "Cancel",
              "enabled": true, "why": "",         // greyed with why: not registered or not 18+, one report already waiting
              "reasons": [ { "code": "cheating", "label": "Cheating" }, … ] }   // the server's; null on Nextendo and offline
}
```

(The comments are this document's.) Every text is the launcher's own catalog entry in the player's language, plain
UTF-8 as for the toasts. The mod ignores keys it does not know; a missing one keeps its default.

The page (Infinity's layout): a list of `entries` at the left under `text.title` and the `clock`; the focused
entry's pane at the right; a hint bar at the bottom (A `text.select` / `text.ok`, B `text.back`).

- **Game** pane: `game.name`, `game.status`, a button `game.backToGame` (closes the page), and `game.quit.label`,
  which asks first (`title`, `body`, `yes`, `no`) and then sends `quitGame`.
- **Lobby** pane: `lobby.title` and `lobby.subtitle`; out of a lobby only `lobby.note`. In one, a row per player:
  the disc with `initials`, `name`, the `lobby.you` pill when `you`, and when `renamed` the warning mark with
  `renamedLabel` and the `names` joined by " · " under the name. A on a row that is not `you` opens that player:
  `renamedBody` (when renamed), `player.idLabel` with `playerId` and `player.idCaption`, the names under
  `player.namesHeader`, and the actions: **Report** (`player.report`; only when `canReport` and `report` is not
  null; greyed with `report.why` when `report.enabled` is false): the mod asks the reason (`report.what`,
  `report.reasons`) and then `report.send`, and sends `report`; **Copy Report** (`player.copyReport` with
  `player.copyReportCaption`; when `canCopyReport`): sends `copyReport`. There is no Add Friend here. A player who
  leaves the lobby disappears from `players`: an open player pane closes.

`POST /local/v1/overlay/action`

Body `{"action": "<name>", "args": {…}}`. The launcher checks it against what it shows now.

| Action | Args | What the launcher does |
|---|---|---|
| `show` | `pane`: `game`, `lobby`, or `""` (closed) | Remembers what the page shows (`GameOverlay.IsOpen`, `Pane`). The mod sends it when the page opens, changes pane and closes. |
| `report` | `id` (a player's `id`), `reason` (a `report.reasons` code) | On an MKWMP server: `SendReportAsync(name, reason, "Player ID …. Names used: …")`, the server's own Report Abuse. Not registered / not 18+, a name that server cannot know, one report already waiting, Nextendo: 409 with the launcher's line. |
| `copyReport` | `id` | Puts a plain-text report on the clipboard (the Player ID, the names of this session with when each was first seen, the network, the launcher's version) and shows a toast that says who to send it to (Nextendo staff, or the server's host). Nothing is sent anywhere. 409 when there is no clipboard (a launcher without a window). |
| `quitGame` | | Answers 200; the mod then ends the game itself through the runtime (plugin API 4 `quit`). The mod asks first. |

- 200 `{"ok":true}`: done (how it went is the launcher's toast, and the view).
- 202 `{"state":"pending"}`: still at it after 15 s (the toast and the view follow).
- 409 `{"state":"refused","message":"…"}`: not now; the message is the launcher's, in the player's language (the
  same action already running: `"busy"`).
- 404 `{"state":"gone","message"}`: what it names is not there (any more): a player who left, the player's own row.
- 400 `{"state":"invalid","message"}`: not JSON, an unknown action, bad args. 401; 503 while the launcher starts.
- Limit: burst 30 then 5/s.

Fixtures for the mod's tests, written by the launcher's harness from its demo data (the exact bytes of both
answers): `mkwmp.exe --demo --shot <png> --toasts --toasts-json <file> --overlay-view-json <file>`.
## Room ID (removed in 1.0.0)

`/local/v1/room` is gone: mkw-recomp's own number pad (0.1.15 on) answers the game's "search group ID"
keyboard (the probe's keypad of 1.0.0 to 1.4.2 is gone too: the mod draws nothing into the game). The launcher
answers 404, and the probe stops asking after four 404s.
