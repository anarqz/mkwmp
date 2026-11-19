#include "detour.h"

#include <windows.h>
#include <string.h>

// One executable page near the hooked module holds, per hook, a 64-byte slot:
//   +0   relay       jmp [rip+0] ; <hook>            (the target's 5-byte jmp rel32 lands here)
//   +16  trampoline  <stolen prologue> ; jmp [rip+0] ; <target + n>
enum { PAGE = 0x1000, SLOT = 64, REACH = 0x70000000 };

static uint8_t *g_page;
static size_t g_used;

static uint8_t *alloc_at(uintptr_t a) {
    return (uint8_t *)VirtualAlloc((void *)a, PAGE, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE);
}

// A free page within REACH of `origin` (so a rel32 from anywhere in the module reaches it).
static uint8_t *alloc_near(uintptr_t origin) {
    SYSTEM_INFO si;
    GetSystemInfo(&si);
    uintptr_t gran = si.dwAllocationGranularity ? si.dwAllocationGranularity : 0x10000;
    uintptr_t lo = origin > REACH ? origin - REACH : gran;
    uintptr_t hi = origin + REACH;
    MEMORY_BASIC_INFORMATION mbi;
    for (uintptr_t p = origin; p < hi && VirtualQuery((void *)p, &mbi, sizeof mbi) == sizeof mbi;
         p = (uintptr_t)mbi.BaseAddress + mbi.RegionSize) {
        if (mbi.State != MEM_FREE) continue;
        uintptr_t base = (uintptr_t)mbi.BaseAddress, end = base + mbi.RegionSize;
        uintptr_t a = (base + gran - 1) & ~(gran - 1);
        if (a + PAGE <= end && a < hi) {
            uint8_t *r = alloc_at(a);
            if (r) return r;
        }
    }
    for (uintptr_t p = origin - 1; p > lo && VirtualQuery((void *)p, &mbi, sizeof mbi) == sizeof mbi;
         p = (uintptr_t)mbi.BaseAddress - 1) {
        uintptr_t base = (uintptr_t)mbi.BaseAddress, end = base + mbi.RegionSize;
        if (mbi.State == MEM_FREE && end >= PAGE) {
            uintptr_t a = (end - PAGE) & ~(gran - 1);
            if (a >= base && a > lo) {
                uint8_t *r = alloc_at(a);
                if (r) return r;
            }
        }
        if (base <= gran) break;
    }
    return NULL;
}

static int rel32_fits(const uint8_t *from_end, const uint8_t *to) {
    intptr_t d = (intptr_t)to - (intptr_t)from_end;
    return d >= INT32_MIN && d <= INT32_MAX;
}

static uint8_t *put_jmp_abs(uint8_t *p, uintptr_t to) {
    static const uint8_t op[6] = {0xFF, 0x25, 0, 0, 0, 0};   // jmp qword ptr [rip+0]
    memcpy(p, op, 6);
    memcpy(p + 6, &to, 8);
    return p + 14;
}

const DetourPrologue *detour_match(const void *target, const DetourPrologue *list, size_t count) {
    for (size_t i = 0; target && list && i < count; i++)
        if (list[i].sig && list[i].steal <= list[i].n && !memcmp(target, list[i].sig, list[i].n)) return &list[i];
    return NULL;
}

int detour_install(void *target, const uint8_t *expect, size_t n, void *hook, void **original) {
    uint8_t *t = (uint8_t *)target;
    if (!t || !expect || !hook || !original || n < DETOUR_MIN || n > DETOUR_MAX) return 0;
    if (memcmp(t, expect, n) != 0) return 0;

    if (g_page && (g_used + SLOT > PAGE || !rel32_fits(t + 5, g_page + g_used))) g_page = NULL;
    if (!g_page) {
        g_page = alloc_near((uintptr_t)t);
        g_used = 0;
        if (!g_page) return 0;
    }
    uint8_t *slot = g_page + g_used;
    if (!rel32_fits(t + 5, slot)) return 0;

    DWORD page_prot = 0, old = 0;
    if (!VirtualProtect(g_page, PAGE, PAGE_EXECUTE_READWRITE, &page_prot)) return 0;
    memset(slot, 0xCC, SLOT);
    put_jmp_abs(slot, (uintptr_t)hook);
    uint8_t *tramp = slot + 16;
    memcpy(tramp, t, n);
    put_jmp_abs(tramp + n, (uintptr_t)(t + n));
    VirtualProtect(g_page, PAGE, PAGE_EXECUTE_READ, &page_prot);
    FlushInstructionCache(GetCurrentProcess(), slot, SLOT);

    if (!VirtualProtect(t, n, PAGE_EXECUTE_READWRITE, &old)) return 0;   // the slot is simply left unused
    *original = tramp;   // before the patch: the hook may run as soon as the jump is in place
    uint8_t patch[DETOUR_MAX];
    int32_t rel = (int32_t)((intptr_t)slot - (intptr_t)(t + 5));
    patch[0] = 0xE9;
    memcpy(patch + 1, &rel, 4);
    memset(patch + 5, 0x90, sizeof patch - 5);
    memcpy(t, patch, n);
    VirtualProtect(t, n, old, &old);
    FlushInstructionCache(GetCurrentProcess(), t, n);
    g_used += SLOT;
    return 1;
}
