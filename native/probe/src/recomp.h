// Interface to the mkw-recomp runtime embedded in mkw.exe.
//
// mkw.exe is a static recompilation: the Switch ARM64 game is lifted to C. The guest's nn::* SDK
// calls are serviced by host functions of type `void fn(Cpu*)`, registered by name (g_rt.override,
// rtapi.h). A host fn reads arguments from the emulated CPU and writes the return into x0.
//
// Layout facts are from runtime/cpu/cpu.h and runtime/cpu/hostfn.c of mkw-recomp. If the game is
// updated and these shift, the build must be revisited — but symbol *names* are resolved
// dynamically, so only the struct offsets below are version-sensitive.
#ifndef MKWMP_RECOMP_H
#define MKWMP_RECOMP_H

#include <stdint.h>
#include <stddef.h>

// --- emulated CPU (prefix of runtime Cpu; we only touch the fields below) ---
// Offsets: x[0..31] at 0, sp at 256, pc at 264, thread at 312.
typedef struct Cpu {
    uint64_t x[32];   // 0:   x0..x30 (x31 unused)
    uint64_t sp;      // 256
    uint64_t pc;      // 264
    uint8_t  _pad[40];
    void    *thread;  // 312
} Cpu;

typedef void (*GuestFn)(Cpu *);

// AAPCS64 argument access, matching runtime core/base/host.h.
static inline uint64_t arg_x(const Cpu *c, int i) {
    return i < 8 ? c->x[i] : *(const uint64_t *)(uintptr_t)(c->sp + 8ull * (uint64_t)(i - 8));
}
static inline void *arg_p(const Cpu *c, int i) { return (void *)(uintptr_t)arg_x(c, i); }
static inline void  ret_x(Cpu *c, uint64_t v) { c->x[0] = v; }

// The runtime itself is reached through rtapi.h (g_rt).

#endif
