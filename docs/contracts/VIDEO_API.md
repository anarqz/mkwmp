# Contract: chat-room video (`video/v1`, draft, branch `chat-rooms`; not deployed)

Camera pictures in friend rooms (`VOICE_API.md` 6). They ride on the voice relay: the same UDP
socket (lobby host, port 8907), the same bindings, rooms, seats and mute sets. The relay forwards
pictures live. It **never decodes, transcodes, records or stores them**, and sends them only to
the sender's mutual friends in its friend room.

Owners: the relay, MACBOOK-MKWMP (`rust/gateway`); the camera, encoder and decoder in the game
patch, WINDOWS-MKWMP.

## 1. Datagrams

STUN-shaped, as in VOICE_API.md 2.

| Kind / attribute | Dir | Value |
| --- | --- | --- |
| `0x3E95` / `0xC0F7` video up | probe -> relay | txid: `ver=1`, `flags` (bit0 key frame, bit1 last chunk of the frame), `frame u16` (wraps), `chunk u8`, `chunks u8` (1..=64), `ts u32` (ms), `codec u8` (1 = MJPEG), 1 random byte. Attribute: 1..=1100 bytes of the encoded frame. Max 1124 bytes. |
| `0x3E95` / `0xC0F8` video down | relay -> probe | the same, with the sender's `seat u8` stamped in place of the random byte. Attribute unchanged. |

- A frame is the concatenation of chunks `0..chunks` in order. No identity in the datagram: the
  relay knows the sender by its bound endpoint, and state down maps seats to accounts.
- **MJPEG (codec 1)**: every frame is one baseline JPEG and stands alone (bit0 is always set). Its
  width and height are the JPEG's own (SOF0). Suggested: 256x144, 10 fps, quality about 40, about
  4-5 KiB per frame. The encoder must keep each frame at **6 KiB or less** (adapting the quality;
  q 40 at 256x144 is the starting point) so 10 fps fits the 64 KiB/s cap (3.3), and must divide
  its frame rate by state down's `fps`. A frame with a missing chunk is dropped: no retransmission or key-frame
  requests.
- Codec 2 (H.264) is reserved for later; a relay forwards any codec byte unchanged.

## 2. Control up (VOICE_API.md 2 / 6.1)

- **`self` bit2 (`0x04`) = my camera is on.** Off is the game's Camera Off, or no camera: a
  client whose player picked Camera Off in the game's prompt sends bit2 = 0 for that whole game
  session.
- **op 6, subscribe video**, `ver=1`, from a bound room-mode endpoint: `ver u8 =1, op u8 =6,
  count u8 (0..=4), count x account u64`, the friends whose pictures this player wants (at most
  four faces). `count = 0` = none. More than 4 is dropped. It is re-sent with every refresh and
  lapses with the binding.

## 3. Relay rules

1. Video up is accepted only from a bound, room-mode subscriber in a friend room with `self`
   bit2 set. Everything else is dropped.
2. It is forwarded, seat stamped, to each other room-mode subscriber of the **same room** who is
   the sender's **mutual friend** and subscribed to the sender's account (op 6), and whose mute
   set doesn't hold the sender. **A mute hides the face too.** Deafen doesn't affect video.
   Nothing goes back to the sender.
3. Per sender: at most 64 KiB/s and 400 datagrams/s up; the excess is dropped.
4. Video egress budget, separate from voice: 8 MiB/s by default (`[lobby] video_budget`). When
   the last second's video egress is past it, state down v2 sets `fps` to 2, then 4, and back to 1
   once it has been under half the budget for 5 s. Senders divide their frame rate by `fps`.
5. `[lobby] video = false` turns it off: the relay drops all video up and never sets the camera
   flag (VOICE_API.md 6.2). Default `true`.
6. State down v2 `members[].flags` bit0 = that friend is bound with `self` bit2 set.

## 4. Reports

A report names what it is about: `POST /seat/v1/reports` takes an optional
`kind: "voice" | "camera"` (default `"voice"`); `Report` gains `kind`; `mkwmp admin reports`
shows it. The reasons stay the same.

## 5. Privacy

Before this is deployed, the site's privacy policy says: voice works only in friend rooms and goes
to the players in that room; camera pictures go only to your mutual friends in that room; both
pass through the relay live and are never recorded or kept.

## 6. Test vectors (hex, `rust/gateway/src/voice/tests.rs` `room_wire_round_trips_and_vectors`)

- Video up, txid `01 03 0007 00 01 000003e8 01 5a` (ver 1, key frame + last chunk, frame 7, chunk 0
  of 1, ts 1000 ms, MJPEG, random `5a`), chunk `ff d8 ff`:
  `3e9500082112a442010300070001000003e8015ac0f70003ffd8ff00`
- The same as video down from seat 4:
  `3e9500082112a442010300070001000003e80104c0f80003ffd8ff00`
