#include "gameaddrs.h"

#include "guesthook.h"
#include "log.h"

#include <windows.h>
#include <string.h>

#define M(off) (0x7000000000ull + (uint64_t)(off))   // main-module offset -> guest address

static const struct game_addrs kRows[] = {
    {
        .build = GAME_100,
        // ---- functional
        .grpc_timedwait_lr = M(0x104a8bc), .grpc_timedwait_bl = 0x945f879a,
        .errsvc_post = M(0x222d78c), .errsvc_vtable = M(0x325bc30), .errsvc_words = {0xd503237f, 0xa9bd7bfd},
        .err_referee_timeout = 0x606314c9,
        .proton_skip_error = 0x9b928162,   // end-of-battle idle kick (user decision for 1.0.0, 2026-11-15)
        .vt_online_pause = M(0x3248970), .fn_pause_open = M(0x21b22ec), .fn_pause_frame = M(0x21b384c),
        .fn_plate_text = M(0x21deb68),     // FUN_021deb68(ctl, char16*): writes the plate's two name panes
        .plate_text_words = {0xd503237f, 0xd10143ff, 0xa9017bfd, 0xf90013f7},
        .slot_open = 7, .slot_frame = 11, .rows = 24,
        .page_plates = 0x770, .plate_size = 0x150, .entry_name = 0x18, .entry_me = 0x78,
        .page_count = 0x3d80, .page_entries = 0x3d88,
        .boot_signin = 1,
        // ---- diagnostic
        .pia_join_step = {
            M(0x156015c),   // WaitAutoConnect
            M(0x15602dc), M(0x1560414),
            M(0x1560c10),   // StartMeshStartup
            M(0x1560f28),   // DisconnectNetwork
            M(0x156102c),   // CreateMesh
            M(0x1561798),   // JoinMesh
        },
        .join_step = 0x38, .join_label = 0x48, .join_error = 0xd8,
        .trace_ranges = {
            {M(0x1200000), M(0x1440000)},   // NPLN matchmaking / gamesync
            {M(0x1500000), M(0x1700000)},   // Pia
            {M(0x1a00000), M(0x1b00000)},   // game matchmaking sequence (AIGlobalSeq*)
            {M(0x2230000), M(0x2240000)},   // game online-session manager (drives Pia)
            {M(0x2240000), M(0x2250000)},   // its request dispatcher (FUN_02248cf0)
            {M(0x0495000), M(0x04a0000)},   // game document writer (FUN_0049c9d8)
            {M(0x1e30000), M(0x1e42000)},   // online race controller (PlayerStatic/Companion)
            {M(0x0195000), M(0x0196000)},   // its caller (FUN_01e377c4 call site)
            {M(0x2120000), M(0x2130000)},   // race comm-error raisers (FUN_02126ad4/021271c8)
        },
        .watch = {
            M(0x2236714),   // FUN_02236714: game start-P2P (dispatcher request kind 1)
            M(0x1285d20),   // FUN_01285d20: P2P initial sequence (thread run method)
            M(0x126c3b8),   // FUN_0126c3b8: Gamesync user-session job run (P2P start gate at +0x140)
            M(0x223d768),   // game callback invoked by Pia session layer at matchmaking start
            M(0x16279e4),   // NplnPlugin/session-layer entry (Pia -> plugin)
            M(0x1629020),   // NplnPlugin/session-layer entry (Pia -> plugin)
        },
        .us_job_dump = 0x130,
        // FUN_01a9637c(mgr, -1): slots at +0x12c0 (0x148 bytes each), count +0x12b8, current index +0x12c8
        .mgr_count = 0x12b8, .mgr_slots = 0x12c0, .mgr_cur = 0x12c8, .mgr_slot_size = 0x148,
        .game_global = M(0x33d2548), .g_flags = 0x110, .g_flags2 = 0x114, .g_flags3 = 0x118,
        .room_sync = M(0x1a82d84), .room_main = M(0x1a5a444), .room_vote = M(0x1a60be8), .room_event = M(0x1a5b104),
        .room_start_event = 0x9d424b78,
        .g_handle = 200, .g_room = 0x188, .sess_states = 8 + 0x24,
        .room_play = 0x40, .room_vote_bits = 0x44, .room_has = 5,
        .sync_a = 0x28, .sync_b = 0x2c,
        .main_a = 0x144, .main_b = 0x28, .main_c = 0x38, .main_d = 0x131,
        .vote_ptr = 0x28, .room_node_size = 0x160,
        // FUN_0035ce38: move counter ctrl+0x1e8, frames +0xb8, error id +0x1cc (RACEEND-RE.md)
        .kart_entry = {M(0x20d43dc), M(0x035a9a0)},
        .kart_info = 0x28, .kart_player = 0x3c, .kart_state = 0x40, .kart_frames = 0xb8, .kart_error = 0x1cc,
        .kart_strikes = 0x1d0, .kart_pos = 0x1d4, .kart_moves = 0x1e8, .kart_fe = 0x1fe, .kart_size = 0x200,
        .kart_idle_kick = 1,
        .bulkget_lr = M(0x1e463dc), .bulkget_fn = M(0x146a200),
        .ctl_event_sink = M(0x1e3f91c),
        .ctl_next_lr = M(0x261f894), .ctl_prepare_lr = M(0x261f044), .ctl_mode = 0x4a8, .ctl_phase = 0x4ac,
        .seq_wait = M(0x1a93138), .seq_start = M(0x1a79fa0), .seq_phase = 0x28,
    },
    {
        .build = GAME_180,
        // ---- functional (all "exact" in GAME-1.8.0-ADDRESSES.md §3)
        .grpc_timedwait_lr = M(0x111232c), .grpc_timedwait_bl = 0x946a740e,
        .errsvc_post = M(0x2564cd0), .errsvc_vtable = M(0x3636ad0), .errsvc_words = {0xd503237f, 0xa9be7bfd},
        .err_referee_timeout = 0x606314c9,
        .proton_skip_error = 0,   // off until the error-post row is proven live (GAME-1.8.0-PLAN.md phase 3.5)
        .vt_online_pause = M(0x3622528), .fn_pause_open = M(0x24ca550), .fn_pause_frame = M(0x24cc010),
        .fn_plate_text = M(0x2505f10),
        .plate_text_words = {0xd503237f, 0xd10143ff, 0xa9017bfd, 0xf90013f7},
        .slot_open = 7, .slot_frame = 11, .rows = 26,
        .page_plates = 0x778, .plate_size = 0x170, .entry_name = 0x18, .entry_me = 0x78,
        .page_count = 0x4628, .page_entries = 0x4630,
        .stock_lo = 0x7003800000ull, .stock_hi = 0x7003800000ull + 0xe0000,   // subsdk0 "movie"
        .boot_signin = 0,   // no boot sign-in in a whole offline session (mkwmp.log 2026-11-18, mkw-recomp 0.1.13)
        // ---- diagnostic (§4; "high" or better only)
        .pia_join_step = {M(0x16a9230), M(0x16a9390), M(0x16a94b0), M(0x16a9cf0), M(0x16aa010), M(0x16aa0f0),
                          M(0x16aa800)},
        .join_step = 0x38, .join_label = 0x48, .join_error = 0xe0,
        .trace_ranges = {
            {M(0x12cd000), M(0x1570000)},   // NPLN matchmaking / gamesync
            {M(0x1635000), M(0x186a000)},   // Pia
            {M(0x1c70000), M(0x1d84000)},   // game matchmaking sequence
            {M(0x2567000), M(0x258a000)},   // online-session manager + dispatcher
            {M(0x20eb000), M(0x2103000)},   // online race controller (the document writer 0x20f6020 is inside)
            {M(0x2403000), M(0x2419800)},   // race comm-error raisers
            // unresolved: the caller of FUN_01e377c4 (§5)
        },
        .watch = {M(0x256e900), M(0x1381530), M(0x1367790), M(0x2575d40), M(0x178fab0), M(0x1791710)},
        .us_job_dump = 0x128,
        .mgr_count = 0x12c0, .mgr_slots = 0x12c8, .mgr_cur = 0x12d0, .mgr_slot_size = 0x148,
        .game_global = M(0x37b3858), .g_flags = 0x120, .g_flags2 = 0x124, .g_flags3 = 0x128,
        // MKWMP_TRACE_ROOM: off. The functions are mapped (SyncState 0x1cfb670, PrivateRoomMain 0x1cc8a30,
        // ReadyVoteSetter 0x1cd0b50, event handler 0x1cc9da0) but the fields behind global+200 / +0x188 and the
        // PrivateRoomMain node are not (§4.3, §5).
        .room_start_event = 0x9d424b78,
        // MKWMP_TRACE_KART / MKWMP_NO_IDLE_KICK: off. Entries 0x5c1680 (medium) / 0x5bb220, frames +0xc0, error id
        // +0x1e4, move count +0x20c are mapped; state (+0x40?), strikes, position and +0x1fe are not, and
        // MKWMP_NO_IDLE_KICK writes guest memory, so it stays refused until the state field is confirmed.
        .kart_idle_kick = 0,
        .bulkget_lr = M(0x2107d48), .bulkget_fn = M(0x1593c30),
        .ctl_event_sink = M(0x20ffa60),
        .ctl_next_lr = M(0x297bc60), .ctl_prepare_lr = M(0x297b1c0), .ctl_mode = 0x4c8, .ctl_phase = 0x4cc,
        .seq_wait = M(0x1d0f940), .seq_start = M(0x1ceea90), .seq_phase = 0x28,
    },
};

