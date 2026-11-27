// The per-build address table (gameaddrs.h): game_build() -> row, zero fields that turn a feature off, the guards.
// A fake "main" is mapped at the guest base 0x7000000000 with one build's MOD0 offset and build-id note; game_build()
// caches its answer, so each build is one run of the exe.
//
//   $zig = "C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"
//   & $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o scratch\test_gameaddrs.exe `
//       native\probe\tests\test_gameaddrs.c native\probe\tests\stub_coff.c native\probe\src\gameaddrs.c `
//       native\probe\src\guesthook.c native\probe\src\log.c native\probe\src\plat_win.c
//   foreach ($b in "none", "100", "180", "other", "badid") { scratch\test_gameaddrs.exe $b }
#include "../src/gameaddrs.h"
#include "../src/guesthook.h"

#include <windows.h>
#include <stdio.h>
#include <string.h>

RtApi g_rt;

static int failures;
#define CHECK(c) do { if (!(c)) { printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #c); failures++; } } while (0)

static const uint8_t kId100[16] = {0x9e, 0x24, 0x44, 0x38, 0xc3, 0xd8, 0xef, 0x1c, 0x70, 0x3a, 0x6a, 0x85, 0xef, 0x99, 0xc4, 0xf0};
static const uint8_t kId180[16] = {0xd0, 0x0e, 0x4f, 0x39, 0x9e, 0x41, 0x1d, 0x06, 0x13, 0xaa, 0x8d, 0x26, 0xb1, 0xc0, 0x2f, 0xef};

