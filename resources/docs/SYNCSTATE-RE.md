# SyncState timeout (0x46b96f91) and 2318-0551 at the start of a friend-room race

2026-11-15. Game build as in RE-MAP.md (`scratch/main.bin`, image offsets). Logs: `scratch/syncstate-1535/`
(run A = `*-previous.log` 18:14Z, run B = 18:34Z, run C = `runC/` 18:41Z). Decompiles and scripts made for this:
`scratch/syncstate-re/` (`ss1.c` .. `ss13.c`, `pia1.c`, `dc.ps1`, `rates.py`, `sizes.py`, `speed.py`,
`piatab.py`). Nothing under `rust/` or `native/` was changed. V = read in code or in a log, I = inferred.

> **Update (same day, after the room trace): read section 9 first.** The trace shows the joiner carries static
> flag `RandomMatchSession`: it treats the friend room as a public Race lobby. The cause found in code is a
> missing session attribute: the joiner reads the integer attribute **`rule`** from the `GameSession` the lobby
> returns, and a missing `rule` reads as 0 = public Race. This is a lobby fix (section 9.4). It supersedes
> answer 4 below ("nothing to fix in `rust/lobby`"), the candidate ranking of section 5 and the test plan of
> section 7. Sections 2 to 4 stand.

## 1. Short answers

1. **SyncState waits for a P2P value, not for the lobby.** It waits until every player in `JoinPlayers` has a
   per-station "sequence state" of 1 or 2. That state is one element of the `System_NetStation` replica, sent
   by each station over Pia (Reliable). No Gamesync document, field or RPC is read. V
2. **The joiner must produce it**, by running the node `ReadyVoteSetter(true)` of its own `PrivateRoom` graph
   (sets sequence flag `ReadyVote`). The joiner only gets there after it accepted the host's start event in
   `PrivateRoomMain`. In runs B and C the host (P1) never saw P2's state become non-zero in 25 s. V for the
   mechanism; **why P2 did not get there is not proven** (section 5 ranks the candidates and section 7 gives
   the trace that settles it).
3. **2318-0551 = Pia result `0x8c52`**: in the mesh code it is raised when the *local* station is the one that
   disappeared from (or conflicts with) the mesh data sent by the host: the Deck was dropped from the mesh. In
   run A the Deck's game thread stalled for 7.7 s (4 frames) right before; the falling send rate is that stall,
   so it is the cause of the drop and a symptom of the Deck's load. V for the code and the stall, I for "the PC
   dropped it" (no PC log of run A).
4. **Lobby:** identical handling for rooms and matchmade lobbies in this phase, and no lobby traffic at all
   during the 25 s. Nothing to fix in `rust/lobby` for this failure. V (log + code).

## 2. SyncState (`AIGlobalSeqSyncState`, vtable `0x3173f60`) V

RE-MAP's name for `FUN_01a82d84` is right: vtable `0x3173f60` has name getter `0x1a82fd8`
("AIGlobalSeqSyncState"), enter `FUN_01a82c90` (+0xe0), update `FUN_01a82d84` (+0xe8).

- Enter `FUN_01a82c90`: `node+0x28 = expected mask` (`FUN_01a82d24`: blackboard `JoinPlayers` and not
  `node+0x2c`, the players marked disconnected), `node+0x30 = now` (wall clock).
- Update `FUN_01a82d84`: recompute the expected mask; for every bit `i` set in the node parameter `state`
  (`**(node+0x40)`), `FUN_0211e100(S, &m, &i)` returns the mask of players whose `S.state[p] == i`; OR them.
  When `expected & ~got == 0` the node leaves by its Success pin. Otherwise, once `now - node+0x30 >= 25000 ms`,
  it posts `0x46b96f91` (`FUN_02624ad8`, priority 2) every frame. It never gives up by itself.
- `state` parameter in `PrivateRoom.module.ainb`: immediate value **6** (entry at file offset `0x2eb8`:
  name `state`, value 6) = states 1 and 2 are accepted.
- `S` = `*(*(G+200)+8)+8` with `G = *(0x33d2548)` (same object as `**0x32f1e18`). `S+0x24 + 4*p` = state of
  player index `p` (24 entries, accessors `FUN_0211e354` / `FUN_0211e370`).

### Where the state comes from V

