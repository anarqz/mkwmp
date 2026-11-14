# Contract: voice chat (`voice/v1`)

Lobby voice chat, modelled on ForzaInfinity's voice (its `docs/launcher/VOICE_CHAT.md`): the same
engine (WASAPI, WebRTC AEC3 echo cancellation, AGC, RNNoise noise suppression and gate, Opus 6
kbit/s SILK wideband, 3 x 20 ms per datagram) and the same STUN-shaped side channel, adapted to
mkwmp. **Every game lobby is one voice room.** No overlay: settings live in the launcher, mutes in
the game's own menus.

Owners:

- voice relay (UDP, inside `mkwmp lobby` / `mkwmp host`), launcher settings and the local API
  endpoints below: MACBOOK-MKWMP (`rust/gateway`, `rust/mkwmp`, `rust/client`);
- room membership (`lobby::Core::voice_room`), the probe and the voice engine
  (`native/voice`, `mkwmp-voice.dll`): WINDOWS-MKWMP.

## 1. Rooms and identities

- A player's **account id** is the u64 the probe already uses for friends: the first 8 bytes of
  the uuid5 user id (`uuid5(lower(name))`), big-endian, top bit cleared.
- A player's **room** is the lobby the Core has them in (`Matchmaker::lobby_of(uid)`); the relay
  asks `Core::voice_room(uid) -> Option<VoiceRoom { lobby: LobbyId, members: Vec<String /*uid*/> }>`
  each time it routes (cheap; no copy of lobby state in the relay). Not in a lobby: no room, no voice.
- **Seat**: a stable u8 0..11 per (room, uid), handed out by the relay in subscribe order, kept while
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
| `0x3E94` / `0xC0F6` state down | relay -> probe | `ver=1`, `room` (bit0 in a room with voice), `bundle` (3; 6 when the relay is over budget), `count u8` (<= 12), `count x {seat u8, account u64, state u8 (bit0 talking now)}`. Every subscriber of the room, the listener included. |

## 3. Relay rules (authority)

1. **Subscribe** (op 1/2): verify the ticket exactly like the lobby's ticket check (Ed25519, not
   expired); uid = uuid5 of the ticket's name. Bind the source endpoint to that uid (a new endpoint
   for the same uid replaces the old one). Store `self` and the mute set. op 2 = same, the mute set
   is replaced. Refresh every 5 s from the probe; a binding with no control up for **15 s** is gone.
2. **Leave** (op 3, needs the ticket too): drop the binding at once.
3. **Voice up** from a bound endpoint whose uid has a room and is not mic-muted: forward as voice down
   (seat stamped) to every **other** bound member of the same room who is not deafened and whose mute
   set does not hold the talker's account id. A listener never learns who muted them.
4. **Limits** per endpoint: 60 voice datagrams/s and 4 KiB/s up (drop the excess); at most 4
   simultaneous talkers forwarded per room (talking = voice within the last 300 ms).
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
