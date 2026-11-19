# Contract: voice chat (`voice/v1`)

Lobby voice chat, modelled on ForzaInfinity's voice (its `docs/launcher/VOICE_CHAT.md`): the same
engine (WASAPI, WebRTC AEC3 echo cancellation, AGC, RNNoise noise suppression and gate, Opus 6
kbit/s SILK wideband, 3 x 20 ms per datagram) and the same STUN-shaped side channel, adapted to
mkwmp. **Every friend room is one voice room** (ONLINE → FRIENDS → a room you create or join);
matchmade lobbies have no voice (§6). No overlay: settings live in the launcher, mutes in
the game's own menus.

Owners:

- voice relay (UDP, inside `mkwmp lobby` / `mkwmp host`), launcher settings and the local API
  endpoints below: MACBOOK-MKWMP (`rust/gateway`, `rust/mkwmp`, `rust/client`);
- room membership (`lobby::Core::voice_room`), the probe and the voice engine
  (`native/voice`, `mkwmp-voice.dll`): WINDOWS-MKWMP.

## 1. Rooms and identities

- A player's **account id** is the u64 the probe already uses for friends: the first 8 bytes of
  the uuid5 user id (`uuid5(lower(name))`), big-endian, top bit cleared.
- A player's **room** is the friend room the Core has them in (`Matchmaker::lobby_of(uid)`, a
  lobby made with CreateGameSessionCreationTicket; a matchmade lobby is no room); the relay
  asks `Core::voice_room(uid) -> Option<VoiceRoom { lobby: LobbyId, members: Vec<String /*uid*/> }>`
  each time it routes (cheap; no copy of lobby state in the relay). Not in a lobby, or the lobby is
  still gathering (no assignment sent yet: the players are not in one world): no room, no voice.
- **Seat**: a stable u8 0..23 per (room, uid) (a lobby holds up to 24 players), handed out by the relay in subscribe order, kept while
  the subscriber stays in that room, freed when it leaves. Voice down carries the seat; state down
  maps seats to account ids.

## 2. Transport

One UDP socket on the lobby host, **port 8907** by default (`[lobby] voice_port`, 0 = voice off).
The probe sends to the lobby host it was seated on (`lobby` of the seat reply, else `server`) at
`voice_port` (mkwmp.toml `voice_port`, default 8907). Every datagram is a STUN indication: 20-byte
header (type, length, magic `0x2112A442`, 12-byte transaction id), then attributes (type u16,
length u16, value padded to 4 bytes), big-endian. Anything else is dropped silently.

