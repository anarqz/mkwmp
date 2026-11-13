# Mario Kart World online netcode: reverse-engineering map

Static analysis of `scratch/main.bin` (image offsets; guest address = 0x7000000000 + offset), the scene graphs in
`scratch/game/romfs/AI/*.ainb`, and the server logs `scratch/run-lan5..9.log`. Nothing was run live.
Date: 2026-11-13. Working files (decompiles, scripts, per-area notes): `C:\Users\alcmo\.claude\jobs\7b106074\tmp\re-map\`.

Every claim is marked **VERIFIED** (read in decompiled code, data, a graph file or a log; address or file given) or
**INFERRED** (reasoning). Sections 2-5 and A use the short forms **V** / **I**.

Contents:
- Summary and the three things to change first
- A. Online menu map, friends and presence
- 1. Post-match flow and the referee (also covers priority B)
- 2. Gamesync document catalog
- 3. Matchmaking / session lifecycle and friend rooms
- 4. Error id catalog
- 5. Race progress sync
- Server actions, prioritized

## Summary

**Why nobody returns to the room after a match.** The race never ends on the clients, because our referee answers
are wrong, and the game only shows results when the referee has ranked every player:

1. The status-0 `@@RefereeResult` must start with `result = []`. `result` is the ordered list of race-player indices
   (0-based) that have finished or been eliminated, not a list of ranks. We send `[1..N]`.
2. Status 1 must be sent only when `result` lists every player. We send it on the first report with a partial list
   (`[1]`), after which the clients stop reporting (the report is only written while the result is at status 0), so
   the second player is never ranked. In `run-lan6.log`, where the server did not finalize, both clients wrote `[1]`
   and 0.5 s later `[1, 0]`; in `run-lan7.log`, with the premature status 1, the second report never came.
3. The scene node `AIRaceSeqWaitRankFixed` (`FUN_00d180e0`) and the rule's `IsFinished` (`FUN_02125730`) wait for a
   "rank fixed" event per player, which is produced only from the referee result (`FUN_02126ad4`). Until then the
   winner auto-drives and the stationary kart is hit by the idle watchdog (`0x9b928162`); `0xcd1bf9ca` is the 30 s
   post-finish timer.

**What happens after a correct verdict.** Results screen, then `ReturnFromRace` back to free roam, then the room loop
`PrivateRoom.module` (used for worldwide rooms too) starts the next vote. That loop runs over Pia between the same
mesh members; the client makes no new matchmaking request and re-reads no GameSession between races. So the server
only has to end the race correctly and keep the session alive. This last statement is inferred from the graphs and
the node code that was read; it has not been seen working live.

**Two server bugs found on the way that affect the same flow.**
- Game document writes are merge writes with a field mask; the server replaces the whole document. `PlayerStatic` is
  written in several partial writes, so `p2p_sid` / `p2p_lid` are lost, which is the likely reason every captured
  `@@RefereeRequest` has empty `users` and `ussid_map`.
- `Member` fields 1 and 2 are swapped in our GameSession (1 = userSession name, 2 = user).

**Still open.** In `run-lan7.log` user 2 left its Gamesync user session 3 s after the premature status 1 (a normal
game-level leave: P2P targets removed, `__pus` deleted, KeepUserSession closed). The caller of that leave
(`FUN_0223a848`, session-manager vtable slot at 0x325c698) was not identified. It is expected to go away once the
referee is fixed; if it does not, log the return address at `main+0x223a848`.

**Where the per-area analyses disagree, use these readings.**
- Result `status`: 0 = open, 1 = final, **>= 2 = error, posts `0x606314c9`** (`FUN_02126ad4`). Section 2 says no code
  handles other values; that is wrong.
- The 36 s referee timeout waits for the **status-0** result (section 4 words it as status 1).
- The `@@RefereeResult` document id must be a **uuid**, with `request_id` = the request key (`FUN_01e39a5c` parses
  the id; section 5 writes it as `<key>`).
- Change **kind 3 = REMOVED** and the server should send it for deleted documents; `docs/NETCODE.md` says not to
  send it, which section 2 corrects.
- Votes and room state travel over Pia, not Gamesync documents (section 3 (c) mentions votes as documents).
- `FUN_02141adc` tests "every player retired / out" (mask `G+0xc`), not "every player finished" (section 5.3.2);
  section 1 calls it "all players done" after the task brief.
- Event names from the game's network schema (section 5): `0xae42b7bf` in section 1 is `RaceSeq_GoalRankFixedLocal`;
  the vote runs on `GlobalSeq_StartCourseVote` / `GlobalSeq_ReplyCourseVote` / `GlobalSeq_StartRoulette`.

**One thing done outside pure reading.** To unpack the schema archive, the race-progress analysis installed the
`zstandard` Python package from PyPI into `re-map\a5-pylib` (nothing else was downloaded, nothing about the project
was sent). No file in the repo was changed except this report.


---

## A. Online menu map, friends and presence


All addresses are image offsets in `scratch/main.bin`. VERIFIED = read in decompile/disassembly/data or in a
capture/graph file (source named). INFERRED = reasoning. Working files (decompiles, filters) are `re-map\a6-*`.

Not resolved (stated up front):
- The exact matchmaking RPC behind three game calls (friend-session query `FUN_0137b9f8`->`FUN_013ae1e4`, short-id
  create `FUN_0137cd00`, short-id lookup op 0x10). RPC names below for these are INFERRED; area a3 owns them.
- The KeepAlive writer coroutine `FUN_012294c0` only partly decompiles; message ORDER is from its call sequence.
- The dispatcher that consumes the session-manager request `mgr+0x4a8..0x4b5` was not traced (a3 territory).
- Node vtable slot roles (28 enter / 29 update / 30 exit) are INFERRED from content; several "update" slots are thin
  wrappers that tail-call the real state machine (e.g. LobbySelectTarget 0x1a4e18c -> 0x1a4e1b8).

---------------------------------------------------------------------------------------------------
### PART 1. Online menu map

#### 1.1 Sequence graphs (VERIFIED, `scratch\game\romfs\AI\*.ainb`, dumped with re-map\ainb.py; dump in a6-ainb.txt)

`MainMenuLobby.module` (the "Online Play" lobby screen):

    SetBlackBoardParamBool -> UpdateLobbyStatus --Success--> LobbyMain      (Failure -> SetDemoCamera -> end)
    LobbyMain child pins: "Wait" -> DummyEndAction, "UpdateSession" -> UpdateSession
    LobbyMain transitions (each through a WaitNetAsyncTask node first):
      "SearchSession"      -> MainMenuFindSession.module      (Room ID entry)
      "CreateSession"      -> MainMenuCreateSession.module    (create a room)
      "JoinSession"        -> MainMenuJoinSession.module      (join a friend)
      "Automatch"          -> MainMenuAnybody.module          (Race, worldwide)
      "AutomatchBattle"    -> MainMenuAnybody.module          (Battle, second instance)
      "AutomatchSurvival"  -> MainMenuLobbySurvival.module    (Knockout Tour)
      "Cancel"             -> SetDemoCamera -> end
    every sub-module: Success/Next -> MainMenuPauseControl -> RaceSendReadyMessage -> ClearTitleSetupInfo ->
      UIRaceInfoInOut -> ChangeMap -> StartPlayerRumble -> end;  Return -> LobbyMain;  Error -> DisconnectConfirm

There is NO Worldwide/Regional split and no separate 2P graph: the transition commands above are the whole menu
(VERIFIED from the graph). 2-player is a local-player count, not a different config (INFERRED: no 2P config name
exists, see 1.3; `FUN_026201dc` loops `FUN_0261f9d8(mgr, i)` for local players 1..n-1 at 0x26201dc).

| Module | Node chain (VERIFIED from .ainb) |
|---|---|
| MainMenuAnybody | AnybodyMain --Next--> MainMenuLobbyPartsSelect.module (Done/Cancel/ForceNext) |
| MainMenuLobbySurvival | SetSequenceFlag -> StartAnybodySurvivalSeq -> MainMenuLobbyPartsSelect.module |
| MainMenuFindSession | InputByKeyboard --Input--> CheckShortIdSession (child pin "InputPassword" -> InputPassword) --Next--> StartFreerunReport -> StartMatchmake (PlayMode input from ConvertLobbyTypeToPlayMode) --"Succes"--> MainMenuLobbyPartsSelect |
| MainMenuCreateSession | CreateSessionSelectSeq --Next--> SetBlackBoardParamBool -> InputPassword --Input/NoInput--> StartFreerunReport ... DisconnectUnusedController -> StartMatchmake --"Succes"--> MainMenuLobbyPartsSelect |
| MainMenuJoinSession | SetBlackBoardParamBool -> LobbySelectTarget (child pin "InputPassword" -> InputPassword) --Join--> SetBlackBoardParamBool -> LobbyJoin --Success--> MainMenuLobbyPartsSelect |
| MainMenuShowSessionInfo | GetShortSessionId --Success--> ShowSessionInfo |
| MainMenuStartGroupGame (room rules / start) | RuleSetting --TeamSelect--> TeamSelect; --Next--> FixGroupPlayInfo --Success--> GroupRuleApply -> SetSequenceFlag -> OnlineUICleanup -> ExecuteSequenceHook -> SendGroupSetting -> MainMenuPauseControl -> end (Failure -> Dialog) |
| MainMenuChangeRule | wraps MainMenuStartGroupGame (Cancel/Start/Finish) |
| PrivateRoomSequence | Simultaneous { SyncPrivateRoom, GroupPlayModeSelectorBinary -> PrivateRoom.module, SwitchSeq -> AnybodySurvivalSeq.module } |
| PrivateRoom (room main loop) | SetSequenceFlag -> PrivateRoomMain --Next--> GroupPlayModeSelector {Race/Survival -> SendVoteElection; Battle -> SyncBattleAnybodyRule -> SendVoteElection} -> StartTimeoutCounter -> ReadyVoteSetter -> SyncRacePlayerInfo -> PreloadPlayer -> SyncState -> WaitPrivateSeqReady -> OnlineUICleanup -> setupUIInfoAfterCleanup -> GroupPlayModeSelector {Race/Battle -> S32Selector -> ShowGroupRule or WaitSyncPlayMode; Survival -> ShowGroupRule} -> GroupPlayModeSelector {Race -> CourseVote.module; Battle -> BattleListup -> VoteOneCourseDirect -> SubmitVoteReport; Survival -> GrandprixListup -> VoteOneCourseDirect -> SubmitVoteReport} -> WaitMapOut -> ClearRacePoint -> CourseRoulette -> SyncNextStartPoint -> SyncTiming -> CourseVoteWaitPlayRequest {ParentNext -> SendGroupSetting -> RequestPlay; ChildNext} -> WaitRaceStart. Any Error -> SendNoPropertyMessage -> SetSequenceFlag -> ReadyVoteSetter -> PrivateRoomMain (= return to the room) |
| AnybodySurvivalSeq (Knockout Tour public loop) | AnybodySurvivalMain (pins Wait / WaitRace -> WaitRaceStart / Disconnect -> DisconnectConfirm / Matchmake -> StartMatchmake) -> WaitMatchmake {Success -> SetSequenceFlag -> SetEnableMap -> ChangeMap -> SyncRacePlayerInfo -> StartTimeoutCounter -> SyncSurvivalCourse -> WaitForceNext -> SetSequenceFlag -> WaitPrivateSeqReady -> PreloadPlayer -> ShowUITelop -> SyncTiming -> StartFreerunReport -> CourseVoteWaitPlayRequest -> RequestPlay -> MainMenuPauseControl -> ResetCom -> AnybodySurvivalMain; Failure -> ShowUITelop -> SetSequenceFlag -> AnybodySurvivalMain} |

#### 1.2 Enums and the session-manager request block (VERIFIED)

Net manager object `M` = component lookup `(*(*0x32f29f8 + 0xd8))->vt[0](typeid 0x32f29d0)` (also `FUN_008cda28()`).

| Field | Meaning | Evidence |
|---|---|---|
| `M+0x7fc` | play mode (config selector) | written by StartMatchmake 0x1a79fa0 state 1 |
| `M+0x4a8` | session kind: 0 = public automatch, 1/2 = private (2 when the node's kind param == 3), 3 = invalid | `FUN_026201bc` |
| `M+0x4ac` | pending operation: 1 connect/login, 2 or 3 matchmake, 4 update session, 0x10 short-id lookup | `FUN_0212f764` case 3, `FUN_026201bc`, `FUN_01a88618`, `FUN_0261f30c` |
| `M+0x4b0` | extra flag (kept only when kind == 0) | `FUN_026201bc` |
| `M+0x4b4 / +0x4b5` | request pending / in progress | same |
| `M+0x46c` | busy counter, 0 = idle | `FUN_0212f764` case 4, InitializeOnline 0x1a47110 |
| `M+0x4b8` (ptr), `+0x4c0` (cap) | password string buffer | StartMatchmake copies its string param there |
| `M+0x800` / `+0x804` | CourseId / AttractionId for the ticket | StartMatchmake state 1 |
| `M+0x5a8` | short id being looked up; result session info at `M+0x760` | `FUN_0261f30c`, `FUN_0261f548` |

LobbyType <-> play mode (VERIFIED tables):
- `FUN_021424a8` table 0x292b810: LobbyType 1 -> 0, 2 -> 3, 3 -> 5, 4 -> 6 (else -1).
- `FUN_021424d0` table 0x29c93b0 (inverse): mode 0 -> 1, 3 -> 2, 5 -> 3, 6 -> 4 (others 0).
- `FUN_02141fcc`: LobbyType -> session kind for UpdateSession/LobbyJoin: 1 -> 1, 2 -> 2, else 0.

#### 1.3 matchmakingConfigs names (VERIFIED, selector at 0x1e3f61c, called by `FUN_01e3fa98`)

    name(M, searchAll):
      if searchAll & 1            -> "SearchAll"
      kind = M[0x4b4] ? M[0x4a8] : M[0x8];  mode = M[0x7fc]
      kind == 0 && mode == 1      -> "World96"
      m = (kind == 0) ? mode : 3
      m == 2 -> "Attraction6";  kind == 0 && mode == 4 -> "Streamer";  m == 5 -> "Battle24";  m == 6 -> "Survival24"
      m == 0 -> "Normal24";  otherwise -> "Private24"

Complete list: SearchAll, World96, Attraction6, Streamer, Battle24, Survival24, Normal24, Private24. No Regional and
no 2-player variants. Any private kind uses Private24 whatever the mode. Resource prefix string `/matchmakingConfigs`
at 0x288824b (ref 0x13eb4e8); live capture shows `tenants/current/matchmakingConfigs/Normal24` (docs/NETCODE.md).

| Menu option | LobbyType | mode | kind | config |
|---|---|---|---|---|
| Race (anybody) | 1 | 0 | 0 | Normal24 |
| Battle (anybody) | 3 | 5 | 0 | Battle24 |
| Knockout Tour (anybody) | 4 | 6 | 0 | Survival24 |
| Friend room (create / join / Room ID) | 2 (or the room's own rule) | 3 or rule | 1/2 | Private24 |
| free-roam world lobby | - | 1 | 0 | World96 (INFERRED use) |
| Attraction6 / Streamer / SearchAll | - | 2 / 4 / flag | | reachable only via StartCompanionMatchmake or non-menu paths (INFERRED) |

Ticket attributes built by `FUN_01e3fa98` (VERIFIED strings): `rom_id`, `batching_key`, `course_id`, `user_name`,
`_attr%d`, `_world%u`, and `cv%d` (`FUN_0261f56c`). A session attribute `rule` (int, default 3) is read back by
`FUN_01e3f370` and converted with `FUN_021424d0` to the LobbyType of a room found by Room ID.

#### 1.4 Per-node behaviour

| Node (vtable) | State machine / calls | Network effect | Mark |
|---|---|---|---|
| InitializeOnline (0x316a0d0; 0x1a47090/0x1a47110) | wait `M+0x46c == 0`; `FUN_0212f6c0`/`FUN_0212f764` connect machine; result 5/7 -> port A, 6 -> port B | op 1 on M (login: Auth + the boot RPCs seen in presence-stream.log) | VERIFIED code, RPC list INFERRED |
| connect helper `FUN_0212f6c0`/`FUN_0212f764` | 2 wait `FUN_02624644`; 3 `FUN_0261ef40(M, kind, 1, 0, 0)`; 4 wait idle; ends 5 ok / 6 failed / 7 already online | shared by InitializeOnline, StartMatchmake, LobbyJoin, CheckShortIdSession, AnybodyMain, UpdateLobbyStatus | VERIFIED |
| UpdateLobbyStatus (0x31752a0) | connect machine, then friend-manager calls (`FUN_02227 8b4/914/990`, lobby list `FUN_02134/5xxx`) | refreshes friend list + friend sessions before LobbyMain | calls VERIFIED (a6-calls.txt), semantics INFERRED |
| LobbyMain (0x316b690) | UI; emits the transition commands of 1.1; reads friend lobby list | none by itself | graph VERIFIED |
| UpdateSession (0x1a88618 enter, 0x1a88670 update) | enter: `M+0x4a8 = FUN_02141fcc(LobbyType)`, `M+0x4ac = 4`, pending = 1; done when `FUN_02624a00()+0x1c` set | op 4 (session update) | VERIFIED |
| AnybodyMain (0x1a30810, helper 0x1a309d8) | connect machine, then tail-calls `FUN_026201bc`; error dialog 7 | CreateMatchmakingTicket + TrackMatchmakingTicket | call VERIFIED, RPC from live capture |
| StartMatchmake (0x1a79f2c / 0x1a79fa0) | enter: mode = `FUN_021424a8(LobbyType)`. State 1: `M+0x7fc = mode`; copy password param to `M+0x4b8`; mode 1 -> `M+0x800 = "CourseId"`, mode 6 -> `M+0x804 = "AttractionId"`; then: host flag set -> `FUN_026201bc(M, kind, 1, 0, 0)`; else session-id string param non-empty -> `M->vt[0x48](M, &id, 0)` (join that session); else `FUN_026201bc(M, kind, 0, mode==1, 0)`. Then connect machine; failure -> dialog -> port | create: CreateGameSessionCreationTicket/Track...; join by id: JoinGameSession; public: CreateMatchmakingTicket | code VERIFIED; RPC names INFERRED (a3) |
| WaitMatchmake (0x1a93138) | 1 wait M idle; 2 poll until `M+0x4b4 && M+0x4b5`; member-bitmap sync via `FUN_0214238c` (net event 0xde577dcb); 0x4b0-frame counter; errors 0x8b738f76 | none new (waits on Track stream + P2P) | VERIFIED |
| StartAnybodySurvivalSeq / AnybodySurvivalMain | switch the running sequence to AnybodySurvivalSeq; its "Matchmake" pin runs StartMatchmake (mode 6) | Survival24 ticket | graph VERIFIED |
| StartCompanionMatchmake (0x1a793f0) | `FUN_026201bc(M, 0, 0, 0, 1)` (twice, states 3 and 6) with "AttractionId"; companion doc helpers `FUN_01e38b1c/8d28/8ea8/8ff0` | public ticket with `M+0x4b0 = 1`; Gamesync `Companion` docs | call VERIFIED, purpose INFERRED |
| InputByKeyboard / InputPassword (0x1a47368, 0x1a47910) | software keyboard; outputs string, "NeedPassword" | none | VERIFIED strings |
| CheckShortIdSession (0x1a3a454) | 1: `FUN_0261f30c(M, &id)` (copies id to `M+0x5a8`, `FUN_0261eff4(M, 0x10)`), connect machine; 3: poll `FUN_0261f548()` (result at `M+0x760`) for at most 120 s (0x1d4c1 ms) -> else error dialog; result `vt[0x40]` true -> output NeedPassword = 1, run InputPassword child; state 8: LobbyType = `FUN_021424d0(session attr "rule")` | GetGameSessionShortAlias then GetGameSession (INFERRED names) | code VERIFIED |
| CreateSessionSelectSeq (0x1a3e75c) | selection dialog (`FUN_021394fc/2139878/21398d8`), outputs "NeedPassword" | none | VERIFIED |
| LobbySelectTarget (0x1a4e1b8) | 1: `FUN_02624a00()` null -> dialog 7 -> error port; `FUN_02134980(list, filter, 0)`, `FUN_01a4bd9c(list, index)` picks the friend entry; none -> dialog 5 -> port; output = entry+0x48, NeedPassword = `FUN_01a4e60c(entry)`; if needed dialog 0x14 + InputPassword child; 6: outputs password + LobbyType, port "Join" | none (uses cached friend/session data) | VERIFIED |
| LobbyJoin (0x1a4b998) | `FUN_01a4bb9c`; `FUN_021354a0(list, index)`; connect machine `FUN_0212f6c0(.., kind)`; failure dialog 7 | JoinGameSession on the friend's session id | code VERIFIED, RPC INFERRED |
| GetShortSessionId (0x1a436ac -> `FUN_01e37c54`) | needs `M+8 == 0` and no cached id; current session handle `FUN_01a9637c`; `FUN_0137cd00(client, session+0x38 id string)` -> future at ctx+0x8f68 | CreateGameSessionShortAlias (INFERRED name) | code VERIFIED |
| ShowSessionInfo (0x31718c0) | shows room id from ctx (`FUN_0045b24c`) | none | calls VERIFIED |
| RuleSetting / TeamSelect / FixGroupPlayInfo / GroupRuleApply / SendGroupSetting / ShowGroupRule / SyncBattleAnybodyRule / SyncPrivateRoom / PrivateRoomMain / WaitPrivateSeqReady / WaitSyncPlayMode | host edits rules, pushes them to members, members display and wait | P2P net events (`FUN_0261e060` + `FUN_01a1a550`) and Gamesync docs; no NPLN RPC found in these nodes' call lists (a6-calls.txt) | calls VERIFIED, payloads not decoded |
| ConvertLobbyTypeToPlayMode (0x3167060) | pure data node, output "PlayMode" from "LobbyType" (`FUN_00d3fed0`) | none | VERIFIED |

Passwords: a room password is a string carried in `M+0x4b8`; `FUN_026201dc` pushes it into the session
request with `FUN_02230870`/`FUN_02231694` (VERIFIED). How it is checked server-side is not visible (INFERRED: a
session attribute compared by the server on join; the client only learns "NeedPassword" from the session info).

#### 1.5 What a private server must answer per option

| Option | Server must |
|---|---|
| Race / Battle / Knockout | accept ticket for Normal24 / Battle24 / Survival24, stream Track state 3 with GameSession (already working for Normal24/Battle24 per NETCODE.md); Survival24 is the same contract with a different config name |
| Create Room | answer the Private24 creation path (a3) and later `CreateGameSessionShortAlias` for the 6-digit Room ID; put attribute `rule` on the session |
| Search by Room ID | resolve the alias to a GameSession within 120 s; include `rule` and the password-needed indication; then accept the join |
| Join a friend | everything in Part 2, plus JoinGameSession on the id published in the friend's `SessionId` |
| Room rules / start / return to room | relay only: Gamesync documents + Pia; no extra NPLN RPC seen |

---------------------------------------------------------------------------------------------------
### PART 2. Friends and presence

#### 2.1 Protobuf classes (VERIFIED: vtables 0x30eb9c0 + 0x80*i, `_InternalSerialize` = slot 12)

friends.pb.cc
| Serializer | Shape | Role |
|---|---|---|
| 0x122d8d8 | 1 string | ActivateUserRequest {name}; captured `"tenants/current/users/current"` |
| 0x122ddc0 | empty | ActivateUser response |
| 0x122e4b8 | 1 string | parent request (SubscribeFriendUsers captured with `tenants/current/users/current`) |
| 0x122ef20 | 1 string, 2 repeated FriendUser | friend group: field 1 = HEX account id (see 2.3) |
| 0x122fae0 | 1 string, 2 repeated BlockingUser | blocking group |
| 0x12309e0 | 1 repeated group(0x122ef20), 2 repeated group(0x122fae0), 3 Duration (0x11b2768) | SubscribeFriendUsersResponse |

presence.pb.cc
| Serializer | Shape | Role |
|---|---|---|
| 0x1232404 | 1 Presence, 2 FieldMask (0x12012a0) | presence update |
| 0x1232c58 | 1 string | open {parent} |
| 0x1233910 | oneof: 1 update(0x1232404), 2 msg(0x1239758), 3 open(0x1232c58) | KeepAliveRequest |
| 0x1234400 | empty | |
| 0x1234c64 | oneof: 1 msg(0x1239040), 2 empty | KeepAliveResponse |
| 0x1235d80 | 1 string parent, 2 repeated string, 3 FieldMask, 4 varint, 5 string | SubscribePresencesRequest |
| 0x1236d40 | 1 repeated Presence, 2 string | presence batch (+ token) |
| 0x1237640 | empty | |
| 0x1238090 | oneof: 1 batch(0x1236d40), 2 empty, 3 msg(0x1239040) | SubscribePresencesResponse |
| 0x1239040 | 1 Duration, 2 Duration | timing; the client reads field 2 as seconds |
| 0x1239758 | 1 string | heartbeat {name} |

resources.pb.cc
| Serializer | Shape | Role |
|---|---|---|
| 0x123a92c | 1 bool, 2 bool, 3 bool | FriendUser flags |
| 0x123b2a4 | 1 string, 2 string, 3 string, 4 flags(0x123a92c) | FriendUser; the client reads field 2 |
| 0x123bfd4 | 1 string, 2 string, 3 string | BlockingUser |
| 0x123c948 | 1 bool | |
| 0x123d5a0 | 1 string name, 2 varint state, 3 map<string, Value> attributes, 4 Timestamp, 5 bool unsubscribed, 6 msg | Presence |
| 0x123ec78 | map entry: 1 string key, 2 Value {2 bool, 3 varint, 4 float, 5 double, 6 Timestamp, 7 string, 8 bytes, 9 msg, 10 msg, 11 string} | attributes entry |

Presence field names for 1/2/3/5 are VERIFIED by the captured field mask `name, state, unsubscribed, attributes`
(scratch\presence-stream.log) and builders `FUN_0122c770/7b0/7f0/840`. Presence resource name format
`tenants/<t>/users/<u>/presence` (parser `FUN_0122cb20`, builder `FUN_0122cfa0`). Type names are stripped from the
binary, so message NAMES above are mine.

#### 2.2 Captured wire (VERIFIED, scratch\presence-stream.log)

- `Friends/ActivateUser {1: "tenants/current/users/current"}` -> server answers empty.
- `Friends/SubscribeFriendUsers {1: "tenants/current/users/current"}` -> server sends one empty message, keeps open.
- `PresenceService/SubscribePresences {1: parent, 3: {name, state, unsubscribed, attributes}}` -> same.
- `PresenceService/KeepAlive` first frame `{3: {1: "tenants/current/users/current"}}` (38 bytes). It is the ONLY
  frame ever captured (4 times), because the server replies with an empty message and the client waits for an ack.
- `ListFriendUsers` / `ListBlockingUsers` never appear in the log; the list comes from the stream.

#### 2.3 Game-side friend manager (VERIFIED; class vtable 0x325b650, ctor `FUN_00c75fec`; derived copies at 0x31dab.., 0x32a81..)

1. **System friend list first.** `FUN_022283e8` (slot 14) calls `nn::friends::GetFriendList(&n, Friend[10], uid,
   offset, 10, filter)` in a loop (PLT 0x282b690, symbol resolved through the dynamic table) and creates one entry
   per system friend (`Friend` is 0x200 bytes at entry+8). Init `FUN_02227fe8` uses
   `GetPlayHistoryRegistrationKey`, `CheckFriendListAvailability`, `EnsureFriendListAvailable`,
   `AsyncContext::HasDone` (PLT 0x282b640/650/660/670).
2. **NPLN friend users are matched onto those entries.** Stream handler `FUN_01213f40` -> `FUN_01206a40`: for each
   group, `strtoull(group.field1, NULL, 16)` = 64-bit account id; for each FriendUser, field 2 is parsed as a user
   resource name (`FUN_00f18090`). `FUN_02228718` (slot 16) then takes each {user, account id} and finds the entry
   whose `nn::friends::Friend::GetAccountId()` equals it (`FUN_022288b8`, PLT 0x282b390); it stores the NPLN user
   string at entry+0x390, sets entry+0x3b0 = 1, and (re)subscribes presences (`FUN_01217f9c` with callback vtable
   0x325b750). **A friend that is not in the system friend list can never appear.**
3. **Presence callback** `FUN_02229f10`: finds the entry by NPLN user string (`FUN_02228934`), copies name
   (entry+0x2d0), state (entry+0x2f0), attributes (entry+0x2f8), and compares attribute `SessionId` (string
   0x288b082) with the cached session info (entry+0x300); a change clears the cache.
4. **Friend sessions are fetched by id.** `FUN_02227ad0` (slot 7): for every entry with non-empty name and
   state == 1, collects distinct `SessionId` values and issues one matchmaking query (`FUN_0137b9f8`), at most every
   10 s. Result handler `FUN_0040ea74` (slot 21): status 0 -> `FUN_02227414` copies each returned session (0x90-byte
   items) into the entry whose `SessionId` equals the session id; status 8 -> back-off flag; other -> error queue.
5. **Joinable test** `FUN_022279b8` (slot 4): entry valid, name non-empty, session id non-empty, byte entry+0x278 set
   and u64 entry+0x270 < u64 entry+0x268 (INFERRED: open flag, current members < max members). `FUN_01e36994` is the
   variant used by LobbySelectTarget's password/target logic (checks entry+0x2a2).
6. So "join a friend" = presence attribute `SessionId` -> session query -> cached session info -> LobbyJoin joins
   that session id. No name search, no QueryGameSessions by attribute (VERIFIED flow; RPC name INFERRED:
   BatchGetGameSessions, since the request is a list of ids).

SDK presence conversion `FUN_0122b7e0` (VERIFIED): proto state 0 -> message ignored; 1 -> game state 1 (online,
attributes copied); 2 -> game state 0; 3 -> game state 2 (attributes dropped for 2 and 3). `unsubscribed` true
removes the user from the SDK cache (`FUN_0122c00c`). Stream handler `FUN_012215c0`: oneof 1 = batch (string
field 2 saved, INFERRED resume token = request field 5); oneof 2 = sets client+0x170; oneof 3 = re-arms a watchdog
with Duration field 2 seconds (1 s if the option flag is off).

#### 2.4 What the game publishes (VERIFIED unless marked)

- Attribute builders: base `FUN_02228bcc` sets `SessionId` = current game-session id string (session handle +0x38,
  via `FUN_01a9637c`); game override `FUN_01e36dd8` adds `DriverId` and `StickerId` (ints). `FUN_02228b14` sets
  `SessionId` to "" when leaving; `FUN_01e36c68` re-sends DriverId/StickerId at most every 10 s when they change.
  `FUN_02228a20` switches publish/clear on whether the player is in a session (`FUN_02235504`).
- The session id is published as the plain id string, not a short alias (the 6-digit Room ID is a separate RPC).
- KeepAlive writer `FUN_012294c0` call order (INFERRED order, VERIFIED calls): (a) open, oneof 3 {parent} — matches
  the capture; (b) update, oneof 1 {Presence{name = own presence resource, attributes}, FieldMask with
  `attributes.<key>` paths (`FUN_0122c880`)}; (c) heartbeat, oneof 2 {name}. Reader `FUN_01227d20`: response oneof 1
  sets the "acked" flag that lets the writer continue and re-arms the timer from Duration field 2; oneof 2 ends the
  read loop.

#### 2.5 Server today vs needed

Today (server/mkwmp_server.py): SUBSCRIPTIONS = SubscribePresences + SubscribeFriendUsers get one empty message and
stay open (l.1265-1272); KeepAlive frames are logged and answered with an empty message (l.1016-1029);
ActivateUser gets an empty reply. Result: empty friend list, and the client never sends its presence.

To make a roster member appear online and joinable (field numbers VERIFIED; values marked):
1. **System friend list**: the runtime/probe must return the roster from `nn::friends::GetFriendList` with a stable
   64-bit account id per launcher username (and a nickname for the UI). Without this nothing below shows.
2. **SubscribeFriendUsers** stream, one message per roster change (full snapshot, INFERRED: handler replaces the
   list): `{1: [ {1: "<account id in hex>", 2: [ {2: "tenants/<t>/users/<uuid>"} ]} ... ], 3: Duration}`. Send
   field 3 and repeat a message before it expires (watchdog is armed from it; with it absent/zero and the option
   flag off the client arms 1 s — INFERRED consequence: reconnect).
3. **SubscribePresences** stream: `{1: {1: [Presence{1: "tenants/<t>/users/<uuid>/presence", 2: 1, 3: {"SessionId":
   Value{7: "<session id>"}, "DriverId": Value{3: n}, "StickerId": Value{3: n}}}]}}`; push the same message again on
   every change; send state 2 (or `5: true`) when the friend goes offline. Also send oneof 3 `{2: Duration}`
   periodically as keep-alive (INFERRED need, same watchdog pattern).
4. **KeepAlive** (bidi): answer every client frame with `{1: {1: Duration, 2: Duration}}` instead of an empty
   message. Then read oneof 1 updates and store `SessionId`/`DriverId`/`StickerId` per user; treat stream close as
   offline. Never send oneof 2 unless closing.
5. **Session query**: answer the id-list query with the friend's GameSession (id equal to the published
   `SessionId`, member count below max, open flag set), then accept the join.

The user id string must be identical in three places: FriendUser field 2, the Presence name, and the auth token's
user (the game compares the parsed strings; that both parsers yield the same component is INFERRED).

---

## 1. Post-match flow and the referee (priorities 1 and B)

Sources: decompiles in `C:\Users\alcmo\.claude\jobs\7b106074\tmp\re-map\` (m1.c, m3.c, p1.c, q1.c, r1.c, t3.c, n1.c, n2.c, s1.c),
the scene graphs in `scratch\game\romfs\AI\*.ainb` (dumper: `re-map\ainb.py`), and the server logs `scratch\run-lan6.log`, `run-lan7.log`.
VERIFIED = read in code/data/log at the address or file given. INFERRED = reasoning.

### 1.1 Conclusion first

1. **The match never ends for the clients because our referee answers wrongly, in two ways** (VERIFIED in code, and matches the logs):
   - The first answer (status 0) carries `result = [1..N]`. It must be an **empty** list: `result` is the *ordered list of race-player indices that have finished/been eliminated so far* (0-based), not ranks.
   - The final answer (status 1) is sent on the **first** report, with `result` = that reporter's partial list (e.g. `[1]`). Status 1 must only be sent when `result` lists **every** player of the match.
2. A client considers the race finished only when **every race player is "rank fixed" by the referee** and the referee result is final (`FUN_02125730`, `FUN_0212778c`, scene node `AIRaceSeqWaitRankFixed` `FUN_00d180e0`). With `result = [1]` and status 1, player 0 is never rank-fixed, so neither client leaves the "RaceEnd" state: the winner keeps auto-driving, the loser's kart stands still, and the generic watchdogs fire (0x9b928162 idle kick for the stationary kart; the 30 s `0xcd1bf9ca` timer on the finished player).
3. **Returning to the room and the next vote is P2P-only.** The room loop (`PrivateRoom.module.ainb`, used for worldwide rooms too) runs on Pia messages between the same mesh members; no new matchmaking ticket, GameSession update or Gamesync document was found on that path (VERIFIED for the graph and the node code read; INFERRED that nothing else is needed). The server's job is to let the race end correctly and keep the session/mesh alive.
4. Open point: in `run-lan7.log` user 2 tore down its Gamesync user session 3 s after the premature status 1 (targets 2/3/4 removed, `__pus/<user2>` rewritten, KeepUserSession half-closed). The code path that requested that leave was not identified statically; it is expected to disappear with a correct referee, and must be re-checked live (see 1.8).

### 1.2 What the logs show (VERIFIED, `scratch\run-lan6.log`, `run-lan7.log`)

| Time | Event |
|---|---|
| lan6 16:21:17 | both users write `@@RefereeRequest/<ussid>/Request/2411989377_1_0_1_a261f`; server answers `@@RefereeResult/<uuid>` status 0 |
| lan6 16:22:12.81 / .88 | user 1 and user 2 each write `@@RefereeReport/<uuid>/User/<ussid>` with `result = [1]` (update mask `` `result` ``) |
| lan6 16:22:13.34 / .51 | both write the same document again with `result = [1, 0]` (0.5 s later). The server of that run never finalized. |
| lan6 16:11:57 | another match: both write `result = [1, 0]` in one go |
| lan7 16:43:14 | request (Battle): `mode "battle"`, `sub_mode 5`, `rating_enabled true`, `option {total_player_count 2, dropout_rank -1}`, **`users = []`, `ussid_map = {}`** |
| lan7 16:44:51.766 | user 1 report `result = [1]` -> server immediately sends status 1 with `result = [1]` |
| lan7 16:44:52.162 | user 2 report `result = [1]` -> status 1 again. **No `[1, 0]` report ever follows.** |
| lan7 16:44:54.95-55.32 | user 2 removes targets 3, 2, 4, writes `__pgn/All/__pus/<user 2>`, half-closes KeepUserSession. User 1 keeps writing `@@KeepAlive` every 15 s. |

So: the report document holds the client's **whole local finish list**, rewritten each time a player finishes (`[1]`, then `[1, 0]`); values are 0-based race-player indices (VERIFIED from the payload bytes `4a 08 0a 02 18 01 0a 02 18 00`). After a premature status 1 the second report is never written (VERIFIED observation; cause INFERRED: reporting is tied to a status-0 result, see 1.4).

### 1.3 The rule ("referee") object

One object per race, class family with vtables at `0x32323f0` (base), `0x3232570` / `0x32327b0` / `0x32328d0` (race family: finish processing `FUN_02129320`, per-frame `FUN_02128120`+`FUN_02128568`, `FUN_0212a3e4`, `FUN_0212a618`), `0x32329f0` / `0x3232d28` (elimination/battle-like: finish processing `FUN_021264ec`), `0x3232b90` (survival / Knockout: several requests). VERIFIED (vtable dump). Which concrete class is Balloon vs Coin battle was not resolved (INFERRED from behaviour).

Vtable slots (offset): VERIFIED by reading each function.

| Slot | Function | Meaning |
|---|---|---|
| +0x38 | `FUN_02125730` | IsFinished: every current race player has rank >= 0 and `done`, and, when the referee flag `this+0x90` is set, slot +0xc8 is true |
| +0x78 | `FUN_021264ec` / `FUN_02129320` | per-frame finish processing: detect locally finished players, report them, apply the referee result |
| +0x80 | `FUN_0212701c` / `FUN_0212d75c` | write the referee request(s) |
| +0x88 | `FUN_021271b8`->`FUN_021271c8` / `FUN_0212d9fc` | wait for the status-0 result (36 s, `FUN_0212747c` returns 36000) |
| +0xa0 | `FUN_02127500` | retire: sets field `retired = 1` on my report document (`FUN_0125e228(ref, "retired", 1)`) |
| +0xa8 | - | returns the request object (`this+0x98`) |
| +0xc0 | `FUN_021275a8` / `FUN_02129138` | report one player: if not yet in the local "reported" list, `FUN_01e3865c` |
| +0xc8 | `FUN_0212778c` | IsRefereeFinal: request `+0xa1` |
| +0xe8 | `FUN_0212785c` / `FUN_02129e44` | rank from result index; `FUN_02129e44` returns `playerCount - 1 - index` when `this+0x90` is set (elimination order -> rank) |

Layout (VERIFIED): `this+0x18/+0x20` = per-player array of 8-byte entries `{int rank; u8 done (+4); u8 eligible (+5)}`; `this+0x40/+0x48` = per-player 0x30-byte finish entries (finished flag +0x10, time +0x18); `this+0x50/+0x58` = list of locally finished entries; request object = `this+0x98`: key string, `+0x90` result uuid (16 bytes), `+0xa0` has-uuid, `+0xa1` **final**; `this+0x140` request time; `this+0x148` failed flag.

### 1.4 Referee protocol as the client implements it

**Request** (`FUN_0212701c` -> `FUN_01e37e98`, VERIFIED): written once per match by every client at race start to
`@@RefereeRequest/<my ussid>/Request/<key>`; key `"%u_%d_%d_%d_%x"` (0x28eb198) built from a session value and race counters, identical on all clients (VERIFIED in the logs). Survival writes one request per round with key `"Surv_%u_%d_%d_%d_%x"` (0x28fc6de, `FUN_0212d75c`).
Fields (strings referenced in `FUN_01e37e98`): `users` (list), `ussid_map` (map), `mode` (`"normal"` / `"survival"` / `"battle"`), `sub_mode` (int), `rating_enabled` (bool = `FUN_006557e8`: online and flag bit 4 or 25 of `*(0x33d2548)+0x114`), `option { total_player_count (popcount of the race player mask), dropout_rank }`. `dropout_rank` is -1 except in survival, where it is `FUN_01b136dc(round, playerCount)+1` for intermediate rounds and 1 for the last (`FUN_0212d75c`).
`users` comes from the per-player table `*(*(0x33e7ee0+0x98)+0x10)` (16-byte entries, `FUN_01e46660`); in our captures it is **empty**, i.e. that table maps no race player to a network id (VERIFIED in the log). Section 2 gives the likely cause: `PlayerStatic` is written in several partial (merge) writes and our server keeps only the last one, wiping `p2p_sid`/`p2p_lid`, so `FUN_01e37358` cannot map players (mechanism VERIFIED in code, effect INFERRED).

**Accept** (`FUN_021271c8` / `FUN_02127388`, VERIFIED): the client looks in the result ring (`ctx+0xe8`, filled by `FUN_00498404`) for an entry whose `request_id` equals its key and whose `status == 0`, and keeps the document id (uuid) at request `+0x90`. No such entry within 36 s -> error `0x606314c9` (`FUN_01e46958`).

**Report** (`FUN_021264ec`/`FUN_02129320` -> slot +0xc0 -> `FUN_01e3865c`, VERIFIED): when a player is locally seen as finished (kart finish object present and state outside 7..11), the rule records it and reports its race-player index. `FUN_01e3865c(ctx, uuid, &index)`:
- does nothing unless the match controller state `*(*(*0x32f29f8)+0xd8)+8` is 0 (so nothing is reported once the session is in error/leaving);
- appends the index to the entry's local list `entry+0x104` (24 ints, -1 = free) and writes the whole list as field `result` of `@@RefereeReport/<uuid>/User/<my ussid>` (`FUN_0125e23c`, document reference from the list `ctx+0x8f88`, created by `FUN_01e393dc`).
Race family: first the players that really crossed the line (`+0x74` of the per-player finish object), then, when everybody left is in the forced-finish state (`FUN_02129cb0`), the rest.

**Result** (`FUN_00498404` reader, `FUN_02126ad4` consumer, VERIFIED):
- `request_id` string, `status` int, `result` list of up to 24 ints copied to `entry+0xa4`, and when `status == 1` `ratings` = map `"<ussid>" -> double`, stored as float at player node `+0x8e0`.
- For each `result[i] >= 0`: the player with that index, if `eligible` (set by event 0x7dde022f / by its own finish, `FUN_021288d8`, `FUN_02129e98`) and not yet `done`, gets `rank = slot+0xe8(i)`, the event `0xae42b7bf` is posted (player, rank, race id) and `done = 1`.
- `status > 1` -> `FUN_01e46958` posts `0x606314c9` (referee failure) and the rule gives up (`this+0x148 = 1`).
- `status == 1` and **every player of the session mask is `done`** -> request `+0xa1 = 1` (final).
So `status`: 0 = match open (accepting reports), 1 = final verdict with ratings, >= 2 = error (VERIFIED). `result[i]` = race-player index that finished i-th (VERIFIED by the consumer; for elimination modes the order is elimination order and the rank is reversed by `FUN_02129e44`).

**Ratings** (`FUN_0212603c` / `FUN_02129830` -> `FUN_01e445a8`, VERIFIED): when `rating_enabled`, once a player's position in `result` is known the local rate is recomputed from the server value in the player node (`+0x8c0`), clamped to >= 500, with the player's index among the non-disconnected players and the player count. A flat `ratings` map (e.g. 3000.0 each) is accepted.

### 1.5 From "all finished" to the results screen (VERIFIED: `RaceSequence.root.ainb` + node code)

`AIRaceDirector` selects, in order, the pins `Ready` -> `Sync` -> `CountDownAndStart` -> `RaceEnd` -> `Result` -> `Finish` -> `ProcessEnd` (also `Retire`, `Demo`, `Wait`).
- `RaceEnd` = Simultaneous[`AIRaceSendStateMessage`, **`AIRaceSeqWaitRankFixed`**, `AIGlobalSeqChatUIReady`].
  `AIRaceSeqWaitRankFixed` (vtable 0x317c188; update `FUN_00d180e0`, message handler `FUN_00d5fb10`): collects the `0xae42b7bf` / `0x03d346bd` "rank fixed" messages (only for this race id and for players in the participant mask) into `this+0x28`; it completes only when every race player is either in that mask or in one of the three player masks at `B+0x10`, `B+0x14`, `B+0x18` (`B = *(*(0x33d2548+200)+8)`; exact meaning not decoded, INFERRED: players that left / do not need a rank), and then after 0x78 frames (0xf0 in the other mode). The handler only accepts players that are in the mask `B+0xc`.
- `Result` = Sequential[`AIRaceSendStateMessage`, `AIWaitSceneFader`, `AIRaceSeqShowResult`, `RaceTimeAttackResult.module`, `AIRaceSeqShowTeamResult`, `AISendNoPropertyMessage`, `AIWaitFrame`].
- `Finish` = [`AIRaceSeqSaveReplay`, `AIRaceSendStateMessage`].
Therefore the results screen is reached only after the referee has listed every (still connected) player. This is the exact place both clients were stuck.

Timers and watchdogs around it (VERIFIED):
- `FUN_02128568` (race family, per frame): when the local player has finished (`vcall +0x38` of the player's rule entry) and (`FUN_02141adc` "all players done" or the byte `+0x40` of the race info is 0) and the global byte `0x33e80c4` is 0, it arms the timer at `this+0x15c` for 0x708 frames with error id `0xcd1bf9ca` (the id is posted on expiry by the base update `FUN_00529fcc`; per section 4 the timer is cancelled only by the local event `0x039f129b`, INFERRED "result accepted"), and at the same time installs a task at `this+0x198` with predicate `FUN_0212a1b4` (table 0x3232740; checks `FUN_003afd58`, a host/station comparison) and action `FUN_0212a230` (table 0x3232778) which posts the event `0x9d76ffd5`. `FUN_020da900` (per-player finish object) turns `0x9d76ffd5` and `0xa20d8985` into finish state 2 (forced finish). `0x33e80c4` is set at race setup by `FUN_003fbd04` from `FUN_003fbec4()` / flag bit 22 of `*(0x33d2548)+0x114` (a play-mode flag, not a network state).
- `FUN_0212a3e4` / `FUN_0212a418`: when all but one player have finished (or the team condition holds) `FUN_02128f00` posts `0xa20d8985` (race end for the rest).
- `FUN_020d43dc` (kart driving-state watchdog): `0x9b928162` when the kart position has not changed for 0x708 frames (or `+0x53c > 0x708`), `0x5382e895` when race progress does not advance for 180 frames, `0x660ac5da` for the wrong-way flag; after the counter `+0x1d0` reaches 0x2d0 the error is posted through `FUN_020d53c8` -> `FUN_020d4ab0` -> `FUN_02624bf0`. `FUN_0035ce38` raises `0x9b928162` for a player that barely moved (`+0xb8 > 3000`, fewer than 0x14 moves). These only run while the kart is still in the driving state, i.e. while the player is not "finished" - which is why the eliminated/stationary player of a match that never ends gets `0x9b928162`.

### 1.6 After the results: return to the room and next vote (VERIFIED graphs; node internals partly read)

`RacePauseSequence.root.ainb` runs these in parallel for the whole online session: `PlayRequestManager`, **`PrivateRoomSequence`**, `AIGlobalSeqP2PSync`, `TimeoutManager`, `VSNextCourseSelect` (offline VS), `ReturnFromRace`, `AIGlobalSeqSyncScoreManager` -> `AIGlobalSeqSyncScore`, `AIGlobalSeqPlayReportManager`, ...
- `ReturnFromRace.module`: `AIRaceSeqReturnToGlobal` pins `Wait` / `Return` / `Award`; `Return` runs `_LocalModule_f1319e4bf6c8` = fader, `RaceSeqFinalize.module` (`AIRaceSeqFinalize` pins `Wait` / `NetToRace` / `NetToFreerun`), `AIGlobalSeqReadyFreeRun`, ..., `AIGlobalSeqRaceFinishFaderOpenCheck`. So after an online race the scene goes back to free roam (the room) or straight into the next race (`NetToRace`).
- `PrivateRoomSequence.module`: Simultaneous[`SyncPrivateRoom`, `GroupPlayModeSelectorBinary` --"Any"--> `PrivateRoom.module`, `SwitchSeq` --"Process"--> `AnybodySurvivalSeq.module`]. `PrivateRoom.module` is the room loop for Race and Battle rooms, worldwide ones included:

  `PrivateRoomMain` --Next--> `GroupPlayModeSelector` {Race, Survival: `SendVoteElection`; Battle: `SyncBattleAnybodyRule` -> `SendVoteElection`} -> `StartTimeoutCounter` -> `ReadyVoteSetter` -> `SyncRacePlayerInfo` -> `PreloadPlayer` -> `SyncState` --Success--> `WaitPrivateSeqReady` -> `OnlineUICleanup` -> `setupUIInfoAfterCleanup` -> {`ShowGroupRule` | `WaitSyncPlayMode`} -> {Race: `CourseVote.module` (`VoteCourse`/`VSStageSelect`/`VSCourseSelect` -> `SubmitVoteReport`); Battle: `BattleListup` -> `VoteOneCourseDirect` -> `SubmitVoteReport`; Survival: `GrandprixListup` -> ...} -> `WaitMapOut` -> `ClearRacePoint` -> `CourseRoulette` --Next--> `SyncNextStartPoint` -> `SyncTiming` --Success--> `CourseVoteWaitPlayRequest` {`ParentNext`: `SendGroupSetting` -> `RequestPlay`; `ChildNext`} -> `WaitRaceStart` -> ... -> `ReadyVoteSetter` -> **back to `PrivateRoomMain`**. Every `Error` edge also leads back to `PrivateRoomMain`.

  "Parent" = the mesh host: it sends the group setting and requests the play; "Child" waits for it.
- `PrivateRoomMain` per-frame `FUN_01a5a444` (VERIFIED read): works on P2P ready masks (`*(*(0x33d2548)+0x188)+0x40/+0x44`), sends room-state messages (`FUN_0213cd20`), starts the vote timer (message `0x44733945` with the frame count at `this+0x13c`), and posts error `0x27ca935a` when the remaining-players condition holds for 15 s. No Gamesync or NPLN call in it apart from reading `FUN_0045b24c(1)`.
- `StartTimeoutCounter` (`FUN_01a7b910`) sends `0x9d424b78` / `0x44733945` with `FUN_02141d24() * 6 / 100` frames; `TimeoutManager` (`FUN_01a859f0`, `FUN_00d53cb4`) counts it down and closes the vote UI (message `0xbbd6e820`, 0x22) at zero.
INFERRED from the above: once the race ends normally, the same players continue into the next vote without any server action, as long as the Pia mesh and the Gamesync user sessions stay up.

### 1.7 Race vs Battle vs Knockout (what differs for the server)

| Mode | `mode` | Requests | End condition (client) | Server |
|---|---|---|---|---|
| Race | `normal` | 1 (`%u_%d_%d_%d_%x`) | players report finishers in order; 30 s after the local finish the rest are force-finished and reported | result = finish order, status 1 when it has `total_player_count` entries |
| Battle | `battle` (`sub_mode` 5 seen) | 1 | reports in elimination/finish order; when all but one are done the last one is ended by `0xa20d8985` and reported (lan6: `[1]` then `[1, 0]`) | same |
| Knockout (survival) | `survival` | one per round, `Surv_...` keys, `dropout_rank` per round | `FUN_0212d9fc` waits for a status-0 result for every round; per round `FUN_0212cab0` checks `result` up to the round's cut | answer every request; each result is completed independently (INFERRED: final when the round's expected count is listed) |

VERIFIED for the request side; the per-mode completion counts on the server side are INFERRED.

### 1.8 Concrete server behaviour for the referee

1. On the first `@@RefereeRequest/.../<key>`: create `@@RefereeResult/<uuid>` `{request_id: <key>, status: 0, result: []}` and push it to the `@@RefereeResult` listeners. Remember `total_player_count`, `mode`, `dropout_rank`. (Later requests with the same key: no new document.)
2. On every `@@RefereeReport/<uuid>/User/<ussid>` write: read its `result` list; merge into the match's ordered list (append indices not yet present, in the reported order; when reporters disagree keep the first seen order). Push the updated result document with **status 0** while the list is shorter than `total_player_count`.
3. When the merged list has `total_player_count` entries: push `{request_id, status: 1, result: <full list>, ratings: {"<ussid>": <double>, ...}}`.
4. Fallbacks (INFERRED, to avoid a hung match): if a member's user session ends, append its race index if known or reduce the expected count; if no report arrives for ~40 s after the first one, finalize with the missing indices appended. `retired = 1` on a report document marks a player that quit (`FUN_02127500`).
5. Never send status >= 2 unless the match must be aborted (clients show `0x606314c9`).
6. Re-test with two players and watch for: second report `[a, b]` from both, status 1 with both indices, results screen, return to free roam, `PrivateRoomMain` vote. If a client still closes its user session right after status 1, trace the caller of the leave (match controller op 10) with the probe edge trace around `main+0x2237828`.

---

## 2. Gamesync document catalog


Sources: static decompiles of scratch/main.bin (addresses = image offsets) plus the decoded
WriteDocuments bodies already present in scratch/run-lan*.log (ground truth for wire shapes;
1314 client writes over all runs). Every claim is tagged **V** (VERIFIED: read in code/data or in a
captured client frame; address/log given) or **I** (INFERRED).

Decompiles made for this area: re-map/a2-b1.c .. a2-b4.c. Helper scripts: a2-off.py (find accesses
to big ctx offsets through `mov w,#K; add x,x0,x`), a2-mm.py (murmur3 doc-name hashes), a2-stub.py.

