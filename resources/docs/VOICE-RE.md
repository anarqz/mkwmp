# Voice chat and the lobby player menu: what the game already has (RE, 2026-11-14)

Static analysis of `scratch/main.bin` (image offsets; guest address = 0x7000000000 + offset), the ExeFS
import tables, `romfs/Layout/*.blarc.zs`, `romfs/Mals/USen/*.msarc`, the mkw-recomp 1.0.8 runtime source
(`scratch/recomp-1.0.8/src/runtime`) and the COFF symbols of `scratch/game/mkw.exe` (0.1.10).
**Nothing was run live.** Working files (decompiles `d1.c`..`d12.c`, scripts): `scratch/voice-re/`.

Marks: **V** = read in decompiled code / data / file (address given). **I** = inferred, not confirmed.

## 0. Short answers

1. **The game has no voice chat at all.** No GameChat, voice, microphone, mute, block, report or profile
   API is imported, no such string exists in `main`, no such message exists in the MSBT files and no
   layout has a mic / speaker / mute pane or texture (all V). On Switch 2, GameChat is a system feature
   outside the game. There is nothing to "turn on"; every voice UI element must be borrowed from
   another feature.
2. **There is no per-player menu.** The online pause menu (`UIPageOnlinePauseMenu`) shows up to 24
   player name plates, display only: no cursor, no option list, no handler (V). "View profile / block /
   report / add friend" do not exist in this game.
3. What does exist and can be borrowed (all driven by guest functions we can call or hook, no overlay):
   - the 24 name plates of the online pause menu (name text, a hidden second text `T_VR`, a host icon,
     an "is me" plate style, an "invalid" state);
   - the in-world name tag above each kart (`ID_00`, has a crown icon animation);
   - the preset-phrase chat (`ChatSelect_00` list of 24 buttons, `ChatBalloon_00` balloons with a
     sender name) and the toast (`Notification_00`).
4. **The 64-override budget is already used up** (`native/probe/src/npln.c`, `kRegs` and its comment). New
   `hostfn_add_override` hooks need freed slots. Guest *virtual* functions can be hooked without any
   slot (section 5).

## 1. SDK APIs (question 1)

Imports of `main` (1289), `subsdk0` (216), `sdk` (8), dumped with `resources/tools/re/nsodump.py`
(full list: `scratch/voice-re/imports-all.txt`). `main` imports by namespace: audio 234, os 127, codec 77,
hid 58, movie 52, util 48, async 44, socket 32, prepo 31, fs 27, ldn 25, fontll 25, account 20, oe 19,
time 15, friends 14, vi 13, image 13, mem 12, err 10, nifm 8, ssl 8, swkbd 8, and a few others. V.

| Area | What is imported | Use for voice |
|---|---|---|
| GameChat / voice chat | **nothing** (no `nn::gamechat`, no chat namespace, no Pia voice module strings) | none |
| Microphone | **nothing**: no `nn::audio::AudioIn*` / `OpenInputStream`. `nn::audio::AddCapture/ReadCaptureBuffer` are audio-renderer capture effects (output tap), not a mic | none |
| Audio out | `nn::audio` renderer + `OpenOutputStream/WriteOutputStream` | our engine should play through its own host device, not through these |
| Opus | `nn::codec::*HardwareOpusDecoder*`, `GetOpusPacketSize` (game audio/movies). The recomp ships `libopus-0.dll` beside `mkw.exe` | I: the voice engine could load the same DLL |
| Friends | `nn::friends::Initialize, SetAllocator, GetFriendList, CheckFriendListAvailability, EnsureFriendListAvailable, GetPlayHistoryRegistrationKey, AddPlayHistory, AsyncContext::{ctor,dtor,HasDone}, Friend::{ctor,GetAccountId,GetNickname,IsValid}` | no block list, no mute, no profile page |
| Parental control | **nothing** (`nn::pctl` not imported) | no "voice restricted" check to answer |
| Pia | only 4 `nn::pia::ZSTD_trace_*` symbols; Pia itself is linked statically | none |
| Applets | `nn::swkbd::ShowKeyboard`, `nn::hid::ShowControllerSupport`, `nn::err::ShowError` | none (no profile / friend applet) |

Consequence: **there is no host function to override for "player X is talking", "player X is muted" or
"voice chat available"**. All three have to be pushed into game UI objects by our own code (sections 3-5).
`MKW_TRACE=host` cannot show anything voice related for the same reason.

