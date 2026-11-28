// The console-style overlay, the mod's part (overlay.h; launcher/docs/OVERLAY.md).
//
// Threads: the pad filter runs on the game's input thread (plugin API 4), the picture callback on the present thread;
// both only record and wake the overlay's own thread, which does the waiting and the talking to the launcher.
//
// Plugin API 3 (mkw-recomp 0.1.15-mkwmp as released) has no filter, no picture and no way to keep mkw-recomp's menu
// shut, so there the thread watches the game's SDL3 pads itself: the chosen shortcut opens the overlay, its buttons
// still reach the game, and the trigger mkw-recomp's own menu uses (MKW_MENU_SHORTCUT: Select+Start, or Guide) is
// left to it. The game reads nothing while the launcher has the focus because SDL drops pad presses of a window
// without the keyboard focus (SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS is off by default, and mkw-recomp leaves it).
#include "plat_net.h"   // winsock2.h before windows.h

#include "overlay.h"

#include "config.h"
#include "ingame.h"
#include "log.h"
#include "mkw_plugin.h"
#include "plat.h"

#include <stdarg.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// ---- pure ----------------------------------------------------------------------------------------------------------

static int32_t iabs32(int32_t v) { return v < 0 ? -v : v; }

// One combination: Guide; a first button held back from the game (mod) that triggers with `with` or after `hold_ms`;
// or buttons that pass to the game until they are all down together (`all`).
typedef struct {
    const char *name;
    int guide;
    uint64_t mod, with;
    uint32_t hold_ms;
    uint64_t all;
} ComboDef;
static const ComboDef kCombos[OVERLAY_COMBO_COUNT] = {
    [OVERLAY_COMBO_DEFAULT] = {"guide_or_select_start", 1, OVERLAY_BTN_MINUS, OVERLAY_BTN_PLUS, OVERLAY_HOLD_MS, 0},
    [OVERLAY_COMBO_GUIDE] = {"guide", 1, 0, 0, 0, 0},
    [OVERLAY_COMBO_SELECT_START] = {"select_start", 0, OVERLAY_BTN_MINUS, OVERLAY_BTN_PLUS, 0, 0},
    [OVERLAY_COMBO_SELECT_HOLD] = {"select_hold", 0, OVERLAY_BTN_MINUS, 0, OVERLAY_HOLD_MS, 0},
    [OVERLAY_COMBO_START_HOLD] = {"start_hold", 0, OVERLAY_BTN_PLUS, 0, OVERLAY_HOLD_MS, 0},
    [OVERLAY_COMBO_STICKS] = {"l3_r3", 0, 0, 0, 0, OVERLAY_BTN_STICK_L | OVERLAY_BTN_STICK_R},
    [OVERLAY_COMBO_BUMPERS_START] = {"lb_rb_start", 0, 0, 0, 0, OVERLAY_BTN_L | OVERLAY_BTN_R | OVERLAY_BTN_PLUS},
    [OVERLAY_COMBO_SELECT_LB] = {"select_lb", 0, OVERLAY_BTN_MINUS, OVERLAY_BTN_L, 0, 0},
    [OVERLAY_COMBO_SELECT_RB] = {"select_rb", 0, OVERLAY_BTN_MINUS, OVERLAY_BTN_R, 0, 0},
};

OverlayCombo overlay_combo_parse(const char *name, int len) {
    for (int i = 0; name && len > 0 && i < OVERLAY_COMBO_COUNT; i++)
        if ((int)strlen(kCombos[i].name) == len && !strncmp(kCombos[i].name, name, (size_t)len)) return (OverlayCombo)i;
    return OVERLAY_COMBO_DEFAULT;
}

const char *overlay_combo_name(OverlayCombo combo) {
    return kCombos[(unsigned)combo < OVERLAY_COMBO_COUNT ? combo : OVERLAY_COMBO_DEFAULT].name;
}

void overlay_gate_set_combo(OverlayGate *g, OverlayCombo combo) {
    if ((unsigned)combo >= OVERLAY_COMBO_COUNT) combo = OVERLAY_COMBO_DEFAULT;
    if (g->combo == combo) return;
    g->combo = combo;
    g->mod_held = g->mod_fired = 0;
    g->replay_until = 0;
    g->all_was = 1;     // what is down now is not a press of the new combination
    g->guide_was = 1;
}

