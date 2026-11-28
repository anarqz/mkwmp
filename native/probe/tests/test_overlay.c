// The overlay's trigger gate and the launcher's answer (overlay.h): which readings open the overlay, and what the game
// may see of the pad around it. The pure part of overlay.c, with the plat files it links.
//
//   $zig = "C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"
//   & $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o scratch\test_overlay.exe `
//       native\probe\tests\test_overlay.c native\probe\tests\stub_ingame.c native\probe\src\overlay.c native\probe\src\config.c `
//       native\probe\src\log.c native\probe\src\plat_win.c native\probe\src\plat_net_win.c -lws2_32
//   scratch\test_overlay.exe
#include "../src/overlay.h"

#include <stdio.h>
#include <string.h>

static int failures;
#define CHECK(c) do { if (!(c)) { printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #c); failures++; } } while (0)

enum { A = 1u << 0, B = 1u << 1, MINUS = OVERLAY_BTN_MINUS, PLUS = OVERLAY_BTN_PLUS };

static OverlayGate g;
static OverlayPad last;
// One reading with these buttons at t ms; the trigger, and `last` what the game gets.
static OverlayTrigger rd(uint64_t t, uint64_t buttons, int guide, int open) {
    last = (OverlayPad){.buttons = buttons, .guide = guide};
    return overlay_gate(&g, &last, t, 1, open);
}

static void test_unarmed(void) {
    memset(&g, 0, sizeof g);
    OverlayPad p = {.buttons = MINUS | PLUS | A, .lx = 30000, .guide = 1};
    CHECK(overlay_gate(&g, &p, 0, 0, 0) == OVERLAY_BY_NONE);
    CHECK(p.buttons == (MINUS | PLUS | A) && p.lx == 30000 && p.guide == 1);   // everything is the game's
    p.buttons = MINUS;
    for (uint64_t t = 0; t < 3000; t += 16) CHECK(overlay_gate(&g, &p, t, 0, 0) == OVERLAY_BY_NONE && p.buttons == MINUS);
}

static void test_short_select(void) {
    memset(&g, 0, sizeof g);
    for (uint64_t t = 0; t < 200; t += 16) {
        CHECK(rd(t, MINUS | A, 0, 0) == OVERLAY_BY_NONE);
        CHECK(last.buttons == A);   // held back; the rest passes
    }
    CHECK(rd(200, A, 0, 0) == OVERLAY_BY_NONE && last.buttons == (A | MINUS));   // given back on release
    CHECK(rd(250, 0, 0, 0) == OVERLAY_BY_NONE && last.buttons == MINUS);
    CHECK(rd(200 + OVERLAY_REPLAY_MS, 0, 0, 0) == OVERLAY_BY_NONE && last.buttons == 0);
}

static void test_hold(void) {
    memset(&g, 0, sizeof g);
    int fired = 0;
    for (uint64_t t = 0; t <= 1500; t += 16) {
        OverlayTrigger r = rd(t, MINUS, 0, fired);
        if (r) {
            CHECK(r == OVERLAY_BY_HOLD && t >= OVERLAY_HOLD_MS && !fired);
            fired++;
        }
        CHECK(last.buttons == 0);
    }
    CHECK(fired == 1);
    // released with the overlay up, then closed by B: nothing reaches the game until every button is up
    CHECK(rd(1600, 0, 0, 1) == OVERLAY_BY_NONE && last.buttons == 0);
    CHECK(rd(1700, B, 0, 1) == OVERLAY_BY_NONE && last.buttons == 0);
    overlay_gate_closed(&g);
    CHECK(rd(1716, B, 0, 0) == OVERLAY_BY_NONE && last.buttons == 0);
    CHECK(rd(1732, B | A, 0, 0) == OVERLAY_BY_NONE && last.buttons == 0);
    CHECK(rd(1748, 0, 0, 0) == OVERLAY_BY_NONE && last.buttons == 0);
    CHECK(rd(1764, A, 0, 0) == OVERLAY_BY_NONE && last.buttons == A);   // the game's again
    CHECK(rd(1780, 0, 0, 0) == OVERLAY_BY_NONE && last.buttons == 0);   // and no late Select from the hold
}

