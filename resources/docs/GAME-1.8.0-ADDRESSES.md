# Game 1.0.0 -> 1.8.0: the probe's guest addresses and offsets

Every game-version-specific constant in `native/probe/src` (1.0.0, main build id `9e244438c3d8ef1c`) mapped to
game 1.8.0 (main build id `d00e4f399e411d06`). Static analysis only (2026-11-17); nothing here was run in the game.
Addresses are main-module offsets unless a module is named; guest address = module base + offset.

Tools: `tools/re/game180/` (section 7). Probe source was not edited.

## 1. Build guard: which game is running (do this before any version-specific hook)

Three independent checks exist inside the process after `ldr_load`. Recommended: (b) to choose the table row,
(a) to confirm it; refuse all version-specific hooks (voice_ui vtables, `profile_dispatch` pc logic, error skip, the
gRPC return-address gate) when they disagree or match no row.

### (a) The build id is in guest memory (GNU build-id note, mapped with .rodata)

The main NSO carries a `.note.gnu.build-id` in its rodata segment. Layout at the offset: 16-byte note header
`04 00 00 00 10 00 00 00 03 00 00 00 47 4e 55 00` ("GNU"), then the 16-byte id (= first 16 bytes of the NSO header id
at file offset 0x40; mkw-recomp's `versions.py` uses its first 8).

| | note header at | build id at | 16 bytes |
|---|---|---|---|
| 1.0.0 | `0x7000000000 + 0x30a7e68` | `+0x30a7e78` | `9e 24 44 38 c3 d8 ef 1c 70 3a 6a 85 ef 99 c4 f0` |
| 1.8.0 | `0x7000000000 + 0x346ba54` | `+0x346ba64` | `d0 0e 4f 39 9e 41 1d 06 13 aa 8d 26 b1 c0 2f ef` |

The offset differs per version, and 1.8.0's (0x346ba64) lies past the end of the 1.0.0 image (size 0x3410000), so
`VirtualQuery` the address first, or use the always-mapped selector below to pick which offset to read:

- `*(uint32_t *)(0x7000000000 + 4)` = MOD0 offset: **`0x02830054` in 1.0.0, `0x02bb3060` in 1.8.0** (first 16 bytes of
  main: `01000000 54008302 88008302 00000000` vs `01000000 6030bb02 9430bb02 00000000`). `"MOD0"` is at that offset.

Suggested check: read main+4, select the row whose MOD0 offset matches, then `memcmp` the 16 id bytes at that row's
note offset. Both are plain image bytes (rodata / the text header); the runtime's code patches do not touch them.
No display-version string ("1.8.0") was looked for: the note is exact and sufficient.

### (b) The runtime exposes the game version

- `rt_game_version` is a COFF symbol of mkw.exe (`int rt_game_version(void)`, returns `rt_game()->version`: **100** or
  **180**). Present in the 1.0.8 exe (RVA 0x14580) and the 0.1.10 exe (RVA 0x148a0); in the upstream source at the
  1.8.0-capable tag (`runtime/core/boot/gamelib.c:88`). Resolve it with `coff_symbol("rt_game_version")` like the
  other runtime functions. Not checked against a 0.1.12 binary (none on disk); treat a missing symbol as "unknown".
- `mkwgame.dll` exports one function, `mkwgame_tables` -> `const RtGame *`; `RtGame.version` (int) is at **offset 72**
  (abi u32 @0, id @8, modules @16, nmodules @24, exefs @32, nexefs @40, patch_sets @48, npatch_sets @56, patch_page @64,
  version @72; same prefix in `RT_GAME_ABI` 1 and 2). `RtGame.modules[i]` = `{name, base, size, ...}` (0x30 bytes) also
  gives the real module bases, so `sdk`/`subsdk` bases need not be assumed.
- No cfg key holds the game version. `mkwgame.id` / `RT_GAME_ID` is a hash of the runtime's headers (it names the
  recomp build, not the game) and must not be used for this.