const struct game_addrs *game_addrs_for(int build) {
    for (size_t i = 0; i < sizeof kRows / sizeof kRows[0]; i++)
        if (kRows[i].build == build) return &kRows[i];
    return NULL;
}

const struct game_addrs *game_addrs(void) { return game_addrs_for(game_build()); }

static int readable(uint64_t addr, size_t n) {
    MEMORY_BASIC_INFORMATION mi;
    if (addr < 0x10000 || !VirtualQuery((const void *)(uintptr_t)addr, &mi, sizeof mi) || mi.State != MEM_COMMIT ||
        (mi.Protect & (PAGE_NOACCESS | PAGE_GUARD)))
        return 0;
    return addr + n <= (uint64_t)(uintptr_t)mi.BaseAddress + mi.RegionSize;
}

int game_guard_words(const char *what, uint64_t addr, const uint32_t *words, int n) {
    if (!readable(addr, 4u * (size_t)n)) {
        mkwmp_log("guard: %s: %#llx is not readable", what, (unsigned long long)addr);
        return 0;
    }
    for (int i = 0; i < n; i++) {
        uint32_t got;
        memcpy(&got, (const void *)(uintptr_t)(addr + 4u * (unsigned)i), 4);
        if (got == words[i]) continue;
        mkwmp_log("guard: %s: word %d at %#llx is %08x, expected %08x", what, i, (unsigned long long)addr, got,
                  words[i]);
        return 0;
    }
    return 1;
}

