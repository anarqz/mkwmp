#include "coff.h"
#include "log.h"

#include <windows.h>
#include <stdlib.h>
#include <string.h>

RtApi g_rt;

// Names we need, in a fixed order mirrored by the pointer slots below. The first N_RT are g_rt's required entries;
// the rest fill its optional ones (or are read through coff_symbol).
static const char *const kNames[] = {
    "hostfn_add_override",
    "ldr_count",
    "ldr_symbol",
    "rt_log",
    "rt_call",
    "cfg_get",
    // optional: the runtime's static fs handlers (g_rt.fs_*)
    "ov_open_file",
    "ov_entry_type",
    "ov_read_file",
    "ov_file_size",
    "ov_close_file",
    "kern_core_away",
    "kern_core_back",
    // optional: the dispatch host registry (g_rt.host_fn_at)
    "g_host",
    // optional, last, never called: its presence says this mkw.exe loads native plugins (runtime/plugin/plugin.c,
    // mkw-recomp 0.1.15 on), which dllmain.c needs for the "mod wins" rule (modmode.h). Absent before: not logged.
    "plugins_load",
};
enum { N_RT = 6, N_SYMS = sizeof(kNames) / sizeof(kNames[0]), K_PLUGINS_LOAD = N_SYMS - 1 };
static void *g_slots[N_SYMS];

// Any resolved name of kNames (NULL if absent or not resolved yet).
void *coff_symbol(const char *name) {
    for (int k = 0; k < N_SYMS; k++)
        if (!strcmp(kNames[k], name)) return g_slots[k];
    return NULL;
}

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

// ---- g_rt.host_fn_at: mkw-recomp runtime/cpu/dispatch.c's
// static struct { uint64_t addr; GuestFn fn; } g_host[HOST_CAP]; (open addressing, its own hash)
enum { HOST_CAP = 8192 };
typedef struct { volatile uint64_t addr; GuestFn fn; } HostEntry;
static HostEntry *g_host;

static uint64_t host_hash(uint64_t addr) { return (addr >> 2) * 0x9E3779B97F4A7C15ull >> 51; }

