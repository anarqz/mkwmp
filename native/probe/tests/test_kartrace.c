// MKWMP_TRACE_KART race-progress trace (kartrace.h): the readability cache, decoding a 1.8.0 kart controller and
// what hangs off it (LapChecker and course scale, player table, kart lookup tree, position table) from fake guest
// memory laid out with the 1.8.0 row's offsets, the watchdog's rules, change events and the log line.
//
//   $zig = "C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"; $s = "native\probe\src"
//   & $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o scratch\test_kartrace.exe `
//       native\probe\tests\test_kartrace.c native\probe\tests\stub_coff.c $s\kartrace.c $s\gameaddrs.c `
//       $s\guesthook.c $s\log.c $s\plat_win.c
//   scratch\test_kartrace.exe
#include "../src/gameaddrs.h"
#include "../src/guesthook.h"
#include "../src/kartrace.h"

#include <stdio.h>
#include <string.h>

RtApi g_rt;

static int failures;
#define CHECK(c) do { if (!(c)) { printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #c); failures++; } } while (0)

static _Alignas(16) uint8_t arena[1 << 16];
static size_t used;
static int calls;
static uint8_t *take(size_t n) { uint8_t *p = arena + used; used += (n + 15) & ~(size_t)15; return p; }
static size_t readable(const void *p) {
    calls++;
    const uint8_t *b = p;
    return b >= arena && b < arena + sizeof arena ? (size_t)(arena + sizeof arena - b) : 0;
}
static void put64(uint8_t *at, const void *v) { uint64_t x = (uint64_t)(uintptr_t)v; memcpy(at, &x, 8); }
static void put32(uint8_t *at, uint32_t v) { memcpy(at, &v, 4); }
static void putf(uint8_t *at, float v) { memcpy(at, &v, 4); }

struct world {
    uint8_t *ctrl, *info, *lc, *path_g, *ptable_g, *pos_g, *entry, *hdr, *kart, *kobj, *posref, *map, *node;
};

