# Testing the Rust lobby (official server) on the LAN

The Rust lobby (`mkwmp lobby`, crates `rust/gateway` + `rust/lobby`) replaces the Python reference
(`resources/python-lobby/mkwmp_server.py` + `turn_relay.py` + `pia_capture.py --reply`) for real play. Python stays for
probe development and protocol experiments. Both speak the same protocol; never run both at once
(same ports).

## Start (this PC, P1 = PC, P2 = Steam Deck)

```powershell
cd C:\Users\alcmo\Projects\mkwmp
cargo build --release -p mkwmp --manifest-path rust\Cargo.toml
$env:RUST_LOG = "info,lobby=debug"            # per-call lines; "info" for quiet
rust\target\release\mkwmp.exe lobby resources\config\lan.toml 2>&1 | Tee-Object scratch\run-rust1.log
```

`resources/config/lan.toml`: binds 0.0.0.0, advertises 192.168.68.238, TURN relays on 49152-49407, the
capture certificate, no seat tickets required (the probe's dev users P1/P2 work), token secret in
`scratch/keys/lobby-token-secret` (restarting the lobby keeps running games recognised).

The clients are set up exactly as for the Python server (same host/port 8905): nothing changes on
the PC or the Deck.

## What to look for in the log

| Step | Log line (target `lobby`) |
|---|---|
| sign-in | `signed in user=... number=N` |
| search | `ticket created ... mode=Battle24` then `lobby created` (first player) |
| match | `lobby assigned ... players=2` and `assignment sent` (10 s after the second player: gather wait) |
| referee | `referee ... opened=true`, then `final_=true` once both reported |
| leave | `seat left ... reason="session closed"`, `lobby dropped (empty)` |
| rooms | `friend room created`, `room id ... room_id=NNNNNN`, `joined by id` |
| TURN | gateway `TURN refresh from ...` (tells us whether the game refreshes allocations) |

## Test plan (in order)

1. **Battle, two players** (same as last night's milestone): match, battle, results on both, back to
   free roam, a second battle. Expect the same behaviour as the Python run.
2. **Leave and search again** (last night's "COIN RACE" case): after a match, P2 quits to the menu and
   searches again. Expected now: P2's seat leaves the old lobby (P1 sees P2 removed), P2 lands in a new
   gathering lobby; when P1 also searches again they are matched together in a fresh lobby.
   Then the experiment the user asked for: set `rejoin = true` under `[lobby.policy]`, restart, and
   repeat. P2 should now land back in P1's lobby (free roam between events) instead of a new one.
   If the game errors on either side, that is MULTI-LOBBY E2 failing: set it back to false.
3. **Race** to the finish line (finish order instead of a timer).
4. **Friend room**: P1 Online -> Create Room (with and without a password); read the Room ID; P2 enters
   it (Search by Room ID). Then a race in the room.
5. **Friends list**: needs the new probe (system friend list from the launcher) and the launcher's
   `/local/v1/friends` (mutual launcher friends who are online). P2 should appear under ONLINE ->
   FRIENDS on P1 and be joinable while in a room.

Switching back to Python for a probe experiment: stop `mkwmp`, then start the three Python processes
as before (`resources/docs/NETCODE.md`).

## Known differences from the Python server

- Lobbies are per mode and unlimited; documents, listeners and the referee are per lobby (the Python
  server had one global store, cleared per match).
- A gathering lobby waits 10 s after its second player before it is assigned, to collect more players
  (`[lobby.policy] gather_secs`).
- `__gs/f maxu` is the lobby capacity (24) instead of 2 (unverified with the game). If the match
  fails right after the assignment, this is the first suspect: set `maxu = 2` under
  `[lobby.policy]` and retry.
- A player whose session ends is removed from the lobby with Gamesync REMOVED changes (the Python
  server never removed anyone).
- PresenceService KeepAlive is acked; friend users and presences are served (Python sent empty streams).
