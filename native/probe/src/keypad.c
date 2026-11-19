// On-screen numeric keypad for nn::swkbd::ShowKeyboard (research/docs/SWKBD-KEYPAD.md).
//
// mkw-recomp's ov_show_keyboard builds a request, then (unless MKW_SWKBD_ANSWER answers it) calls its text-entry
// stub `text_entry_run(req, out, cap, &len)`, which today only logs "no text entry window on this host yet" and
// returns the default text. We detour that stub: for a digits-only request the calling guest thread waits here
// (as the real, modal swkbd would; ov_show_keyboard already left its core and blocked the game's input around the
// call) while the keypad is drawn into the recomp's own Dear ImGui overlay frame on the present thread:
//   _Z17menu_notice_shownv   -> true while the keypad is open, so menu_overlay_frame runs a frame
//   _Z16menu_notice_drawff   -> runs between ImGui::NewFrame and ImGui::Render: we draw after the original
// Drawing uses draw-list primitives only (no ImGui windows / navigation / IO), input is read directly: the game's
// SDL3 gamepads, GetAsyncKeyState, and the mouse over the game window.
#include "keypad.h"

#include "detour.h"
#include "keypad_logic.h"
#include "log.h"
#include "rtapi.h"

#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

void *coff_symbol(const char *name);

// ---- recomp side (mkw.exe COFF symbols; MKW Recomp 0.1.10) ---------------------------------------------------------

// ov_show_keyboard.req, as text_entry_run receives it.
enum {
    REQ_TITLE = 0x000,      // char16[0x101]: header, else guide, else sub text
    REQ_INITIAL = 0x202,    // char16[501]
    REQ_MAX = 0x5f0,        // int32 units
    REQ_MIN = 0x5f4,
    REQ_PASSWORD = 0x5f8,
    REQ_NUMERIC = 0x5fc,    // KeyboardMode == Numeric
    REQ_FLAGS = 0x600,      // InvalidCharFlag ("disabled keys" in the runtime's log)
};

typedef int (*TextEntryFn)(const uint8_t *req, uint16_t *out, size_t cap, size_t *len);   // 0 = cancelled
typedef _Bool (*NoticeShownFn)(void);
typedef void (*NoticeDrawFn)(float width, float scale);

// The functions' first bytes, as disassembled; the first *_STEAL of them (whole, position-independent
// instructions) move to the trampoline.
static const uint8_t kEntrySig[] = {0x41, 0x57, 0x41, 0x56, 0x56, 0x57, 0x53,      // push r15; r14; rsi; rdi; rbx
                                    0x48, 0x83, 0xEC, 0x20, 0x4C, 0x89, 0xCE};      // sub rsp,0x20; mov rsi,r9
static const uint8_t kShownSig[] = {0x56, 0x48, 0x83, 0xEC, 0x20,                  // push rsi; sub rsp,0x20
                                    0x48, 0x8B, 0x35};                              // mov rsi,[rip+g_until]
static const uint8_t kDrawSig[] = {0x56, 0x57, 0x48, 0x81, 0xEC, 0x78, 0x01, 0x00, 0x00,   // push rsi; rdi; sub rsp,0x178
                                   0xC5, 0x78, 0x29, 0x84, 0x24};                           // vmovaps [rsp+..],xmm8
enum { ENTRY_STEAL = 7, SHOWN_STEAL = 5, DRAW_STEAL = 9 };

static TextEntryFn g_orig_entry;
static NoticeShownFn g_orig_shown;
static NoticeDrawFn g_orig_draw;

// Dear ImGui 1.91.9 inside mkw.exe (Itanium C++ ABI on Win64: `this` first, ImVec2 by const reference, an ImVec2
// result comes back in rax).
typedef struct { float x, y; } V2;
static void *(*im_foreground)(void);
static void (*im_rect_filled)(void *dl, const V2 *a, const V2 *b, uint32_t col, float rounding, int flags);
static void (*im_rect)(void *dl, const V2 *a, const V2 *b, uint32_t col, float rounding, int flags, float thickness);
static void (*im_text)(void *dl, void *font, float size, const V2 *pos, uint32_t col, const char *begin, const char *end,
                       float wrap, const void *clip);