OverlayTrigger overlay_gate(OverlayGate *g, OverlayPad *pad, uint64_t now_ms, int armed, int open) {
    const ComboDef *c = &kCombos[(unsigned)g->combo < OVERLAY_COMBO_COUNT ? g->combo : OVERLAY_COMBO_DEFAULT];
    if (!armed) {
        OverlayCombo keep = g->combo;
        memset(g, 0, sizeof *g);
        g->combo = keep;
        return OVERLAY_BY_NONE;
    }
    OverlayTrigger t = OVERLAY_BY_NONE;
    int guide = pad->guide != 0;
    if (c->guide && guide && !g->guide_was) t = OVERLAY_BY_GUIDE;
    g->guide_was = guide;

    uint64_t b = pad->buttons;
    int mod = c->mod && (b & c->mod) == c->mod;
    if (mod) {
        if (!g->mod_held) {
            g->mod_held = 1;
            g->mod_fired = 0;
            g->mod_since = now_ms;
        }
        if (!g->mod_fired) {
            if (c->with && (b & c->with) == c->with) {
                g->mod_fired = 1;
                if (!t) t = OVERLAY_BY_CHORD;
            } else if (c->hold_ms && now_ms - g->mod_since >= c->hold_ms) {
                g->mod_fired = 1;
                if (!t) t = OVERLAY_BY_HOLD;
            }
        }
    } else if (g->mod_held) {
        g->mod_held = 0;
        if (!g->mod_fired) g->replay_until = now_ms + OVERLAY_REPLAY_MS;   // a normal press: the game gets it, late
    }

    int all = c->all && (b & c->all) == c->all;   // L3+R3, LB+RB+Start: each alone was the game's until now
    if (all && !g->all_was && !t) t = OVERLAY_BY_CHORD;
    g->all_was = all;

    if (t) g->idle = 1;
    if (open || g->idle) {
        int at_rest = !pad->buttons && !guide && iabs32(pad->lx) < OVERLAY_STICK_IDLE &&
                      iabs32(pad->ly) < OVERLAY_STICK_IDLE && iabs32(pad->rx) < OVERLAY_STICK_IDLE &&
                      iabs32(pad->ry) < OVERLAY_STICK_IDLE;
        if (!open && !t && at_rest) g->idle = 0;
        g->replay_until = 0;
        memset(pad, 0, sizeof *pad);   // connected and neutral, never disconnected
        return t;
    }
    if (c->guide) pad->guide = 0;   // otherwise Guide stays the game's (and mkw-recomp's menu_shortcut = guide)
    if (c->mod) {
        pad->buttons &= ~c->mod;
        if (!mod && now_ms < g->replay_until) pad->buttons |= c->mod;
    }
    return t;
}

void overlay_gate_closed(OverlayGate *g) {
    g->idle = 1;
    g->replay_until = 0;
    if (g->mod_held) g->mod_fired = 1;   // what is held now is not a press for the game
}

// The value after "key": in a flat JSON object of the launcher's (no nesting, no escapes in what is read here).
static const char *json_value(const char *json, const char *key) {
    char pat[32];
    if (!json || snprintf(pat, sizeof pat, "\"%s\"", key) >= (int)sizeof pat) return NULL;
    const char *p = strstr(json, pat);
    if (!p) return NULL;
    p += strlen(pat);
    while (*p == ' ' || *p == '\t') p++;
    if (*p != ':') return NULL;
    p++;
    while (*p == ' ' || *p == '\t') p++;
    return p;
}

int overlay_parse_state(const char *json, OverlayState *out) {
    memset(out, 0, sizeof *out);
    const char *v = json_value(json, "open");
    if (!v) return 0;
    if (!strncmp(v, "true", 4)) out->open = 1;
    else if (strncmp(v, "false", 5)) return 0;
    if ((v = json_value(json, "seq"))) out->seq = strtoll(v, NULL, 10);
    out->enabled = !(v = json_value(json, "enabled")) || !strncmp(v, "true", 4);
    if ((v = json_value(json, "action"))) out->menu = !strncmp(v, "\"menu\"", 6), out->quit = !strncmp(v, "\"quit\"", 6);
    if ((v = json_value(json, "shortcut")) && *v == '"') {
        const char *end = strchr(v + 1, '"');
        if (end) out->combo = overlay_combo_parse(v + 1, (int)(end - v - 1));
    }
    return 1;
}

// ---- the overlay of this process -----------------------------------------------------------------------------------

enum {
    TICK_MS = 16,             // the thread's step while armed: Shift+Tab, and the pads on plugin API 3
    PROBE_ARMED_MS = 10000,   // how often the launcher is asked whether it still has an overlay
    PROBE_IDLE_MS = 3000,     // ... while it has none (not started yet, an older launcher)
    FRAME_WAIT_MS = 300,      // for the picture, before the request goes without one
    FRAME_W = 1280, FRAME_H = 720,
    STATE_WAIT_S = 2,         // the long poll while open; a second trigger is looked at between two of them
};

static const MkwPluginApi *g_api;
static void (*g_menu_open)(int open);
static int (*g_capture)(int max_w, int max_h,
                        void (*cb)(const uint8_t *rgba, int w, int h, int row_bytes, void *user), void *user);