## 2. Strings, messages, layouts (question 2)

- `main.bin` strings matching voice/GameChat/mute/microphone/speak/block/report/profile: none relevant
  (only audio-speaker parameters, play reports, `ResultParticipantInBlockList`). V.
- `Mals/USen/MsgMain.msarc`, `LayoutMsg.msarc` (not compressed; dumped to
  `scratch/voice-re/msgs-USen.txt` with `scratch/voice-re/msg.py`): no voice text. Related texts, all in
  `Menu.msbt` / `Race.msbt`, label = the number: 5316 "Participants", 5021 "<name> has joined!",
  9001 Resume, 9002 Quit, 9004 Change Character, 9018 Start Game, 9019 Resume Free Roam, 9021 Change
  Rules, 9022 View Room Info, 9023 Settings/Controller, 9025 View Rules, 9221-9241 settings rows with
  On/Off (9223/9224), 9701-9733 and 9750-9753 preset chat phrases. V.
- Layout archives (218; pane dumps via `scratch/voice-re/panes.py`): no pane, texture or animation named
  after voice/mic/mute/speaker. V. Relevant layouts:

| Layout | Content (V) |
|---|---|
| `OnlinePauseMenu_00` | `L_Btn_00..04` (parts `DialogBtnL_00`), `L_Name_00..23` (parts `PlayerName_00`); anims `BtnNum`, `State`, `In`, `Out` |
| `PlayerName_00` | `T_Text_00/01` (name), `T_VR_00/01` (hidden text), `P_HostIcon_00` (hidden, texture `GroupIcon_02`), `P_CharIcon_00`, `P_Sticker_00`, `N_User_00/01`; anims `StickerOnOff, Invalid, Score, BaseColor, UpdateOut, UpdateIn, BaseSize, User, IsHost` (+ `Loop`, `In`, `Out`). No bounding pane |
| `ID_00` (name tag over karts) | `P_CharIcon_00`, `P_Crown_00` (hidden, `MiniMapIconCrown_00`), `T_Text_00`, `P_Sticker_00`; anims `In, Team, Out, StickerOnOff, Crown` |
| `ChatSelect_00` | 25 parts `ChatSelectBtn_00` (`T_Text_00/01`), `T_Pic_00`, texture `ChatIcon_00` |
| `Chat_1`, `Chat_2` | 24 parts `L_ChatBalloon_%02d` -> `ChatBalloon_00` (`T_Name_00/01`, `T_Text_00`; anims `IsMe`, `ColorState`, `Arrow`, `IsSplit`) |
| `Notifications_00` / `Notification_00` | one toast, `T_Msg_00` |
| `FriendBtn_00`, `TeamSelectBtn_00`, `ResultBand_00`, `Watching_00` | other places a player name is drawn |

UI classes named in the binary: `UIPageOnlinePauseMenu`, `UIPageChatSelect`, `UIPageChatNotice`,
`UIControlChatPopup`, `UIControlChatSelectButton`, `UIPageNotifications`, `UIControlNotification`,
`UIControlRaceNameDisplay`, `UIPageRaceInfo`; sequence nodes `FreerunPauseMenu`, `CloseFreerunPauseMenu`,
`IsPauseMenuOpen`, `ChatDisplay`, `ChatUIReady`. V.

## 3. The online lobby player list (question 3)

### 3.1 `UIPageOnlinePauseMenu` (vtable 0x3248970, 63 slots) - V unless marked

| Slot | Function | Role |
|---|---|---|
| 32 | `FUN_00e19058` | setup: 24 name-plate controls **embedded at page+0x770 + i*0x150** (init `FUN_00aff860`), two button groups (cursor) at page+0x3550 / +0x3968 (one per local player, callback object vtable 0x32488e0), button text panes `L_Btn_%02d/T_Text_00` at page+0x34b8..0x34d8, button objects at page+0x3490..0x34b0, animators `BtnNum` page+0x4b78, `State` page+0x4b88 |
| 7 | `FUN_021b22ec` | open: clears the plates, builds the item list, sets `BtnNum` = count-1, labels the buttons (`FUN_00a5cfd0(textPane, &label, &"Race")`), fills the plates (`FUN_021b2ec0` copy+sort, `FUN_021b35e0` apply) |
| 11 | `FUN_021b384c` | per-frame update while open: refills plates when `info+0xe12` is set, enables/disables buttons from `info+0xe1e/0xe1f` |
| - | `FUN_021b39d0` (from callback `FUN_021b4848`, vtable 0x32488e0 slot 0) | **decide handler**: maps the pressed button object to index 0..4, reads the item id, writes `info+0x68 = id`, posts page event (vtable 0x3248938), plays `pDecide_Big` (id 6) or `pDecide_Middle` |