### (c) Fingerprint bytes (only if (a) and (b) are both unusable)

| main offset | 1.0.0 (u32 LE) | 1.8.0 (u32 LE) |
|---|---|---|
| +0x4 | `0x02830054` | `0x02bb3060` |
| +0x8 | `0x02830088` | `0x02bb3094` |

## 2. Module layout (recomp `layout.py`, 2 MiB aligned)

| | main | subsdk0 | subsdk1 | sdk |
|---|---|---|---|---|
| 1.0.0 | 0x7000000000 (size 0x3410000) | 0x7003600000 (Pia/NEX middleware, 0x8d9000) | - | 0x7004000000 |
| 1.8.0 | 0x7000000000 (size 0x37f0000) | 0x7003800000 (new, small: 0xe0000) | 0x7003a00000 (the old subsdk0) | **0x7004400000** |

`GUEST_MAIN()` is unchanged. Anything the probe expresses as `0x70...` above main's end is in another module and
moves with the layout (one case: `0x70046966a0`, section 4).

## 3. FUNCTIONAL constants (a wrong value breaks a feature or online play)

All resolved. "exact" = the code at the new address was read and is the same code; "high" = matched function with
independent structural evidence (same vtable slot, all known callees/callers agree).

| Probe site | What | 1.0.0 | 1.8.0 | Conf. | Method |
|---|---|---|---|---|---|
| `npln.c:118` | return address of the gRPC `pthread_cond_timedwait` call (clock-id conversion gate) | `0x700104a8bc` | **`0x700111232c`** | exact | fn 0x104a850 -> 0x11122c0 is the same instruction multiset, callees 6/6, callers 8/8; the BL to the PLT stub is at 0x1112328 (`946a740e`), next insn `cbnz w0` |
| `npln.c:1043,1307,1342,1349` | error service, vtable slot 6: post communication error (target of `MKWMP_SKIP_ERROR`, the Proton default skip, `MKWMP_SKIP_REFEREE_TIMEOUT`) | `0x700222d78c` | **`0x7002564cd0`** | exact | vtable 0x325bc30 -> 0x3636ad0, slots 0..7 all pair index for index, slot 6 = this; body read: lock at +0xb0, `enqueue(this+0x70, x1)`; x1 = info, id = first u32 (poster FUN_01e46958 -> 0x21086b0 is L1-identical) |
| `npln.c:1339` | error id: end-of-battle idle kick | `0x9b928162` | same | exact | constant built by mov/movk at 8 sites in both builds |
| `npln.c:1351` | error id: referee timeout | `0x606314c9` | same | exact | one site in both, inside the L1-identical FUN_01e46958 / 0x21086b0 |
| `voice_ui.c:24` | `VT_ONLINE_PAUSE` (UIPageOnlinePauseMenu vtable, slot 0 address) | `0x3248970` | **`0x3622528`** | exact | 63 slots in both; every class-own slot pairs index for index; ctor 0x21b3f58 -> 0x24cc900 loads it by ADRP+ADD |
| `voice_ui.c:25` | `SLOT_OPEN` | 7 (`0x21b22ec`) | **7** (`0x24ca550`) | exact | same |
| `voice_ui.c:26` | `SLOT_FRAME` | 11 (`0x21b384c`) | **11** (`0x24cc010`) | exact | same |
| `voice_ui.c:27` | `FN_PLATE_TEXT(ctl, char16*)` | `0x21deb68` | **`0x2505f10`** | exact | same instruction multiset, read side by side; only the two text-pane offsets differ (ctl+0xa0/0xb0 -> +0xc0/0xd0) |
| `voice_ui.c:28` | `PAGE_PLATES` | `0x770` | **`0x778`** | exact | plate-apply fn 0x21b35e0 -> 0x24cbc60: `add x20, x19, #0x778` |
| `voice_ui.c:29` | `PLATE_SIZE` | `0x150` | **`0x170`** | exact | same fn: `add x20, x20, #0x170` |
| `voice_ui.c:34` | `ROWS` (plates on the page) | 24 | **26** | exact | same fn: loop bound `cmp x24, #0x1a` (was `#0x18`); the open fn touches 26 plate bases |
| `voice_ui.c:30` | `PAGE_COUNT` | `0x3d80` | **`0x4628`** | exact | same fn: `ldrsw x8, [x19, #0x4628]` |
| `voice_ui.c:31` | `PAGE_ENTRIES` | `0x3d88` | **`0x4630`** | exact | same fn: `ldr x8, [x19, #0x4630]` |
| `voice_ui.c:32` | `ENTRY_NAME` | `0x18` | `0x18` | exact | same fn: `ldr x8, [x22, #0x18]!`, length at +0x20 |
| `voice_ui.c:33` | `ENTRY_ME` | `0x78` | `0x78` | exact | same fn: `ldrb w8, [x21, #0x78]` (is-host still +0x80; new fields +0x88, +0x8c, +0x90) |

