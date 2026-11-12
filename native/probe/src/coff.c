#include "coff.h"
#include "log.h"

#include <windows.h>
#include <string.h>

Runtime g_rt;
Delegates g_dele;

// Names we need, in a fixed order mirrored by the pointer slots below. The first N_RT go to g_rt
// (required); the rest are optional delegate handlers (g_dele).
static const char *const kNames[] = {
    "hostfn_add_override",
    "ldr_count",
    "ldr_symbol",
    "rt_log",
    "rt_call",
    "cfg_get",
    // optional delegates (static fs handlers)
    "ov_open_file",
    "ov_entry_type",
    "ov_read_file",
    "ov_file_size",
    "ov_close_file",
    "fiber_switch_to",
    "kern_fiber_switch",
    "kern_fiber_initialize",
    "g_fibers",
    "plat_fiber_new",
    "plat_fiber_free",
    "kern_cpu",
    "kern_core_away",
    "kern_core_back",
};
enum { N_RT = 6, N_SYMS = sizeof(kNames) / sizeof(kNames[0]) };

static uint8_t *read_own_image(uint32_t *out_size) {
    wchar_t path[MAX_PATH];
    DWORD n = GetModuleFileNameW(NULL, path, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return NULL;
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return NULL;
    LARGE_INTEGER sz;
    if (!GetFileSizeEx(h, &sz) || sz.QuadPart <= 0 || sz.QuadPart > (1LL << 31)) {
        CloseHandle(h);
        return NULL;
    }
    uint8_t *buf = (uint8_t *)HeapAlloc(GetProcessHeap(), 0, (SIZE_T)sz.QuadPart);
    if (!buf) {
        CloseHandle(h);
        return NULL;
    }
    SIZE_T got = 0;
    while (got < (SIZE_T)sz.QuadPart) {
        DWORD rd = 0;
        DWORD want = (DWORD)((SIZE_T)sz.QuadPart - got > 0x400000 ? 0x400000 : (SIZE_T)sz.QuadPart - got);
        if (!ReadFile(h, buf + got, want, &rd, NULL) || rd == 0) break;
        got += rd;
    }
    CloseHandle(h);
    if (got != (SIZE_T)sz.QuadPart) {
        HeapFree(GetProcessHeap(), 0, buf);
        return NULL;
    }
    *out_size = (uint32_t)sz.QuadPart;
    return buf;
}

static uint32_t rd32(const uint8_t *p) { uint32_t v; memcpy(&v, p, 4); return v; }
static uint16_t rd16(const uint8_t *p) { uint16_t v; memcpy(&v, p, 2); return v; }

int coff_resolve_runtime(void) {
    uintptr_t base = (uintptr_t)GetModuleHandleW(NULL);
    if (!base) return 0;

    uint32_t size = 0;
    uint8_t *d = read_own_image(&size);
    if (!d) {
        log_line("coff: could not read own image");
        return 0;
    }

    int ok = 0;
    void *slots[N_SYMS] = {0};

    do {
        if (size < 0x40 || d[0] != 'M' || d[1] != 'Z') break;
        uint32_t pe = rd32(d + 0x3c);
        if (pe + 24 > size || memcmp(d + pe, "PE\0\0", 4) != 0) break;

        uint16_t nsec = rd16(d + pe + 6);
        uint32_t symptr = rd32(d + pe + 12);
        uint32_t nsym = rd32(d + pe + 16);
        uint16_t osz = rd16(d + pe + 20);
        if (!symptr || !nsym) {
            log_line("coff: no COFF symbol table (stripped?)");
            break;
        }

        const uint8_t *sechdr = d + pe + 24 + osz;
        if ((const uint8_t *)(sechdr + (size_t)nsec * 40) > d + size) break;

        // section VirtualAddress by 1-based index
        uint32_t *secva = (uint32_t *)HeapAlloc(GetProcessHeap(), 0, (size_t)nsec * sizeof(uint32_t));
        if (!secva) break;
        for (uint16_t i = 0; i < nsec; i++) secva[i] = rd32(sechdr + (size_t)i * 40 + 12);

        const uint8_t *symtab = d + symptr;
        const uint8_t *strtab = symtab + (size_t)nsym * 18;
        if (strtab + 4 > d + size) { HeapFree(GetProcessHeap(), 0, secva); break; }
        uint32_t strsz = rd32(strtab);

        uint32_t resolved = 0;
        for (uint32_t i = 0; i < nsym && resolved < N_SYMS; ) {
            const uint8_t *s = symtab + (size_t)i * 18;
            uint8_t naux = s[17];
            int16_t secno = (int16_t)rd16(s + 12);
            // name
            char namebuf[9];
            const char *name;
            if (rd32(s) == 0) {
                uint32_t off = rd32(s + 4);
                name = (off < strsz && strtab + off < d + size) ? (const char *)(strtab + off) : "";
            } else {
                memcpy(namebuf, s, 8);
                namebuf[8] = 0;
                name = namebuf;
            }
            if (secno > 0 && secno <= (int)nsec && *name) {
                for (int k = 0; k < N_SYMS; k++) {
                    if (!slots[k] && strcmp(name, kNames[k]) == 0) {
                        uint32_t rva = secva[secno - 1] + rd32(s + 8);
                        slots[k] = (void *)(base + rva);
                        resolved++;
                        break;
                    }
                }
            }
            i += 1u + naux;
        }
        HeapFree(GetProcessHeap(), 0, secva);

        g_rt.hostfn_add_override = (fn_hostfn_add_override)slots[0];
        g_rt.ldr_count           = (fn_ldr_count)slots[1];
        g_rt.ldr_symbol          = (fn_ldr_symbol)slots[2];
        g_rt.rt_log              = (fn_rt_log)slots[3];
        g_rt.rt_call             = (fn_rt_call)slots[4];
        g_rt.cfg_get             = (fn_cfg_get)slots[5];
        g_dele.fs_open_file  = (GuestFn)slots[6];
        g_dele.fs_entry_type = (GuestFn)slots[7];
        g_dele.fs_read_file  = (GuestFn)slots[8];
        g_dele.fs_file_size  = (GuestFn)slots[9];
        g_dele.fs_close_file = (GuestFn)slots[10];
        g_dele.fiber_switch_to = (void (*)(void *, const Cpu *, void *))slots[11];
        g_dele.fiber_switch = (GuestFn)slots[12];
        g_dele.fiber_initialize = (GuestFn)slots[13];
        g_dele.fibers = (void **)slots[14];
        g_dele.fiber_new = (void *(*)(size_t, void (*)(void *), void *))slots[15];
        g_dele.fiber_free = (void (*)(void *))slots[16];
        g_dele.current_cpu = (Cpu *(*)(void))slots[17];
        g_dele.core_away = (int (*)(void))slots[18];
        g_dele.core_back = (void (*)(int *))slots[19];

        int req = 0;
        for (int k = 0; k < N_RT; k++) req += slots[k] != NULL;
        ok = (req == N_RT);
        for (int k = 0; k < N_SYMS; k++)
            if (!slots[k]) log_line2("coff: unresolved symbol:", kNames[k]);
    } while (0);

    HeapFree(GetProcessHeap(), 0, d);
    return ok;
}