`info` = `*(FUN_007f23cc(page) + 0x188)` (the UI info block of the pause menu, filled by the sequence node
`FreerunPauseMenu`, update `FUN_00aa24fc`, vtable 0x3168d80). Fields: `+0x64` mode, `+0x68` chosen item,
`+0x70` player count, `+0x78` player entry pointer array, `+0xe10` is-host, `+0xe12` list dirty,
`+0xe14` local player index. V.

Item ids and their labels (pointer table 0x32486c0, strings at 0x3248688.., MSBT file "Race"): V

| id | label | text | id | label | text |
|---|---|---|---|---|---|
| 0 | 9018 | Start Game | 5 | 9022 | View Room Info |
| 1 | 9001 | Resume | 6 | 9004 | Change Character |
| 2 | 9019 | Resume Free Roam | 7 | 9002 | Quit |
| 3 | 9021 | Change Rules | **8** | "0000" | unused |
| 4 | 9025 | View Rules | **9** | "0000" | unused |

Item sets built by `FUN_021b22ec`: mode < 4: `[1,6,7]`; mode 4: `[0?,2,5,6,7]` or `[0?,2,6,7]`;
mode 5: `[3|4,2,5,6,7]` or `[3|4,2,6,7]`. The layout has 5 buttons and the online sets already use up
to 5, so **a sixth item does not fit without a layout change**. Ids 8 and 9 have no consumer that was
read (the reader of `info+0x68` in the sequence graph was not traced). I: an unknown id closes the menu.

So the list is display only: **no option table and no per-player handler exist to repurpose.**

### 3.2 Name plate control (0x150 bytes, `FUN_00aff860`) - V

| Offset | Member | Setter used by the page |
|---|---|---|
| +0x00 / +0x10 / +0x20 / +0x30 | anims `StickerOnOff` / `Invalid` / `Score` / `BaseColor` | `FUN_021de6ac(ctl, bool)` = Invalid (empty slot) |
| +0x40 / +0x50 / +0x60 | anims `UpdateOut` / `UpdateIn` / `BaseSize` | |
| +0x70 | anim `User` (plate style "me" vs "other") | `FUN_021de630(ctl, isOther)` (frame 1 when the flag is 0); `FUN_021de5b4(ctl, isOther)` drives +0x00 the same way |
| +0x80 | anim `IsHost` (shows `P_HostIcon_00`) | `FUN_021deaec(ctl, bool)` |
| +0x90, +0x98 | root pane, `P_CharIcon_00` | `FUN_021de4fc(ctl, entry)` |
| +0xa0, +0xb0 | `T_Text_00`, `T_Text_01` (name) | `FUN_021dec1c(ctl, &safeString16, x)`; `FUN_021deb68(ctl, char16*)` writes any UTF-16 text to both |
| +0xa8, +0xb8, +0xc0 | `T_VR_00`, `T_VR_01`, `A_VR_00` | not written by this page (hidden) |
| +0xe0 | sticker | `FUN_021de560(ctl, entry+0x68)` |

Primitives (V): state animation = `a = *(anim); a->vt[0x20](a, &{typeid, 0x12}); a[2]->vt[0x138](float
frame, a[1])`; play/stop pair = `FUN_021e677c(animSlot, bool)`; text = `p = pane; p->vt[0x20](p,
&{typeid, 0x46}); p[2]->vt[0x478](p[1], -1, char16*)`.

Player entry in `info+0x78[]` (also copied to page+0x3d80 count / +0x3d88 array, sorted by `+0x7c`): V

| Offset | Field |
|---|---|
| +0x00 u32, +0x04 valid | character id |
| +0x10 u32, +0x14 valid | costume |
| +0x18 char16*, +0x20 int capacity, +0x24 inline buffer (0x40 bytes) | **player name** |
| +0x68 u32, +0x6c valid, +0x70 u64 | sticker |
| +0x78 u8 | is local player ("me") |
| +0x7c int | **race-player index** (the key `FreerunPauseMenu` uses to find the player object) |
| +0x80 u8 | is host |
| +0x84 int | second index (-1 when unset) |