- Local value `FUN_0211e394`: **2** if static flag bit 1 (`RaceFull`, `G+0x114`), else bit 29 of `G+0x110`
  (`ReadyVote`) = **1** or **0**. Flag enums are at `0x3180bd8` (ESequenceFlag: bit n = n-th name, None = 0;
  `CourseVoting` 6, `CourseVoteDeciding` 7, `ReadyVote` 29, `AfterPrivateRoomMain` 31) and `0x3180ec8`
  (ESequenceFlagStatic: `RaceFull` 1, `CourseVoteSession` 3, `RandomMatchSession` 4, `GroupRuleSet` 15,
  `P2PSync` 23, `AnybodySurvival` 25, `P2PReady` 26).
- `ReadyVote` is written only by `ReadyVoteSetter` enter `FUN_01a60be8` (bool parameter), plus one unrelated
  site at `0x79b750`.
- `System_NetStation` update `FUN_01a95cc8` (one actor per station, numbered `100 + aid`): for the local
  station it writes replica element `0xe3808a9c` (= NameHash 3816852124, the `ENetStationSequenceState` element
  of RE-MAP 1.2) with `FUN_0211e394()`, and element `0x290ef4b3` ("station ready"). For every station actor,
  when the replicated value differs from the cached one (`node+0x30` / `node+0x2d`) it posts the local events
  `0x478e0ec5` (state, aid) and `0x9c364c04` (ready, aid).
- `FUN_00d5509c` handles `0x478e0ec5`: `S.state[aid] = value`. `FUN_00a36994` handles `0x9c364c04`: when
  `aid` is the local player index it sets or clears static flag `P2PReady` (bit 26). A leaving player's state is
  reset to 0 (`FUN_00a36994`, event `0xd6221bab`).
- Other readers of the same array: `PrivateRoomMain` (`FUN_01a5a444`, state 1, the 15 s error `0x27ca935a`),
  `WaitMatchmake` (`FUN_01a93138`, state 2, error `0x8b738f76`), `FUN_00c26f34`.

So the host needs, per joiner: joiner leaves `PrivateRoomMain` -> `ReadyVoteSetter(true)` -> its
`System_NetStation` replica carries 1 -> host posts `0x478e0ec5` -> `S.state[joiner] = 1`.

### How the joiner gets to ReadyVoteSetter V

Graph (RE-MAP 1.1): `PrivateRoomMain -> GroupPlayModeSelector -> SendVoteElection -> StartTimeoutCounter ->
ReadyVoteSetter -> SyncRacePlayerInfo -> PreloadPlayer -> SyncState`.

- Host: leaves `PrivateRoomMain` through `FUN_02125308` (rule set, the room's interval seconds
  `G188+0x10 * 60` elapsed; this is the countdown after picking the rules). `StartTimeoutCounter`
  (`FUN_01a7b910`, host only) sends `0x9d424b78` and `0x44733945` with `frames = FUN_02141d24() * 6 / 100` and
  the `JoinPlayers` mask. `FUN_02141d24` = 10000 ms with `AnybodySurvival`, 20000 with `G188[1] && G188[0]==1`,
  else **30000**. The host then reaches SyncState within the same second.
- Joiner, `PrivateRoomMain` event handler `FUN_01a5b104`: events are processed only when static flag
  `CourseVoteSession` is set (or `RandomMatchSession` with a non-zero mode). `0x9d424b78` is accepted only when
  the local station is a session member in state 2 (`FUN_005b04a8`), is not the host (`FUN_02624674`), and
  `FUN_020e855c(mask)` holds: **static flag `P2PReady` set** and a local player in the mask. Then
  `node+0x144 = 1`, frames and mask are stored, `AfterPrivateRoomMain` is set. The event is one-shot: a joiner
  that fails one of these tests never starts.
- Joiner, update `FUN_01a5a444`: with `node+0x144`, member state 2 and the telop UI idle (`node+0x38` or
  `node+0x131`) it sets `CourseVoting | CourseVoteDeciding`, starts the on-screen timer (`G+0x218 =
  frames * 1000 / 60`) and sets `node+0x28`; it leaves once the host's `RaceManager` replica has element
  `0x983960e3` (`G188+5`) and a non-zero bitmap `#de577dcb` (`G188+0x44`).