static void test_chord(void) {
    memset(&g, 0, sizeof g);
    CHECK(rd(0, MINUS, 0, 0) == OVERLAY_BY_NONE && last.buttons == 0);
    CHECK(rd(16, MINUS | PLUS, 0, 0) == OVERLAY_BY_CHORD && last.buttons == 0);   // neither reaches mkw-recomp's shortcut
    CHECK(rd(32, MINUS | PLUS, 0, 0) == OVERLAY_BY_NONE && last.buttons == 0);    // once per press
    CHECK(rd(2000, MINUS | PLUS, 0, 0) == OVERLAY_BY_NONE);                       // and no hold on top of it
    CHECK(rd(2016, PLUS, 0, 0) == OVERLAY_BY_NONE && last.buttons == 0);          // releasing one does not leak the other
    CHECK(rd(2032, 0, 0, 0) == OVERLAY_BY_NONE && last.buttons == 0);
    CHECK(rd(2048, PLUS, 0, 0) == OVERLAY_BY_NONE && last.buttons == PLUS);       // Start alone is the game's pause
    // Start first, then Select: still the chord
    CHECK(rd(2064, PLUS | MINUS, 0, 0) == OVERLAY_BY_CHORD && last.buttons == 0);
}

static void test_guide(void) {
    memset(&g, 0, sizeof g);
    CHECK(rd(0, A, 1, 0) == OVERLAY_BY_GUIDE && last.buttons == 0 && last.guide == 0);
    CHECK(rd(16, A, 1, 0) == OVERLAY_BY_NONE && last.guide == 0);   // held: once
    CHECK(rd(32, A, 0, 0) == OVERLAY_BY_NONE && last.buttons == 0);   // idle until A is up too
    CHECK(rd(48, 0, 0, 0) == OVERLAY_BY_NONE);
    CHECK(rd(64, A, 0, 0) == OVERLAY_BY_NONE && last.buttons == A);
    // with the overlay up the game took the focus back: the button answers again, the pad stays idle
    CHECK(rd(80, A, 1, 1) == OVERLAY_BY_GUIDE && last.buttons == 0);
    // Guide is never the game's (nor mkw-recomp's MKW_MENU_SHORTCUT=guide)
    memset(&g, 0, sizeof g);
    g.guide_was = 1;
    CHECK(rd(0, 0, 1, 0) == OVERLAY_BY_NONE && last.guide == 0);
}

static void test_sticks_hold_idle(void) {
    memset(&g, 0, sizeof g);
    overlay_gate_closed(&g);
    OverlayPad p = {.lx = 20000};
    CHECK(overlay_gate(&g, &p, 0, 1, 0) == OVERLAY_BY_NONE && p.lx == 0 && g.idle);   // a stick still pushed
    p = (OverlayPad){.lx = 3000};
    CHECK(overlay_gate(&g, &p, 16, 1, 0) == OVERLAY_BY_NONE && !g.idle);
    p = (OverlayPad){.lx = 20000, .ry = -9000};
    CHECK(overlay_gate(&g, &p, 32, 1, 0) == OVERLAY_BY_NONE && p.lx == 20000 && p.ry == -9000);
}

// ---- the chosen shortcut (OverlayCombo) ----------------------------------------------------------------------------

enum { L3 = OVERLAY_BTN_STICK_L, R3 = OVERLAY_BTN_STICK_R, LB = OVERLAY_BTN_L, RB = OVERLAY_BTN_R };

static void use(OverlayCombo c) {
    memset(&g, 0, sizeof g);
    overlay_gate_set_combo(&g, c);
    g.all_was = 0, g.guide_was = 0;   // nothing held at the start of each case
}

