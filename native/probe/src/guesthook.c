#include "guesthook.h"

#include "log.h"

#include <windows.h>
#include <string.h>

// mkw-recomp runtime/cpu/dispatch.c: static struct { uint64_t addr; GuestFn fn; } g_host[HOST_CAP];
enum { HOST_CAP = 8192 };
typedef struct { volatile uint64_t addr; GuestFn fn; } HostEntry;
static HostEntry *g_host;
static uint64_t g_next_fake = 0x00000FF000000000ull;   // far outside every guest module and the flat table

void *coff_symbol(const char *name);

int guesthook_init(void) {
    if (g_host) return 1;
    g_host = (HostEntry *)coff_symbol("g_host");
    if (!g_host) mkwmp_log("guesthook: g_host not found in mkw.exe; in-game voice UI off");
    return g_host != NULL;
}

static uint64_t host_hash(uint64_t addr) { return (addr >> 2) * 0x9E3779B97F4A7C15ull >> 51; }

static int host_insert(uint64_t addr, GuestFn fn) {
    for (uint64_t h = host_hash(addr), n = 0; n < HOST_CAP; h = (h + 1) & (HOST_CAP - 1), n++) {
        if (g_host[h].addr == addr) { g_host[h].fn = fn; return 1; }
        if (!g_host[h].addr) {
            g_host[h].fn = fn;   // the function first: a reader that sees the address sees it
            __atomic_store_n(&g_host[h].addr, addr, __ATOMIC_RELEASE);
            return 1;
        }
    }
    return 0;
}

int guesthook_vtable(uint64_t vtable, int slot, GuestFn fn, uint64_t *original) {
    if (!guesthook_init()) return 0;
    uint64_t *at = (uint64_t *)(uintptr_t)(vtable + 8ull * (uint64_t)slot);
    MEMORY_BASIC_INFORMATION mi;
    if (!VirtualQuery(at, &mi, sizeof mi) || mi.State != MEM_COMMIT) {
        mkwmp_log("guesthook: vtable %#llx slot %d not mapped", (unsigned long long)vtable, slot);
        return 0;
    }
    uint64_t old = *at;
    if (old >= 0x00000FF000000000ull && old < 0x0000100000000000ull) {   // ours already
        return 1;
    }
    uint64_t fake = g_next_fake;
    g_next_fake += 16;
    if (!host_insert(fake, fn)) { mkwmp_log("guesthook: host registry full"); return 0; }
    DWORD prot;
    if (!VirtualProtect(at, 8, PAGE_READWRITE, &prot)) {
        mkwmp_log("guesthook: VirtualProtect %#llx failed (%lu)", (unsigned long long)(uintptr_t)at, GetLastError());
        return 0;
    }
    *original = old;
    __atomic_store_n(at, fake, __ATOMIC_RELEASE);
    VirtualProtect(at, 8, prot, &prot);
    FlushInstructionCache(GetCurrentProcess(), at, 8);
    mkwmp_log("guesthook: vtable %#llx slot %d: %#llx -> ours", (unsigned long long)vtable, slot, (unsigned long long)old);
    return 1;
}

uint64_t guest_call(Cpu *c, uint64_t fn, int n, uint64_t a0, uint64_t a1, uint64_t a2, uint64_t a3) {
    switch (n) {
    case 0: return g_rt.rt_call(c, fn, 0);
    case 1: return g_rt.rt_call(c, fn, 1, a0);
    case 2: return g_rt.rt_call(c, fn, 2, a0, a1);
    case 3: return g_rt.rt_call(c, fn, 3, a0, a1, a2);
    default: return g_rt.rt_call(c, fn, 4, a0, a1, a2, a3);
    }
}