Notes for the port:

- `ROWS` also sizes `g.shown[]` and bounds `count`; with 24, a 1.8.0 list of 25 or 26 entries makes `apply()` return
  early (no labels). Use the per-version value (or 26 as the array size).
- `FN_PLATE_TEXT` keeps its signature and its own lookup of the panes, so the probe needs no pane offsets.
- Hook semantics (x0 = page in both hooks) are unchanged.

### Guard values for the functional rows (cheap self-check at install time)

| Check | 1.0.0 | 1.8.0 |
|---|---|---|
| `u64 [VT_ONLINE_PAUSE + 7*8]` - 0x7000000000 | 0x21b22ec | 0x24ca550 |
| `u64 [VT_ONLINE_PAUSE + 11*8]` - 0x7000000000 | 0x21b384c | 0x24cc010 |
| `u64 [errsvc vtable + 6*8]` (0x325bc30 / 0x3636ad0) | 0x222d78c | 0x2564cd0 |
| `u32` x4 at `FN_PLATE_TEXT` | `d503237f d10143ff a9017bfd f90013f7` | same four words |
| `u32` x2 at error service fn | `d503237f a9bd7bfd` | `d503237f a9be7bfd` |
| `u32` at gate return address - 4 (the BL) | `945f879a` | `946a740e` |

## 4. DIAGNOSTIC constants (trace/logging, env-gated)

`MKWMP_TRACE_EDGES`, `MKWMP_TRACE_ROOM`, `MKWMP_TRACE_KART`, `MKWMP_NO_IDLE_KICK`. A wrong value only makes a trace
silent or wrong, except `MKWMP_NO_IDLE_KICK`, which writes guest memory (see the last rows).

### 4.1 Function / pc addresses

