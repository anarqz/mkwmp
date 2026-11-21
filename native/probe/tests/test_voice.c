// voice_parse_settings (voice.h): the launcher's GET /local/v1/voice answer. (Who is heard is the relay's business:
// everyone in the friend room, nobody in a matchmade lobby; the probe filters nothing. native/voice's voice_room_test.)
// (voice.c installs the menu hooks, so voice_ui.c and what it needs come along; coff.c defines g_rt.)
//
//   $zig = "C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"; $s = "native\probe\src"
//   & $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o scratch\test_voice.exe `
//       native\probe\tests\test_voice.c $s\voice.c $s\voice_ui.c $s\guesthook.c $s\gameaddrs.c $s\coff.c $s\log.c `
//       $s\config.c $s\launcher.c $s\seat.c $s\friends.c $s\uuid5.c $s\notify.c $s\plat_win.c $s\plat_net_win.c -lws2_32
//   scratch\test_voice.exe
#include "../src/voice.h"

#include "../src/gameaddrs.h"
#include "../src/guesthook.h"
#include "../src/voice_ui.h"

#include <stdint.h>
#include <stdio.h>
#include <string.h>

static int failures;
#define CHECK(c) do { if (!(c)) { printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #c); failures++; } } while (0)

// voice_ui_page_entries: the online pause menu's player list, a count and a pointer to the entry array. Up to v1.4.2
// the probe read the array inline at page + page_entries, so the 4th member's "entry" was the page's own word at
// +page_entries + 0x18, and that word's +0x18 a garbage "name" pointer: the crash when a 4th player joined (record
// 20261121T232506Z-33580, ACCESS_VIOLATION reading 0x7f952a542948bde2 in voice_ui.c apply). The fake page below
// reproduces those words.
#define GARBAGE 0x7f952a542948bde2ull
static _Alignas(16) uint8_t page[0x5000];
static _Alignas(16) uint8_t ent[GAME_ROWS_MAX + 2][0x100];
static _Alignas(16) uint16_t nm[GAME_ROWS_MAX + 2][8];
static _Alignas(16) uint64_t arr[GAME_ROWS_MAX + 2];
static _Alignas(16) uint8_t decoy[0x40];

static void fake_page(const struct game_addrs *r, int count) {
    memset(page, 0, sizeof page);
    for (int i = 0; i < GAME_ROWS_MAX + 2; i++) {
        memset(ent[i], 0, sizeof ent[i]);
        nm[i][0] = (uint16_t)('A' + i); nm[i][1] = 'n'; nm[i][2] = 'a'; nm[i][3] = 0;
        *(uint64_t *)(ent[i] + r->entry_name) = (uint64_t)(uintptr_t)nm[i];
        *(int32_t *)(ent[i] + r->entry_name + 8) = 4;   // the SafeString's length
        arr[i] = (uint64_t)(uintptr_t)ent[i];
    }
    ent[0][r->entry_me] = 1;
    *(int32_t *)(page + r->page_count) = count;
    *(uint64_t *)(page + r->page_entries) = (uint64_t)(uintptr_t)arr;
    // the words that follow, as in the crash dump: 0, a pointer whose +0x18 is a pointer, then the decoy
    *(uint64_t *)(page + r->page_entries + 8) = 0;
    *(uint64_t *)(page + r->page_entries + 16) = (uint64_t)(uintptr_t)(page + r->page_entries + 0x3c);
    *(uint64_t *)(page + r->page_entries + 24) = (uint64_t)(uintptr_t)decoy;
    *(uint64_t *)(decoy + 0x18) = GARBAGE;
}