- `P2PReady` follows the local station's own "ready" element: station holder active (`+0x2a`), `+0x2c` clear,
  id range resolved (`node+0x2c`, from the session handle `+0xa0` = `ussid`, range `ussid * 1e6`,
  `FUN_025d0000`), `(*(0x33ee148+0x390) & 3) == 0` and `!FUN_0271f9f0(actor)` (actor state machine not in
  states 0, 1, 3). The last two were not decoded further. I: "player actor fully loaded and active".

The 30 s on-screen timer explains "rules timer at 3 s / 4 s" on P1 when the error fires: it starts 1 to 2 s
before SyncState and the error comes 25 s after SyncState's enter. I (matches both runs).

## 3. What the logs show

Run B (PC log clock; 97297 ms = 18:34:10.43Z on the lobby). V unless marked.

| PC ms | Event |
|---|---|
| 45.5 s | P2 joins (lobby 18:33:18.66); Pia mesh up at once, PC sends 60 packets/s, Deck 17 to 18/s |
| 54.9 s | P1 opens the pause menu (rules) |
| 72.0 to 72.9 s | burst both ways: PC sends 141 x3, 157, 221 B; Deck answers 141, 157, 173 B. `SyncState` enter = 97297 - 25000 = 72.3 s (I from the code) |
| 72.9 to 97.3 s | steady traffic, no retransmission pattern; one Deck gap 88.9 to 90.0 s (+16.7 s) |
| 97.297 s | `controller event kind=2 detail=0/0/0/0xc/0x25/0x46b96f91`, leave op 10 |

- Lobby, 18:33:45 to 18:34:10: only the two periodic 15 s `WriteDocuments` (P1 18:34:03.14, P2 18:34:04.89),
  both `writes=1 stored=9`. No `GetDocument`, no error status, no stream end. The three writes at 18:34:10.43+
  are P1's leave. V
- Run C: same shape. Pause menu 61.8 s, burst 74.x s, error 99.421 s, Deck gap at 89.x s (+15 s). V
- After P1 left, P2 wrote its `@@RefereeRequest` 50.7 s (B) and 45.6 s (C) later. In run A the PC did the same
  21 s after the Deck was dropped. A Deck at 0.57x doing the same steps needs about that long, so P2 was most
  likely already inside the start sequence and continued by itself when P1 disappeared. I
- **The Deck runs slow in the room in every run**: `game: speed 0.42x .. 0.67x`, 64 to 105 frames per 5 s,
  present interval 2, for the whole time in the friend room (menus 1.00x, race about 0.95x). The PC is at
  1.00x. V (`deck-mkw*.log`, mapped to probe time with `speed.py`).
- Other friend-room attempts today (`scratch/lobby-1100.err` 14:15Z: PC leaves, Deck's referee opens 43 s
  later; `lobby-1121.err` 18:06Z: Deck leaves 94 s after joining) fit the same two failure shapes. No friend
  room start has succeeded in any log, in either sign-in order (PC number 1 or 2). V

## 4. 2318-0551 (run A)

- `0x44f3e` = module 318, description 551. Pia result -> description table `FUN_0151c96c`: description =
  1300 - offset; 551 <-> **`0x8c52`** (entry at `0x151da1c`; calibrated on 540 <-> `0x647e`,
  MESH-RUST-DIFF.md). V
- Posted with reason 11 by the per-frame Pia dispatch `FUN_02255f78` (`FUN_01510ab0` returns the framework's
  pending error) -> `FUN_0222d4bc`; category `(r >> 13) & 0x1f` = 4, priority 2. V
- 19 producers. The mesh ones: `FUN_0156e040` @0x156e190 (a station of my list is missing from the member list
  sent by the host and that station is **me**), `FUN_0156bda0` @0x156be9c / 0x156bf94 (an address or id of the
  update collides with **my** station), plus generic failures in the mesh jobs (`0x1577xxx`, `0x157cxxx`) and
  nplnd (`0x162a634`, `0x1631064`). For other stations the same code just disconnects them. So 0x8c52 here
  reads "I am no longer part of the mesh". V (code), I (which producer fired).
