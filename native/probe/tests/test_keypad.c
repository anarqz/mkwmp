// The Room ID keypad's state machine / layout (keypad_logic.h) and the inline hook (detour.c), the latter on
// stand-in functions that start with the very prologues keypad.c patches in mkw.exe.
//
//   $zig = "C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"
//   & $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o scratch\test_keypad.exe `
//       native\probe\tests\test_keypad.c native\probe\src\detour.c
//   scratch\test_keypad.exe
#include "../src/detour.h"
#include "../src/keypad_logic.h"
#include "../src/keypad_sigs.h"

#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int failures;
#define CHECK(c) do { if (!(c)) { printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #c); failures++; } } while (0)

// ---- detour --------------------------------------------------------------------------------------------------------

// text_entry_run's prologue, then: mov eax,42 ; epilogue
static const uint8_t kFnEntry[] = {0x41, 0x57, 0x41, 0x56, 0x56, 0x57, 0x53, 0x48, 0x83, 0xEC, 0x20,
                                   0xB8, 0x2A, 0x00, 0x00, 0x00,
                                   0x48, 0x83, 0xC4, 0x20, 0x5B, 0x5F, 0x5E, 0x41, 0x5E, 0x41, 0x5F, 0xC3};
// menu_notice_shown's prologue, then: mov eax,0xFFFFFF00 (false in al, junk above) ; epilogue
static const uint8_t kFnShown[] = {0x56, 0x48, 0x83, 0xEC, 0x20,
                                   0xB8, 0x00, 0xFF, 0xFF, 0xFF,
                                   0x48, 0x83, 0xC4, 0x20, 0x5E, 0xC3};
// menu_notice_draw's prologue, then: cvttss2si eax,xmm0 ; cvttss2si ecx,xmm1 ; imul eax,eax,100 ; add eax,ecx ; epilogue
static const uint8_t kFnDraw[] = {0x56, 0x57, 0x48, 0x81, 0xEC, 0x78, 0x01, 0x00, 0x00,
                                  0xF3, 0x0F, 0x2C, 0xC0, 0xF3, 0x0F, 0x2C, 0xC9, 0x6B, 0xC0, 0x64, 0x01, 0xC8,
                                  0x48, 0x81, 0xC4, 0x78, 0x01, 0x00, 0x00, 0x5F, 0x5E, 0xC3};

typedef int (*EntryFn)(const void *, void *, size_t, size_t *);
typedef _Bool (*ShownFn)(void);
typedef int (*DrawFn)(float, float);
static EntryFn g_orig_entry;
static ShownFn g_orig_shown;
static DrawFn g_orig_draw;
static int g_shown_override;

static int hook_entry(const void *a, void *b, size_t c, size_t *d) { return g_orig_entry(a, b, c, d) + (int)c; }
static _Bool hook_shown(void) { return g_shown_override ? 1 : g_orig_shown(); }
static int hook_draw(float w, float s) { return g_orig_draw(w, s) + 10000; }

static void test_detour(void) {
    uint8_t *code = (uint8_t *)VirtualAlloc(NULL, 0x1000, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE);
    CHECK(code != NULL);
    if (!code) return;
    memset(code, 0xCC, 0x1000);
    uint8_t *entry = code, *shown = code + 0x100, *draw = code + 0x200;
    memcpy(entry, kFnEntry, sizeof kFnEntry);
    memcpy(shown, kFnShown, sizeof kFnShown);
    memcpy(draw, kFnDraw, sizeof kFnDraw);
    CHECK(((EntryFn)(void *)entry)(NULL, NULL, 5, NULL) == 42);
    CHECK(((ShownFn)(void *)shown)() == 0);
    CHECK(((DrawFn)(void *)draw)(12.0f, 34.0f) == 1234);

    // a prologue that is not the expected one: refused, target untouched
    static const uint8_t kOther[] = {0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48};
    void *none = NULL;
    CHECK(!detour_install(entry, kOther, sizeof kOther, (void *)hook_entry, &none));
    CHECK(none == NULL && !memcmp(entry, kFnEntry, sizeof kFnEntry));
    CHECK(!detour_install(entry, kFnEntry, 4, (void *)hook_entry, &none));   // too short for a jmp rel32

    CHECK(detour_install(entry, kFnEntry, 7, (void *)hook_entry, (void **)&g_orig_entry));
    CHECK(detour_install(shown, kFnShown, 5, (void *)hook_shown, (void **)&g_orig_shown));
    CHECK(detour_install(draw, kFnDraw, 9, (void *)hook_draw, (void **)&g_orig_draw));
    CHECK(entry[0] == 0xE9 && entry[5] == 0x90 && entry[6] == 0x90 && entry[7] == 0x48);   // the 8th byte on is intact
    CHECK(shown[0] == 0xE9 && shown[5] == 0xB8);
    CHECK(draw[0] == 0xE9 && draw[8] == 0x90 && draw[9] == 0xF3);
    CHECK(((EntryFn)(void *)entry)(NULL, NULL, 5, NULL) == 47);
    CHECK(g_orig_entry(NULL, NULL, 5, NULL) == 42);
    CHECK(((ShownFn)(void *)shown)() == 0);
    g_shown_override = 1;
    CHECK(((ShownFn)(void *)shown)() == 1);
    CHECK(((DrawFn)(void *)draw)(12.0f, 34.0f) == 11234);   // the float arguments reach the original
    // the trampolines sit within rel32 reach of the targets
    CHECK(llabs((long long)((intptr_t)(void *)g_orig_entry - (intptr_t)entry)) < 0x7fffffffLL);
}

