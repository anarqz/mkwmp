// Hooking guest virtual calls without override slots (resources/docs/VOICE-RE.md §5). Every indirect guest call goes
// through the recomp's rt_dispatch, which falls back to a host registry for addresses outside the guest modules
// (g_host; coff.c). We register {fake address, our fn} there (g_rt.host_fn_at) and point a guest vtable slot at the
// fake address; our fn receives the guest Cpu and calls the original with g_rt.call(c, original, 0).
#ifndef MKWMP_GUESTHOOK_H
#define MKWMP_GUESTHOOK_H

#include "rtapi.h"

#include <stdint.h>

enum { GUEST_MAIN_BASE_HI = 0x70 };              // main is loaded at 0x7000000000 (recomp layout.py FIRST_BASE)
#define GUEST_MAIN(off) (0x7000000000ull + (uint64_t)(off))

// Which game build is mapped at 0x7000000000: identified by main's MOD0 offset (main+4) and its GNU build-id note
// (resources/docs/GAME-1.8.0-ADDRESSES.md §1). Every main+0x... constant of the probe is in the build's row of
// gameaddrs.h: version-specific hooks take it from game_addrs() and stay off without a row. Cached after the first
// known answer.
enum { GAME_OTHER = -1, GAME_UNKNOWN = 0, GAME_100 = 100, GAME_180 = 180 };   // UNKNOWN = main not mapped yet
int game_build(void);
const char *game_build_name(int build);

// Returns 1 if hooks can be installed (the runtime routes indirect calls to host functions: g_rt.host_fn_at).
int guesthook_init(void);
// Point slot `slot` of the guest vtable at guest address `vtable` to `fn`. *original receives the slot's old target.
// Returns 1 on success. Idempotent per (vtable, slot).
int guesthook_vtable(uint64_t vtable, int slot, GuestFn fn, uint64_t *original);
// Call a guest function: up to 6 integer arguments; returns x0. Registers x0..x7 are replaced by the arguments.
uint64_t guest_call(Cpu *c, uint64_t fn, int n, uint64_t a0, uint64_t a1, uint64_t a2, uint64_t a3);

#endif