- Run A, Deck clock: start burst at 127 s; sends per second 17 to 22 until 140 s; 3 at 141 s, **0 from 142
  to 147 s**; error at 150.075 s. `deck-mkw-previous.log`: `game: speed 0.01x over 5.2 s (2 frames)`, and the
  frame-time report lists frames 3898 to 3901 = 1818, 3320, 1877, 703 ms with almost no GPU time (pipeline
  build 0, texture upload 0.1, replay 17, present wait 23). So the guest main thread stalled for 7.7 s, Pia was
  not dispatched, and the station went silent. V. The brief's "no multi-second stall" does not hold.
- Cause or symptom: the send rate is the frame rate (one Pia dispatch per game frame). The slow, then stalled
  game is the cause; the host dropping a silent station is Pia working as designed. I for the host side.
- The stall comes 14 s after the start burst. Runs B and C have a short Deck gap at the same point (+16.7 s,
  +15 s). Something on P2 happens there (I: a load step of its start sequence); not identified.

## 5. Why P2's state did not reach P1: candidates

Verified: P1 waited in SyncState for a non-zero state of P2 for 25 s; the Pia link was up and carried both
directions; the lobby was idle. Not verified: what P2's game did. Ranked:

1. **P2 never accepted the start event** (I). `FUN_01a5b104` drops `0x9d424b78` unless `P2PReady`,
   member state 2, not host and `CourseVoteSession` all hold on P2 at that instant; the event is not repeated.
   Fits: deterministic failure in every friend-room attempt, no error on P2 (its 15 s check has the same
   `FUN_020e855c` gate). Against: P2 seems to continue by itself after P1 leaves (section 3).
2. **P2 accepted it but is slower than 25 s of wall clock** (I). Everything on P2's side is counted in frames
   or game time and the Deck runs at 0.42 to 0.67x with stalls, while P1's limit is wall clock. Fits the Deck
   speed, the +15 s stall, P2 continuing alone. Against: `ReadyVoteSetter` comes before the heavy nodes, and
   the same two machines pass this phase in a matchmade battle.
3. **P2 set the state but P1 did not apply it** (I, weakest): replica element not delivered, or handled for
   another index. Indices are Pia station indexes on both sides (`100 + aid`), so no mismatch was found.

Not causes (V): the lobby (no traffic; `__us`/`__pus`, `upcsid`, `mcn`, referee and deferments are built by the
same code for `Private24` and `Battle24`/`Normal24`: `core.rs` `user_session_document`, `write_documents`,
`lobbies.rs` `add_seat`); the id range (`ussid` 1 and 2 give valid ranges, no `0xe538311c`); AFK (run C).

## 6. Friend room vs matchmade battle

- Same `PrivateRoom` graph and the same P2P elements. Static flags differ: `CourseVoteSession` + `GroupRuleSet`
  (room) against `RandomMatchSession` (matchmade). V
- Matchmade: the session only reaches this point through `WaitMatchmake` (`FUN_01a93138`), which compares the
  member mask with the mask of stations in state 2 and has its own timers, so a slow or unready joiner delays
  the start. Room: the host starts by hand; nothing in the host path read here (`FUN_02125308`, `FUN_021251c4`,
  `FUN_02140cc4`) looks at the joiner's ready element. V for the two code paths, I for "that is the difference
  that matters".
- Lobby side: no difference in this phase (section 5).

## 7. Next live test

Order by cost. Each step has the log line that decides.

1. **No build: P1 with `MKWMP_SKIP_REFEREE_TIMEOUT=2`** (existing switch, `npln.c` l.1189: drops every
   hash-id error at `FUN_0222d78c`, which is where `FUN_02624ad8` ends). SyncState then keeps waiting.
   - PC `mkwmp.log` shows `communication error 0x46b96f91 suppressed` (25 s after the start) and the race
     starts for both later: candidate 2. The delay between the first "suppressed" line and the course vote is
     the time P2 needed.
   - The line repeats (it is capped at 50) and nothing happens for a minute: candidate 1 or 3.
   This switch hides all errors on P1; use it for this test only.
2. **No build: wait 2 minutes after P2 stands in the room before P1 opens the rules**, with P2 not moving the
   camera. If it then passes, readiness at the start instant (candidate 1) is the condition.
