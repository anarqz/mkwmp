// MKWMP_TRACE_KART on 1.8.0: per-kart race progress and the anti-stall watchdog's inputs (research
// GAME-1.8.0-PROTOCOL.md §10). Pure logic: decoding a kart controller from guest memory through a readability cache,
// the watchdog's rules as the game evaluates them, change detection, the log line. npln.c owns the hooks and the log.
#ifndef MKWMP_KARTRACE_H
#define MKWMP_KARTRACE_H

#include "gameaddrs.h"

#include <stddef.h>
#include <stdint.h>

// Readability of guest memory, cached: plat_mem_readable is a VirtualQuery (a /proc/self/maps scan on Linux), too dear
// to call for every field of every kart. A range found readable stays trusted for KART_RD_MS.
enum { KART_RD_SLOTS = 16, KART_RD_MS = 2000 };
typedef size_t (*kart_readable_fn)(const void *p);   // bytes readable from p (0: none)
struct kart_rd {
    kart_readable_fn fn;
    struct { uintptr_t lo, hi; uint64_t at; } slot[KART_RD_SLOTS];
    unsigned next;
};
int kart_rd_ok(struct kart_rd *rd, const void *p, size_t n, uint64_t now_ms);

// The watchdog's constants (immediates in 0x5c1680; all frames at 60 ticks/s)
enum {
    KART_NOMOVE_MAX = 0xe10,   // rule 1: XY still for more than this -> 0x9b928162
    KART_IDLE_MAX = 0x709,     // rule 2: no input for this long (and kart object +0x191 bit 7 clear) -> 0x9b928162
    KART_SLOW_MAX = 0xb4,      // rule 3: progress not past its target for more than this -> 0x5382e895
    KART_STRIKES_MAX = 0x2d0,  // 12 s of strikes: refine the id, then rescue (first time) or state 9 (post)
    KART_REFINE_MIN = 0x259,   // refinement to 0x9b928162: no-move and no-input both >= this
    KART_RESCUE_FRAMES = 0xe0f,
};
#define KART_ID_STALL 0x5382e895u     // rule 3
#define KART_ID_BEHIND 0x660ac5dau    // rules 4, 5 (and the reset value)
#define KART_ID_IDLE 0x9b928162u      // rules 1, 2

struct kart_snap {
    uint64_t ctrl;
    int32_t player;                    // info +0x3c (-1: unreadable)
    uint32_t state, frames;
    // watchdog
    uint32_t error, strikes, nomove, slow, grace, moves, window;
    uint8_t behind, rescued, off, skip;   // off bit 0: no rule runs; skip: rules 3-5 off
    float xy[2], target, tbest;
    // LapChecker (0 flags and lap -2: unreadable); p and best are scaled the way the watchdog compares them
    int lap_ok;
    uint32_t lflags;
    float p, best, scale;
    int8_t lap, maxlap;
    int16_t cp;
    // player table entry (tbl_ok 0: not found)
    int tbl_ok;
    int32_t mode, t_local, t_a, t_b;
    uint8_t t_flags;
    // the kart (kart_ok 0: lookup failed)
    int kart_ok;
    int32_t kstate, idle, pidx;
    uint8_t kobj_flags, kflags;
    float pos[2];
};

// Decode the controller at ctrl. Reads only what rd says is readable; `full` 0 reads the controller's own fields
// (cheap: the per-tick change check), 1 adds the LapChecker, the player table and the kart lookup. 0: ctrl unreadable.
int kart_read(const struct kart_layout *L, uint64_t path_g, uint64_t ptable_g, uint64_t pos_g, struct kart_rd *rd,
              uint64_t ctrl, int full, uint64_t now_ms, struct kart_snap *s);

// The watchdog evaluates its rules for this kart (the gates of 0x5c1680 that kart_read can see: a local player
// whose table entry is valid, kart found and not in states 7..11, not a battle mode)
int kart_watched(const struct kart_snap *s);
// The rules being violated right now, as the game would count them this frame: bit (rule - 1), rules 1..5
unsigned kart_rules(const struct kart_snap *s);
// Changes worth a line of their own between two snapshots of one kart
enum {
    KART_EV_STATE = 1,      // controller state changed
    KART_EV_ID = 2,         // latched id changed
    KART_EV_STRIKE = 4,     // strikes went from 0 to non-zero (first strike of a run)
    KART_EV_CLEAR = 8,      // strikes back to 0
    KART_EV_MAXED = 16,     // strikes reached 0x2d0 (12 s)
    KART_EV_RESCUE = 32,    // rescued flag set (the game's first-time rescue, local event 0xa66067f2)
    KART_EV_LAP = 64,       // lap changed
};
unsigned kart_events(const struct kart_snap *was, const struct kart_snap *now);
// "rescue strike-start ..." for an event mask
size_t kart_events_text(unsigned ev, char *out, size_t n);
// The line body (no prefix): player, local/remote, progress, watchdog state. tps: this kart's updates over the last
// second (the device's race sim rate; 60 = full speed), -1 unknown.
size_t kart_format(const struct kart_snap *s, int tps, char *out, size_t n);

#endif
