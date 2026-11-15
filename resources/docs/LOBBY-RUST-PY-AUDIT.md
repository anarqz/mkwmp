# Rust lobby vs Python reference lobby: behaviour audit (2026-11-14)

Scope: `rust/lobby/src`, `rust/gateway/src` against `resources/python-lobby/` (the only server that carried full
matches). Nothing under `rust/` or `native/` was changed; every patch below is a proposal and **none was compiled**.
`cargo test -p lobby` on the tree as found: see the last section.

Marks: **V-code** = read in the Rust/Python source or in a decompile/disassembly made for this audit;
**V-log** = read in a log; **I** = inferred. Addresses are image offsets in `scratch/main.bin`.
Working files: `scratch/lobby-audit/` (`ka1.c`, `ka2.c`, `ka3.c` decompiles, `writer.asm`, `writes.py`,
`timeline.py`, `dc.ps1`).

Logs used:

| name | file | what |
|---|---|---|
| run A | `scratch/game/mkwmp.log` 108 s to 180 s (PC probe; lobby log overwritten) | LAN test 21:16, lobby before commit 519d8c3 |
| run B | same file 370 s to 600 s, and `scratch/lobby.err` (00:20 to 00:24 UTC) | LAN test 21:21, lobby **with** 519d8c3 |
| Python | `scratch/run-lan9.log`, `run-lan10.log`, `run-lan11.log` | 436 real WriteDocuments, two full matches |

## 0. Run B also failed, 108 s after joining, with the presence flood running

Not in the brief, so stated first. In run B (fix 3 active: no "rate limit" line and no KeepAlive stream end in
`lobby.err`), the PC log shows:

- `[490828ms] controller event kind=2 detail=0/0/0/0xc/0x25/0x709ed269`, then Match controller op 10 (leave).
  The lobby sees the PC half-close KeepUserSession at 00:22:54 (`lobby.err` line 70). No `@@RefereeRequest` was
  written before it (no write larger than 155 B after 00:21:10), so the race had not started. V-log
- `0x709ed269` is the game's link-quality error: at least half the peers with RTT above 1000 ms, sampled each
  second over 15 to 30 s (RE-MAP section 4.1, `FUN_00499148`). V (RE-MAP)
- From 374 s until the game exits the PC sends about 210 TLS records per second without pause (48 000 in 230 s).
  In run A the same burst stops after about 250 records because the lobby ended the stream. V-log
- Pia UDP from the PC: about 60 packets/s sent in run A, about 20/s in run B; received from the Deck 15 to 24/s in
  run A, 7.5/s in run B, then 12 in the last 10 s and none after. V-log

Reading: the KeepAlive ping-pong of finding 1 runs for the whole session on both machines and the Pia traffic
drops to a third; the RTT watchdog then ends the session. The causal link is I (strong correlation, one run, no
Deck log for run B). Run A ended differently: `detail=0x1/0x44f3e/0/0x7/0xb/0`, an nn::Result (2318-0551 if decoded
as module 318, description 551); its cause was not examined here.

## 1. HIGH: every KeepAliveResponse{1} makes the client send a heartbeat; acking heartbeats is an endless loop

**Client (V-code, `ka1.c`, `ka3.c`, `writer.asm`).**

- Shared state `S` (ctor at `0x1218100`: `strh #0x100 -> S+0x70`, so `S+0x71 = 1`). Flags: `S+0x58` update pending,
  `S+0x59` heartbeat requested, `S+0x5a` stop, `S+0x5b` restart.
- Reader `FUN_01227d20`: for a response with oneof 1 it sets `S+0x59 = 1` and wakes the writer, then sets the read
  deadline of the stream to `now + N s`, where `N = seconds of Duration field 2` (`msg+0x18`, tag `0x12` in
  serializer `FUN_01239040`; `FUN_011dff38` returns `Duration+0x10` = seconds, nanos ignored; a zero value changes
  nothing). With `S+0x71 == 0` it would be 1 s. Oneof 2 ends the read loop. An empty message does neither.
- `S+0x59` is written nowhere else (scan of `0x1210000..0x1232000`; `0x122b2c4` is another object).
- Writer `FUN_012294c0` (`0x1229638..0x12298a0`): it sleeps until one of the four flags is set. With `S+0x59` it
  builds oneof **2** (heartbeat `{1: "tenants/current/users/current"}`, 33 bytes) and writes it. With `S+0x58` it
  writes the update (oneof 1). It never waits for an acknowledgement. With `S+0x5b` (`0x122a6d0`) it writes oneof 3
  and the coroutine ends; so oneof 3 is the last frame of a stream that is being restarted, not an "open" (I for the
  meaning; the code path is V).
