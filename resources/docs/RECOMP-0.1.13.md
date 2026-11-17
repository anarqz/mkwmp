# mkw-recomp v0.1.12 -> v0.1.13 against the MKWMP probe

Sources: the official Windows `mkw.exe` of both releases, out of the zips GitHub serves for the tags
(`gh release download v0.1.13 --repo adriadam10/mkw-recomp --pattern "*windows-x64*"`, same for v0.1.12);
COFF symbol table, section table, export table and `.ovr` parsed with one script over the extracted
`mkw.exe`; the probe at `native/probe/src`. Nothing was run, and no `mkwgame.dll` of 0.1.13 exists here
(the player's setup builds it), so everything about the runtime DLL is from the binaries.

mkw.exe SHA-256: 0.1.12 `fbd215a5...5d66d1b1` (the value `rust/client/src/recomp.rs:23` already lists;
33,552,896 B), 0.1.13 `1c47ffa2502a1ee92c2d01da0791802c18bf6c3365040c2b4fa1c91dacbc8695`
(33,757,696 B). The 0.1.12 hash coming out identical is what checks the extraction: same method, same zip,
and it reproduces the number already in the repo.

**Bottom line: nothing in 0.1.13 requires a different probe binary.** Every name resolves, `.ovr` holds
the same 330 names in the same order, the `Cpu` prefix and the 64-slot hostfn table are where the probe
reads them, all three detour prologues match byte for byte. What is missing is one line: 0.1.13's hash is
not in `KNOWN`, so the launcher says "Unknown mkw-recomp" instead of naming the version. It is **not**
added to `SUPPORTED` here, and should not be until somebody plays a session on it: this report is binary
parsing, not a live test.

---

## BREAKING (must change)

### B1. The launcher does not recognise 0.1.13 (not the probe)
- The launcher refuses 0.1.13 today because `KNOWN` (`rust/client/src/recomp.rs:20-27`) has no hash to
  match: `identify_bytes` (`:80-91`) lands on `Unknown` and the window says "Unknown mkw-recomp: MKWMP
  needs 0.1.10 or 0.1.12" (`:52`). With the line in, it becomes `Other("0.1.13")` -> "mkw-recomp 0.1.13:
  MKWMP needs 0.1.10 or 0.1.12", which is the correct sentence while nobody has tested it.
- The change is one line in `KNOWN` (`:27`) and one line of test in `only_supported_releases_pass`
  (`:150`). `SUPPORTED` (`:14`), `RECOMMENDED` (`:16`) and `DOWNLOAD` (`:17`) stay as they are: putting
  0.1.13 in `SUPPORTED` promises it plays.
- User-facing text that still names 0.1.10/0.1.12 only (`site/index.html:90`, `README.md`): unchanged on
  purpose, it follows `RECOMMENDED`.

### B2. Probe source: nothing
No probe file needs an edit for 0.1.13 to load, resolve, register and detour (S1-S4 below). If a runtime
version check is ever wanted for logging only, RECOMP-0.1.12.md's cheapest discriminator (the export
`rt_sj_owner`) is still there and so is `cfg_game_dir`; 0.1.13's exe exports the same 7 names as 0.1.12.

---

## NEEDS LIVE TEST

### T1. First start after the update: mkwgame.dll — this one looks fine
RECOMP-0.1.12.md T1: the runtime hashes `runtime/cpu/*.h` + `gamelib.h` into `RT_GAME_ID`, and a game
library built for another id is rejected ("game code: built for another version of the runtime") before
the start screen can rebuild it. The id moved 0.1.10 `f3dd89d35d5d19af` -> 0.1.12 `c0dd6cf12c07c23a`.
- 0.1.13's `.rdata` holds `c0dd6cf12c07c23a`; 0.1.12's holds the same string (each exe holds exactly two
  16-hex strings in `.rdata`, the other being `0123456789abcdef`). **The id did not move**, so a folder
  updated 0.1.12 -> 0.1.13 should not need a rebuild and should not die in that fatal. Not run.

### T2. RECOMP-0.1.12.md's T1-T9 are not re-confirmed here
This report parses the two binaries; it does not read the 0.1.12 -> 0.1.13 source diff the way
RECOMP-0.1.12.md read 0.1.10 -> 0.1.12. So the nine behaviour points there (game folder, profiles,
SDL3/libusb controllers, `MKW_FPS=auto` timing, keypad and room-id answer, the launcher updater) are
neither confirmed nor contradicted for 0.1.13. Everything the probe calls *by name* still resolves and
nothing it detours by *bytes* moved; what those functions now *do* is another question, and it needs a
run. Two of those points are cheap to settle from the binaries, and are settled here: the game-code id
(T1 above) and the runtime exports (S2 below).