static int (*g_focus_set)(int in_focus, int suspend);   // the game behind the overlay as behind HOME (API 4, later)
static void (*g_quit)(void);                            // the runtime's own clean end (API 4, later)
static int g_suspend = 1;    // the mod's setting overlay_pause: 0 keeps the game running behind the overlay
static int g_filtered;       // the runtime calls pad_filter (plugin API 4)

static PlatEvent *g_wake;    // a trigger: the thread looks now
static PlatEvent *g_framed;  // the picture arrived
static volatile int g_armed, g_open, g_trigger, g_closed, g_idle;
static volatile int g_combo;   // OverlayCombo: the launcher's "shortcut", at every answer of its (live)
static OverlayGate g_gate;   // the filter's (the game's input thread only)
static OverlayGate g_watch;  // the thread's own, on plugin API 3

static PlatLock g_frame_lock = PLAT_LOCK_INIT;
static uint8_t *g_frame;     // RGBA8 rows, tight; under g_frame_lock
static int g_frame_w, g_frame_h;

static const char *trigger_name(int t) {
    switch (t) {
    case OVERLAY_BY_GUIDE: return "guide";
    case OVERLAY_BY_CHORD: return "chord";
    case OVERLAY_BY_HOLD: return "hold";
    case OVERLAY_BY_KEYS: return "keys";
    default: return "none";
    }
}

static void trigger(OverlayTrigger t) {
    plat_cas(&g_trigger, (int)t, 0);
    plat_event_set(g_wake);
}

// The launcher's "shortcut" (Settings › Game), from any of its answers: the filter takes it at its next read.
static void use_combo(OverlayCombo combo) {
    if ((int)combo == plat_load(&g_combo)) return;
    plat_store(&g_combo, (int)combo);
    mkwmp_log("overlay: the shortcut is now %s", overlay_combo_name(combo));
}

// Plugin API 4: each read of a player's pad, before mkw-recomp's menu shortcut and the game. Does not wait.
static void pad_filter(MkwPad *pad, void *user) {
    (void)user;
    int armed = plat_load(&g_armed), open = plat_load(&g_open);
    if (pad->player != 0) {   // players 2-8 idle with player 1: nobody drives blind behind the overlay
        if (armed && (open || plat_load(&g_idle))) pad->buttons = 0, pad->lx = pad->ly = pad->rx = pad->ry = 0, pad->guide = 0;
        return;
    }
    if (plat_xchg(&g_closed, 0)) overlay_gate_closed(&g_gate);
    overlay_gate_set_combo(&g_gate, (OverlayCombo)plat_load(&g_combo));
    OverlayPad p = {pad->buttons, pad->lx, pad->ly, pad->rx, pad->ry, pad->guide};
    OverlayTrigger t = overlay_gate(&g_gate, &p, plat_ms(), armed, open);
    plat_store(&g_idle, g_gate.idle);
    pad->buttons = p.buttons, pad->lx = p.lx, pad->ly = p.ly, pad->rx = p.rx, pad->ry = p.ry, pad->guide = p.guide;
    if (t) trigger(t);
}

// Plugin API 4: the shown picture, on the present thread.
static void frame_arrived(const uint8_t *rgba, int w, int h, int row_bytes, void *user) {
    (void)user;
    if (!rgba || w <= 0 || h <= 0 || w > 4096 || h > 4096 || row_bytes < w * 4) return;
    uint8_t *copy = malloc((size_t)w * (size_t)h * 4);
    if (!copy) return;
    for (int y = 0; y < h; y++) memcpy(copy + (size_t)y * (size_t)w * 4, rgba + (size_t)y * (size_t)row_bytes, (size_t)w * 4);
    plat_lock(&g_frame_lock);
    free(g_frame);
    g_frame = copy, g_frame_w = w, g_frame_h = h;
    plat_unlock(&g_frame_lock);
    plat_event_set(g_framed);
}