### 3.3 Chat (preset phrases) - V unless marked

- Pages: `UIPageChatSelect` (vtable 0x3242d00, page id 0x3e; 24 buttons, 16 when
  `chatInfo+0x72` is set), `UIPageChatNotice` (vtable 0x32427a8, page id 0x3f, 24 balloons at
  `page+0x780` array). `chatInfo` = `*(uiInfo + 0x578)`; `+0x6c` chosen phrase index, `+0x71` reset
  request, `+0x72` phrase-set mode, `+0x73` chat enabled, `+0x78` queue count, `+0x80` queue.
- Send: node `ChatDisplay` (vtable 0x3165e00, message handler slot 35 `FUN_00d3e6dc`), page event
  0xc76e139a for page 0x3e -> `FUN_01a38a50(node, &phraseIdx, playerIdx, mode)` -> net event 0x2f815dfc.
- Receive: event 0x2f815dfc -> `FUN_02119490` (decode) -> **`FUN_02119540(x, kind, netId, phrase, mode)`**
  builds a 0x78-byte item and queues it with **`FUN_0215ba80(chatInfo, &item)`**. Item: `+0x00/+0x08/+0x10`
  phrase id per mode with valid bytes at `+0x04/+0x0c/+0x14`; `+0x18` char16* -> inline `+0x24` (33
  chars) **sender name**; `+0x68` display index; `+0x6c` race-player index; `+0x70/+0x71` shown flags.
- How a phrase index becomes text, and how `ChatSelect` buttons get their texts, was **not traced**.

### 3.4 In-world name tag - V unless marked

`UIControlRaceNameDisplay` (vtable 0x323e430, size 0x4c8, created per remote player by `UIPageRaceInfo`
setup `FUN_00adad04`; pointers at `racePage+0x7e8[]`, count `racePage+0x820`). Members: anims at
`+0x1c8 In, +0x1d8 Team, +0x1e8 Out, +0x1f8 StickerOnOff, +0x208 Crown`; `+0x1c0` `T_Text_00`;
`+0x270` target player object. Per-frame update = vtable slot 9 `FUN_003d4978`; it calls
`FUN_021e677c(ctl+0x208, on)` (crown) **only** when the target has `player+0x50 >= 0` and
`player+0xd0 & 1`, with `on = (*(FUN_00bafa2c(g)+0x158) != 0)`. I: for players without that flag the
crown is never touched, so it can be driven by us; what the flag and the crown mean in each mode
(leader? knockout winner?) is not confirmed.

## 4. Player identity (question 4)

| Thing | Where (V, also RE-MAP sections 1.4 and 2) |
|---|---|
| player object | from the race player list (`FUN_00b83250(ctx)+0xb0` count, `+0xb8` array). `+0x00` name SafeString (char16*, cap 0x21, inline at +0xc; getter `FUN_01a42c0c`), `+0x50` race-player index, `+0x58` character, `+0xd0` flags |
| index -> network id | `FUN_01e46660(&idx, &u8, &u32)`: table `t = *(*(0x33e7ee0) + 0x98)`, `v = *(t+0x10)`, count `*(v+8)`, 16-byte entries at `*(v+0x10)`: `+0` u8 (0xff = none), `+4` u32. RE-MAP: this is the table behind `ussid_map` ("player index" -> ussid) of `@@RefereeRequest` |
| network id -> index | `FUN_01e466c0(&u8, u32, &idxOut, 0)` (used by the chat receiver) |
| ussid -> user | server side: Gamesync `__us/<id>` (`uid`, `ussid`) and `PlayerStatic/<ussid>` (`name`, `p2p_sid`, `p2p_lid`) |

Practical key for voice: **the name**. The name shown in the list is `PlayerStatic.name` = the launcher
user name, and the probe's account id is `uuid5(URL, "mkwmp-user/" + lower(name))[0..8]` with the top bit
cleared (`native/probe/src/friends.c`), so `name -> account id` is computable on the client with no
lookup. I: names are unique per server (the launcher reserves gamertags). Where an exact id is needed,
use `index -> ussid` and let the server map ussid to the user. `nn::friends` ids only exist for players
in the launcher roster, so they are not a general key.

## 5. How to hook without override slots