- Read wrapper `FUN_01228364`: every successfully read message, of any content, re-arms the deadline with the
  current interval. The interval starts at 60 (`FUN_01222f00`: call option `0x3c`).

So the stream is **server-paced**: the server asks for a heartbeat with `KeepAliveResponse{1: {2: Duration}}`, the
client answers with one heartbeat, and the server must send something again before the Duration runs out. There is
no unit to tune that would slow the client down: field 2 is only the read deadline.

**Python (V-code, `mkwmp_server.py` l.1093-1106).** One empty message per client frame: no heartbeat, deadline
re-armed for 60 s. V-log: `run-lan11.log` shows the client cancelling KeepAlive about every 60 s
(`HTTP/2 error 8`) and reopening; matches ran through it.

**Rust (V-code).** `core.rs:916` answers every frame, heartbeats included, with
`KeepAliveResponse{1: {1: 60 s, 2: 30 s}}` (`social.rs:81-93`); `core.rs:272-276` does the same above the rate
limit. Each answer triggers the next heartbeat at round-trip speed. V-log: stream totals `in 221 msg/7410 B, out
220 msg/2200 B` = one 150-byte update and 220 heartbeats of 33 bytes (`mesh-fail-2020/lobby.log` line 96). A menu
stream with no update gets nothing from the lobby and is cancelled after 60.1 s (`lobby.log` line 17). Nothing
pings a KeepAlive stream periodically today (`core.rs:1254-1263` covers only the other two streams).

**Correct ack:** none per frame. Send `KeepAliveResponse{1: {1: 60 s, 2: 60 s}}` once when the stream opens (the
heartbeat that comes back marks the player online) and again every 20 s from the timer. Never answer a heartbeat.

```diff
--- a/rust/lobby/src/core.rs
+++ b/rust/lobby/src/core.rs
@@ pub fn open(&self, call: &Call<'_>, out: UnboundedSender<Out>)
                 let _ = out.send(exhausted(why));
                 return;
             }
+            // Presence KeepAlive is paced by the server (reader FUN_01227d20): each KeepAliveResponse{1}
+            // makes the client send ONE heartbeat and sets its read deadline. One now, then every tick
+            // refresh. Never answer a client frame with it: that loops at round-trip speed.
+            if kind == Kind::PresenceKeepAlive {
+                let _ = out.send(Out::Message(social::keepalive_ack()));
+            }
             s.streams.insert(
@@ pub fn message(&self, conn: ConnId, stream: StreamId, body: &[u8])
                 if !allowed && kind == Kind::PresenceKeepAlive {
-                    debug!(?key, "presence frame over the rate limit: acknowledged, not processed");
-                    sends.push((key, Out::Message(social::keepalive_ack())));
-                    s.flush(sends);
+                    debug!(?key, "presence frame over the rate limit: dropped");
                     return;
                 }
@@ fn keepalive(&mut self, key: StreamKey, body: &[u8], now: f64, sends: &mut Sends)
-        sends.push((key, Out::Message(social::keepalive_ack())));
         let Some((uid, user)) = self.caller_uid(key) else { return };
@@ fn tick(&mut self, now: f64)
             let timing = social::presence_timing();
+            let ping = social::keepalive_ack();
             for (k, s) in &self.streams {
                 match s.kind {
                     Kind::Presences => sends.push((*k, Out::Message(timing.clone()))),
+                    Kind::PresenceKeepAlive => sends.push((*k, Out::Message(ping.clone()))),
                     Kind::FriendUsers => sends.push((*k, Out::Message(self.friend_snapshot(*k)))),
--- a/rust/lobby/src/social.rs
+++ b/rust/lobby/src/social.rs
@@ fn timing() -> Msg
-    Msg::new().msg(1, &wire::seconds(WATCHDOG_SECS)).msg(2, &wire::seconds(WATCHDOG_SECS / 2))
+    Msg::new().msg(1, &wire::seconds(WATCHDOG_SECS)).msg(2, &wire::seconds(WATCHDOG_SECS))
```

Expected after the patch: 1 heartbeat per player every 20 s. `tests/flow.rs:487` still finds its ack (sent at
open). Unconfirmed: that the heartbeat at open is harmless in every menu state (the code path is the normal one).

## 2. HIGH for friend rooms and late joiners: `upcsid` is a join counter on the real server, a global number here

- Client: the mesh host is the member with the lowest `upcsid` among the `__pus` documents it holds
  (MESH-RUST-DIFF, V). The write that creates `__pus/<uid>` carries the transform
  `upcsid: counter "__upcsidn", increment 1, maximum 20000` (V-log, all 12 `__pus` writes), i.e. a per-group counter
  in **write order**.