---------------------------------------------------------------------------------------------

### 0. Three findings that change what the server must do (read first)

1. **Game writes are MERGE writes; our server replaces the document.** (V) Every write that goes
   through the game writer `FUN_0049c9d8` -> `FUN_0125efa0` -> `FUN_0129f204` is
   `Write{1: Update{1: Document{name, fields}, 2: FieldMask{1: "`field`", ...}}}` where the mask
   lists exactly the fields in that write (run-lan9.log lines 567-821: `PlayerStatic/1` is written
   5 separate times with masks {p2p_sid,p2p_lid}, {name}, {vehicle,driver},
   {rating_point_coefficient,rating_point_raw_value}). `server/mkwmp_server.py store_write()`
   builds the stored doc from the incoming fields only, so each partial write wipes the others.
   Consequence (V for the mechanism, I for the causal link): `FUN_0049a5a0` re-reads all fields
   from the latest snapshot, so `p2p_sid` becomes 0 after the next partial write;
   `FUN_01e37358` (maps node `p2p_sid`+`p2p_lid` -> Pia player index) then returns 0xff and
   `FUN_01e37e98` emits **empty `users` and `ussid_map`** - exactly what every captured
   `@@RefereeRequest` shows (run-lan6.log 4019-4092: `ussid_map: {}`, `users: []`).
   Fix: apply the FieldMask (merge listed fields into the stored doc; mask `["*"]` = replace;
   present-but-empty mask with no fields = touch only).
