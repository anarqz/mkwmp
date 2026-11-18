// Minimal x64 inline hook for recomp-internal functions of mkw.exe (research/docs/SWKBD-KEYPAD.md).
// No length decoder: the caller states the exact prologue bytes it expects (whole, position-independent
// instructions, verified by disassembly) and nothing is patched unless the live bytes match.
#ifndef MKWMP_DETOUR_H
#define MKWMP_DETOUR_H

#include <stddef.h>
#include <stdint.h>

enum { DETOUR_MIN = 5, DETOUR_MAX = 16 };

// Redirect `target` to `hook`. `expect` are the first `n` bytes of the target (DETOUR_MIN..DETOUR_MAX): they must be
// whole instructions without rip-relative operands or branch targets inside, because they are copied to a
// trampoline. *original receives the trampoline (those bytes + a jump to target+n), callable as the target was.
// Returns 1 on success; 0 (nothing changed) on a prologue mismatch or when no memory within +-2 GB could be had.
// Not atomic: install while no other thread can be executing the target (the probe does it in DllMain).
int detour_install(void *target, const uint8_t *expect, size_t n, void *hook, void **original);

#endif