// The 1.8.0 structures, by the row's offsets
static void build(const struct kart_layout *L, struct world *w) {
    memset(arena, 0, sizeof arena);
    used = 0;
    w->ctrl = take(L->c_size);
    w->info = take(0x40);
    put64(w->ctrl + L->c_info, w->info);
    put32(w->info + L->i_player, 3);
    put32(w->ctrl + L->c_state, 5);
    put32(w->ctrl + L->c_frames, 1234);
    put32(w->ctrl + L->w_error, KART_ID_BEHIND);
    putf(w->ctrl + L->w_xy, 10.5f);
    putf(w->ctrl + L->w_xy + 4, -20.25f);
    putf(w->ctrl + L->w_target, 175.0f);
    putf(w->ctrl + L->w_best, 100.0f);
    put32(w->ctrl + L->w_moves, 0);
    // LapChecker, two course paths, the second selected; scale 2
    w->lc = take(L->l_path + 4u);
    put64(w->ctrl + L->c_lap, w->lc);
    put32(w->lc + L->l_flags, 0x2420);
    putf(w->lc + L->l_p, 50.0f);
    putf(w->lc + L->l_best, 50.0f);
    w->lc[L->l_lap] = 1;
    w->lc[L->l_lap + 1] = 1;
    int16_t cp = 4;
    memcpy(w->lc + L->l_cp, &cp, 2);
    put32(w->lc + L->l_path, 1);
    uint8_t *paths = take(0x80), *path0 = take(0x70), *path1 = take(0x70);
    put64(paths + L->g_paths, path0);
    put64(paths + L->g_paths + 8, path1);
    putf(path0 + L->p_scale, 7.0f);
    putf(path1 + L->p_scale, 2.0f);
    w->path_g = take(8);
    put64(w->path_g, paths);
    // player table: G -> +0xc0 -> +8 -> [0] -> +8 -> [0] = header (0xd92920); 4 entries, entry 3 local. The decoy
    // one load short of the header holds what a reader stopping there would take for a table.
    uint8_t *g = take(0xc8), *a = take(0x10), *b = take(8), *c = take(0x10), *d = take(0x20);
    w->hdr = take(0x20);
    uint8_t *items = take(4u * L->t_size), *decoy = take(4u * L->t_size);
    w->ptable_g = take(8);
    put64(w->ptable_g, g);
    put64(g + 0xc0, a);
    put64(a + 8, b);
    put64(b, c);
    put64(c + 8, d);
    put64(d, w->hdr);
    put32(d + L->t_count, 4);
    put64(d + L->t_items, decoy);
    put32(decoy + 3u * L->t_size + L->t_local, 26);
    put32(w->hdr + L->t_count, 4);
    put64(w->hdr + L->t_items, items);
    put32(w->hdr + L->t_mode, 1);
    w->entry = items + 3u * L->t_size;
    put32(w->entry + L->t_local, 0);
    put32(w->entry + L->t_a, 3);
    put32(w->entry + L->t_b, 3);
    w->entry[L->t_flags] = 1;
    // kart: obj +0x248 -> +0xc8 = map; tree root key 5 -> left child key 0 -> handle -> holder -> actor -> kart
    uint8_t *obj = take(0x250), *comp = take(0xd0), *root = take(0x40), *holder = take(0x90), *actor = take(0x250),
            *ac = take(0x108), *ac2 = take(0x50);
    w->map = take(0x220);
    w->node = take(0x40);
    w->kart = take(0x5c0);
    w->kobj = take(0x198);
    w->posref = take(0x20);
    put64(w->ctrl + L->c_obj, obj);
    put64(obj + L->o_comp, comp);
    put64(comp + L->o_map, w->map);
    put64(w->map + L->m_n1, root);
    put32(w->map + L->m_n2, 1);
    put64(w->map + L->m_root, root);
    put32(root + L->n_key, 5);
    put64(root, w->node);               // key 5 > 0: left
    put32(w->node + L->n_key, 0);
    put64(w->node + L->n_handle + L->h_ptr, holder);
    put32(w->node + L->n_handle + L->h_gen, 9);
    put32(holder + L->a_gen, 9);
    put64(holder + L->a_actor, actor);
    put32(actor + L->a_state, 3);
    put64(actor + L->a_comp, ac);
    put64(ac + L->a_comp2, ac2);
    put64(ac2 + L->a_kart, w->kart);
    put64(w->kart + L->k_obj, w->kobj);
    put32(w->kart + L->k_idle, 12);
    put64(w->kart + L->k_posref, w->posref);
    put32(w->posref + L->r_idx, 2);
    put32(w->kart + L->k_btn, 0x41);
    put32(w->kart + L->k_mode, 3);
    putf(w->kart + L->k_stick, 0.5f);
    putf(w->kart + L->k_stick + 4, -0.25f);
    // pad table: 3 pads, pad 2's left stick (1,2), held 0x8, idle 7, connected; the default pad's stick (-1,-1).
    // Pad 0 is unreadable (outside the arena) and pad 1 is null.
    uint8_t *t = take(0xc8), *list = take(24), *e2 = take(L->d_size), *def = take(L->d_size);
    w->pos_g = take(8);
    put64(w->pos_g, t);
    put32(t + L->q_count, 3);
    put64(t + L->q_items, list);
    put64(list, (void *)(uintptr_t)0x100);
    put64(list + 16, e2);
    put64(t + L->q_default, def);
    put64(e2, (void *)(uintptr_t)0x7003300000ull);
    putf(e2 + L->q_pos, 1.0f);
    putf(e2 + L->q_pos + 4, 2.0f);
    put32(e2 + L->d_held, 8);
    putf(e2 + L->d_idle, 7.0f);
    put64(e2 + L->d_src, e2);
    e2[L->d_on] = 1;
    e2[L->d_conn] = 1;
    putf(def + L->q_pos, -1.0f);
    putf(def + L->q_pos + 4, -1.0f);
}

static int rd(const struct kart_layout *L, struct world *w, struct kart_rd *r, int full, struct kart_snap *s) {
    return kart_read(L, (uint64_t)(uintptr_t)w->path_g, (uint64_t)(uintptr_t)w->ptable_g,
                     (uint64_t)(uintptr_t)w->pos_g, r, (uint64_t)(uintptr_t)w->ctrl, full, 100000, s);
}

static void test_cache(void) {
    struct kart_rd r = {.fn = readable};
    calls = 0;
    CHECK(kart_rd_ok(&r, arena + 100, 8, 1000));
    CHECK(kart_rd_ok(&r, arena + 5000, 64, 1500));   // inside the range found first
    CHECK(calls == 1);
    CHECK(kart_rd_ok(&r, arena + 5000, 64, 1000 + KART_RD_MS));   // expired: asked again
    CHECK(calls == 2);
    uint8_t outside[16];
    CHECK(!kart_rd_ok(&r, outside, 8, 1000));
    CHECK(!kart_rd_ok(&r, (void *)(uintptr_t)0x100, 8, 1000));   // low addresses: never
    CHECK(!kart_rd_ok(&r, arena + sizeof arena - 4, 8, 1000));   // runs past the end
}