static void test_combo_names(void) {
    for (int i = 0; i < OVERLAY_COMBO_COUNT; i++) {
        const char *n = overlay_combo_name((OverlayCombo)i);
        CHECK(overlay_combo_parse(n, (int)strlen(n)) == (OverlayCombo)i);
    }
    CHECK(!strcmp(overlay_combo_name(OVERLAY_COMBO_DEFAULT), "guide_or_select_start"));
    CHECK(overlay_combo_parse("l3_r3", 5) == OVERLAY_COMBO_STICKS);
    CHECK(overlay_combo_parse("l3_r3_and_more", 5) == OVERLAY_COMBO_STICKS);   // only len bytes
    CHECK(overlay_combo_parse("guide", 4) == OVERLAY_COMBO_DEFAULT);          // "guid": unknown
    CHECK(overlay_combo_parse("nope", 4) == OVERLAY_COMBO_DEFAULT && overlay_combo_parse(NULL, 3) == OVERLAY_COMBO_DEFAULT);
    CHECK(!strcmp(overlay_combo_name((OverlayCombo)99), "guide_or_select_start"));
}

static void test_combo_guide_only(void) {
    use(OVERLAY_COMBO_GUIDE);
    CHECK(rd(0, MINUS | PLUS, 0, 0) == OVERLAY_BY_NONE && last.buttons == (MINUS | PLUS));   // Select+Start: the game's
    CHECK(rd(16, 0, 0, 0) == OVERLAY_BY_NONE);
    for (uint64_t t = 32; t < 2000; t += 16) CHECK(rd(t, MINUS, 0, 0) == OVERLAY_BY_NONE && last.buttons == MINUS);   // no hold
    CHECK(rd(2000, 0, 0, 0) == OVERLAY_BY_NONE && last.buttons == 0);   // and no late replay
    CHECK(rd(2016, 0, 1, 0) == OVERLAY_BY_GUIDE && last.guide == 0);
}

static void test_combo_select_start_only(void) {
    use(OVERLAY_COMBO_SELECT_START);
    for (uint64_t t = 0; t < 2000; t += 16) CHECK(rd(t, MINUS, 0, 0) == OVERLAY_BY_NONE && last.buttons == 0);   // no hold
    CHECK(rd(2000, 0, 0, 0) == OVERLAY_BY_NONE && last.buttons == MINUS);   // given back, late
    CHECK(rd(2100, 0, 1, 0) == OVERLAY_BY_NONE && last.guide == 1);         // Guide is not taken
    CHECK(rd(2116, 0, 0, 0) == OVERLAY_BY_NONE);
    CHECK(rd(2132, MINUS, 0, 0) == OVERLAY_BY_NONE && rd(2148, MINUS | PLUS, 0, 0) == OVERLAY_BY_CHORD && last.buttons == 0);
}

static void test_combo_select_hold_only(void) {
    use(OVERLAY_COMBO_SELECT_HOLD);
    CHECK(rd(0, MINUS, 0, 0) == OVERLAY_BY_NONE && rd(16, MINUS | PLUS, 0, 0) == OVERLAY_BY_NONE);
    CHECK(last.buttons == PLUS);   // Start passes: no chord here
    CHECK(rd(32, 0, 0, 0) == OVERLAY_BY_NONE && last.buttons == MINUS);   // the short Select, late
    CHECK(rd(200, 0, 1, 0) == OVERLAY_BY_NONE && last.guide == 1);
    CHECK(rd(216, MINUS, 0, 0) == OVERLAY_BY_NONE);
    CHECK(rd(216 + OVERLAY_HOLD_MS - 1, MINUS, 0, 0) == OVERLAY_BY_NONE);
    CHECK(rd(216 + OVERLAY_HOLD_MS, MINUS, 0, 0) == OVERLAY_BY_HOLD && last.buttons == 0);
}

static void test_combo_start_hold(void) {
    use(OVERLAY_COMBO_START_HOLD);
    CHECK(rd(0, PLUS, 0, 0) == OVERLAY_BY_NONE && last.buttons == 0);     // held back
    CHECK(rd(100, 0, 0, 0) == OVERLAY_BY_NONE && last.buttons == PLUS);   // a short press still pauses, on release
    CHECK(rd(100 + OVERLAY_REPLAY_MS, 0, 0, 0) == OVERLAY_BY_NONE && last.buttons == 0);
    CHECK(rd(300, MINUS, 0, 0) == OVERLAY_BY_NONE && last.buttons == MINUS);   // Select is the game's here
    CHECK(rd(316, 0, 0, 0) == OVERLAY_BY_NONE);
    CHECK(rd(400, PLUS, 0, 0) == OVERLAY_BY_NONE);
    CHECK(rd(400 + OVERLAY_HOLD_MS, PLUS, 0, 0) == OVERLAY_BY_HOLD && last.buttons == 0);
    CHECK(rd(2000, 0, 0, 0) == OVERLAY_BY_NONE && last.buttons == 0);   // no pause after the hold
}

