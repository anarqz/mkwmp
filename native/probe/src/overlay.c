// The console-style overlay, the mod's part (overlay.h; launcher/docs/OVERLAY.md).
//
// Threads: the pad filter runs on the game's input thread (plugin API 4), the picture callback on the present thread;
// both only record and wake the overlay's own thread, which does the waiting and the talking to the launcher.
//
// Plugin API 3 (mkw-recomp 0.1.15-mkwmp as released) has no filter, no picture and no way to keep mkw-recomp's menu
// shut, so there the thread watches the game's SDL3 pads itself: Guide and the Select hold open the overlay, Select
// still reaches the game, and the trigger mkw-recomp's own menu uses (MKW_MENU_SHORTCUT: Select+Start, or Guide) is
// left to it. The game reads nothing while the launcher has the focus because SDL drops pad presses of a window
// without the keyboard focus (SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS is off by default, and mkw-recomp leaves it).
#include "plat_net.h"   // winsock2.h before windows.h

#include "overlay.h"

#include "config.h"
#include "log.h"
#include "mkw_plugin.h"
#include "plat.h"

#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// ---- pure ----------------------------------------------------------------------------------------------------------

static int32_t iabs32(int32_t v) { return v < 0 ? -v : v; }

OverlayTrigger overlay_gate(OverlayGate *g, OverlayPad *pad, uint64_t now_ms, int armed, int open) {
    if (!armed) {
        memset(g, 0, sizeof *g);
        return OVERLAY_BY_NONE;
    }
    OverlayTrigger t = OVERLAY_BY_NONE;
    int guide = pad->guide != 0;
    if (guide && !g->guide_was) t = OVERLAY_BY_GUIDE;
    g->guide_was = guide;

    int minus = (pad->buttons & OVERLAY_BTN_MINUS) != 0, plus = (pad->buttons & OVERLAY_BTN_PLUS) != 0;
    if (minus) {
        if (!g->select_held) {
            g->select_held = 1;
            g->select_fired = 0;
            g->select_since = now_ms;
        }
        if (!g->select_fired) {
            if (plus) {
                g->select_fired = 1;
                if (!t) t = OVERLAY_BY_CHORD;
            } else if (now_ms - g->select_since >= OVERLAY_HOLD_MS) {
                g->select_fired = 1;
                if (!t) t = OVERLAY_BY_HOLD;
            }
        }
    } else if (g->select_held) {
        g->select_held = 0;
        if (!g->select_fired) g->replay_until = now_ms + OVERLAY_REPLAY_MS;   // a normal press: the game gets it, late
    }

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
    pad->guide = 0;
    pad->buttons &= ~(uint64_t)OVERLAY_BTN_MINUS;
    if (!minus && now_ms < g->replay_until) pad->buttons |= OVERLAY_BTN_MINUS;
    return t;
}