| Probe site | What | 1.0.0 | 1.8.0 | Conf. | Evidence |
|---|---|---|---|---|---|
| `npln.c:1002` | Pia join step WaitAutoConnect | 0x156015c | 0x16a9230 | high | step table 0x33035d0[0] -> 0x36df520[0], callees 5/6 |
| `npln.c:1003` | Pia join step | 0x15602dc | 0x16a9390 | high | unique string; vtable 0x310f400[8] -> 0x34d7758[8] |
| `npln.c:1004` | Pia join step | 0x1560414 | 0x16a94b0 | high | vtable slot [9] of the same pair, callees 8/8 |
| `npln.c:1005` | StartMeshStartup | 0x1560c10 | 0x16a9cf0 | high | unique string; step table 0x3303608[0] -> 0x36df558[0] |
| `npln.c:1006` | DisconnectNetwork | 0x1560f28 | 0x16aa010 | high | unique string; step table [2] |
| `npln.c:1007` | CreateMesh | 0x156102c | 0x16aa0f0 | high | unique string; step table [4] |
| `npln.c:1008` | JoinMesh | 0x1561798 | 0x16aa800 | high | unique string; step table [10] |
| `npln.c:1037` | watch start_p2p | 0x2236714 | 0x256e900 | high | vtable 0x325c5b0[38] -> 0x3637478[38], callees 13/13 |
| `npln.c:1038` | watch P2P initial sequence | 0x1285d20 | 0x1381530 | high | unique string, callees 31/33 |
| `npln.c:1039` | watch Gamesync user-session job run | 0x126c3b8 | 0x1367790 | high | unique string, callees 31/33 |
| `npln.c:1040` | watch game callback from Pia session layer | 0x223d768 | 0x2575d40 | high | L1-identical; vtable slot [2] -> [3] (one virtual inserted before it; neighbours align) |
| `npln.c:1041` | watch NplnPlugin entry | 0x16279e4 | 0x178fab0 | high | 2-insn fn; vtable slot [33] -> [36], 11 neighbouring slots align |
| `npln.c:1042` | watch NplnPlugin entry | 0x1629020 | 0x1791710 | high | read side by side; slot [69] -> [78] |
| `npln.c:1185,1254` | room trace SyncState | 0x1a82d84 | 0x1cfb670 | high | vtable 0x3173f60[29] -> 0x35466f0[29], callees 10/10 |
| `npln.c:1185,1254` | room trace PrivateRoomMain | 0x1a5a444 | 0x1cc8a30 | high | vtable 0x316dc80[37] -> 0x353ed00[37], callees 30/33 (fn grew 457 -> 699 insns) |
| `npln.c:1185,1254` | room trace ReadyVoteSetter | 0x1a60be8 | 0x1cd0b50 | exact | read side by side; slot [28] of 0x316e500 -> 0x353f820 (delta equals upstream's ReadyRace node pair) |
| `npln.c:1255` | PrivateRoomMain event handler | 0x1a5b104 | 0x1cc9da0 | high | same vtable pair slot [35]; still compares 0x9d424b78 |
| `npln.c:1259` | "string from C string" (`sdk-msg`) | 0x70046966a0 | 0x7004b5d1e0 | exact | it is **sdk `memmove`** (sdk+0x6966a0 -> sdk+0x75d1e0, dynsym); use `ldr_symbol("memmove")` instead of a constant |
| `npln.c:1265` | return address, BulkGetDocuments result retrieval | 0x1e463dc | 0x2107d48 | exact | fn 0x1e463b4 -> 0x2107d20 read side by side (after `blr [vt+0x38]`) |
| `npln.c:1265` | target: result getter | 0x146a200 | 0x1593c30 | high | slot 7 of vtable 0x31033b0+0x10 -> 0x34ca7f8+0x10; the ctor's four vtables keep their spacing; 9 slots each |
| `npln.c:1269` | controller event sink | 0x1e3f91c | 0x20ffa60 | exact | L1-identical; vtable 0x31dad18[14] -> 0x35afbd8[14] |
| `npln.c:1278` | return address, controller next-phase BLR | 0x261f894 | 0x297bc60 | high | fn 0x261f85c -> 0x297bc30 (slot [11] of two vtables), after `blr [vt+0x60]`; x1 = &next_phase still |
| `npln.c:1280` | return address, controller prepare-operation BLR | 0x261f044 | 0x297b1c0 | exact | fn 0x261eff4 -> 0x297b170 read side by side (after `blr [vt+0x68]`) |
| `npln.c:1291` | matchmake sequence "wait" | 0x1a93138 | 0x1d0f940 | high | vtable 0x3177b18[29] -> 0x354a970[29], callees 28/28 |
| `npln.c:1291` | matchmake sequence "start" | 0x1a79fa0 | 0x1ceea90 | high | vtable 0x31725f8[29] -> 0x3544998[29], callees 14/14 |
| `npln.c:1247` | kart watchdog (idle kick) entry A | 0x20d43dc | 0x5c1680 | medium | one of the two functions with three 0x9b928162 sites calling the kick check; paired by elimination (similarity 0.65) |
| `npln.c:1247` | kart watchdog (idle kick) entry B | 0x35a9a0 | 0x5bb220 | high | vtable 0x322ac70[3] -> 0x3600958[3], first callee matches, similarity 0.87 |
| `npln.c:1569` (comment) | nifm request caller | 0x2247a7c | 0x2581458 | exact | read: second of three consecutive BLs |
| `npln.c:1172,1287` | global pointer (game state) | 0x70033d2548 | **0x70037b3858** | exact | 97 data-reference votes from L2-identical functions; read in ReadyVoteSetter (`adrp 0x37b3000; add #0x858`) |

Related functions named in comments (for reading logs): idle-kick check FUN_0035ce38 -> 0xd74a60; FUN_020d4ab0 ->
0x23acc60; FUN_02624bf0 -> 0x2981170; FUN_01e46958 -> 0x21086b0; FUN_02126ad4 -> 0x240dd14; FUN_021271c8 -> 0x240e740;
FUN_02248cf0 -> 0x25827b8; FUN_0049c9d8 -> 0x20f6020; FUN_01a9637c -> 0x1d13690.

### 4.2 Trace ranges (`kTraceRanges`, `npln.c:1022-1030`)

Mapped through the matched functions at the range ends (code keeps its link order here). Approximate by nature.

| What | 1.0.0 | 1.8.0 |
|---|---|---|
| NPLN matchmaking / gamesync | 0x1200000-0x1440000 | 0x12cd000-0x1570000 |
| Pia | 0x1500000-0x1700000 | 0x1635000-0x186a000 |
| game matchmaking sequence | 0x1a00000-0x1b00000 | 0x1c70000-0x1d84000 |
| online-session manager + dispatcher | 0x2230000-0x2250000 | 0x2567000-0x258a000 |
| online race controller | 0x1e30000-0x1e42000 | 0x20eb000-0x2103000 |
| race comm-error raisers | 0x2120000-0x2130000 | 0x2403000-0x2419800 |
| document writer (FUN_0049c9d8) | 0x495000-0x4a0000 | no range: that region was re-ordered; the function is 0x20f6020 (inside the race controller range) |
| caller of FUN_01e377c4 | 0x195000-0x196000 | unresolved (section 5) |

### 4.3 Struct offsets read through guest pointers

| Probe site | Field | 1.0.0 | 1.8.0 | Conf. | Evidence |
|---|---|---|---|---|---|
| `npln.c:1065-1070` | mgr slot count / slots / current | +0x12b8 / +0x12c0 / +0x12c8 | **+0x12c0 / +0x12c8 / +0x12d0** | exact | FUN_01a9637c -> 0x1d13690 read side by side; slot size 0x148 unchanged |
| `npln.c:1048` | us_job dump offset | +0x130 (gate +0x140) | +0x128 (gate +0x138) | medium | immediates of the job run fn shift by -8 |
| `npln.c:1299,1378-1381` | Pia join job step / label / error | +0x38 / +0x48 / +0xd8 | +0x38 / +0x48 / **+0xe0** | medium | immediates of the step functions (0xd8 -> 0xe0) |
| `npln.c:1294,1366` | matchmake seq phase | +0x28 | +0x28 | high | present in both |
| `npln.c:1285` | controller desired mode / phase | +0x4a8 / +0x4ac | +0x4c8 / +0x4cc | medium | neighbouring fields move +0x20 (0x46c -> 0x48c, 0x488 -> 0x4a8, 0x4b0 -> 0x4d0) |
| `npln.c:1175,1288` | global flags | g+0x110 / +0x114 / +0x118 | g+0x120 / +0x124 / +0x128 | +0x110 exact, others medium | ReadyVoteSetter reads `[g+0x120]` |
| `npln.c:1177-1183` | `g+200` (handle), `g+0x188` (room), `s+8+0x24`, `r+0x40/0x44/5` | as written | not verified (`g+0x188` likely +0x198) | low | reached through helpers that were not read |
| `npln.c:1195-1199` | SyncState node +0x28/+0x2c; ReadyVoteSetter `*(node+0x28)` | same | same | high | present in both |
| `npln.c:1196` | PrivateRoomMain node +0x144, +0x28, +0x38, +0x131 | as written | changed, not derived | low | the function grew by half; its immediates do not line up one to one |
| `npln.c:1215-1223` | kart ctrl: frames / error id / move count | +0xb8 / +0x1cc / +0x1e8 | **+0xc0 / +0x1e4 / +0x20c** | exact | kick check 0xd74a60 read side by side (`cmp #0xbb9`, `cmp #0x13`, `str 0x9b928162`) |
| same | kart ctrl: info +0x28 (player +0x3c), state +0x40, strikes +0x1d0, pos +0x1d4, +0x1fe | as written | not verified (strikes/pos probably +0x18: 0x1e8 / 0x1ec; +0x1fe probably +0x222) | low | immediate lists only |

`MKWMP_NO_IDLE_KICK=1` writes the move counter: on 1.8.0 it must use +0x20c and read frames at +0xc0, and `state`
(+0x40) is unverified, so keep that switch disabled on 1.8.0 until the state field is confirmed.

### 4.4 Not game-version dependent (checked)

- `keypad.c` `REQ_*` (0x000, 0x202, 0x5f0..0x600): offsets of the **runtime's** `SwkbdRequest`
  (`runtime/sdk/swkbd/swkbd.h`), which the runtime fills from the SDK's `ShowKeyboardArg` with no version branch.
  They follow the mkw-recomp version, not the game version.
- `recomp.h` `Cpu` offsets, `g_host` layout, `.ovr`: runtime.
- All `nn::*` overrides are bound by symbol name. Whether 1.8.0's SDK renames or changes any of them is the other
  agent's subject.
- `main+0x33e7ee0`, `ctx+0x8da0` and other offsets in older notes are not in the current probe source.

## 5. Unresolved, and what would resolve it

| Item | Why | Resolve by |
|---|---|---|
| Trace range "caller of FUN_01e377c4" (0x195000-0x196000) | FUN_01e377c4 has no match (no function with its callee sequence in 1.8.0: probably inlined or split); its caller 0x195a20 matched only weakly (0x2a5ce0) | Read the callers of 0x20f6020 (document writer) in 1.8.0, or drop the range: `MKWMP_TRACE_EDGES` only |
| Room-trace fields behind `g+200` / `g+0x188`, PrivateRoomMain node fields | helpers / a rewritten function | Decompile 0x1cc8a30 and 0x1cfb670 (Ghidra on the 1.8.0 main), or one `MKWMP_TRACE_ROOM` run with a raw dump |
| Kart ctrl state / strikes / pos / +0x1fe | not read | Read 0x5bb220 around its three kick sites (0x5bb854, 0x5bb8a4, 0x5bba74) |
| Watchdog entry A pairing (0x5c1680) | medium: by elimination | A `MKWMP_TRACE_KART` run: both entries must log |
| Everything | static only | One two-client 1.8.0 session with `MKWMP_VOICE_UI_TRACE=1` (hooks installed, rows logged) and a forced communication error |

## 6. Recommendation: selecting constants per version

1. One table, one row per game build, selected once after `ldr_load` and before any hook:

   ```c
   struct game_addrs {
       int version;                    // 100, 180 (rt_game_version)
       uint32_t mod0_off;              // u32 at main+4
       uint32_t build_note;            // main offset of the 16 id bytes
       uint8_t build_id[16];
       // functional
       uint64_t grpc_timedwait_lr, errsvc_post;
       uint32_t vt_online_pause, fn_plate_text;
       uint16_t slot_open, slot_frame, page_plates, plate_size, rows, entry_name, entry_me;
       uint32_t page_count, page_entries;
       // diagnostic (0 = feature off for this build)
       uint64_t pia_step[7], watch[6], room[4], ...;
       uint64_t trace_ranges[9][2];
       uint16_t mgr_count, mgr_slots, mgr_cur, kart_frames, kart_error, kart_moves, ...;
   };
   const struct game_addrs *game_addrs(void);   // NULL: unknown build
   ```

2. Selection: `rt_game_version()` when the symbol resolves, confirmed by MOD0 offset + build-id note (section 1). With
   the symbol missing, the note alone selects. `NULL` (unknown build, or the checks disagree) turns every
   version-specific hook off and logs one line; the name-bound `nn::*` overrides keep working, so online play still
   runs without voice UI, the Proton error skip and the traces.
3. Keep the install-time guards of section 3 (vtable slot contents, first words): they cost nothing and catch a
   wrong row or a patched image.
4. A zero in a diagnostic field disables that trace, so a new game version can ship with only the functional fields
   filled.
5. Runtime signature scanning instead of a table: **not recommended as the primary mechanism.** 1.8.0 was built
   with a different compiler revision: instructions are re-scheduled inside basic blocks, virtual slots and struct
   offsets move. Of 195 882 functions only about 10 600 keep an identical relocation-masked body, and none of the
   functional targets except FUN_01e46958 does. A signature therefore has to be written per build anyway, and the
   struct offsets (0x778, 0x170, 0x4628, 26 rows) cannot come from a scan at all. Where a runtime lookup is cheap
   and robust, use it: `memmove` by symbol; the error service function as slot 6 of its vtable; the pause-menu hooks
   already go through the vtable. For a future game update, run `tools/re/game180/fmatch.py` on the new exefs and
   add a row.

## 7. Method and tools

`tools/re/game180/` (run with `.venv\Scripts\python.exe -I`; `.deps` is added to `sys.path` by `gamebin.py`; the
NSO loader is the existing `resources/tools/re/nsodump.py`, loaded by path because that folder's `dis.py` shadows the
stdlib module capstone imports). Work on copies of the exefs; `--cache DIR` keeps the analysis (about 525 MB).

| Script | Use |
|---|---|
| `gamebin.py` | `load(exefs)`: image with RELA and **RELR** relative relocations applied (main's vtables are RELR), segment bounds, function starts, L1/L2-normalised text |
| `fmatch.py OLD NEW --cache DIR [addr...]` | function matcher: unique strings, identical L1/L2 bodies, identical instruction multisets, call-graph and vtable propagation, link-order alignment between matched neighbours; prints the match, similarity, callee/caller agreement and vtable slots; data addresses print their ADRP-reference votes |
| `fdiff.py OLD NEW oldaddr newaddr [n]` | side-by-side disassembly, differing lines marked |
| `fnconsts.py OLD NEW --cache DIR [--all] fn[:newfn]...` | immediates only in old / only in new / common: how struct offsets moved |
| `sigport.py OLD NEW addrs` | masked-window signature port; exact only where the code was not re-scheduled (few places in this update) |
| `probe-addrs-100.txt` | the probe's 1.0.0 address list, input for the above |

Result of `fmatch` on this pair: 140 864 of 195 882 functions paired (4 970 by string, 10 614 L1, 798 L2, 6 885
multiset, 27 754 call graph, 3 099 vtable, 428 reference, 86 316 link order). Link-order pairs are the weakest
kind; every row above that relies on one also has a vtable slot, callee or side-by-side confirmation, as stated.
Cross-check against upstream anchors: mkw-recomp's `version.c` gives the sequence-node vtables for both builds
(ReadyRace 0x316e2a0 -> 0x353f5c0, delta 0x3d1320); the ReadyVoteSetter vtable pair found here has the same delta.

Upstream anchors for 1.8.0 are in `runtime/core/boot/version.c`, `recomp/versions.py` and
`recomp/patches-mkw-180.txt` of mkw-recomp. The Ghidra project under `scratch/ghidra` holds 1.0.0 only and was not
needed.