Facts (V in `runtime/cpu/dispatch.c`, `hostfn.c`, `core/boot/gamelib.h` of 1.0.8; symbol presence V in
the 0.1.10 `mkw.exe` COFF table; behaviour on 0.1.10 not run):

- `hostfn_add_override`: fixed `added[64]`, fatal on the 65th; only before `ldr_load`. Budget is used up.
  The 7 `nn::fs` overrides only log and delegate, so they are the slots to free if one is needed.
- Guest functions are lifted C functions `void fn(Cpu*)`. Direct calls (`bl`) are direct C calls and
  cannot be redirected. **Indirect calls (every virtual call) go through `rt_dispatch`**: a flat
  read-only table for module code, then the host registry `g_host` (`{u64 addr, GuestFn fn}[8192]`, open
  addressing, hash `(addr >> 2) * 0x9E3779B97F4A7C15 >> 51`) for addresses outside the modules.
- COFF symbols present in 0.1.10: `g_host`, `g_nhost`, `g_ready`, `rt_dispatch`, `rt_dispatch_lookup`,
  `rt_register_host`, `rt_call`, `slots`, `nslots`, `hostfn_entry`. `mkwgame.dll` exports
  `mkwgame_tables()` (guest function address -> lifted function pointer for every module).
- RomFS overlays are a stock feature (`<game>/mods/<name>/romfs`; boot log "RomFS ready: ... 0 overlays").

**Vtable hook recipe (I, needs one live test):** pick an unused guest address outside all modules, insert
`{addr, our_fn}` into `g_host` (write `fn` first, then `addr`; `rt_register_host` itself refuses after
init), then overwrite the 8-byte vtable slot in guest memory with that address. Our `our_fn(Cpu*)` sees
`x0 = this` and calls the original with `rt_call(c, 0x7000000000 + original, n, ...)`. No override slot,
no x64 patching. Guest vtables are plain data in the guest image (may need `VirtualProtect`).
Calls into the game from our hook are ordinary `rt_call`s and run on the game's UI thread.

## 6. Recommended hook plan

Shared state in the probe: `talking[name]`, `muted[name]`, `voice_available`, fed by the voice engine.

### 6.1 Talking / muted indicator in the lobby list (lowest risk; all addresses V)

Hook `UIPageOnlinePauseMenu` vtable slot 11 (0x3248970 + 11*8, original `FUN_021b384c`) and slot 7
(+7*8, `FUN_021b22ec`). After the original, for `i < *(u32*)(page+0x3d80)`:
`e = (*(u64**)(page+0x3d88))[i]`, `ctl = page + 0x770 + i*0x150`, name = `*(char16**)(e+0x18)`.
- muted: rewrite the plate text with `FUN_021deb68(ctl, L"<name> (muted)")` (text of our choice, no MSBT
  needed), or write it to the hidden `T_VR_00` pane at `ctl+0xa8` and make it visible (visibility
  call not identified; the `Score` anim at `ctl+0x20` is the likely switch, I).
- talking: `FUN_021deaec(ctl, on)` (the host icon) is the only ready-made icon; it collides with the real
  host mark (`e+0x80`). Alternative with no collision: toggle the plate style `FUN_021de630(ctl, !on)`
  (the "me" highlight) for remote rows. Which looks right needs a look at the screen.
- Do not touch rows where `e+0x78` (me) is set unless showing the own mic state.

### 6.2 Talking indicator during free roam / race (I)

Hook `UIControlRaceNameDisplay` vtable slot 9 (0x323e430 + 9*8, original `FUN_003d4978`): after the
original, `p = *(u64*)(ctl+0x270)`; if `p` and `*(i32*)(p+0x50) >= 0` and `!(*(u32*)(p+0xd0) & 1)`:
`FUN_021e677c(ctl+0x208, talking[name(p)])` with name from `FUN_01a42c0c(p)`. Unconfirmed: the meaning of
flag bit 0, whether another code path also drives `Crown`, and whether calling `FUN_021e677c` every frame
restarts the animation (call it only on change). Fallback: prefix the tag text at `ctl+0x1c0`.

### 6.3 Mute / unmute toggle (needs work; nothing existing to repurpose)