// ---- the launcher's local API: one request, with a body of bytes and its own timeout (launcher.c sends JSON, waits
// 5 s and serializes its callers: the long poll must not hold the seat's keepalive)
static int request_once(const MkwmpConfig *cfg, const char *method, const char *path, const void *body, size_t len,
                        int json, uint32_t timeout_ms, char *buf, size_t cap, const char **answer) {
    plat_net_init();
    PlatSock s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == PLAT_NO_SOCK) return 0;
    plat_sock_timeouts(s, timeout_ms);
    struct sockaddr_in sa = {0};
    sa.sin_family = AF_INET;
    sa.sin_port = htons((unsigned short)cfg->launcher_port);
    sa.sin_addr.s_addr = inet_addr(cfg->launcher_host);
    if (connect(s, (struct sockaddr *)&sa, sizeof sa)) { plat_sock_close(s); return 0; }
    char head[640];
    int n = snprintf(head, sizeof head,
                     "%s %s HTTP/1.1\r\nHost: %s:%d\r\nAuthorization: Bearer %s\r\n%s"
                     "Content-Length: %llu\r\nConnection: close\r\n\r\n",
                     method, path, cfg->launcher_host, cfg->launcher_port, cfg->launcher_token,
                     !len ? "" : json ? "Content-Type: application/json\r\n" : "Content-Type: application/octet-stream\r\n",
                     (unsigned long long)len);
    if (n < 0 || n >= (int)sizeof head || send(s, head, n, 0) != n) { plat_sock_close(s); return 0; }
    const char *p = body;
    for (size_t sent = 0; sent < len;) {
        size_t chunk = len - sent > 65536 ? 65536 : len - sent;
        int k = send(s, p + sent, (int)chunk, 0);
        if (k <= 0) { plat_sock_close(s); return 0; }
        sent += (size_t)k;
    }
    int got = 0, r;
    while (got < (int)cap - 1 && (r = recv(s, buf + got, (int)cap - 1 - got, 0)) > 0) got += r;
    plat_sock_close(s);
    buf[got] = 0;
    int status = 0;
    if (sscanf(buf, "HTTP/1.%*d %d", &status) != 1) return 0;
    const char *b = strstr(buf, "\r\n\r\n");
    *answer = b ? b + 4 : buf + got;
    return status;
}

// Who answers: the launcher that started the game (mkwmp.toml), else the overlay host this mod started itself (its
// session file in the mod's folder, the same two keys). NULL when there is neither yet.
static int g_hosted;                 // no launcher runs the game: the mod starts one as its overlay host
static char g_host_exe[520];         // ... this one
static MkwmpConfig g_host_cfg;
static const char kSessionFile[] = "mkwmp-overlay.toml";

static const MkwmpConfig *endpoint(int reload) {
    if (!g_hosted) {
        const MkwmpConfig *cfg = reload ? mkwmp_config_reload() : mkwmp_config();
        return cfg->launcher_port && !cfg->direct ? cfg : NULL;
    }
    if (reload || !g_host_cfg.launcher_port) {
        char path[520], text[2048];
        memset(&g_host_cfg, 0, sizeof g_host_cfg);
        long n = plat_home_path(path, sizeof path, kSessionFile) ? plat_read_file(path, text, sizeof text - 1) : -1;
        if (n > 0) {
            text[n] = 0;
            mkwmp_config_parse(text, &g_host_cfg);
        }
    }
    return g_host_cfg.launcher_port ? &g_host_cfg : NULL;
}

// The HTTP status; 0 when the launcher did not answer; -1 when there is none to ask.
// The endpoint for one request, as a copy: the overlay's thread and the in-game overlay's pollers ask at the same time,
// and one of them may be reading the session again.
static int endpoint_now(int reload, MkwmpConfig *out) {
    static PlatLock lock = PLAT_LOCK_INIT;
    plat_lock(&lock);
    const MkwmpConfig *cfg = endpoint(reload);
    if (cfg) *out = *cfg;
    plat_unlock(&lock);
    return cfg != NULL;
}

static int request_as(const char *method, const char *path, const void *body, size_t len, int json, uint32_t timeout_ms,
                      char *buf, size_t cap, const char **answer) {
    buf[0] = 0;
    *answer = buf;
    MkwmpConfig cfg;
    int status = endpoint_now(0, &cfg) ? request_once(&cfg, method, path, body, len, json, timeout_ms, buf, cap, answer) : 0;
    if (status == 0 || status == 401) {   // the launcher may have restarted (new token, same port)
        if (!endpoint_now(1, &cfg)) return -1;
        status = request_once(&cfg, method, path, body, len, json, timeout_ms, buf, cap, answer);
    }
    return status;
}

static int request(const char *method, const char *path, const void *body, size_t len, uint32_t timeout_ms, char *buf,
                   size_t cap, const char **answer) {
    return request_as(method, path, body, len, 0, timeout_ms, buf, cap, answer);
}

// ---- the in-game overlay (ingame.h): the launcher's toasts and, on plugin API 5, its overlay page, drawn in the game's
// own frames. What it needs of this file: the launcher's local API (the same endpoint and bearer as above; several
// threads ask at once, each with a socket of its own), and the pad around the page.
static int ingame_request(const char *method, const char *path, const char *json, uint32_t timeout_ms, char *buf,
                          size_t cap, const char **answer) {
    return request_as(method, path, json, json ? strlen(json) : 0, 1, timeout_ms, buf, cap, answer);
}

static void ingame_log(const char *fmt, ...) {
    char line[512];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(line, sizeof line, fmt, ap);
    va_end(ap);
    mkwmp_log("%s", line);
}

static uint64_t ingame_now(void) { return plat_ms(); }
static void ingame_sleep(uint32_t ms) { plat_sleep_ms(ms); }

