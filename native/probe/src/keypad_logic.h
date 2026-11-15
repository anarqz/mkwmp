// The numeric keypad's state machine and layout, free of Win32 / ImGui so native tests can drive it
// (native/probe/tests/test_keypad.c). keypad.c owns drawing, input devices and the recomp hooks.
#ifndef MKWMP_KEYPAD_LOGIC_H
#define MKWMP_KEYPAD_LOGIC_H

#include <stddef.h>
#include <stdint.h>
#include <string.h>

enum { KP_COLS = 3, KP_ROWS = 5, KP_MAX_DIGITS = 12, KP_KEYS = 13 };
// Keys: 0..9 are the digits.
enum { KP_KEY_BACKSPACE = 10, KP_KEY_OK = 11, KP_KEY_CANCEL = 12 };
// One frame's input, edges only.
enum {
    KP_UP = 1 << 0, KP_DOWN = 1 << 1, KP_LEFT = 1 << 2, KP_RIGHT = 1 << 3,
    KP_ACCEPT = 1 << 4,    // press the focused key (pad A / Space)
    KP_BACK = 1 << 5,      // delete a digit, or cancel when there is none (pad B)
    KP_CONFIRM = 1 << 6,   // OK from anywhere (pad Start / Enter)
    KP_ESCAPE = 1 << 7,    // cancel from anywhere (Esc)
    KP_DELETE = 1 << 8,    // delete a digit, never cancels (Backspace)
};
enum { KP_OPEN = 0, KP_DONE_OK = 1, KP_DONE_CANCEL = -1 };

typedef struct {
    char digits[KP_MAX_DIGITS + 1];
    int n, min, max;
    int row, col;     // focus; row 4 is the single Cancel key
    int result;       // KP_OPEN / KP_DONE_*
} Keypad;

// Grid:  1 2 3 / 4 5 6 / 7 8 9 / <- 0 OK / Cancel
static inline int kp_key_at(int row, int col) {
    if (row < 0 || row >= KP_ROWS || col < 0 || col >= KP_COLS) return -1;
    if (row < 3) return 1 + row * 3 + col;
    if (row == 3) return col == 0 ? KP_KEY_BACKSPACE : col == 1 ? 0 : KP_KEY_OK;
    return KP_KEY_CANCEL;
}

static inline int kp_focus_key(const Keypad *k) { return kp_key_at(k->row, k->col); }
static inline int kp_ok_enabled(const Keypad *k) { return k->n >= k->min && k->n <= k->max && k->n > 0; }

static inline void kp_init(Keypad *k, int min, int max, const char *prefill) {
    memset(k, 0, sizeof *k);
    if (max < 1) max = 1;
    if (max > KP_MAX_DIGITS) max = KP_MAX_DIGITS;
    if (min < 0) min = 0;
    if (min > max) min = max;
    k->min = min;
    k->max = max;
    for (const char *p = prefill; p && *p && k->n < max; p++)
        if (*p >= '0' && *p <= '9') k->digits[k->n++] = *p;
    k->row = 1;   // "5": the centre of the pad
    k->col = 1;
    if (k->n == k->max) { k->row = 3; k->col = 2; }
}

static inline void kp_press(Keypad *k, int key) {
    if (k->result != KP_OPEN) return;
    if (key >= 0 && key <= 9) {
        if (k->n < k->max) {
            k->digits[k->n++] = (char)('0' + key);
            k->digits[k->n] = 0;
            if (k->n == k->max) { k->row = 3; k->col = 2; }   // complete: the focus goes to OK
        }
    } else if (key == KP_KEY_BACKSPACE) {
        if (k->n > 0) k->digits[--k->n] = 0;
    } else if (key == KP_KEY_OK) {
        if (kp_ok_enabled(k)) k->result = KP_DONE_OK;
    } else if (key == KP_KEY_CANCEL) {
        k->result = KP_DONE_CANCEL;
    }
}

static inline void kp_input(Keypad *k, int ev) {
    if (k->result != KP_OPEN) return;
    if (ev & KP_UP) k->row = (k->row + KP_ROWS - 1) % KP_ROWS;
    if (ev & KP_DOWN) k->row = (k->row + 1) % KP_ROWS;
    if (k->row < KP_ROWS - 1) {   // the Cancel row is one key: left / right keep the column for the way back up
        if (ev & KP_LEFT) k->col = (k->col + KP_COLS - 1) % KP_COLS;
        if (ev & KP_RIGHT) k->col = (k->col + 1) % KP_COLS;
    }
    if (ev & KP_ESCAPE) { k->result = KP_DONE_CANCEL; return; }
    if (ev & KP_CONFIRM) { kp_press(k, KP_KEY_OK); return; }
    if (ev & KP_DELETE) kp_press(k, KP_KEY_BACKSPACE);
    if (ev & KP_BACK) {
        if (k->n > 0) kp_press(k, KP_KEY_BACKSPACE);
        else { k->result = KP_DONE_CANCEL; return; }
    }
    if (ev & KP_ACCEPT) kp_press(k, kp_focus_key(k));
}

// ---- which keyboards the keypad replaces ---------------------------------------------------------------------------