2. **Change kind 3 is REMOVED and the server must send it.** (V) `FUN_01271c00` (KeepUserSession
   reader, field 2 Change): kind 1/2 -> snapshot with data (`FUN_0126643c` -> `FUN_01272248`);
   **kind 3 -> takes `Change.3.Document.name` (`FUN_012a0c00`), builds a doc ref (`FUN_0125e250`)
   and delivers a NOT-EXISTS snapshot (`FUN_012663c0`) to the same listener** (0x1271c00, the
   `iVar2 == 3` branch). docs/NETCODE.md currently says "kind 3 is not a document change (do not
   send)" - that is wrong. Listener callbacks test `exists` (`FUN_012664b0`), e.g. the `__us`
   member callback erases the member.
3. **Deferments and explicit deletes are sent by the client and currently ignored.** (V)
   Deferment = "delete this document when my user session ends" (section 6). The client also
   sends plain `Write{4: Delete{1: name}}` (run-lan9.log 4789-4797:
   `docs/__pgn/All/__pus/<uid>` on leave). The server logs both as `[]` / `('', 0)` and keeps the
   documents, so stale `__pus`, `PlayerStatic`, `Companion` docs survive a leave.

---------------------------------------------------------------------------------------------

### 1. Wire shapes (all V, from captured client frames + serializers)

```
WriteDocumentsRequest { 1: repeated Write, 2: repeated DefermentOp }
Write (oneof)   1: Update    { 1: Document{1 name, 2 fields}, 2: FieldMask{1: repeated string path},
                               3: Precondition{1: bool exists} }
                3: Transform { 1: name, 2: repeated FieldTransform{1 path, 3 increment Value,
                               4 maximum Value, 6 string, 7 string} }      (only __pus upcsid)
                4: Delete    { 1: name }
DefermentOp     { 1: { 1: Deferment{ 1: "userSessions/current/deferments/<path without docs/>",
                                     2: repeated Write } } }
Value (oneof)   2 bool(varint) | 3 int(varint, -1 = 0xffff..ff) | 4 float(fixed32) | 7 string |
                8 bytes | 9 list{1: repeated Value} | 10 map{1: repeated Entry{1 key, 2 Value}}
Change          { 1: decimal target id, 2: kind (1 ADDED, 2 MODIFIED, 3 REMOVED), 3: Document }
```
- Field paths in masks are backtick-quoted: "`name`". (V, log)
- Native builders: `FUN_00f12c64`(int32 -> kind 3), `FUN_00f12cdc`(uint32 -> kind 3),
  `FUN_00f12d54`(cstring -> 7), `FUN_00f12b78`(bool -> 2), float via `thunk 0x125e230`
  (`FUN_00f06f84`, kind 4), list `FUN_00f12db4`, map `FUN_00f12e78`/`FUN_00f12f3c`. (V)
- Readers (thunks at 0x12665c8..0x12666b8): bool `0xf082f0`, int `0xf08408`, number-as-double
  `0xf0851c`, string `0xf0873c`, bytes `0xf08a28`, list `0xf08bcc`, map `0xf08d10`,
  is-absent `FUN_01266574`. Missing keys read as 0 / "" (V: `mov x0,xzr` fallthrough).
  Whether the number reader accepts both kind 4 and kind 5 was not checked (the client itself
  writes floats as kind 4; our referee `ratings` use kind 5 and the game does show them - I).

Write primitives of the SDK doc-ref class (V):

| Primitive | Wire | Used by |
|---|---|---|
| `FUN_0125efa0` set(fields, options) -> `FUN_01298260`/`FUN_0129f204` | Update + mask of the written fields (merge) | game writer `FUN_0049c9d8` (all PlayerStatic/Companion/@@KeepAlive), `@@RefereeReport` |
| `FUN_0125e7a0` create -> `FUN_01297f80`/`FUN_0129e3f8` | Update, no mask, `3:{1: false}` (must not exist) | `@@RefereeRequest`, `Global/Session` |
| `FUN_0125f140` update(precond) -> `FUN_01298300` | (not captured live) | `__gs/m` (`FUN_02235f64`) |
| SDK set -> `FUN_01298120` etc. | Update + mask `["*"]` (replace) | `__pus`, `__stu`, `__mt/nat_traversal` |
| `FUN_0125cde0` -> `FUN_01298420`/`FUN_0125d8e0` | DefermentOp with a Delete write | section 6 |
| delete | `Write{4:{1 name}}` | `__pus/<uid>` on leave |

All of them end in `FUN_01298560` (batch -> WriteDocumentsRequest) -> `FUN_01298c3c`.

---------------------------------------------------------------------------------------------

### 2. Catalog

Doc ids: `<ussid>` = decimal `ussid` of the writing player ("%u", 0x49ad0c; the per-player map
node stores it at node+0x10 and every game path uses that string). Game collection objects are
created by `FUN_00bf081c` (ctor) -> `FUN_00bf0b54(obj, name, capacity)`: ctx[0] PlayerStatic
(cap 192), ctx[1] DPID (1), ctx[2] Global (2), ctx[3] Companion (192), ctx[4] @@RefereeResult
(100). Documents inside a collection object are looked up by murmur3_32(doc id, seed 0)
(`FUN_00163ec0`, lookup `FUN_02252df8`). (all V)

#### 2.1 Game-level documents

| Path | Author | Fields (type) | Writer / trigger | Reader |
|---|---|---|---|---|
| `PlayerStatic/<ussid>` | client | `name` str; `play_history_key` bytes(0x40); `p2p_sid` int64 (Pia station id); `p2p_lid` int (local player 0/1); `vehicle` int (hash); `driver` int (hash); `rating_point_raw_value` float; `rating_point_coefficient` float; `greeting_type` int; `greeting_id` int; `streamer_min_ussid`, `streamer_max_ussid` int (read only, no writer found) | `FUN_01e37654` name+play_history_key (once per session via `FUN_01e39c50` from `FUN_0049952c`); `FUN_01e377c4` p2p_sid/p2p_lid (per frame from `FUN_00195a20`, only when it differs from cache ctx+0x8e10 / +0x8ed8); `FUN_00347624` (entry 0x3475f0) vehicle/driver and `FUN_00347760` (entry 0x347720) rating, both from `FUN_003462c0`, only changed fields (cache ctx+0x8dc0+slot*200: +0x5c,+0x60,+0x68,+0x6c); `FUN_01e37a2c` greeting (from 0x1a38a50) | `FUN_0049a5a0` (0x49aee4..0x49b138) -> player node: name +0x878.., p2p_sid node+0x8c8, p2p_lid +0x8d0, vehicle +0x8d4, driver +0x8d8, rating raw +0x8e0, coeff +0x8e4 |
| `Companion/<ussid>` | client | `companion_state` int (0 none, 1 leader/gathering, 2 leader ready, 3 follower); `my_group_id` int; `course_sid` int; `track_id` str; `matching_type` int; `leader_user_id` str; `leader_group_id` int; `delegation_token` str | `FUN_01e3902c` {state 0}; `FUN_01e3891c` {state 1, my_group_id, course_sid, track_id ""}; `FUN_01e38b1c` {state 2} (only when own state == 1); `FUN_01e38d28` {state 0, leader_group_id}; `FUN_01e39228` {state 3, leader_user_id, delegation_token}; `FUN_01e390f8` {track_id, matching_type} (from `FUN_00195a20` when mgr+0x12d0 is set and own state is 1 or 2). Callers: 0x1a793f0, 0x1e3f774, 0x1e3f88c | `FUN_0049a5a0` (0x49afec..) -> node+0x930 state, +0x934 my_group_id, ... |
| `@@KeepAlive/<ussid>` | client | none (empty fields, empty mask) | `FUN_00497ef4` per frame: if >= 15000 ms since ctx+0x8f58, one write per local player; only in some game states (gate at 0x498020..0x49804c) | none in the client (no listener, not in ctor list) |
| `DPIS/<ussid>` | **not written by the client** | - | only a deferment delete is registered (`FUN_0049952c`, 0x49987c) | - |
| `DPID/d` | **server** (I) | one field per player: key = decimal ussid, value = bytes of a fixed length | no client writer (string "DPID" only in the ctor) | `FUN_0049a5a0` 0x49ab.. : doc hash 0x27191473 = "d"; `FUN_0126665c(doc, "<ussid>")`; if length == service(0x15e55814)+0x48 -> `FUN_01dec6d4` deserializes into node+0x920 (12-byte struct) |
| `Global/Session` | client (create) or server | `short_session_id` str | `FUN_00498a80` 0x498f5c: when the future at ctx+0x8f68 completes with result 0 it creates `Global/Session {short_session_id}` (create-if-absent). The future is started by `FUN_01e37c54` (GameSessionShortAlias RPC `FUN_0137cd00`, matchmaking stub) from seq node **GetShortSessionId** (`FUN_01a436ac`, vtable slot 0x31692d8) only when ctx short_session_id is empty | `FUN_00498a80` 0x498c28: hash 0x1cde5c61 = "Session" -> copies into ctx+0x48 (buffer ctx+0x54, 0x80). The seq node waits in state 2 until it is non-empty |
| `Global/Sequence` | **server** (I) | `global_time_offset` number (read with the float reader) | no client writer (field string referenced only at 0x498d78) | `FUN_00498a80`: hash 0xe5937b14 = "Sequence" -> ctx+0xd8 |
| `@@RefereeRequest/<ussid>/Request/<key>` | client (create) | `users` list<int ussid>; `ussid_map` map<"player index" -> int ussid>; `mode` str "normal"/"survival"/"battle"; `sub_mode` int; `rating_enabled` bool; `option` map{`total_player_count` int, `dropout_rank` int (-1 = none)} | `FUN_01e37e98` (from `FUN_0212701c`, `FUN_0212d75c`), once per match; key "%u_%d_%d_%d_%x" | server |
| `@@RefereeResult/<uuid>` | **server** | `request_id` str (= key); `status` int; `result` list<int> (<= 24); `ratings` map<"ussid" -> number> (read only when status == 1) | - | `FUN_00498404` (section 4) |
| `@@RefereeReport/<result uuid>/User/<ussid>` | client | `result` list<int> (cumulative finish order, player indexes); `retired` bool true (`FUN_02127538`, string 0x29075d8) | `FUN_01e393dc` (entry 0x1e393c0) called from `FUN_00498404` 0x498a74; queue filled by `FUN_01e3865c` (from `FUN_021275a8`, `FUN_02129138`) | server |
| `@@drppr_ga_scopes/%s/@@drppr_ga_users/%s/@@drppr_ga_profiles/Data` | - | - | `FUN_01e4542c` only formats the path with a scope name (table 0x29c7e8c, 3 entries) and the user id from `*(FUN_02226d70()+0x12b0)+0x20` | **Not Gamesync as far as seen**: no Gamesync doc-ref builder is involved; consistent with the ugcstore BulkGetDocuments profile path (I) |

`FUN_00a48e18` references the string "Global" but is an unrelated parameter-tree loader, not a
Gamesync reader. (V)

#### 2.2 NPLN SDK / session documents

| Path | Author | Fields | Code |
|---|---|---|---|
| `__us/<doc id>` (own doc listened as `__us/<user-id>`, collection `__us/` after the join) | **server** | `uid` str; `st` int; `ussid` int; `ucsid` int; `upcsid` int; `pgn` str; `att` map; `ltc` str; `tn` str | SDK parser `FUN_0128b2b4` (uid, ussid, ucsid, upcsid, pgn); game parser `FUN_02255158` (exists, uid +8, st +0x28, name +0x30, att +0x50, ltc +0x58, tn +0x78, ussid +0x98, ucsid +0x9c); key getters 0x1297a64..0x1297aac |
| `__gs/f` | **server** | `gsid` str, `addr` str, `p` int, `mcn` str, `maxu` int, `rs` bytes | `FUN_01267698`; name check `FUN_01267820`/`FUN_01267910` |
| `__gs/r` | **server** (I) | `ipc` int, `dpc` int | name `FUN_01267bec` = "r", check `FUN_01267a40`, parser `FUN_012679d8`; `FUN_00498a80` 0x498df8 copies `dpc` to ctx+0xe4 |
| `__gs/m` | server initial (I) + **client update** | `cp` bool, `ip` bool, `pw` str, `ebf` bool, `bfmin` int, `bfmax` int, `prp` map (game reads `prp["rule"]` int) | name `FUN_0126768c` = "m", check `FUN_012674e0`, parser `FUN_012673cc`; `FUN_00498a80` 0x498e4c -> ctx+0xe0 cp, +0xe1 ip, +0xe2 ebf, +0xdc rule; also `FUN_02235364`. **Writer `FUN_02235f64(mgr, bool)`**: update of `cp` (+ `ip` if mgr+0x1d10 bit0, + `ebf` if bit1), all set to the same bool, then **blocks** polling the write future and posts an error (kind 0xd/0xe) if it fails. Callers 0x223c78c / 0x223c794 |
| `__pgn/<pgn>/__pus/<uid>` | client (replace) + server transform | `uid` str, `ussid` int, `ucsid` int, `pgn` str, `upcsid` int (server counter `__upcsidn`, +1, max 20000) | `FUN_01281500`; path `FUN_0128b4dc`; same request also registers the deferment for this doc; deleted with `Write.4` on leave |
| `__pgn/<pgn>/__stu/<uid>` | clients (peer mailbox, replace) | `suid` str, `susid` str, `sussid` int, `suscid` int, `pl` bytes, `mp` map | parser `FUN_0128bc00`, path `FUN_0128bf1c` |
| `__stg/<pgn>` | not written in any capture | same parser as `__stu` (`__stg` key at 0x128bdbc) | listened as `docs/__stg/All` |
| `__mt/nat_traversal` | client (replace), write-only | `a` list<map{`p2p` str plugin name, `lu` str local uid, `ru` str remote uid, `rc` int, `re` bool}> | `FUN_012ecd60` (from 0x162c9a0). Telemetry; nothing listens |

No string `__mt` exists in the image; the path comes from the metrics writer. (V)

---------------------------------------------------------------------------------------------

### 3. Answers to the specific questions

#### Q1 Global
- Only two documents exist in the game's view: `Session` and `Sequence` (collection capacity 2).
  Fields: `short_session_id`, `global_time_offset`. `rule` is NOT in Global; it comes from
  `__gs/m.prp.rule`. (V)
- Writer conditions (V): not host-gated. Any client whose GetShortSessionId seq node runs while
  ctx short_session_id is empty starts the alias RPC; on success `FUN_00498a80` creates
  `Global/Session` with precondition exists=false, ignoring the write result, then reads the id
  back from the collection listener. A server can pre-author `Global/Session` and the client
  never calls the RPC (I, follows from the `**(ctx+0x48) != 0 -> return false` check at
  0x1e37c54).
- There is **no round counter / phase field in Global**. Nothing in `FUN_00498a80` changes per
  round. Per-round server interaction is the referee chain only (new `<key>` per match).
