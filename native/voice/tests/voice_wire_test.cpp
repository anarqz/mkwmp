// mkwmp voice: the relay datagrams (voice_wire.h, VOICE_API.md §2). Run by build.py --test.
#include "voice_wire.h"

#include <cstdio>
#include <cstring>

using namespace mkwmp::voice_wire;

static int failures = 0;
static void Check(bool ok, const char *what) {
    std::printf("%s %s\n", ok ? "ok  " : "FAIL", what);
    if (!ok) failures++;
}

int main() {
    const uint8_t opus[5] = {0x78, 1, 2, 3, 4}, r4[4] = {9, 9, 9, 9}, r3[3] = {7, 7, 7};
    auto up = EncodeVoiceUp(kEndOfSpurt, 0x1234, 0xA0B0C0D0, opus, sizeof opus, r4);
    Check(up.size() == 32 && Rd16(up.data()) == kVoiceKind && Rd32(up.data() + 4) == kMagic && up[8] == 1 && up[9] == 1 &&
              Rd16(up.data() + 10) == 0x1234 && Rd32(up.data() + 12) == 0xA0B0C0D0 && Rd16(up.data() + 20) == kVoiceUp &&
              Rd16(up.data() + 22) == 5 && up[24] == 0x78,
          "voice up layout");
    auto down = EncodeVoiceDown(0, 7, 99, 23, opus, sizeof opus, r3);
    VoiceDown v;
    Check(DecodeVoiceDown(down.data(), down.size(), v) && v.seat == 23 && v.seq == 7 && v.ts == 99 && v.size == 5 && v.opus[0] == 0x78,
          "voice down round trip, seat 23");
    auto bad = EncodeVoiceDown(0, 7, 99, 24, opus, sizeof opus, r3);
    Check(!DecodeVoiceDown(bad.data(), bad.size(), v), "seat 24 refused");
    Check(!DecodeVoiceDown(up.data(), up.size(), v), "voice up is not voice down");
    down[3] ^= 4; // a wrong length
    Check(!DecodeVoiceDown(down.data(), down.size(), v), "bad length refused");

    const uint8_t tx[12] = {};
    std::string ticket(512, 'k');
    auto ctl = EncodeControlUp(kSubscribe, kSelfDeafened, {1, 0x7fffffffffffffffull}, ticket, tx);
    const uint8_t *val = nullptr;
    size_t n = 0;
    Check(Attribute(ctl.data(), ctl.size(), kControlKind, kControlUp, &val, &n) && n == 4 + 16 + 2 + 512 && val[0] == 1 && val[1] == 1 &&
              val[2] == 2 && val[3] == 2 && Rd64(val + 12) == 0x7fffffffffffffffull && Rd16(val + 20) == 512 && val[22] == 'k',
          "control up layout with a 512-byte ticket");
    Check(ctl.size() <= kMaxControlDatagram, "control up within 1200 bytes");
    Check(EncodeControlUp(kSubscribe, 0, std::vector<uint64_t>(33, 1), ticket, tx).empty(), "33 mutes refused");

    StateDown s;
    s.room = 1;
    s.bundle = 3;
    for (uint8_t i = 0; i < 24; i++) s.seats.push_back({i, 1000u + i, static_cast<uint8_t>(i & 1)});
    auto st = EncodeStateDown(s, tx);
    StateDown got;
    Check(st.size() == 24 + 244 && DecodeStateDown(st.data(), st.size(), got) && got.room == 1 && got.seats.size() == 24 &&
              got.seats[23].account == 1023 && got.seats[23].state == 1,
          "state down, 24 seats");
    // VOICE_API.md §2.1: the relay's golden vectors (rust/gateway/src/voice/tests.rs contract_vectors)
    {
        auto hex = [](const std::vector<uint8_t> &v) {
            std::string s;
            char b[3];
            for (uint8_t x : v) { std::snprintf(b, sizeof b, "%02x", x); s += b; }
            return s;
        };
        const uint8_t o[3] = {0xf8, 0xff, 0xfe}, dead[4] = {0xde, 0xad, 0xbe, 0xef}, r[3] = {0x11, 0x22, 0x33}, zero[12] = {};
        Check(hex(EncodeVoiceUp(0, 42, 126, o, 3, dead)) == "3e9300082112a4420100002a0000007edeadbeefc0f30003f8fffe00", "vector: voice up");
        auto vd = EncodeVoiceDown(0, 42, 126, 5, o, 3, r);
        Check(hex(vd) == "3e9300082112a4420100002a0000007e05112233c0f40003f8fffe00", "vector: voice down");
        Check(DecodeVoiceDown(vd.data(), vd.size(), v) && v.seat == 5 && v.size == 3, "vector: voice down decodes");
        Check(hex(EncodeControlUp(kSubscribe, kSelfDeafened, {0x0123456789abcdefull}, "mkw1.a.b", zero)) ==
                  "3e94001c2112a442000000000000000000000000c0f50016010102010123456789abcdef00086d6b77312e612e620000",
              "vector: control up");
        StateDown one;
        one.room = 1;
        one.bundle = 3;
        one.seats.push_back({2, 0x0123456789abcdefull, 1});
        auto sd = EncodeStateDown(one, zero);
        Check(hex(sd) == "3e9400142112a442000000000000000000000000c0f6000e01010301020123456789abcdef010000", "vector: state down");
    }
    // ---- chat rooms and video/v1: the server's five vectors (VOICE_API.md §6, VIDEO_API.md), byte for byte
    {
        auto hex = [](const std::vector<uint8_t> &v) {
            std::string s;
            char b[3];
            for (uint8_t x : v) { std::snprintf(b, sizeof b, "%02x", x); s += b; }
            return s;
        };
        auto bytes = [](const char *h) {
            std::vector<uint8_t> v;
            for (; h[0] && h[1]; h += 2) { unsigned x = 0; std::sscanf(h, "%2x", &x); v.push_back(static_cast<uint8_t>(x)); }
            return v;
        };
        const uint8_t zero[12] = {};
        const uint64_t ana = 0x0123456789abcdefull;
        const char *kOp5 = "3e9400202112a442000000000000000000000000c0f500190105024b31010123456789abcdef00096d6b7772312e612e62000000";
        const char *kOp6 = "3e9400102112a442000000000000000000000000c0f5000b0106010123456789abcdef00";
        const char *kState2 = "3e9400282112a442000000000000000000000000c0f6002102010301020123456789abcdef01112233445566778801010123456789abcdef03000000";
        const char *kVideoUpHex = "3e9500082112a442010300070001000003e8015ac0f70003ffd8ff00";
        const char *kVideoDownHex = "3e9500082112a442010300070001000003e80104c0f80003ffd8ff00";

        // 1. op 5, the room request
        Check(hex(EncodeRoomRequest("K1", {ana}, "mkwr1.a.b", zero)) == kOp5, "vector 1: op 5 room request encodes");
        ControlUpMessage m;
        auto d = bytes(kOp5);
        Check(DecodeControlUp(d.data(), d.size(), m) && m.op == kRoomRequest && m.key == "K1" && m.accounts == std::vector<uint64_t>{ana} &&
                  m.ticket == "mkwr1.a.b",
              "vector 1: op 5 room request decodes");
        // 2. op 6, the video subscription
        Check(hex(EncodeVideoSubscribe({ana}, zero)) == kOp6, "vector 2: op 6 video subscribe encodes");
        d = bytes(kOp6);
        Check(DecodeControlUp(d.data(), d.size(), m) && m.op == kVideoSubscribe && m.accounts == std::vector<uint64_t>{ana} && m.ticket.empty(),
              "vector 2: op 6 video subscribe decodes");
        // 3. state down ver 2
        d = bytes(kState2);
        StateDown s2;
        Check(DecodeStateDown(d.data(), d.size(), s2) && s2.version == 2 && s2.room == 1 && s2.bundle == 3 && s2.seats.size() == 1 &&
                  s2.seats[0].seat == 2 && s2.seats[0].account == ana && s2.seats[0].state == kSeatTalking && s2.room_id == 0x1122334455667788ull &&
                  s2.fps == 1 && s2.members.size() == 1 && s2.members[0].account == ana && s2.members[0].flags == (kMemberCamera | kMemberVoice),
              "vector 3: state down ver 2 decodes");
        Check(hex(EncodeStateDown(s2, zero)) == kState2, "vector 3: state down ver 2 encodes");
        // 4. video up
        const uint8_t jpeg[3] = {0xff, 0xd8, 0xff};
        Check(hex(EncodeVideoUp(kVideoKeyFrame | kVideoLastChunk, 7, 0, 1, 1000, kCodecMjpeg, 0x5a, jpeg, 3)) == kVideoUpHex, "vector 4: video up encodes");
        d = bytes(kVideoUpHex);
        VideoChunk c;
        Check(DecodeVideoUp(d.data(), d.size(), c) && c.flags == 3 && c.frame == 7 && c.chunk == 0 && c.chunks == 1 && c.ts == 1000 &&
                  c.codec == kCodecMjpeg && c.seat == 0x5a && c.size == 3 && !std::memcmp(c.data, jpeg, 3),
              "vector 4: video up decodes");
        Check(!DecodeVideoDown(d.data(), d.size(), c), "video up is not video down");
        // 5. video down from seat 4
        Check(hex(EncodeVideoDown(kVideoKeyFrame | kVideoLastChunk, 7, 0, 1, 1000, kCodecMjpeg, 4, jpeg, 3)) == kVideoDownHex, "vector 5: video down encodes");
        d = bytes(kVideoDownHex);
        Check(DecodeVideoDown(d.data(), d.size(), c) && c.flags == 3 && c.frame == 7 && c.chunk == 0 && c.chunks == 1 && c.ts == 1000 &&
                  c.codec == kCodecMjpeg && c.seat == 4 && c.size == 3 && !std::memcmp(c.data, jpeg, 3),
              "vector 5: video down decodes");

        // the self bits: voice/v1's encoder never lets bits 2 and 3 out; room mode's needs bit 3
        Check(EncodeControlUp(kSubscribe, kSelfRoomMode, {}, "mkw1.a.b", zero).empty() && EncodeControlUp(kSubscribe, kSelfCameraOn, {}, "mkw1.a.b", zero).empty() &&
                  EncodeControlUp(kReplaceMutes, 4, {}, "mkw1.a.b", zero).empty() && !EncodeControlUp(kSubscribe, 3, {}, "mkw1.a.b", zero).empty(),
              "voice/v1 control up refuses self bits 2 and 3");
        Check(EncodeControlUpRoom(kSubscribe, kSelfCameraOn, {}, "mkw1.a.b", zero).empty() && EncodeControlUpRoom(kSubscribe, 3, {}, "mkw1.a.b", zero).empty() &&
                  EncodeControlUpRoom(kSubscribe, 16 | kSelfRoomMode, {}, "mkw1.a.b", zero).empty(),
              "room mode control up needs bit 3 and nothing above it");
        auto rm = EncodeControlUpRoom(kReplaceMutes, kSelfRoomMode | kSelfCameraOn | kSelfDeafened, {ana}, "mkw1.a.b", zero);
        Check(DecodeControlUp(rm.data(), rm.size(), m) && m.op == kReplaceMutes && m.self == 14 && m.accounts.size() == 1 && m.ticket == "mkw1.a.b",
              "room mode control up: self 0x0e round trip");
        Check(hex(EncodeControlUpRoom(kSubscribe, kSelfRoomMode, {}, "mkw1.a.b", zero)).substr(48, 8) == "01010800", "room mode control up: ver 1, op 1, self 8");

        // op 5 and op 6 limits
        Check(EncodeRoomRequest("", {ana}, "t", zero).empty() && EncodeRoomRequest(std::string(40, 'k'), {ana}, "t", zero).empty() &&
                  !EncodeRoomRequest(std::string(39, 'k'), {ana}, "t", zero).empty(),
              "op 5: a sharing key of 1..39 characters");
        Check(EncodeRoomRequest("K", {1, 2, 3, 4, 5}, "t", zero).empty() && !EncodeRoomRequest("K", {1, 2, 3, 4}, std::string(512, 't'), zero).empty() &&
                  EncodeRoomRequest("K", {1}, std::string(513, 't'), zero).empty() && EncodeRoomRequest("K", {1}, "", zero).empty(),
              "op 5: at most 4 wanted, a ticket of at most 512 bytes");
        auto leave = EncodeRoomRequest("K1", {}, "", zero);
        Check(DecodeControlUp(leave.data(), leave.size(), m) && m.op == kRoomRequest && m.key == "K1" && m.accounts.empty() && m.ticket.empty(),
              "op 5: an empty want list (leave) round trip");
        Check(EncodeRoomRequest(std::string(39, 'k'), {1, 2, 3, 4}, std::string(512, 't'), zero).size() <= kMaxControlDatagram, "op 5 within 1200 bytes");
        Check(EncodeVideoSubscribe({1, 2, 3, 4, 5}, zero).empty() && EncodeVideoSubscribe({}, zero).size() == 28, "op 6: 0..4 accounts");

        // state down ver 2: none, four members, and what is refused
        StateDown r;
        r.version = kVersionRooms;
        r.room = 1;
        r.seats.push_back({0, 10, 0});
        r.seats.push_back({1, 11, 1});
        auto none = EncodeStateDown(r, zero);
        StateDown got2;
        Check(DecodeStateDown(none.data(), none.size(), got2) && got2.version == 2 && got2.room_id == 0 && got2.fps == 1 && got2.members.empty() &&
                  got2.seats.size() == 2,
              "state down ver 2: no room, no members");
        r.room_id = 0x8000000000000001ull;
        r.fps = 4;
        for (uint64_t a = 11; a < 15; a++) r.members.push_back({a, static_cast<uint8_t>(a & 3)});
        auto four = EncodeStateDown(r, zero);
        Check(DecodeStateDown(four.data(), four.size(), got2) && got2.room_id == 0x8000000000000001ull && got2.fps == 4 && got2.members.size() == 4 &&
                  got2.members[3].account == 14 && got2.members[3].flags == 2 && got2.members[0].flags == 3,
              "state down ver 2: four members, divisor 4");
        r.members.push_back({15, 1});
        Check(EncodeStateDown(r, zero).empty(), "state down ver 2: five members refused by the encoder");
        auto cut = four;
        cut.resize(cut.size() - 4); // a member short: the lengths no longer agree
        Be16(cut.data() + 2, static_cast<uint16_t>(cut.size() - 20));
        Be16(cut.data() + 22, static_cast<uint16_t>(Rd16(cut.data() + 22) - 4));
        Check(!DecodeStateDown(cut.data(), cut.size(), got2), "state down ver 2: a short member list refused");
        auto five = four;
        five[24 + 4 + 20 + 9] = 5; // the member count
        Check(!DecodeStateDown(five.data(), five.size(), got2), "state down ver 2: a count of five refused");
        auto v3 = four;
        v3[24] = 3;
        Check(!DecodeStateDown(v3.data(), v3.size(), got2), "state down ver 3 refused");
        auto odd = four;
        odd[24 + 4 + 20 + 8] = 7; // a divisor that is not 1, 2 or 4 reads as 1
        Check(DecodeStateDown(odd.data(), odd.size(), got2) && got2.fps == 1, "state down ver 2: an unknown divisor is 1");
        // a ver 1 state still reads as before, and says so
        StateDown one;
        one.room = 1;
        one.seats.push_back({2, ana, 1});
        auto v1 = EncodeStateDown(one, zero);
        Check(DecodeStateDown(v1.data(), v1.size(), got2) && got2.version == 1 && got2.room_id == 0 && got2.members.empty(), "state down ver 1: no rooms");

        // video: the chunk's limits
        std::vector<uint8_t> big(1101, 0x55);
        Check(EncodeVideoUp(1, 0, 0, 1, 0, 1, 0, big.data(), 1101).empty() && EncodeVideoUp(1, 0, 0, 1, 0, 1, 0, big.data(), 1100).size() == 1124,
              "video: a chunk of at most 1100 bytes, a datagram of at most 1124");
        Check(EncodeVideoUp(1, 0, 0, 0, 0, 1, 0, big.data(), 10).empty() && EncodeVideoUp(1, 0, 64, 64, 0, 1, 0, big.data(), 10).empty() &&
                  EncodeVideoUp(1, 0, 0, 65, 0, 1, 0, big.data(), 10).empty() && !EncodeVideoUp(1, 0, 63, 64, 0, 1, 0, big.data(), 10).empty(),
              "video: 1..64 chunks, chunk below chunks");
        auto seat24 = EncodeVideoDown(1, 0, 0, 1, 0, 1, 24, big.data(), 10);
        Check(!DecodeVideoDown(seat24.data(), seat24.size(), c), "video down: seat 24 refused");
        auto ver2 = EncodeVideoDown(1, 0, 0, 1, 0, 1, 3, big.data(), 10);
        ver2[8] = 2;
        Check(!DecodeVideoDown(ver2.data(), ver2.size(), c), "video down: ver 2 refused");
    }
    std::printf("voice_wire_test: %s\n", failures ? "FAIL" : "PASS");
    return failures ? 1 : 0;
}
