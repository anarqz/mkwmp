// Hooking guest virtual calls without override slots (resources/docs/VOICE-RE.md §5). Every indirect guest call goes
// through the recomp's rt_dispatch, which falls back to its host registry `g_host` for addresses outside the guest
// modules. We add {fake address, our fn} there and point a guest vtable slot at the fake address; our fn receives the
// guest Cpu and calls the original with rt_call(c, original, 0) (registers untouched).
#ifndef MKWMP_GUESTHOOK_H
#define MKWMP_GUESTHOOK_H

#include "recomp.h"

#include <stdint.h>

enum { GUEST_MAIN_BASE_HI = 0x70 };              // main is loaded at 0x7000000000 (recomp layout.py FIRST_BASE)
#define GUEST_MAIN(off) (0x7000000000ull + (uint64_t)(off))

// Resolve g_host from mkw.exe's COFF table. Returns 1 if hooks can be installed.
int guesthook_init(void);
// Point slot `slot` of the guest vtable at guest address `vtable` to `fn`. *original receives the slot's old target.
// Returns 1 on success. Idempotent per (vtable, slot).
int guesthook_vtable(uint64_t vtable, int slot, GuestFn fn, uint64_t *original);
// Call a guest function: up to 6 integer arguments; returns x0. Registers x0..x7 are replaced by the arguments.
uint64_t guest_call(Cpu *c, uint64_t fn, int n, uint64_t a0, uint64_t a1, uint64_t a2, uint64_t a3);

#endif