- Python: `user_slot` (global per user), with `__pus` listens deferred by that number. Two players only.
- Rust: `core.rs:640-657` answers the transform with `users.number(uid)` (global, sign-in order since the lobby
  started), `core.rs:1291-1295` defers by `ussid` (same number).
- Consequence (I, same mechanism as H2): whenever a member enters an existing group with a lower global number
  than the current host, it finds no lower-numbered seat, is answered at once, sees only higher `upcsid`s and
  creates a second mesh. A friend room hits this whenever the joiner signed in before the creator
  (`session_join`, `core.rs:1086-1128`); so would `join_window_secs`, `rejoin`, `friend_join_public`.
  Gathered matchmaking lobbies are safe.
- Fix (listed as follow-up in MESH-RUST-DIFF, restated because friend rooms depend on it): make the transform
  result and the `upcsid` fields the seat's `join_seq`, and defer by `join_seq` again.

```diff
--- a/rust/lobby/src/core.rs
+++ b/rust/lobby/src/core.rs
@@ fn write_documents
         for (name, count) in &summary {
             if *count > 0 {
-                transformed.insert(name.clone(), number_of(&mut self.users, name));
+                // upcsid = join order inside the lobby (the real `__upcsidn` counter); lowest hosts.
+                let id = users::tail(name);
+                let seq = self.mm.lobby_of(id).and_then(|l| self.mm.lobbies.get(&l)).and_then(|l| l.seat(id)).map(|s| s.join_seq as i64);
+                let n = match seq { Some(n) => n, None => number_of(&mut self.users, name) };
+                transformed.insert(name.clone(), n);
             }
         }
@@ fn earlier_members_present
-    lobby.seats.iter().filter(|s| s.ussid < me.ussid).all(|s| have.contains(&s.uid()))
+    lobby.seats.iter().filter(|s| s.join_seq < me.join_seq).all(|s| have.contains(&s.uid()))
@@ fn user_session_document(name: &str, uid: &str, ussid: u32, pgn: &str, now: u64)   (add a `upcsid: u64` argument)
-        .with("upcsid", Value::Int(ussid.into()))
+        .with("upcsid", Value::Int(upcsid as i64))      // callers pass seat.join_seq
```

Not covered by this patch: a member that deletes its `__pus` and writes it again while seated keeps its number
(the real counter would give a new, higher one). Test `pus_deferral_follows_the_user_session_number...` must change.

## 3. MEDIUM: a leave deletes other members' documents and republishes `__gs/f`

- Python: never deleted anything and never pushed REMOVED or a changed `__gs/f`.
- Real traffic (V-log): each client registers deferments that delete exactly `@@KeepAlive/<n>`, `Companion/<n>`,
  `DPIS/<n>`, `PlayerStatic/<n>` and `__pgn/All/__pus/<uid>`, and deletes its own `__pus` itself (`Write.4`) when
  it leaves the P2P group. Rust runs both (`core.rs:696-700`, `1177-1193`), which is the documented server duty;
  the REMOVED encoding is V in the client reader (RE-MAP section 2) but has never been confirmed in live play.
- Beyond that, `core.rs:1194-1201` deletes **every document last written by the leaver**. V-log: a player writes
  the signalling inbox of each peer, `__pgn/All/__stu/<peer uid>`, and the shared `__mt/nat_traversal`. Each
  remaining member therefore gets REMOVED on its own `__stu/<uid>` listen. What nplnd does with that is unknown.
- `core.rs:1213-1219`: when the first seat leaves, `__gs/f` is pushed MODIFIED with a new `mcn` to every `__gs/`
  listener. The client's reaction to a changed fixed document mid-session is unknown; `mcn` does not choose the
  host (MESH-RUST-DIFF).
- Matters with three or more players when one leaves. Unconfirmed in both directions.

```diff
--- a/rust/lobby/src/core.rs
+++ b/rust/lobby/src/core.rs
@@ fn leave
+        // Only the leaver's own documents: a peer's `__stu/<peer uid>` inbox and `__mt/nat_traversal`
+        // are last written by the leaver too, and the game registered deferments for what must go.
+        let own = [format!("/{uid}"), format!("/{ussid}")];
         for name in lobby.docs.written_by(uid) {
-            if name.starts_with("docs/@@Referee") {
+            if name.starts_with("docs/@@Referee") || !own.iter().any(|s| name.ends_with(s.as_str())) {
                 continue;
             }
@@
             if was_host {
                 lobby.host = lobby.seats.first().map(|s| s.uid().to_owned());
                 lobby.closed = true;
-                let doc = fixed_document(lobby, &cfg, &policy, t);
-                publish(lobby, &doc, ChangeKind::Modified, sends);
             }
```
(then drop the unused `cfg`/`policy` binding at the top of `leave`).

