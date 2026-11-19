// Every game-version-specific guest address and struct offset of the probe, one row per game build
// (research/docs/GAME-1.8.0-ADDRESSES.md; the values are that report's, §3 functional and §4 diagnostic).
//
// Rules:
//   - game_addrs() is NULL for an unknown build and while main is not mapped: every version-specific hook stays off.
//   - Addresses are absolute guest addresses (main is at 0x7000000000). A zero address in a diagnostic field turns
//     that trace off for the build, so a new game version can ship with only the functional fields filled.
//   - Before a functional hook is installed its guard values are checked against guest memory (game_guard_*): a wrong
//     row or a patched image is logged and the hook is skipped.
#ifndef MKWMP_GAMEADDRS_H
#define MKWMP_GAMEADDRS_H

#include <stdint.h>

enum {
    GAME_ROWS_MAX = 26,       // most name plates on the online pause menu of any build (1.0.0: 24, 1.8.0: 26)
    GAME_PIA_STEPS = 7,
    GAME_TRACE_RANGES = 9,
    GAME_WATCH = 6,
};

struct game_addrs {
    int build;   // GAME_100 / GAME_180 (guesthook.h)

    // ---------------------------------------------------------------- functional
    // npln.c ov_cond_timedwait: return address of gRPC's pthread_cond_timedwait call (its deadline is converted to
    // the monotonic clock). Guard: the instruction before it is this BL.
    uint64_t grpc_timedwait_lr;
    uint32_t grpc_timedwait_bl;
    // npln.c sdk_monotonic_repair, both in the sdk module: the slot clock_gettime reads its CLOCK_MONOTONIC source
    // from, and the SDK's default source (nn::os tick based) to put back into it. 0: the build's SDK clock needs no
    // repair. Guard: the default source (the slot holds nn::time::GetStandardSteadyClockCurrentTimespec when it fails)
    // starts with these words.
    uint64_t sdk_mono_source_slot, sdk_tick_source;
    uint32_t sdk_tick_source_words[8];
    // Error service, vtable slot 6: post a communication error (x1 = info, id = its first u32). Target of
    // MKWMP_SKIP_ERROR and MKWMP_SKIP_REFEREE_TIMEOUT. Guard: slot 6 of the vtable holds it
    // and the function starts with these words.
    uint64_t errsvc_post, errsvc_vtable;
    uint32_t errsvc_words[2];
    uint32_t err_referee_timeout;   // id dropped by MKWMP_SKIP_REFEREE_TIMEOUT=1
    // voice_ui.c: UIPageOnlinePauseMenu. Guards: the two slots hold fn_pause_open / fn_pause_frame, and
    // fn_plate_text(ctl, char16*) starts with plate_text_words.
    uint64_t vt_online_pause, fn_pause_open, fn_pause_frame, fn_plate_text;
    uint32_t plate_text_words[4];
    uint16_t slot_open, slot_frame;
    uint16_t rows;                  // plates on the page (<= GAME_ROWS_MAX)
    uint16_t page_plates, plate_size, entry_name, entry_me;
    uint32_t page_count, page_entries;
    // A guest module whose calls to the gated SDK overrides always get the runtime's stock handler (npln.c
    // gate_call): 1.8.0's subsdk0 "movie" shares nn::socket::Close/GetLastError/EventFd* with gRPC. 0: none.
    uint64_t stock_lo, stock_hi;
    // The game makes one automatic nifm sign-in at boot (1.0.0: ~7 s in, main+0x2247a7c) that the lazy probe
    // declines; 0 = this build makes none (1.8.0: none in a whole offline session), so the first request is the
    // player opening Online Play.
    uint8_t boot_signin;

    // ---------------------------------------------------------------- diagnostic (0 address = off)
    uint64_t pia_join_step[GAME_PIA_STEPS];         // JoinRandomSessionJob step functions
    uint16_t join_step, join_label, join_error;     // ... and its fields
    uint64_t trace_ranges[GAME_TRACE_RANGES][2];    // MKWMP_TRACE_EDGES caller ranges; {0, 0} = unused
    uint64_t watch[GAME_WATCH];                     // MKWMP_TRACE_EDGES watchpoints, in npln.c W_* order
    uint16_t us_job_dump;                           // W_US_JOB: field dump at x0 + this
    uint16_t mgr_count, mgr_slots, mgr_cur, mgr_slot_size;   // W_START_P2P: session manager slots
    uint64_t game_global;                           // address of the game-state global pointer
    uint16_t g_flags, g_flags2, g_flags3;           // ... its flag words
    // MKWMP_TRACE_ROOM (friend-room race start)
    uint64_t room_sync, room_main, room_vote, room_event;
    uint32_t room_start_event;
    uint16_t g_handle, g_room;                      // global -> session handle, room
    uint16_t sess_states;                           // session: four u32 states
    uint16_t room_play, room_vote_bits, room_has;   // room fields
    uint16_t sync_a, sync_b;                        // SyncState node
    uint16_t main_a, main_b, main_c, main_d;        // PrivateRoomMain node
    uint16_t vote_ptr;                              // ReadyVoteSetter node
    uint16_t room_node_size;
    // MKWMP_TRACE_KART / MKWMP_NO_IDLE_KICK (end-of-battle idle kick). kart_idle_kick: MKWMP_NO_IDLE_KICK may write
    // the move counter (every field it uses is confirmed).
    uint64_t kart_entry[2];
    uint16_t kart_info, kart_player, kart_state, kart_frames, kart_error, kart_strikes, kart_pos, kart_moves, kart_fe;
    uint16_t kart_size;
    uint8_t kart_idle_kick;
    // always-on logs of profile_dispatch
    uint64_t bulkget_lr, bulkget_fn;                // BulkGetDocuments result retrieval: return address, target
    uint64_t ctl_event_sink;                        // online race controller: event sink
    uint64_t ctl_next_lr, ctl_prepare_lr;           // ... return addresses of its next-phase / prepare-operation BLRs
    uint16_t ctl_mode, ctl_phase;                   // ... desired mode / phase
    uint64_t seq_wait, seq_start;                   // matchmake sequence callbacks
    uint16_t seq_phase;
};

// The row of a build (GAME_100, GAME_180); NULL for anything else.
const struct game_addrs *game_addrs_for(int build);
// The row of the running game: game_addrs_for(game_build()). NULL while main is not mapped (ask again later) and for
// an unknown build.
const struct game_addrs *game_addrs(void);

// Guards: 1 when guest memory holds the expected value. A mismatch (or unreadable memory) is logged with `what`.
int game_guard_words(const char *what, uint64_t addr, const uint32_t *words, int n);
int game_guard_slot(const char *what, uint64_t vtable, int slot, uint64_t expect);
// Read-only check of every functional guard of the running build, with one summary line in mkwmp.log (npln.c runs it
// once, at the game's first gated SDK call, so an offline session already shows whether the row fits). Returns the
// number of failed guards, -1 without a row. Only meaningful once main is mapped, and before any vtable is hooked.
int game_addrs_selfcheck(void);

#endif