void overlay_gate_closed(OverlayGate *g) {
    g->idle = 1;
    g->replay_until = 0;
    if (g->select_held) g->select_fired = 1;   // what is held now is not a press for the game
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
    if ((v = json_value(json, "action"))) out->menu = !strncmp(v, "\"menu\"", 6);
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
static int g_filtered;       // the runtime calls pad_filter (plugin API 4)

static PlatEvent *g_wake;    // a trigger: the thread looks now
static PlatEvent *g_framed;  // the picture arrived
static volatile int g_armed, g_open, g_trigger, g_closed, g_idle;
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

// Plugin API 4: each read of a player's pad, before mkw-recomp's menu shortcut and the game. Does not wait.
static void pad_filter(MkwPad *pad, void *user) {
    (void)user;
    int armed = plat_load(&g_armed), open = plat_load(&g_open);
    if (pad->player != 0) {   // players 2-8 idle with player 1: nobody drives blind behind the overlay
        if (armed && (open || plat_load(&g_idle))) pad->buttons = 0, pad->lx = pad->ly = pad->rx = pad->ry = 0, pad->guide = 0;
        return;
    }
    if (plat_xchg(&g_closed, 0)) overlay_gate_closed(&g_gate);
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
                        uint32_t timeout_ms, char *buf, size_t cap, const char **answer) {
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
                     len ? "Content-Type: application/octet-stream\r\n" : "", (unsigned long long)len);
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
static int request(const char *method, const char *path, const void *body, size_t len, uint32_t timeout_ms, char *buf,
                   size_t cap, const char **answer) {
    buf[0] = 0;
    *answer = buf;
    const MkwmpConfig *cfg = endpoint(0);
    int status = cfg ? request_once(cfg, method, path, body, len, timeout_ms, buf, cap, answer) : 0;
    if (status == 0 || status == 401) {   // the launcher may have restarted (new token, same port)
        if (!(cfg = endpoint(1))) return -1;
        status = request_once(cfg, method, path, body, len, timeout_ms, buf, cap, answer);
    }
    return status;
}

// The overlay host: the MKWMP launcher started by the mod, without a window until the trigger, for players who start
// the game without it (launcher/docs/OVERLAY.md, "Without the launcher"). It writes kSessionFile and leaves with the
// game.
static uint64_t g_host_started;

static int file_there(const char *path) {
    char two[2];
    return path[0] && plat_read_file(path, two, sizeof two) > 0;
}

// "exe": "<path>" of %LOCALAPPDATA%\mkwmp\launcher.json, which the launcher writes at every start (JSON escapes \\ and
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
    // 3. the launcher's own record for this user, also for players who only ever opened it once
    char dir[400], path[520], text[2048];
    if (plat_getenv("LOCALAPPDATA", dir, sizeof dir) &&
        snprintf(path, sizeof path, "%s/mkwmp/launcher.json", dir) < (int)sizeof path) {
        long n = plat_read_file(path, text, sizeof text - 1);
        if (n > 0) {
            text[n] = 0;
            if (record_exe(text, g_host_exe, sizeof g_host_exe) && file_there(g_host_exe)) return 1;
        }
    }
    // 4. a launcher put into the mod's folder
    if (plat_home_path(g_host_exe, sizeof g_host_exe, "mkwmp-launcher.exe") && file_there(g_host_exe)) return 1;
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
enum { SDL_BTN_BACK = 4, SDL_BTN_GUIDE = 5, SDL_BTN_START = 6 };   // SDL_GamepadButton

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
    }
    if (ids) g_sdl.free(ids);
    OverlayTrigger t = overlay_gate(&g_watch, &p, plat_ms(), 1, plat_load(&g_open));
    if (!t) return;
    // mkw-recomp's menu opens on one of the two and cannot be kept shut from here: that one stays its own
    const char *own = g_api->cfg_get ? g_api->cfg_get("MKW_MENU_SHORTCUT") : NULL;
    int own_guide = own && !strcmp(own, "guide");
    if ((t == OVERLAY_BY_GUIDE && own_guide) || (t == OVERLAY_BY_CHORD && !own_guide)) return;
    trigger(t);
}

// ---- one opening: the picture, the request, the wait for "back to the game"
static void closed(void) {
    plat_store(&g_closed, 1);
    overlay_gate_closed(&g_watch);
    plat_store(&g_open, 0);
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
    // menu=1: this runtime opens its own menu when asked (plugin API 4), so the launcher may offer it
    const char *menu = g_menu_open ? "&menu=1" : "";
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
        } else if (++fails >= 3) {   // the launcher went away: the game is the player's again
            mkwmp_log("overlay: the launcher stopped answering (HTTP %d): back to the game", status);
            st.open = 0, st.menu = 0;
        } else {
            plat_sleep_ms(300);
        }
        int again = plat_xchg(&g_trigger, 0);
        if (st.open && again) {   // the game has the focus again with the overlay still up: bring the launcher back
            OverlayState re;
            if (ask_open((OverlayTrigger)again, 0, &re) == 200) st = re;
        }
    }
    closed();
    plat_xchg(&g_trigger, 0);
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
        if (t) session((OverlayTrigger)t);
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
                  "launcher_path, launcher_exe of mkwmp.toml, %%LOCALAPPDATA%%/mkwmp/launcher.json, the mod's folder)");
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
    mkwmp_log("overlay: %s; %s", g_filtered ? "the pad goes through the mod (plugin API 4)" : "plugin API 3: watching only",
              g_hosted ? "the mod starts the launcher as its overlay host" : "the launcher that started the game answers");
    if (!plat_thread_start(overlay_main, NULL)) mkwmp_log("overlay: off: no thread");
}
