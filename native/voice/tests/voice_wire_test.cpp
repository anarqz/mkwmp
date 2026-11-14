// mkwmp voice: the relay datagrams (voice_wire.h, VOICE_API.md §2). Run by build.py --test.
#include "voice_wire.h"

#include <cstdio>

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
    std::printf("voice_wire_test: %s\n", failures ? "FAIL" : "PASS");
    return failures ? 1 : 0;
}