---

## SAFE (checked, unchanged)

### S1. (a) COFF names
All **31** names of `coff.c:12-45` are present in 0.1.13's symbol table, each exactly once, in `.text` or
`.data`: the six required (`hostfn_add_override, ldr_count, ldr_symbol, rt_log, rt_call, cfg_get`), the
five fs delegates (`ov_open_file, ov_entry_type, ov_read_file, ov_file_size, ov_close_file`), the three
fiber entries, `kern_cpu / kern_core_away / kern_core_back`, `g_host`, `text_entry_run`, the two
`menu_notice_*`, and the seven ImGui symbols. COFF symbol records: 26,892 -> 26,992.

### S2. (b) Override names
`.ovr` is `vsz=0x14C0` in both, and holds **330 entries, the same 330 names in the same order** (one name,
`_ZN2nn5prepo28RequestImmediateTransmissionEv`, appears twice, in both exes). `stock_handler`
(`npln.c:1469-1494`) reads `.ovr` out of the *loaded* image via `GetModuleHandleW(NULL)`, so the section's
new RVA does not matter.
- Of the 64 `kRegs` names, the same 37 have a stock handler in both versions and the same 27 have none:
  **no override the probe delegates to lost its handler**.
- Exports of `mkw.exe`: 7 in both, the same 7 (`rt_dispatch, rt_sj_owner, rt_svc, rt_udf, rt_ticks,
  rt_spin_gate, fnprof_hit`). `npln.c:1401-1418` patches `mkwgame.dll`'s import of `rt_dispatch` from
  `mkw.exe`, and that name is still exported.

### S3. (c) Struct layouts and the 64-slot budget
- `Cpu` prefix (`recomp.h:19-25`): `rt_dispatch` starts `48 8B 81 38 01 00 00` (`mov rax,[rcx+0x138]`
  = `thread` at 312) in both exes, and the whole first sixteen bytes are identical
  (`48 8B 81 38 01 00 00 48 85 C0 74 0A 8B 00 85 C0`).
- `hostfn_add_override` still contains `48 83 F8 40` (`cmp rax,0x40`) at **+0x1C** of its body in both,
  followed by the same `74 33 8D 48 01 89 0D ...` (the rip displacement differs, nothing else). So the
  64-slot limit `RUNTIME_OVERRIDE_MAX = 64` (`npln.c:1676`, asserted by `:1681-1683`) holds, and `kRegs`
  is exactly 64 names (`npln.c:1597-1669`): the table is **full**, as the comment above the asserts says.

### S4. (d) Detours (keypad.c is the only user of detour.c)

| function | 0.1.12 RVA | 0.1.13 RVA | first bytes in 0.1.13 | probe signature | match |
|---|---|---|---|---|---|
| text_entry_run | 0x197B20 | 0x1A1320 | `41 57 41 56 56 57 53 48 83 EC 20 4C 89 CE` | kEntrySig (14, steal 7) | yes |
| menu_notice_shown | 0x27E190 | 0x287D60 | `56 48 83 EC 20 48 8B 35` | kShownSig (8, steal 5) | yes |
| menu_notice_draw | 0x27E1C0 | 0x287D90 | `56 57 48 81 EC 78 01 00 00 C5 78 29 84 24` | kDrawSig (14, steal 9) | yes |

The three prologues are byte-identical to 0.1.12's, not just matching the signatures.

### S5. (e) What moved, and why it does not matter

| symbol | 0.1.12 RVA | 0.1.13 RVA |
|---|---|---|
| hostfn_add_override | 0x3A260 | 0x3CD30 |
| rt_dispatch | 0x39B70 | 0x3C640 |
| rt_call | 0x3AD90 | 0x3D860 |
| rt_log | 0x1A2D0 | 0x1A420 |
| cfg_get | 0xE260 | 0xE260 |
| ldr_count / ldr_symbol | 0x16D00 / 0x16EB0 | 0x16E50 / 0x17000 |
| ov_open_file (static) | 0x15DC00 | 0x1673F0 |
| g_host (.data) | 0x1ACE1D0 | 0x1AE2290 |
| g_fibers (.data) | 0x1870220 | 0x1881740 |
| kern_cpu | 0x2B330 | 0x2C130 |
| text_entry_run | 0x197B20 | 0x1A1320 |
| menu_notice_shown / menu_notice_draw | 0x27E190 / 0x27E1C0 | 0x287D60 / 0x287D90 |