3. **Probe trace** (proposal, not applied; `native/probe/src/npln.c`, in `profile_dispatch` before
   `original_dispatch(c)`; these functions are reached by BLR like `0x7001a93138` already traced there):

   ```c
   // Sequence state of the room start (SYNCSTATE-RE.md).
   if (g_rt.rt_log && (target == 0x7001a82d84ull || target == 0x7001a5a444ull ||
                       target == 0x7001a60be8ull || target == 0x7001a5b104ull))
       trace_room_start(target, (const uint8_t *)(uintptr_t)c->x[0], (const uint8_t *)(uintptr_t)c->x[1]);
   ```
   ```c
   static void trace_room_start(uint64_t target, const uint8_t *node, const uint8_t *arg) {
       const uint8_t *g = *(const uint8_t **)(uintptr_t)0x70033d2548ull;
       if (!g || !guest_readable(node, 0x160)) return;
       uint32_t f110, f114, st[4] = {0}, snap[12] = {0};
       memcpy(&f110, g + 0x110, 4); memcpy(&f114, g + 0x114, 4);
       const uint8_t *h = *(const uint8_t **)(g + 200);
       const uint8_t *s = h && guest_readable(h, 16) ? *(const uint8_t **)(h + 8) : NULL;
       if (s && guest_readable(s + 8 + 0x24, 16)) memcpy(st, s + 8 + 0x24, 16);
       const uint8_t *r = *(const uint8_t **)(g + 0x188);
       uint32_t play = 0, vote = 0; uint8_t has = 0;
       if (r && guest_readable(r, 0x50)) { memcpy(&play, r + 0x40, 4); memcpy(&vote, r + 0x44, 4); has = r[5]; }
       static uint32_t last[4][12];
       int k = target == 0x7001a82d84ull ? 0 : target == 0x7001a5a444ull ? 1 : target == 0x7001a60be8ull ? 2 : 3;
       if (k == 3) {                         // PrivateRoomMain event handler: only the start event
           uint32_t id = 0;
           if (!guest_readable(arg, 8)) return;
           memcpy(&id, arg + 4, 4);
           if (id != 0x9d424b78u) return;
           mlog("room start event: flags %#x/%#x states %u,%u,%u,%u", f110, f114, st[0], st[1], st[2], st[3]);
           return;
       }
       snap[0] = f110; snap[1] = f114; memcpy(snap + 2, st, 16); snap[6] = play; snap[7] = vote; snap[8] = has;
       if (k == 0) { memcpy(&snap[9], node + 0x28, 4); memcpy(&snap[10], node + 0x2c, 4); }
       if (k == 1) { snap[9] = node[0x144]; snap[10] = node[0x28]; snap[11] = node[0x38] | node[0x131] << 8; }
       if (k == 2) { const uint8_t *p = *(const uint8_t **)(node + 0x28); snap[9] = p && guest_readable(p, 1) ? *p : 2; }
       if (!memcmp(last[k], snap, sizeof snap)) return;
       memcpy(last[k], snap, sizeof snap);
       mlog("%s: flags %#x/%#x states %u,%u,%u,%u playing %#x vote %#x has %u a=%#x b=%#x c=%#x",
            k == 0 ? "SyncState" : k == 1 ? "PrivateRoomMain" : "ReadyVoteSetter",
            f110, f114, st[0], st[1], st[2], st[3], play, vote, has, snap[9], snap[10], snap[11]);
   }
   ```
   Reading it (both machines):
   - P2 has no `room start event` line: the event never reached its `PrivateRoomMain` (not in that node, or
     event gate at the top of `FUN_01a5b104`).
   - P2 has it with flags lacking `0x4000000` (`P2PReady`) or `0x8` (`CourseVoteSession`) in the second
     word: candidate 1, and the missing bit names the gate.
   - P2 `PrivateRoomMain: ... a=0x1` (start accepted) but no `ReadyVoteSetter: ... a=0x1` within 25 s:
     candidate 2; `has`/`vote` tell whether it waits for the host's `RaceManager` replica, `c` whether for the
     telop UI.
   - P2 `ReadyVoteSetter: ... a=0x1` and first word with `0x20000000`, while P1's `SyncState:` line keeps
     `a` (expected mask) with P2's bit and P2's entry of `states` at 0: candidate 3.
   - P1's `SyncState: ... a=<mask> b=<disconnected>` also shows the expected mask, to rule out a stale bit.
4. Independent of the above, the Deck must not stall in the room: run A is lost to a 7.7 s stall, and the
   room runs at about half speed. Confirm with `game: speed` in `deck-mkw.log` (target: no line under 0.9x in
   the room) and no gap in `Pia UDP send` on the Deck.

