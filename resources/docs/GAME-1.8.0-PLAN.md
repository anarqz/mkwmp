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
