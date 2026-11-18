# Plan: MKWMP on game 1.8.0 (with mkw-recomp 0.1.13)

Status 2026-11-17. This plan builds on three static analyses (nothing has been run in the game yet):
- `RECOMP-0.1.13.md`: the runtime bump needs no probe change. The launcher accepts 0.1.13 (MACBOOK 938ffda).
- `GAME-1.8.0-ADDRESSES.md`: every probe guest constant mapped 1.0.0 -> 1.8.0. All 15 functional constants are exact.
- `GAME-1.8.0-PROTOCOL.md`: NPLN 1.33 -> 1.47, Pia 6.37.3 -> 7.3.0, SDK 19.3.3 -> 21.4.0. Lobby and probe gaps.

Key constraint: **1.0.0 and 1.8.0 players can never meet.** The Pia header version byte went 0x0F -> 0x10, and every receive
path drops a mismatch. So we support both builds side by side, with separate lobbies, rather than switching.

Owners: **W** = WINDOWS-MKWMP (probe, RE, docs). **M** = MACBOOK-MKWMP (rust/ lobby, seat, launcher, deploys, releases).

## Phase 0: safety, done
- W `5ca8d51`: the probe's `game_build()` reads main+4 (MOD0) and the GNU build-id note. The voice UI hooks and
  `profile_dispatch` install only on 1.0.0, whatever started mkw.exe.
- M `938ffda`: the launcher refuses PLAY when `setup-state.json` says a game version other than 1.0.0, and accepts recomp 0.1.13.

## Phase 1: probe runs on 1.8.0, offline-safe (W)
1. **Per-build tables.** Add `struct game_addrs` (`GAME-1.8.0-ADDRESSES.md` §6), with one row for 1.0.0 and one for 1.8.0,
   selected by `game_build()`. Move every `0x70...` literal in `npln.c` and every `voice_ui.c` enum into it. A zero
   diagnostic field turns that trace off. An unknown build leaves every version-specific hook off.
   - Functional rows for 1.8.0: gRPC wait gate `0x700111232c`; error post `0x7002564cd0`; `VT_ONLINE_PAUSE 0x3622528`;
     `FN_PLATE_TEXT 0x2505f10`; page `PLATES 0x778`, `PLATE_SIZE 0x170`, `ROWS 26`, `COUNT 0x4628`, `ENTRIES 0x4630`.
   - `ROWS` becomes per-build, and `g.shown[]` is sized to the maximum (26).
   - Replace the `memmove` constant `0x70046966a0` with `ldr_symbol("memmove")`. It is an sdk function, and sdk moved to 0x7004400000.
   - Before hooking, check the install-time guard values in §3 (vtable slot targets, first instruction words). On a mismatch,
     log it and skip the hook.
   - Keep `MKWMP_NO_IDLE_KICK` refused on 1.8.0 until the kart state field at +0x40 is confirmed, because it writes guest memory.
2. **Pia 7.3 ping job** (`GAME-1.8.0-PROTOCOL.md` B1). Add three things:
   - an override for the new `nn::nifm::GetCurrentIpConfigInfo(InAddr*, InAddr*, InAddr*, InAddr*, InAddr*)` overload,
     beside `npln.c:1600`;
   - an override for `nn::socket::Recv(int, void*, ulong, MsgFlag)`;
   - a policy for `Socket(AF_INET, SOCK_RAW, IPPROTO_ICMP)`, which `ov_pia_socket` at `npln.c:821` rejects today. Start by
     returning a clean error code and see whether Pia tolerates it; otherwise emulate the echo reply on our relay.

   Check the 64-slot override budget (`npln.c:1681-1683`).
3. **subsdk0 (`movie`)** imports `nn::socket::Close`, `GetLastError`, `EventFd*` and `nn::fs`. Make sure the probe's
   socket overrides pass through non-NPLN handles unchanged (no fd confusion).
4. Build, run the native tests and `tools/recomp/check_release.py`. Offline smoke test on 1.8.0: title -> menus ->
   `mkwmp.log` shows "game build: 1.8.0", the hooks installed and no fault.

**Status 2026-11-17: items 1-4 are in the probe (static; the smoke test is still to run).** What differs from the list:
- The table is `native/probe/src/gameaddrs.c` (`struct game_addrs`, rows 1.0.0 and 1.8.0). At the game's first gated SDK
  call the probe checks the six guards read-only and logs `game build 1.8.0: address table row found, 6 of 6 guards ok`;
  the hooks themselves still go in when Online Play opens (`voice-ui: ... hooks installed (game 1.8.0, 26 plates)`,
  `game build 1.8.0: version-specific hooks on`).