Recommended: **a cursor on the existing list.** While the online pause menu is open, a button moves a
highlight over the plates (the `User` style, `FUN_021de630`) and another toggles `muted[name]` for the
highlighted entry (`e+0x18` name, `e+0x7c` index); state shown as in 6.1. All drawing calls are known.
Missing: **pad input.** Either free two `nn::fs` slots and observe `nn::hid::GetNpadStates`
(`_ZN2nn3hid13GetNpadStatesEPNS0_16NpadFullKeyStateEiRKj` and the `NpadHandheldState` /
`NpadJoyDualState` variants, all imported by `main`; delegate to the stock handler and read the result
buffer), or locate the game's own pad state object (not done). Pick buttons the page does not use
(which buttons the page and its "Settings/Controller" footer already consume was not checked).
A footer hint needs a new MSBT label through a RomFS overlay of `Mals/<lang>/MsgMain.msarc` (16 languages).

Alternatives, in order:
1. **Global "Voice chat: On/Off" as menu item 8.** Add labels (for example 9050/9051) to `Race.msbt` by
   RomFS overlay, point `0x32486c0[8]` at the label, append id 8 to the item list after
   `FUN_021b22ec` (node from the free list `page+0x34f0`, array `page+0x34e8`, count `page+0x34e0`,
   capacity `page+0x34e4`), relabel with `FUN_00a5cfd0` and set `BtnNum`. Intercept the decide callback
   (vtable 0x32488e0 slot 0, `FUN_021b4848` -> `FUN_021b39d0`): when the item is 8, toggle and do not
   call the original. **Only works when the list has fewer than 5 items**; online rooms usually have 5,
   so in practice this means replacing an item or shipping a modified `OnlinePauseMenu_00` layout.
2. **`ChatSelect` as a 24-entry player picker** (it is the only selectable 24-entry list): fill the
   button texts with names and catch the selection in `ChatDisplay` slot 35 (`FUN_00d3e6dc`, branch
   event 0xc76e139a) before `FUN_01a38a50` sends it. Blocked on two things not traced: how the button
   texts are set, and in which lobby states the page can be opened.
3. Mute from the launcher (no game change).

### 6.4 Voice availability / notices

There is no availability flag to answer. To tell the player something ("Voice chat connected",
"<name> muted") inside the game, queue a chat balloon: build the 0x78-byte item of 3.3 with any sender
name text and call `FUN_0215ba80(chatInfo, &item)` (V for the queue call; I that the phrase id can be one
added by overlay, and that balloons are drawn in every lobby state). `UIPageNotifications` (vtable
0x3247870, toast `T_Msg_00`) is the other candidate; its show function was not traced.

## 7. Not confirmed (do these before building on them)

| # | Open point | How to settle |
|---|---|---|
| 1 | `g_host` insertion + vtable slot rewrite works on 0.1.10 (table layout equal to 1.0.8 source, dispatch falls back to `g_host`, guest vtable memory writable) | 20-line test: hook slot 11 of 0x3248970, log once, open the pause menu in a single-player session |
| 2 | Which thread calls the hooked virtuals and whether `rt_call` from there is safe | same test, log thread id |
| 3 | Item-set modes (`info+0x64` 4 / 5) = which lobby types; buttons free in the pause menu | `MKW_SEQ_WATCH` + screenshots in a room |
| 4 | Consumer of `info+0x68` (what ids 8/9 do) | trace readers of event vtable 0x3248938 in `FreerunPauseMenu` (`FUN_00aa24fc` is 1000 lines; not read to the end) |
| 5 | `Crown` on name tags: meaning of `player+0xd0 & 1`, other writers | xref `FUN_021e677c` callers with `+0x208`; live look |
| 6 | `T_VR` visibility switch on the name plate | read `PlayerName_00_Score.bflan` targets |
| 7 | Chat: phrase id -> text, `ChatSelect` button text, where chat is enabled (`chatInfo+0x73`, node `ChatUIReady`) | decompile `UIControlChatSelectButton` (type info 0x216ff48), `FUN_0216f18c/0x216f238/0x216f2e4` |
| 8 | Name uniqueness and exact equality between the displayed name and the launcher name (truncation at 32 chars, case) | compare in a two-client run |

Tools left in `scratch/voice-re/`: `msg.py` (SARC + MSBT dump), `panes.py` (bflyt pane/texture/anim
dump, needs the `zstandard` copy in the re-map job dir), `q.py` (vtable / callers / refs over the
re-map index), `xr.py`, `fs.py`, `tbl.py`, `cs.py`, `dc.ps1` (Ghidra headless decompile, read-only).