static void test_read(const struct kart_layout *L) {
    struct world w;
    build(L, &w);
    struct kart_rd r = {.fn = readable};
    struct kart_snap s;
    CHECK(rd(L, &w, &r, 1, &s));
    CHECK(s.player == 3 && s.state == 5 && s.frames == 1234 && s.error == KART_ID_BEHIND);
    CHECK(s.xy[0] == 10.5f && s.xy[1] == -20.25f && s.target == 175.0f && s.tbest == 100.0f);
    CHECK(s.lap_ok && s.lflags == 0x2420 && s.lap == 1 && s.maxlap == 1 && s.cp == 4);
    CHECK(s.scale == 2.0f && s.p == 100.0f && s.best == 100.0f);   // the second path's scale
    CHECK(s.tbl_ok && s.t_local == 0 && s.t_a == 3 && s.t_b == 3 && s.t_flags == 1 && s.mode == 1);
    CHECK(s.kart_ok && s.kstate == 3 && s.idle == 12 && s.pidx == 2 && s.pos[0] == 1.0f && s.pos[1] == 2.0f);
    CHECK(s.kbtn == 0x41 && s.kmode == 3 && s.kstick[0] == 0.5f && s.kstick[1] == -0.25f);
    CHECK(s.pad_ok && s.pheld == 8 && s.pidle == 7.0f && s.pon == 1 && s.pconn == 1 && s.psrc == 1);
    CHECK(kart_watched(&s));
    CHECK(kart_rules(&s) == 0);
    char pads[600];
    CHECK(kart_pads(L, (uint64_t)(uintptr_t)w.pos_g, &r, 100000, 8, pads, sizeof pads) > 0);
    CHECK(strstr(pads, "n 3 | def @0 st -1.00,-1.00") != NULL);
    CHECK(strstr(pads, " | 0 ? | 1 ? | 2 @0x3300000 st 1.00,2.00 r 0.00,0.00 held 0x8 idle 7 on 1 conn 1 src 1") != NULL);
    printf("example: kart pads: %s\n", pads);
    CHECK(kart_pads(L, 0, &r, 100000, 8, pads, sizeof pads) == 0);
    CHECK(kart_pads(L, (uint64_t)(uintptr_t)w.pos_g, &r, 100000, 8, pads, 10) == 9);   // truncated, terminated

    // the cheap read: the controller only
    struct kart_snap q;
    CHECK(rd(L, &w, &r, 0, &q));
    CHECK(q.player == 3 && q.error == KART_ID_BEHIND && !q.lap_ok && !q.tbl_ok && !q.kart_ok);

    // a position index past the table: the default entry, as the game reads it
    put32(w.posref + L->r_idx, 7);
    CHECK(rd(L, &w, &r, 1, &s) && s.kart_ok && s.pidx == 7 && s.pos[0] == -1.0f);
    // a stale handle generation: no kart
    put32(w.node + L->n_handle + L->h_gen, 8);
    CHECK(rd(L, &w, &r, 1, &s) && !s.kart_ok && !kart_watched(&s));
    put32(w.node + L->n_handle + L->h_gen, 9);
    // remote (no local index) and battle modes are not watched
    put32(w.entry + L->t_local, (uint32_t)-1);
    CHECK(rd(L, &w, &r, 1, &s) && !kart_watched(&s));
    char line[640];
    kart_format(&s, 60, line, sizeof line);
    CHECK(strstr(line, "p3 remote st5 tps 60") != NULL);
    put32(w.entry + L->t_local, 0);
    put32(w.hdr + L->t_mode, 6);
    CHECK(rd(L, &w, &r, 1, &s) && !kart_watched(&s));
    put32(w.hdr + L->t_mode, 1);
    // an unreadable LapChecker: the rest still decodes
    uint8_t outside[8];
    put64(w.ctrl + L->c_lap, outside);
    CHECK(rd(L, &w, &r, 1, &s) && !s.lap_ok && s.tbl_ok && s.kart_ok);
    // an unreadable controller: nothing
    CHECK(!kart_read(L, 0, 0, 0, &r, (uint64_t)(uintptr_t)outside, 1, 100000, &s));
}