static int coff_host_fn_at(uint64_t addr, GuestFn fn) {
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

// ---- g_rt.stock: mkw.exe's .ovr section, {const char *name, GuestFn fn} pairs (runtime/cpu/hostfn.c)
static GuestFn coff_stock(const char *name) {
    static const char **names;
    static GuestFn *fns;
    static int count = -1;
    if (count < 0) {
        count = 0;
        unsigned char *img = (unsigned char *)GetModuleHandleW(NULL);
        IMAGE_NT_HEADERS64 *nt = (IMAGE_NT_HEADERS64 *)(img + ((IMAGE_DOS_HEADER *)img)->e_lfanew);
        IMAGE_SECTION_HEADER *sec = IMAGE_FIRST_SECTION(nt);
        for (int i = 0; i < nt->FileHeader.NumberOfSections; i++, sec++) {
            if (memcmp(sec->Name, ".ovr", 5)) continue;
            uint64_t *p = (uint64_t *)(img + sec->VirtualAddress);
            int n = (int)(sec->Misc.VirtualSize / 16);
            names = calloc((size_t)n, sizeof *names);
            fns = calloc((size_t)n, sizeof *fns);
            for (int k = 0; k < n && names && fns; k++) {
                if (!p[2 * k] || !p[2 * k + 1]) continue;
                names[count] = (const char *)(uintptr_t)p[2 * k];
                fns[count++] = (GuestFn)(uintptr_t)p[2 * k + 1];
            }
        }
        mkwmp_log("dormant mode: %d stock handlers in .ovr", count);
        if (g_rt.log) g_rt.log("mkwmp: dormant mode: %d stock handlers in .ovr", count);   // as npln.c's mlog
    }
    for (int k = 0; k < count; k++) if (!strcmp(names[k], name)) return fns[k];
    return NULL;
}

// ---- g_rt.game_code_loaded / g_rt.dispatch_hook: the lifted game code is mkwgame.dll, whose indirect calls go
// through its import of mkw.exe!rt_dispatch; we swap that import (IAT entry) for ours.
static int coff_game_code_loaded(void) { return GetModuleHandleW(L"mkwgame.dll") != NULL; }

static int coff_dispatch_hook(GuestFn ours, GuestFn *original) {
    uint8_t *base = (void *)GetModuleHandleW(L"mkwgame.dll");
    if (!base) return 0;
    IMAGE_DOS_HEADER *dos = (void *)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return 0;
    IMAGE_NT_HEADERS64 *nt = (void *)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC) return 0;
    IMAGE_DATA_DIRECTORY dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!dir.VirtualAddress) return 0;
    IMAGE_IMPORT_DESCRIPTOR *imports = (void *)(base + dir.VirtualAddress);
    for (size_t i = 0; i < dir.Size / sizeof(*imports) && imports[i].Name; i++) {
        IMAGE_IMPORT_DESCRIPTOR *imp = &imports[i];
        if (_stricmp((char *)base + imp->Name, "mkw.exe") || !imp->OriginalFirstThunk) continue;
        IMAGE_THUNK_DATA64 *names = (void *)(base + imp->OriginalFirstThunk);
        IMAGE_THUNK_DATA64 *iat = (void *)(base + imp->FirstThunk);
        for (size_t j = 0; names[j].u1.AddressOfData; j++) {
            uint64_t rva = names[j].u1.AddressOfData;
            if (IMAGE_SNAP_BY_ORDINAL64(rva)) continue;
            IMAGE_IMPORT_BY_NAME *name = (void *)(base + rva);
            if (strcmp((char *)name->Name, "rt_dispatch")) continue;
            DWORD protection;
            if (!VirtualProtect(&iat[j], sizeof iat[j], PAGE_READWRITE, &protection)) return 0;
            *original = (GuestFn)(uintptr_t)iat[j].u1.Function;   // before the swap: ours may run at once
            InterlockedExchangePointer((void *volatile *)&iat[j].u1.Function, (void *)ours);
            VirtualProtect(&iat[j], sizeof iat[j], protection, &protection);
            return 1;
        }
    }
    return 0;
}

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
    void **slots = g_slots;

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

        // mkw-recomp runtime/cpu/hostfn.c, core/boot/loader.c, core/base/log.c, core/base/cfg.c
        g_rt.override     = (void (*)(const char *, GuestFn))slots[0];       // hostfn_add_override
        g_rt.guest_modules = (int (*)(void))slots[1];                       // ldr_count
        g_rt.guest_symbol = (uint64_t (*)(const char *))slots[2];           // ldr_symbol
        g_rt.log          = (void (*)(const char *, ...))slots[3];          // rt_log
        g_rt.call         = (uint64_t (*)(Cpu *, uint64_t, int, ...))slots[4];   // rt_call
        g_rt.cfg_get      = (const char *(*)(const char *))slots[5];
        g_rt.fs_open_file  = (GuestFn)slots[6];
        g_rt.fs_entry_type = (GuestFn)slots[7];
        g_rt.fs_read_file  = (GuestFn)slots[8];
        g_rt.fs_file_size  = (GuestFn)slots[9];
        g_rt.fs_close_file = (GuestFn)slots[10];
        g_rt.core_away = (int (*)(void))slots[11];        // kern_core_away
        g_rt.core_back = (void (*)(int *))slots[12];      // kern_core_back
        g_host = (HostEntry *)slots[13];
        g_rt.host_fn_at = g_host ? coff_host_fn_at : NULL;
        g_rt.stock = coff_stock;
        g_rt.game_code_loaded = coff_game_code_loaded;
        g_rt.dispatch_hook = coff_dispatch_hook;

        int req = 0;
        for (int k = 0; k < N_RT; k++) req += slots[k] != NULL;
        ok = (req == N_RT);
        for (int k = 0; k < N_SYMS; k++)
            if (!slots[k] && k != K_PLUGINS_LOAD) log_line2("coff: unresolved symbol:", kNames[k]);
    } while (0);

    HeapFree(GetProcessHeap(), 0, d);
    return ok;
}