// The page's own reading of the pad while it has the input (the present thread): the player's shortcut pressed again
// closes it. A gate of its own, primed as the page opens so what is still held from the opening is not a press.
static OverlayGate g_page_gate;
static volatile int g_page_prime;

static int ingame_shortcut(const MkwPad *pad, uint64_t now_ms) {
    if (plat_xchg(&g_page_prime, 0)) {
        memset(&g_page_gate, 0, sizeof g_page_gate);
        g_page_gate.guide_was = g_page_gate.all_was = g_page_gate.mod_held = g_page_gate.mod_fired = 1;
    }
    g_page_gate.combo = (OverlayCombo)plat_load(&g_combo);
    OverlayPad p = {pad->buttons, pad->lx, pad->ly, pad->rx, pad->ry, pad->guide};
    return overlay_gate(&g_page_gate, &p, now_ms, 1, 1) != OVERLAY_BY_NONE;
}

// The in-game page closed (B, Esc, the shortcut, the launcher went away), on the present thread: as after the window
// overlay, the game's pad stays idle until every button is up.
static void ingame_page_closed(void) {
    plat_store(&g_closed, 1);
    plat_store(&g_open, 0);
}

// The overlay host: the MKWMP launcher started by the mod, without a window until the trigger, for players who start
// the game without it (launcher/docs/OVERLAY.md, "Without the launcher"). It writes kSessionFile and leaves with the
// game.
static uint64_t g_host_started;

static int file_there(const char *path) {
    char two[2];
    return path[0] && plat_read_file(path, two, sizeof two) > 0;
}

// "exe": "<path>" of the mod folder's launcher.json, which the launcher writes there (JSON escapes \\ and
// \" undone; anything else is not a path the launcher writes).
static int record_exe(const char *json, char *out, size_t cap) {
    const char *v = json_value(json, "exe");
    if (!v || *v != '"') return 0;
    size_t n = 0;
    for (v++; *v && *v != '"' && n + 1 < cap; v++) {
        if (*v == '\\' && (v[1] == '\\' || v[1] == '"' || v[1] == '/')) v++;
        out[n++] = *v;
    }
    out[n] = 0;
    return *v == '"' && n > 0;
}

// Where the launcher is, most explicit first. Under Wine every one of them is a path of this prefix.
static int host_find(const MkwPluginApi *api) {
    int has_settings = api->size >= offsetof(MkwPluginApi, setting_set) + sizeof api->setting_set && api->setting_get;
    // 1. the mod's own setting (mod-settings/mkwmp.ini: launcher_path)
    if (has_settings && api->setting_get("launcher_path", g_host_exe, sizeof g_host_exe) && file_there(g_host_exe))
        return 1;
    // 2. what the launcher left in mkwmp.toml when it last ran this game (launcher_exe)
    snprintf(g_host_exe, sizeof g_host_exe, "%s", mkwmp_config()->launcher_exe);
    if (file_there(g_host_exe)) return 1;
    // 3. the record the launcher leaves in this mod's folder (portable: nothing in the user's profile), also for
    //    players who only ever opened the launcher once
    char path[520], text[2048];
    if (plat_home_path(path, sizeof path, "launcher.json")) {
        long n = plat_read_file(path, text, sizeof text - 1);
        if (n > 0) {
            text[n] = 0;
            if (record_exe(text, g_host_exe, sizeof g_host_exe) && file_there(g_host_exe)) return 1;
        }
    }
    // 4. a launcher put into the mod's folder
    if (plat_home_path(g_host_exe, sizeof g_host_exe, "mkwmp.exe") && file_there(g_host_exe)) return 1;
    g_host_exe[0] = 0;
    return 0;
}
static void host_start(void) {
    uint64_t now = plat_ms();
    if (!g_hosted || (g_host_started && now - g_host_started < 15000)) return;   // one start at a time
    g_host_started = now ? now : 1;
    char session[520], args[700];
    if (!plat_home_path(session, sizeof session, kSessionFile)) return;
    snprintf(args, sizeof args, "--overlay-host --parent-pid %lu --session \"%s\"", plat_process_id(), session);
    int ok = plat_spawn(g_host_exe, args);
    mkwmp_log("overlay: %s the launcher as the overlay host (%s)", ok ? "started" : "could not start", g_host_exe);
}

// ---- plugin API 3: the game's own SDL3, read from here (the same pads, after Steam Input)
static struct {
    int looked;
    uint32_t *(*get_gamepads)(int *count);
    void *(*from_id)(uint32_t id);
    unsigned char (*button)(void *pad, int button);
    void (*free)(void *p);
} g_sdl;
enum {   // SDL_GamepadButton
    SDL_BTN_BACK = 4, SDL_BTN_GUIDE = 5, SDL_BTN_START = 6, SDL_BTN_LSTICK = 7, SDL_BTN_RSTICK = 8, SDL_BTN_LB = 9,
    SDL_BTN_RB = 10,
};