static void test_combo_sticks(void) {
    use(OVERLAY_COMBO_STICKS);
    CHECK(rd(0, L3, 0, 0) == OVERLAY_BY_NONE && last.buttons == L3);   // one stick pressed: the game's, at once
    CHECK(rd(16, 0, 0, 0) == OVERLAY_BY_NONE && last.buttons == 0);
    CHECK(rd(32, R3 | A, 0, 0) == OVERLAY_BY_NONE && last.buttons == (R3 | A));
    CHECK(rd(48, R3 | L3 | A, 0, 0) == OVERLAY_BY_CHORD && last.buttons == 0);   // both: neither reaches the game
    CHECK(rd(64, R3 | L3, 0, 0) == OVERLAY_BY_NONE && last.buttons == 0);         // (nor mkw-recomp's L3+R3)
    CHECK(rd(80, R3, 0, 0) == OVERLAY_BY_NONE && last.buttons == 0);              // idle until all are up
    CHECK(rd(96, 0, 0, 0) == OVERLAY_BY_NONE);
    CHECK(rd(112, L3, 0, 0) == OVERLAY_BY_NONE && last.buttons == L3);
    CHECK(rd(128, 0, 1, 0) == OVERLAY_BY_NONE && last.guide == 1);   // Guide is not taken
    for (uint64_t t = 144; t < 2000; t += 16) CHECK(rd(t, MINUS, 0, 0) == OVERLAY_BY_NONE && last.buttons == MINUS);
}

static void test_combo_bumpers_start(void) {
    use(OVERLAY_COMBO_BUMPERS_START);
    CHECK(rd(0, LB | RB | A, 0, 0) == OVERLAY_BY_NONE && last.buttons == (LB | RB | A));   // item and drift: the game's
    CHECK(rd(16, LB | RB | A | PLUS, 0, 0) == OVERLAY_BY_CHORD && last.buttons == 0);     // Start completes it
    CHECK(rd(32, LB | RB | PLUS, 0, 0) == OVERLAY_BY_NONE && last.buttons == 0);
    CHECK(rd(48, 0, 0, 0) == OVERLAY_BY_NONE);
    CHECK(rd(64, PLUS, 0, 0) == OVERLAY_BY_NONE && last.buttons == PLUS);   // Start alone pauses at once
    CHECK(rd(80, PLUS | LB, 0, 0) == OVERLAY_BY_NONE && last.buttons == (PLUS | LB));
}

static void test_combo_select_bumper(void) {
    use(OVERLAY_COMBO_SELECT_LB);
    CHECK(rd(0, LB, 0, 0) == OVERLAY_BY_NONE && last.buttons == LB);   // LB alone: the item
    CHECK(rd(16, 0, 0, 0) == OVERLAY_BY_NONE);
    CHECK(rd(32, MINUS, 0, 0) == OVERLAY_BY_NONE && last.buttons == 0);
    CHECK(rd(48, MINUS | RB, 0, 0) == OVERLAY_BY_NONE && last.buttons == RB);   // RB is not part of it
    CHECK(rd(64, MINUS | RB | LB, 0, 0) == OVERLAY_BY_CHORD && last.buttons == 0);
    for (uint64_t t = 80; t < 2000; t += 16) CHECK(rd(t, MINUS, 0, 1) == OVERLAY_BY_NONE);   // no hold on top
    use(OVERLAY_COMBO_SELECT_RB);
    CHECK(rd(0, MINUS, 0, 0) == OVERLAY_BY_NONE && rd(16, MINUS | LB, 0, 0) == OVERLAY_BY_NONE && last.buttons == LB);
    CHECK(rd(32, MINUS | RB, 0, 0) == OVERLAY_BY_CHORD && last.buttons == 0);
}

