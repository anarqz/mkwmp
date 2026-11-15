# On-screen keypad for the Room ID keyboard

The game asks for the Room ID through `nn::swkbd::ShowKeyboard` (main+0x25dae00, header "Insira ID do grupo",
6-6 units, invalid-char flags 0x80). mkw-recomp has no text-entry window, so the probe draws a numeric keypad
into the runtime's own overlay and feeds the digits back. Code: `native/probe/src/keypad.c` (hooks, input,
drawing), `keypad_logic.h` (state machine + layout, unit-tested), `detour.c` (inline hook).
All addresses are RVAs in `mkw.exe` of MKW Recomp 0.1.10 (sha256 4FE39FC7...FEBCBDCB); disassemble with
`python -I resources/tools/re/pedis.py scratch/game/mkw.exe at <symbol|rva> <n>`.

## What the runtime does (disassembly of `ov_show_keyboard`, 0x18d080)

1. `cfg_get("MKW_SWKBD_ANSWER")` **on every call** (`cfg_get` = `getenv`, then the config for `MKW_` keys), then
   `kern_core_away()`, lock `ov_show_keyboard.one`, build `ov_show_keyboard.req` (0x604 bytes):
   title char16[0x101] @0 (header, else guide, else sub text), start text @0x202, has-start @0x5ec, max @0x5f0,
   min @0x5f4, password @0x5f8, numeric mode @0x5fc, invalid-char flags @0x600.
2. Answer set and not empty: it is used (`<cancel>` / `<initial>` are special). Otherwise:
   `input_block(1)`; `text_entry_run(req, out, 1000, &len)`; `input_block(0)`.
3. `text_entry_run` (0x18dff0) is the stub for the missing window: it copies the start text, logs
   "swkbd: no text entry window on this host yet", returns nonzero (0 only for `MKW_SWKBD_TEST=cancel`).
4. Result 0 -> the guest gets 0x29f (swkbd cancel). Otherwise `clean()` filters the text (0x80 filters nothing,
   digits pass), shorter than min -> cancel, else it is copied out as UTF-16 / UTF-8.

So `text_entry_run` is the one place to put a text-entry UI: plain C ABI, the caller already yielded the guest
core, blocks the game's input for the duration (`g_block`, released only once every button is up) and turns a
0 return into the cancel result.

## Hooks (x64 inline detours, installed in DllMain; no override slot used)

| target | RVA | bytes moved to the trampoline |
|---|---|---|
| `text_entry_run` | 0x18dff0 | `41 57 41 56 56 57 53` (push r15, r14, rsi, rdi, rbx), 7 |
| `_Z17menu_notice_shownv` | 0x26ebb0 | `56 48 83 EC 20` (push rsi; sub rsp,0x20), 5 |
| `_Z16menu_notice_drawff` | 0x26ebe0 | `56 57 48 81 EC 78 01 00 00` (push rsi; push rdi; sub rsp,0x178), 9 |

`detour_install` has no length decoder: the caller gives the exact bytes it expects and nothing is patched on a
mismatch. keypad.c compares 8-14 leading bytes of all three functions before the first patch (all or none) and
logs `keypad: NOT installed ...` for another build. The target gets `jmp rel32` (+ NOPs) to a relay
(`jmp [rip]` -> hook) in a page allocated within +-0x70000000 of the module; the trampoline is the moved bytes
plus `jmp [rip]` -> target+n. The moved instructions are position-independent pushes / `sub rsp`.
The trampolines have no unwind data (only matters for an exception inside the three originals).

## Drawing

The runtime draws Dear ImGui 1.91.9 (Vulkan back end) only from `menu_overlay_frame` (0x26c060, called by
`present_image`), and only when its menu is open, the perf overlay is on, or `menu_notice_shown()` is true.
In every case the frame is `ImGui_ImplVulkan_NewFrame; ImGui::NewFrame; ...; perf_draw; menu_notice_draw(width,
scale); ImGui::Render` (0x26d4ea). Hence:

- `menu_notice_shown` hook: true while a keypad is open -> the runtime runs a frame (creating its context,
  fonts and Vulkan objects exactly as for one of its own notices).