- Consumers of ctx+0xd8/0xdc/0xe0..0xe4 were not located (not reachable with the patterns
  tried); what `global_time_offset` drives (GlobalTimeManager / GlobalTimeSyncSeq nodes) is I.

#### Q2 DPID / DPIS / Companion / PlayerStatic
- See 2.1. Per-player doc id is the decimal `ussid` (the `__us` field), not the user id.
- Gate ctx+0x8f50 (V): `FUN_0049952c` runs its body once (registers the 4 deferments per local
  player: DPIS, PlayerStatic, Companion, @@KeepAlive; resets the PlayerStatic write cache at
  ctx+0x8dc0+slot*200; writes name + play_history_key) and sets the byte at 0x499f20. The only
  clear is `FUN_00195a20` 0x195ae4: when the current slot's session handle[0] is null. So it is
  **once per game session**, redone after leaving and rejoining; not per round.
- Local player count = `*(*(0x33dc800)+0x18)+0x14`; split-screen player 2 uses handle[1] and
  ctx+0x8da8.

#### Q3 @@KeepAlive
- Empty merge write every >= 15 s per local player (timer ctx+0x8f58 is reset only when the
  write was issued). No reader, no listener, no response expected. (V)
- Server role (I): liveness. Refresh the doc/update_time; when a user stops writing (or the
  KeepUserSession stream ends) run that user's deferments. Nothing needs to be written back.

#### Q4 Referee
- Request/Report/Result schemas: 2.1.
- `result` ints are **Pia player indexes in finish order, -1 = empty**, not ranks (V):
  `FUN_021264ec` 0x2126700.. looks for its own player id in entry+0xa4[0..23] and uses the
  *position* as the rank (event hash 0xae42b7bf); `FUN_0212603c`/`FUN_02129830` walk the same
  array. Captured reports are `[1]` then `[1, 0]` from both clients (run-lan6.log 8303-8424).
  Our server answers status 0 with `result = [1..N]`, which the game reads as "player 1 finished
  first, player 2 second" before anyone finished. Send an empty list at status 0 and then the
  agreed order.
- Ring (V, `FUN_00498404`): for every valid doc in the collection each frame: id = uuid parsed
  from the doc name (`FUN_01e39a5c`); entry found by id or allocated (oldest evicted), then
  `request_id` (+0x10, only on allocation), `result` (+0xa4, 24 ints, refreshed every frame),
  `status` (+0xa0, refreshed every frame); if status == 1 and `ratings` present: for each key
  (decimal ussid, `FUN_02264830`) the player node's rating (node+0x8e0, same slot as
  `rating_point_raw_value`) = value. A second 24-int array at +0x104 holds what this client
  already reported (-1 = free).
- Status consumers (V, all readers of entry+0xa0 found): `FUN_02127388` (start waiter: needs
  request_id == own key **and status == 0**, else 36 s timeout -> error 0x606314c9);
  `FUN_00498404` 0x4985a0 (a queued report is written only while status == 0; queue entries for
  other statuses are dropped); status == 1 -> ratings. **No code handles any other value**: a
  first result with status >= 2 just makes the start waiter time out. Later lookups are by uuid
  (`FUN_01e37600`, `FUN_021269d0`) and ignore status.
- Report flow (V): `FUN_021275a8`/`FUN_02129138` append a player index to entry+0x104 if absent
  and queue `{result: [all reported so far]}`; one write in flight at a time (future ctx+0x8f78);
  path uses the result uuid (0x25 chars) and own ussid.
- Server obligations (I): answer the first request per key with a result doc (status 0,
  request_id); keep status 0 until all reports arrived, updating `result` to the agreed order;
  then status 1 with `ratings` for every ussid. A new key arrives for every match, so this
  repeats per round.

#### Q5 __us and friends
- Member callback `FUN_02254e4c` (V): requires parent collection "__us" (`FUN_022550d4`); key =
  murmur3(doc id); **keeps the member only if exists && st == 2**, otherwise erases it
  (`FUN_022544fc`); same hash with a different doc id posts an error (0x18, kind 0xd).
  `FUN_0049a5a0` then drops the player node of an erased member. No other `st` value is
  interpreted anywhere (only readers: `FUN_02255158`, `FUN_0049a5a0` 0x49ae60 which stores it at
  node+0x854). So st != 2 or REMOVED = "player left"; names of the other states are unknown.
- `att`, `ltc`, `tn` are parsed but no consumer was traced.
- `__gs/r`, `__gs/m`, `__pus`, `__stu`, `__stg`, `__mt`: 2.2.

#### Q6 Deferments
- Path (V): `userSessions/current/deferments/<doc path>` (`FUN_012a0f70`: "userSessions" + "/" +
  `*0x30f0cc8` + "/deferments" + "/" + key), carried in `WriteDocumentsRequest.2`, each with one
  `Delete{docs/<doc path>}` write. Example run-lan9.log 605-620.
- When (V): `FUN_0049952c` once per session per local player for DPIS, PlayerStatic, Companion,
  @@KeepAlive (4 separate requests); `FUN_01281500` for `__pgn/<pgn>/__pus/<uid>` inside the
  `__pus` write.
- Server obligation (I, from the name and the REMOVED path in the client): when the user session
  ends (stream closed, or keep-alive expiry) execute the stored writes = delete those docs, and
  notify every listener with `Change{target id, kind 3, Document{1: name}}`. Re-registering the
  same deferment name must be idempotent (the client re-sends it every session).

#### Q7 Other RPCs
- Stub methods (vtable 0x30f2db0.., `FUN_012bae9c` table). Direct call sites exist only for
  IssueToken (0x128dac0), RefreshToken (0x1292820), GetDocument (0x12616e0), WriteDocuments
  (0x1298f20), KeepUserSession (0x1273d20). **ReadDocuments, ListDocuments, QueryCollectionIds,
  Begin/Commit/RollbackTransaction and CreateRound have no direct caller** (V); the used ones
  are all devirtualized, so the rest are most likely dead code (I). All logs agree: only
  IssueToken, GetDocument, WriteDocuments, KeepUserSession were ever sent.
- **CreateRound is not called by the game**; it is not the per-race mechanism.
- RefreshToken will be called when the Gamesync token nears expiry; the server already answers.

---------------------------------------------------------------------------------------------

### 4. Server checklist

| Item | Today | Needed |
|---|---|---|
| Update with FieldMask | replaces doc | merge masked fields; `*` replace; empty = touch |
| Precondition exists=false | ignored | keep first writer (matters for `Global/Session`) |
| `Write.4` delete | ignored | delete + REMOVED to listeners |
| Deferments (`request.2`) | ignored | store per user session; run at session end |
| Change kind 3 | never sent | send with `Document{name}` |
| `__us` member `st` | 2 | keep 2 while present; REMOVED (or st != 2) on leave |
| `@@RefereeResult.result` | `[1..N]` at status 0 | empty, then finish order of player indexes |
| `DPID/d`, `Global/Sequence`, `__gs/r`, `__gs/m` | absent | optional so far (client tolerates absence: every reader null-checks); `__gs/m` must accept the client's `cp` update or the caller posts an error |
| CreateRound / ReadDocuments / transactions | - | not needed |

### 5. Not verified / open
- Number reader accepting kind 5 (double) as well as kind 4 (float).
- What `DPIS`/`DPID` stand for and who fills `DPID/d` (service hash 0x15e55814 not resolved).
- Consumers of `global_time_offset`, `rule`, `cp/ip/ebf`, `dpc` in the ctx.
- Wire shape of the `__gs/m` update (`FUN_01298300`), never captured.
- Meaning of `st` values other than 2, and of `att`/`ltc`/`tn`.
- That the replace-vs-merge bug is the cause of the empty `users`/`ussid_map` (mechanism read
  in code, not tested live).

---

## 3. Matchmaking / session lifecycle and friend rooms


Static analysis of `scratch/main.bin` (image offsets). **V** = VERIFIED (read in decompiled code/data at the
address given), **I** = INFERRED (reasoning from verified facts). Work files: `re-map\a3-*.c` (decompiles),
`a3-ser.txt`, `a3_up.py` (caller walker), `a3_view.py` (compact decompile viewer).

Contents: 0 corrections to existing contracts, 1 ticket state / Track semantics, 2 RPC table and message shapes,
3 friend-room flow, 4 leave and between-match behaviour, 5 server-pushed state, 6 what the server must implement,
7 open points.

---------------------------------------------------------------------------------------------------

### 0. Corrections to what the repo currently assumes