## 8. Limits

- No runtime view of P2's sequence flags: every statement about P2's node is inferred.
- Not decoded: `FUN_0271f9f0` state meanings, `*(0x33ee148+0x390)`, the host-side start conditions beyond the
  three functions named, the +15 s event on P2, which of the 19 producers of `0x8c52` fired in run A.
- The graph order is taken from RE-MAP 1.1; only the `state` parameter of SyncState was re-read from the
  `.ainb` here.
- The successful matchmade battle has no Deck or PC log in `scratch/syncstate-1535`, so "same machines pass in
  matchmaking" rests on the brief.

## 9. Follow-up: what the room trace shows, and the cause

Logs: `scratch/syncstate-1535/pc-mkwmp-trace.log`, `deck-mkwmp-trace.log` (probe hooks of section 7).
New decompiles in `scratch/syncstate-re/`: `ss14.c`..`ss16.c`, `ssi.c`, `flag4.c`, `rule*.c`, `gsrule.c`, `gsd.c`,
`mgr1.c`..`mgr3.c`; graph dump `ainb2.py`.

### 9.1 The trace

| Machine | ms | Line | Meaning |
|---|---|---|---|
| P1 | 60985.. | `PrivateRoomMain: flags .../0x4988948` | static flags: `CourseVoteSession` (0x8), **no 0x10** |
| P1 | 61141 | `room start event`, `ReadyVoteSetter ... playing 0x3 vote 0x3 a=0x1` | host left `PrivateRoomMain`, sent the start, state 1 |
| P1 | 61203 | `SyncState: ... states 1,0,0,0 a=0x3` | expects players 0 and 1; player 1 (P2) is at 0 |
| P2 | 32358.. | `PrivateRoomMain: flags .../0x180358` .. `0x4980b58` | static flags have **0x10 = `RandomMatchSession`** and 0x8 from the first line on |
| P2 | 53953 | `room start event: flags 0x2/0x4988b58` | the host's start arrived; `P2PReady` (0x4000000) and 0x8 are set, so it is accepted |
| P2 | 53996, 54076 | `PrivateRoomMain ... a=0x1 b=0`, then `flags 0x800000c2 ... states 1,0,0,0 ... b=0x1` | start accepted, `node+0x28` set; P2 sees P1's state 1 |
| P2 | 54076 to 119830 | nothing | no `ReadyVoteSetter` line for 65 s |
| P2 | 119830 | `room start event: flags .../0x4988b5c states 1,0,0,0`, then `ReadyVoteSetter ... playing 0x2 a=0x1`, `SyncState ... a=0x2` | P1 is gone (playing 0x3 -> 0x2); P2 is now the host and this start event is **its own** |

So (a) of the follow-up: the second `0x9d424b78` is not a resend by P1. The PC crashed, the Deck became host
and ran `StartTimeoutCounter` itself (the hook logs the sender's own event too, as on P1 at 61141). V

After the first event every gate of section 2 held on P2 and `PrivateRoomMain` took its exit: at 54076 the exit
test of `FUN_01a5a444` (`node+0x28 && G188+5 && G188+0x44`, disassembly 0x1a5a4b0..0x1a5a50c) is true with
`b=0x1 has 1 vote 0x3`. V. The graph then goes (re-read from `PrivateRoom.module.ainb`, `ainb2.py`):
node 1 `PrivateRoomMain` -> node 28 `GroupPlayModeSelector` {Wait -> DummyEndAction; Race, Survival -> 27
`SendVoteElection`; Battle -> 29 `SyncBattleAnybodyRule` -> 27} -> 4 `StartTimeoutCounter` -> 32
`ReadyVoteSetter` -> 38 `SyncRacePlayerInfo` -> 39 `PreloadPlayer` -> 31 `SyncState`. P2 did not reach node 32,
so it sat in node 28, 27 or 4. Node 27 finishes in its enter (`FUN_01a73514`) and node 4 (`FUN_01a7b910`) has no
wait, so it is node 28. I (no hook on it).

`GroupPlayModeSelector` update `FUN_01a46670` branches on `RandomMatchSession`:
- clear (a room): child chosen from the host's replicated group play mode `G188[0]` (0 Race, 1 Survival,
  2/3 Battle), "Wait" while `G188[1]` is 0;
