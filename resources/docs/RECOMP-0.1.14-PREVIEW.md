# mkw-recomp v0.1.13 -> main@2cf005a (pre-0.1.14) against the MKWMP probe

**Preview, written 2026-11-18, before v0.1.14 was tagged.** Sources: `git diff v0.1.13 2cf005a` of adriadam10/mkw-recomp
(165 commits), the CI artifact `mkw-windows-runtime` of 2cf005a (its `mkw.exe` only, no `SDL3.dll`), the v0.1.13 release
zip, and the probe at `native/probe/src`. Nothing was run. When v0.1.14 is tagged, run the `bump-mkw-recomp` skill again
on the real zip: the hash below **will** change, and commits after 2cf005a are not covered here.

main@2cf005a mkw.exe SHA-256 (CI build, not a release): `2a1dec6609c5f988948c67ec125377f62e96f66f6a8d6323cd888508ee030267`
RT_GAME_ID: `c0dd6cf12c07c23a` (unchanged from 0.1.13).

**Bottom line: nothing the probe resolves, patches or reads changed so far.** `check_release.py 0.1.13 -> 2cf005a`:
`0 FAIL, 2 WARN` (launcher hash unknown; no SDL3.dll in the CI artifact). The 0.1.14 launcher work will be the usual
hash in `rust/client/src/recomp.rs`.

## Binary (check_release.py)

| Check | Result |
|---|---|
| PE exports | identical (`rt_dispatch`, `rt_sj_owner`, ...) |
| `.ovr` | 330 entries, same names, same order; 36 of 64 kRegs have a stock handler, as before |
| detours | `menu_notice_draw` 0x2a3220, `menu_notice_shown` 0x2a31f0, `text_entry_run` 0x1abe40: prologues match |
| `rt_dispatch` | `mov rax,[rcx+0x138]`: Cpu.thread still at 312 |
| spans | `g_host` 0x20000, `g_fibers` 0x2000, `added` 0x400 |
| WINMM | mkw.exe imports only `timeBeginPeriod`, forwarded by `winmm.def` |
| RT_GAME_ID | unchanged: an `mkwgame.dll` from 0.1.13's setup is accepted |
| MKW_* keys we use | all still known |

## Source review (files the probe depends on)

- `runtime/cpu/{cpu.h,hostfn.c,dispatch.c}`, `runtime/core/kernel/*`: **untouched**. `sjlj.c`: comment only.
  New `runtime/cpu/fma.c` (host FMA helpers) does not touch dispatch or overrides.
- `runtime/sdk/ov/ov_socket.c` / `ov_lanaddr.c`: `MKW_LAN_PEERS` (LAN over VPN), broadcast fan-out in SendTo, loopback
  bind in Bind. **No effect on us:** the probe overrides `Socket/Bind/SendTo/RecvFrom/Poll/Close/...` (npln.c kRegs).
  Only matters if a player sets `MKW_LAN=1` together with MKWMP (already unsupported).
- `swkbd.c`: comment only. `menu_notice.*`: not changed.
- `recomp/versions.py`, `tools/build/build.py`: generated dir / patch table detected from the exefs build id
  (`build/generated-180`). Build-time only; `layout.py` untouched, main base unchanged, so no `0x70...` constant moves.
- `patches-mkw*.txt` / `patches.c`: one new aspect site (`main+0xd6160`, Knockout Tour rainbow at 21:9, patch page
  +0xf20). Does not overlap any guest address in `GAME-1.8.0-ADDRESSES.md` or the probe.
- `romfs.c`: a missing RomFS path is now `rt_fatal` with a clear message instead of an empty index.
- `boot.c`: `rt_write_png` no longer fatal on a bad path; `--prewarm` mode added.

## Updater / setup (must not delete `winmm.dll`, `winmm_host.dll`, `mkwmp*`)

- New background update (`launcher_update.cpp`, `tools/prepare/nextsetup.py`): the next version is prepared in
  `<game>/setup-next/` and promoted on restart. `promote()` / `remove_old()` touch only a fixed list
  (`mkwgame.*`, `pipeline-*`, `spirv`, `setup-state.json`). **Safe.**
- `update.py` clean-up removes only `*.old` files the launcher renamed (files the package ships); recomp ships no
  `winmm.dll`, so ours is never renamed. **Safe**, same as 0.1.13.

## NEEDS LIVE TEST (on the real 0.1.14)

- T1 `MKW_FAST_CORES` (on by default with >= 8 spare fast cores; "background threads' children stay background") and
  `MKW_SUBMIT_APART`: thread placement changed. Check our gateway/voice threads are not starved on a hybrid Intel CPU:
  `mkwmp.log` keepalive gaps, voice dropouts.
- T2 Frame pacing changes (prewarm, `MKW_TEX_PAR` texture loads on workers): one full online race + battle, PC + Deck;
  grep `logs/mkw.log` for `sjlj:`, `fatal`, frame-interval lines.
- T3 In-app update from 0.1.13 -> 0.1.14 in `scratch/game` with MKWMP installed: `winmm.dll`, `winmm_host.dll`,
  `mkwmp.toml` still present afterwards, PLAY from our launcher works.
- T4 the usual minimum set from the skill (runtime resolved, overrides registered, Pia tracing, keypad Room ID, voice UI).

## Upstream policy (commit ecafc22, CONTRIBUTING.md / AGENTS.md)

Online play is now explicitly out of scope upstream and **MKWMP is named as the place for it**. What stays upstream:
local play, LAN mode, "and generic, documented hooks that such projects can build on". No plugin loader / hook API has
landed yet (no commit or branch mentions one as of 2cf005a). See `RECOMP-MODS.md` for what we need from such an API.
