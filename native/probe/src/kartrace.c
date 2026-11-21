#include "kartrace.h"

#include <stdio.h>
#include <string.h>

int kart_rd_ok(struct kart_rd *rd, const void *p, size_t n, uint64_t now_ms) {
    uintptr_t a = (uintptr_t)p;
    if (a < 0x10000 || a + n < a) return 0;
    for (unsigned i = 0; i < KART_RD_SLOTS; i++) {
        if (!rd->slot[i].hi || now_ms - rd->slot[i].at >= KART_RD_MS) continue;
        if (a >= rd->slot[i].lo && a + n <= rd->slot[i].hi) return 1;
    }
    size_t m = rd->fn ? rd->fn(p) : 0;
    if (m < n) return 0;
    unsigned k = rd->next++ % KART_RD_SLOTS;
    rd->slot[k].lo = a;
    rd->slot[k].hi = a + m;
    rd->slot[k].at = now_ms;
    return 1;
}

struct rd_ctx { struct kart_rd *rd; uint64_t now; int ok; };

static void get(struct rd_ctx *x, uint64_t addr, void *dst, size_t n) {
    if (x->ok && addr && kart_rd_ok(x->rd, (const void *)(uintptr_t)addr, n, x->now)) {
        memcpy(dst, (const void *)(uintptr_t)addr, n);
        return;
    }
    x->ok = 0;
    memset(dst, 0, n);
}
static uint64_t ptr(struct rd_ctx *x, uint64_t addr) { uint64_t v; get(x, addr, &v, 8); return v; }
static int32_t i32(struct rd_ctx *x, uint64_t addr) { int32_t v; get(x, addr, &v, 4); return v; }
static float f32(struct rd_ctx *x, uint64_t addr) { float v; get(x, addr, &v, 4); return v; }
static uint8_t u8(struct rd_ctx *x, uint64_t addr) { uint8_t v; get(x, addr, &v, 1); return v; }