- Off on 1.8.0 (zero fields): `MKWMP_TRACE_ROOM`, `MKWMP_TRACE_KART`, `MKWMP_NO_IDLE_KICK` (refused, logged), the
  Proton default skip of `0x9b928162` (`MKWMP_SKIP_ERROR=9b928162` still works), two trace ranges.
- The runtime's 64 override slots were all taken. `GetCurrentIpConfigInfo(InAddr*)` got the slot of
  `nn::socket::FreeAddrInfo(AddrInfo*)`: typed `GetAddrInfo` results are now ring entries and the runtime's stock
  `FreeAddrInfo` (a bare `ret` in 0.1.10, 0.1.12, 0.1.13; `check_release.py` checks it) serves the name.
- `nn::socket::Recv` has a tested handler (`ov_pia_recv`) but **no slot**: the raw ICMP socket is refused
  (`EPROTONOSUPPORT`), and with it refused nothing reaches `Recv` (its one caller is the ICMP receive of `PingJob`,
  which never starts while the ping socket's open count is 0; the transport start discards the failed open). Evidence
  in the comment above `ov_pia_socket`. To register it later, another override has to give up its slot.
- subsdk0 (`movie`): calls from that module to the gated overrides (`Close`, `GetLastError`, `EventFd*`) always get the
  runtime's stock handlers (`gate_call`, by return address), so its task queue never holds a descriptor of the probe.

## Phase 2: lobby speaks 1.8.0 (M; items in `GAME-1.8.0-PROTOCOL.md` §6-7)
1. **Version key** on Ticket/Lobby (`lobbies.rs:102, 398, 403`; `msgs.rs:75-84`) and on friend rooms (`msgs.rs:211-220`,
   `social.rs:44-57`). 1.8.0 = the Player has `npln_user_id`; also log `rom_id` ("cv%d") so a live capture confirms it.
2. **Referee:** send `user_session_count` in `@@RefereeResult`, and accept `course_id`, `clutch_id` and `result_order`.
3. **BulkGet:** defaults for `RaceCount`, `RatingDisplay`, `RatingCoefficient`, `DriverId`, `StickerId`.
4. **New RPCs:** empty/OK stubs for Appconfig (3), Leaderboard (2), Ugcstore (1). Log every call, because Appconfig may gate the online flow.
5. **Modes:** reject `Clutch24` and `ClutchSurvival24` until they are captured. Accept `pusa` in user-session docs.
6. Deploy to a test lobby first (self-hosted `mkwmp host`), not to prod.

## Phase 3: first live run on 1.8.0 (user clicks, W drives logs)
Order: PC (1.8.0) alone -> PC + PC (`scratch/game` + `scratch/game-p2`, both on 1.8.0) -> PC + Deck.
1. Sign-in, then Online Play. Check for Appconfig calls, the 1.8.0 `rom_id`, and Player attributes with `npln_user_id`.
2. Matchmade race (`Normal24`) to the finish: Pia mesh (7.3 STUN/TURN rework, NAT probe kinds 101-103), referee result,
   ratings, back to the room.
3. Battle (`Battle24`, `Survival24`), and friend room via Room ID (keypad), voice UI on the pause menu with 26 plates.
4. Capture and diff the new document keys (PlayerStatic `sticker`/`su_ussid`/`driver_id_for_clutch`, Companion
   `my_follow_ussid`/`my_sub_ussid`, the `Swarm` collection), then decide whether the lobby must fill them.
5. Error ids: confirm `0x9b928162` (Deck end-of-round watchdog) and `0x606314c9` (referee timeout) behave the same, and
   re-enable the Proton default skip on 1.8.0 once the error-post row is proven.
6. Mixed test: a 1.0.0 client and a 1.8.0 client searching at the same time must land in different lobbies.

### Phase 3 log (2026-11-18, PC alone, mkw-recomp 0.1.13, MACBOOK LAN host on branch game-180)
- Offline: build detected, 6 of 6 guards ok, full race, clean quit. 1.8.0 makes **no boot nifm sign-in**, so the first
  Online Play click now activates the probe (`boot_signin = 0`, 4f393ea). Started without `--play`, the start screen
  holds `mkwmp.log`, so the game process writes `mkwmp-game.log`.
- Online, first try: the game crawled at 2-30% speed and froze on the Online menu. Two probe causes, both fixed:
  1. `ldr_symbol` (a linear strcmp scan) ran per call in the hot clock/cond overrides. Now cached (e3bb742).
  2. 1.8.0's SDK `clock_gettime` fails for clocks 0 and 1 under mkw-recomp: `nntimeStartup` re-points the clock sources
     at nn::time Timespec functions whose shared memory the runtime never sets up. SDK timed waits returned EAGAIN at
     once and gRPC's timer thread spun. The probe restores the SDK tick source in the monotonic slot (279d2ae; log
     "tick source restored, timed waits can block").
