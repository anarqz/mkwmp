# Friend rooms never show in ONLINE -> FRIENDS: cause and fix

2026-11-15. Game build as in RE-MAP.md (`scratch/main.bin`). Decompilation used here:
`scratch/lobby-audit/fr1.c` .. `fr6.c` (Ghidra, `scratch/lobby-audit/dc.ps1`). V = verified in the binary or by a
test, I = inferred.

## 1. Result

The game maps NPLN friend users onto its system friend list **once per sign-in, from the first
`SubscribeFriendUsers` message only**. Every later message on that stream updates an SDK cache that the game
never reads again. A friend that is missing from that first message can never get presence, so the game never
has a `SessionId` to ask about and never calls `BatchGetGameSessions`.

The Rust lobby builds that message from "players signed in on this lobby process right now, and friends
according to the roster so far". In both live runs of this morning the first message P2 received did not
contain P1:

| Run | What happened | Why P1 was missing |
|---|---|---|
| `scratch/lobby-1047.err` | P2 signed in 13:54:39, P1 signed in 13:54:47 | P1 was not signed in yet when P2 subscribed |
| `scratch/lobby-1100.err` | lobby restarted 14:00:36; P1 reconnected 14:02:17 with `ValidateToken` (token of the earlier sign-in, no `IssuePrearrangedUserToken`); P2 signed in 14:02:27 | `friend_snapshot` reads `conn_user`, which only sign-in fills; P1 was online but not in it |

Presence itself was correct (P1's `SessionId` was parsed and pushed to P2's stream); the game dropped it
because no friend entry carried P1's NPLN user id.

The lobby does not log the bytes it sends on these streams, so "the first message was empty" is derived from
the code and the log order, not read from a capture. The flow test in section 4 reproduces both cases.

## 2. What the client requires (question 1)

Friend manager: class vtable `0x325b650`, the game's subclass `0x31dab40` (overrides slots 0, 1, 3, 4, 17, 20,
28 only). Offsets below are in the manager (`mgr`) and in one friend entry (`e`).

### 2.1 State machine (V)

- `mgr+0x88` friend-users state, `mgr+0x8c` presence state. Tick `FUN_0040e860` (slot 10).
- `mgr+0x28` holds the SDK "start" operation (`FUN_01205ea0` -> coroutine `FUN_0120a354`). That coroutine
  calls `ActivateUser`, opens `SubscribeFriendUsers`, **reads the first response** (`FUN_0120b434`), applies
  it (`FUN_01206900` -> `FUN_01206a40`), starts the background reader (`FUN_01213f40`) and only then completes.
- When it completes with status 0 the tick sets `mgr+0x88 = 1`, creates the presence client (slot 15
  `FUN_02228620`) and, once the system friend list was read (`mgr+0x465b9`), calls slot 16 `FUN_02228718`
  while `(mgr+0x8c & ~2) != 1`. Slot 16 returns true, the tick sets `mgr+0x8c = 1`, and slot 16 is not called
  again.