| Kind / attribute | Dir | Value |
| --- | --- | --- |
| `0x3E93` / `0xC0F3` voice up | probe -> relay | txid: `ver=1`, `flags` (bit0 end of talk spurt), `seq u16`, `ts u32` (20 ms units), 4 random bytes. Attribute: one Opus packet, 1-400 bytes. No identity: the relay knows the sender by the endpoint its subscribe bound. Max 548 bytes. |
| `0x3E93` / `0xC0F4` voice down | relay -> probe | txid: `ver`, `flags`, `seq`, `ts` copied, `seat u8` (the talker's, stamped by the relay), 3 random bytes. Attribute: the Opus packet unchanged. |
| `0x3E94` / `0xC0F5` control up | probe -> relay | `ver=1`, `op` (1 subscribe/refresh, 2 replace mutes, 3 leave), `self` (bit0 mic muted, bit1 deafened), `count u8` (<= 32), `count x account u64` (the players this listener muted), `ticket_len u16`, `ticket` (the seat ticket `mkw1...`, <= 900 bytes). Max 1200 bytes. |
| `0x3E94` / `0xC0F6` state down | relay -> probe | `ver=1`, `room` (bit0 in a room with voice), `bundle` (3; 6 when the relay's voice egress over the last second is past its budget, 2 MiB/s), `count u8` (<= 24), `count x {seat u8, account u64, state u8 (bit0 talking now)}`. Every subscriber of the room, the listener included. |

Notes on the table: lengths in the STUN header count the padded attribute; the attribute length
is the unpadded value length. A relay seat that is full (25th subscriber) gets state down without
a seat for it and none of its voice is forwarded.

### 2.1 Test vectors (hex, `rust/gateway/src/voice/tests.rs` `contract_vectors`)

- Voice up, txid `01 00 002a 0000007e deadbeef` (ver 1, flags 0, seq 42, ts 126), Opus `f8 ff fe`:
  `3e9300082112a4420100002a0000007edeadbeefc0f30003f8fffe00`
- The same as voice down at seat 5, random `11 22 33`:
  `3e9300082112a4420100002a0000007e05112233c0f40003f8fffe00`
- Control up, txid zero, op 1, self 2 (deafened), one mute `0x0123456789abcdef`, ticket `mkw1.a.b`:
  `3e94001c2112a442000000000000000000000000c0f50016010102010123456789abcdef00086d6b77312e612e620000`
- State down, txid zero, in a room, bundle 3, seat 2 = account `0x0123456789abcdef` talking:
  `3e9400142112a442000000000000000000000000c0f6000e01010301020123456789abcdef010000`

## 3. Relay rules (authority)

1. **Subscribe** (op 1/2; either binds an unbound endpoint): verify the ticket exactly like the lobby's ticket check (Ed25519, not
   expired); uid = uuid5 of the ticket's name. Bind the source endpoint to that uid (a new endpoint
   for the same uid replaces the old one). Store `self` and the mute set. op 2 = same, the mute set
   is replaced. Refresh every 5 s from the probe; a binding with no control up for **15 s** is gone.
2. **Leave** (op 3, needs the ticket too, from the bound endpoint): drop the binding at once.
3. **Voice up** from a bound endpoint whose uid has a room and is not mic-muted: forward as voice down
   (seat stamped) to every **other** bound member of the same room who is not deafened and whose mute
   set does not hold the talker's account id. A listener never learns who muted them.
4. **Limits** per endpoint: 60 voice datagrams/s and 4 KiB/s up (drop the excess); at most 4
   simultaneous talkers forwarded per room (talking = voice within the last 300 ms; a fifth is
   dropped until one of the four stops). Control up is limited per source address (20/s, burst 40)
   before its ticket is checked.
5. **State down** to each subscriber: on subscribe, whenever the room's member set, seats or
   talking flags change, and at least every 1 s.
6. Datagrams from unbound endpoints (except control up) are dropped; nothing is ever echoed back to
   the talker.

## 4. Launcher local API (`/local/v1/voice`, bearer `launcher_token`)