static uint64_t (*im_calc_text)(const char *begin, const char *end, int hide_after_hashes, float wrap);
static float (*im_font_size)(void);
static void *(*im_io)(void);
enum { IO_DISPLAY_SIZE = 8 };   // ImGuiIO::DisplaySize

// ---- state ---------------------------------------------------------------------------------------------------------

static volatile LONG g_active;   // a keypad is open (read by the present thread's hooks)
static volatile LONG g_frames;   // frames drawn for the open keypad
static HANDLE g_done;            // manual-reset: the present thread finished the keypad
static SRWLOCK g_lock = SRWLOCK_INIT;
static struct {
    Keypad k;
    char title[0x101 * 3 + 1];
    int primed;                  // the first frame only records what is already held
    uint32_t prev_levels;
    uint32_t prev_digits;
    int prev_click;
    POINT prev_mouse;
    uint32_t repeat_dir;
    ULONGLONG repeat_at;
} g;

// ---- input ---------------------------------------------------------------------------------------------------------

static HWND own_foreground(void) {
    HWND fg = GetForegroundWindow();
    DWORD pid = 0;
    if (fg) GetWindowThreadProcessId(fg, &pid);
    return pid == GetCurrentProcessId() ? fg : NULL;
}

static int key_down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

// Held inputs as KP_* bits: the game's own SDL3 gamepads (as voice_ui.c reads them) and the keyboard.
static uint32_t read_levels(HWND fg) {
    uint32_t b = 0;
    static uint32_t *(*get_pads)(int *);
    static void *(*from_id)(uint32_t);
    static _Bool (*button)(void *, int);
    static int16_t (*axis)(void *, int);
    static void (*sdl_free)(void *);
    static int resolved;
    if (!resolved) {
        resolved = 1;
        HMODULE sdl = GetModuleHandleA("SDL3.dll");
        if (sdl) {
            *(FARPROC *)&get_pads = GetProcAddress(sdl, "SDL_GetGamepads");
            *(FARPROC *)&from_id = GetProcAddress(sdl, "SDL_GetGamepadFromID");
            *(FARPROC *)&button = GetProcAddress(sdl, "SDL_GetGamepadButton");
            *(FARPROC *)&axis = GetProcAddress(sdl, "SDL_GetGamepadAxis");
            *(FARPROC *)&sdl_free = GetProcAddress(sdl, "SDL_free");
        }
    }
    if (get_pads && from_id && button) {
        int n = 0;
        uint32_t *ids = get_pads(&n);
        for (int i = 0; ids && i < n; i++) {
            void *pad = from_id(ids[i]);   // only gamepads the game opened
            if (!pad) continue;
            if (button(pad, 11)) b |= KP_UP;        // SDL_GAMEPAD_BUTTON_DPAD_UP
            if (button(pad, 12)) b |= KP_DOWN;
            if (button(pad, 13)) b |= KP_LEFT;
            if (button(pad, 14)) b |= KP_RIGHT;
            if (button(pad, 0)) b |= KP_ACCEPT;     // SOUTH
            if (button(pad, 1)) b |= KP_BACK;       // EAST
            if (button(pad, 6)) b |= KP_CONFIRM;    // START
            if (axis) {
                int x = axis(pad, 0), y = axis(pad, 1);   // SDL_GAMEPAD_AXIS_LEFTX / LEFTY
                enum { DEAD = 16000 };
                if (x < -DEAD) b |= KP_LEFT;
                if (x > DEAD) b |= KP_RIGHT;
                if (y < -DEAD) b |= KP_UP;
                if (y > DEAD) b |= KP_DOWN;
            }
        }
        if (ids && sdl_free) sdl_free(ids);
    }
    if (fg) {
        if (key_down(VK_UP)) b |= KP_UP;
        if (key_down(VK_DOWN)) b |= KP_DOWN;
        if (key_down(VK_LEFT)) b |= KP_LEFT;
        if (key_down(VK_RIGHT)) b |= KP_RIGHT;
        if (key_down(VK_SPACE)) b |= KP_ACCEPT;
        if (key_down(VK_RETURN)) b |= KP_CONFIRM;
        if (key_down(VK_ESCAPE)) b |= KP_ESCAPE;
        if (key_down(VK_BACK)) b |= KP_DELETE;
    }
    return b;
}