// ---- the prologue tables keypad.c installs from (keypad_sigs.h) -----------------------------------------------------

// text_entry_run as mkw-recomp 0.1.15 starts it (sub rsp,0xb30), then: mov eax,42 ; epilogue
static const uint8_t kFnEntry15[] = {0x41, 0x57, 0x41, 0x56, 0x56, 0x57, 0x53, 0x48, 0x81, 0xEC, 0x30, 0x0B, 0x00, 0x00,
                                     0xB8, 0x2A, 0x00, 0x00, 0x00,
                                     0x48, 0x81, 0xC4, 0x30, 0x0B, 0x00, 0x00, 0x5B, 0x5F, 0x5E, 0x41, 0x5E, 0x41, 0x5F, 0xC3};
static EntryFn g_orig_entry13, g_orig_entry15;
static int hook_entry13(const void *a, void *b, size_t c, size_t *d) { return g_orig_entry13(a, b, c, d) + 100; }
static int hook_entry15(const void *a, void *b, size_t c, size_t *d) { return g_orig_entry15(a, b, c, d) + 200; }

static void test_prologues(void) {
    uint8_t *code = (uint8_t *)VirtualAlloc(NULL, 0x1000, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE);
    CHECK(code != NULL);
    if (!code) return;
    memset(code, 0xCC, 0x1000);
    // the 0.1.13 stand-in needs the real 14 signature bytes: "mov rsi,r9" after the frame, then as kFnEntry
    uint8_t *e13 = code, *e15 = code + 0x100, *other = code + 0x200, *shown = code + 0x300, *draw = code + 0x400;
    memcpy(e13, kFnEntry, 11);
    memcpy(e13 + 11, "\x4C\x89\xCE", 3);
    memcpy(e13 + 14, kFnEntry + 11, sizeof kFnEntry - 11);
    memcpy(e15, kFnEntry15, sizeof kFnEntry15);
    memcpy(other, "\x48\x89\x5C\x24\x08\x57\x48\x83\xEC\x20\x31\xC0\xC3\x90\x90\x90", 16);
    memcpy(shown, "\x56\x48\x83\xEC\x20\x48\x8B\x35\x00\x00\x00\x00", 12);
    memcpy(draw, "\x56\x57\x48\x81\xEC\x78\x01\x00\x00\xC5\x78\x29\x84\x24\x60\x01\x00\x00", 18);
    CHECK(((EntryFn)(void *)e13)(NULL, NULL, 0, NULL) == 42);
    CHECK(((EntryFn)(void *)e15)(NULL, NULL, 0, NULL) == 42);

    const DetourPrologue *p13 = detour_match(e13, kEntryPrologues, KEYPAD_COUNT(kEntryPrologues));
    const DetourPrologue *p15 = detour_match(e15, kEntryPrologues, KEYPAD_COUNT(kEntryPrologues));
    CHECK(p13 == &kEntryPrologues[0] && p13->sig == kEntrySig);
    CHECK(p15 == &kEntryPrologues[1] && p15->sig == kEntrySig15);
    CHECK(!detour_match(other, kEntryPrologues, KEYPAD_COUNT(kEntryPrologues)));
    CHECK(!detour_match(NULL, kEntryPrologues, KEYPAD_COUNT(kEntryPrologues)));
    CHECK(!detour_match(e15, kShownPrologues, KEYPAD_COUNT(kShownPrologues)));
    CHECK(detour_match(shown, kShownPrologues, KEYPAD_COUNT(kShownPrologues)) == &kShownPrologues[0]);
    CHECK(detour_match(draw, kDrawPrologues, KEYPAD_COUNT(kDrawPrologues)) == &kDrawPrologues[0]);
    // every entry: the stolen bytes are inside the signature and a jmp rel32 fits
    const struct { const DetourPrologue *l; size_t n; } all[] = {
        {kEntryPrologues, KEYPAD_COUNT(kEntryPrologues)}, {kShownPrologues, KEYPAD_COUNT(kShownPrologues)},
        {kDrawPrologues, KEYPAD_COUNT(kDrawPrologues)}};
    for (size_t t = 0; t < 3; t++)
        for (size_t i = 0; i < all[t].n; i++)
            CHECK(all[t].l[i].steal >= DETOUR_MIN && all[t].l[i].steal <= DETOUR_MAX && all[t].l[i].steal <= all[t].l[i].n);
    // a frame of another size (a rebuild of 0.1.15's source) is still the 0.1.15 entry
    uint8_t alt[sizeof kFnEntry15];
    memcpy(alt, kFnEntry15, sizeof alt);
    alt[10] = 0x40;
    CHECK(detour_match(alt, kEntryPrologues, KEYPAD_COUNT(kEntryPrologues)) == &kEntryPrologues[1]);

    // installed the way keypad.c does it: both functions still run, through their hooks
    if (p13) CHECK(detour_install(e13, p13->sig, p13->steal, (void *)hook_entry13, (void **)&g_orig_entry13));
    if (p15) CHECK(detour_install(e15, p15->sig, p15->steal, (void *)hook_entry15, (void **)&g_orig_entry15));
    CHECK(e13[0] == 0xE9 && e13[7] == 0x48 && e13[8] == 0x83);                    // sub rsp,0x20 intact
    CHECK(e15[0] == 0xE9 && e15[7] == 0x48 && e15[8] == 0x81 && e15[10] == 0x30);   // sub rsp,0xb30 intact
    CHECK(((EntryFn)(void *)e13)(NULL, NULL, 0, NULL) == 142);
    CHECK(((EntryFn)(void *)e15)(NULL, NULL, 0, NULL) == 242);
    CHECK(g_orig_entry15 && g_orig_entry15(NULL, NULL, 0, NULL) == 42);
}