static int sdl_ready(void) {
    if (!g_sdl.looked) {
        g_sdl.looked = 1;
        void *lib = plat_dl_loaded("SDL3");
        if (lib) {
            g_sdl.get_gamepads = (uint32_t *(*)(int *))plat_dl_sym(lib, "SDL_GetGamepads");
            g_sdl.from_id = (void *(*)(uint32_t))plat_dl_sym(lib, "SDL_GetGamepadFromID");
            g_sdl.button = (unsigned char (*)(void *, int))plat_dl_sym(lib, "SDL_GetGamepadButton");
            g_sdl.free = (void (*)(void *))plat_dl_sym(lib, "SDL_free");
        }
        mkwmp_log("overlay: no pad filter in this mkw-recomp (plugin API 3): %s", g_sdl.button && g_sdl.get_gamepads &&
                  g_sdl.from_id && g_sdl.free ? "watching the game's SDL3 pads (Guide, Select held)"
                                              : "SDL3 not found, Shift+Tab only");
    }
    return g_sdl.get_gamepads && g_sdl.from_id && g_sdl.button && g_sdl.free;
}

static void sdl_watch(void) {
    if (!sdl_ready()) return;
    OverlayPad p = {0};
    int n = 0;
    uint32_t *ids = g_sdl.get_gamepads(&n);
    for (int i = 0; ids && i < n; i++) {
        void *pad = g_sdl.from_id(ids[i]);   // the pads the game opened; NULL for one it did not
        if (!pad) continue;
        if (g_sdl.button(pad, SDL_BTN_BACK)) p.buttons |= OVERLAY_BTN_MINUS;
        if (g_sdl.button(pad, SDL_BTN_START)) p.buttons |= OVERLAY_BTN_PLUS;
        if (g_sdl.button(pad, SDL_BTN_GUIDE)) p.guide = 1;
        if (g_sdl.button(pad, SDL_BTN_LSTICK)) p.buttons |= OVERLAY_BTN_STICK_L;
        if (g_sdl.button(pad, SDL_BTN_RSTICK)) p.buttons |= OVERLAY_BTN_STICK_R;
        if (g_sdl.button(pad, SDL_BTN_LB)) p.buttons |= OVERLAY_BTN_L;
        if (g_sdl.button(pad, SDL_BTN_RB)) p.buttons |= OVERLAY_BTN_R;
    }
    if (ids) g_sdl.free(ids);
    OverlayCombo combo = (OverlayCombo)plat_load(&g_combo);
    overlay_gate_set_combo(&g_watch, combo);
    OverlayTrigger t = overlay_gate(&g_watch, &p, plat_ms(), 1, plat_load(&g_open));
    if (!t) return;
    // mkw-recomp's menu opens on one of the two and cannot be kept shut from here: that one stays its own
    const char *own = g_api->cfg_get ? g_api->cfg_get("MKW_MENU_SHORTCUT") : NULL;
    int own_guide = own && !strcmp(own, "guide");
    int select_start = t == OVERLAY_BY_CHORD && (combo == OVERLAY_COMBO_DEFAULT || combo == OVERLAY_COMBO_SELECT_START);
    if ((t == OVERLAY_BY_GUIDE && own_guide) || (select_start && !own_guide)) return;
    trigger(t);
}

// ---- one opening: the picture, the request, the wait for "back to the game"
// The game behind the overlay, as behind a console's HOME menu: out of focus, silent and, where the game lets the
// console do it, suspended (MKW asks not to be while it uses the network: an online race is never stopped here).
// Without the entry (an older runtime) the game runs on with an idle pad, as before.
static int g_held;
static void hold(int on) {
    if (!g_focus_set || g_held == on) return;
    g_held = on;
    int suspended = g_focus_set(!on, on && g_suspend);
    if (on) mkwmp_log("overlay: the game is out of focus and %s", suspended ? "suspended" : !g_suspend
                      ? "runs on (overlay_pause = 0)" : "runs on (it uses the network, or is still starting)");
}

// The idle pad first, then the game runs again: the B that closed the overlay and whatever is held stay with the gate
// until every button is up.
static void closed(void) {
    plat_store(&g_closed, 1);
    overlay_gate_closed(&g_watch);
    plat_store(&g_open, 0);
    hold(0);
}