- Writers of `mgr+0x8c` (scan of all stores in the class): init `FUN_02227fe8` (= 0, on sign-in), teardown
  `FUN_02228300` (= 0), the tick (= 1), slot 25 `FUN_02228d50` (= 3 when the presence stream ended, which also
  clears every entry's presence), slot 9 `FUN_02227ee0` (3 -> 1, resubscribes presences). Only 0 leads back
  to slot 16.
- `FUN_01206054` (copy of the SDK friend list) has exactly one caller: slot 16. `e+0x3b0` has exactly one
  store in the class: slot 16.

So the mapping is one-shot per sign-in (V). Nothing the server sends later can redo it (V for the paths
above; I that no other class reads the SDK list).

### 2.2 Slot 16: friend user -> system friend (V)

For each `{user id, account id}` of the SDK list: `FUN_022288b8` finds the entry whose
`nn::friends::Friend::GetAccountId()` equals the account id; then `e+0x390 = user id`, `e+0x3b0 = 1`. After
the loop it subscribes presences (`FUN_01217f9c`, callback vtable `0x325b750`, both filter lists empty, so
request field 2 is empty and the server chooses).

- account id = `strtoull(group.field1, 16)` (`FUN_01206a40`).
- user id = second component of `FriendUser.field2` parsed as `tenants/<t>/users/<u>` (`FUN_00f180e0`); the
  tenant is parsed and not used. A name that does not parse aborts the game.

### 2.3 Presence -> entry (V)

SDK `FUN_0122b7e0`, per `Presence` of a batch:

- state (field 2): 0 = message ignored; 1 = game state 1, attributes copied; 2 = game state 0; 3 = game
  state 2. No title, application or platform field is read.
- name (field 1) parsed as `tenants/<t>/users/<u>/presence` (`FUN_0122cb20`); the callback gets `<u>` only.
- `unsubscribed` (field 5) removes the user from the SDK cache instead.

Game callback `FUN_02229f10`: `FUN_02228934` looks for the entry whose `e+0x390` equals `<u>` (plain string
compare). **No entry: the presence is dropped.** Otherwise:

| Field | Source |
|---|---|
| `e+0x2d0` | the user id `<u>` from the presence name |
| `e+0x2f0` | game state (1 = online) |
| `e+0x2f8` | attribute map |
| `e+0x300` | cached session info (0x90 bytes), cleared when `SessionId` changes |

The first batch after subscribing and later pushes take the same path (`FUN_012215c0` oneof 1 ->
`FUN_0121b5dc` -> `FUN_0122b7e0`); there is no snapshot/update distinction (V).

### 2.4 Session query `FUN_02227ad0` (slot 7) (V)

Runs when `mgr+0x8c == 1`, no query is pending (`mgr+0x38 == 0`) and the last one is at least 10 s old.
An entry contributes its `SessionId` when `e+0x3b0 != 0`, `e+0x2d0` is non-empty, `e+0x2f0 == 1` and the
attribute `SessionId` exists and is non-empty. With at least one id it calls `FUN_0137b9f8`
(`BatchGetGameSessions`). The result handler `FUN_02227414` stores a returned session in the entry whose
`SessionId` equals the session's id (exact string).

## 3. What the lobby sends (question 2)

- Encoding (`rust/lobby/src/social.rs`) matches the client: group `{1 hex account id, 2 [{2 user}]}`,
  `Presence{1 "<user>/presence", 2 state 1/2, 3 attributes}`, batch in oneof 1. No mismatch found (V against
  the parsers above; account id equality with the probe is covered by `social::tests`).
- `push_presence` sends to every `SubscribePresences` stream whose caller passes `are_friends`; fine.
- **Mismatch: the content and timing of the first `SubscribeFriendUsers` message** (`core.rs`
  `friend_snapshot`):
  1. it lists only `conn_user` (signed in on this process now), so a friend who signs in later is missing;
  2. a player connected with a token from an earlier sign-in is not in `conn_user` at all;
  3. with a roster, `are_friends(me, x)` is false until the roster names `me` (up to `roster_secs`, default
     15 s, after sign-in), while the game subscribes about 0.1-0.4 s after sign-in. On a lobby that already
     received a roster, a first sign-in gets an empty first message even when the friend is signed in.
- The later corrections the lobby sends (on `pending_social`, on roster changes, every 20 s) reach only the
  SDK cache.

## 4. Fix (question 3)

Diff: `scratch/lobby-audit/friend-rooms.diff` (applies to `rust/` with `git apply --directory=rust`; built
and tested in a copy, `scratch/fr-rust`). `cargo test -p lobby`: 34 unit + 10 flow tests pass; the new test
fails on the current code.

1. `friend_snapshot` names every friend the lobby can name, signed in or not: users of open connections and
   streams (so token-only connections count), users with a presence record, and the caller's roster friends
   (seated mutual friends, by name, before they sign in). Presence stays the only online signal.
2. The first answer is held until the roster has an entry for the caller (`friend_wait`, at most 20 s).
   Refreshes skip a held stream so nothing is sent early.
3. `mkwmp lobby` calls `core.roster(Vec::new())` when a roster URL is configured, so roster mode applies from
   the first sign-in instead of "everyone is a friend" until the first roster answer.

Core of the change (`rust/lobby/src/core.rs`):

```diff
+    fn awaits_roster(&self, key: StreamKey) -> bool {
+        let Some((me, _)) = self.caller_uid(key) else { return false };
+        self.seat_sessions.contains_key(&me) && self.friends.as_ref().is_some_and(|f| !f.contains_key(&me))
+    }
+
     fn friend_snapshot(&self, key: StreamKey) -> Vec<u8> {
-        let me = self.caller_uid(key).map(|(u, _)| u).unwrap_or_default();
-        let mut online: Vec<&str> = self.conn_user.values().map(String::as_str)
-            .filter(|u| self.are_friends(&me, users::tail(u))).collect();
-        online.sort_unstable();
-        online.dedup();
-        social::friend_users(online)
+        let Some((me, user)) = self.caller_uid(key) else { return social::friend_users([]) };
+        let mut ids: std::collections::BTreeSet<&str> = self.conn_user.values()
+            .chain(self.streams.values().filter_map(|s| s.user.as_ref()))
+            .map(|u| users::tail(u))
+            .chain(self.social.presences.keys().map(String::as_str))
+            .filter(|uid| self.are_friends(&me, uid))
+            .collect();
+        // Mutual friends that hold a seat but have not signed in to the lobby yet.
+        ids.extend(self.friends.iter().filter_map(|f| f.get(&me)).flatten().map(String::as_str));
+        ids.remove(me.as_str());
+        let tenant = users::tenant(&user);
+        let list: Vec<String> = ids.into_iter().map(|uid| format!("{tenant}/users/{uid}")).collect();
+        debug!(user = %me, friends = list.len(), "friend users");
+        social::friend_users(list.iter().map(String::as_str))
     }
```

plus: `friend_wait: HashMap<StreamKey, f64>` set in `open()` when `awaits_roster`; `half_closed` sends nothing
for a held stream; `roster()` releases held streams whose caller now has an entry; `tick()` releases them at
the deadline; `flush()` and the 20 s refresh skip held streams; `stream_ended` forgets them.

Test: `first_friend_users_message_names_friends_before_they_sign_in` in `rust/lobby/tests/flow.rs` (in the
diff). It covers: no message before the roster; the first message names a seated friend who has not signed
in; that friend's later `SessionId` presence uses the same user id; the 20 s deadline; a token-only
connection is listed. The last assertion of `presence_reaches_friends_and_offline_is_announced` changes: a
friend who goes offline stays in the list.

### Not covered, and side effects

- **Host restarted under running games.** A seat issued by an earlier seat-server boot never gets a roster
  entry (`mkwmp/src/lobby.rs` "leave it be"), so that player has no friends in roster mode. The 11:00 run was
  this case on both sides. Test with games launched after the host.
- **Friend takes a seat after my sign-in.** Not in my roster entry when the first message goes out, so not
  listed until I sign in again. The probe's system friend list has the same one-shot limit (RE-MAP.md A.2.6).
- **Delay.** The game's friend start, and with it its own presence `KeepAlive`, now waits up to `roster_secs`
  after sign-in. That the game tolerates this is I (the start coroutine just waits; the stream is opened with
  a 60 s value at `FUN_0120a354`). `roster_secs = 3` in `[lobby]` shortens it.
- `cargo test -p mkwmp --test lobby_smoke` (real process and seat server) passes with the diff (3 tests);
  the other `mkwmp` tests and the rest of the workspace were not run.
- What the card needs after `BatchGetGameSessions` answers (session id equal to the published `SessionId`,
  open flag, member counts: RE-MAP.md 3.4, A.2.3 item 5) is implemented but has never run live, because the
  RPC was never reached.

## 5. Live verification

Start the host, then launch both games from the launcher (fresh seats). `RUST_LOG=lobby=debug`.

1. P2 signs in: `friend users: first answer waits for the roster`, then within `roster_secs`
   `friend users user=<P2 uid> friends=1` (debug, `lobby::core`). P2's `PresenceService/KeepAlive` and
   `SubscribePresences` open after that line, not before.
2. P1 creates a friend room: `presence user=<P1 uid> session=Some("<lobby id>")`.
3. P2 in ONLINE -> FRIENDS: within about 10 s
   `gateway: conn N stream M /nn.npln.matchmaking.v1.GameSessionService/BatchGetGameSessions: in 1 msg/.. B, out 1 msg/.. B, grpc-status 0`,
   repeating about every 10 s while the menu is open. This line has never appeared in any log so far.
4. P2 picks the card: `JoinGameSession`.

If 1 and 2 appear and 3 does not, the one-shot mapping is not the whole story; if 3 appears and the card
still offers no join, look at the `GameSession` returned (section 4, last bullet).

Check without rebuilding (current binary): both players enter online once on the running lobby, wait 15 s,
then P2 goes back to the title and enters online again while P1 stays online. P2's second sign-in finds P1 in
`conn_user` and its own roster entry already present, so the first message contains P1 and step 3 should
appear.