| Call | Body / answer |
| --- | --- |
| `GET /local/v1/voice` | `200 {"enabled":true,"input":"<device id or empty>","output":"<device id or empty>","inputGain":100,"outputGain":100,"mutes":[{"account":"<u64 decimal>","name":"..."}]}`. Polled by the probe every 2 s; a change applies live. |
| `POST /local/v1/voice/devices` | `{"inputs":[{"id","name"}],"outputs":[{"id","name"}]}` as the game process sees them (under Proton these are Wine's ids, which a native launcher cannot enumerate). The launcher keeps the last list (persisted) for its settings pane. `204`. |
| `POST /local/v1/voice/mute` | `{"account":"<u64 decimal>","name":"...","muted":true}`: a mute from the game's menus. The launcher persists the list (by account id) and returns it in the next GET. `204`. |

Settings pane (launcher SETTINGS): **Voice chat** on/off (default **on**), **Microphone** and
**Output** dropdowns ("System default" first = empty id, then the devices the game reported; before
the first game run only "System default"), mic and voice volume sliders 1-100 (default 100),
**Muted players** list with Unmute. Saved in `client.toml` `[voice]`.

## 5. Client behaviour (probe + voice engine)

- Voice runs only while the probe is online and the launcher says `enabled`.
- Capture: WASAPI -> 48 kHz mono -> 100 Hz high-pass + AEC3 (reference = loopback of the output in
  use, else the voice mix) -> AGC to -20 dBFS -> mic gain -> RNNoise (gate on voice probability,
  300 ms hangover) -> Opus.
- Playback: per-talker jitter buffer, Opus decode with deep PLC, **per-talker loudness
  normalization** (each talker brought to -20 dBFS speech level, -12..+12 dB, slow attack, no
  pumping), per-talker gain, mix, **mix limiter** (hard ceiling -3 dBFS, soft knee): nobody sounds
  louder than anyone else and no scream reaches the ears at full scale.
- Mutes from the game's menus go to the relay (op 2) and to the launcher (`POST /voice/mute`).

## 6. Friend rooms: voice and camera (draft, branch `chat-rooms`; not deployed)

Voice and camera work only in **friend rooms**, the private rooms players create or join under
ONLINE → FRIENDS. Joining one is the player's choice to talk to the people in it. Matchmade races
and battles have no voice and no camera.

- **Voice: everyone in the room hears everyone** (rule 3.3: mutes and deafen as before). The
  room is the friend room the lobby has the player in (§1), nothing else. A released client
  (lobby-wide, v1) in a friend room hears and is heard like everyone else; in a matchmade lobby
  it gets "no room" in state down and no voice.
- **Camera: only between mutual friends in the same room**, as the seat roster names them (the
  lobby already holds each player's mutual friends; a dev lobby without a roster counts everyone
  as a friend). A player sees at most **4 faces** at once: the client picks which (friends it
  asked for first), the relay enforces mutual friendship and the limit.
- The relay looks the room and the friends up again at least every second, so someone who leaves
  the room or is unfriended stops getting pictures within a second.

### 6.1 Room mode (control up)

- **op 1/2, `self` bit3 (`0x08`) = room mode**: this client speaks state down v2 and video.
  Send it on every op 1/2. Clearing it drops the client's video subscriptions. An old relay ignores
  the bit and keeps sending `ver=1` state down, which tells the client there is no video.
- **`self` bit2 (`0x04`) = camera on** (VIDEO_API.md §2).
- **op 6, video subscribe**: VIDEO_API.md §2.

Released clients never set bits 2 or 3 (their voice library builds `self` from mic-muted and
deafened only and refuses any other bit), so they always get v1.

### 6.2 State down v2 (to room-mode subscribers only)

`ver=2`, `room`, `bundle`, `count`, `count x {seat, account, state}` as in v1 (every subscriber of
the room, talking as heard), then `room_id u64` (the room, non-zero; 0 = no room), `fps u8` (video
frame-rate divisor, 1, 2 or 4; VIDEO_API.md §3), `members u8` (0..=23), `members x {account u64,
flags u8}`: the listener's **mutual friends** bound in the room, in seat order. Flags: bit0 camera
on, bit1 bound. `account` is the account id of §1, the id the game knows that player by
(`lobby::account_id(ticket_user_id(name))`: the first 8 bytes of the user id
`uuid5(URL, "mkwmp-user/" + lower(name))`, big-endian, top bit cleared; the game patch's
`friends_account_id` is the same function). Sent with v1's rules (§3.5) and whenever a friend's
camera flag or the friends in the room change.

### 6.3 Test vectors (hex, `rust/gateway/src/voice/tests.rs` `room_wire_round_trips_and_vectors`)

- Op 6, txid zero, one account `0x0123456789abcdef`:
  `3e9400102112a442000000000000000000000000c0f5000b0106010123456789abcdef00`
- State down v2, txid zero, in a room, bundle 3, seat 2 = `0x0123456789abcdef` talking, room
  `0x1122334455667788`, fps 1, one member `0x0123456789abcdef` bound with its camera on:
  `3e9400282112a442000000000000000000000000c0f6002102010301020123456789abcdef0111223344556677880101`
  `0123456789abcdef03000000`