int kart_read(const struct kart_layout *L, uint64_t path_g, uint64_t ptable_g, uint64_t pos_g, struct kart_rd *rd,
              uint64_t ctrl, int full, uint64_t now_ms, struct kart_snap *s) {
    memset(s, 0, sizeof *s);
    s->ctrl = ctrl;
    s->player = -1;
    s->lap = s->maxlap = -2;
    s->pidx = -1;
    if (!kart_rd_ok(rd, (const void *)(uintptr_t)ctrl, L->c_size, now_ms)) return 0;
    const uint8_t *c = (const uint8_t *)(uintptr_t)ctrl;
    memcpy(&s->state, c + L->c_state, 4);
    memcpy(&s->frames, c + L->c_frames, 4);
    memcpy(&s->error, c + L->w_error, 4);
    memcpy(&s->strikes, c + L->w_strikes, 4);
    memcpy(s->xy, c + L->w_xy, 8);
    memcpy(&s->nomove, c + L->w_nomove, 4);
    memcpy(&s->target, c + L->w_target, 4);
    memcpy(&s->slow, c + L->w_slow, 4);
    s->behind = c[L->w_behind];
    memcpy(&s->grace, c + L->w_grace, 4);
    memcpy(&s->tbest, c + L->w_best, 4);
    memcpy(&s->moves, c + L->w_moves, 4);
    s->rescued = c[L->w_rescued];
    memcpy(&s->window, c + L->w_window, 4);
    s->off = c[L->c_off];
    s->skip = c[L->c_skip];
    struct rd_ctx x = {rd, now_ms, 1};
    uint64_t info;
    memcpy(&info, c + L->c_info, 8);
    int32_t player = i32(&x, info + L->i_player);
    if (x.ok) s->player = player;
    if (!full) return 1;

    // LapChecker, and the course path's scale (0x298da00: path = *(global + g_paths + 8 * (selector == 1)))
    uint64_t lc;
    memcpy(&lc, c + L->c_lap, 8);
    x.ok = 1;
    uint32_t lflags = (uint32_t)i32(&x, lc + L->l_flags);
    float praw = f32(&x, lc + L->l_p), braw = f32(&x, lc + L->l_best);
    int8_t lap = (int8_t)u8(&x, lc + L->l_lap), maxlap = (int8_t)u8(&x, lc + L->l_lap + 1u);
    int16_t cp;
    get(&x, lc + L->l_cp, &cp, 2);
    int32_t sel = i32(&x, lc + L->l_path);
    if (x.ok) {
        s->lap_ok = 1;
        s->lflags = lflags;
        s->lap = lap;
        s->maxlap = maxlap;
        s->cp = cp;
        uint64_t paths = ptr(&x, path_g);
        uint64_t path = ptr(&x, paths + L->g_paths + (sel == 1 ? 8u : 0u));
        float scale = f32(&x, path + L->p_scale);
        s->scale = x.ok ? scale : 0.0f;
        s->p = s->scale ? praw * s->scale : praw;
        s->best = s->scale ? braw * s->scale : braw;
    }

    // Player table (0xd92920: **(*(*G + 0xc0) + 8) + 8; an out-of-range index reads entry 0, as the game does)
    if (s->player >= 0) {
        x.ok = 1;
        uint64_t hdr = ptr(&x, ptr(&x, ptr(&x, ptr(&x, ptr(&x, ptable_g) + 0xc0) + 8)) + 8);
        int32_t mode = i32(&x, hdr + L->t_mode);
        uint32_t count = (uint32_t)i32(&x, hdr + L->t_count);
        uint64_t items = ptr(&x, hdr + L->t_items);
        uint64_t e = items + (uint64_t)((uint32_t)s->player < count ? (uint32_t)s->player : 0u) * L->t_size;
        int32_t tl = i32(&x, e + L->t_local), ta = i32(&x, e + L->t_a), tb = i32(&x, e + L->t_b);
        uint8_t tf = u8(&x, e + L->t_flags);
        if (x.ok && items) {
            s->tbl_ok = 1;
            s->mode = mode;
            s->t_local = tl;
            s->t_a = ta;
            s->t_b = tb;
            s->t_flags = tf;
        }
    }

    // The kart (0x9d5ce0: tree of handles keyed by int, left +0, right +8; then the generation check)
    x.ok = 1;
    uint64_t obj;
    memcpy(&obj, c + L->c_obj, 8);
    uint64_t map = ptr(&x, ptr(&x, obj + L->o_comp) + L->o_map);
    uint64_t n1 = ptr(&x, map + L->m_n1);
    int32_t n2 = i32(&x, map + L->m_n2);
    uint64_t node = x.ok && n1 && n2 ? ptr(&x, map + L->m_root) : 0;
    for (int depth = 0; node && x.ok && depth < 64; depth++) {
        int32_t key = i32(&x, node + L->n_key);
        if (key == 0) break;
        node = ptr(&x, node + (key > 0 ? 0u : 8u));
    }
    if (node && x.ok) {
        uint64_t h = node + L->n_handle;
        int32_t gen = i32(&x, h + L->h_gen);
        uint64_t holder = ptr(&x, h + L->h_ptr);
        if (x.ok && gen != -1 && holder && i32(&x, holder + L->a_gen) == gen && x.ok) {
            uint64_t actor = ptr(&x, holder + L->a_actor);
            int32_t kstate = i32(&x, actor + L->a_state);
            uint64_t kart = ptr(&x, ptr(&x, ptr(&x, actor + L->a_comp) + L->a_comp2) + L->a_kart);
            uint64_t kobj = ptr(&x, kart + L->k_obj);
            int32_t idle = i32(&x, kart + L->k_idle);
            uint8_t kflags = u8(&x, kart + L->k_flags), oflags = u8(&x, kobj + L->ko_flags);
            int32_t pidx = i32(&x, ptr(&x, kart + L->k_posref) + L->r_idx);
            if (x.ok) {
                s->kart_ok = 1;
                s->kstate = kstate;
                s->idle = idle;
                s->kflags = kflags;
                s->kobj_flags = oflags;
                s->pidx = pidx;
                // 0x2528bc0: an index out of range reads the table's default entry
                uint64_t t = ptr(&x, pos_g);
                int32_t n = i32(&x, t + L->q_count);
                uint64_t e = pidx >= 0 && pidx < n ? ptr(&x, ptr(&x, t + L->q_items) + 8u * (uint64_t)pidx)
                                                   : ptr(&x, t + L->q_default);
                float pos[2] = {f32(&x, e + L->q_pos), f32(&x, e + L->q_pos + 4u)};
                if (x.ok) memcpy(s->pos, pos, sizeof pos);
            }
        }
    }
    return 1;
}