| # | Finding | Mark |
|---|---|---|
| C1 | **`Member` fields 1 and 2 are swapped in `matchmaking.proto` and in `server/mkwmp_server.py`.** Serializer `0x1405fec`: field 1 = object `+0x10`, field 2 = `+0x18`. Member converter `FUN_013e94c8` parses `+0x10` with the **userSession-name** parser `0x13ea200` (-> `FUN_013e9de0`, strings `tenants`/`gameSessions`/`userSessions`) and `+0x18` with the **user** parser `0xf183d0`. So `Member{1: "tenants/<t>/gameSessions/<g>/userSessions/<u>", 2: "tenants/<t>/users/<u>"}`. The server sends them the other way round today (both parses fail quietly; the game's member tree comes from Gamesync so nothing visibly breaks). | V |
| C2 | `MatchmakingTicket` field 3 (`0x140a0d8` `{1 str, 2 str, 3 varint}`) is the **failure cause**: the consumer reads `+0x28` (field 2 = message) and `+0x30` (field 3 = code) only when state == 5 (`0x138fea0`, case 5). | V |
| C3 | `GameSession` field 6 is the **password**: the create builder `0x136dbc8` writes `CreateParams+0x28` into `GameSession+0x30` (= field 6, serializer `0x140b440` tag 0x32); the game's setter `FUN_02230870` stores the user-entered string there. On the way back the converter (`0x13dc320`, inside the blob Ghidra shows as `FUN_013dc0c0`) only tests `field6 == "*"` -> bool `info+0x62` = "has password". | V (write path, "*" test) / I (that the bool means "needs password") |
| C4 | `TrackMatchmakingTicketRequest` is `{1 name, 2 repeated string, 3 repeated string, 4 repeated string}` (serializer `0x13ffcc0`); the client fills 1 and 2 = `["tenants/current/users/current"]` (`FUN_013de500`). Wire-compatible with the repo proto. | V |
| C5 | The Matchmaker service also has `CancelMatchmakingTicket` and `CreateAcceptance`; `GameSessionService` has 15 methods but the client has **no call site** for `ListUserSessions` (slot 0xd0) and `GetUserSession` (slot 0xe8), and no request/response classes for them. | V (stub wrappers enumerated in `0x1418000-0x141e000`; no external caller) |

---------------------------------------------------------------------------------------------------

### 1. MatchmakingTicket.state and TrackMatchmakingTicket semantics

Track consumer `FUN_0138fea0(result*, ctx, request, users, acceptanceHandler, cancelToken)` (decompile:
`scratch/track-consumer.c`).

#### 1.1 Per-message behaviour (read loop, lines 363-979)

- Every received ticket message **replaces** the kept "latest ticket" (`0x1409970/0x14099b4`). Only the last
  message's state matters after the stream ends. **V**
- On every message the expiry timer is re-armed to `now + *(*0x33013a0) s` (`func_0x00f003e0(timer, now+dur)`).
  `*0x33013a0 -> 0x3326b20 = 150` (matchmaking), `*0x33013a8 -> 0x3326b28 = 120` (session creation). **V** (values,
  call) / **I** (so a server that keeps sending ticket updates, e.g. state 1, keeps the search alive beyond 150 s;
  silence for 150 s ends it).
- **state == 7** inside the loop = "acceptance required": the client builds the user list and
  - with no acceptance handler (`param_5 == 0`): immediately calls `FUN_013d6e80(ctx, 1)` -> `Matchmaker/CreateAcceptance`
    with accept = true;
  - with a handler: hands the pending match to it once (flag `handler+0x50`), the game answers later. **V**

#### 1.2 After the stream finishes (`Finish()`), status OK -> `switch(last.state)` at `0x139183c`

| state | Client action | Result code (gRPC-style, arg 3 of `FUN_0137e024`) | Mark |
|---|---|---|---|
| 3 | convert field 7 GameSession + field 6 MatchedPlayers (`0x13dbf00`), **success** | 0 | V |
| 4 | "The request has been timed out. ticketId: <id>" (`FUN_0138343c`, result `0xb0141`) | 4 DEADLINE_EXCEEDED | V |
| 5 | "The request has failed. cause message: <field3.2> ... ticketId" | **field3.3** (server-chosen code) | V |
| 6 | "The request has been cancelled. ticketId: <id>" | 1 CANCELLED | V |
| 8 | "The match has been declined. ticketId: <id>" | 10 ABORTED | V |
| 7 | (handled in loop, see above); if it is the *last* state -> default | 2 | V |
| 0,1,2, other | "Unknown error. ticketId: <id>" | 2 UNKNOWN | V |

Naming of 1/2 (presumably QUEUED / SEARCHING) is **I**; the client has no code for them, they are only "not final".
Displayed error = `2321-(code-1)*384` (docs/NETCODE.md section 1): declined 2321-3456, unknown 2321-0384,
timeout 2321-1152.

#### 1.3 Stream ends with a non-OK status

- Retry policy first (`0x1213bc0`, `0xf1f948`, `0xf1f9d8`; backoff 1000..30000 ms from `0x292d2a8`): for
  retryable statuses the whole `TrackMatchmakingTicket` call is re-issued (outer `do { } while (bVar13)`). **V**
- status 1 (CANCELLED - this is what the local 150 s timer produces by cancelling the call) -> reported as
  code 14 UNAVAILABLE "Deadline exceeded while matchmaking. See MatchmakingClient::GetMatchmakingExpireDuration()"
  (2321-4992). No Cancel RPC is issued on this branch. **V** (branch `LAB_013915a0`) / **I** (no cancel = absence of a call)
- any other status -> passed through (`FUN_013832fc`). **V**

So: **stream end + OK + no state-3 message = "Unknown error" (2321-0384)**; the server must always finish a
ticket with a final-state message (3, 4, 5, 6 or 8) before closing with OK.

#### 1.4 CancelMatchmakingTicket

- At entry the consumer registers a callback object (vtable `0x30f9c00`, body `FUN_01392a60`) on the caller's
  cancellation token (`func_0x00ef31b0`). When the game cancels the operation (player backs out of the search),
  the callback sends `Matchmaker/CancelMatchmakingTicket{1: name}` where name is the ticket resource
  `tenants/current/matchmakingTickets/<id>` captured from the Track request (`FUN_0138359c`). **V**
- The response body is not inspected (`0x13836cc` wrapper only waits). An empty message is fine. **I**
- After Cancel the client still waits for the Track stream to end; the clean answer is a last ticket message
  with **state 6** and then OK trailers (-> "cancelled", code 1, which the game treats as a user cancel), or
  simply ending the stream with status CANCELLED... the first is safer because status 1 is mapped to the
  2321-4992 "deadline" text. **I**

#### 1.5 CreateAcceptance

`FUN_013d7380` builds `CreateAcceptanceRequest{1 string (ticket name), 2 Acceptance}` (serializer `0x1401240`),
`Acceptance{1 string, 2 repeated string, 3 bool accept, 4 repeated string}` (serializer `0x140f3c0`). Only sent
in reaction to state 7. A server that never sends state 7 never sees it. **V** (shapes, trigger)

---------------------------------------------------------------------------------------------------

### 2. RPC table: who issues what

Stub ctor `FUN_014180ac`; async wrappers were matched to stub slots by their `add x1, x0, #slot`.

| RPC (slot) | stub wrapper | SDK request builder / issuer | SDK job (vtable slot ptr) / public API | Game caller | Trigger |
|---|---|---|---|---|---|
| Matchmaker.CreateMatchmakingTicket (0x28) | `0x14535a4` | `0x137ec40` <- `0x137e940` | job `FUN_0138bc20` (`0x30f9bb0`); API `FUN_01376ae0` <- `0x1377514` | `FUN_022391a0` | dispatcher op hash `0x05bc1741`, reservation string empty |
| Matchmaker.TrackMatchmakingTicket (0x40) | `0x1453808` | `0x13871e0` <- `FUN_0138fea0` | jobs `0x138bc20` and `FUN_01394ca0` (`0x30f9db0`, track an existing ticket; API `FUN_013776e0` <- `0x1377684`) | `FUN_022391a0`, `FUN_02237d98` | `0x05bc1741`; the second when `P+0x470` is non-empty |
| Matchmaker.CancelMatchmakingTicket (0x58) | `0x1453e64` | `0x13839c0` <- `0x13836cc` | callback `FUN_01392a60` (`0x30f9c40`) | cancel token of the automatch op | player cancels the search |
| Matchmaker.CreateAcceptance (0x70) | `0x14544c4` | `0x13d7c60` <- `0x13d7968` <- `FUN_013d7380` <- `FUN_013d6e80` | inside `FUN_0138fea0` | - | ticket state 7 |
| GSS.CreateGameSessionCreationTicket (0x28) | `0x14188b4` | `0x1396e80` <- `0x1396c94` <- `0x1396b94` | job `FUN_013a2d60` (`0x30fa9f8`); API `FUN_013784c0` <- `0x1378ef4` | `FUN_02239878` | op hash `0x1fc20fef` (CreateSessionSelectSeq node posts `0x1fc20fef` at `0x1a3e7bc`) |
| GSS.TrackGameSessionCreationTicket (0x40) | `0x1418b18` | `0x139b5e0` <- `FUN_01395860` | jobs `0x13a2d60`, `FUN_013c9500` (`0x30fd7e8`, track an existing creation ticket; API `FUN_013790c0`) | `FUN_02239878`, `FUN_022384d0` | after Create; second when `P+0x470` non-empty |
| GSS.CancelGameSessionCreationTicket (0x58) | `0x1419174` | `0x139eec0` <- `0x139ebc4` <- `FUN_0139b08c` | from `FUN_01395860` (deadline branch, and cancel callbacks `0x30fa1e8`/`0x30fa240`) | - | 120 s creation deadline or user cancel |
| GSS.GetGameSession (0x70) | `0x14197d4` | `0x13a9840` <- `0x13a954c`; request `FUN_013d4c80` | API `FUN_0137b63c` | task `FUN_02235a80` (game op 0x10) | CheckShortIdSession node (after the alias lookup) |
| GSS.BatchGetGameSessions (0x88) | `0x1419e34` | `0x13ae4c0` <- `0x13ae1e4`; request `FUN_013d4e60` | API `FUN_0137b9f8` | `FUN_02227ad0` (friend-list manager, vtables `0x31dab78/0x325b688/0x32a8120`) | every >= 10 s while friends advertise a `SessionId` |
| GSS.QueryGameSessions (0xa0) | `0x141a494` | `0x13a4960` <- `0x13a467c`; request `FUN_013dd3a0` | API `FUN_0137aeac` | `FUN_02237a6c` | op hash `0x7cd8168e` |
| GSS.JoinGameSession (0xb8) | `0x141aaf4` | `0x13b3620` <- `0x13b3328` <- `FUN_013b2da0` | job `FUN_013b7d20` (`0x30fc050`); API `FUN_0137bdc0` <- `0x137c7d4` | `FUN_02239f60` | op hash `0x9aca788e`, reservation string empty |
| GSS.ListUserSessions (0xd0), GetUserSession (0xe8) | present in stub | none | none | none | **unused** |
| GSS.IssueMatchmakingIdToken (0x100) | `0x141be14` | `0x13ca0a0` <- `0x13c9db0`; request `FUN_0136ce98` | job `FUN_013ce3c0` (`0x30fdcd0`); API `FUN_0137a080` | `FUN_02238c08` | op hash `0x9aca788e` when `P+0x470` is non-empty (join by reservation) |
| GSS.IssueUserDelegationToken (0x118) | `0x141c474` | `0x13b8d00` <- `0x13b8a00`; request `FUN_013d51e0` | API `FUN_0137c944`; also `FUN_0138d5e0` inside every job's retry loop | task `FUN_0223569c` (game op 0xf) | game op 0xf (from `0x261f2ec`); SDK: when a job needs tokens for extra local users |
| GSS.CreateGameSessionShortAlias (0x130) | `0x141cad4` | `0x13bdb40` <- `0x13bd84c` | API `FUN_0137cd00` | `FUN_01e37c54` <- `FUN_01a436ac` | **GetShortSessionId** sequence node, state 1 |
| GSS.GetGameSessionShortAlias (0x148) | `0x141d134` | `0x13c2aa0` <- `0x13c27a8` | API `FUN_0137d3dc` | `FUN_02235a80` (op 0x10) and `FUN_02239f60` (join) | CheckShortIdSession; again inside Join when `P+0x880` is set |
| GSS.AllocateIceServerSet (0x160) | `0x141d794` | `0x13d0b80` <- `0x13d08a0` <- `FUN_013cf270` | `FUN_013c7658` (`0x30fd288`), started by every job via `0x13cf148` | - | after a session is obtained, before Gamesync |
| GSS.ListLatencyMeasurementServers (0x178) | `0x141ddf4` | `0x1371e20` <- `0x1371b28` | `FUN_01370f40` (`0x30f8ad0`); `0x136f72c` called by jobs `0x138bc20`, `0x13a2d60`, `0x13b2da0` | - | before automatch / create / join |

Game dispatcher (`FUN_022379b0`, vtable slot `0x325c670`; ops polled from `FUN_02237828`): **V**

```
op hash 0x7cd8168e                       -> FUN_02237a6c  QueryGameSessions
P = *(mgr+0x38); if *(P+0x470) string is NON-empty ("reservation"/existing ticket id):
   0x9aca788e -> FUN_02238c08 (IssueMatchmakingIdToken job)
   0x1fc20fef -> FUN_022384d0 (track existing creation ticket)
   0x05bc1741 -> FUN_02237d98 (track existing matchmaking ticket)
else:
   0x9aca788e -> FUN_02239f60 (GetShortAlias? + JoinGameSession)
   0x1fc20fef -> FUN_02239878 (CreateGameSessionCreationTicket + Track)
   0x05bc1741 -> FUN_022391a0 (CreateMatchmakingTicket + Track)
```

All six session pollers finish the same way: the SDK job returns user-session handles, which
`FUN_0223b804` copies into the current slot (`mgr+0x12c0 + cur*0x148`, `+8`), and the returned session id
string is copied to `mgr+0x12d0`. **V**

Every SDK job has the same tail (call lists of `0x13a2d60`, `0x13c9500`, `0x13ce3c0`, `0x1394ca0`,
`0x13b7d20`): `0x138dea0` (each local user must be in the MatchedPlayer list) -> `0x13cf148`
(AllocateIceServerSet) -> `FUN_0138e280` (Gamesync connect + UserSession; on failure it calls
`FUN_01268304` to tear the P2P/user session down). **V** So **create, join, id-token and automatch all need the
same thing from the server: a `GameSession` plus one `MatchedPlayer` per local user.**

#### 2.1 Resource names (builders `0x13eb220..0x13ebae0`, tenant literal `current` at `*0x30b07e0`) **V**

```
tenants/current                                              parent              (0xebec90)
tenants/current/users/current                                "me"                (0xebed40)
tenants/current/matchmakingConfigs/<cfg>                     0x13eb3e0
tenants/current/matchmakingTickets/<id>                      0x13eb920  (parser 0x13ea960)
tenants/current/gameSessionCreationTickets/<id>              0x13eb5a0  (parser 0x13e99c0)
tenants/current/gameSessions/<id>                            0x13ebae0  (parser 0x13ead80)
tenants/<t>/gameSessions/<g>/userSessions/<u>                parser 0x13e9de0
tenants/current/GameSessionShortAliases/<id>                 0x13eb760  (parser 0x13ea460)  (capital G, plural)
tenants/current/gameSessionSearchConfigs/<name>              0x13eb220
```

Config names chosen by the game (`FUN_01e3f61c`): `Normal24`, `Battle24`, `Survival24`, `World96`,
`Attraction6`, `Streamer`, **`Private24`** (whenever the lobby kind `*(this+8)` / `*(this+0x4a8)` is non-zero =
private/friend room), and `SearchAll` when the flag argument is set. **V**

#### 2.2 Message shapes (from `_InternalSerialize`; object offsets in brackets) **V** unless noted

Service file 1 `game_session_service.proto` (vtables `0x30ff328..0x30ffc28`):

```
CreateGameSessionCreationTicketRequest (ser 0x13ed2c0)
  1 string parent = "tenants/current"                         [+0x28]
  2 GameSessionCreationTicket ticket                          [+0x38]
  3 repeated string  (strings supplied by the game: FUN_0223b418 list)   [+0x10]
  4 bool             (CreateParams+0xb0 = P+0x1a0)            [+0x40]
  5 string           (CreateParams+0xb8 = P+0x1a8)            [+0x30]
TrackGameSessionCreationTicketRequest (ser 0x13ee540)   same layout as TrackMatchmakingTicketRequest:
  1 string name; 3 repeated string = ["tenants/current/users/current"]  (FUN_013ddf08)
CancelGameSessionCreationTicketRequest (ser 0x13ef1b8)  1 string name
GetGameSessionRequest (ser 0x13ef970)        1 string name "tenants/current/gameSessions/<id>"; 2 enum view (1..3)
BatchGetGameSessionsRequest (ser 0x13f04a0)  1 string parent; 2 repeated string names; 3 enum view
BatchGetGameSessionsResponse (ser 0x13f0ea0) 1 repeated GameSession
QueryGameSessionsRequest (ser 0x13f2020)
  1 string parent [+0x28]; 2 enum [+0x48]; 3 string "tenants/current/gameSessionSearchConfigs/<name>" [+0x30];
  4 int32 [+0x4c]; 5 int32 [+0x50] (never set); 6 Attributes [+0x40]; 7 repeated string users [+0x10];
  8 int32 page size [+0x54]; 9 string page token [+0x38]
QueryGameSessionsResponse (ser 0x13f32c0)    1 repeated GameSession; 2 string next page token
JoinGameSessionRequest (ser 0x13f47e0)
  1 string name "tenants/current/gameSessions/<id>"           [+0x58]
  2 string password (raw JoinParams+0x20 = P+0x278)           [+0x60]   (password meaning: I)
  3 repeated Player (own from P+0x298, then extra local users) [+0x10]
  4 repeated string (game-supplied list)                      [+0x28]
  5 repeated string = ["tenants/current/users/current"]       [+0x40]
JoinGameSessionResponse (ser 0x13f5960)      1 repeated MatchedPlayer; 2 GameSession
IssueMatchmakingIdTokenRequest (ser 0x13f6b80)
  1 string name "tenants/current/gameSessions/<reservation>"  [+0x58]
  2 repeated string = [users/current]; 3 repeated string (unused); 4 repeated string = [users/current]
IssueMatchmakingIdTokenResponse (ser 0x13f79c0)  1 repeated MatchedPlayer; 2 GameSession   (same as Join response)
IssueUserDelegationTokenRequest (ser 0x13f89c0)
  1 string parent; 2 string user resource; 3 string user resource; 4 Attributes; 5 packed repeated enum (1..3)
IssueUserDelegationTokenResponse (ser 0x13f973c) 1 UserDelegationToken (ser 0x1404b80:
  1 string, 2 string, 3 Attributes, 4 packed repeated enum, 5 string, 6 Duration)
CreateGameSessionShortAliasRequest (ser 0x13fa1c0) 1 string parent = "tenants/current"; 2 GameSessionShortAlias
GetGameSessionShortAliasRequest (ser 0x13faad8)    1 string name "tenants/current/GameSessionShortAliases/<id>"
AllocateIceServerSetRequest (ser 0x13fb380)        1 string, 2 string        (response: IceServerSet, see NETCODE.md)
ListLatencyMeasurementServersRequest (ser 0x13fbd94) 1 string, 2 varint, 3 string
ListLatencyMeasurementServersResponse (ser 0x13fc8c0) 1 repeated server (ser 0x1412f7c: 1 str,2 str,3 str,4 varint,5 varint); 2 string
```

Service file 2 `matchmaker.proto` (vtables `0x30ffca8..0x30ffe28`):

```
CreateMatchmakingTicketRequest (ser 0x13febe0)  1 parent; 2 MatchmakingTicket; 3 repeated string (game-supplied)
TrackMatchmakingTicketRequest  (ser 0x13ffcc0)  1 name; 2 repeated string = [users/current]; 3,4 repeated string
CancelMatchmakingTicketRequest (ser 0x1400938)  1 name
CreateAcceptanceRequest        (ser 0x1401240)  1 string; 2 Acceptance
```

Types file (vtables `0x30ffea8..0x3100638`):

```
GameSessionCreationTicket (ser 0x14032e0)
  1 string name [+0x40]; 2 string config "tenants/current/matchmakingConfigs/<cfg>" [+0x48];
  3 repeated Player [+0x10]; 4 enum state [+0x58]; 5 repeated MatchedPlayer [+0x28]; 6 GameSession [+0x50]
GameSessionShortAlias (ser 0x1407230)   1 string name; 2 string game_session resource; 3 Timestamp (expiry)
MatchmakingTicket (ser 0x1408a40)       1 name; 2 config; 3 Cause{1 str, 2 message, 3 code}; 4 repeated Player;
                                        5 state; 6 repeated MatchedPlayer; 7 GameSession  (+ 8 msg, 9 varint seen
                                        by the heuristic, not consumed by the client)
GameSession (ser 0x140b440)             see 3.4
Member (ser 0x1405fec)                  1 string userSession NAME [+0x10]; 2 string USER [+0x18];
                                        3 properties (ser 0x140e2a0) [+0x28]; 4 Timestamp [+0x30]; 5 enum (1,2) [+0x40];
                                        6 string [+0x20]; 7 Attributes [+0x38]
Acceptance (ser 0x140f3c0)              1 string; 2 repeated string; 3 bool; 4 repeated string
```

#### 2.3 GameSessionCreationTicket.state (consumer `FUN_01395860`, switch at line 1568 of `a3-b3.c`) **V**

| state | meaning | result |
|---|---|---|
| 2 | **created**: field 6 GameSession converted (`0x13dbf00`), field 5 MatchedPlayers | success |
| 3 | "The request has been cancelled. ticketId:" | 1 |
| 4 | "The request has been timed out. ticketId:" | 4 |
| 5 | "The request has failed. ticketId:" | 13 INTERNAL |
| other / none | "Unknown error. ticketId:" | 2 |
| gRPC status 1 | client sends `CancelGameSessionCreationTicket`, reports "Deadline exceeded while creating a game session." | 4 |

Note the numbering differs from MatchmakingTicket (done = **2** here, **3** there). Deadline timer = 120 s,
re-armed per message, same mechanics as 1.1.

---------------------------------------------------------------------------------------------------

### 3. Friend rooms

#### 3.1 Game-side parameter block `P` (`P = *(mgr+0x38)`, getter `FUN_026204c0`; `*P` = backend kind: 1 = NPLN, 2/3 = Pia LAN)

| Field | Content | Written by | Used by |
|---|---|---|---|
| `P+0xf0..` | automatch params (config, own player) | `FUN_02230a74`, `FUN_02230b84` | `FUN_022391a0` |
| `P+0x110` attrs, `+0x118` string **password**, `+0x138` own Player, `+0x1a0` bool, `+0x1a8` string | **create params** | `FUN_01e4027c` (attrs `rom_id`, `course_id`, `user_name`), `FUN_02230870` (password) | `FUN_02239878` |
| `P+0x1d8..0x250` | query params (config string, attrs `rom_id`, users, page size) | `FUN_01e402ec` | `FUN_02237a6c` |
| `P+0x258` session id, `+0x278` password, `+0x298` own Player, `+0x300..` | **join params** | `FUN_02620468`, `FUN_02231694` | `FUN_02239f60` |
| `P+0x430` | passed to automatch/create/join (acceptance/progress object) | - | all three |
| `P+0x470` | "reservation" string; non-empty switches to the track-existing/id-token variants | - | dispatcher |
| `P+0x880` | **room id (short alias) typed by the player** | `FUN_026201dc` (copies `this[0x7b]`) | `FUN_02239f60` |

All **V** (offsets read in `FUN_02239878`, `FUN_02239f60`, `FUN_01e3fa98`, `FUN_026201dc`).

Automatch/create attributes written by the game (`FUN_01e3fa98`): per player `rom_id` (string = config
name), `batching_key` (config name + `_world%u` / `_attr%d`), `course_id` (int); session (create) attributes
`rom_id`, `course_id`, `user_name`. **V**

#### 3.2 Create Room: exact sequence

1. `GameSessionService/ListLatencyMeasurementServers` (job prelude `0x136f72c`). **V** (call exists; whether it is
   skipped when cached is not checked)
2. `CreateGameSessionCreationTicket` request (`FUN_0136d600`): **V**
   ```
   1: "tenants/current"
   2: GameSessionCreationTicket {
        2: "tenants/current/matchmakingConfigs/Private24"
        3: [ Player{own, attrs rom_id/batching_key/course_id} , ...extra local players ]
        6: GameSession { 6: "<password or empty>", 11: Attributes{rom_id, course_id, user_name} } }
   3: [...]  4: bool  5: string
   ```
   Reply type = `GameSessionCreationTicket`; the client needs field 1
   `tenants/<t>/gameSessionCreationTickets/<id>` (it re-parses the id with `0x13e9960` for messages and the
   Track request). **V** (request) / **I** (reply needs only the name, by symmetry with CreateMatchmakingTicket)
3. `TrackGameSessionCreationTicket{1: name, 3: [users/current]}` (server stream). Final message must be
   **state 2** with field 6 `GameSession` (name, 8/9 Gamesync endpoint, ...) and field 5 one `MatchedPlayer`
   per local user `{1 Player, 2 userSession resource, 3 user-session id}`; then OK trailers. **V**
4. `AllocateIceServerSet`, then Gamesync (IssueToken, KeepUserSession, `__us/<id>` ...) exactly as after an
   automatch (same tail, section 2). **V**
5. Room id: sequence node **GetShortSessionId** (`FUN_01a436ac`): state 1, if the per-match context
   (`ctx = FUN_0045b24c(-1)`) has an empty short-id string at `ctx+0x48`, `FUN_01e37c54` calls
   `CreateGameSessionShortAlias{1: "tenants/current", 2: {2: "tenants/current/gameSessions/<session id>"}}`
   with the id taken from the session handle (`handle+0x38`), stores the future at `ctx+0x8f68`; state 2 waits
   until `ctx+0x48` is non-empty. **V**
   Reply = `GameSessionShortAlias`; converter `FUN_013dca4c` parses field 1 with the
   `GameSessionShortAliases` parser (-> the id shown to players), field 2 with the `gameSessions` parser, field 3
   as a Timestamp. **V** So **the server invents the room id** and returns
   `{1: "tenants/<t>/GameSessionShortAliases/<ROOMID>", 2: "tenants/<t>/gameSessions/<g>", 3: expiry}`.
   The id is a string for the client; "6 digits" is a server policy (**I**; the input UI limit was not read).
   The code that moves the finished future's id into `ctx+0x48` was not located (see 7).

#### 3.3 Join by Room ID: exact sequence

1. **CheckShortIdSession** node (`FUN_01a3a454`): `FUN_0261f30c` copies the typed id to `lobby+0x5a8` and starts
   game op 0x10 = task `FUN_02235a80`: **V**
   1. `GetGameSessionShortAlias{1: "tenants/current/GameSessionShortAliases/<typed id>"}`.
      Error -> error kind 0x22 posted (`FUN_0222a04c`), node shows the failure dialog.
   2. `GetGameSession{1: "tenants/current/gameSessions/<id from alias field 2>", 2: view}`; the converted session
      info is stored (`FUN_022274f4` -> `lobby+0x6c8`, exposed by `FUN_0261f548`).
   Node then waits up to **120 s** (`0x1d4c1` ms) for the info; if the info object's virtual `+0x40` returns
   true the flow goes to the **InputPassword** UI (`NeedPassword` property), otherwise straight on. **V**
   (which bool the virtual returns: **I** = `info+0x62`, the `"*"` flag).
2. Join op (hash `0x9aca788e`, `FUN_02239f60`): if `P+0x880` is non-empty it calls
   `GetGameSessionShortAlias` **again** and overwrites the join target with the resolved session id (failure ->
   error kind 0x23); then `JoinGameSession{1: "tenants/current/gameSessions/<id>", 2: "<password>", 3: [Player],
   4: [...], 5: ["tenants/current/users/current"]}`. **V**
3. Reply `JoinGameSessionResponse{1: repeated MatchedPlayer, 2: GameSession}`; `0x138dea0` requires every local
   user to appear in field 1 with a parseable userSession name (field 2) - otherwise FAILED_PRECONDITION "The
   game session was found but the user's connection info not found" (2321-3072). Then AllocateIceServerSet and
   Gamesync as usual. **V**
   A wrong password should be answered with a gRPC error status (the client has no in-band "wrong password"
   field); which status the game maps to its "wrong password" dialog was not traced. **I**

#### 3.4 GameSession as consumed by the client (serializer `0x140b440`, converter `0x13dc320`) **V**

| Field | Type | msg offset | Converted info | Meaning |
|---|---|---|---|---|
| 1 | string | +0x28 | `info+0x00` id (via `0x13eb070`) | resource name; must parse as `tenants/x/gameSessions/<id>` |
| 2 | int32 | +0x50 | `info+0x28` (int64) | unknown number (I: current / min player count) |
| 3 | int32 | +0x54 | `info+0x30` (int64) | unknown number (I: max player count) |
| 4 | bool | +0x58 | `info+0x38` | unknown flag (I: joinable/open) |
| 5 | bool | +0x59 | `info+0x39` | unknown flag (I: searchable/visible) |
| 6 | string | +0x30 | `info+0x62` = (value == "*") | password; server masks it as `*` |
| 7 | varint | +0x5c | **not copied** | ignored by the client |
| 8 | string | +0x38 | `info+0x40` | Gamesync host (verified live) |
| 9 | varint->u16 | +0x60 | `info+0x60` | Gamesync port |
| 10 | Timestamp | +0x40 | `info+0x68` | time (I: create/expire time) |
| 11 | Attributes | +0x48 | `info+0x20` | session attributes (`rom_id`, `course_id`, `user_name` as written by the creator) |
| 12 | repeated Member | +0x10 | `info+0x70` vector of 0x90-byte entries | members: user id, user-session id, state enum (field 5: 1/2), string field 6, attributes |

The info struct is copied verbatim into game objects by `FUN_022274f4` (short-id check, query results
`FUN_0223f4c4`, friend-list results). Which UI reads 2/3/4/5 (ShowSessionInfo / SyncSessionInfo nodes) was not
traced; the names above for 2-5 are guesses. Sensible server values: 2 = current members, 3 = 24,
4 = true, 5 = true.

#### 3.5 How friends' rooms are discovered **V** (mechanism) / **I** (who publishes)

- Friend-list manager `FUN_02227ad0`: for every friend entry with presence (`+0x2d0` non-empty, `+0x2f0 == 1`)
  it looks up the key **`SessionId`** in the friend's presence key/value map (`+0x2f8`, lookup `0xf088ec`),
  collects the distinct ids and calls **`BatchGetGameSessions`** (at most every 10 s). The results fill the
  friend room list.
- Joining a friend directly (`FUN_026201dc`, lobby kind 2, no typed id): the join target is the value of
  `SessionId` from the selected friend's map (`this+0x258`), no alias lookup.
- The presence data itself comes from NPLN `nn.npln.friends.v1.PresenceService` (`SubscribePresences`,
  `KeepAlive`; stub `0x1252140`), outside this area. The game must publish its own `SessionId` there after
  creating/joining a room (**I**). `QueryGameSessions` is a separate path (op `0x7cd8168e`, search config
  `SearchAll`), not used for the friend list.

---------------------------------------------------------------------------------------------------

### 4. Leaving, and what happens between matches

#### 4.1 Leave (game session manager vtable `0x325c658..`) **V**

| Step | Function | What it does |
|---|---|---|
| stop P2P | `FUN_0223a9e8` (vtable `0x325c6a8`, virtual `+0xf0`) | clears `slot+0x128`, calls Gamesync `FUN_01268304(handle)` -> `FUN_0126ad70` (P2P teardown; Pia `LeaveSessionJob` etc.), waits for the future |
| leave session | `FUN_0223a848` (vtable `0x325c698`) | if `slot+0x128` -> stop P2P first; then for each user-session handle in the slot: `FUN_01267ebc(handle)` = **UserSession::Leave**, wait, drop the handle (slot handle = 0); finally `FUN_0222fe8c` |
| watchdog | `FUN_0223b1a0` (vtable `0x325c6d0`) | polls each handle's "closed" future (`FUN_01267e48`); if it completed with an error -> error kind 0x10 (disconnected from session) |

- `FUN_01267ebc` -> `FUN_0126ff88`: sets `session+0x1a8 = 1`, signals the session thread's condition variable
  and returns the "finished" future (`+0x140`). **No matchmaking RPC is involved**; `GameSessionService` has no
  Leave/Update method at all (15 methods, section 2). **V** Leaving therefore = the client ends its
  `Gamesync/KeepUserSession` stream (**I** for "half-closes the stream"; the stream thread itself is in the
  Gamesync area).
- The UserSession destructor (`FUN_01269548`) also calls `FUN_01267ebc` if `+0xa8` is not set, so dropping the
  handle after an error leaves too. **V**
- **Deferments** (`FUN_0125d8e0` <- `FUN_01298420` <- doc writers `0x125cde0` (game, via `FUN_0049952c`) and
  `0x1281500` (`__pus` write)): a WriteDocuments entry whose resource name is
  `userSessions/current/deferments/<name>` (`FUN_012a0f70`, literal `current` at `*0x30f0cc8`) wrapping a list
  of ordinary writes. **V** (name, structure). Meaning: writes the server must apply **when that user's session
  ends** (disconnect or leave), e.g. removing the player's `__pus` / game documents and notifying the other
  listeners. **I** The server has to store them per user session and execute them at stream end.
- SDK-side failure cleanup: every job calls `FUN_0138e280`, which on error runs the same `FUN_01268304`. **V**

#### 4.2 Matchmaking-level cancel paths **V**

| Situation | Client RPC |
|---|---|
| player backs out while searching | `CancelMatchmakingTicket{name}` (callback `FUN_01392a60`), then waits for Track to end |
| search silent for 150 s | call cancelled locally, **no Cancel RPC**, error 2321-4992 |
| creating a room, 120 s deadline | `CancelGameSessionCreationTicket{name}` (`FUN_0139b08c`), DEADLINE_EXCEEDED |
| creating a room, user cancel | `CancelGameSessionCreationTicket` via callbacks `0x30fa1e8/0x30fa240` |

#### 4.3 Between matches

- No code re-reads or updates the GameSession through the matchmaking API after a match: `GetGameSession` is
  only called from the short-id check (`FUN_02235a80`), `BatchGetGameSessions` only from the friend list,
  `QueryGameSessions` only from op `0x7cd8168e`; there is no polling loop and no update RPC. **V** (complete
  caller enumeration of the three APIs)
- The session handles stay in the slot until `FUN_0223a848` runs; nothing in the pollers releases them on race
  end. **V** So the matchmaking-level GameSession is created once and **kept across races**; per-race
  state (course vote, start, results) lives in Gamesync documents and Pia, not in matchmaking. **I**
- The `UpdateSession` sequence node (`FUN_01a88618`) does not talk to the server: it writes a pending
  "switch lobby" request into the lobby manager (`+0x4a8` = lobby type from the node parameter, `+0x4ac` = 4,
  `+0x4b4` = 1), the same record `FUN_0261ef40` writes for DisconnectConfirm / UpdateLobbyStatus. **V** The
  request is executed later by the lobby manager as leave + new session operation of the requested type (**I**;
  the executor was not traced). I.e. a *lobby type change* (e.g. matchmade race -> something else) re-matchmakes;
  staying in the same lobby does not.
- `SyncSessionInfo` (`FUN_01a829f8`) only reads the local lobby kind/mode; `ShowSessionInfo` not traced.

Game operation ids (`FUN_0261fc30`, `this+0x46c` = running op): 3/4/5 and 6/7/8 post controller ops
`0xbc4048ee` / `0xded6a14c` / `0xf06239fb` (the 3-5 variants first set `slot+0x10 = 1`, which makes the pollers
attach the P2P plugin via `0x136e8c4`/`0x136d568`), 1 `0xe83e5770`, 2 `0x1c0b7395`, 9 `0x26b8deb7`,
10 `0x30329dbe`, 11 `0x1965ad8d`, 12 `0xc6282348`, 13 `0xe06f0457`, 14 `0xadce7517`, 15 = task `FUN_0223569c`
(IssueUserDelegationToken), 16 = task `FUN_02235a80` (short-id check). **V** (table) - the mapping of the first
group onto matchmake/create/join is **I** (order matches the dispatcher's three variants).

---------------------------------------------------------------------------------------------------

### 5. Server-pushed state the client depends on while in a lobby

| Source | What | Mark |
|---|---|---|
| TrackMatchmakingTicket / TrackGameSessionCreationTicket | only until the final state; the stream is finished and dropped afterwards. Nothing is pushed over matchmaking once in the lobby. | V |
| GameSession (fields 2-7, 10-12) | a snapshot taken at join/create/alias-check/friend-list time; never refreshed for the session the player is in. Field 6 `"*"` only matters before joining (password prompt). Field 12 members are converted but the in-game member tree comes from Gamesync `__us`. | V / I |
| Gamesync `KeepUserSession` | all live lobby state: member list (`docs/__us` collection), `__pgn/.../__pus`, `__stu` signaling, `Global`, `PlayerStatic`, `Companion`, `@@RefereeResult`. A dying stream = error kind 0x10 via `FUN_0223b1a0`. | V (watchdog) / other areas |
| Friends PresenceService | friends' `SessionId` (room discovery), pushed by `SubscribePresences`. | V (consumer) |

No "closed"/"started" flag of the GameSession is watched during a lobby.

---------------------------------------------------------------------------------------------------

### 6. What the server must implement

#### (a) Friend rooms: create / join by id

1. `GameSessionService/ListLatencyMeasurementServers` -> empty reply is acceptable (already needed for automatch). (I)
2. `CreateGameSessionCreationTicket`: read `2.2` (config, expect `.../matchmakingConfigs/Private24`), `2.3` players,
   `2.6.6` password, `2.6.11` attributes. Create a room `{id, password, attrs, owner}`. Reply
   `GameSessionCreationTicket{1: "tenants/<t>/gameSessionCreationTickets/<uuid>"}`.
3. `TrackGameSessionCreationTicket` (server stream, request field 1): send one message
   `{1: name, 2: config, 3: players, 4: 2, 5: [MatchedPlayer{1 Player(with real user resource), 2
   "<session>/userSessions/<user>", 3 "<user-session id>"}], 6: GameSession}` and close with OK. GameSession:
   `1` name, `2`/`3` counts, `4`/`5` true, `6` `"*"` if a password is set (never echo the password), `8`/`9`
   Gamesync endpoint, `10` timestamp, `11` the creator's attributes, `12` members with
   **field 1 = userSession name, field 2 = user**.
4. `CancelGameSessionCreationTicket{1 name}` -> empty reply; if a Track stream is open end it with state 3.
5. `CreateGameSessionShortAlias{1 parent, 2 {2 game session resource}}` -> allocate a unique room id (6 digits)
   and reply `{1: "tenants/<t>/GameSessionShortAliases/<id>", 2: same game session resource, 3: Timestamp}`.
   Be idempotent per session (the node may run again).
6. `GetGameSessionShortAlias{1: ".../GameSessionShortAliases/<id>"}` -> the same alias message, or NOT_FOUND.
   It is called twice per join (check, then join).
7. `GetGameSession{1 name}` -> the `GameSession` (field 6 `"*"` when password-protected, 11 attributes, 12 members).
8. `JoinGameSession{1 name, 2 password, 3 players, 5 users}` -> check room exists / not full / password, add the
   member, reply `{1: [MatchedPlayer for each joining user], 2: GameSession}`. Errors as gRPC statuses
   (NOT_FOUND, FAILED_PRECONDITION/PERMISSION_DENIED for a bad password, RESOURCE_EXHAUSTED when full).
9. Gamesync side: the room is the same kind of lobby as an automatch lobby (same `__gs/f`, `__us/<id>`,
   `__pgn/All/...` documents); every joiner must get the already-present members in the `__us` collection.
10. For the friend list (optional): `BatchGetGameSessions{2 names}` -> `{1: [GameSession...]}` (omit unknown ids),
    plus the Presence service carrying `SessionId` for each player in a room; `QueryGameSessions` -> `{1: []}`.
11. Fix C1 (Member 1/2 order) in the automatch reply too.

#### (b) Clean leave / cancel

1. `CancelMatchmakingTicket{1 name}`: remove the ticket from the queue, reply empty, then finish that ticket's
   Track stream with a last message **state 6** and OK trailers (not a bare close: OK without a final state =
   "Unknown error", status CANCELLED = "deadline" 2321-4992).
2. Always end Track streams with a final state: 3 matched, 4 server-side timeout, 5 failure
   (`3: {2: message, 3: grpc code}`), 6 cancelled. Send a ticket update (state 1) at least every < 150 s if a
   search may last longer, or let the client time out on its own.
3. Never send state 7 unless `CreateAcceptance` is implemented (the client would answer with it and wait).
4. Leave = the player's `KeepUserSession` stream ends (also on crash / timeout). On that event the server must:
   apply the user's stored **deferments** (`userSessions/current/deferments/<name>` entries received in
   WriteDocuments), remove `docs/__us/<id>` and `__pgn/All/__pus/<id>` from the lobby, push those removals to
   the remaining members' listeners, free the seat (so the room/lobby count and `GameSession` members are
   right for later Get/Join), and delete the room + its short alias when the last member leaves.
