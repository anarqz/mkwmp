# 0x9b928162 on the loser at the end of a friend-room match

2026-11-15. Logs: `scratch/raceend-9b92/`. Build and conventions as in RE-MAP.md and SYNCSTATE-RE.md (image
offsets in `scratch/main.bin`; V = read in code or a log, I = inferred). Decompiles: `scratch/syncstate-re/re1.c`
.. `re7.c`. Nothing under `rust/` or `native/` was changed.

## 1. Short answers

1. **Producer: `FUN_0035ce38`** (I, from timing and code; no runtime view). It is the only producer that can
   fire at the instant a match ends for a player. It exists only for play modes 5 and 6 (the two Battle
   types), so this attribution **assumes the room was running a Battle**. If it was a VS race, say so: then
   this producer cannot fire and section 5 lists what is left.
2. **A "move" is not controller input.** It is one frame in which the kart's stored position changed by at
   least 0.1 unit since the last counted sample. No `nn::hid` state, no replica, no Steam Input or SDL mapping
   is involved in this producer. V
3. **The check:** when the local player's battle ends, more than 3000 frames after the race object was reset
   and with fewer than 20 counted moves, the kart controller stores `0x9b928162` and goes straight to its
   error state. No strikes, no rescue, no grace time. V
4. A player who drives for a whole battle counts thousands of moves, so on the Deck **the counter did not
   run** (or was reset just before the end). Why is not proven; section 4 gives the three ways the code allows
   and the trace that tells them apart.
5. **Room vs public:** nothing in this path depends on the room, the referee, ratings or `rule`. The same id
   at 0:01 of a public coin battle on the Deck (memory note) is the same check at the end of a battle. The
   referee side of this run was correct (section 3).

## 2. Code (V)

Kart controller state machine (built in `FUN_00a7307c`): state 5 = racing, enter `FUN_020d4384`, update
`FUN_020d43dc` (also inlined in the dispatcher `FUN_0035a9a0`); 6 = finished, 7 = out, 9 = error
(`FUN_020d4ab0` posts `controller+0x1cc`).

Racing update, each frame, with `rule = controller+0xb0`:

    rule->vt[0x78]()
    if rule+0x74 (this player's match is over):   FUN_0035ce38() ? state 9 : state 6
    else if rule->vt[200]():                      FUN_0035ce38() ? state 9 : state 7
    else:                                         watchdog / move counter (below)

Move counter (`FUN_020d43dc` 0x20d4614..0x20d4644, inlined copy 0x35ae60..0x35aec0), only in modes 5 and 6
(`*(root+0x1c) - 5 < 2`, `root` = the player table `**(*(*0x33d0fd0+0xc0)+8)+8`):

    p = FUN_021fb328(*(kart+0x70))        // *(*0x32f2850 + 0x78)[idx] + 0x120, idx = *(kart+0x70)+0x18;
                                          // an idx out of range reads a shared default entry
    if (p[0]-ctrl+0x1d4)^2 + (p[1]-ctrl+0x1d8)^2 >= 0.01:
        ctrl+0x1d4 = p[0]; ctrl+0x1d8 = p[1]; ctrl+0x1e8 += 1

It runs only when all of these hold: `ctrl+0x1fe == 0`; local station is a session member in state 2
(`FUN_005b04a8`); `FUN_00656fb8() == 0`; `*(*0x33e6b38+8) != 1`; the kart's player index is local
(`FUN_00cfb8bc >= 0`); player-table entry has `+0x50 >= 0`, `+0xd0 & 1`, `+0x54 >= 0`; the kart lookup
succeeds and its state is not 7..11.

`FUN_0035ce38` (0x35ce38..0x35d0a8): the same gates **except `ctrl+0x1fe`**, plus kart flag `+0x110` bit 5
clear; then `if (ctrl+0xb8 > 3000 && ctrl+0x1e8 < 20) { ctrl+0x1cc = 0x9b928162; return 1; }`.

- `ctrl+0x1e8` is zeroed only by the racing-state enter `FUN_020d4384` (the 8-byte store at `+0x1e5`).
- `ctrl+0xb8` is zeroed by the race reset `FUN_020d4b94` (called from the controller's event handler
  `FUN_00a3195c` at 0xa3213c), not by a state change; its increment site was not located.
- `ctrl+0x1fe` is written only at 0xa32514 in `FUN_00a3195c`, on event `0xba6fec95` (bool): true -> 1,
  false -> `vt[0x48]()` and 0. The only sender is the enter of `AIRaceSeqFinalize` (`FUN_00b69b08`, vtable
  0x317aa50), with its bool parameter; true is the "back to free roam" branch.

The other producers of this id (`FUN_020d43dc`: position unchanged for 0x708 frames, or `kart+0x53c > 0x708`
with `kartObj+0x191` bit 7 clear) run only outside modes 5 and 6 and need two 12 s strike windows with a
rescue in between (RE-MAP 5), so they cannot coincide with the end of a match.

## 3. What the logs show (V)

- Deck probe: `ReadyVoteSetter ... a=0` at 86160 ms (leaves the room sequence for the match), error at
  181745 ms, no other event in between. PC: no error.
- Lobby, 19:55:46.80 to 19:55:47.36: Deck writes 70 B and 83 B, then its `@@RefereeReport/.../User/1` (104 B)
  at .835; PC's `User/2` report (104 B) at .846; PC's second report (108 B) at 47.362; the lobby publishes the
  final result at 47.364. Both first reports have the same size, so they carry the same list; the PC then
  reports one more entry. The Deck's error is at the same instant as its first report (leave completes at
  184936 ms, connection closed 19:55:49.95).
