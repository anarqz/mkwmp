// Voice in the game's own menus (no overlay; research/docs/VOICE-RE.md §3, §6). The game has no voice UI and no
// per-player menu, so the online pause menu's player list (UIPageOnlinePauseMenu, 24 name plates in 1.0.0 and 26 in 1.8.0, display only) gets
// one: every frame while it is open, each plate's name is relabelled with the player's voice state, and a cursor over
// the list toggles the mute of the highlighted player.
//   [voice]  "Ana  ((o))"  talking now        "Ana  [MUTED]"  muted by this player
//   cursor   "> Ana"       highlighted        LB / RB (L / R) moves it, Y (X on a Nintendo layout) mutes / unmutes
//   keyboard PageUp / PageDown and M do the same
// The pad is read through the game's own SDL3 gamepads (no input hook). Plates are only rewritten when their text
// changes.
#include "voice_ui.h"

#include "friends.h"
#include "gameaddrs.h"
#include "guesthook.h"
#include "log.h"
#include "voice.h"

#include "plat.h"
#include <stdint.h>
#include <stdio.h>
#include <string.h>

// The page's vtable, its plate function and its layout are per game build (gameaddrs.h; VOICE-RE.md §3.1, §3.2).
// ga is set once the row's guards passed, before the hooks are installed.
enum { ROWS_MAX = GAME_ROWS_MAX, LABEL = 64 };
static const struct game_addrs *ga;

static uint64_t g_orig_open, g_orig_frame;
static int g_trace;

static struct {
    int open;            // the menu is open (set by the open hook, cleared when frames stop)
    uint64_t last_frame;
    int cursor;          // row index, -1 = none
    uint16_t shown[ROWS_MAX][LABEL];
    int shown_valid[ROWS_MAX];
    int prev_buttons;
} g;

// ---- input ---------------------------------------------------------------------------------------

enum { BTN_PREV = 1, BTN_NEXT = 2, BTN_TOGGLE = 4 };

static int read_buttons(void) {
    int b = 0;
    static uint32_t *(*get_pads)(int *);
    static void *(*from_id)(uint32_t);
    static int (*button)(void *, int);
    static void (*sdl_free)(void *);
    static int resolved;
    if (!resolved) {
        resolved = 1;
        void *sdl = plat_dl_loaded("SDL3");
        if (sdl) {
            *(void **)&get_pads = plat_dl_sym(sdl, "SDL_GetGamepads");
            *(void **)&from_id = plat_dl_sym(sdl, "SDL_GetGamepadFromID");
            *(void **)&button = plat_dl_sym(sdl, "SDL_GetGamepadButton");
            *(void **)&sdl_free = plat_dl_sym(sdl, "SDL_free");
        }
    }
    if (get_pads && from_id && button) {
        int n = 0;
        uint32_t *ids = get_pads(&n);
        for (int i = 0; ids && i < n; i++) {
            void *pad = from_id(ids[i]);   // only gamepads the game opened
            if (!pad) continue;
            if (button(pad, 9)) b |= BTN_PREV;    // SDL_GAMEPAD_BUTTON_LEFT_SHOULDER
            if (button(pad, 10)) b |= BTN_NEXT;   // SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER
            if (button(pad, 3)) b |= BTN_TOGGLE;  // SDL_GAMEPAD_BUTTON_NORTH (Y / X)
        }
        if (ids && sdl_free) sdl_free(ids);
    }
    if (plat_game_focused()) {
        if (plat_key_down(PLAT_KEY_PAGE_UP)) b |= BTN_PREV;
        if (plat_key_down(PLAT_KEY_PAGE_DOWN)) b |= BTN_NEXT;
        if (plat_key_down(PLAT_KEY_M)) b |= BTN_TOGGLE;
    }
    return b;
}

// ---- labels --------------------------------------------------------------------------------------

static size_t u16len(const uint16_t *s, size_t max) {
    size_t n = 0;
    while (n < max && s[n]) n++;
    return n;
}

static void put_ascii(uint16_t *dst, size_t *k, size_t cap, const char *s) {
    while (*s && *k + 1 < cap) dst[(*k)++] = (uint8_t)*s++;
}

// The name as UTF-8 (the launcher names are ASCII by the seat server's rule; others become '?').
static void name_utf8(const uint16_t *name, size_t n, char *out, size_t cap) {
    size_t k = 0;
    for (size_t i = 0; i < n && k + 1 < cap; i++) out[k++] = name[i] < 0x80 ? (char)name[i] : '?';
    out[k] = 0;
}