5. A player who leaves and searches again sends a brand-new `CreateMatchmakingTicket`; nothing links it to the
   old session, so stale membership must not block it.

#### (c) Continuing in the same session after a match

1. Do nothing at the matchmaking level: no new ticket, no GameSession update is requested by the client
   between races; keep the GameSession, the user sessions and the Gamesync endpoint alive and unchanged.
2. Keep every member's `KeepUserSession` stream and all listeners open across races; the next race is driven
   only by Gamesync documents (`Global`, votes, `@@RefereeRequest/@@RefereeResult`) and Pia.
3. Late joiners/leavers during or between races are handled as in (a)8 / (b)4 (update the `__us` collection;
   the matchmaking snapshot of the others is never refreshed).
4. Do not expire a GameSession (or its short alias) while it has members; field 10 is only informational for
   the client (never checked against the clock in the code read).
5. If the game does request a different lobby type (UpdateSession / UpdateLobbyStatus nodes), expect a normal
   leave (b)4 followed by a fresh ticket / create / join.

---------------------------------------------------------------------------------------------------

### 7. Not resolved (honest gaps)

- Where the finished `CreateGameSessionShortAlias` future (`ctx+0x8f68`) is polled and its id copied to
  `ctx+0x48` (no direct reference found; likely through a different base pointer). The reply shape itself is
  verified from the SDK converter.
- Semantics of `GameSession` 2, 3, 4, 5, 10 and `Member` 3, 4, 5, 6: positions and types verified, names guessed.
- Which gRPC status the game maps to "wrong password" / "room full" / "room not found" dialogs (error kinds
  0x22/0x23 are posted for alias and session lookup failures; kind 8 for join failures in `FUN_02239f60`).
- Contents of the game-supplied string lists (Create request 3, Join request 4, CreateMatchmakingTicket request 3;
  builder `FUN_0223b418`) and when `P+0x470` ("reservation") is filled - presumably the companion / second-player
  flow (StartCompanionMatchmake node `FUN_01a793f0` not read).
- The executor of the pending lobby-switch record (`lobby+0x4a8..0x4b5`) and the exact mapping of game op ids
  1, 2, 9-14 to leave / start-P2P / etc.
- Whether the game passes an acceptance handler to the automatch job (only matters if the server sends state 7).

---

## 4. Error id catalog


All addresses are image offsets. V = VERIFIED (read in disassembly/decompile at the address given), I = INFERRED.
Frame counts converted at 60 fps. Decompiles used: re-map\a4-core.c, a4-post.c, a4-svc.c, a4-svc2.c, a4-timers.c, a4-t2.c,
a4-ref.c, a4-b3.c, a4-eh.c, a4-seq.c, a4-pA.c, a4-pB.c. Scripts: a4_const.py (movz/movk 32-bit const index), a4-scan3.py (wrapper callers + ids).

### 0. Corrections to the brief

- V: there are no direct BL callers of FUN_0222d78c. Every hash-id post goes: wrapper -> `(*(0x33e7ee0))->vtable[0x70]` (GOT 0x32f2ad0 -> 0x33e7ee0, vtable 0x32a8038, slot 14 = **FUN_0262125c**) -> error service `FUN_02226c10()` (= `*(*0x33dc800+0x28)`) vtable 0x325bc30 slot 6 FUN_0222d78c -> FUN_0222d7d8.
- V: FUN_02624cdc posts **0x6094b033** (packet-loss case), not 0x709ed269. 0x709ed269 is only the RTT case (FUN_02624c70).
- V: nn::Result / Pia / gRPC failures are NOT converted into hash ids. They are posted as separate records with a numeric *reason* (1..38) through other error-service functions (section 2). Hash ids always carry reason 37 (0x25). The only place an nn::Result rides along with a hash id is FUN_02624b24 (section 3, "kind 1" ids).
- V: 0x9b928162 is not a "mesh closed" error. It is the "player idle / kart not moving / no input" verdict of the per-player race watchdog (section 5), and it is the only id the UI special-cases (ErrorDialogType 6).

### 1. Poster, record layout, parameters

Game-side poster FUN_0262125c(this, u32* id, 12-byte* result, int* prio) (V, 0x262125c):
```
if (FUN_00aa3750() || FUN_00aa38b0() || *prio != 2)      // "online play active" predicates; prio!=2 always passes
    errsvc->vt[6](errsvc, {id, prio, result[12]});
```
FUN_00aa3750 / FUN_00aa38b0 (V): true when the local station is in the session member list with state 2, or the net state object `*(*(0x33e7ed8)+0xd8)` has +0x46c in certain states (3..8, 0xb, 0xd bitmask 0x2038 etc.). I: "we are in/entering an online session". So prio-2 ids are silently dropped when offline.

FUN_0222d78c (slot 6) locks svc+0xb0 and calls FUN_0222d7d8(svc+0x70, rec) which builds the 0x20-byte error record (V, 0x222d7d8):

| off | field | value for hash-id posts |
|---|---|---|
| +0x00 | priority (int) | the wrapper's "kind" argument: 2 (all wrappers) or 1 (FUN_02624b24) |
| +0x04 | 12-byte result variant (type via import 0x282b5c0: 1 = nn::Result compared on low 22 bits, 2 = 64-bit code) | default global *(0x32fce30)=0x33dc588 (success/empty), or the nn::Result for FUN_02624b24 |
| +0x10 | kind (int) | 12 (0xc) |
| +0x14 | reason (int) | 37 (0x25) |
| +0x18 | hash id (u32) | the id |
| +0x1c | flag byte | 1 |

Replace policy FUN_02226f1c / dedupe FUN_02227060 (V): a new record replaces the stored one only if its priority is higher (equal priority: only in special cases involving reasons 0x1b..0x24); identical records are ignored. So the FIRST error of a given priority wins; later ids are lost.
Slot 4 FUN_0222db04 = "error pending" (`*(svc+8) > 0`); slot 5 FUN_0222d714 = clear all buffers; slot 7 FUN_0222d874 = promote back buffer to front buffer (double buffer at svc+0x30[2], index svc+0x48).

Priority table for non-id records FUN_02226dd0(reason, kind) (V, table 0x29cf6f8 indexed by reason 0..38):
`0,4,3,4,4,3,2,2,2,3,3,2,2,2,2,2,3,3,3,2,2,2,2,2,2,2,4,2,2,2,2,2,2,2,1,2,2,2,4`; kind 1 or 3 forces priority 4.

#### id -> message mapping
V (FUN_02141dac @0x2141dac, called from FUN_01e3f920 = virtual "on error" callback in vtable slot 0x31dad90): the record's reason is mapped to an **ErrorDialogType** and event 0x73f65e77 (ErrorDialogType, RawErrorApplicationId) is emitted; flag bit 16 of `*(0x33d2548)+0x110` is set ("error UI requested"):

| reason | ErrorDialogType |
|---|---|
| 0x1c | 0 |
| 0x1d | 1 |
| 0x1e | 2 |
| 0x1f | 3 |
| 0x20 | 4 |
| 0x22, 0x23 | 5 |
| 0x25 and id == 0x9b928162 | 6 (I: "disconnected for inactivity" text) |
| anything else (all other hash ids, all other reasons) | 7 (generic communication error) |

RawErrorApplicationId = the hash id (only filled for reason 0x25). V: no id->message table exists in code; I searched every movz/movk occurrence of each id - they occur only at the posting sites (plus 0x9b928162 in FUN_02141dac). I: the on-screen error code for type 7 is derived by the viewer from the id/result (not decoded).

#### How a pending error is consumed (sequence nodes)
- V ErrorRoot (name slot 0x3168320; event handler FUN_01a408b8): on event 0x73f65e77 stores the two args in sequence variables named "ErrorDialogType" (0x28a05ae) and "RawErrorApplicationId" (0x288f556), once (flag +0x28).
- V ErrorHandler (name slot 0x31681d0; event handler FUN_01a405a4; state machine FUN_004406b4 on the helper at node+0x28):
  - state 0 -> 1 when flag `+0x110 bit16` (set by FUN_02141dac) or `+0x118 bit1` or two net-state bytes (+0x4b4,+0x4b5) are set; sends event 0xa9956e41.
  - state 1: waits FUN_0211cde0()==0 and `+0x11c == 0`; then fade-out FUN_02120ddc(1) -> state 2 (or state 6 if a flag at FUN_008cda28()+0x548 is set = just wait).
  - state 2: FUN_00bead80(); FUN_00a382ac(*0x32f2118, 2) or FUN_008133bc(); FUN_02134874(); event 0x7e36bcd3 -> state 3. I: this is the scene teardown (race/lobby scene exit) and where the session is left.
  - state 3 -> 4 -> 5: waits teardown, then fade-in FUN_02120ddc(0), clears flag bit16, state 0.
  - event 0x2eb6b3bc (viewer closed, bool) sets helper+0x10/+0x11; event 0x73f65e77 sets +0x12.
- V AppErrorViewer (name slot 0x3164ac8; update FUN_01a31290): states 1 (wait until not busy) -> 2 (60 frames) -> 4 (open dialog FUN_021394fc) -> 5/6 (wait close) -> 7 -> 8 (30 frames) -> emits 0x2eb6b3bc and clears `+0x110 bit16`.
- I (not fully traced): after the viewer closes the flow returns to the online lobby/menu root rather than the title unless the reason class demands it; the leave of the NPLN session (match controller op 10) is issued from the teardown in state 2. Only the state machine and events above are verified.

### 2. nn::Result / Pia / gRPC errors (reason-coded records, no hash id)

Four posters in the error service (all build the same 0x20 record, V):

| function | input | notes |
|---|---|---|
| FUN_0222a04c(svc, buf, grpcStatus*, reason, kind) | gRPC status object (int code at +0) | code 3 or 12 -> reason 38; reason 6: code 1->0x24, 10->0x21, 4->0x20; reason 8: table 0x29cf794 for codes 1..10 = `36,8,8,8,31,8,30,8,29,28`; default reason 0x1a, kind 0xb. Result = FUN_00efd4c0(code, status) (gRPC -> nn::Result, the 2321-xxxx family). |
| FUN_0222a2cc(svc, buf, nnResult*, reason, kind) | nn::Result | default reason 0x1a, kind 0xd |
| FUN_0222d998(svc, buf, result12*, reason, kind) | 12-byte result | reason 1 + kind 1 -> priority 5, flag 0 |
| FUN_0222d4bc(svc, buf, piaResult*, reason, kind) | Pia result u32 | `(r>>13)&0x1f`: 1,2 -> ignored; 3,4 -> prio 2; 5,7 -> prio 3; else 4. Special: 0x6c3d, 0x6c1d, 0x6c48 -> reason 0x1c; 0x6c4f -> 0x1d; 0x6c4d -> 0x1e; 0x6c47 -> 0x1f (these select ErrorDialogType 0..3) |

FUN_0222a210 (common tail, V): if the nn::Result has module 0x7c (2124-xxxx) and description 200..269 -> kind 8, reason 2, priority 5, flag 0.

Call sites and their reason argument (V, arguments recovered from the mov before each BL; a4-scan7.py):

| reason | poster | caller (session manager unless noted) |
|---|---|---|
| 3 | a04c/d998/a2cc/d4bc | FUN_02236de8 (start/join op), FUN_022477b4, FUN_0222f524, FUN_0222f7b8 |
| 4 | d998/a2cc | FUN_02247c1c |
| 5 | d4bc/d998 | FUN_0222bdf0, FUN_0222dea4, FUN_022338b0, FUN_0223ad08 |
| 6 | a04c | FUN_02237d98, FUN_022391a0 (op pollers) |
| 7 | a04c | FUN_022384d0, FUN_02239878 |
| 8 | a04c | FUN_02238c08, FUN_02239f60 |
| 9 | a04c | FUN_02237a6c |
| 10 | a04c | FUN_0040ea74 (vtable 0x32a80e8 +0xa8) |
| 11 | d4bc/d998 | FUN_0224fb74, FUN_02255f78, FUN_022341c4 |
| 12 | a04c | FUN_02236714 |
| 13 / 14 | d4bc | FUN_0222f3c8 / FUN_0222f454 |
| 15 | d4bc | FUN_0222bc74, FUN_0222dcc8 |
| 16 | a04c | FUN_0223b1a0 |
| 17 | d4bc | FUN_0223ad08 |
| 20 | d998 | FUN_022491ac |
| 22 / 23 | d4bc (d998 for 22 in FUN_022409fc) | FUN_02241c3c, FUN_0224edb4 / FUN_02241d64, FUN_0224f0d4 |
| 24 | d998 | FUN_02254200, FUN_02254e4c |
| 25 | d998 | FUN_022495d4 |
| 26 | a2cc (kind 8) | FUN_02236c64 (import 0x282b700 failed) |
| 27 | d998 | FUN_022341c4 |
| 34 | a04c | FUN_02235a80 |
| 35 | a04c | FUN_02239f60 |
| 38 | d998 | FUN_02234dbc, FUN_022477b4, FUN_0224a678 |

I: reasons 6/7/8 are the three op families polled by FUN_02237828's pollers (create / join / leave-or-update); reasons 22/23 are Pia session/mesh events (disconnect, host migration failure). Individual semantic names were not decoded.

Server consequence (V for the mechanics): any non-OK gRPC status on a polled matchmaking op produces a reason-coded record whose displayed code is the nn::Result from FUN_00efd4c0 (2321-xxxx). gRPC codes 3 (INVALID_ARGUMENT) and 12 (UNIMPLEMENTED) are forced to reason 38 (priority 4, the highest table value).

### 3. Wrapper family (all V)

| wrapper | behaviour | callers |
|---|---|---|
| FUN_02624ad8(u32* id) | unconditional post, prio 2, default result | 29 sites (section 4) |
| FUN_02624b24(u32* id, nnResult*) | post with prio **1** and result built by import 0x282b6c0 from the nn::Result | 8 sites in FUN_0049b248 / FUN_0049bc5c |
| FUN_02624b84() | post 0x3e62f892 if none pending | FUN_0051846c x2, FUN_008188bc (inlined copy), FUN_01a95cc8, FUN_01a964d8 |
| FUN_02624bf0(u32* id) | post *id if `*(*(*0x33e7ed8+0xd8)+8)==0` and none pending | FUN_020d4ab0 (watchdog, id at obj+0x1cc) |
| FUN_02624c70() | 0x709ed269 if none pending | FUN_00499148 |
| FUN_02624cdc() | 0x6094b033 if none pending | FUN_00499148 |
| FUN_02624d48() | 0x03e5ac7e if none pending | FUN_00ca5648 |
| FUN_02624db4() | 0x8bd70a64 if none pending | FUN_003604b0 (tail), FUN_00360bf8 |
| FUN_02624e20() | 0x23b94dd8 if none pending | FUN_003604b0 (tail), FUN_00360bf8 |
| FUN_01e46958() | 0x606314c9 if none pending (via 2624ad8) | FUN_02126ad4, FUN_021271c8, FUN_021274d4 |
| FUN_01e4699c() | 0x67f88170 | FUN_00498404 |
| FUN_01e469e0() | 0xe538311c | FUN_01a96440, FUN_01a964d8 |
| FUN_01e46a24() | 0x5d0aa622 | FUN_0051846c x2 |
| FUN_01e46a68() | 0x36259bfd | FUN_0051846c |
| FUN_01e46aac() | 0xaa040955 | FUN_002408e0 |
| FUN_01e46af0() | 0xf4151c7b | FUN_002408e0 |
| FUN_0213bd34(timer*) | post timer+0xc if timer+8 flag and online | FUN_00655560, FUN_0213bd04 |

"If none pending" = `errsvc->vt[4]()` (FUN_0222db04) false.

### 4. Catalog

"Affects": every check runs locally on each client, so the client whose condition trips gets the error; there is no host-only id except where noted.

#### 4.1 Race / result phase (most relevant to the private server)