static uint32_t read_digits(HWND fg) {
    uint32_t d = 0;
    if (!fg) return 0;
    for (int i = 0; i < 10; i++)
        if (key_down('0' + i) || key_down(VK_NUMPAD0 + i)) d |= 1u << i;
    return d;
}

// One frame of input into the keypad (g_lock held). w, h: the overlay's display size.
static void keypad_input(const KpLayout *l, float w, float h) {
    HWND fg = own_foreground();
    uint32_t levels = read_levels(fg), digits = read_digits(fg);
    int click = fg && key_down(VK_LBUTTON);
    POINT screen = {0, 0};
    int have_mouse = fg && GetCursorPos(&screen);
    ULONGLONG now = GetTickCount64();

    if (!g.primed) {   // whatever opened the keyboard is still held: not a press
        g.primed = 1;
        g.prev_levels = levels;
        g.prev_digits = digits;
        g.prev_click = click;
        g.prev_mouse = screen;
        g.repeat_dir = 0;
        return;
    }

    uint32_t pressed = levels & ~g.prev_levels;
    enum { DIRS = KP_UP | KP_DOWN | KP_LEFT | KP_RIGHT };
    uint32_t dir = levels & DIRS;
    if (pressed & DIRS) {
        g.repeat_dir = dir;
        g.repeat_at = now + 380;
    } else if (dir && dir == g.repeat_dir && now >= g.repeat_at) {
        pressed |= dir;
        g.repeat_at = now + 110;
    } else if (dir != g.repeat_dir) {
        g.repeat_dir = dir;
        g.repeat_at = now + 380;
    }
    kp_input(&g.k, (int)pressed);

    uint32_t typed = digits & ~g.prev_digits;
    for (int i = 0; i < 10; i++)
        if (typed & (1u << i)) kp_press(&g.k, i);

    if (have_mouse) {
        int moved = screen.x != g.prev_mouse.x || screen.y != g.prev_mouse.y;
        POINT pt = screen;
        RECT rc;
        if (ScreenToClient(fg, &pt) && GetClientRect(fg, &rc) && rc.right > 0 && rc.bottom > 0) {
            int hit = kp_hit(l, (float)pt.x * w / (float)rc.right, (float)pt.y * h / (float)rc.bottom);
            if (hit >= 0 && (moved || (click && !g.prev_click))) kp_focus(&g.k, hit);
            if (hit >= 0 && click && !g.prev_click) kp_press(&g.k, hit);
        }
        g.prev_mouse = screen;
    }
    g.prev_levels = levels;
    g.prev_digits = digits;
    g.prev_click = click;
}

// ---- drawing -------------------------------------------------------------------------------------------------------

#define RGBA(r, g, b, a) ((uint32_t)(r) | ((uint32_t)(g) << 8) | ((uint32_t)(b) << 16) | ((uint32_t)(a) << 24))

static void fill(void *dl, const KpRect *r, uint32_t col, float rounding) {
    V2 a = {r->x0, r->y0}, b = {r->x1, r->y1};
    im_rect_filled(dl, &a, &b, col, rounding, 0);
}