// ---- keypad --------------------------------------------------------------------------------------------------------

static void u16(const char *s, uint16_t *out) {
    size_t i = 0;
    for (; s[i]; i++) out[i] = (uint8_t)s[i];
    out[i] = 0;
}

static void test_keypad(void) {
    Keypad k;
    kp_init(&k, 6, 6, NULL);
    CHECK(k.n == 0 && kp_focus_key(&k) == 5 && !kp_ok_enabled(&k));
    // grid
    CHECK(kp_key_at(0, 0) == 1 && kp_key_at(2, 2) == 9 && kp_key_at(3, 0) == KP_KEY_BACKSPACE && kp_key_at(3, 1) == 0);
    CHECK(kp_key_at(3, 2) == KP_KEY_OK && kp_key_at(4, 1) == KP_KEY_CANCEL && kp_key_at(5, 0) == -1);

    // pad: 5 (A), right -> 6 (A), up -> 3 (A)
    kp_input(&k, KP_ACCEPT);
    kp_input(&k, KP_RIGHT);
    kp_input(&k, KP_ACCEPT);
    kp_input(&k, KP_UP);
    kp_input(&k, KP_ACCEPT);
    CHECK(!strcmp(k.digits, "563"));
    // OK is refused below the minimum, from the key and from Start
    kp_input(&k, KP_CONFIRM);
    kp_press(&k, KP_KEY_OK);
    CHECK(k.result == KP_OPEN);
    // B deletes; keyboard digits; Backspace
    kp_input(&k, KP_BACK);
    CHECK(!strcmp(k.digits, "56"));
    kp_press(&k, 7); kp_press(&k, 3); kp_press(&k, 1);
    kp_input(&k, KP_DELETE);
    kp_press(&k, 6); kp_press(&k, 0);
    CHECK(!strcmp(k.digits, "567360") && k.n == 6);
    CHECK(kp_focus_key(&k) == KP_KEY_OK && kp_ok_enabled(&k));   // complete: the focus is on OK
    kp_press(&k, 9);                                              // full: ignored
    CHECK(k.n == 6);
    kp_input(&k, KP_ACCEPT);
    CHECK(k.result == KP_DONE_OK && !strcmp(k.digits, "567360"));
    kp_input(&k, KP_BACK);                                        // closed: inert
    CHECK(k.n == 6 && k.result == KP_DONE_OK);

    // B on an empty pad cancels; Backspace on an empty pad does not
    kp_init(&k, 6, 6, NULL);
    kp_input(&k, KP_DELETE);
    CHECK(k.result == KP_OPEN);
    kp_input(&k, KP_BACK);
    CHECK(k.result == KP_DONE_CANCEL);
    kp_init(&k, 6, 6, "12");
    kp_input(&k, KP_ESCAPE);
    CHECK(k.result == KP_DONE_CANCEL);

    // navigation wraps; the Cancel row keeps the column
    kp_init(&k, 1, 4, NULL);
    kp_input(&k, KP_LEFT); kp_input(&k, KP_LEFT);
    CHECK(kp_focus_key(&k) == 6);
    kp_input(&k, KP_DOWN); kp_input(&k, KP_DOWN);
    CHECK(kp_focus_key(&k) == KP_KEY_OK);
    kp_input(&k, KP_DOWN);
    CHECK(kp_focus_key(&k) == KP_KEY_CANCEL);
    kp_input(&k, KP_LEFT);
    kp_input(&k, KP_DOWN);
    CHECK(kp_focus_key(&k) == 3);
    kp_input(&k, KP_UP); kp_input(&k, KP_ACCEPT);
    CHECK(k.result == KP_DONE_CANCEL);

    // prefill and clamps
    kp_init(&k, 6, 6, "731605");
    CHECK(!strcmp(k.digits, "731605") && kp_focus_key(&k) == KP_KEY_OK);
    kp_input(&k, KP_CONFIRM);
    CHECK(k.result == KP_DONE_OK);
    kp_init(&k, 9, 99, "12a3");
    CHECK(k.max == KP_MAX_DIGITS && k.min == 9 && !strcmp(k.digits, "123"));
    kp_init(&k, 0, 4, NULL);
    kp_input(&k, KP_CONFIRM);
    CHECK(k.result == KP_OPEN);   // never an empty answer

    // which keyboards are taken
    uint16_t t[64];
    u16("Insira ID do grupo", t);
    CHECK(kp_eligible(0, 0x80, 6, t, 64));
    CHECK(kp_eligible(0, 0, 6, t, 64));           // by the header alone
    CHECK(kp_eligible(1, 0, 4, t, 64));
    CHECK(!kp_eligible(1, 0x80, 32, t, 64));      // too long for a keypad
    u16("Enter a nickname", t);
    CHECK(!kp_eligible(0, 0, 10, t, 64));
    u16("IDEAL VIDEO", t);
    CHECK(!kp_title_mentions_id(t, 64));
    u16("Group ID", t);
    CHECK(kp_title_mentions_id(t, 64));

    char utf8[32];
    static const uint16_t kTitle[] = {'N', 0xFA, 'm', 'e', 'r', 'o', ' ', 0x20AC, 0xD83D, 0xDE00, 0};
    CHECK(kp_utf16_to_utf8(kTitle, 16, utf8, sizeof utf8) == 15);
    CHECK(!strcmp(utf8, "N\xC3\xBAmero \xE2\x82\xAC\xF0\x9F\x98\x80"));
    CHECK(kp_utf16_to_utf8(kTitle, 16, utf8, 3) == 1 && !strcmp(utf8, "N"));   // never a cut sequence

    // layout: on screen, keys disjoint, every key hit at its centre (Steam Deck and 4K)
    static const float kSizes[][2] = {{1280, 800}, {3840, 2160}, {1920, 1080}, {640, 480}};
    for (size_t s = 0; s < sizeof kSizes / sizeof kSizes[0]; s++) {
        KpLayout l;
        kp_layout(kSizes[s][0], kSizes[s][1], &l);
        CHECK(l.panel.x0 >= 0 && l.panel.y0 >= 0 && l.panel.x1 <= kSizes[s][0] && l.panel.y1 <= kSizes[s][1]);
        CHECK(l.hint.y1 <= l.panel.y1 && l.title.y0 >= l.panel.y0);
        for (int i = 0; i < KP_KEYS; i++) {
            const KpRect *r = &l.key[i];
            CHECK(r->x1 > r->x0 && r->y1 > r->y0 && r->x0 >= l.panel.x0 && r->x1 <= l.panel.x1);
            CHECK(kp_hit(&l, (r->x0 + r->x1) / 2, (r->y0 + r->y1) / 2) == i);
        }
        CHECK(kp_hit(&l, l.panel.x0 + 1, l.panel.y0 + 1) == -1);
    }
    kp_init(&k, 6, 6, NULL);
    kp_focus(&k, KP_KEY_BACKSPACE);
    CHECK(kp_focus_key(&k) == KP_KEY_BACKSPACE);
    kp_focus(&k, KP_KEY_CANCEL);
    CHECK(kp_focus_key(&k) == KP_KEY_CANCEL && k.col == 0);
}

int main(void) {
    test_detour();
    test_prologues();
    test_keypad();
    printf("test_keypad: %s\n", failures ? "FAIL" : "PASS");
    return failures != 0;
}