| id | posting function + address | condition | timers | affects | status |
|---|---|---|---|---|---|
| 0x606314c9 | FUN_021271c8 @0x2127374 | `@@RefereeResult` wait: FUN_02127388 (result doc lookup for key at this+0x98) still fails and wall clock since this+0x140 (stamped in FUN_0212701c when the result request is written via FUN_01e37e98) > 36000 ms; sets this+0x148 | 36 s wall clock | each client waiting for the referee result | V |
| 0x606314c9 | FUN_02126ad4 @0x212700c | referee result entry found (FUN_01e37600) but its status field `+0xa0 > 1` (0 = pending, 1 = OK; >1 = failed); sets this+0x29 | none | each client | V (status meaning I) |
| 0x606314c9 | FUN_021274d4 @0x21274e8 | unconditional helper (sets +0x148); no direct BL callers (virtual/callback) | - | - | V |
| 0x67f88170 | FUN_00498404 @0x498920 | the async RefereeResult query future at ctx+0x8f78 completed with non-zero status (FUN_01e3a7f4 status != 0) | none | each client | V |
| 0xcd1bf9ca | timer armed in FUN_02128568 / FUN_02128120 @0x212862c (FUN_0086b7ec(this+0x15c, 0x708, &id)); fired by FUN_00529fcc @0x52a0a4 | armed once (this+400 bit0) when the local player's race entry reports finished (`(*entry+8)->vt[7]()`) and FUN_02630f7c() is false. Fires when counter this+0x160 reaches the limit while race info `+0x14 != 0`. Cancelled (FUN_00c33c4c zeroes limit/counter/id) when byte this+0x168 is set, which happens on event 0x39f129b (FUN_0086b558 @0x86b684) | 0x708 frames = 30 s | the finished local player | V (event meaning I: "race end / result accepted") |
| 0xf6378925 | id source FUN_0086b7e0 (vtable slot 30 of the race-mode classes at 0x32329f0 and 0x3232d28); armed in FUN_0086b2c4 @0x86b510; fired by FUN_00529fcc @0x52a0a4 | on network event 0x47b1ddf2 (payload <= 0x400 bytes carrying a player index; marks that player's slot byte +5 = "finished") if the timer is not already running: arm 0xe10 frames with id = vt[30](). Same cancel rule as above (event 0x39f129b) | 0xe10 frames = 60 s after the first "player finished" message | every client still in the race (also the finished one if its 30 s timer is not armed) | V; I: "race did not end within 60 s of the first finisher notice". This is the id seen when the result never arrives |
| 0xf1ebc3e3 | FUN_02127864 (slot 30 of base vtable 0x32323f0) | same 60 s timer in the base race-mode class | 60 s | - | V (constant), I (which mode uses the base) |
| 0xd2a56bf9 | FUN_0212c648 @0x212c888 | derived race-mode event handler (calls FUN_0086b2c4 first): after flushing the pending result list via FUN_021269d0, in the branch where the expected ranking entry is absent | none | each client | V site, I meaning |
| 0x709ed269 | FUN_00499148 @0x4994a8 | once per >= 1000 ms: count peers with RTT (FUN_0222a3dc) > 1000.0; if count >= ceil(n/2) (n = FUN_02255570(), skipped when n == 1) push a timestamp into ring ctx+200; if the ring is full and its oldest sample is <= 15 s old (threshold count 1) or <= 30 s old (otherwise) -> post | 1 s sampling, 15/30 s window | each client with bad links | V |
| 0x6094b033 | FUN_00499148 @0x499524 | same with packet loss (FUN_0222a448) > 70.0, ring ctx+0x108 | same | same | V |
| 0x3e62f892 | FUN_02624b84 from FUN_0051846c @0x5190c4/0x5191f0, FUN_01a95cc8 @0x1a9621c, FUN_01a964d8 @0x1a96588; inlined in FUN_008188bc @0x818a84 | a P2P game-message send returned false (FUN_01e3e228 msg 0x7efcfac1, FUN_01a96984 msg 0x68712751, FUN_01a5b960 msg 0x7f00beaf, FUN_025d0b0c) | none | sender | V |
| 0xe538311c | FUN_01a96440 @0x1a964b4, FUN_01a964d8 @0x1a96538 | per-station data accessor FUN_025d0000 / FUN_025cfee8 failed (data id 0xd36076e7) | none | local | V site, I meaning |
| 0x5d0aa622 | FUN_0051846c @0x518dfc and @0x5195ac | (a) FUN_01e3da44 failed and FUN_02141ad4() true; (b) counter this+0x44 > 0x708 while member state bits `&3 == 0` and this+0x2a == 0 | (b) 30 s | local | V |
| 0x36259bfd | FUN_0051846c @0x51915c | fall-through failure branch of the same sync state machine | none | local | V site, I meaning |
| 0x03e5ac7e | FUN_00ca5648 @0xca57a8 | route/segment tracker: new (u16,u16) position is > 8 steps away from the stored one (FUN_00ca57b0 walk count > 8) with param_3 bit0, valid slot, and obj+0x10 bit5 | none | local (I: remote kart position desync check) | V site |
| 0x8bd70a64 | FUN_00360bf8 @0x360ee8, FUN_003604b0 @0x360668 | counted set bits < expected FUN_003610c0() and obj+0x10 bit5 | none | local | V site, I: checkpoint/consistency validation |
| 0x23b94dd8 | FUN_00360bf8 @0x360e80, FUN_003604b0 @0x3606c0 | obj+0x32 < FUN_00360f44() and obj+0x10 bit5 | none | local | V site |
| 0x660ac5da / 0x5382e895 / 0x9b928162 | watchdog, section 5 | | | | |

#### 4.2 Gamesync / NPLN document fetch failures (FUN_02624b24, prio 1, carries the nn::Result)

All V. A completed future with status 0 = success; listed "tolerated" statuses post nothing; any other gRPC status posts the id together with the converted nn::Result.

| id | function + address | request | tolerated statuses |
|---|---|---|---|
| 0x8474755b | FUN_0049b248 @0x49b418 | future at obj+0x80f*8 | 1, 3, 5, 7 (mask 0xaa) |
| 0x3b0789de | FUN_0049b248 @0x49b98c | future feeding state obj+0x3e7c | 1, 3, 5, 7 |
| 0x9c32c4f7 | FUN_0049b248 @0x49b9d8 | future feeding state obj+2000*8 (string "ugc_slot" nearby) | 1, 3, 5, 7 |
| 0x152ba8df | FUN_0049b248 @0x49ba68 | future at obj+0x817*8 (state obj+0x3e84); status 5 handled as "reset" | 1, 3, 7 (mask 0x8a) and 5 |
| 0x1e3041a0 | FUN_0049bc5c @0x49be2c | future at ctx+0x18 (state ctx+0x78) | 1, 3, 7, 11 |
| 0xbc6ffb17 | FUN_0049bc5c @0x49c8ec | future feeding ctx+0x6748 ("gs_%x_%d" key) | 1, 3, 5, 7 |
| 0xa61b6894 | FUN_0049bc5c @0x49bda4 | "Rating" fetch (state +0x6758) | 1, 3, 5, 7 |
| 0xab80c000 | FUN_0049bc5c @0x49c378 | "Rating" list fetch (state ctx+0x6788) | 1, 7 |

Server rule: answer these with OK or NOT_FOUND (5) / CANCELLED (1) / INVALID_ARGUMENT (3) / PERMISSION_DENIED (7) as allowed per row; anything else (e.g. UNIMPLEMENTED 12, INTERNAL 13, UNAVAILABLE 14) raises a priority-1 error. Priority 1 is lower than every prio-2 id, so a later prio-2 id replaces it.

#### 4.3 Lobby / sequence timeouts (FUN_02624ad8, prio 2)

Node names are taken from seq_nodes.tsv by "next getter after the function" (I); sites and timers are V.

| id | function + address | node (I) | condition | timer |
|---|---|---|---|---|
| 0xa140d3d4 | FUN_01a3cb9c @0x1a3cfd4 | CourseRoulette | wall clock since node+0x27*8 >= 10001 ms in the wait branch | 10 s |
| 0x02363a78 | FUN_01a3cb9c @0x1a3d224 | CourseRoulette | state 5, FUN_01a3d234 not done, > 50000 ms | 50 s |
| 0x72a5fef7 | FUN_01a3e460 @0x1a3e4b8 | CourseVoteWaitPlayRequest | frame counter node+0x98 > `*(node+0xa0) * 60` | N s (N from data) |
| 0x27ca935a | FUN_01a5a444 @0x1a5aac0 | PrivateRoomMain | wall clock >= 15000 ms since node+0x29*8 | 15 s |
| 0x11173fe5 | FUN_01a5b6bc @0x1a5b798 | PrivateRoomMain | a session member's player bit missing from mask obj+0x40 | none |
| 0xa367900c | FUN_01a77254 @0x1a7729c | SetupRaceGate | counter node+0x98 > 0x4b0 | 20 s |
| 0x46b96f91 | FUN_01a82d84 @0x1a82f04 | SyncState | wall clock >= 25000 ms | 25 s |
| 0x8b738f76 | FUN_01a93138 @0x1a93730 | WaitMatchmake | expected member mask != present member mask | none |
| 0x17c53c70 | FUN_01a9420c @0x1a94260 | WaitRaceStart | counter node+0x28 > 0x960 | 40 s |
| 0x81ce0890 | FUN_01a947ec @0x1a94840 | WaitSyncPlayMode | counter node+0x28 >= 900 while flag `+0x114 bit15` clear | 15 s |
| 0x6f476dbe | FUN_00bf43a8 @0xbf44a4 | (seq node helper) | wall clock > 25000 ms | 25 s |
| 0x6f6041e6 | FUN_00d05460 @0xd055b4 | (seq node) | counter +0x2c >= 0xe10 waiting FUN_00d055c0 | 60 s |
| 0x8b511a71 | FUN_00d0e478 @0xd0e524; FUN_006540f4 @0x6552cc | (seq node) / race loader | counter +0x2c >= 0x709 waiting FUN_00d0e52c / FUN_00d0e66c | 30 s |
| 0x5ea7ecc7 | FUN_006540f4 @0x654fac | race loader state machine | counter obj+0x12*8 > 0xe0f while FUN_00245030 fails, online only | 60 s |
| 0x9850e06b | FUN_00655560 -> FUN_0213bd34 @0x213bd70 (timer struct at global 0x33d1598: limit 0xe10, id at +0xc) | readiness helper used by several nodes | called "not ready" 0xe10 times | 60 s |
| 0xa263f39d | FUN_00cb00c8 @0xcb07bc | "RaceSequence" | state 4 (after sending message 0x4a9310ec with start time +8000) counter >= 0x708 | 30 s |
| 0xdc124d41 | FUN_00c27178 @0xc27644 | (race start sync) | state 2: flag obj+0x14*8 set and deadline obj+0x13*8 < FUN_00c2765c() | deadline |
| 0xaa040955 / 0xf4151c7b | FUN_002408e0 @0x241534 / @0x241584 | (sync variable wait) | FUN_01a14a18 keeps failing, counter obj+99*8 > 0xe10; which id depends on the missing variable hash | 60 s |
| 0x94e6a137 | FUN_01e41818 @0x1e41898 | - | event type 2 for a slot already marked (duplicate) | none |
| 0x4d2fcd85 / 0x6db05bf1 | FUN_0045bb3c @0x45c5a4 (id stored at obj+0x50, posted in state 3 after a 60-frame countdown) | - | online and (frame float > 10368000.0 = 48 h at 60 fps -> 0x4d2fcd85) or (tick margin exhausted -> 0x6db05bf1) | 48 h uptime |

### 5. Race watchdog: 0x660ac5da, 0x5382e895, 0x9b928162 (all V unless marked)

Object: per-player race controller state machine. State table built in FUN_00a7307c (refs 0xa735a0 -> FUN_020d43dc = "racing" update, 0xa73934 -> FUN_020d4ab0 = enter of state 9). FUN_0035a9a0 is the state dispatcher with FUN_020d43dc inlined (same constants at 0x35afd8.. and 0x20d4750..).
Fields: +0x1cc pending id (reset to 0x660ac5da by FUN_020d4384), +0x1d0 strike counter, +0x1d4/+0x1d8 last position, +0x1dc no-move frames, +0x1e0 progress target, +0x1e4 slow frames, +0x1e8 move count, +0x1ec "rescued" flag, +0x1f0 rescue window.

Gates (all must hold or nothing counts): +0x1fe == 0; FUN_005b04a8() (local station is session member in state 2); FUN_00656fb8()==0; `*(0x33e6b38)+8 != 1`; player index valid and its race entry has `+0x50 >= 0`, `+0xd0 bit0`, `+0x54 >= 0`; kart state not in 7..11; `!(+0x1fb)` and `(kartObj+0x191 & 8) == 0`.

Per frame (kart flag `+0x110 bit5` clear):
1. position moved less than sqrt(0.01) since last sample: +0x1dc++; at >= 0x708 (30 s) -> candidate **0x9b928162**, strike. Else +0x1dc = 0.
2. `kartObj+0x191` bit7 clear and `kart+0x53c > 0x708` (I: frames without controller input, 30 s) -> candidate **0x9b928162**, strike.
3. progress: p = `*(info+0x28) * FUN_02630f54()`; if p <= target(+0x1e0) then +0x1e4++, at > 0xb3 (3 s) candidate **0x5382e895**, strike; else target = p + 75.0, +0x1e4 = 0. (I: must gain 75 units every 3 s.)
4. `*(info+0x14) & 1` -> candidate **0x660ac5da**, strike.
5. `(*(info+0x2c) - *(info+0x28)) * scale > 5.0` (I: more than 5 units behind own best progress) -> if strike counter is 0, set id 0x660ac5da and counter 1.
A candidate id is only written when the strike counter is 0. With a strike the counter increments by 1 per frame up to 0x2d0; without, it decays by 2.
At 0x2d0 strikes (**12 s**): if the id is 0x5382e895 it is refined to 0x660ac5da (still behind) or 0x9b928162 (no-move >= 0x259 and no-input >= 0x259, 10 s). Then:
- first time (+0x1ec == 0): FUN_020d53c8 = rescue: sends event 0xa66067f2 (player index), resets id to 0x660ac5da and strike counter to 0, sets +0x1ec for 0xe0e frames (60 s);
- again within that 60 s window: transition to state 9 -> FUN_020d4ab0 -> FUN_02624bf0(obj+0x1cc) posts the id.

Additional path FUN_0035ce38 (@0x35d0a0): in rule types 5/6 (`raceInfo+0x1c - 5 < 2`) when the player finishes/gets eliminated: if state time obj+0xb8 > 3000 frames (50 s) and move count +0x1e8 < 20 -> id 0x9b928162, go straight to state 9.

#### Why 0x9b928162 hits the spectating loser ~23 s after the mesh closes
- V: the only producers of 0x9b928162 are the two idle rules (no movement >= 30 s, no input > 30 s), the 0x5382e895 refinement and the mode-5/6 check above. There is no "mesh closed" timer.
- V: the post needs two full 12 s strike windows (first one only triggers the rescue event). 2 x 720 frames = 24 s.
- I: a spectating/eliminated player already satisfies "no input > 30 s" (and usually "no movement"), but one of the gates masks the watchdog while the session is healthy (kart flag `+0x110 bit5`, `kartObj+0x191 & 8`, or kart state 7..11). When the P2P mesh closes that gate opens, the strike counter starts immediately with candidate 0x9b928162, reaches 0x2d0 after 12 s (rescue, counter reset), and again 12 s later -> state 9 -> post. That gives the observed ~23-24 s. Which gate flips was not identified statically; verify by watching obj+0x1d0 / +0x1ec in the probe.
- V: the UI maps this id to ErrorDialogType 6.

0x660ac5da (seen with MKWMP_SKIP_REFEREE_TIMEOUT=2): rule 4/5 above (not progressing / behind own best progress), posted after rescue + 12 s.

### 6. Disconnect / mesh / host-migration handlers
- V: no hash id is posted by Pia callbacks. Pia results go through FUN_0222d4bc with reasons 5, 11, 13, 14, 15, 17, 22, 23 (table in section 2); the four special results 0x6c3d/0x6c1d/0x6c48, 0x6c4f, 0x6c4d, 0x6c47 choose ErrorDialogType 0/1/2/3.
- V: results whose `(r>>13)&0x1f` is 1 or 2 are ignored by FUN_0222d4bc.
- V: the game-level symptoms of a dead mesh are the hash ids of section 4.1: 0x709ed269 / 0x6094b033 (link quality), 0x3e62f892 (send failed), 0xf6378925 / 0xcd1bf9ca / 0x606314c9 (race never ends / no referee result), and the watchdog ids.
- V: FUN_01e3f88c (net-state virtual, vtable 0x31dad80): on state 10 writes the "Companion" document field "companion_state" through FUN_0049c9d8; on state 2 it tears down the per-match objects. Not an error path.

### 7. What a private server must do to avoid each id
- 0x606314c9 / 0x67f88170: answer the RefereeResult query with gRPC OK and make a result entry with status 1 appear within 36 s of the client's request (FUN_0212701c stamps the clock when it writes the request). A status > 1 or a non-OK query status fails immediately.
- 0xf6378925 (60 s) / 0xcd1bf9ca (30 s): these are cancelled only by local event 0x39f129b; I: that event is produced when the race result is accepted, so the same referee result fixes them.
- Section 4.2 ids: return OK or a tolerated status for every document fetch; never UNIMPLEMENTED/INTERNAL.
- Section 2: every polled matchmaking op must return gRPC OK; codes 3 and 12 become the highest-priority error.

### 8. Limits of this catalog
- The id index only covers 32-bit constants built with `movz w / movk w,#,lsl 16` within 5 instructions (a4_const.py). Ids stored through a 64-bit immediate (found one: 0x9850e06b at 0x33d1598) or loaded from data can be missed. Ids kept in objects and posted later were followed for obj+0x1cc (watchdog), obj+0x164 (race timer), obj+0x50 (FUN_0045bb3c), timer+0xc (FUN_0213bd34).
- Semantic names of reasons 1..38 and of events 0x39f129b / 0x47b1ddf2 / 0x7e36bcd3 are inferred from context only.
- The exact session action after the dialog (leave vs title) was not traced past ErrorHandler state 2.

---

## 5. Race progress sync


Static analysis of `scratch/main.bin` plus the game's own network schema files (romfs
`System/GymlArchive/general_format.Product_100.sarc.zs`, decompressed to
`re-map/a5-general_format.Product_100.sarc`). V = VERIFIED (read in code/data, address given),
I = INFERRED.

Helper files (all in `re-map/`): `a5-*.c` decompiles, `a5-netgyml.txt` (dump of every Net*GParam
bgyml), `a5_ev.py <regex>` (pretty-prints replicas/events with hash names resolved),
`a5_const.py <name|0xhash>` (finds where a murmur3 name hash is used in code),
`a5_sarc.py`, `a5_byml.py`, `a5-pylib/` (zstandard, pip-installed only into this folder).

### 0. Short answers

| Question | Answer |
|---|---|
| What carries lap/checkpoint between players? | **Nothing.** Lap and checkpoint are never serialized. Every client runs a `LapChecker` for every kart (local and remote) on that kart's position. For remote karts the position comes from the `Player` replica (`pos`, `front`, `up`, `vehicle_vel`). V |
| Who is authoritative for lap? | Each client for itself, and each client recomputes everyone else's lap locally from replicated positions. `Player_ChangeLap` is a local (IntraProcess) event. V (schema) |
| What carries "player N finished"? | Per-player `Player` replica elements `is_goal` (bool) and `goal_clock` (u64 ms, max 1 210 000), sent by the owner of the kart. V `FUN_00911c1c` |
| Who fixes the finishing order? | The server referee: clients write `@@RefereeReport/<request uuid>/User/<ussid> {result:[player_idx...], retired:bool}`, server answers in `@@RefereeResult` `result[rank] = player_idx`. V. In host-driven modes the P2P events `Net_NoticeGoalRank` / `RaceSeq_GoalRankFixed` do it instead. I |
| Does the server take part in lap/checkpoint/position sync? | No. No Gamesync doc carries lap, checkpoint, position-in-race or timing. V (all doc writers listed in section 5) |
| What is watchdog `FUN_0035a9a0` / error `0x660ac5da`? | An anti-stall check on a kart's **own LapChecker** in a network session: `0x660ac5da` = "wrong way, or more than 5 units behind own best progress". It never looks at the online context, the player map or any doc. V |

### 1. P2P data channels

#### 1.1 Pia protocols instantiated by the game (V)

`netcfg = *(*0x33dc800 + 0x58)` (getter `FUN_02226cf0`) holds one std::map per transport class;
the session manager (`FUN_0223462c`) calls the creators below. Port id = `100 + entry.byte@+0x1c`.

| Pia protocol (RTTI) | Pia ctor | Game factory | Registered by | Config map |
|---|---|---|---|---|
| `transport::UnreliableProtocol` | `0x15a988c` | `FUN_0225252c` | `FUN_0225189c` | `netcfg+0x40` |
| `transport::ReliableProtocol` | `0x1596150` | `FUN_022525dc` | `FUN_0225189c` | `netcfg+0x100` |
| `transport::BroadcastReliableProtocol` | `0x158915c` | `FUN_0225268c` | `FUN_0225189c` | `netcfg+0x1c0` |
| `transport::StreamBroadcastReliableProtocol` | `0x159f2a0` | `FUN_0225273c` | `FUN_0225189c` | `netcfg+0x280` |
| `clone::BroadcastEventProtocol` | `0x14ff314` | `FUN_022527ec` | `FUN_02251370` | `netcfg+0x340` |
| `clone::ClockProtocol` (args 1, 100000) | `0x14ff464` | `FUN_0225289c` | `FUN_02251370` | - |
| `sync::SyncProtocol` | `0x1583ce0` | `FUN_02252940` | `FUN_02251270` | settings `netcfg+8` |

Teardown: `FUN_02251554` (clone), `FUN_022517ac` (sync). Pia "clone" *elements* (CloneProtocol) are
not used: there is no `nn::pia::clone::CloneProtocol` RTTI in the image, only Event/BroadcastEvent/Clock. V

Traffic-statistics categories (`FUN_02255900`): `System`, `ReplicaUnreliable`, `ReplicaReliable`,
`ReplicaEvent`, `ReplicaHolderEvent`, `Unreliable`, `BroadcastReliable`, `BroadcastEvent` (+ per-packet
`NetworkHeader`, `PiaHeader`, `Footer`, `Crypto`). So the game layers its own **replica** system
("gear" engine `NetReplica`) on Pia Unreliable/Reliable, plus an **event** channel. V

#### 1.2 The replica schema (V, from bgyml; names are murmur3-32 seed 0 hashes)

`Gyml/Component/NetGParam/*.bgyml` binds an actor to a replica and a sender policy:

| Actor | Replica | SenderPolicy |
|---|---|---|
| Player | `Player` | `Manual` (the owning station sends) |
| RaceManager | `RaceManager` | `Host` |
| GlobalSequence | `Sequence` (events only) | `Host` |
| RaceSequence | `Sequence` (events only) | `Manual` |
| System_NetManager | `System_NetManager` (events only) | `Host` |
| System_NetStation | `System_NetStation` | (default) |
| SoundMessageManager | `SoundMessageManager` | `Host` |

`ProtocolType` enum = {Unreliable, Reliable}, default 0 = Unreliable (`FUN_0275fdcc`, default word at `0x2a280dc`). V

**`Player` replica** (46 elements; `re-map/a5_ev.py "^Player\.gear__NetReplica"`). No lap, no
checkpoint, no sector, no rank-in-race element exists:

| # | Protocol | Name (hash) | Type / range |
|---|---|---|---|
| 0 | U | `pos` | Vec3f +-9999, 7 frac bits |
| 1,2 | U | `front`, `up` | normalized Vec3f, 12 bits |
| 3 | U | `vehicle_vel` | Vec3f +-9, 10 frac bits |
| 4 | U | `stick` | Vec2f +-1 |
| 9,10,11 | U | `warp_count`, `recover_count`, `recover_end_count` | 2-bit counters |
| 12 | U | **`is_goal`** (0xa77a1b7c) | bool |
| 13,14,15 | R | `vehicle_id`, `driver_id`, `sticker_id` | StringId |
| 16 | R | `team` | 0..4 |
| 17 | U | `#454cbf44` | CompressedClock |
| 18 | R | `#e57101cf` (S8_aid) | -1..23 |
| 19 | U | **`goal_clock`** (0x3c89a831) | u64 0..1 210 000 (ms), compressed |
| 20,21 | U | `dash_count`, `dash_level` | 0..250, 0..31 |
| 23 | U | `star_count` | 0..255 |
| 24 | U | `is_wing` | bool |
| 29 | U | `is_on_rail` | bool |
| 31 | U | `is_killer` | bool |
| 35 | U | `coin_num` | 0..999 |
| 37 | R | `group_race_point` | 0..99999 |
| 38 | R | `#392f4500` (S32_goal_rank_with_invalid) | -1..23 |
| 39 | U | `#ac8f3b93` | bool (read by `FUN_00911c1c`, sets bit `player_idx` in mask `G+0x1c` via `FUN_0211e078`) |
| 44 | R | `is_handle_assist` | bool |
| 45 | R | `driver_change_type` | enum |
| others | U/R | unresolved hashes (bools, small ints) | see `a5_ev.py` |