int kart_watched(const struct kart_snap *s) {
    return !(s->off & 1) && s->tbl_ok && s->t_local >= 0 && s->t_a >= 0 && (s->t_flags & 1) && s->t_b >= 0 &&
           s->kart_ok && !(s->kstate >= 7 && s->kstate <= 11) && !(s->mode >= 5 && s->mode <= 7);
}

unsigned kart_rules(const struct kart_snap *s) {
    unsigned r = 0;
    if (s->kart_ok && !(s->kflags & 0x20)) {
        if (s->nomove > KART_NOMOVE_MAX) r |= 1;
        if (!(s->kobj_flags & 0x80) && s->idle >= KART_IDLE_MAX) r |= 2;
    }
    if (s->skip || (s->kart_ok && (s->kobj_flags & 8))) return r;   // rules 3-5 off for this kart
    if (s->slow > KART_SLOW_MAX) r |= 4;
    if (s->lap_ok && (s->lflags & 1)) r |= 8;
    if (s->lap_ok && s->tbest - s->p > 5.0f && s->grace == 0) r |= 16;
    return r;
}

unsigned kart_events(const struct kart_snap *was, const struct kart_snap *now) {
    unsigned ev = 0;
    if (was->state != now->state) ev |= KART_EV_STATE;
    if (was->error != now->error) ev |= KART_EV_ID;
    if (!was->strikes && now->strikes) ev |= KART_EV_STRIKE;
    if (was->strikes && !now->strikes) ev |= KART_EV_CLEAR;
    if (was->strikes < KART_STRIKES_MAX && now->strikes >= KART_STRIKES_MAX) ev |= KART_EV_MAXED;
    if (!was->rescued && now->rescued) ev |= KART_EV_RESCUE;
    if (was->lap_ok && now->lap_ok && was->lap != now->lap) ev |= KART_EV_LAP;
    return ev;
}

size_t kart_events_text(unsigned ev, char *out, size_t n) {
    static const char *const kName[] = {"state", "id", "strike-start", "strikes-clear", "strikes-max(12s)",
                                        "rescue", "lap"};
    size_t k = 0;
    if (n) out[0] = 0;
    for (unsigned i = 0; i < sizeof kName / sizeof kName[0]; i++) {
        if (!(ev & (1u << i))) continue;
        int w = snprintf(out + k, n > k ? n - k : 0, "%s%s", k ? "," : "", kName[i]);
        if (w > 0) k += (size_t)w;
    }
    return k < n ? k : (n ? n - 1 : 0);
}

size_t kart_format(const struct kart_snap *s, int tps, char *out, size_t n) {
    char rules[16] = "-", *r = rules;
    unsigned v = kart_rules(s);
    for (int i = 0; i < 5; i++)
        if (v & (1u << i)) r += snprintf(r, (size_t)(rules + sizeof rules - r), "%s%d", r == rules ? "" : ",", i + 1);
    const char *who = !s->tbl_ok ? "?" : s->t_local >= 0 ? "LOCAL" : "remote";
    int w = snprintf(out, n,
        "p%d %s st%u tps %d f %u | prog %.2f tgt %.2f best %.2f/%.2f lap %d/%d cp %d lc %#x | id %#x strikes %u "
        "slow %u still %u idle %d moves %u behind %u grace %u resc %u/%u | rules %s wd %s | xy %.1f,%.1f pos %.1f,%.1f "
        "pi %d k%d m%d t%d/%d/%d/%u g%u/%u o%#x",
        s->player, who, s->state, tps, s->frames, (double)s->p, (double)s->target, (double)s->best, (double)s->tbest,
        s->lap, s->maxlap, s->cp, s->lflags, s->error, s->strikes, s->slow, s->nomove, s->kart_ok ? s->idle : -1,
        s->moves, s->behind, s->grace, s->rescued, s->window, rules, kart_watched(s) ? "on" : "off",
        (double)s->xy[0], (double)s->xy[1], (double)s->pos[0], (double)s->pos[1], s->pidx,
        s->kart_ok ? s->kstate : -1, s->mode, s->t_local, s->t_a, s->t_b, s->t_flags, s->off, s->skip,
        s->kobj_flags);
    if (w < 0) { if (n) out[0] = 0; return 0; }
    return (size_t)w < n ? (size_t)w : (n ? n - 1 : 0);
}