static void outline(void *dl, const KpRect *r, uint32_t col, float rounding, float thickness) {
    V2 a = {r->x0, r->y0}, b = {r->x1, r->y1};
    im_rect(dl, &a, &b, col, rounding, 0, thickness);
}

// Text centred in a rectangle at `size` pixels, shrunk to fit its width.
static void label(void *dl, const KpRect *r, float size, uint32_t col, const char *s) {
    float base = im_font_size();
    if (!(base > 1.0f)) base = 13.0f;
    uint64_t packed = im_calc_text(s, NULL, 0, -1.0f);
    float tw, th;
    memcpy(&tw, &packed, 4);
    memcpy(&th, (const char *)&packed + 4, 4);
    float avail = r->x1 - r->x0;
    if (tw * size / base > avail && tw > 0.0f) size = avail * base / tw;
    tw *= size / base;
    th *= size / base;
    V2 pos = {(r->x0 + r->x1 - tw) * 0.5f, (r->y0 + r->y1 - th) * 0.5f};
    im_text(dl, NULL, size, &pos, col, s, NULL, 0.0f, NULL);
}

static void keypad_draw(const KpLayout *l, float w, float h) {
    void *dl = im_foreground();
    if (!dl) return;
    const Keypad *k = &g.k;
    float u = l->unit, round = u * 0.12f;
    const uint32_t ink = RGBA(245, 245, 245, 255), dim_ink = RGBA(150, 150, 150, 255), accent = RGBA(255, 170, 30, 255);

    KpRect screen = {0, 0, w, h};
    fill(dl, &screen, RGBA(0, 0, 0, 150), 0);
    fill(dl, &l->panel, RGBA(24, 26, 32, 245), round * 1.5f);
    outline(dl, &l->panel, RGBA(90, 96, 110, 255), round * 1.5f, u * 0.03f);
    label(dl, &l->title, u * 0.42f, ink, g.title);

    // one box per digit
    float gap = u * 0.1f, total = l->display.x1 - l->display.x0;
    float bw = (total - gap * (float)(k->max - 1)) / (float)k->max;
    if (bw > u * 0.8f) bw = u * 0.8f;
    float x = l->display.x0 + (total - (bw * (float)k->max + gap * (float)(k->max - 1))) * 0.5f;
    for (int i = 0; i < k->max; i++) {
        KpRect box = {x, l->display.y0, x + bw, l->display.y1};
        fill(dl, &box, RGBA(10, 11, 14, 255), round);
        outline(dl, &box, i == k->n ? accent : RGBA(70, 75, 88, 255), round, u * (i == k->n ? 0.04f : 0.02f));
        if (i < k->n) {
            char d[2] = {k->digits[i], 0};
            label(dl, &box, u * 0.6f, ink, d);
        }
        x += bw + gap;
    }

    static const char *const kText[KP_KEYS] = {"0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "<-", "OK", "Cancel"};
    int focus = kp_focus_key(k);
    for (int i = 0; i < KP_KEYS; i++) {
        int disabled = (i == KP_KEY_OK && !kp_ok_enabled(k)) || (i <= 9 && k->n >= k->max) ||
                       (i == KP_KEY_BACKSPACE && k->n == 0);
        uint32_t bg = i == KP_KEY_OK && !disabled ? RGBA(40, 110, 60, 255) : RGBA(52, 56, 66, 255);
        fill(dl, &l->key[i], bg, round);
        if (i == focus) outline(dl, &l->key[i], accent, round, u * 0.06f);
        label(dl, &l->key[i], u * (i == KP_KEY_CANCEL ? 0.38f : 0.5f), disabled ? dim_ink : ink, kText[i]);
    }
    label(dl, &l->hint, u * 0.3f, dim_ink, "A: select    B: delete    START: OK    |    0-9  Enter  Esc");
}

// Present thread, between ImGui::NewFrame and ImGui::Render.
static void keypad_frame(float width) {
    float w = width, h = 0.0f;
    const float *size = (const float *)((const char *)im_io() + IO_DISPLAY_SIZE);
    if (size[0] > 1.0f && size[1] > 1.0f) { w = size[0]; h = size[1]; }
    if (!(w > 1.0f)) return;
    if (!(h > 1.0f)) h = w * 9.0f / 16.0f;
    KpLayout l;
    kp_layout(w, h, &l);

    AcquireSRWLockExclusive(&g_lock);
    if (g.k.result == KP_OPEN) {
        keypad_input(&l, w, h);
        keypad_draw(&l, w, h);
        InterlockedIncrement(&g_frames);
        if (g.k.result != KP_OPEN) SetEvent(g_done);
    }
    ReleaseSRWLockExclusive(&g_lock);
}

// ---- hooks ---------------------------------------------------------------------------------------------------------

static _Bool hook_notice_shown(void) {
    if (g_active) return 1;
    return g_orig_shown();
}

static void hook_notice_draw(float width, float scale) {
    g_orig_draw(width, scale);
    if (g_active) keypad_frame(width);
}

static int32_t req_i32(const uint8_t *req, size_t off) {
    int32_t v;
    memcpy(&v, req + off, 4);
    return v;
}

// Guest thread, inside nn::swkbd::ShowKeyboard. Returns nonzero with the text in out / *len, or 0 for a cancel
// (ov_show_keyboard then answers 0x29f, the swkbd cancel result).
static int hook_text_entry_run(const uint8_t *req, uint16_t *out, size_t cap, size_t *len) {
    if (!req || !out || !len) return g_orig_entry(req, out, cap, len);
    const uint16_t *title = (const uint16_t *)(const void *)(req + REQ_TITLE);
    int max = req_i32(req, REQ_MAX), min = req_i32(req, REQ_MIN);
    if (req_i32(req, REQ_PASSWORD) || (size_t)(max < 0 ? 0 : max) > cap ||
        !kp_eligible(req_i32(req, REQ_NUMERIC), (uint32_t)req_i32(req, REQ_FLAGS), max, title, 0x101) ||
        (g_rt.cfg_get && g_rt.cfg_get("MKW_SWKBD_TEST")))   // the runtime's own test switch keeps its meaning
        return g_orig_entry(req, out, cap, len);

    // a start text is kept only when it is a number (the runtime puts its default *name* there otherwise)
    char prefill[KP_MAX_DIGITS + 1] = {0};
    const uint16_t *initial = (const uint16_t *)(const void *)(req + REQ_INITIAL);
    for (int i = 0; i <= KP_MAX_DIGITS; i++) {
        if (!initial[i]) break;
        if (initial[i] < '0' || initial[i] > '9' || i == KP_MAX_DIGITS) { prefill[0] = 0; break; }
        prefill[i] = (char)initial[i];
        prefill[i + 1] = 0;
    }

    AcquireSRWLockExclusive(&g_lock);
    kp_init(&g.k, min, max, prefill);
    kp_utf16_to_utf8(title, 0x101, g.title, sizeof g.title);
    g.primed = 0;
    ReleaseSRWLockExclusive(&g_lock);
    ResetEvent(g_done);
    InterlockedExchange(&g_frames, 0);
    InterlockedExchange(&g_active, 1);
    mkwmp_log("keypad: open \"%s\" (%d-%d digits, thread %lu)", g.title, min, max, GetCurrentThreadId());

    // Modal, like the console's keyboard. If the overlay never gets a frame (the game is not presenting while this
    // thread waits), give the request back to the runtime rather than hang.
    DWORD start = GetTickCount();
    int drawn = 1;
    while (WaitForSingleObject(g_done, 100) != WAIT_OBJECT_0) {
        if (!g_frames && GetTickCount() - start > 3000) { drawn = 0; break; }
    }
    InterlockedExchange(&g_active, 0);

    AcquireSRWLockExclusive(&g_lock);
    int result = g.k.result, n = g.k.n;
    char digits[KP_MAX_DIGITS + 1];
    memcpy(digits, g.k.digits, sizeof digits);
    g.k.result = KP_DONE_CANCEL;   // closed: a frame still in flight draws nothing
    ReleaseSRWLockExclusive(&g_lock);

    if (result == KP_OPEN && !drawn) {
        mkwmp_log("keypad: no overlay frame in 3 s; the runtime's default answer is used");
        return g_orig_entry(req, out, cap, len);
    }
    if (result != KP_DONE_OK) {
        mkwmp_log("keypad: cancelled");
        *len = 0;
        return 0;
    }
    for (int i = 0; i < n; i++) out[i] = (uint16_t)(uint8_t)digits[i];
    *len = (size_t)n;
    mkwmp_log("keypad: OK \"%s\"", digits);
    return 1;
}

// ---- install -------------------------------------------------------------------------------------------------------

static int signature_ok(const void *fn, const uint8_t *sig, size_t n) { return fn && !memcmp(fn, sig, n); }

void keypad_install(void) {
    static int done;
    if (done) return;
    done = 1;
    char v[8] = {0};
    if (GetEnvironmentVariableA("MKWMP_KEYPAD", v, sizeof v) && v[0] == '0') {
        mkwmp_log("keypad: off (MKWMP_KEYPAD=0)");
        return;
    }
    void *entry = coff_symbol("text_entry_run");
    void *shown = coff_symbol("_Z17menu_notice_shownv");
    void *draw = coff_symbol("_Z16menu_notice_drawff");
    *(void **)&im_foreground = coff_symbol("_ZN5ImGui21GetForegroundDrawListEv");
    *(void **)&im_rect_filled = coff_symbol("_ZN10ImDrawList13AddRectFilledERK6ImVec2S2_jfi");
    *(void **)&im_rect = coff_symbol("_ZN10ImDrawList7AddRectERK6ImVec2S2_jfif");
    *(void **)&im_text = coff_symbol("_ZN10ImDrawList7AddTextEP6ImFontfRK6ImVec2jPKcS6_fPK6ImVec4");
    *(void **)&im_calc_text = coff_symbol("_ZN5ImGui12CalcTextSizeEPKcS1_bf");
    *(void **)&im_font_size = coff_symbol("_ZN5ImGui11GetFontSizeEv");
    *(void **)&im_io = coff_symbol("_ZN5ImGui5GetIOEv");
    if (!entry || !shown || !draw || !im_foreground || !im_rect_filled || !im_rect || !im_text || !im_calc_text ||
        !im_font_size || !im_io) {
        mkwmp_log("keypad: NOT installed (a runtime symbol is missing)");
        return;
    }
    // all three or none: check every prologue before the first patch
    if (!signature_ok(entry, kEntrySig, sizeof kEntrySig) || !signature_ok(shown, kShownSig, sizeof kShownSig) ||
        !signature_ok(draw, kDrawSig, sizeof kDrawSig)) {
        mkwmp_log("keypad: NOT installed (this mkw.exe has other prologues than the build the hooks were made for)");
        return;
    }
    g_done = CreateEventA(NULL, TRUE, FALSE, NULL);
    if (!g_done) return;
    // the overlay hooks are inert until a keypad opens; the entry hook goes in last, only when they are in place
    int ok = detour_install(draw, kDrawSig, DRAW_STEAL, (void *)hook_notice_draw, (void **)&g_orig_draw) &&
             detour_install(shown, kShownSig, SHOWN_STEAL, (void *)hook_notice_shown, (void **)&g_orig_shown) &&
             detour_install(entry, kEntrySig, ENTRY_STEAL, (void *)hook_text_entry_run, (void **)&g_orig_entry);
    mkwmp_log("keypad: hooks %s (text_entry_run %p, menu_notice_shown %p, menu_notice_draw %p)",
              ok ? "installed" : "NOT fully installed; the keypad stays off", entry, shown, draw);
}