int game_addrs_selfcheck(void) {
    int build = game_build();
    const struct game_addrs *a = game_addrs_for(build);
    if (!a) {
        mkwmp_log("game build %s: no address table row, version-specific hooks stay off (voice menu, "
                  "communication-error log and skips, traces, gRPC clock fix)", game_build_name(build));
        return -1;
    }
    int bad = 0;
    bad += !game_guard_words("gRPC timed wait call", a->grpc_timedwait_lr - 4, &a->grpc_timedwait_bl, 1);
    bad += !game_guard_slot("error service post", a->errsvc_vtable, 6, a->errsvc_post);
    bad += !game_guard_words("error service post", a->errsvc_post, a->errsvc_words, 2);
    bad += !game_guard_slot("voice-ui open", a->vt_online_pause, a->slot_open, a->fn_pause_open);
    bad += !game_guard_slot("voice-ui frame", a->vt_online_pause, a->slot_frame, a->fn_pause_frame);
    bad += !game_guard_words("voice-ui plate text", a->fn_plate_text, a->plate_text_words, 4);
    if (bad) mkwmp_log("game build %s: address table row found, %d of 6 guards FAILED (those hooks stay off)",
                       game_build_name(build), bad);
    else mkwmp_log("game build %s: address table row found, 6 of 6 guards ok (hooks go in when Online Play opens)",
                   game_build_name(build));
    return bad;
}

int game_guard_slot(const char *what, uint64_t vtable, int slot, uint64_t expect) {
    uint64_t at = vtable + 8ull * (uint64_t)slot, got;
    if (!readable(at, 8)) {
        mkwmp_log("guard: %s: vtable %#llx slot %d is not readable", what, (unsigned long long)vtable, slot);
        return 0;
    }
    memcpy(&got, (const void *)(uintptr_t)at, 8);
    if (got == expect) return 1;
    mkwmp_log("guard: %s: vtable %#llx slot %d holds %#llx, expected %#llx", what, (unsigned long long)vtable, slot,
              (unsigned long long)got, (unsigned long long)expect);
    return 0;
}
