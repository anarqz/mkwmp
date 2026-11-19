// The first bytes of the three mkw.exe functions keypad.c detours, per mkw-recomp release, as disassembled from the
// official Windows builds (research/docs/RECOMP-<version>.md, section "Detours"). Each table lists every accepted
// prologue with its steal length: the first *_STEAL bytes are whole, position-independent instructions and move to the
// trampoline (detour.h). A build whose function starts with none of them is left alone (no keypad).
//
// tools/recomp/check_release.py parses this file (the k*Sig arrays, the *_STEAL values and the k*Prologues tables) and
// checks a release's mkw.exe against it: keep the shapes below when adding a prologue.
#ifndef MKWMP_KEYPAD_SIGS_H
#define MKWMP_KEYPAD_SIGS_H

#include "detour.h"

// text_entry_run (runtime/sdk/swkbd/swkbd_sdl.c)
// mkw-recomp 0.1.10 - 0.1.13: the stub that only takes the starting text.
static const uint8_t kEntrySig[] = {0x41, 0x57, 0x41, 0x56, 0x56, 0x57, 0x53,      // push r15; r14; rsi; rdi; rbx
                                    0x48, 0x83, 0xEC, 0x20, 0x4C, 0x89, 0xCE};      // sub rsp,0x20; mov rsi,r9
// mkw-recomp 0.1.15: it converts the prompt for the runtime's own number pad first, so its frame is large
// (sub rsp,0xb30 in the official build). The frame size is left out of the signature: it follows the locals, and a
// rebuild of the same source (the 0.1.15-mkwmp fork) may pick another. Only the five pushes are stolen.
static const uint8_t kEntrySig15[] = {0x41, 0x57, 0x41, 0x56, 0x56, 0x57, 0x53,    // push r15; r14; rsi; rdi; rbx
                                      0x48, 0x81, 0xEC};                            // sub rsp,imm32
// menu_notice_shown() and menu_notice_draw(float, float) (runtime/ui/menu/menu_notice.cpp): 0.1.10 - 0.1.15 alike.
static const uint8_t kShownSig[] = {0x56, 0x48, 0x83, 0xEC, 0x20,                  // push rsi; sub rsp,0x20
                                    0x48, 0x8B, 0x35};                              // mov rsi,[rip+g_until]
static const uint8_t kDrawSig[] = {0x56, 0x57, 0x48, 0x81, 0xEC, 0x78, 0x01, 0x00, 0x00,   // push rsi; rdi; sub rsp,0x178
                                   0xC5, 0x78, 0x29, 0x84, 0x24};                           // vmovaps [rsp+..],xmm8
enum { ENTRY_STEAL = 7, ENTRY15_STEAL = 7, SHOWN_STEAL = 5, DRAW_STEAL = 9 };

static const DetourPrologue kEntryPrologues[] = {
    {kEntrySig, sizeof kEntrySig, ENTRY_STEAL},
    {kEntrySig15, sizeof kEntrySig15, ENTRY15_STEAL},
};
static const DetourPrologue kShownPrologues[] = {
    {kShownSig, sizeof kShownSig, SHOWN_STEAL},
};
static const DetourPrologue kDrawPrologues[] = {
    {kDrawSig, sizeof kDrawSig, DRAW_STEAL},
};
#define KEYPAD_COUNT(a) (sizeof(a) / sizeof((a)[0]))

#endif