static int ask_open(OverlayTrigger by, int with_frame, OverlayState *st) {
    char path[160], buf[1024];
    const char *answer;
    uint8_t *frame = NULL;
    int w = 0, h = 0;
    if (with_frame) {
        plat_lock(&g_frame_lock);
        frame = g_frame, w = g_frame_w, h = g_frame_h;
        g_frame = NULL;
        plat_unlock(&g_frame_lock);
    }
    // menu=1: this runtime opens its own menu when asked (plugin API 4), so the launcher may offer it;
    // quit=1: and ends itself cleanly when asked (the launcher's "Quit Game" then waits for that before a WM_CLOSE)
    const char *menu = g_menu_open ? g_quit ? "&menu=1&quit=1" : "&menu=1" : g_quit ? "&quit=1" : "";
    if (frame)
        snprintf(path, sizeof path, "/local/v1/overlay/open?by=%s&pid=%lu%s&w=%d&h=%d&format=rgba", trigger_name(by),
                 plat_process_id(), menu, w, h);
    else
        snprintf(path, sizeof path, "/local/v1/overlay/open?by=%s&pid=%lu%s", trigger_name(by), plat_process_id(), menu);
    plat_allow_foreground();   // Windows: the launcher may take the foreground from the game
    int status = request("POST", path, frame, frame ? (size_t)w * (size_t)h * 4 : 0, 5000, buf, sizeof buf, &answer);
    free(frame);
    if (status != 200 || !overlay_parse_state(answer, st)) {
        memset(st, 0, sizeof *st);
        return status;
    }
    return 200;
}

static void session(OverlayTrigger by) {
    plat_store(&g_open, 1);   // the game reads an idle pad from now
    int framed = 0;
    if (g_capture) {
        plat_event_reset(g_framed);
        framed = g_capture(FRAME_W, FRAME_H, frame_arrived, NULL) && plat_event_wait(g_framed, FRAME_WAIT_MS);
    }
    hold(1);   // after the picture: a suspended game presents nothing
    OverlayState st;
    int status = ask_open(by, 1, &st);
    if (status != 200 || !st.open) {
        mkwmp_log("overlay: %s: the launcher did not open (HTTP %d)", trigger_name(by), status);
        closed();
        if (status != 200) plat_store(&g_armed, 0), host_start();   // probed again; a host that died is started again
        // the button was taken from mkw-recomp: its own menu instead of nothing
        if (g_menu_open && (by == OVERLAY_BY_GUIDE || by == OVERLAY_BY_CHORD)) g_menu_open(1);
        return;
    }
    mkwmp_log("overlay: opened by %s (%s)", trigger_name(by), framed ? "with the picture" : "no picture");
    int fails = 0;
    while (st.open) {
        char path[96], buf[1024];
        const char *answer;
        snprintf(path, sizeof path, "/local/v1/overlay/state?wait=%d&seq=%lld", STATE_WAIT_S, st.seq);
        status = request("GET", path, NULL, 0, (STATE_WAIT_S + 5) * 1000, buf, sizeof buf, &answer);
        OverlayState now;
        if (status == 200 && overlay_parse_state(answer, &now)) {
            fails = 0;
            st = now;
            use_combo(now.combo);
        } else if (++fails >= 3) {   // the launcher went away: the game is the player's again
            mkwmp_log("overlay: the launcher stopped answering (HTTP %d): back to the game", status);
            st.open = 0, st.menu = 0, st.quit = 0;
        } else {
            plat_sleep_ms(300);
        }
        int again = plat_xchg(&g_trigger, 0);
        if (st.open && again) {   // the game has the focus again with the overlay still up: bring the launcher back
            OverlayState re;
            if (ask_open((OverlayTrigger)again, 0, &re) == 200) st = re;
        }
    }
    if (st.quit) {
        // Quit Game: the runtime ends as by its menu's Quit (the mod's on_exit gives the seat back, the saves finish).
        // The game stays held meanwhile: no frame more, no sound. Without the entry the launcher closes the window,
        // which ends the game the same way.
        mkwmp_log("overlay: closed (Quit Game asked): %s", g_quit ? "the game ends" : "this mkw-recomp has no quit "
                  "entry, the launcher closes the window");
        if (g_quit) {
            g_quit();
            plat_sleep_ms(15000);   // the process ends within this; the launcher closes the window after 8 s
            mkwmp_log("overlay: the game did not end: it is the player's again");
        }
    }
    closed();
    plat_xchg(&g_trigger, 0);
    if (st.quit) return;
    mkwmp_log("overlay: closed%s", st.menu ? " (mkw-recomp's menu asked)" : "");
    if (st.menu && g_menu_open) g_menu_open(1);
}

