#include "guesthook.h"

#include "log.h"

#include "plat.h"
#include <string.h>

static uint64_t g_next_fake = 0x00000FF000000000ull;   // far outside every guest module and the flat table

static int mapped(uint64_t addr, size_t n) { return plat_mem_readable((const void *)(uintptr_t)addr) >= n; }

int game_build(void) {
    static const struct { int build; uint32_t mod0; uint32_t note; uint8_t id[16]; } kBuilds[] = {
        {GAME_100, 0x02830054u, 0x30a7e78u,
         {0x9e, 0x24, 0x44, 0x38, 0xc3, 0xd8, 0xef, 0x1c, 0x70, 0x3a, 0x6a, 0x85, 0xef, 0x99, 0xc4, 0xf0}},
        {GAME_180, 0x02bb3060u, 0x346ba64u,
         {0xd0, 0x0e, 0x4f, 0x39, 0x9e, 0x41, 0x1d, 0x06, 0x13, 0xaa, 0x8d, 0x26, 0xb1, 0xc0, 0x2f, 0xef}},
    };
    static volatile int32_t cached;
    if (cached) return cached;
    uint64_t main = GUEST_MAIN(0);
    if (!mapped(main + 4, 4)) return GAME_UNKNOWN;   // not loaded yet: ask again later
    uint32_t mod0 = *(const uint32_t *)(uintptr_t)(main + 4);
    int build = GAME_UNKNOWN;
    for (size_t i = 0; i < sizeof kBuilds / sizeof kBuilds[0]; i++) {
        if (kBuilds[i].mod0 != mod0) continue;
        uint64_t id = main + kBuilds[i].note;
        if (mapped(id, 16) && !memcmp((const void *)(uintptr_t)id, kBuilds[i].id, 16)) build = kBuilds[i].build;
        break;
    }
    if (build == GAME_UNKNOWN) build = GAME_OTHER;   // mapped but not a build we know: final
    plat_xchg(&cached, build);
    mkwmp_event("game build: %s (main+4 = %#x)", game_build_name(build), mod0);
    return build;
}

const char *game_build_name(int build) {
    return build == GAME_100 ? "1.0.0" : build == GAME_180 ? "1.8.0" : build == GAME_UNKNOWN ? "not loaded" : "unknown";
}

int guesthook_init(void) {
    static volatile int32_t said;
    if (g_rt.host_fn_at || g_rt.override_address) return 1;
    if (!plat_xchg(&said, 1))
        mkwmp_log("guesthook: the runtime cannot route indirect calls to us now (no g_host, or the plugin API's "
                  "address overrides are closed: the game is running); vtable hooks off");
    return 0;
}

int guesthook_vtable(uint64_t vtable, int slot, GuestFn fn, uint64_t *original) {
    if (!guesthook_init()) return 0;
    uint64_t *at = (uint64_t *)(uintptr_t)(vtable + 8ull * (uint64_t)slot);
    if (plat_mem_readable(at) < 8) {
        mkwmp_log("guesthook: vtable %#llx slot %d not mapped", (unsigned long long)vtable, slot);
        return 0;
    }
    uint64_t old = *at;
    if (old >= 0x00000FF000000000ull && old < 0x0000100000000000ull) {   // ours already
        return 1;
    }
    if (!g_rt.host_fn_at) {
        // the plugin API: the slot stays; the function it holds is overridden for every indirect caller, so fn checks
        // its object (the hook's caller does) and passes anything else on with guesthook_original
        if (!old || !g_rt.override_address(old, fn)) {
            mkwmp_log("guesthook: override of %#llx (vtable %#llx slot %d) refused", (unsigned long long)old,
                      (unsigned long long)vtable, slot);
            return 0;
        }
        *original = old;
        mkwmp_log("guesthook: vtable %#llx slot %d: %#llx overridden", (unsigned long long)vtable, slot,
                  (unsigned long long)old);
        return 1;
    }
    uint64_t fake = g_next_fake;
    g_next_fake += 16;
    if (!g_rt.host_fn_at(fake, fn)) { mkwmp_log("guesthook: host registry full"); return 0; }
    *original = old;   // before the slot changes: ours may run at once
    if (!plat_mem_write_u64(at, fake)) {
        mkwmp_log("guesthook: cannot write vtable slot %#llx (error %lu)", (unsigned long long)(uintptr_t)at,
                  plat_last_error());
        return 0;
    }
    mkwmp_log("guesthook: vtable %#llx slot %d: %#llx -> ours", (unsigned long long)vtable, slot, (unsigned long long)old);
    return 1;
}

uint64_t guest_call(Cpu *c, uint64_t fn, int n, uint64_t a0, uint64_t a1, uint64_t a2, uint64_t a3) {
    switch (n) {
    case 0: return g_rt.call(c, fn, 0);
    case 1: return g_rt.call(c, fn, 1, a0);
    case 2: return g_rt.call(c, fn, 2, a0, a1);
    case 3: return g_rt.call(c, fn, 3, a0, a1, a2);
    default: return g_rt.call(c, fn, 4, a0, a1, a2, a3);
    }
}

uint64_t guesthook_original(Cpu *c, uint64_t original) {
    if (g_rt.host_fn_at) return g_rt.call(c, original, 0);   // the slot points at us: call its old target
    return g_rt.call_original(c);   // an address override: calling original would land in us again
}