`.text` grew `0x724096 -> 0x72DCC6` (+0xB830), `.data` moved `0x91A000 -> 0x925000` with `vsz`
`0x3517D38 -> 0x3598D38`. Every entry above is resolved by name at runtime or found by the
`GetModuleHandleW(NULL)` scan; nothing in the probe reads an RVA from disk, so the shifts are expected and
harmless. `cfg_get` at 0xE260 and `cfg_game_dir` at 0x10920 are the only two in this list that did not
move. `detour.c alloc_near` searches +-0x70000000, unaffected.

---

## Two questions

### (a) The game side: is 1.8.0 the blocker, and what would supporting it take?
The probe does not check the game version (no build-id test in `native/probe/src`), and `0.1.10` already
ran both `1.0.0` and `1.8.0`, so a 1.8.0 session on a recent runtime is a plausible configuration — but
the probe's game-side constants are 1.0.0 addresses. From reading `npln.c` and `voice_ui.c`, only a
handful of them are on a functional path (`0x700104a8bc` for the `pthread_cond_timedwait` call site;
`0x700222d78c` for the error-service slot that skips `0x9b928162` under Proton; the race-controller
writes around `ctrl+0x1e8`); the rest are `MKWMP_TRACE_*` diagnostics, and `voice_ui.c`'s ten are
fail-soft (the in-game voice menu stays off, the voice path itself is `mkwmp-voice.dll` + UDP). So the
question is which one breaks *first* in a real session, and whether it is the probe at all — the netcode
side (`RE-MAP.md`) is where the guess is weakest, and a run is the only thing that says.

One constraint worth naming before 1.8.0 is attempted: **the override table is full.** `kRegs` is
exactly 64 names (`npln.c:1597-1669`) and the runtime's table holds 64 (`hostfn_add_override`'s
`cmp rax,0x40`, asserted at `npln.c:1681-1683`); the two `*Fiber` slots are traded for the core-mask pair
by default (`npln.c:1709-1740`), and two were "stolen" from `GetAddrInfo`/`FreeAddrInfo`
(`npln.c:1629-1631`). If a newer SDK calls a new `nn::*` that has to be intercepted, **an existing
override has to be dropped**: it is not additive work, it is a trade.

### (b) What data from us would be useful?
We run 1.8.0 (build id `d00e4f399e411d06`), and we have three things that map onto your problem:
- the four 1.8.0 ExeFS modules (`main`, `sdk`, `subsdk0`, `subsdk1`) — **not shippable**, it is Nintendo
  code and your `NOTICE.md` is right; only offsets and analysis can travel;
- a function/symbol/call/string database for 1.8.0 (per module, 226k functions, 30k symbols, vtables,
  string refs) and the 275 `nn::pia` RTTI class-name strings with their offsets in `main` — enough to
  find the same call sites by *meaning* instead of by offset, which is how the functional constants above
  could be re-derived for 1.8.0;
- the 1.0.0 -> 1.8.0 relocation we did for mkw-recomp's own patch table (117 spin sites -> 97, each with
  how it was found), which is the same shape of work as `RE-MAP.md`'s 555 offsets, at a smaller scale.
Also, if it helps: we can run sessions on two PCs. Tell us which build you want exercised and what to
grep in `mkwmp.log` / `mkw.log`, and we can hand back both logs. Say which of these is worth your time;
none of it is offered as a patch to `npln.c` without a run that points at the line.

---

## Files
- Script (PE sections, COFF symbols, exports, `.ovr` names): `~/lavoro/tmp/verify013/check5.py`,
  `~/lavoro/tmp/verify013/pe.py`
- Override name lists: `~/lavoro/tmp/verify013/ovr12.txt`, `~/lavoro/tmp/verify013/ovr13.txt`
- `mkw.exe` of both releases, extracted to `~/lavoro/tmp/verify013/ex12/`, `~/lavoro/tmp/verify013/ex13/`
  (from `mkw-recomp-0.1.12-windows-x64.zip` and `mkw-recomp-0.1.13-windows-x64.zip`)
