# Game 1.8.0: first live runs (2026-11-18)

Game 1.8.0 + mkw-recomp 0.1.13 on Windows, against a local `mkwmp host`. Two games on one PC
(`scratch`-style second game folder, romfs hard-linked), driven with `MKW_INPUT_SCRIPT`. The probe was the
1.0.2 one with 1.8.0 changes of its own (no `game_addrs` table yet); the launcher's 1.0.0-only check was
bypassed. Result: **a matchmade `Normal24` race with two players**: shared course vote, both on the grid, each
seeing the other.

## What it took (in the order the runs hit them)

### 1. mkw-recomp's LAN play crashes NPLN (launcher, `MKW_LAN=0`)
`lan = 1` in mkw.ini (set by community "LAN packs"; mkw.log: `patches: lan on`) patches the game's network
code. Under MKWMP the NPLN gRPC dispatcher (1.8.0 `main+0xfcc440`, helper `main+0xfefda0`) later calls
through a garbage object: `FATAL: rt_dispatch: no function at guest pc 0 (lr main+0xfcc4b0)`, about 1 s
after `Auth/IssuePrearrangedUserToken`. 4 crashes in 4 runs with `lan = 1`; 4 sign-ins in 4 with
`MKW_LAN=0` (env overrides mkw.ini). The launcher now starts the game with `MKW_LAN=0`.

### 2. Online Play does not make the second nifm request (probe)
On 1.8.0 the Online Play click does not call `nifm::SubmitNetworkRequestAndWait` again, so the dormant probe
never seats and the click returns to the menu. The runs used `MKWMP_EAGER=1`; with it 1.8.0 still signs in
only when Online Play is opened. The gate needs another trigger on 1.8.0.

### 3. Matchmade GameSession without capacity: error 0x884b839c (lobby fix)
Right after the assignment both games showed "A communication error has occurred" on the character
screen, with the Gamesync session closed ~150 ms after it opened.
- Error service slot 6 is `main+0x2564cd0` (vtable `0x3636ad0`; 1.0.0's `0x222d78c`, found by its
  `{kind 12, reason 37}` constant). Logged there: id `0x884b839c`, posted by `main+0x6f6e4c` (in
  `main+0x6f6d10`, a per-frame check of the online session manager, sub-object `+0xc8`).
- The condition is the admission check `main+0x20fa450`: collect the member ussids, take
  capacity = `main+0x256d5a0(mgr, -1)` = session object `+0x80`, fail if capacity < 1, else require
  `members < capacity` or my ussid among the first `capacity`.
- Live: capacity was **0**. It is GameSession field 2. `msgs::assignment` never sent fields 2/3 (only the
  room RPCs did). 1.0.0 never reads it for a matchmade session.
- Fix: the assignment's GameSession carries `2 capacity` (the lobby's) and `3 players`. The error is gone and
  Gamesync, `AllocateIceServerSet`, `GetDocument(__gs/f)` and the Pia mesh follow.

### 4. Pia port the OS refuses (probe fix, also 1.0.0)
`ov_pia_bind` fell back to an ephemeral port when the requested one failed. Windows reserves UDP ranges
(Hyper-V / WSL: `netsh int ipv4 show excludedportrange protocol=udp`, here 64652-64751 among others). Live:
`requested port 64720, got 65371`. Pia still advertised 64720, so the peer sent there too and the game
counted the loss. Now a refused requested port returns `EADDRINUSE` (98) and Pia picks another (seen live:
it retries at once with a new port). Port 0 still binds an ephemeral port.

### 5. Connection quality kick: error 0x709ed269 (one-PC test artefact)
`main+0x6f66e0` (every second) reads per-station ping and loss maps (stats object
`[[main+0x37be190]+0x40]+0x18[idx]`, ping map `+0x68`, loss map `+0x10a0`, node value `+0x28` float) and
posts `0x709ed269` when enough peers have ping > 1000 ms or loss > 70 % over 15/30 s. Two games on one GPU
ran at 0.4-0.8x speed: ping 1012 ms, loss 0. The runs dropped that id (`MKWMP_SKIP_ERROR`) to reach the
race. With two machines this should not fire; not verified.

## Observed on 1.8.0
- Gamesync listens: `__us`, `Companion`, `PlayerStatic`, `DPID`, `__gs`, **`Swarm`** (new), `@@RefereeResult`,
  `Global`; writes `DPIS/<n>`, `PlayerStatic/<n>`, `Companion/<n>`, `@@KeepAlive/<n>` (+ deferments). Companion
  carries `my_follow_ussid`, `my_sub_ussid`. The lobby serves them as is; nothing failed on them.
- `@@RefereeRequest/<ussid>/Request/<key>` arrives at race start with `option {total_player_count,
  dropout_rank, clutch_id}`; the current referee answered and the race ran.
- Pia: NAT probes to 10025 as before; a 308-340-byte packet goes to UDP **34343** (relay, from
  `g2122d301.%.p.srv.nintendo.net`); nothing answers there and the mesh still formed directly.
- 1.8.0 probe diagnostics used: `strlen` (resolved by name) replaces `main+0x46966a0` for SDK texts, with
  callers in `main+0x12f0000..0x1600000`; 1.0.0's "Timed out to get my UserSession." site `0x126c6b0` is
  `0x13679b8`. gRPC wait gate: `0x700111232c`.
