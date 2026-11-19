// mkwmp voice: the relay's datagrams (docs/contracts/VOICE_API.md §2). STUN indications (RFC 5389 header, magic
// 0x2112A442), big-endian, attributes padded to 4 bytes. Header only, no Windows: tests/voice_wire_test.cpp.
//   0x3E93 voice: txid {ver = 1, flags (bit 0 end of spurt), seq u16, ts u32 (20 ms units), up: 4 random | down: seat,
//          3 random}; attribute 0xC0F3 (up) / 0xC0F4 (down) = one Opus packet, 1-400 bytes; at most 548 bytes
//   0x3E94 control up, 0xC0F5: {ver, op (1 subscribe, 2 replace mutes, 3 leave), self (bit 0 mic muted, bit 1
//          deafened), count <= 32, count x account u64, ticket_len u16, ticket}; at most 1200 bytes
//   0x3E94 state down, 0xC0F6: {ver, room (bit 0 voice on), bundle (3 or 6), count <= 24, count x {seat, account u64,
//          state (bit 0 talking)}}
// Chat rooms (VOICE_API.md §6) and video/v1 (VIDEO_API.md); only a client in room mode sends or reads any of it:
//   control up, self: bit 2 my camera is on, bit 3 room mode (on every op 1/2 of a client in room mode)
//   control up, op 5 room request: {ver, 5, key_len 1..39, sharing key, want <= 4, want x account u64, rticket_len
//          u16, room ticket <= 512}; op 6 video subscribe: {ver, 6, count <= 4, count x account u64}
//   state down ver 2: the ver 1 body, then {room_id u64 (0 none), fps divisor (1, 2 or 4), members <= 4, members x
//          {account u64, flags (bit 0 camera on, bit 1 bound and in the voice room)}}
//   0x3E95 video: txid {ver = 1, flags (bit 0 key frame, bit 1 last chunk), frame u16, chunk u8, chunks u8 (1..64),
//          ts u32 ms, codec (1 MJPEG), up: 1 random | down: the sender's seat}; attribute 0xC0F7 (up) / 0xC0F8 (down)
//          = one chunk, 1-1100 bytes; at most 1124 bytes
#pragma once

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