// The list is a count and a pointer to an array of entry pointers: the 1.8.0 plate-apply function (0x24cbc60) does
// `ldrsw x8, [x19, #0x4628]` ... `ldr x8, [x19, #0x4630]; ldr x21, [x8, x24, lsl #3]` (1.0.0: 0x3d80 / 0x3d88;
// VOICE-RE.md §6.1). Up to v1.4.2 the probe read the array INLINE at page + page_entries: entry i was then the page's
// own word at +page_entries + 8*i. For i < 3 those words happened to be harmless, the 4th was not (a 4-member room
// crashed the player whose pause menu frame ran: ACCESS_VIOLATION reading a garbage "name" pointer). Every pointer
// is now also checked before it is read: the page is the game's, and a list being rebuilt must not take us down.
static int readable(uint64_t p, size_t n) {
    return p && plat_mem_readable((const void *)(uintptr_t)p) >= n;
}

int voice_ui_page_entries(uint64_t page, const struct game_addrs *row, uint64_t *entries, const uint16_t **names,
                          size_t *lens) {
    if (!row || !readable(page, (size_t)row->page_entries + 8)) return -1;
    int count = *(const int32_t *)(uintptr_t)(page + row->page_count);
    if (count < 0 || count > row->rows || count > GAME_ROWS_MAX) return -1;
    if (count == 0) return 0;
    uint64_t arr = *(const uint64_t *)(uintptr_t)(page + row->page_entries);
    if (!readable(arr, (size_t)count * 8)) return -1;
    size_t need = (size_t)(row->entry_me > row->entry_name + 8 ? row->entry_me + 1 : row->entry_name + 8);
    for (int i = 0; i < count; i++) {
        uint64_t e = ((const uint64_t *)(uintptr_t)arr)[i];
        entries[i] = 0;
        names[i] = NULL;
        lens[i] = 0;
        if (!readable(e, need)) continue;
        uint64_t name = *(const uint64_t *)(uintptr_t)(e + row->entry_name);
        size_t avail = name ? plat_mem_readable((const void *)(uintptr_t)name) : 0;
        if (avail < 2) continue;
        entries[i] = e;
        names[i] = (const uint16_t *)(uintptr_t)name;
        lens[i] = u16len(names[i], avail / 2 < 32 ? avail / 2 : 32);
    }
    return count;
}

static void apply(Cpu *c, uint64_t page) {
    uint64_t entries[ROWS_MAX];
    const uint16_t *names[ROWS_MAX];
    size_t lens[ROWS_MAX];
    int count = voice_ui_page_entries(page, ga, entries, names, lens);
    if (count < 0) return;
    static int traced;
    if (g_trace && !traced) {
        traced = 1;
        mkwmp_log("voice-ui: page %#llx rows %d", (unsigned long long)page, count);
    }

    // input: edges only
    int buttons = read_buttons(), pressed = buttons & ~g.prev_buttons;
    g.prev_buttons = buttons;
    int active = voice_active();
    if (active && count > 0 && (pressed & (BTN_PREV | BTN_NEXT))) {
        int step = (pressed & BTN_NEXT) ? 1 : -1, at = g.cursor;
        for (int tries = 0; tries < count; tries++) {
            at = at < 0 ? (step > 0 ? 0 : count - 1) : (at + step + count) % count;
            uint64_t e = entries[at];
            if (e && !*(uint8_t *)(uintptr_t)(e + ga->entry_me)) break;   // never the player's own row
        }
        g.cursor = at;
    }
    if (g.cursor >= count) g.cursor = -1;

    for (int i = 0; i < count; i++) {
        uint64_t e = entries[i];
        if (!e) continue;
        const uint16_t *name = names[i];
        size_t n = lens[i];
        // strip a label we wrote earlier if the game hands our text back (it keeps its own copy, so normally not)
        char utf8[40];
        name_utf8(name, n, utf8, sizeof utf8);
        int me = *(uint8_t *)(uintptr_t)(e + ga->entry_me) != 0;
        uint64_t account = friends_account_id(utf8);
        if (i == g.cursor && (pressed & BTN_TOGGLE) && !me && active) voice_set_muted(account, utf8, !voice_is_muted(account));

        uint16_t label[LABEL];
        size_t k = 0;
        if (active && i == g.cursor) put_ascii(label, &k, LABEL, "> ");
        for (size_t j = 0; j < n && k + 1 < LABEL; j++) label[k++] = name[j];
        if (active) {
            if (!me && voice_is_muted(account)) put_ascii(label, &k, LABEL, "  [MUTED]");
            else if (voice_is_talking(account)) put_ascii(label, &k, LABEL, "  ((o))");
        }
        label[k] = 0;
        if (g.shown_valid[i] && !memcmp(g.shown[i], label, (k + 1) * 2)) continue;
        memcpy(g.shown[i], label, (k + 1) * 2);
        g.shown_valid[i] = 1;
        // the text goes through guest stack memory, as the game's own strings do
        uint64_t sp = c->sp;
        c->sp = (sp - sizeof label - 64) & ~15ull;
        memcpy((void *)(uintptr_t)c->sp, label, sizeof label);
        guest_call(c, ga->fn_plate_text, 2, page + ga->page_plates + (uint64_t)i * ga->plate_size, c->sp, 0, 0);
        c->sp = sp;
    }
}