- set (a public lobby): child chosen from the local controller mode `**(G+0xd8)`; **returns without choosing
  while that is 0**. V (code); that this is what held P2 is I.

### 9.2 Why the joiner has `RandomMatchSession`

Only `FUN_0211d0d4(G, &mode)` sets 0x10 (with 0x8): for mode 0 or 5 it sets `RandomMatchSession |
CourseVoteSession`, `G188[4..5]`, interval 30 and controller mode 2 or 5. V (the other sites with 0x10 on
`G+0x114` are clears: `FUN_00c04100`, `FUN_00c737bc`, `FUN_0211d314`, LobbyJoin enter `FUN_01a4c20c`).
Callers: `AnybodyMain` (`FUN_01a30810` case 7, the public search) and **`SyncSessionInfo`** update
`FUN_01a829f8`, which runs on a non-host:

    mode = M+0x7fc;  kind = M[0x4b4] ? M[0x4a8] : M[8];  if (kind != 0) mode = 3;
    mode 5 or 6 -> FUN_0211d0d4;  mode 3 -> flags |= CourseVoteSession only;  mode 0 -> FUN_0211d0d4

`M+8` selects the backend in `FUN_0261eff4` (0 = the NPLN path, 1 and 2 = other managers), so it is 0 for every
internet session, room or not. V. So a joiner is "private" only when `M+0x7fc == 3`.

`M+0x7fc` of a joiner comes from the session it was shown:
- Room ID: `CheckShortIdSession` 0x1a3a734: `LobbyType = FUN_021424d0(FUN_01e3f370(session info))`, then
  `StartMatchmake` sets `M+0x7fc = FUN_021424a8(LobbyType)`.
- Friend card: list entry `+0x48 = FUN_01e3f0d0(index)` (`FUN_02134b8c`), handed on as LobbyType by
  `LobbySelectTarget`.
- `FUN_01e3f370` / `FUN_01e3f0d0`: no session info -> 3; otherwise the session attribute **`rule`**
  (string 0x28da353) through `FUN_0223f22c` + `FUN_00f131c0`, which returns the integer only when the value's
  kind is 3 (int) and **0 otherwise, also when the attribute is missing**.
- Tables: `FUN_021424d0` (0x29c93b0) rule 0 -> LobbyType 1, 3 -> 2, 5 -> 3, 6 -> 4; `FUN_021424a8` (0x292b810)
  LobbyType 1 -> mode 0, 2 -> 3, 3 -> 5, 4 -> 6.

All V. The client never writes a session attribute `rule`: the string has four references, all readers
(`FUN_01e3f370`, `FUN_01e3f0d0`, the friend entry getter at 0x1e36a8c, and `FUN_00498a80` which reads
`prp["rule"]` of the Gamesync document `__gs/m`). The create request carries `rom_id`, `course_id`, `user_name`
only (RE-MAP 3.2), and no log shows an UpdateGameSession call. So on the real service `rule` is put there by
the server (I: from the matchmaking config; `Private24` = 3).

The Rust lobby returns the creator's attributes unchanged (`core.rs` `room_create` l.1061 ->
`session_view_msg` -> `msgs::game_session` field 11) in GetGameSession, BatchGetGameSessions,
JoinGameSession and TrackGameSessionCreationTicket. No `rule` -> the joiner reads 0 -> LobbyType 1 (public
Race) -> mode 0 -> `RandomMatchSession`. V for each step in code, and the flag is in the trace; the link
"missing `rule` is what set it in this run" is I until the test of 9.5.

### 9.3 What this explains

- P2 accepts the start but stalls in the play-mode selector; P1 waits 25 s in SyncState (this document).
- The up to 15 s in which P1 can do nothing after picking the mode: the joiner applies the host's rule replica
  only when `FUN_01a80590` holds, and that needs `RandomMatchSession` clear; the host waits for it
  (`WaitSyncPlayMode`, 900 frames, RE-MAP 4.3). I
- P2's chat not shown on P1 while P1 -> P2 works: P2 runs public-lobby logic in a room. I (the chat event
  0x2f815dfc was not traced).