namespace mkwmp::voice_wire {

constexpr uint16_t kVoiceKind = 0x3E93, kControlKind = 0x3E94, kVideoKind = 0x3E95;
constexpr uint16_t kVoiceUp = 0xC0F3, kVoiceDown = 0xC0F4, kControlUp = 0xC0F5, kStateDown = 0xC0F6, kVideoUp = 0xC0F7, kVideoDown = 0xC0F8;
constexpr uint8_t kVersion = 1;
constexpr uint32_t kMagic = 0x2112A442;
constexpr size_t kMaxVoiceDatagram = 548, kMaxControlDatagram = 1200, kMaxOpus = 400, kMaxMutes = 32, kMaxSeats = 24,
                 kMaxTicket = 900;
enum : uint8_t { kSubscribe = 1, kReplaceMutes = 2, kLeave = 3 };
enum : uint8_t { kSelfMicMuted = 1, kSelfDeafened = 2 };
enum : uint8_t { kEndOfSpurt = 1 };
enum : uint8_t { kRoomVoice = 1, kSeatTalking = 1 };
// chat rooms and video
constexpr uint8_t kVersionRooms = 2; // the state down of a relay that speaks rooms, to a client in room mode
constexpr size_t kMaxSharingKey = 39, kMaxRoomFriends = 4, kMaxRoomTicket = 512, kMaxVideoChunk = 1100, kMaxVideoDatagram = 1124,
                 kMaxVideoChunks = 64;
enum : uint8_t { kRoomRequest = 5, kVideoSubscribe = 6 };
enum : uint8_t { kSelfCameraOn = 4, kSelfRoomMode = 8 };
enum : uint8_t { kMemberCamera = 1, kMemberVoice = 2 };
enum : uint8_t { kVideoKeyFrame = 1, kVideoLastChunk = 2 };
constexpr uint8_t kCodecMjpeg = 1;

inline void Be16(uint8_t *p, uint16_t v) { p[0] = static_cast<uint8_t>(v >> 8); p[1] = static_cast<uint8_t>(v); }
inline void Be32(uint8_t *p, uint32_t v) { Be16(p, static_cast<uint16_t>(v >> 16)); Be16(p + 2, static_cast<uint16_t>(v)); }
inline void Be64(uint8_t *p, uint64_t v) { Be32(p, static_cast<uint32_t>(v >> 32)); Be32(p + 4, static_cast<uint32_t>(v)); }
inline uint16_t Rd16(const uint8_t *p) { return static_cast<uint16_t>(p[0] << 8 | p[1]); }
inline uint32_t Rd32(const uint8_t *p) { return static_cast<uint32_t>(Rd16(p)) << 16 | Rd16(p + 2); }
inline uint64_t Rd64(const uint8_t *p) { return static_cast<uint64_t>(Rd32(p)) << 32 | Rd32(p + 4); }

// A STUN indication with one attribute; empty past `max`.
inline std::vector<uint8_t> Indication(uint16_t kind, uint16_t attribute, const uint8_t *value, size_t size, const uint8_t txid[12], size_t max) {
    const size_t length = 24 + (size + 3) / 4 * 4;
    if (length > max) return {};
    std::vector<uint8_t> out(length, 0);
    Be16(out.data(), kind);
    Be16(out.data() + 2, static_cast<uint16_t>(length - 20));
    Be32(out.data() + 4, kMagic);
    std::memcpy(out.data() + 8, txid, 12);
    Be16(out.data() + 20, attribute);
    Be16(out.data() + 22, static_cast<uint16_t>(size));
    if (size) std::memcpy(out.data() + 24, value, size);
    return out;
}

// The attribute's value when `data` is a well-formed indication of `kind` carrying `attribute` (found anywhere; an
// attribute running past the end fails the whole datagram).
inline bool Attribute(const uint8_t *data, size_t size, uint16_t kind, uint16_t attribute, const uint8_t **value, size_t *value_size) {
    if (size < 20 || size > kMaxControlDatagram || size % 4 || (data[0] & 0xC0) || Rd32(data + 4) != kMagic || Rd16(data + 2) + 20u != size ||
        Rd16(data) != kind)
        return false;
    bool found = false;
    for (size_t at = 20; at + 4 <= size;) {
        const uint16_t tag = Rd16(data + at), count = Rd16(data + at + 2);
        if (count > size - at - 4) return false;
        if (tag == attribute && !found) {
            *value = data + at + 4;
            *value_size = count;
            found = true;
        }
        at += 4 + (count + 3u) / 4 * 4;
    }
    return found;
}

inline std::vector<uint8_t> EncodeVoiceUp(uint8_t flags, uint16_t seq, uint32_t ts, const uint8_t *opus, size_t size, const uint8_t random[4]) {
    if (size == 0 || size > kMaxOpus) return {};
    uint8_t txid[12] = {kVersion, flags};
    Be16(txid + 2, seq);
    Be32(txid + 4, ts);
    std::memcpy(txid + 8, random, 4);
    return Indication(kVoiceKind, kVoiceUp, opus, size, txid, kMaxVoiceDatagram);
}

// The relay's side (tests): voice down with the talker's seat.
inline std::vector<uint8_t> EncodeVoiceDown(uint8_t flags, uint16_t seq, uint32_t ts, uint8_t seat, const uint8_t *opus, size_t size, const uint8_t random[3]) {
    if (size == 0 || size > kMaxOpus) return {};
    uint8_t txid[12] = {kVersion, flags};
    Be16(txid + 2, seq);
    Be32(txid + 4, ts);
    txid[8] = seat;
    std::memcpy(txid + 9, random, 3);
    return Indication(kVoiceKind, kVoiceDown, opus, size, txid, kMaxVoiceDatagram);
}

struct VoiceDown {
    uint8_t flags = 0, seat = 0;
    uint16_t seq = 0;
    uint32_t ts = 0;
    const uint8_t *opus = nullptr; // into the datagram
    size_t size = 0;
};

inline bool DecodeVoiceDown(const uint8_t *data, size_t size, VoiceDown &out) {
    const uint8_t *value = nullptr;
    size_t count = 0;
    if (size > kMaxVoiceDatagram || !Attribute(data, size, kVoiceKind, kVoiceDown, &value, &count) || count == 0 || count > kMaxOpus ||
        data[8] != kVersion || data[16] >= kMaxSeats)
        return false;
    out = {data[9], data[16], Rd16(data + 10), Rd32(data + 12), value, count};
    return true;
}

// Op 1/2/3 with whatever self bits `self_mask` lets through.
inline std::vector<uint8_t> ControlUp(uint8_t op, uint8_t self, uint8_t self_mask, const std::vector<uint64_t> &mutes, const std::string &ticket,
                                      const uint8_t txid[12]) {
    if (op < kSubscribe || op > kLeave || (self & ~self_mask) || mutes.size() > kMaxMutes || ticket.empty() || ticket.size() > kMaxTicket) return {};
    std::vector<uint8_t> v(4 + mutes.size() * 8 + 2 + ticket.size(), 0);
    v[0] = kVersion;
    v[1] = op;
    v[2] = self;
    v[3] = static_cast<uint8_t>(mutes.size());
    size_t at = 4;
    for (uint64_t m : mutes) { Be64(v.data() + at, m); at += 8; }
    Be16(v.data() + at, static_cast<uint16_t>(ticket.size()));
    std::memcpy(v.data() + at + 2, ticket.data(), ticket.size());
    return Indication(kControlKind, kControlUp, v.data(), v.size(), txid, kMaxControlDatagram);
}

// voice/v1 as every released client sends it: self is the microphone and deafen bits and nothing else (self > 3 is
// refused: bits 2 and 3 never leave a client that is not in room mode).
inline std::vector<uint8_t> EncodeControlUp(uint8_t op, uint8_t self, const std::vector<uint64_t> &mutes, const std::string &ticket, const uint8_t txid[12]) {
    return ControlUp(op, self, kSelfMicMuted | kSelfDeafened, mutes, ticket, txid);
}

// The same from a client in room mode: bit 3 is on every one of them, bit 2 while its camera is on.
inline std::vector<uint8_t> EncodeControlUpRoom(uint8_t op, uint8_t self, const std::vector<uint64_t> &mutes, const std::string &ticket,
                                                const uint8_t txid[12]) {
    if (!(self & kSelfRoomMode)) return {};
    return ControlUp(op, self, kSelfMicMuted | kSelfDeafened | kSelfCameraOn | kSelfRoomMode, mutes, ticket, txid);
}

// Op 5: the room this player asks for. An empty want list leaves the room (the ticket may then be empty too).
inline std::vector<uint8_t> EncodeRoomRequest(const std::string &key, const std::vector<uint64_t> &want, const std::string &room_ticket,
                                              const uint8_t txid[12]) {
    if (key.empty() || key.size() > kMaxSharingKey || want.size() > kMaxRoomFriends || room_ticket.size() > kMaxRoomTicket ||
        (room_ticket.empty() && !want.empty()))
        return {};
    std::vector<uint8_t> v(3 + key.size() + 1 + want.size() * 8 + 2 + room_ticket.size(), 0);
    v[0] = kVersion;
    v[1] = kRoomRequest;
    v[2] = static_cast<uint8_t>(key.size());
    std::memcpy(v.data() + 3, key.data(), key.size());
    size_t at = 3 + key.size();
    v[at++] = static_cast<uint8_t>(want.size());
    for (uint64_t a : want) { Be64(v.data() + at, a); at += 8; }
    Be16(v.data() + at, static_cast<uint16_t>(room_ticket.size()));
    if (!room_ticket.empty()) std::memcpy(v.data() + at + 2, room_ticket.data(), room_ticket.size());
    return Indication(kControlKind, kControlUp, v.data(), v.size(), txid, kMaxControlDatagram);
}

// Op 6: whose pictures this player wants (members of its room with a camera).
inline std::vector<uint8_t> EncodeVideoSubscribe(const std::vector<uint64_t> &accounts, const uint8_t txid[12]) {
    if (accounts.size() > kMaxRoomFriends) return {};
    std::vector<uint8_t> v(3 + accounts.size() * 8, 0);
    v[0] = kVersion;
    v[1] = kVideoSubscribe;
    v[2] = static_cast<uint8_t>(accounts.size());
    for (size_t i = 0; i < accounts.size(); ++i) Be64(v.data() + 3 + i * 8, accounts[i]);
    return Indication(kControlKind, kControlUp, v.data(), v.size(), txid, kMaxControlDatagram);
}

// The relay's side (tests): any control up, by its op.
struct ControlUpMessage {
    uint8_t op = 0, self = 0;
    std::vector<uint64_t> accounts; // op 1/2 the mutes, op 5 the want list, op 6 the subscriptions
    std::string ticket, key;        // op 1/2/3 the seat ticket; op 5 the room ticket and the sharing key
};
inline bool DecodeControlUp(const uint8_t *data, size_t size, ControlUpMessage &out) {
    const uint8_t *v = nullptr;
    size_t n = 0;
    if (!Attribute(data, size, kControlKind, kControlUp, &v, &n) || n < 3 || v[0] != kVersion) return false;
    ControlUpMessage m;
    m.op = v[1];
    size_t at = 0, count = 0;
    if (m.op >= kSubscribe && m.op <= kLeave) {
        if (n < 4) return false;
        m.self = v[2];
        count = v[3];
        at = 4;
        if (count > kMaxMutes) return false;
    } else if (m.op == kRoomRequest) {
        const size_t key = v[2];
        if (key == 0 || key > kMaxSharingKey || n < 3 + key + 1) return false;
        m.key.assign(reinterpret_cast<const char *>(v + 3), key);
        count = v[3 + key];
        at = 3 + key + 1;
        if (count > kMaxRoomFriends) return false;
    } else if (m.op == kVideoSubscribe) {
        count = v[2];
        at = 3;
        if (count > kMaxRoomFriends || n != 3 + count * 8) return false;
    } else {
        return false;
    }
    if (n < at + count * 8) return false;
    for (size_t i = 0; i < count; ++i) m.accounts.push_back(Rd64(v + at + i * 8));
    at += count * 8;
    if (m.op != kVideoSubscribe) {
        if (n < at + 2 || n != at + 2 + Rd16(v + at)) return false;
        m.ticket.assign(reinterpret_cast<const char *>(v + at + 2), Rd16(v + at));
    }
    out = std::move(m);
    return true;
}

struct Seat {
    uint8_t seat = 0;
    uint64_t account = 0;
    uint8_t state = 0;
};
struct Member {
    uint64_t account = 0;
    uint8_t flags = 0; // kMemberCamera, kMemberVoice
};
struct StateDown {
    uint8_t room = 0, bundle = 3;
    std::vector<Seat> seats;
    // ver 2 (a relay that speaks rooms, to a client in room mode); ver 1 leaves these as they are here
    uint8_t version = kVersion;
    uint64_t room_id = 0;        // 0: in no chat room
    uint8_t fps = 1;             // the send divisor: 1, 2 or 4
    std::vector<Member> members; // the room's members, at most 4
};

inline bool DecodeStateDown(const uint8_t *data, size_t size, StateDown &out) {
    const uint8_t *v = nullptr;
    size_t count = 0;
    if (!Attribute(data, size, kControlKind, kStateDown, &v, &count) || count < 4 || (v[0] != kVersion && v[0] != kVersionRooms)) return false;
    const size_t seats = v[3], tail = 4 + seats * 10;
    if ((v[2] != 3 && v[2] != 6) || seats > kMaxSeats) return false;
    StateDown s;
    s.version = v[0];
    if (s.version == kVersion) {
        if (count != tail) return false;
    } else {
        if (count < tail + 10) return false;
        const size_t members = v[tail + 9];
        if (members > kMaxRoomFriends || count != tail + 10 + members * 9) return false;
        s.room_id = Rd64(v + tail);
        s.fps = v[tail + 8] == 2 || v[tail + 8] == 4 ? v[tail + 8] : 1;
        for (size_t i = 0; i < members; ++i) s.members.push_back({Rd64(v + tail + 10 + i * 9), v[tail + 10 + i * 9 + 8]});
    }
    s.room = v[1];
    s.bundle = v[2];
    for (size_t i = 0; i < seats; ++i) {
        const uint8_t *at = v + 4 + i * 10;
        if (at[0] >= kMaxSeats) return false;
        s.seats.push_back({at[0], Rd64(at + 1), at[9]});
    }
    out = std::move(s);
    return true;
}

// The relay's side (tests).
inline std::vector<uint8_t> EncodeStateDown(const StateDown &s, const uint8_t txid[12]) {
    if (s.seats.size() > kMaxSeats || s.members.size() > kMaxRoomFriends) return {};
    const bool rooms = s.version == kVersionRooms;
    const size_t tail = 4 + s.seats.size() * 10;
    std::vector<uint8_t> v(tail + (rooms ? 10 + s.members.size() * 9 : 0), 0);
    v[0] = rooms ? kVersionRooms : kVersion;
    v[1] = s.room;
    v[2] = s.bundle;
    v[3] = static_cast<uint8_t>(s.seats.size());
    for (size_t i = 0; i < s.seats.size(); ++i) {
        uint8_t *at = v.data() + 4 + i * 10;
        at[0] = s.seats[i].seat;
        Be64(at + 1, s.seats[i].account);
        at[9] = s.seats[i].state;
    }
    if (rooms) {
        Be64(v.data() + tail, s.room_id);
        v[tail + 8] = s.fps;
        v[tail + 9] = static_cast<uint8_t>(s.members.size());
        for (size_t i = 0; i < s.members.size(); ++i) {
            Be64(v.data() + tail + 10 + i * 9, s.members[i].account);
            v[tail + 10 + i * 9 + 8] = s.members[i].flags;
        }
    }
    return Indication(kControlKind, kStateDown, v.data(), v.size(), txid, kMaxControlDatagram);
}

// ---- video/v1: one chunk of a frame per datagram

struct VideoChunk {
    uint8_t flags = 0, chunk = 0, chunks = 0, codec = 0;
    uint8_t seat = 0;              // down: the sender's seat; up: the random byte
    uint16_t frame = 0;
    uint32_t ts = 0;               // the sender's clock, ms
    const uint8_t *data = nullptr; // into the datagram
    size_t size = 0;
};

inline std::vector<uint8_t> Video(uint16_t attribute, uint8_t flags, uint16_t frame, uint8_t chunk, uint8_t chunks, uint32_t ts, uint8_t codec,
                                  uint8_t last, const uint8_t *data, size_t size) {
    if (size == 0 || size > kMaxVideoChunk || chunks == 0 || chunks > kMaxVideoChunks || chunk >= chunks) return {};
    uint8_t txid[12] = {kVersion, flags};
    Be16(txid + 2, frame);
    txid[4] = chunk;
    txid[5] = chunks;
    Be32(txid + 6, ts);
    txid[10] = codec;
    txid[11] = last;
    return Indication(kVideoKind, attribute, data, size, txid, kMaxVideoDatagram);
}

inline std::vector<uint8_t> EncodeVideoUp(uint8_t flags, uint16_t frame, uint8_t chunk, uint8_t chunks, uint32_t ts, uint8_t codec, uint8_t random,
                                          const uint8_t *data, size_t size) {
    return Video(kVideoUp, flags, frame, chunk, chunks, ts, codec, random, data, size);
}

// The relay's side (tests): video down with the sender's seat.
inline std::vector<uint8_t> EncodeVideoDown(uint8_t flags, uint16_t frame, uint8_t chunk, uint8_t chunks, uint32_t ts, uint8_t codec, uint8_t seat,
                                            const uint8_t *data, size_t size) {
    return Video(kVideoDown, flags, frame, chunk, chunks, ts, codec, seat, data, size);
}

inline bool DecodeVideo(const uint8_t *data, size_t size, uint16_t attribute, VideoChunk &out) {
    const uint8_t *value = nullptr;
    size_t count = 0;
    if (size > kMaxVideoDatagram || !Attribute(data, size, kVideoKind, attribute, &value, &count) || count == 0 || count > kMaxVideoChunk ||
        data[8] != kVersion || data[13] == 0 || data[13] > kMaxVideoChunks || data[12] >= data[13])
        return false;
    out = {data[9], data[12], data[13], data[18], data[19], Rd16(data + 10), Rd32(data + 14), value, count};
    return true;
}
inline bool DecodeVideoDown(const uint8_t *data, size_t size, VideoChunk &out) {
    return DecodeVideo(data, size, kVideoDown, out) && out.seat < kMaxSeats;
}
// The relay's side (tests).
inline bool DecodeVideoUp(const uint8_t *data, size_t size, VideoChunk &out) { return DecodeVideo(data, size, kVideoUp, out); }

} // namespace mkwmp::voice_wire
