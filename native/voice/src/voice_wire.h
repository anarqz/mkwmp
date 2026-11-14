// mkwmp voice: the relay's datagrams (docs/contracts/VOICE_API.md §2). STUN indications (RFC 5389 header, magic
// 0x2112A442), big-endian, attributes padded to 4 bytes. Header only, no Windows: tests/voice_wire_test.cpp.
//   0x3E93 voice: txid {ver = 1, flags (bit 0 end of spurt), seq u16, ts u32 (20 ms units), up: 4 random | down: seat,
//          3 random}; attribute 0xC0F3 (up) / 0xC0F4 (down) = one Opus packet, 1-400 bytes; at most 548 bytes
//   0x3E94 control up, 0xC0F5: {ver, op (1 subscribe, 2 replace mutes, 3 leave), self (bit 0 mic muted, bit 1
//          deafened), count <= 32, count x account u64, ticket_len u16, ticket}; at most 1200 bytes
//   0x3E94 state down, 0xC0F6: {ver, room (bit 0 voice on), bundle (3 or 6), count <= 24, count x {seat, account u64,
//          state (bit 0 talking)}}
#pragma once

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

namespace mkwmp::voice_wire {

constexpr uint16_t kVoiceKind = 0x3E93, kControlKind = 0x3E94;
constexpr uint16_t kVoiceUp = 0xC0F3, kVoiceDown = 0xC0F4, kControlUp = 0xC0F5, kStateDown = 0xC0F6;
constexpr uint8_t kVersion = 1;
constexpr uint32_t kMagic = 0x2112A442;
constexpr size_t kMaxVoiceDatagram = 548, kMaxControlDatagram = 1200, kMaxOpus = 400, kMaxMutes = 32, kMaxSeats = 24,
                 kMaxTicket = 900;
enum : uint8_t { kSubscribe = 1, kReplaceMutes = 2, kLeave = 3 };
enum : uint8_t { kSelfMicMuted = 1, kSelfDeafened = 2 };
enum : uint8_t { kEndOfSpurt = 1 };
enum : uint8_t { kRoomVoice = 1, kSeatTalking = 1 };

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

inline std::vector<uint8_t> EncodeControlUp(uint8_t op, uint8_t self, const std::vector<uint64_t> &mutes, const std::string &ticket, const uint8_t txid[12]) {
    if (op < kSubscribe || op > kLeave || self > 3 || mutes.size() > kMaxMutes || ticket.empty() || ticket.size() > kMaxTicket) return {};
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

struct Seat {
    uint8_t seat = 0;
    uint64_t account = 0;
    uint8_t state = 0;
};
struct StateDown {
    uint8_t room = 0, bundle = 3;
    std::vector<Seat> seats;
};

inline bool DecodeStateDown(const uint8_t *data, size_t size, StateDown &out) {
    const uint8_t *v = nullptr;
    size_t count = 0;
    if (!Attribute(data, size, kControlKind, kStateDown, &v, &count) || count < 4 || v[0] != kVersion) return false;
    const size_t seats = v[3];
    if ((v[2] != 3 && v[2] != 6) || seats > kMaxSeats || count != 4 + seats * 10) return false;
    StateDown s;
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
    if (s.seats.size() > kMaxSeats) return {};
    std::vector<uint8_t> v(4 + s.seats.size() * 10, 0);
    v[0] = kVersion;
    v[1] = s.room;
    v[2] = s.bundle;
    v[3] = static_cast<uint8_t>(s.seats.size());
    for (size_t i = 0; i < s.seats.size(); ++i) {
        uint8_t *at = v.data() + 4 + i * 10;
        at[0] = s.seats[i].seat;
        Be64(at + 1, s.seats[i].account);
        at[9] = s.seats[i].state;
    }
    return Indication(kControlKind, kStateDown, v.data(), v.size(), txid, kMaxControlDatagram);
}

} // namespace mkwmp::voice_wire