static uint8_t *commit(uint64_t addr, size_t n) {
    uint64_t page = addr & ~0xffffull;   // allocation granularity
    MEMORY_BASIC_INFORMATION mi;
    if (VirtualQuery((void *)(uintptr_t)addr, &mi, sizeof mi) && mi.State == MEM_COMMIT) return (uint8_t *)(uintptr_t)addr;
    void *p = VirtualAlloc((void *)(uintptr_t)page, (size_t)(addr + n - page), MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    if (!p) { printf("FAIL cannot map %#llx (%lu)\n", (unsigned long long)addr, GetLastError()); failures++; return NULL; }
    return (uint8_t *)(uintptr_t)addr;
}

// main+4 = the MOD0 offset, and the 16 build-id bytes at the build's note offset.
static void map_main(uint32_t mod0, uint32_t note, const uint8_t *id) {
    uint8_t *head = commit(GUEST_MAIN(0), 16), *at = commit(GUEST_MAIN(note), 16);
    if (!head || !at) return;
    memcpy(head + 4, &mod0, 4);
    memcpy(at, id, 16);
}

static void put32(uint64_t addr, uint32_t v) { uint8_t *p = commit(addr, 4); if (p) memcpy(p, &v, 4); }
static void put64(uint64_t addr, uint64_t v) { uint8_t *p = commit(addr, 8); if (p) memcpy(p, &v, 8); }

// What every row must have: the functional fields, and a sane layout.
static void check_functional(const struct game_addrs *a) {
    CHECK(a->grpc_timedwait_lr > GUEST_MAIN(0) && a->grpc_timedwait_bl >> 26 == 0x25);   // a BL
    CHECK(a->errsvc_post > GUEST_MAIN(0) && a->errsvc_vtable > a->errsvc_post);
    CHECK(a->err_referee_timeout == 0x606314c9);
    CHECK(a->stock_lo <= a->stock_hi && (!a->stock_hi || a->stock_lo > GUEST_MAIN(0x3000000)));
}

int main(int argc, char **argv) {
    const char *mode = argc > 1 ? argv[1] : "none";

    // ---- the table itself (no game needed)
    const struct game_addrs *r100 = game_addrs_for(GAME_100), *r180 = game_addrs_for(GAME_180);
    CHECK(r100 && r180 && r100 != r180 && r100->build == GAME_100 && r180->build == GAME_180);
    CHECK(!game_addrs_for(GAME_UNKNOWN) && !game_addrs_for(GAME_OTHER) && !game_addrs_for(123));
    if (r100 && r180) {
        check_functional(r100);
        check_functional(r180);
        // 1.0.0: the values the probe shipped with
        CHECK(r100->grpc_timedwait_lr == 0x700104a8bcull && r100->errsvc_post == 0x700222d78cull);
        CHECK(r100->kart_idle_kick == 1 && !r100->stock_hi);
        CHECK(r100->game_global == 0x70033d2548ull && r100->kart_entry[0] && r100->kart_entry[1] && r100->room_sync);
        for (int i = 0; i < GAME_PIA_STEPS; i++) CHECK(r100->pia_join_step[i]);
        for (int i = 0; i < GAME_WATCH; i++) CHECK(r100->watch[i]);
        for (int i = 0; i < GAME_TRACE_RANGES; i++) CHECK(r100->trace_ranges[i][0] < r100->trace_ranges[i][1]);
        // 1.8.0: the functional rows of GAME-1.8.0-ADDRESSES.md §3 ...
        CHECK(r180->grpc_timedwait_lr == 0x700111232cull && r180->errsvc_post == 0x7002564cd0ull);
        CHECK(r180->stock_lo == 0x7003800000ull && r180->stock_hi == 0x70038e0000ull);   // subsdk0 "movie"
        // ... and what stays off there: zero fields
        CHECK(r180->kart_idle_kick == 0 && !r180->kart_entry[0] && !r180->kart_entry[1]);   // MKWMP_NO_IDLE_KICK refused
        CHECK(!r180->room_sync && !r180->room_main && !r180->room_vote && !r180->room_event);   // MKWMP_TRACE_ROOM
        int ranges = 0;
        for (int i = 0; i < GAME_TRACE_RANGES; i++) {
            CHECK(r180->trace_ranges[i][0] <= r180->trace_ranges[i][1]);
            ranges += r180->trace_ranges[i][1] != 0;
        }
        CHECK(ranges == 6);   // three 1.0.0 ranges have no 1.8.0 counterpart (two merged, one unresolved, one gone)
        // the friend manager (nxfriends.c): 1.8.0 only
        CHECK(!r100->fr_tick && !r100->fr_vtables[0] && !game_guard_friends(r100) && !game_guard_friends(NULL));
        CHECK(r180->fr_tick == 0x70005faef0ull && r180->fr_users_copy == 0x70012f4c10ull && r180->fr_slot_tick == 11);
        CHECK(r180->fr_vtables[0] && r180->fr_vtables[1] && r180->fr_vtables[2] && r180->fr_guard[0].at);
        CHECK(r180->fr_user_size == 0x28 && r180->fr_user_account == 0x20 && r180->fr_state == 0x88 && r180->fr_op == 0x28);
        // no 1.0.0 address leaked into the 1.8.0 row
        CHECK(r180->grpc_timedwait_lr != r100->grpc_timedwait_lr && r180->errsvc_post != r100->errsvc_post &&
              r180->game_global != r100->game_global && r180->bulkget_lr != r100->bulkget_lr);
    }

    // ---- guards (any readable memory will do)
    static uint32_t words[4] = {0xd503237f, 0xd10143ff, 0xa9017bfd, 0xf90013f7};
    static uint64_t vtable[12];
    vtable[7] = 0x70021b22ecull;
    CHECK(game_guard_words("test", (uint64_t)(uintptr_t)words, words, 4));
    uint32_t other[4] = {0xd503237f, 0xd10143ff, 0xa9017bfd, 0xf90013f8};
    CHECK(!game_guard_words("test (expected mismatch)", (uint64_t)(uintptr_t)words, other, 4));
    CHECK(!game_guard_words("test (expected unreadable)", 0x10, words, 1));
    CHECK(!game_guard_words("test (expected unreadable)", 0x6ff0000000ull, words, 1));
    CHECK(game_guard_slot("test", (uint64_t)(uintptr_t)vtable, 7, 0x70021b22ecull));
    CHECK(!game_guard_slot("test (expected mismatch)", (uint64_t)(uintptr_t)vtable, 11, 0x70021b384cull));
    CHECK(!game_guard_slot("test (expected unreadable)", 0x6ff0000000ull, 7, 1));

    // ---- game_build() -> row
    if (!strcmp(mode, "none")) {
        // main is not mapped: unknown for now, nothing cached, no row
        CHECK(game_build() == GAME_UNKNOWN && game_addrs() == NULL);
        // ... and once it is mapped, the next call decides
        map_main(0x02bb3060u, 0x346ba64u, kId180);
        CHECK(game_build() == GAME_180 && game_addrs() == r180);
    } else if (!strcmp(mode, "100")) {
        map_main(0x02830054u, 0x30a7e78u, kId100);
        CHECK(game_build() == GAME_100 && game_addrs() == r100);
        // the 1.0.0 guards against a faked image
        put32(r100->grpc_timedwait_lr - 4, r100->grpc_timedwait_bl);
        CHECK(game_guard_words("gRPC", r100->grpc_timedwait_lr - 4, &r100->grpc_timedwait_bl, 1));
        put64(r100->errsvc_vtable + 6 * 8, r100->errsvc_post);
        CHECK(game_guard_slot("errsvc", r100->errsvc_vtable, 6, r100->errsvc_post));
        CHECK(!game_guard_slot("errsvc (expected mismatch)", r100->errsvc_vtable, 6, r180->errsvc_post));
        CHECK(game_addrs_selfcheck() == 1);   // the error service post's first words are not faked here
    } else if (!strcmp(mode, "180")) {
        map_main(0x02bb3060u, 0x346ba64u, kId180);
        CHECK(game_build() == GAME_180 && game_addrs() == r180);
        CHECK(game_build() == GAME_180);   // cached
        put64(r180->errsvc_vtable + 6 * 8, r180->errsvc_post);
        CHECK(game_guard_slot("errsvc", r180->errsvc_vtable, 6, r180->errsvc_post));
        CHECK(!game_guard_slot("errsvc (expected mismatch)", r180->errsvc_vtable, 6, r100->errsvc_post));
        // the whole self-check on a faked 1.8.0 image: every guard value in place
        put32(r180->grpc_timedwait_lr - 4, r180->grpc_timedwait_bl);
        for (int i = 0; i < 2; i++) put32(r180->errsvc_post + 4u * (unsigned)i, r180->errsvc_words[i]);
        CHECK(game_addrs_selfcheck() == 0);
        put32(r180->errsvc_post + 4, 0xd503201f);   // a patched image: one guard fails
        CHECK(game_addrs_selfcheck() == 1);
        // the friend manager's guards: nothing of it in place, then all of it, then one word off, then one slot off
        CHECK(!game_guard_friends(r180));
        for (int i = 0; i < 3; i++) {
            put64(r180->fr_vtables[i] + 8u * r180->fr_slot_changed, r180->fr_changed);
            put64(r180->fr_vtables[i] + 8u * r180->fr_slot_tick, r180->fr_tick);
            put64(r180->fr_vtables[i] + 8u * r180->fr_slot_reload, r180->fr_reload);
            put64(r180->fr_vtables[i] + 8u * r180->fr_slot_remap, r180->fr_remap);
        }
        int guards = 0;
        for (; guards < 24 && r180->fr_guard[guards].at; guards++) put32(r180->fr_guard[guards].at, r180->fr_guard[guards].word);
        CHECK(guards == 22 && game_guard_friends(r180));
        put32(r180->fr_guard[19].at, 0xd503201f);
        CHECK(!game_guard_friends(r180));
        put32(r180->fr_guard[19].at, r180->fr_guard[19].word);
        put64(r180->fr_vtables[2] + 8u * r180->fr_slot_tick, r180->fr_reload);
        CHECK(!game_guard_friends(r180));
    } else if (!strcmp(mode, "other")) {
        map_main(0x02c00000u, 0x346ba64u, kId180);   // a MOD0 offset of no known build
        CHECK(game_build() == GAME_OTHER && game_addrs() == NULL);
        CHECK(game_addrs_selfcheck() == -1);
    } else if (!strcmp(mode, "badid")) {
        map_main(0x02bb3060u, 0x346ba64u, kId100);   // 1.8.0's MOD0 offset with another build id
        CHECK(game_build() == GAME_OTHER && game_addrs() == NULL);
    } else {
        printf("usage: test_gameaddrs [none|100|180|other|badid]\n");
        return 2;
    }
    printf("test_gameaddrs %s: %s\n", mode, failures ? "FAIL" : "PASS");
    return failures != 0;
}