static void test_rules(const struct kart_layout *L) {
    struct world w;
    build(L, &w);
    struct kart_rd r = {.fn = readable};
    struct kart_snap s;
    put32(w.ctrl + L->w_slow, KART_SLOW_MAX + 1);                 // rule 3
    putf(w.ctrl + L->w_best, 106.0f);                             // best 106 - p 100 > 5: rule 5
    put32(w.ctrl + L->w_nomove, KART_NOMOVE_MAX + 1);             // rule 1
    put32(w.kart + L->k_idle, KART_IDLE_MAX);                     // rule 2
    put32(w.ctrl + L->w_error, KART_ID_STALL);
    put32(w.ctrl + L->w_strikes, 37);
    CHECK(rd(L, &w, &r, 1, &s));
    CHECK(kart_rules(&s) == (1 | 2 | 4 | 16));
    char line[640];
    kart_format(&s, 48, line, sizeof line);
    CHECK(strstr(line, "p3 LOCAL st5 tps 48 f 1234") != NULL);
    CHECK(strstr(line, "prog 100.00 tgt 175.00 best 100.00/106.00 lap 1/1 cp 4") != NULL);
    CHECK(strstr(line, "id 0x5382e895 strikes 37") != NULL);
    CHECK(strstr(line, "rules 1,2,3,5 wd on") != NULL);
    CHECK(strstr(line, "xy 10.50,-20.25 pos 1.00,2.00 pi 2") != NULL);
    CHECK(strstr(line, " | pad held 0x8 idle 7 r 0.00,0.00 on1 conn1 src1 | kin m3 b0x41 s 0.50,-0.25") != NULL);
    printf("example: kart: %s\n", line);
    w.kobj[L->ko_flags] = 0x80;                                   // bit 7: input ignored, no rule 2
    put32(w.ctrl + L->w_grace, 1);                                // recovery grace: no rule 5
    w.lc[L->l_flags] |= 1;                                        // wrong way: rule 4
    CHECK(rd(L, &w, &r, 1, &s) && kart_rules(&s) == (1 | 4 | 8));
    w.kart[L->k_flags] = 0x20;                                    // kart flag bit 5: rules 1 and 2 skipped
    CHECK(rd(L, &w, &r, 1, &s) && kart_rules(&s) == (4 | 8));
    w.kart[L->k_flags] = 0;
    w.ctrl[L->c_skip] = 1;                                        // ctrl+0x21f: rules 3-5 off
    CHECK(rd(L, &w, &r, 1, &s) && kart_rules(&s) == 1);
    w.ctrl[L->c_skip] = 0;
    w.kobj[L->ko_flags] = 0x88;                                   // kart object +0x191 bit 3: the same
    CHECK(rd(L, &w, &r, 1, &s) && kart_rules(&s) == 1 && kart_watched(&s));
    w.ctrl[L->c_off] = 1;                                         // ctrl+0x222 bit 0: no rule runs
    CHECK(rd(L, &w, &r, 1, &s) && !kart_watched(&s));
    kart_format(&s, 60, line, sizeof line);
    CHECK(strstr(line, "g1/0 o0x88") != NULL);
}

static void test_events(void) {
    struct kart_snap a, b;
    memset(&a, 0, sizeof a);
    a.state = 5;
    a.error = KART_ID_BEHIND;
    b = a;
    CHECK(kart_events(&a, &b) == 0);
    b.strikes = 1;
    b.error = KART_ID_STALL;
    CHECK(kart_events(&a, &b) == (KART_EV_STRIKE | KART_EV_ID));
    a = b;
    a.strikes = KART_STRIKES_MAX - 1;
    b.strikes = KART_STRIKES_MAX;
    CHECK(kart_events(&a, &b) == KART_EV_MAXED);
    a = b;
    b.strikes = 0;
    b.rescued = 1;
    CHECK(kart_events(&a, &b) == (KART_EV_CLEAR | KART_EV_RESCUE));
    a = b;
    b.state = 9;
    a.lap_ok = b.lap_ok = 1;
    a.lap = 0;
    b.lap = 1;
    CHECK(kart_events(&a, &b) == (KART_EV_STATE | KART_EV_LAP));
    char t[96];
    kart_events_text(KART_EV_STRIKE | KART_EV_RESCUE | KART_EV_LAP, t, sizeof t);
    CHECK(!strcmp(t, "strike-start,rescue,lap"));
    kart_events_text(KART_EV_STATE | KART_EV_ID | KART_EV_MAXED, t, 12);   // truncated, still terminated
    CHECK(strlen(t) == 11);
}

int main(void) {
    const struct game_addrs *a = game_addrs_for(GAME_180);
    CHECK(a && a->kart_dispatch && a->kart_racing && a->kart_post && a->kart_path_g && a->kart_ptable_g &&
          a->kart_pos_g && !a->kart_entry[0] && !a->kart_idle_kick);
    const struct game_addrs *o = game_addrs_for(GAME_100);
    CHECK(o && !o->kart_dispatch);   // 1.0.0 keeps its own kart_entry trace
    if (a) {
        test_cache();
        test_read(&a->kl);
        test_rules(&a->kl);
        test_events();
    }
    printf(failures ? "test_kartrace: %d FAILED\n" : "test_kartrace: ok\n", failures);
    return failures != 0;
}