static void test_page_entries(int build) {
    const struct game_addrs *r = game_addrs_for(build);
    CHECK(r != NULL);
    if (!r) return;
    CHECK((size_t)r->page_entries + 32 <= sizeof page && r->rows <= GAME_ROWS_MAX);
    uint64_t e[GAME_ROWS_MAX];
    const uint16_t *n[GAME_ROWS_MAX];
    size_t len[GAME_ROWS_MAX];
    uint64_t pg = (uint64_t)(uintptr_t)page;

    // 1 to 4 members (the 4th is the one that crashed), and a full page
    for (int count = 1; count <= r->rows; count++) {
        fake_page(r, count);
        int got = voice_ui_page_entries(pg, r, e, n, len);
        CHECK(got == count);
        for (int i = 0; i < got && i < count; i++) {
            CHECK(e[i] == (uint64_t)(uintptr_t)ent[i]);
            CHECK(n[i] == nm[i] && len[i] == 3 && n[i][0] == 'A' + i);
        }
    }
    // the inline reading would have taken the decoy as entry 3: its "name" is the garbage pointer
    fake_page(r, 4);
    CHECK(*(uint64_t *)(page + r->page_entries + 24) == (uint64_t)(uintptr_t)decoy && *(uint64_t *)(decoy + 0x18) == GARBAGE);

    // a garbage entry or name pointer is skipped, not read
    fake_page(r, 4);
    arr[3] = GARBAGE;
    *(uint64_t *)(ent[1] + r->entry_name) = GARBAGE;
    *(uint64_t *)(ent[2] + r->entry_name) = 0;
    CHECK(voice_ui_page_entries(pg, r, e, n, len) == 4);
    CHECK(e[0] == (uint64_t)(uintptr_t)ent[0] && n[0] == nm[0]);
    CHECK(e[1] == 0 && n[1] == NULL && e[2] == 0 && e[3] == 0);
    arr[0] = ~0ull;
    CHECK(voice_ui_page_entries(pg, r, e, n, len) == 4 && e[0] == 0);

    // no usable list
    fake_page(r, r->rows + 1);
    CHECK(voice_ui_page_entries(pg, r, e, n, len) == -1);
    fake_page(r, -1);
    CHECK(voice_ui_page_entries(pg, r, e, n, len) == -1);
    fake_page(r, 4);
    *(uint64_t *)(page + r->page_entries) = 0;
    CHECK(voice_ui_page_entries(pg, r, e, n, len) == -1);
    *(uint64_t *)(page + r->page_entries) = GARBAGE;
    CHECK(voice_ui_page_entries(pg, r, e, n, len) == -1);
    fake_page(r, 0);
    CHECK(voice_ui_page_entries(pg, r, e, n, len) == 0);
    CHECK(voice_ui_page_entries(0, r, e, n, len) == -1);
    CHECK(voice_ui_page_entries(pg, NULL, e, n, len) == -1);
}

int main(void) {
    test_page_entries(GAME_180);
    test_page_entries(GAME_100);

    VoiceSettings s;
    CHECK(voice_parse_settings(200, "{\"enabled\":false,\"input\":\"{0.0.1.00000000}.{ab\\\"c}\",\"output\":\"\","
                                    "\"inputGain\":40,\"outputGain\":250,\"mutes\":[{\"account\":\"8070450532247928832\",\"name\":\"Bia\"},"
                                    "{\"account\":\"12\",\"name\":\"x\"}]}", &s));
    CHECK(s.enabled == 0);
    CHECK(!strcmp(s.input, "{0.0.1.00000000}.{ab\"c}"));
    CHECK(s.output[0] == 0);
    CHECK(s.input_gain == 40);
    CHECK(s.output_gain == 100);   // out of range: the default
    CHECK(s.mute_count == 2 && s.mutes[0] == 8070450532247928832ull && s.mutes[1] == 12);
    CHECK(voice_parse_settings(200, "{}", &s) && s.enabled == 1 && s.input_gain == 100 && s.mute_count == 0);
    CHECK(!voice_parse_settings(404, "{}", &s) && s.enabled == 1);
    CHECK(voice_parse_settings(200, "{\"enabled\": true, \"mutes\": []}", &s) && s.enabled == 1 && s.mute_count == 0);

    printf("test_voice: %s\n", failures ? "FAIL" : "PASS");
    return failures != 0;
}