static void overlay_main(void *arg) {
    (void)arg;
    uint64_t next_probe = 0;
    int keys_was = 0;
    host_start();   // pre-warmed: the trigger then only has to show it
    for (;;) {
        int armed = plat_load(&g_armed);
        plat_event_wait(g_wake, armed ? TICK_MS : PROBE_IDLE_MS);
        uint64_t now = plat_ms();
        if (now >= next_probe || !armed) {
            char buf[512];
            const char *answer;
            OverlayState st;
            int ok = request("GET", "/local/v1/overlay/state", NULL, 0, 2000, buf, sizeof buf, &answer) == 200 &&
                     overlay_parse_state(answer, &st) && st.enabled;
            if (ok) use_combo(st.combo);
            if (ok != armed) mkwmp_log(ok ? "overlay: the launcher has one: armed" : "overlay: no launcher overlay: off");
            plat_store(&g_armed, armed = ok);
            next_probe = now + PROBE_ARMED_MS;
        }
        if (!armed) {
            plat_xchg(&g_trigger, 0);
            continue;
        }
        if (plat_game_focused()) {
            int keys = plat_key_down(PLAT_KEY_SHIFT) && plat_key_down(PLAT_KEY_TAB);   // Steam's chord
            if (keys && !keys_was) trigger(OVERLAY_BY_KEYS);
            keys_was = keys;
            if (!g_filtered) sdl_watch();
        }
        int t = plat_xchg(&g_trigger, 0);
        if (!t) continue;
        // Plugin API 5 with a launcher that serves its page: the page opens (or closes) in the game's own frames and
        // the game runs on (ingame.h). Anything else: the launcher's window over the game, as before.
        int was = ingame_page_open();
        if (!was) {
            plat_store(&g_page_prime, 1);
            plat_store(&g_open, 1);   // the game reads an idle pad from now (the runtime also idles it for the page)
        }
        if (ingame_page_toggle(trigger_name(t))) {
            if (!was && !ingame_page_open()) ingame_page_closed();   // it did not open after all (the same press twice)
            continue;
        }
        if (!was) plat_store(&g_open, 0);
        session((OverlayTrigger)t);
    }
}

void overlay_install(const MkwPluginApi *api) {
    if (!api) return;
    char off[8] = "";
    if (api->size >= offsetof(MkwPluginApi, setting_set) + sizeof api->setting_set && api->setting_get &&
        api->setting_get("overlay", off, sizeof off) && !strcmp(off, "0")) {
        mkwmp_log("overlay: off (the mod's setting overlay = 0)");
        return;
    }
    const MkwmpConfig *cfg = mkwmp_config();
    g_hosted = !(cfg->launcher_port && !cfg->direct);
    if (g_hosted && !host_find(api)) {
        mkwmp_log("overlay: off: no MKWMP launcher runs this game and none is found to start (the setting "
                  "launcher_path, launcher_exe of mkwmp.toml, the mod folder's launcher.json, the mod's folder)");
        return;
    }
    g_api = api;
    g_wake = plat_event_new(0);
    g_framed = plat_event_new(0);
    if (!g_wake || !g_framed) {
        mkwmp_log("overlay: off: no event");
        return;
    }
    // plugin API 4 (launcher/docs/OVERLAY.md, "The fork patch"): each entry is there when the table reaches it
    if (api->size >= offsetof(MkwPluginApi, frame_capture) + sizeof api->frame_capture) {
        g_menu_open = api->menu_open;
        g_capture = api->frame_capture;
        g_filtered = api->input_filter && api->input_filter(pad_filter, NULL);
    }
    // ... and the two added to version 4 later (a runtime without them: the game runs on behind the overlay, and
    // "Quit Game" is the launcher closing the window)
    if (api->size >= offsetof(MkwPluginApi, focus_set) + sizeof api->focus_set) g_focus_set = api->focus_set;
    if (api->size >= offsetof(MkwPluginApi, quit) + sizeof api->quit) g_quit = api->quit;
    char pause[8] = "";
    if (api->size >= offsetof(MkwPluginApi, setting_set) + sizeof api->setting_set && api->setting_get &&
        api->setting_get("overlay_pause", pause, sizeof pause) && !strcmp(pause, "0"))
        g_suspend = 0;
    mkwmp_log("overlay: %s; %s", g_filtered ? "the pad goes through the mod (plugin API 4)" : "plugin API 3: watching only",
              g_hosted ? "the mod starts the launcher as its overlay host" : "the launcher that started the game answers");
    mkwmp_log("overlay: behind it the game %s; Quit Game %s",
              !g_focus_set ? "runs on (this mkw-recomp has no focus_set)" : g_suspend
              ? "is out of focus and suspended where the game allows it (not while it uses the network)"
              : "is out of focus and runs on (overlay_pause = 0)",
              g_quit ? "ends it through the runtime" : "is the launcher closing the window");
    // the launcher's toasts (and, on plugin API 5, its overlay page) in the game's own frames: overlay_set is only
    // allowed here, in mkw_plugin_init
    IngameHost host = {
        .api = api,
        .request = ingame_request,
        .log = ingame_log,
        .now_ms = ingame_now,
        .sleep_ms = ingame_sleep,
        .thread_start = plat_thread_start,
        .page_closed = ingame_page_closed,
        .shortcut = ingame_shortcut,
    };
    ingame_install(&host);
    if (!plat_thread_start(overlay_main, NULL)) mkwmp_log("overlay: off: no thread");
}