static inline int kp_is_letter(uint16_t c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c >= 0xC0; }

// The header names an ID ("Insira ID do grupo", "Enter Group ID", ...): "ID" as a word of its own.
static inline int kp_title_mentions_id(const uint16_t *t, size_t max) {
    for (size_t i = 0; i + 1 < max && t[i] && t[i + 1]; i++) {
        if (t[i] != 'I' || t[i + 1] != 'D') continue;
        int before = i > 0 && kp_is_letter(t[i - 1]);
        int after = i + 2 < max && kp_is_letter(t[i + 2]);
        if (!before && !after) return 1;
    }
    return 0;
}

// numeric: nn::swkbd KeyboardMode Numeric. invalid_flags: nn::swkbd InvalidCharFlag (0x80 = only download-code
// characters, what the game sets for the Room ID). Name keyboards (text, long, no ID in the header) stay with the
// runtime.
static inline int kp_eligible(int numeric, uint32_t invalid_flags, int max_len, const uint16_t *title, size_t title_max) {
    if (max_len < 1 || max_len > KP_MAX_DIGITS) return 0;
    return numeric || (invalid_flags & 0x80u) || kp_title_mentions_id(title, title_max);
}

static inline size_t kp_utf16_to_utf8(const uint16_t *s, size_t max, char *out, size_t cap) {
    size_t k = 0;
    if (!cap) return 0;
    for (size_t i = 0; i < max && s[i]; i++) {
        uint32_t c = s[i];
        if (c >= 0xD800 && c < 0xDC00 && i + 1 < max && s[i + 1] >= 0xDC00 && s[i + 1] < 0xE000)
            c = 0x10000 + ((c - 0xD800) << 10) + (s[++i] - 0xDC00);
        else if (c >= 0xD800 && c < 0xE000)
            c = '?';
        size_t need = c < 0x80 ? 1 : c < 0x800 ? 2 : c < 0x10000 ? 3 : 4;
        if (k + need >= cap) break;
        if (need == 1) out[k++] = (char)c;
        else if (need == 2) { out[k++] = (char)(0xC0 | (c >> 6)); out[k++] = (char)(0x80 | (c & 0x3F)); }
        else if (need == 3) { out[k++] = (char)(0xE0 | (c >> 12)); out[k++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[k++] = (char)(0x80 | (c & 0x3F)); }
        else { out[k++] = (char)(0xF0 | (c >> 18)); out[k++] = (char)(0x80 | ((c >> 12) & 0x3F)); out[k++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[k++] = (char)(0x80 | (c & 0x3F)); }
    }
    out[k] = 0;
    return k;
}

// ---- layout (display pixels) ---------------------------------------------------------------------------------------

typedef struct { float x0, y0, x1, y1; } KpRect;
typedef struct {
    KpRect panel, title, display, hint;
    KpRect key[KP_KEYS];   // by key id
    float unit;            // key height
} KpLayout;

static inline void kp_layout(float w, float h, KpLayout *l) {
    float u = h * 0.095f;
    if (u * 6.0f > w) u = w / 6.0f;   // portrait / very narrow targets
    float kw = u * 1.5f, gap = u * 0.14f, pad = u * 0.35f;
    float gw = 3 * kw + 2 * gap, pw = gw + 2 * pad;
    float th = u * 0.6f, dh = u * 0.9f, ch = u * 0.8f, hh = u * 0.45f;
    float ph = pad + th + gap + dh + gap + 4 * u + 3 * gap + gap + ch + gap + hh + pad;
    float x = (w - pw) * 0.5f, y = (h - ph) * 0.5f;
    l->unit = u;
    l->panel = (KpRect){x, y, x + pw, y + ph};
    float gx = x + pad, cy = y + pad;
    l->title = (KpRect){gx, cy, gx + gw, cy + th};
    cy += th + gap;
    l->display = (KpRect){gx, cy, gx + gw, cy + dh};
    cy += dh + gap;
    for (int r = 0; r < 4; r++) {
        for (int c = 0; c < KP_COLS; c++) {
            float kx = gx + (float)c * (kw + gap);
            l->key[kp_key_at(r, c)] = (KpRect){kx, cy, kx + kw, cy + u};
        }
        cy += u + gap;
    }
    l->key[KP_KEY_CANCEL] = (KpRect){gx, cy, gx + gw, cy + ch};
    cy += ch + gap;
    l->hint = (KpRect){gx, cy, gx + gw, cy + hh};
}

// The key under a point, or -1.
static inline int kp_hit(const KpLayout *l, float x, float y) {
    for (int i = 0; i < KP_KEYS; i++)
        if (x >= l->key[i].x0 && x < l->key[i].x1 && y >= l->key[i].y0 && y < l->key[i].y1) return i;
    return -1;
}

// Move the focus to a key (mouse hover).
static inline void kp_focus(Keypad *k, int key) {
    for (int r = 0; r < KP_ROWS; r++)
        for (int c = 0; c < KP_COLS; c++)
            if (kp_key_at(r, c) == key) {
                k->row = r;
                if (r < KP_ROWS - 1) k->col = c;
                return;
            }
}

#endif
