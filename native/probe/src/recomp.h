// Interface to the mkw-recomp runtime embedded in mkw.exe.
//
// mkw.exe is a static recompilation: the Switch ARM64 game is lifted to C. The guest's nn::* SDK
// calls are serviced by host functions of type `void fn(Cpu*)`, registered by name. We register
// our own through the runtime's hostfn_add_override (resolved from the COFF symbol table, see
// coff.c). A host fn reads arguments from the emulated CPU and writes the return into x0.
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

// --- runtime functions resolved from mkw.exe's COFF symbols (coff.c fills these) ---
typedef void     (*fn_hostfn_add_override)(const char *name, GuestFn fn);
typedef int      (*fn_ldr_count)(void);
typedef uint64_t (*fn_ldr_symbol)(const char *name);
typedef void     (*fn_rt_log)(const char *fmt, ...);
typedef uint64_t (*fn_rt_call)(Cpu *c, uint64_t fn, int n, ...);
typedef const char *(*fn_cfg_get)(const char *key);

typedef struct {
    fn_hostfn_add_override hostfn_add_override;
    fn_ldr_count           ldr_count;
    fn_ldr_symbol          ldr_symbol;
    fn_rt_log              rt_log;
    fn_rt_call             rt_call;
    fn_cfg_get             cfg_get;
} Runtime;

extern Runtime g_rt;

// Recomp-internal host handlers (static fns in mkw.exe) we delegate to after logging, so normal
// behaviour is preserved. Resolved by name from COFF (coff.c); NULL if not found.
typedef struct {
    GuestFn fs_open_file;   // ov_open_file
    GuestFn fs_entry_type;  // ov_entry_type
    GuestFn fs_read_file;   // ov_read_file
    GuestFn fs_file_size;   // ov_file_size
    GuestFn fs_close_file;  // ov_close_file
    void (*fiber_switch_to)(void *thread, const Cpu *from, void *to);
    GuestFn fiber_switch;
    GuestFn fiber_initialize;
    void **fibers;
    void *(*fiber_new)(size_t stack_size, void (*entry)(void *), void *arg);
    void (*fiber_free)(void *fiber);
    Cpu *(*current_cpu)(void);
    int (*core_away)(void);
    void (*core_back)(int *away);
} Delegates;

extern Delegates g_dele;

#endif