- So the match ended normally for both, the referee answered within 2 ms of the last report, and the error was
  raised on the Deck by the end-of-match check itself, before any result could matter. Nothing in
  `@@RefereeResult` (order, `rating_enabled`, `dropout_rank`) is read by `FUN_0035ce38`.
- The Deck ran at 0.92 to 0.97x during the match and the Pia link was steady (`Pia UDP last 60s` lines).

## 4. Why the counter was below 20: the three ways the code allows

| # | Condition | What it would look like in the trace below |
|---|---|---|
| A | `ctrl+0x1fe == 1` for the whole battle: the "free roam" flag from `AIRaceSeqFinalize(true)` was never cleared for this kart. The counter is gated on it, the final check is not | `fe=1`, `moves` stays 0 |
| B | the position entry does not follow the kart (index `*(kart+0x70)+0x18` out of range, so the shared default entry is read) | `fe=0`, `moves` 0 or 1, `pos` constant while driving |
| C | the racing state was re-entered shortly before the end (`FUN_020d4384` zeroes the counter, `+0xb8` keeps running) | `moves` large, then a drop to 0 and `state` leaving and re-entering 5 within the last second |

A is the only one with a code asymmetry between counter and check, so it is the first suspect. I

## 5. If the room was running a VS race, not a Battle

Then `FUN_0035ce38` returns 0 and the id can only come from the strike path: `kart+0x53c > 0x708` (frames
without input as the kart object sees it) or 30 s without position change, sustained through a rescue and
another 12 s. That would put a rescue (event `0xa66067f2`) on screen about 12 s before the error, and it does
not line up with the end of the match except by chance. The same trace settles it (`cc`, `strikes`).

## 6. Next step (proposal, not applied)

Trace and optional guard in the probe, `native/probe/src/npln.c`, `profile_dispatch`, before
`original_dispatch(c)`. `0x70020d43dc` is the state-table callback and `0x700035a9a0` the dispatcher that
inlines it; hook both, `x0` = controller.

```c
if (target == 0x70020d43dcull || target == 0x700035a9a0ull)
    trace_kart_watchdog((uint8_t *)(uintptr_t)c->x[0]);
```
```c
// RACEEND-RE.md: battle idle check (FUN_0035ce38). MKWMP_NO_IDLE_KICK=1 keeps the move count above its limit.
static void trace_kart_watchdog(uint8_t *ctrl) {
    if (!guest_readable(ctrl, 0x200)) return;
    const uint8_t *info = *(const uint8_t **)(ctrl + 0x28);
    int32_t player = -1;
    if (info && guest_readable(info, 0x40)) memcpy(&player, info + 0x3c, 4);
    if (player < 0) return;
    uint32_t state, frames, cc, strikes; int32_t moves; float pos[2];
    memcpy(&state, ctrl + 0x40, 4); memcpy(&frames, ctrl + 0xb8, 4); memcpy(&cc, ctrl + 0x1cc, 4);
    memcpy(&strikes, ctrl + 0x1d0, 4); memcpy(&moves, ctrl + 0x1e8, 4); memcpy(pos, ctrl + 0x1d4, 8);
    if (g_no_idle_kick && state == 5 && frames > 2900 && moves < 20) {
        moves = 20;
        memcpy(ctrl + 0x1e8, &moves, 4);
        static volatile LONG said;
        if (InterlockedIncrement(&said) <= 5) mkwmp_log("kart watchdog: move count raised (player %d)", player);
    }
    static uint32_t last_state[24]; static ULONGLONG last_ms[24];
    ULONGLONG now = GetTickCount64();
    if (!g_rt.rt_log || player >= 24 || (state == last_state[player] && now - last_ms[player] < 1000)) return;
    last_state[player] = state; last_ms[player] = now;
    mlog("kart watchdog: player %d state %u frames %u moves %d fe=%u cc=%#x strikes %u pos %.1f,%.1f",
         player, state, frames, moves, ctrl[0x1fe], cc, strikes, pos[0], pos[1]);
}
```
(`g_no_idle_kick` read from `MKWMP_NO_IDLE_KICK` next to `MKWMP_SKIP_REFEREE_TIMEOUT`.)

- Without the guard: the last `kart watchdog:` lines of the local player before the error name the case of
  section 4 (`fe`, `moves`, `pos`), or show `cc`/`strikes` climbing if it is the strike path of section 5.
- With `MKWMP_NO_IDLE_KICK=1` on the Deck: `kart watchdog: move count raised` appears about 48 s into the
  battle and the match ends with the results screen and no `controller event ... 0x9b928162`. If the lines
  never appear, neither function is reached by an indirect call; then the hook has to go on the state-table
  callbacks of states 6 and 7 (`0x70020d4944`, enter of "finished") or on the event handler `0x7000a3195c`.

The guard only removes the end-of-battle idle kick for a player whose counter is broken. The real fix depends
on the case: A = find why `AIRaceSeqFinalize(false)` (or a fresh controller) does not precede the battle for
this kart; B = the position table registration.

No lobby change is indicated.

## 7. Limits

- Play mode of the run is not in any log; sections 1 to 4 assume Battle.
- Not decoded: the increment of `ctrl+0xb8`, the position table at `*0x32f2850`, what `rule->vt[200]` and
  `rule+0x74` mean per mode, why the PC does not hit the check.
- Whether `0x70020d43dc` / `0x700035a9a0` are reached through the runtime's indirect-call dispatch was not
  verified.