static void test_combo_switch(void) {
    use(OVERLAY_COMBO_DEFAULT);
    CHECK(rd(0, L3 | R3, 0, 0) == OVERLAY_BY_NONE && last.buttons == (L3 | R3));
    overlay_gate_set_combo(&g, OVERLAY_COMBO_STICKS);   // changed with both sticks still pressed: not a press
    CHECK(rd(16, L3 | R3, 0, 0) == OVERLAY_BY_NONE);
    CHECK(rd(32, 0, 0, 0) == OVERLAY_BY_NONE && rd(48, L3 | R3, 0, 0) == OVERLAY_BY_CHORD);
    // the choice survives the launcher going away (armed 0 forgets the rest)
    OverlayPad p = {0};
    overlay_gate(&g, &p, 64, 0, 0);
    CHECK(g.combo == OVERLAY_COMBO_STICKS);
}

static void test_state(void) {
    OverlayState s;
    CHECK(overlay_parse_state("{\"open\":true,\"seq\":7}", &s) && s.open && s.seq == 7 && !s.menu && s.enabled);
    CHECK(overlay_parse_state("{\"open\":false,\"seq\":0,\"enabled\":false}", &s) && !s.open && !s.enabled);
    CHECK(overlay_parse_state("{\"open\":false,\"seq\":0,\"enabled\":true}", &s) && s.enabled);
    CHECK(overlay_parse_state("{\"open\": false, \"seq\": 12, \"action\": \"menu\"}", &s) && !s.open && s.seq == 12 && s.menu);
    CHECK(overlay_parse_state("{\"open\":false,\"seq\":3,\"action\":\"resume\"}", &s) && !s.open && !s.menu && !s.quit);
    CHECK(overlay_parse_state("{\"open\":false,\"seq\":4,\"enabled\":true,\"action\":\"quit\"}", &s) && !s.open && s.quit && !s.menu);
    CHECK(overlay_parse_state("{\"open\": false, \"seq\": 12, \"action\": \"menu\"}", &s) && !s.quit);
    CHECK(!overlay_parse_state("{\"state\":\"unauthorized\"}", &s));
    CHECK(!overlay_parse_state("{\"open\":1}", &s));
    CHECK(!overlay_parse_state("", &s) && !overlay_parse_state(NULL, &s));
    CHECK(overlay_parse_state("{\"open\":false,\"seq\":0,\"enabled\":true}", &s) && s.combo == OVERLAY_COMBO_DEFAULT);
    CHECK(overlay_parse_state("{\"open\":false,\"seq\":0,\"enabled\":true,\"shortcut\":\"select_hold\"}", &s) &&
          s.combo == OVERLAY_COMBO_SELECT_HOLD && s.enabled);
    CHECK(overlay_parse_state("{\"open\":true,\"seq\":2,\"enabled\":true,\"shortcut\": \"lb_rb_start\"}", &s) &&
          s.combo == OVERLAY_COMBO_BUMPERS_START && s.open);
    CHECK(overlay_parse_state("{\"open\":false,\"seq\":0,\"shortcut\":\"from_the_future\"}", &s) && s.combo == OVERLAY_COMBO_DEFAULT);
    CHECK(overlay_parse_state("{\"open\":false,\"seq\":0,\"shortcut\":\"l3_r3", &s) && s.combo == OVERLAY_COMBO_DEFAULT);
}

int main(void) {
    test_unarmed();
    test_short_select();
    test_hold();
    test_chord();
    test_guide();
    test_sticks_hold_idle();
    test_combo_names();
    test_combo_guide_only();
    test_combo_select_start_only();
    test_combo_select_hold_only();
    test_combo_start_hold();
    test_combo_sticks();
    test_combo_bumpers_start();
    test_combo_select_bumper();
    test_combo_switch();
    test_state();
    printf(failures ? "%d FAILURES\n" : "overlay: all passed\n", failures);
    return failures != 0;
}