// ---- hooks ---------------------------------------------------------------------------------------

// The hooked functions may be shared with other pages' vtables (the plugin API overrides the function, not the slot):
// only the online pause menu is ours.
static int is_pause_page(uint64_t page) {
    return page && plat_mem_readable((const void *)(uintptr_t)page) >= 8 &&
           *(const uint64_t *)(uintptr_t)page == ga->vt_online_pause;
}

static void hook_open(Cpu *c) {
    uint64_t page = c->x[0];
    if (!is_pause_page(page)) { guesthook_original(c, g_orig_open); return; }
    guesthook_original(c, g_orig_open);   // the game fills the plates
    uint64_t ret = c->x[0];
    memset(g.shown_valid, 0, sizeof g.shown_valid);   // the game rewrote every plate
    g.cursor = -1;
    g.prev_buttons = read_buttons();                  // a button held while opening is not a press
    if (g_trace) mkwmp_log("voice-ui: online pause menu open (thread %lu)", plat_thread_id());
    (void)page;
    c->x[0] = ret;
}

static void hook_frame(Cpu *c) {
    uint64_t page = c->x[0];
    if (!is_pause_page(page)) { guesthook_original(c, g_orig_frame); return; }
    uint64_t saved[8];
    memcpy(saved, c->x, sizeof saved);
    guesthook_original(c, g_orig_frame);
    uint64_t ret = c->x[0];
    uint64_t now = plat_ms();
    if (now - g.last_frame > 1000) memset(g.shown_valid, 0, sizeof g.shown_valid);   // reopened, or refilled
    g.last_frame = now;
    // the game refills the plates when its list changes (info+0xe12): our cache must not hide the new text
    static int last_count = -1;
    int count = readable(page, (size_t)ga->page_count + 4) ? *(const int32_t *)(uintptr_t)(page + ga->page_count) : -1;
    if (count != last_count) { memset(g.shown_valid, 0, sizeof g.shown_valid); last_count = count; }
    apply(c, page);
    (void)saved;
    c->x[0] = ret;
}

void voice_ui_install(void) {
    static int done;
    if (done) return;
    done = 1;
    g_trace = plat_getenv("MKWMP_VOICE_UI_TRACE", NULL, 0);
    g.cursor = -1;
    char v[8] = {0};
    if (plat_getenv("MKWMP_VOICE_UI", v, sizeof v)) {
        if (v[0] == '0') { mkwmp_log("voice-ui: off (MKWMP_VOICE_UI=0)"); return; }
    }
    int build = game_build();
    const struct game_addrs *row = game_addrs_for(build);
    if (!row) {   // no table row: the vtable, the plate function and the page layout are unknown
        mkwmp_log("voice-ui: off (game build %s; no menu hooks for it)", game_build_name(build));
        if (build == GAME_UNKNOWN) done = 0;   // main is not mapped yet: a later call decides
        return;
    }
    // Install-time guards (GAME-1.8.0-ADDRESSES.md §3): both vtable slots hold the game's own functions and the plate
    // function starts as expected. Checked before any slot is written.
    if (row->rows > ROWS_MAX ||
        !game_guard_slot("voice-ui open", row->vt_online_pause, row->slot_open, row->fn_pause_open) ||
        !game_guard_slot("voice-ui frame", row->vt_online_pause, row->slot_frame, row->fn_pause_frame) ||
        !game_guard_words("voice-ui plate text", row->fn_plate_text, row->plate_text_words, 4)) {
        mkwmp_log("voice-ui: off (game build %s: guard mismatch, hooks NOT installed)", game_build_name(build));
        return;
    }
    ga = row;
    int ok = guesthook_vtable(ga->vt_online_pause, ga->slot_open, hook_open, &g_orig_open) &&
             guesthook_vtable(ga->vt_online_pause, ga->slot_frame, hook_frame, &g_orig_frame);
    mkwmp_log("voice-ui: online pause menu hooks %s (game %s, %d plates)", ok ? "installed" : "NOT installed",
              game_build_name(build), ga->rows);
}