- `menu_notice_draw` hook: original first, then the keypad on `ImGui::GetForegroundDrawList()` with
  `ImDrawList::AddRectFilled / AddRect / AddText(font=NULL, size, ...)` and `ImGui::CalcTextSize` (ImVec2 comes
  back in rax, as the runtime's own call sites show); display size from `ImGui::GetIO()+8`.

No ImGui window, navigation or IO is used: with the menu closed the runtime feeds ImGui no events, and
primitives keep us independent of ImGui struct layouts. Layout scales with the display height (key height
9.5 %), so it is the same on a 1280x800 Deck and on 4K.

## Input (read on the present thread, once per drawn frame, edges only)

- Pad: the game's SDL3 gamepads (`SDL_GetGamepads` / `SDL_GetGamepadFromID`, as voice_ui.c): D-pad or left stick
  moves (auto-repeat 380 / 110 ms), south (A) presses the focused key, east (B) deletes or cancels when empty,
  Start = OK.
- Keyboard (only while the game window is foreground): 0-9 and numpad, Backspace, Enter = OK, Esc = cancel,
  arrows + Space.
- Mouse / touch: hover focuses, left click presses (cursor mapped from the client area to the display size).
- Whatever is held when the keypad opens is ignored until released. The focus jumps to OK when the code is
  complete; OK needs min..max digits.

## Result

The guest thread that called ShowKeyboard waits in the hook (modal, as on the console) until the present thread
signals. OK: digits as UTF-16 into `out`, `*len = n`, return 1 -> the runtime continues exactly as for a typed
text. Cancel: `*len = 0`, return 0 -> 0x29f. If no overlay frame is drawn within 3 s of opening (the game not
presenting while that thread waits), the hook returns the original `text_entry_run`'s answer instead of hanging.

Scope: only requests with numeric mode, flags & 0x80, or "ID" as a word in the header, max <= 12 units, not a
password; everything else (name keyboards) goes to the original. `MKW_SWKBD_ANSWER` (launcher Room ID,
voice.c `room_id_poll`) is untouched and still wins: with it set the runtime never reaches `text_entry_run`, so
the keypad appears only when the launcher has no Room ID. `MKWMP_KEYPAD=0` (environment or mkwmp.env) turns the
hooks off; `MKW_SWKBD_TEST` keeps its runtime meaning.

## Verification

Static / offline (done):
- prologues and call sites above read from the disassembly; signatures match `scratch/game` and
  `scratch/game-p2` `mkw.exe` on disk;
- `native/probe/tests/test_keypad.c` (PASS): the detour on stand-in functions that start with the three real
  prologues (redirect, trampoline call, float arguments, mismatch refused), the keypad state machine, the
  eligibility rule, UTF-16 -> UTF-8, layout / hit-testing at 1280x800 .. 4K;
- probe builds warning-free with `-Wall -Wextra`.

Needs a live run (not done: the games were running):
- that the game keeps presenting while ShowKeyboard blocks. Evidence it does: the guest caller (main+0x25dac84)
  is a delegate that only sets a "done" byte (+0xd) and a "cancelled" byte (+0x30) after the call, i.e. an
  asynchronous job polled by the UI. If not, the log shows `keypad: no overlay frame in 3 s`;
- looks (font scaling), pad / mouse / keyboard under Windows and under Proton in Game Mode.

Live test: build, copy `native/probe/build/winmm.dll` into the game folder(s) with no `mkw.exe` running, clear
the launcher's ROOM ID field (so `MKW_SWKBD_ANSWER` is unset), go online and choose SEARCH GROUP ID. Expect in
`mkwmp.log`: `keypad: hooks installed (...)` at start, `keypad: open "Insira ID do grupo" (6-6 digits, ...)`,
then `keypad: OK "731605"` (or `cancelled`), and in `logs/mkw.log` the runtime's
`swkbd: from ...: "Insira ID do grupo" (6-6 units, ...) -> "731605"`. Check: pad navigation + A, B deletes,
B on empty cancels back to the menu, Start confirms, typed digits + Enter, mouse click; the game must not react
to these presses; then repeat with a Room ID in the launcher (no keypad, direct join, as before).