- Matchmade battles work: both sides are public and take the same branches.
- (b) of the follow-up: nothing is missing on the host side. The host admits the joiner: P1 has
  `playing 0x3` and expected mask `a=0x3`. Player indexes are agreed peer to peer in the station manager
  (vtable 0x325dac0: join queue `FUN_02249774`, promotion `FUN_02249a90`, slot table `FUN_0224cee8`), with no
  Gamesync or GameSession member list involved. V. The `__us` ADDED push the lobby sends on a join is enough
  for this phase.

### 9.4 Fix (proposal, not compiled)

`rust/lobby/src/core.rs`, `room_create`: store the attributes with `rule` added when the creator did not send
one. `Attributes` = `{1: entry{1 key, 2 Value}}`, the same shape as `Fields::encode`; `Value::Int` is field 3,
the kind `FUN_00f131c0` wants.

```diff
-        let room = Room { password: req.password, attributes: req.attributes, creation_ticket: creation, alias: None };
+        let room = Room { password: req.password, attributes: with_rule(req.attributes), creation_ticket: creation, alias: None };
```
```rust
/// Joiners read the int session attribute `rule` (FUN_01e3f370 / FUN_01e3f0d0); a missing one reads as 0,
/// a public Race lobby (resources/docs/SYNCSTATE-RE.md 9). 3 = friend room.
fn with_rule(mut attributes: Vec<u8>) -> Vec<u8> {
    let has = crate::wire::repeated_bytes(&attributes, 1)
        .is_ok_and(|es| es.iter().any(|e| crate::wire::str_field(e, 1).ok().flatten().as_deref() == Some("rule")));
    if !has {
        let entry = crate::wire::Msg::new().bytes(1, "rule").msg(2, &Value::Int(3).encode());
        attributes.extend(crate::wire::Msg::new().msg(1, &entry).into_bytes());
    }
    attributes
}
```

Test to add (`rust/lobby/tests/flow.rs`): create a room with attributes `{rom_id, course_id, user_name}`;
GetGameSession, BatchGetGameSessions and the JoinGameSession reply must carry field 11 with `rule` = int 3 and
the three original entries unchanged.

Second step, only if 9.5 still fails: author `docs/__gs/m` = `{cp: false, ip: false, pw: "", ebf: false,
bfmin: 0, bfmax: 0, prp: {rule: 3}}` in the answer to the `docs/__gs/` listen (parser `FUN_012673cc`; the
game copies `prp["rule"]` to `ctx+0xdc` in `FUN_00498a80`; its consumer was not traced). The client may then
update `cp` on that document and the write must be accepted (MULTI-LOBBY.md 2.2).

No probe change is needed. (c) of the follow-up: making the joiner act on the first event, or suppressing
`0x46b96f91`, treats the symptom; with suppression both sides sit forever because the joiner never leaves the
selector.

### 9.5 Confirmation in the next run (trace probe on both machines)

1. Joiner, from its first `PrivateRoomMain:` line: second flags word **without 0x10** (for example
   `0x...348` / `0x...b48` where it was `0x...358` / `0x...b58`).
2. Joiner, within a second of `room start event`: `ReadyVoteSetter: ... a=0x1`, then `SyncState: ...`.
3. Host: a `SyncState:` line with `states 1,1,0,0` (or no second line at all because it leaves at once), and
   no `controller event ... 0x46b96f91`.
4. No dead time on the host after picking the rules; chat from the joiner appears on the host.

If 1 fails with the fix deployed, dump field 11 of the GetGameSession / BatchGetGameSessions reply and check
the `rule` entry (key, Value field 3). If 1 holds and 2 fails, hook `GroupPlayModeSelector` update
`0x7001a46670` (log `G188[0]`, `G188[1]`, `**(G+0xd8)`, `node+0x1e`).

### 9.6 Corrections to earlier sections

- Section 5: candidate 1 ("P2 never accepted the start") is refuted by the trace; candidate 2 (Deck speed) is
  refuted by the run with the Deck at 0.93 to 1.00x. The cause is on the joiner, after the accept.
- Section 7, test 1 (`MKWMP_SKIP_REFEREE_TIMEOUT=2`): the observed result (both sit forever) fits 9.1.
- Section 3: the Deck's slow room was mostly the probe's own logging (fixed in 4b62fee, 1153967); the 7.7 s
  stall of run A and its Pia drop (section 4) are a separate matter from the SyncState failure.