## 4. MEDIUM: a write whose bearer is not recognised is acknowledged and thrown away

- Python stored every WriteDocuments whoever sent it (its Gamesync tokens were opaque; it never resolved the
  writer on the Gamesync connection). V-code
- Rust: `core.rs:662-666` returns the normal response and stores nothing when `caller()` is `None`; GetDocument
  returns an empty document. Then no `@@RefereeResult` is ever published and the race start times out (36 s).
- When it happens: the Gamesync token is older than two hours (`users.rs:96`: TTL 3600 s plus 3600 s grace); or
  the game refreshed through `Gamesync/RefreshToken` without a usable bearer: `core.rs:593-605` then mints
  `mkwmp-gamesync-anonymous-...`, because field 2 is only accepted when it is a UUID. RefreshTokenRequest is
  `{1 str, 2 str}` (NETCODE); no RefreshToken call exists in any log, so its content is unconfirmed, and so is
  whether the game refreshes at all within a session.

```diff
--- a/rust/lobby/src/core.rs
+++ b/rust/lobby/src/core.rs
@@ fn caller(&self, key: StreamKey) -> Option<String>
-        self.conn_user.get(&key.0).cloned().or_else(|| self.streams.get(&key).and_then(|s| s.user.clone()))
+        self.conn_user.get(&key.0).cloned()
+            .or_else(|| self.streams.get(&key).and_then(|s| s.user.clone()))
+            // The Gamesync connection carries this user's KeepUserSession stream.
+            .or_else(|| self.session_user.iter().find(|(k, _)| k.0 == key.0).map(|(_, uid)| format!("tenants/current/users/{uid}")))
@@ fn gamesync_token
         let user = caller.or_else(|| {
-            let id = crate::wire::str_field(body, 2).ok().flatten()?;
-            uuid::Uuid::parse_str(&id).is_ok().then(|| format!("tenants/current/users/{id}"))
+            [1, 2].into_iter().filter_map(|f| crate::wire::str_field(body, f).ok().flatten()).find_map(|v| {
+                if uuid::Uuid::parse_str(&v).is_ok() {
+                    return Some(format!("tenants/current/users/{v}"));
+                }
+                // RefreshToken: our refresh token is "<access>-refresh".
+                self.tokens.subject(v.strip_suffix("-refresh")?, now as u64)
+            })
         });
```

## 5. MEDIUM/LOW: limits a 2 to 12 player session can reach

| limit | where | reached when | proposal |
|---|---|---|---|
| Sign-in 10, then 12/min **per IP** | `lobby/limits.rs:56` | 11th player behind one address (LAN party, shared NAT) signs in | `rate(40.0, 1.0)` |
| NAT probe replies 20/s, burst 40 per IP | `gateway/nat.rs:31` | V-log: one client sends 12 probes in one second at a join; 4 or more players behind one address joining together | `PerIp::new(200.0, 400.0)` (the reply is as long as the probe) |
| TURN allocations 8 per IP, kept 3600 s | `gateway/turn.rs:36`, `426-428`, `gateway/limits.rs:30` | allocations are keyed by client address:port; the 9th in an hour from one address gets 486. Python had no quota | at the quota, evict that address's allocation with the earliest `expires` |
| TURN 512 KiB/s per allocation, both directions | `gateway/limits.rs:32` | a fully relayed 12 to 24 player race (I, no measurement) | measure before changing |
| Documents per lobby 4096 | `lobby/limits.rs:76`, `core.rs:683-687` | each match leaves one `@@RefereeRequest` and `@@RefereeReport` per player and one result (25 per 12-player match): about 160 matches. Past the cap the request is refused **and** `continue` skips `referee.on_write` (`core.rs:691`), so races stop starting | delete a match's request and report documents when its result goes final; never skip the referee |
| KeepUserSession frames 300, then 60/s; over it the stream is **ended** | `limits.rs:65`, `core.rs:278-284` | not by the game (17/s measured, 11 listens per join). An ended session stream is a lost session | keep, or raise to `rate(600.0, 120.0)` |
| WriteDocuments 300, then 60/s per user | `limits.rs:63` | measured peak 14/s for two players (V-log); signalling grows with peers, still far below | none |
| TCP connections 32 per IP | `gateway/limits.rs:24` | 16 players behind one address (2 connections each) | none for 12 |