**`RaceManager` replica** (Host -> all, Reliable): group play mode, team, com level, cc, interval
seconds (x2), race count, rule version, `privateroom_item_mode`, vs mode, `playing_bitmap`
(0x25576822), bitmaps `#dd7688ee` and `#de577dcb`, bool `#c28db670`, random seed (Unreliable).
Receiver `FUN_01a7f0d8` copies them to `*(0x33d2548+0x188)` (`+0x40` playing_bitmap, `+0x44` #de577dcb,
`+0x3c` #c28db670); host-side setter of #de577dcb is `FUN_0214238c`. V

**`System_NetStation` replica** (Reliable): `station_id` (u64), one untyped element (NameHash
688846003, name unresolved), and one ENetStationSequenceState element (NameHash 3816852124, name
unresolved). V

#### 1.3 Events (V for the schema, 579 formats in `Gyml/NetEventFormatGParam/`)

`EventType` enum order = {Common, InterProcess, IntraProcess, Common_RegardlessSender}
(`FUN_02621c0c`), **default = 2 = IntraProcess** (`FUN_0262171c`, default word `0x2a26ac8`). V
Meaning (I, from names and from which events carry which type): IntraProcess = local only;
InterProcess = remote only; Common = local + remote, sent by the replica's sender;
Common_RegardlessSender = local + remote, any station may send.

Race-progress related events and their scope:

| Event | Type | Args | Notes |
|---|---|---|---|
| `Player_ChangeLap` (0x5bfc70e1) | **local** | player_idx, race_lap (-1..50) | emitted by `FUN_0035c760`; never leaves the process |
| `Player_ChangeTopPlayerLap` | local | player_idx, race_lap | |
| `Player_ForceAdaptSector` / `...Handle` | local | pos / path_id, point_id | re-seat a LapChecker |
| `RaceSeq_LapCheckerStarted` | local | player_idx | `FUN_00937b88` |
| `RaceSeq_GoalInfo` (0x47b1ddf2) | local | player_idx | "this player goaled", raised locally for local and remote karts |
| `RaceSeq_RetireRace` (0x7dde022f) | local | player_idx | |
| `Net_NoticeGoalRank` (0x6db1985f) | Common | aid, net_local_index, goal_rank 0..23, race_section_id | sender `FUN_00bfbf78` (only when mode field == 3) |
| `RaceSeq_GoalRankFixed` (0x03d346bd) | Common | player_idx, fixed_rank, race_section_id | handlers `FUN_00596600`, `FUN_006ce564`, `FUN_00a3195c` |
| `RaceSeq_GoalRankFixedLocal` (0xae42b7bf) | local | (player_idx, rank, frame%1000) | emitted from the referee result, `FUN_021264ec` |
| `RaceSeq_ForceFinish` / `RaceSeq_TimeUp` | Common | - | |
| `RaceSeq_RequestForceFinish`, `RaceSeq_BattleFinish` | Common_RegardlessSender | - | |
| `RaceSeq_ForceRetireForDistance`, `RaceSeq_LeaveRaceKeepConnect` | Common_RegardlessSender | player_idx | |
| `BattleEvent_Retire` (0x1b8ea4f5) | Common | player_idx, clock | battle elimination |
| `RaceSeq_SurvivalThroughSync` | Common | area id, player_idx, goal_clock | Knockout checkpoints |
| `RaceSeq_SurvivalThroughFixed` | Common | area id, player_idx, goal_rank | |
| `RaceSeq_SurvivalThroughSyncForServerReferee` | local | - | |
| `Obj_SectorThrough_Request` | Common | target_guid, game_frame, player_idx, 2 floats, bool | object gates, not lap |
| `GlobalSeq_SetBaseClock` | Common | clock u64 | race clock base |
| `Net_NoticeGlobalTimeOffset` | Common | time_offset f32 | GlobalTimeManager node |
| `GlobalSeq_StartCourseVote` | Common | 4 x (vote type, u32), goal_clock | vote |
| `GlobalSeq_ReplyCourseVote` | Common_RegardlessSender | aid, net_local_index, vote type, u32, bool | vote |
| `GlobalSeq_StartRoulette` | Common | aid, vote type, u32 | vote result |
| `GlobalSeq_SetupRaceFirst` / `GlobalSeq_SetupRace` | Common | course StringId, course_option, player_bitmap, ... | race setup |
| `GlobalSeq_SyncRacePlayerInfo` | Common | player_idx, driver, vehicle, sticker | |
| `GlobalSeq_SyncGroupComPoint` / `...LastRank` | Common | 22 x race_point / goal rank | CPU scores, host |
| `GlobalSeq_StartP2PSync`, `GlobalSeq_ReadySync`, `GlobalSeq_CommonSync_Ready` | local / C_RS / C_RS | player_idx (+id) | ready barriers |
| `Net_OnJoinSession`, `Net_ReqLogicalId`, `Gamesync_DynamicPlayerInfo` | InterProcess | | |
| `Net_StationDisconnected`, `Net_ChangeSequenceState`, `Net_ChangeStationReady` | local | aid | |
| Item events (`ItemEvent_*`, `ItemObj_*`, `ItemOwner_*`, `Vehicle_*`) | mixed | | ~150 formats, see `a5-netgyml.txt` |

Sequence nodes from `seq_nodes.tsv` (SendVoteElection, VoteCourse, SubmitVoteReport, SyncScore,
SyncRacePlayerInfo, SyncState, SyncTiming, GlobalTimeSyncSeq, P2PSync) are scene-graph states that
wait on / emit the events above; they define no wire formats of their own. Event constants found near
them: P2PSync -> `GlobalSeq_StartP2PSync` (`FUN_01a565d8`); SyncScore -> `GlobalSeq_SyncGroupComPointRaceEnd`,
`GlobalSeq_SyncGroupComLastRankRaceEnd`, `RaceSeq_StartSyncScoreLocal` (`FUN_01a82408`);
SyncRacePlayerInfo -> `GlobalSeq_SyncRacePlayerInfo` (`FUN_01a81ab8`, `FUN_01a81c1c`). The node
boundaries in the TSV are approximate, so this mapping is I.

Send cadence: not recovered statically. The live trace in NETCODE.md (~18 packets/s each way) is the
unreliable Player replica stream. I

### 2. Lap / checkpoint path

#### 2.1 LapChecker (V)

Class `LapChecker` (size 0x3088, vtable `0x32a9478`, heap label string `0x28f7dda`), created in the
kart controller ctor `FUN_00a7307c` (stored at controller `+0x30`) and shared with the race tracker
component (tracker `+0x18`).

| Offset | Meaning |
|---|---|
| +0x10 | config flags. `FUN_020de348` (tracker init) sets `0x10 | 0x4`; bit 0x20 = also post an error on a failed lap check; bit 0x8 = suspended |
| +0x14 | state flags. 0x1 = facing the wrong way; 0x2 = off the course graph; 0x4 = disabled; 0x20 = started (seated on a sector); 0x400 = strict lap check; 0x80 = final lap done; 0x8000 = lap refused. Reset value 0x2400 (`FUN_0012f170`) |
| +0x1c / +0x1e | current sector (u16 group, u16 index) |
| +0x28 | current progress (float): fraction of a lap + lap number; negative before the start line |
| +0x2c | best progress so far (float), reset to 0 |
| +0x30 / +0x31 | lap (s8, -1 before the line) / highest lap |
| +0x32 | checkpoint counter (s16) |
| +0x7c | 0x3000-byte bitmap of key sectors passed this lap |
| +0x307c | path set selector |

- Per-frame update: `FUN_0035d5e0(LapChecker, pos, front)`. It only takes a position and a direction.
- Lap increment: `FUN_00360bf8` (called through `FUN_003602c8`). With the strict flag it counts the key
  sectors passed and compares with the required count (`FUN_003610c0`) and the required checkpoint index
  (`FUN_00360f44`); on failure it sets 0x8000 and returns **without** incrementing when `(+0x10 & 0x26) != 0`
  (always true after tracker init). If `+0x10 & 0x20` it also posts error `0x8bd70a64` (`FUN_02624db4`) or
  `0x23b94dd8` (`FUN_02624e20`) through `(*0x33e7ee0)->vtbl+0x70`.
- Debug text of the component (`FUN_020de568`): "Sector: [%d,%d]", "Dist : %.5f(%d)[%d]", "ToGoal", "ToLap",
  "CheckP : %d >= %d", "CntNum : %s".

#### 2.2 Race tracker component (V)

Vtable `0x322b990` (race) and derived `0x322bbb0`; battle variants `0x322b5f8`, `0x322b7d0`; base `0x322b3d8`.

| Slot | Function | Role |
|---|---|---|
| 6 | `FUN_00911c1c` | network receive step (section 3.1) |
| 10 | `FUN_020de348` | init: reset LapChecker, set config flags |
| 11 | `FUN_020de3b4` | early update: seat the LapChecker on the nearest sector (`FUN_02630cc4`) |
| 12 | `FUN_00937b88` | start: emits `RaceSeq_LapCheckerStarted` |
| 15 | `FUN_0035b1c0` | main update |
| 51 | `FUN_0035c760` | emits `Player_ChangeLap` and `UIView_RaceInfo_Change` (type 5, lap+1) for the on-screen counter |

`FUN_0035b1c0` first calls `FUN_0035d5e0(tracker+0x18, tracker+0x44, tracker+0x38)` for **every** kart, with
no local/remote test in front of it. The local/remote flags only gate later blocks: `tracker+0x50 == 0`
gates the course-out / Lakitu logic, `tracker+0x51 == 0` gates "write my own race clock into goal_clock".
Then it compares `LapChecker+0x30` with the last lap it announced (`tracker+0x90`) and calls slot 51.

That `tracker+0x44` holds the replicated, interpolated position for a remote kart is I (it is the only
position input, and the Player replica carries nothing else that could drive it).

#### 2.3 Watchdog `FUN_0035a9a0` (V)

It is slot 5 of vtable `0x322ac60`, a state of the kart controller state machine built by `FUN_00a7307c`.
The branch that matters runs when the state's delegate is `0x20d43dc` (`FUN_020d43dc` contains the same
logic as a separate copy).

Preconditions, in order: a tracker exists; the network object `*(*0x32fd120 + 0x50)` reports its own
station in state 2 (vtbl +0x80/+0x88, `FUN_0224affc` / `FUN_0224b0c0`); `FUN_00656fb8() == 0`;
`*(*0x33e6b38 + 8) != 1`; the kart's player-table entry (0xd8 bytes each, table at
`**(*(*0x33d0fd0 + 0xc0) + 8) + 8`) has `+0xc4 >= 0`, `+0x50 >= 0`, `+0xd0 & 1`, `+0x54 >= 0`.
None of these is the online context `FUN_0045b24c`, the player map `ctx+0x28`, or a Gamesync doc.

Data sources: the kart's position (`FUN_021fb328`), and `LapChecker` `+0x14`, `+0x28`, `+0x2c` scaled by
`FUN_02630f54` (= path `+0x68`, the same factor used for the "ToGoal" debug distance).

| Reason id | Condition |
|---|---|
| `0x9b928162` | XY position has not moved for 0x708 (1800) frames |
| `0x5382e895` | progress has not grown by 75 units for 180 frames |
| `0x660ac5da` | `LapChecker+0x14 & 1` (wrong way), **or** `(best - current) * scale > 5.0` |

The reason is latched only while the strike counter `+0x1d0` is 0; the gap test runs last, so whenever
the gap exceeds 5 the latched id is `0x660ac5da`. The counter climbs to 0x2d0 (720 frames). First time:
`FUN_020d53c8` sends local event `0xa66067f2(player_idx)`, sets `+0x1ec` (armed for 0xe0f frames) and
restarts. Second time while armed: the state machine goes to state 9. Total about 24 s of continuous
violation. That state 9 is what posts the stored id to the error service is I.

Two situations produce exactly "lap shows 1/3 and the id is `0x660ac5da`":
1. The kart sits behind the start line. Lap is -1 (drawn as 1), current progress is negative, best is 0,
   so the gap is the distance to the line; more than 5 units trips it from the first frame.
2. The kart crosses the line but the strict lap check refuses the lap. Progress then drops by one whole
   lap against the best value, and the lap counter stays where it was.

Both are purely local to the client that owns the kart. Whether the check also runs for remote karts
depends on player-table fields `+0x50/+0x54/+0xd0` that were not decoded; that it is local-player-only is I.

#### 2.4 What could make a lap stay at 1 (I unless marked)

- The kart was not driven forward (an unfocused or unattended second client). Fits case 1 above.
- A kart skipped key sectors (teleport-like motion under heavy frame drops, or a remote kart with a
  starved position stream), so `FUN_00360bf8` refuses the lap. Mechanism V, occurrence I.
- LapChecker never seated (`+0x14 & 0x20` clear) or flagged off-course (0x2). Mechanism V.
- Not a cause: the online context, `ctx+0x28`, `ctx+0x8da0`, PlayerStatic/DPID/DPIS, `p2p_sid` / `p2p_lid`.
  No code on the lap path reads them. `p2p_sid`/`p2p_lid` are copied into the player map by
  `FUN_0049a5a0` only. V

### 3. Race finish

#### 3.1 Per-player finish flag and time (V, `FUN_00911c1c`)

Local kart (`tracker+0x51 == 0`): until it has goaled, `FUN_0035b1c0` writes the race time in ms
(clamped to 0x127690) into `tracker+0x110/+0x118`; these and `tracker+0x120` are the variables bound to
the replica elements `goal_clock` and `is_goal`.

Remote kart (`tracker+0x51 != 0` and the replica's station is not mine): `FUN_00911c1c` reads
`is_goal` (`FUN_0261e068(netComp, 0xa77a1b7c)`, value `**(elem+0x10)`) into `tracker+0x120` and, while the
flag is not yet latched on both sides, copies `goal_clock` into `tracker+0x110/+0x118`. It also reads bool
`#ac8f3b93` and calls `FUN_0211e078(G, &player_idx, flag)`, which sets or clears bit `player_idx` of `G+0x1c`.

`G = *(*(*0x33d2548 + 200) + 8) + 8` (getters `FUN_008ce860`, `FUN_002c705c`).

#### 3.2 `FUN_02141adc` (V)

It walks the race player list (`*(...+0xa0)` of the player table object, iterator `FUN_0039e27c`) and
returns 1 only if, for every listed player, bit `(entry+0x50) & 31` is set in the u32 at `G+0xc`
(accessor `FUN_00d19d54` = arg + 0xc). It is a **retired / out-of-race** mask, not a "finished" mask.

Writers of `G+0xc` (setter `FUN_0211e048(G, bit)`):

| Writer | Trigger |
|---|---|
| `FUN_02125fcc(seq, idx)` from `FUN_0086b558` | local event `RaceSeq_RetireRace(player_idx)` |
| `FUN_02125fcc` from `FUN_02128a0c` | `BattleEvent_Retire` (Common: battle elimination, arrives from the P2P event channel) |
| `FUN_006540f4` (two sites, bit = player count - 1) | sequence step 5, special modes |

Neighbour mask `G+0x10` (setter `FUN_0211e060`): set in `FUN_0086b558` on `RaceSeq_LeaveRaceKeepConnect(player_idx)`
(Common_RegardlessSender) and on the sequence's own stop path. Disconnects surface as local
`Net_StationDisconnected(aid)` (users: `FUN_0051846c`, `FUN_006cecdc`, `FUN_00a36994`, `FUN_00d3e6e4`, ...);
the chain from there to `RaceSeq_RetireRace` was not followed. `RaceSeq_RetireRace` emitters include
`FUN_00596a98` (on `RaceSeq_ForceRetireForDistance`), `FUN_020d4944`, `FUN_0079c184`, `FUN_02133ed8`.

Callers of `FUN_02141adc`: `FUN_02128568` (starts a 0x708-frame timer once everybody is out),
`FUN_00d1bf80`, `FUN_01a9af00`, `FUN_01a9ea04` (choose the result-page flow).

The local "who has goaled" mask is separate: `FUN_00d193e0` ORs bit `player_idx` into `seq+0x3c` on local
`RaceSeq_GoalInfo` and `FUN_00d19c80` compares popcounts with the playing set.

#### 3.3 Referee report and result (V)

Per-match context `ctx = FUN_0045b24c(-1)`:
- `ctx+0xe8`: ring of result entries, 0x168 bytes each: `+0x00` 16-byte request uuid, `+0x10` `request_id`
  string, `+0xa0` `status`, `+0xa4` 24 x s32 `result` from the server, `+0x104` 24 x s32 local report list (-1 = empty).
- `ctx+0x8f88`: list of pending reports (key + field set); `ctx+0x8f78`: write in flight.

Building the report:
- `FUN_021275a8` / `FUN_02129138` (race-sequence vtable slots, tables `0x32324b0`, `0x3232630`, ...) call
  `FUN_01e3865c(ctx, entry, &player_idx)` if that index is not yet in the entry's local list. It appends the
  index and sets field **`result`** = the whole local list so far, i.e. player indices in the order this
  client saw them finish.
- `FUN_02127538` / `FUN_01e38898` set field **`retired` = true**.

Sending: per frame `FUN_00498404` takes a pending report whose entry exists and has `status == 0` and calls
`0x1e393c0` -> `FUN_01e393dc`, which writes
`<gs>/@@RefereeReport/<request uuid>/User/<my ussid>` (ussid string from `ctx+0x8da0 -> +0x10`).
Gates: `*(*(*0x33e7ed8 + 0xd8) + 8) == 0`, session handle `FUN_01a9637c(...)` non-null, `ctx+0x8da0` non-null.

Consuming the result: `FUN_00498404` parses each `@@RefereeResult` doc: `request_id`, `status`, `result`
(up to 24 ints into `+0xa4`), and when `status == 1` a map `ratings` {ussid string: double} stored as a float
at player-map node `+0x8e0`. `FUN_021264ec` then, for each kart waiting for its rank, searches
`result[0..playerCount)` for the kart's player index; **the array position is the rank**. It emits local
`RaceSeq_GoalRankFixedLocal(player_idx, rank, frame % 1000)`. `FUN_02126ad4` is the same for a second mode.

So the report requires an existing `@@RefereeResult` entry with `status == 0`, which requires the
`@@RefereeRequest` round trip, which requires the online context (`ctx+0x8da0`, player map). This is the
part that was missing in the earlier test, and it explains P1 auto-driving forever after the finish.

### 4. Server participation

| Doc / RPC | Content | Role in progress sync |
|---|---|---|
| `PlayerStatic` (`FUN_0049952c`, `FUN_0049a5a0`) | name, p2p_sid, p2p_lid, vehicle, driver, greeting, rating fields, play_history_key | identity only |
| `DPIS` / `DPID` (`FUN_0049952c`) | carries the InterProcess event `Gamesync_DynamicPlayerInfo` = one Vec3f `pos` (hash 0x15e55814 used in `FUN_0049a5a0`) | free-roam position hint; no lap, no timing |
| `Companion` | companion_state, my_group_id, course_sid, track_id, leader ids, delegation_token | grouping |
| `Global/Session` (`FUN_00498a80`) | client writes `short_session_id`; reads `short_session_id` -> `ctx+0x48`, `global_time_offset` -> `ctx+0xd8`, `rule` -> `ctx+0xdc`, session flags -> `ctx+0xe0..0xe4` | world clock offset and rule set; not race progress |
| `@@KeepAlive` (`FUN_00497ef4`) | heartbeat | none |
| `@@RefereeRequest` / `@@RefereeResult` / `@@RefereeReport` | section 3.3 | final order and ratings only |
| CreateRound RPC | not found on any path examined here | not assessed |

`FUN_00a48e18` does not read the Gamesync `Global` document: it resolves a scope named "Global" in a
parameter tree (string compare against `0x28bcfc2` inside a resource walk). Consumers of `ctx+0xd8` were
not traced; the P2P event `Net_NoticeGlobalTimeOffset(time_offset)` belongs to the GlobalTimeManager node
(`FUN_01a43fcc`). I

Race clock: `GlobalSeq_SetBaseClock(clock)` (Common event) plus Pia `clone::ClockProtocol`. P2P only. V (schema), I (usage)

### 5. Conclusions

**What the server must do for race progress to sync: nothing.** Position, lap, checkpoint, finish flag and
finish time are P2P (Player replica + local LapChecker). The server only has to (a) keep the Pia mesh
alive (already working) and (b) run the referee so the race can end:
1. Deliver the `__us` member set so the client fills `ctx+0x28` / `ctx+0x8da0` (already known).
2. On `@@RefereeRequest/<...>/Request/<key>`, create `@@RefereeResult/<key>` with
   `request_id = <key>`, `status = 0`, `result = []`.
3. Read each `@@RefereeReport/<uuid>/User/<ussid>` `{result:[player_idx...], retired}` and keep
   `@@RefereeResult.result` updated as an array where index = rank and value = player index (merging the
   clients' lists; with two honest clients, the longest reported list is enough).
4. When everyone has finished or retired, set `status = 1` and add `ratings` {ussid: number}.
   Field names and layouts are V; the exact timing the client expects for steps 3-4 is I.

**Most likely cause of "P2 stuck at lap 1/3" with `0x660ac5da`:** P2's own kart was not making forward
progress on P2's client (it sat behind the start line, or it was more than 5 units behind its own best
progress), so P2's local anti-stall watchdog fired after about 24 s. With one person driving two clients on
one PC this is the expected result for the client that is not being driven. It is not a sync failure and
not caused by the missing online context: the watchdog and the lap path read only the kart's position and
its LapChecker. The second symptom of that run (P1 auto-driving forever, later `0xf6378925`) is the part
that the missing context and referee explain.

A cheap live check: on the stuck client read `LapChecker` (`tracker+0x18`) `+0x14`, `+0x28`, `+0x2c`, `+0x30`
and the controller's `+0x1cc` / `+0x1d0`. Lap -1 with negative `+0x28` means "never crossed the line";
flag 0x8000 means "lap refused by the key-sector check".

---

## Server actions, prioritized

Each item names the section that carries the evidence. "V" = the client behaviour behind it is verified in code or
logs; "I" = the server-side rule is inferred.

### (a) Returning to the room after a match

1. **Referee: start empty, accumulate, finalize only when complete** (section 1.8). V for the client side.
   - On the first `@@RefereeRequest/<ussid>/Request/<key>`: publish `@@RefereeResult/<uuid>`
     `{request_id: <key>, status: 0, result: []}`. Keep `total_player_count`, `mode`, `dropout_rank`.
   - On each `@@RefereeReport/<uuid>/User/<ussid>` write: merge its `result` list into the match list (append
     unseen indices in reported order) and republish with status 0.
   - When the list has `total_player_count` entries: publish status 1 with the full `result` and
     `ratings = {"<ussid>": number}`.
   - Add a fallback so a match cannot hang: member leaves or `retired` is set -> stop waiting for that player;
     no new report for ~40 s -> finalize. (I)
   - Survival: one result per `Surv_...` request key.
2. **Apply the write field mask** (section 2, finding 1). Merge masked fields into the stored document instead of
   replacing it; honour `["*"]` (replace), empty mask (touch) and the create-if-absent precondition. This should
   restore `p2p_sid` / `p2p_lid` in `PlayerStatic` and fill `users` / `ussid_map` in the request. V mechanism, I effect.
3. **Keep the session untouched between races** (sections 1.6, 3 (c)). No new ticket, no GameSession change; keep
   every KeepUserSession stream, listener and the `__us` member set (`st = 2`) as they are. I.
4. **Re-test and check, in order:** request has non-empty `users`; both clients report `[a]` then `[a, b]`; status 1
   with both indices; results screen; free roam; next vote. If a client still leaves after status 1, capture the
   caller of `main+0x223a848`.

### (b) Friend rooms

5. **Fix `Member` field order** in every GameSession we send: 1 = userSession resource, 2 = user (section 3, C1). V.
6. **Room create:** `CreateGameSessionCreationTicket` (config `Private24`, password in `GameSession.6`, attributes
   in `.11`) -> `TrackGameSessionCreationTicket` ending with state 2 + MatchedPlayers + GameSession;
   `CancelGameSessionCreationTicket` (section 3.2). V shapes.
7. **Room ID:** `CreateGameSessionShortAlias` (server allocates the 6-digit id, idempotent per session) and
   `GetGameSessionShortAlias`; `GetGameSession`; `JoinGameSession` (password check, reply MatchedPlayers +
   GameSession, field 6 = `"*"` when protected, attribute `rule` on the session) (sections 3.2-3.4, A 1.4). V shapes,
   I for the status codes behind "wrong password" / "full".
8. **Friend list:** the list starts from `nn::friends::GetFriendList`, so the probe/runtime must return the seat
   roster there with a stable 64-bit account id per launcher name. Then: `SubscribeFriendUsers` groups keyed by that
   id in hex; `KeepAlive` answered with `{1: {Duration, Duration}}` so the client starts publishing; store each
   player's `SessionId` / `DriverId` / `StickerId`; push `Presence` (state 1 + attributes) on `SubscribePresences`;
   answer `BatchGetGameSessions` for the published session ids (section A part 2). V shapes, I for heartbeat needs.

### (c) Other things blocking or degrading real play

9. **Leave handling:** when a KeepUserSession stream ends, run that user's stored deferments and deletes, remove its
   `__us` and `__pus` documents, push **kind 3 (REMOVED)** to listeners, free the seat, drop the room and alias when
   empty (sections 2 finding 3, 3 (b)). V wire shapes, I obligation.
10. **Ticket streams:** always end `TrackMatchmakingTicket` with a final state (3 matched, 4 timeout, 5 failed with
    field 3, 6 cancelled after `CancelMatchmakingTicket`) and OK trailers; never send state 7 (section 3.1). V.
11. **Knockout Tour:** accept config `Survival24` like `Normal24` / `Battle24`; referee handles the per-round
    requests (sections A 1.3, 1.7). V names, I that nothing else differs.
12. **Document fetch statuses:** answer `GetDocument` for missing documents with a tolerated status (NOT_FOUND
    class), never UNIMPLEMENTED / INTERNAL / UNAVAILABLE, which raise a priority-1 error (section 4.2). V.
13. **Optional:** pre-author `Global/Session {short_session_id}` and `DPID/d`; neither has been shown to block
    (section 2). I.

### Tools left for follow-up work

- `re-map\ainb.py <Graph>.ainb`: dumps a scene graph (nodes, pins, transitions) from `scratch\game\romfs\AI`.
- `re-map\rt.py callers|refs|str|fstr|fn|dis`: call-site, data-reference and string index over `main.bin`.
- `re-map\dc.ps1 <out.c> entry:<hex> ...`: Ghidra decompile, several functions per call, safe to run concurrently.
  (`decomp.ps1` in the jobs tmp folder only decompiles its first address when run with `-File`.)