- Online, after the fix: smooth. Sign-in, CreateMatchmakingTicket (`Normal24`, lobby detects game 1.8.0, **rom_id
  "cv13"**; 1.0.0 is "cv1"), 1.8.0 lobby created, free roam while waiting. No Appconfig call. New path
  `LeaderboardService/UpdateHolder` (OK/empty is accepted). No gamesync/Pia while alone, as expected for a lobby still gathering.
- Still open: SDK-internal CLOCK_REALTIME stays broken (`time()`, `gettimeofday`; gRPC's realtime goes through the
  probe's override and is fine). Next: two-player race (Pia 7.3 mesh, ping-socket refusal, referee).

### Phase 3 log, two players (2026-11-18, PC + Steam Deck under Proton, LAN host)
- Deck install `~/mkw-180`: 1.0.0 romfs hardlinked plus the 8295 changed files (`ROM\update` on the PC), the PC-built
  1.8.0 mkwgame.dll, and a Linux game-180 launcher; Steam shortcut appid 2800733737 swapped to it (backup
  `shortcuts.vdf.bak-180-20261118-102620`).
- Matchmade race: both clients got **0x884b839c** 0.27 s into Gamesync. Cause: a new 1.8.0 seat check (`has_seat`,
  main+0x20fa450) reads GameSession field 2 (max players) from the matchmaking assignment, which our lobby left
  out. Fix (MACBOOK b566022, 1.8.0 lobbies only): fields 2/3 in the assignment's GameSession. After it: Gamesync, ICE,
  Appconfig FindApplicationConfig (empty reply accepted), Pia 7.3 P2P PC<->Deck (ping socket refused, no harm),
  referee open with the new `clutch_id`.
- Mid-race **0x660ac5da** on a kart circling without course progress: the game's own anti-stall watchdog (same as
  1.0.0), legitimate.
- Friend room (Private24 create, BatchGet, JoinGameSession), battle: Reports, then the final @@RefereeResult with
  `user_session_count` accepted by both. The Deck left each battle with **0x9b928162** (the known Proton-only
  end-of-round kart watchdog), so the Proton default skip is now on for 1.8.0 too (86f00ca). Battle 3: both stayed.
- Open: a 324 B Pia UDP send to 192.168.68.53:34343 that nothing serves (matters for internet play, not LAN); the Appconfig
  contents; Clutch modes; the mixed 1.0.0 + 1.8.0 separation test; SDK-internal CLOCK_REALTIME.

## Phase 4: release (M, with the user's go)
- Launcher: `recomp::GAMES = ["1.0.0", "1.8.0"]`. Show the game version in the window and the bug report.
- Lobby deploy to mkwmp-sao-1. README/site: "game 1.0.0 or 1.8.0; players only meet others on the same version".
- Release notes, signed SHA256SUMS, new probe hash.

## Later: Clutch modes
Capacity and referee rules for `Clutch24` / `ClutchSurvival24` (mode 7, matchmaking config at 0x20ff768) need a capture
from real play, or more RE of the referee request's `clutch_id`.

## Risks
- NPLN message wire layouts are not proven unchanged; the serializer diff was inconclusive. The first live run is the test.
- Pia 7.3's reworked STUN/TURN may need changes to `turn_relay` / the Rust relay.
- Six diagnostic traces have no 1.8.0 mapping (§5 of the address report), so we would debug with fewer traces until they are ported.

## Future game updates
`tools/re/game180/fmatch.py` + `sigport.py` port `probe-addrs-100.txt` to a new exefs (140,864 of 195,882 functions
matched on 1.0.0 -> 1.8.0). Add a `game_addrs` row and a `game_build()` entry. Recomp bumps use the `bump-mkw-recomp`
skill.