Never reached in the logs: writes per request (max 2 of 64), deferments per seat (5 of 64), listens per stream
(11 of 64), document size (697 B of 16 KiB), request size (773 B of 4 MiB), concurrent streams (about 12 of 128).

TURN and the NAT responder otherwise answer exactly like the Python (V-code: same attributes, lifetimes, keys,
ChannelData padding, probe reply). TURN was never used on the LAN: no allocation in any lobby log and no packet to
port 3478 in the PC log (V-log), so the relay is live-untested with both servers.

## 6. LOW, or checked and equal

- **`exists` preconditions.** V-log: only `@@RefereeRequest/<ussid>/Request/<key>` carries one (`{1: 0}`, create),
  12 of 436 requests, always a new name. Rust's no-op on an existing document (`docs.rs:181-184`) is never
  exercised; Python overwrote. No `exists: true` in real traffic.
- **Value re-encoding and timestamp nanos.** V-log: `writes.py` re-encodes all 436 requests the way `value.rs`
  does: 0 differences. Kinds seen: bool, int, float (field 4, kept raw), string, bytes, list, map. No Timestamp
  value was ever written, so `value.rs:146` is not exercised. Document times are whole seconds in both servers.
- **Race start and referee.** Same sequence as `run-lan11.log`: listen on `@@RefereeResult/`, first request opens
  `{request_id, status 0, result []}`, reports merge, final once the order is complete and everyone reported, 10 s
  grace. `ussid_map` is `{"0": 1, "1": 2}` and `users` `[1, 2]` in the capture, as `referee.rs:143-152` reads them.
  Differences, all Rust-side improvements: result id salted per lobby, `ratings` keyed by the real ussids (Python:
  `1..total`), leavers retired. No extra KeepUserSession op, deferment or document is involved at race start
  (V-log: nothing but `@@KeepAlive` between the vote and the request).
- **Change kinds and order, listen answers, op acks, transform result placement, PlayerStatic merge writes,
  `@@KeepAlive` (empty mask, empty fields), assignment message, IssueToken response, AllocateIceServerSet,
  BulkGetDocuments:** equal to the Python (V-code). Request in the capture: `IssueToken {1: "userSessions/<uid>",
  2: "<uid>"}`.
- **PlayerStatic `name`.** Rust replaces it with the launcher name (`core.rs:674-678`). The Python had the same
  code but could never resolve the writer on the Gamesync connection, so real matches ran with the game's own
  name. Today's run reached a shared world with the replacement.
- **`maxu`** 24 vs 2: upper bound only (MESH-RUST-DIFF); 2 would refuse a third player. **`mcn`**: first seat vs
  lowest number; not the host selector. **GetDocument `__gs/f`**: the Python served a "solo" document there
  (unknown caller), Rust serves the lobby's; Rust is the consistent one.
- **`ussid`** is global and grows with every user that signs in (`users.rs:118-138`); the real counter is small.
  No client limit found; unconfirmed above 2.
- **Gathering.** Rust assigns 10 s after the second tracker; Python at once. The client's own search timeout is
  150 s (V-log, both Rust logs).
- **`__pus` deferral stall.** A lower-numbered member that never opens its session blocks the others' `__pus`
  listen until `connect_timeout_secs` = 120 s (`lobbies.rs:64`). Sessions open within 0.3 s of the assignment in
  every log; 30 s would do.
- **Seat removal.** 5 s without a KeepUserSession stream (`lobbies.rs:63`), or the seat server reporting the
  launcher session gone (`core.rs:370-374`), removes the player mid-race. Python never removed anyone.
- **Oneof 3 on KeepAlive** is treated as "open, online" (`social.rs:128-130`); per finding 1 it is the closing
  frame of a restarted stream. Harmless: the stream ends right after.
- **Subscriptions.** `SubscribeMaintenanceSchedules` gets one empty message, is cancelled by the client after 60 s
  and reopened; same as Python.

## 7. Not determined

- Whether the flood is the cause of `0x709ed269` in run B (section 0), and the cause of run A's error.
- What the client does on REMOVED of its own `__stu` inbox, and on MODIFIED `__gs/f`.
- RefreshToken content and timing.
- Whether Pia ever allocates on the TURN relay, and its refresh behaviour.
- Everything with more than two players: no log exists.

## 8. Tests

`cargo test -p lobby` in `rust/` on the tree as found (with the uncommitted presence diagnostics in `core.rs`):
34 unit tests and 9 flow tests pass. None of the patches above was applied or compiled. Tests that must change
with them: `pus_deferral_follows_the_user_session_number_not_the_search_order` (finding 2); the leave test at
`flow.rs:284` if it counts REMOVED pushes (finding 3).
