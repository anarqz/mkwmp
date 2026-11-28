// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-only; see LICENSE and NOTICE.
// The in-game overlay page (plugin API 5): the MKWMP launcher's overlay page drawn in the game's own frames
// (docs/contracts/LAUNCHER_API.md, "In-game overlay (v1)"). The game keeps running, the screen, the focus and its full
// screen; while the page is open the runtime hands the pad, the keyboard and the mouse to it (overlay_capture) and the
// game reads idle input.
//
// Ported from Infinity's overlay (overlay/src/v4/overlay_v4.cpp), close to line for line where MKWMP has the same thing:
// - The look is the launcher's overlay page (Settings' shape: the entries at the left, the pane at the right, the hint
//   bar), in its stage units (1080 high) scaled to the picture: the same metrics, colours, Nunito, line icons and the
//   focus card (c:FocusRing: the turning blue / cyan / violet / pink border).
// - The navigation is the launcher's FocusNav: the D-pad / left stick (or the arrows) move to the nearest stop that
//   way, A (Enter) activates, B (Esc) goes back: the top layer closes, else back to the list, else back to the game.
// - The mouse, as on the launcher's page: the cursor (drawn here) shows once the mouse moves and hides again with the
//   pad; Brush.Hover under it; a tap (pressed and let go on the same stop, never a drag) focuses it and does what A
//   does; a tap on a hint presses its button; a tap beside a modal or a column dismisses it; the wheel and a drag
//   scroll the viewer under it.
// - The column: a player of the lobby, half the screen wide over the dimmed game, sliding up from below the screen to
//   its top edge (and back down on B), the content on top and the page's rule and hint bar at the bottom.
// MKWMP's own: the panes are Game and Lobby (the lobby's players as rows: the renamed flag and every name used, the
// player's own row; Report and Copy Report in the player's column). No friends, no invites, no text field.
//
// Threads: everything here is the present thread's, except OpenPage / ClosePage / PageOpen and SetView / View.
#include "internal.h"
#include "kit.h"

#include <algorithm>
#include <atomic>
#include <cfloat>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <functional>
#include <iterator>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "imgui.h"

namespace mkwmp::ov {

namespace {

// ================================================================ the launcher's answer (the view thread)
std::mutex g_vlock;
std::shared_ptr<const OverlayView> g_view; // the last answer read (null: none, or the launcher stopped answering)

// ================================================================ input, queued for the frame (the present thread)
struct Input {
    enum Kind : uint8_t { NavIn, PointerIn } kind;
    int value;
    bool keyboard;
    Pointer pointer = Pointer::Move; // PointerIn: what the mouse did, at (x, y) (the picture's fraction; kNoPoint: where it was)
    float x = 0, y = 0;
};
std::vector<Input> g_inputs;
std::atomic<bool> g_open{false};

// ================================================================ the page's state (the present thread)
enum Area : uint8_t { kList, kPane, kModal };
enum class Column : uint8_t { None, Card };

struct Stop {
    std::string id;
    float x0 = 0, y0 = 0, x1 = 0, y1 = 0; // stage units, as drawn
    float vy0 = -FLT_MAX, vy1 = FLT_MAX;  // what of it can be seen (a scroll viewer's clip): the mouse reaches only that
    Area area = kPane;
    std::function<void()> activate;
};

// The mouse's places that are not stops: a hint of the hint bar (a button for the mouse, never a focus stop), a scroll viewer.
struct HintBox {
    float x0, y0, x1, y1;
    char button;
};
struct RegionBox {
    std::string key;
    float x0, y0, x1, y1, max; // max: how far it scrolls
};
// What the player touched last (the launcher's InputKind): the pad hides the cursor, the keyboard and the mouse show it and key caps.
enum class Hands : uint8_t { Gamepad, Keyboard, Mouse };

struct Modal {
    bool open = false;
    std::string title, body;
    std::vector<std::pair<std::string, std::function<void()>>> actions;
    std::string bottom;
    std::function<void()> on_bottom;
    std::string back_focus; // where the focus goes when it closes
};

struct Scroll {
    float offset = 0, target = 0;
};

struct Ui {
    bool was_open = false;
    uint64_t changed_at = 0;
    std::string entry = "game";
    std::string focus = "game.back";
    uint64_t focus_at = 0;
    std::map<std::string, std::string> remembered; // the pane's last focus, per entry (FocusNav.IsGroup)
    // the column (a player of the lobby): over the dimmed game, sliding up from below the screen to its top edge
    Column column = Column::None;
    bool column_closing = false; // sliding back down (no stops; then gone, and the page is back)
    uint64_t column_at = 0;      // when it began to slide (in or out)
    float column_top = 0;        // where its top is drawn now (stage units; 0: at the screen's top edge)
    float column_x0 = 0, column_x1 = 0; // its sides (a tap beside it is B, as beside a modal)
    std::string card;            // the column's player (the launcher's handle, "p5")
    std::string column_focus;    // the page's focus to come back to
    Modal modal;
    bool keyboard = false; // the last input was not the pad (the keyboard or the mouse): key caps in the hints
    bool pad_present = false;
    Hands hands = Hands::Gamepad;
    std::map<std::string, Scroll> scroll;
    std::vector<Stop> stops; // this frame's (the next frame's navigation reads them)
    std::string shown;       // the pane the launcher was told
    float ring_angle = 0;
    // the mouse (stage units): where it is, the press under way (a tap acts when it is let go, a drag scrolls and never clicks)
    bool mouse_known = false, pressed = false, dragged = false;
    float mx = 0, my = 0, moved_x = 0, moved_y = 0, press_x = 0, press_y = 0, drag_y = 0;
    std::string press_id, drag_region, hover; // press_id "@scrim": beside the modal
    char press_hint = 0, hover_hint = 0;
    uint64_t scrolled_at = 0; // the wheel or a drag moved a viewer: the focus no longer pulls it back until it moves
    std::vector<HintBox> hints;
    std::vector<RegionBox> regions;
    float modal_x0 = 0, modal_y0 = 0, modal_x1 = 0, modal_y1 = 0;
};
Ui g_ui;

// the game's own picture (core.cpp: the runtime's, or a still taken as the page opens)
uint32_t g_game_texture = 0;
unsigned g_game_w = 0, g_game_h = 0;

// ================================================================ drawing (stage units, 1080 high, to pixels)
struct Paint {
    ImDrawList *dl = nullptr;
    float s = 1, alpha = 1;
    float W = 1920, H = 1080;
    OverlayPalette pal;
} g_p;

ImVec2 P(float x, float y) { return ImVec2(x * g_p.s, y * g_p.s); }
ImU32 C(uint32_t argb, float a = 1) { return Col(argb, a * g_p.alpha); }

float TextW(Weight w, float em, const std::string &t) {
    if (t.empty()) return 0;
    return FontFor(w, em)->CalcTextSizeA(Px(em, g_p.s), FLT_MAX, 0, t.c_str()).x / g_p.s;
}

void Text(Weight w, float em, float x, float y, uint32_t col, const std::string &t, float a = 1, const char *end = nullptr) {
    if (t.empty()) return;
    g_p.dl->AddText(FontFor(w, em), Px(em, g_p.s), P(x, y), C(col, a), t.c_str(), end);
}

// One line, cut with "…" to fit `max` units.
void TextFit(Weight w, float em, float x, float y, uint32_t col, const std::string &t, float max, float a = 1) {
    ImFont *f = FontFor(w, em);
    Text(w, em, x, y, col, Trim(f, Px(em, g_p.s), t, max * g_p.s), a);
}

std::vector<std::string> Lines(Weight w, float em, const std::string &t, float max) {
    std::vector<std::string> out;
    if (t.empty()) return out;
    for (const auto &line : Wrap(FontFor(w, em), Px(em, g_p.s), t, max * g_p.s)) out.emplace_back(line.first, line.second);
    return out;
}

// Wrapped text, each line `line_h` high (WPF's LineHeight: the text at the top of its line); its height.
float TextWrap(Weight w, float em, float x, float y, uint32_t col, const std::string &t, float max, float line_h, float a = 1) {
    const auto lines = Lines(w, em, t, max);
    for (size_t i = 0; i < lines.size(); ++i) Text(w, em, x, y + static_cast<float>(i) * line_h, col, lines[i], a);
    return static_cast<float>(lines.size()) * line_h;
}

void RectFill(float x0, float y0, float x1, float y1, uint32_t col, float r = 0, float a = 1) {
    g_p.dl->AddRectFilled(P(x0, y0), P(x1, y1), C(col, a), r * g_p.s);
}

// WPF's Border: the outline inside the edge.
void RectLine(float x0, float y0, float x1, float y1, uint32_t col, float r, float w, float a = 1) {
    const float h = w / 2;
    g_p.dl->AddRect(P(x0 + h, y0 + h), P(x1 - h, y1 - h), C(col, a), std::max(0.0f, r - h) * g_p.s, 0, w * g_p.s);
}

void Disc(float cx, float cy, float r, uint32_t col, float a = 1) { g_p.dl->AddCircleFilled(P(cx, cy), r * g_p.s, C(col, a), 64); }

void Icon(const std::string &name, float x, float y, float size, uint32_t col, float stroke = 2.0f, float a = 1) {
    DrawIcon(g_p.dl, name, P(x, y), size * g_p.s, C(col, a), stroke);
}

// The game's live picture in a box, cropped to fill it (WPF's UniformToFill) with rounded corners; false when there is none yet.
bool GamePicture(float x0, float y0, float x1, float y1, float radius) {
    if (!g_game_texture || !g_game_w || !g_game_h) return false;
    const float box = (x1 - x0) / (y1 - y0), pic = static_cast<float>(g_game_w) / static_cast<float>(g_game_h);
    ImVec2 uv0(0, 0), uv1(1, 1);
    if (pic > box) { const float keep = box / pic; uv0.x = (1 - keep) / 2; uv1.x = 1 - uv0.x; }
    else if (pic < box) { const float keep = pic / box; uv0.y = (1 - keep) / 2; uv1.y = 1 - uv0.y; }
    g_p.dl->AddImageRounded(static_cast<ImTextureID>(g_game_texture), P(x0, y0), P(x1, y1), uv0, uv1, C(0xFFFFFFFF), radius * g_p.s);
    return true;
}

// ---------------------------------------------------------------- the focus card (c:FocusRing)
// The gradient of the ring (blue, cyan, blue, violet, pink, blue) over the card's box, turned once every 5 s.
ImU32 RingColour(float u, float v, float alpha) {
    const float a = g_ui.ring_angle, c = std::cos(a), s = std::sin(a);
    const float x = u - 0.5f, y = v - 0.5f;
    const float bx = x * c + y * s + 0.5f, by = -x * s + y * c + 0.5f; // the brush's own point (its RotateTransform undone)
    float t = (bx + by) / 2;
    t = t < 0 ? 0 : t > 1 ? 1 : t;
    struct StopC { float at; float r, g, b; };
    static const StopC stops[] = {{0, 0x2F, 0x74, 0xFF}, {0.22f, 0x58, 0xD3, 0xFF}, {0.42f, 0x2F, 0x74, 0xFF},
                                  {0.62f, 0x9A, 0x6B, 0xFF}, {0.82f, 0xFF, 0x8B, 0xC2}, {1, 0x2F, 0x74, 0xFF}};
    size_t i = 0;
    while (i + 2 < std::size(stops) && t > stops[i + 1].at) ++i;
    const float k = (t - stops[i].at) / (stops[i + 1].at - stops[i].at);
    const float r = stops[i].r + (stops[i + 1].r - stops[i].r) * k, g = stops[i].g + (stops[i + 1].g - stops[i].g) * k,
                b = stops[i].b + (stops[i + 1].b - stops[i].b) * k;
    return IM_COL32(static_cast<int>(r), static_cast<int>(g), static_cast<int>(b), static_cast<int>(255 * alpha));
}

void GradientStroke(float x0, float y0, float x1, float y1, float r, float width, float alpha, float bx0, float by0, float bx1, float by1) {
    ImDrawList *dl = g_p.dl;
    const int start = dl->VtxBuffer.Size;
    dl->AddRect(P(x0, y0), P(x1, y1), IM_COL32_WHITE, std::max(0.0f, r) * g_p.s, 0, width * g_p.s);
    const float w = std::max(1.0f, (bx1 - bx0) * g_p.s), h = std::max(1.0f, (by1 - by0) * g_p.s);
    for (int i = start; i < dl->VtxBuffer.Size; ++i) {
        ImDrawVert &vert = dl->VtxBuffer[i];
        const float va = static_cast<float>((vert.col >> IM_COL32_A_SHIFT) & 0xFF) / 255.0f;
        vert.col = RingColour((vert.pos.x - bx0 * g_p.s) / w, (vert.pos.y - by0 * g_p.s) / h, va * alpha * g_p.alpha);
    }
}

// The card over the focused stop: a faint halo, the fill, the turning ring (5 thick), fading in over 140 ms.
void FocusRing(float x0, float y0, float x1, float y1, float radius, uint32_t fill) {
    const float t = static_cast<float>(NowMs() - g_ui.focus_at);
    const float a = 0.2f + 0.8f * std::min(1.0f, t / 140.0f);
    const float r = std::min(radius, std::min(x1 - x0, y1 - y0) / 2);
    const float th = 5;
    GradientStroke(x0, y0, x1, y1, r, th + 14, 0.13f * a, x0, y0, x1, y1);
    GradientStroke(x0, y0, x1, y1, r, th + 6, 0.22f * a, x0, y0, x1, y1);
    if (fill) RectFill(x0, y0, x1, y1, fill, r, a);
    GradientStroke(x0 + th / 2, y0 + th / 2, x1 - th / 2, y1 - th / 2, std::max(0.0f, r - th / 2), th, a, x0, y0, x1, y1);
}

// ---------------------------------------------------------------- stops
bool Focused(const std::string &id) { return g_ui.focus == id && !g_ui.modal.open; }
bool ModalFocused(const std::string &id) { return g_ui.focus == id && g_ui.modal.open; }

float g_clip_y0 = -FLT_MAX, g_clip_y1 = FLT_MAX; // the scroll viewer being drawn (BeginScroll .. EndScroll)

// Brush.Hover under the mouse (Brush.Pressed while pressed there), never on the focused stop (its ring is there): the launcher's
// "Hover" border of a button, a row, a list entry.
void HoverFill(const std::string &id, float x0, float y0, float x1, float y1, float r, ImDrawFlags flags = 0) {
    if (g_ui.hover.empty() || g_ui.hover != id || g_ui.focus == id) return;
    const bool down = g_ui.pressed && !g_ui.dragged && g_ui.press_id == id;
    g_p.dl->AddRectFilled(P(x0, y0), P(x1, y1), C(down ? g_p.pal.pressed : g_p.pal.hover), r * g_p.s, flags);
}

Stop &AddStop(const std::string &id, float x0, float y0, float x1, float y1, Area area, std::function<void()> activate) {
    Stop s;
    s.id = id;
    s.x0 = x0, s.y0 = y0, s.x1 = x1, s.y1 = y1;
    s.vy0 = g_clip_y0, s.vy1 = g_clip_y1;
    s.area = area;
    s.activate = std::move(activate);
    g_ui.stops.push_back(std::move(s));
    return g_ui.stops.back();
}

const Stop *FindStop(const std::string &id) {
    for (const Stop &s : g_ui.stops)
        if (s.id == id) return &s;
    return nullptr;
}

// ---------------------------------------------------------------- controls
enum class Kind : uint8_t { Outline, Primary, Secondary, Danger, Bottom };

struct ButtonSpec {
    std::string id, icon, label;
    Kind kind = Kind::Outline;
    float height = 88, pad = 44, min_width = 280, em = 30, width = 0; // width 0: from the content
    bool enabled = true;
    Weight weight = Weight::SemiBold;
    float radius = 12;
    Area area = kPane;
};

float ButtonWidth(const ButtonSpec &b) {
    if (b.width > 0) return b.width;
    const float content = TextW(b.weight, b.em, b.label) + (b.icon.empty() ? 0 : 38 + 16);
    return std::max(b.min_width, content + 2 * b.pad);
}

// c:ConsoleButton at (x, y): its width.
float Button(const ButtonSpec &b, float x, float y, std::function<void()> activate) {
    const float w = ButtonWidth(b), h = b.height;
    const bool focused = (b.area == kModal ? ModalFocused(b.id) : Focused(b.id)) && b.enabled;
    const float a = b.enabled ? 1 : 0.38f;
    const OverlayPalette &p = g_p.pal;
    uint32_t fg = p.accent, border = p.accent, bg = 0;
    switch (b.kind) {
    case Kind::Primary: fg = p.on_accent; bg = p.accent; break;
    case Kind::Secondary: fg = p.text; border = p.outline; break;
    case Kind::Danger: fg = p.bad; border = p.bad; break;
    case Kind::Bottom: border = 0; bg = p.surface; break;
    default: break;
    }
    if (b.kind == Kind::Bottom) {
        g_p.dl->AddRectFilled(P(x, y), P(x + w, y + h), C(bg, a), 14 * g_p.s, ImDrawFlags_RoundCornersBottom);
        if (b.enabled) HoverFill(b.id, x, y, x + w, y + h, 14, ImDrawFlags_RoundCornersBottom);
        if (focused) FocusRing(x - 3, y - 3, x + w + 3, y + h + 3, 14, p.focus_fill);
    } else {
        if (bg) RectFill(x, y, x + w, y + h, bg, b.radius, a);
        if (b.enabled) HoverFill(b.id, x, y, x + w, y + h, b.radius);
        if (border) RectLine(x, y, x + w, y + h, border, b.radius, 2.5f, a);
        if (focused) FocusRing(x, y, x + w, y + h, b.radius, b.kind == Kind::Primary ? p.accent : p.focus_fill);
    }
    const float tw = TextW(b.weight, b.em, b.label), iw = b.icon.empty() ? 0.0f : 38.0f + 16;
    float cx = x + (w - tw - iw) / 2;
    if (!b.icon.empty()) {
        Icon(b.icon, cx, y + (h - 38) / 2, 38, fg, 2, a);
        cx += iw;
    }
    Text(b.weight, b.em, cx, y + (h - b.em * kLine) / 2, fg, b.label, a);
    if (b.enabled) AddStop(b.id, x, y, x + w, y + h, b.area, std::move(activate));
    return w;
}

enum class RowKind : uint8_t { Plain, Chevron };

struct RowSpec {
    std::string id, icon, label, value, caption;
    RowKind kind = RowKind::Plain;
    bool enabled = true, destructive = false;
};

// c:SettingsRow from x0 to x1 at y: its height (the row, 104 at least, and its caption).
float Row(const RowSpec &r, float x0, float y, float x1, std::function<void()> activate) {
    const OverlayPalette &p = g_p.pal;
    const float h0 = 104;
    const bool focused = Focused(r.id) && r.enabled;
    const uint32_t fg = !r.enabled ? p.text_faint : r.destructive ? p.bad : p.text;
    // the rule under the row (under the ring while focused)
    RectFill(x0, y + h0 - 1.5f + (focused ? 11 : 0), x1, y + h0 + (focused ? 11 : 0), p.rule);
    if (r.enabled) HoverFill(r.id, x0 - 10, y + 2, x1 + 10, y + h0 - 2, 12); // the row's Hover: Margin -10,2
    if (focused) FocusRing(x0 - 10, y - 3, x1 + 10, y + h0 + 3, 12, p.focus_fill);
    float x = x0 + 20;
    if (!r.icon.empty()) {
        Icon(r.icon, x, y + (h0 - 44) / 2, 44, fg);
        x += 44 + 24;
    }
    float right = x1 - 20;
    if (r.kind == RowKind::Chevron) {
        Icon("chevronRight", right - 34 + 6, y + (h0 - 34) / 2, 34, p.text_muted);
        right -= 34 - 6 + 14;
        if (!r.value.empty()) {
            const float vw = std::min(560.0f, TextW(Weight::Regular, 32, r.value));
            TextFit(Weight::Regular, 32, right - vw, y + (h0 - 32 * kLine) / 2, r.enabled ? p.accent : p.text_faint, r.value, 560);
            right -= vw;
        }
        right -= 32;
    }
    TextFit(Weight::Regular, 32, x, y + (h0 - 32 * kLine) / 2, fg, r.label, std::max(10.0f, right - x));
    float h = h0;
    if (!r.caption.empty()) h += 14 + TextWrap(Weight::Regular, 25, x0 + 20, y + h0 + 14, p.text_muted, r.caption, x1 - x0 - 40, 36) + 20;
    if (r.enabled) AddStop(r.id, x0, y, x1, y + h0, kPane, std::move(activate));
    return h;
}

// c:SectionHeader from x0 to x1 with its top margin: its height.
float Section(const std::string &text, float x0, float y, float x1, float top = 44) {
    y += top;
    const float line = 28 * kLine;
    RectFill(x0, y + (line - 34) / 2, x0 + 5, y + (line + 34) / 2, g_p.pal.bar, 1.5f);
    TextFit(Weight::Regular, 28, x0 + 5 + 16, y, g_p.pal.text, text, x1 - x0 - 21);
    RectFill(x0, y + line + 16, x1, y + line + 17.5f, g_p.pal.rule);
    return top + line + 17.5f;
}

// c:Avatar: the disc of a name (its colour, its initials) at the centre (cx, cy), `size` across.
void Avatar(float cx, float cy, float size, const std::string &initials, uint32_t disc, float a = 1) {
    const OverlayPalette &p = g_p.pal;
    const float r = size / 2;
    Disc(cx, cy, r, disc, a);
    g_p.dl->AddCircle(P(cx, cy), (r - 1.5f) * g_p.s, C(p.surface, a), 64, 3 * g_p.s);
    const float box = size - 36;
    if (box > 0 && !initials.empty()) {
        ImFont *f = FontFor(Weight::ExtraBold, 60);
        const ImVec2 m = f->CalcTextSizeA(Px(30, 1), FLT_MAX, 0, initials.c_str()); // 30 units, the TextBlock's size
        const float k = std::min(box / std::max(1.0f, m.x), box / std::max(1.0f, m.y));
        const float em = 30 * k;
        g_p.dl->AddText(f, Px(em, g_p.s), P(cx - m.x * k / 2, cy - m.y * k / 2), C(0xFFFFFFFF, a), initials.c_str());
    }
}

// c:HintGlyph (42 high): a disc with the letter, or a key cap with the key's name.
float Glyph(char button, float x, float cy, float scale = 1) {
    const OverlayPalette &p = g_p.pal;
    const float size = 42 * scale;
    if (g_ui.keyboard) {
        const char *key = button == 'A' ? "Enter" : button == 'B' ? "Esc" : button == 'Y' ? "F" : "X";
        const float w = std::max(size, (TextW(Weight::ExtraBold, 22, key) + 26) * scale);
        RectLine(x, cy - size / 2, x + w, cy + size / 2, p.glyph, 9 * scale, 2.5f * scale);
        const float em = 22 * scale;
        Text(Weight::ExtraBold, em, x + (w - TextW(Weight::ExtraBold, em, key)) / 2, cy - em * kLine / 2, p.glyph, key);
        return w;
    }
    Disc(x + size / 2, cy, size / 2, p.glyph);
    const std::string letter(1, button);
    const float em = 26 * scale;
    Text(Weight::ExtraBold, em, x + (size - TextW(Weight::ExtraBold, em, letter)) / 2, cy - em * kLine / 2, p.on_glyph, letter);
    return size;
}

// ---------------------------------------------------------------- scrolling (c:SmoothScrollViewer)
struct Region {
    std::string key;
    float top, bottom, right;
    size_t first_stop;
    float left; // where the mouse's wheel and drag reach it from
};

Region BeginScroll(const std::string &key, float top, float bottom, float right, float left = 593.5f) {
    g_p.dl->PushClipRect(P(0, top), P(g_p.W, bottom), true);
    g_clip_y0 = top, g_clip_y1 = bottom;
    return Region{key, top, bottom, right, g_ui.stops.size(), left};
}

float ScrollOffset(const std::string &key) { return g_ui.scroll[key].offset; }

// The content was `content` high: keep the focused stop in view (90 from the edges), glide there, draw the bar. As the launcher's
// BringIntoView on a focus change, not while the mouse is in use: the wheel or a drag moved the viewer, the focus no longer pulls it
// back until it moves (by the pad or the keys).
void EndScroll(const Region &r, float content, float dt) {
    g_p.dl->PopClipRect();
    g_clip_y0 = -FLT_MAX, g_clip_y1 = FLT_MAX;
    Scroll &sc = g_ui.scroll[r.key];
    const float view = r.bottom - r.top, max = std::max(0.0f, content - view);
    g_ui.regions.push_back({r.key, r.left, r.top, r.right, r.bottom, max});
    const bool chase = g_ui.hands != Hands::Mouse && g_ui.focus_at >= g_ui.scrolled_at;
    for (size_t i = chase ? r.first_stop : g_ui.stops.size(); i < g_ui.stops.size(); ++i) {
        const Stop &s = g_ui.stops[i];
        if (s.id != g_ui.focus) continue;
        const float edge = std::min(90.0f, view / 4);
        if (s.y0 < r.top + edge) sc.target -= (r.top + edge) - s.y0;
        else if (s.y1 > r.bottom - edge) sc.target += s.y1 - (r.bottom - edge);
    }
    sc.target = std::clamp(sc.target, 0.0f, max);
    sc.offset += (sc.target - sc.offset) * std::min(1.0f, dt * 14);
    if (std::fabs(sc.target - sc.offset) < 0.5f) sc.offset = sc.target;
    sc.offset = std::clamp(sc.offset, 0.0f, max);
    // stops scrolled out of the viewer are not stops (the launcher's FocusNav moves within what can be scrolled to, so they stay)
    if (max > 0) {
        const float track0 = r.top + 12, track1 = r.bottom - 12, th = std::max(40.0f, (track1 - track0) * view / content);
        const float ty = track0 + (track1 - track0 - th) * (sc.offset / max);
        RectFill(r.right - 8, ty, r.right, ty + th, g_p.pal.text_faint, 4);
    }
}

// ================================================================ the page
bool ColumnUp() { return g_ui.column != Column::None && !g_ui.column_closing; }

std::string Str(const char *key, const std::string &value) { return "\"" + std::string(key) + "\":\"" + JsonText(value) + "\""; }
std::string Args(std::initializer_list<std::string> fields) {
    std::string out = "{";
    for (const auto &f : fields) {
        if (out.size() > 1) out += ",";
        out += f;
    }
    return out + "}";
}

// The pane the launcher is told (show): "" while the page is closed.
std::string ShownPane() { return g_ui.was_open ? g_ui.entry : std::string(); }

void SendShow() {
    const std::string pane = ShownPane();
    if (pane != g_ui.shown) {
        g_ui.shown = pane;
        Act("show", Args({Str("pane", pane)}));
    }
}

void SelectEntry(const std::string &key) {
    if (key == g_ui.entry) return;
    g_ui.entry = key;
    SendShow();
}

void SetFocus(const std::string &id, Area area) {
    if (id != g_ui.focus) g_ui.focus_at = NowMs();
    g_ui.focus = id;
    if (area == kList && id.rfind("list:", 0) == 0) SelectEntry(id.substr(5));
    if (area == kPane && !ColumnUp()) g_ui.remembered[g_ui.entry] = id;
}

bool InPane() {
    const Stop *s = FindStop(g_ui.focus);
    return s ? s->area == kPane : g_ui.focus.rfind("list:", 0) != 0;
}

void FocusList() { SetFocus("list:" + g_ui.entry, kList); }

// The first stop of the pane in reading order (top, then left).
const Stop *FirstPaneStop() {
    const Stop *best = nullptr;
    for (const Stop &s : g_ui.stops) {
        if (s.area != kPane) continue;
        if (!best || s.y0 < best->y0 - 1 || (std::fabs(s.y0 - best->y0) <= 1 && s.x0 < best->x0)) best = &s;
    }
    return best;
}

void EnterPane() {
    const auto it = g_ui.remembered.find(g_ui.entry);
    if (it != g_ui.remembered.end()) {
        if (const Stop *s = FindStop(it->second); s && s->area == kPane) {
            SetFocus(s->id, kPane);
            return;
        }
    }
    if (const Stop *s = FirstPaneStop()) SetFocus(s->id, kPane);
}

// The nearest stop of `area` that way from `from` (FocusNav.Move: wholly that way first; sideways, the same line first).
const Stop *Nearest(const Stop &from, Nav dir, Area area) {
    const Stop *best = nullptr;
    float best_score = FLT_MAX;
    for (int pass = 0; pass < 2 && !best; ++pass) {
        for (const Stop &s : g_ui.stops) {
            if (s.area != area || s.id == from.id) continue;
            float primary = 0, secondary = 0;
            const float fcx = (from.x0 + from.x1) / 2, fcy = (from.y0 + from.y1) / 2, cx = (s.x0 + s.x1) / 2, cy = (s.y0 + s.y1) / 2;
            const float hgap = std::max(0.0f, std::max(s.x0 - from.x1, from.x0 - s.x1));
            const float vgap = std::max(0.0f, std::max(s.y0 - from.y1, from.y0 - s.y1));
            bool ok = false;
            switch (dir) {
            case Nav::Down: ok = pass == 0 ? s.y0 >= from.y1 - 1 : cy > fcy + 1; primary = s.y0 - from.y1; secondary = hgap * 2 + std::fabs(cx - fcx) * 0.05f; break;
            case Nav::Up: ok = pass == 0 ? s.y1 <= from.y0 + 1 : cy < fcy - 1; primary = from.y0 - s.y1; secondary = hgap * 2 + std::fabs(cx - fcx) * 0.05f; break;
            case Nav::Right: ok = pass == 0 ? s.x0 >= from.x1 - 1 : cx > fcx + 1; primary = s.x0 - from.x1; secondary = vgap * 4 + std::fabs(cy - fcy) * 0.5f; break;
            case Nav::Left: ok = pass == 0 ? s.x1 <= from.x0 + 1 : cx < fcx - 1; primary = from.x0 - s.x1; secondary = vgap * 4 + std::fabs(cy - fcy) * 0.5f; break;
            default: break;
            }
            if (!ok) continue;
            const float score = std::max(0.0f, primary) + secondary;
            if (score < best_score) { best_score = score; best = &s; }
        }
    }
    return best;
}

void CloseModal() {
    if (!g_ui.modal.open) return;
    g_ui.modal.open = false;
    const std::string back = g_ui.modal.back_focus;
    g_ui.focus = back;
    g_ui.focus_at = NowMs();
}

// ConfirmAsync's modal: the question, the yes (Outline) and the no (the bottom, focused first).
void Ask(const std::string &title, const std::string &body, const std::string &yes, const std::string &no, std::function<void()> on_yes) {
    Modal m;
    m.open = true;
    m.title = title;
    m.body = body;
    m.back_focus = g_ui.focus;
    m.actions.push_back({yes, [on_yes] { CloseModal(); on_yes(); }});
    m.bottom = no;
    m.on_bottom = [] { CloseModal(); };
    g_ui.modal = std::move(m);
    g_ui.focus = "modal.bottom";
    g_ui.focus_at = NowMs();
}

void CloseOverlay() { g_open = false; } // core.cpp gives the input back: the A or B that closed it never reaches the game

// ---------------------------------------------------------------- the column (a player of the lobby)
constexpr float kColumnIn = 280, kColumnOut = 220; // ms: the slide up from below the screen, and back down

// Opens the column over the page (B comes back to the focus it had there).
void OpenColumn(const std::string &card) {
    Ui &u = g_ui;
    const bool was_up = u.column != Column::None && !u.column_closing;
    if (!was_up) {
        u.column_focus = u.modal.open ? u.modal.back_focus : u.focus;
        u.column_at = NowMs();
    }
    u.modal = Modal{};
    u.column = Column::Card;
    u.column_closing = false;
    u.card = card;
    u.scroll.erase("column");
    u.focus = "col.pending.card"; // its first stop, once drawn
    u.focus_at = NowMs();
}

// B on a column: it slides back down; the page's focus comes back at once (its stops are drawn again as it fades in).
void CloseColumn() {
    Ui &u = g_ui;
    if (u.column == Column::None || u.column_closing) return;
    u.column_closing = true;
    u.column_at = NowMs();
    u.modal = Modal{};
    u.focus = u.column_focus.empty() ? "list:" + u.entry : u.column_focus;
    u.focus_at = NowMs();
}

// B: the top layer closes, else back toward the list, else back to the game (OverlayViewModel.OnBack).
void Back() {
    if (g_ui.modal.open) {
        if (g_ui.modal.on_bottom) {
            const auto run = g_ui.modal.on_bottom; // it replaces the modal it is called from
            run();
        } else {
            CloseModal();
        }
        return;
    }
    if (g_ui.column != Column::None) {
        CloseColumn();
        return;
    }
    if (InPane() && g_ui.entry != "game") {
        FocusList();
        return;
    }
    CloseOverlay();
}

void HandleNav(Nav nav) {
    if (g_ui.column_closing) return; // sliding away: nothing in it to press
    const Stop *cur = FindStop(g_ui.focus);
    switch (nav) {
    case Nav::Back: Back(); return;
    case Nav::Accept: {
        if (!cur) return;
        if (cur->area == kList) { EnterPane(); return; }
        const auto run = cur->activate; // what it does may change what the next frame draws
        if (run) run();
        return;
    }
    case Nav::Up: case Nav::Down: case Nav::Left: case Nav::Right: break;
    default: return; // X, Y: the page has no use for them
    }
    if (!cur) {
        if (g_ui.modal.open) SetFocus("modal.bottom", kModal);
        else if (const Stop *first = FirstPaneStop(); first && (InPane() || ColumnUp())) SetFocus(first->id, kPane);
        else if (!ColumnUp()) FocusList();
        return;
    }
    if (const Stop *next = Nearest(*cur, nav, cur->area)) {
        SetFocus(next->id, next->area);
        return;
    }
    if (cur->area == kList && nav == Nav::Right) EnterPane();
    else if (cur->area == kPane && nav == Nav::Left && !ColumnUp()) FocusList();
}

// ---------------------------------------------------------------- the mouse (Input/Tap.cs: everything acts when the press is let
// go, and only if it was a tap; a drag scrolls the viewer it began in). It reads the stops, hints and viewers of the last frame drawn.
const Stop *StopAt(float x, float y) {
    const Stop *hit = nullptr;
    for (const Stop &s : g_ui.stops)
        if (x >= s.x0 && x < s.x1 && y >= std::max(s.y0, s.vy0) && y < std::min(s.y1, s.vy1)) hit = &s; // the last drawn is on top
    return hit;
}

char HintAt(float x, float y) {
    for (const HintBox &h : g_ui.hints)
        if (x >= h.x0 && x < h.x1 && y >= h.y0 && y < h.y1) return h.button;
    return 0;
}

const RegionBox *RegionAt(float x, float y) {
    for (const RegionBox &r : g_ui.regions)
        if (x >= r.x0 && x < r.x1 && y >= r.y0 && y < r.y1) return &r;
    return nullptr;
}

bool BesideModal(float x, float y) {
    return g_ui.modal.open && (x < g_ui.modal_x0 || x >= g_ui.modal_x1 || y < g_ui.modal_y0 || y >= g_ui.modal_y1);
}

// The dimmed game beside a column (no modal over it).
bool BesideColumn(float x) { return !g_ui.modal.open && ColumnUp() && (x < g_ui.column_x0 || x >= g_ui.column_x1); }

void UseMouse() {
    g_ui.hands = Hands::Mouse;
    g_ui.keyboard = true; // HintGlyph: key caps whenever it is not the pad
}

// A tap on a stop: it takes the focus (a list entry is selected, as the launcher's tap on a ListBoxItem) and does what A does.
void Tapped(const Stop &stop) {
    const Stop s = stop; // what it does may change what the next frame draws
    if (s.area == kList) {
        SetFocus(s.id, kList);
        return;
    }
    if (s.area == kModal) {
        if (g_ui.focus != s.id) g_ui.focus_at = NowMs();
        g_ui.focus = s.id;
    } else {
        SetFocus(s.id, kPane);
    }
    if (s.activate) s.activate();
}

void HandlePointer(const Input &in) {
    Ui &u = g_ui;
    const float slop = 10 / g_p.s; // TapTracker.Slop, 10 pixels of the window
    if (in.x > kNoPoint / 2) {
        const float x = in.x * g_p.W, y = in.y * g_p.H;
        if (!u.mouse_known) {
            u.mouse_known = true;
            u.moved_x = x, u.moved_y = y;
        } else if (std::fabs(x - u.moved_x) + std::fabs(y - u.moved_y) > 6 / g_p.s) { // only a real movement (MainWindow's 6)
            u.moved_x = x, u.moved_y = y;
            if (u.hands == Hands::Gamepad) UseMouse(); // the cursor comes back
        }
        u.mx = x, u.my = y;
    }
    switch (in.pointer) {
    case Pointer::Move:
        if (u.pressed && !u.dragged && std::max(std::fabs(u.mx - u.press_x), std::fabs(u.my - u.press_y)) > slop) {
            u.dragged = true; // no click when it is let go
            u.drag_y = u.press_y;
            const RegionBox *r = RegionAt(u.press_x, u.press_y);
            u.drag_region = r && !u.modal.open ? r->key : "";
        }
        if (u.pressed && u.dragged) {
            for (const RegionBox &r : u.regions) {
                if (r.key != u.drag_region) continue;
                Scroll &sc = u.scroll[r.key];
                sc.target = sc.offset = std::clamp(sc.target + (u.drag_y - u.my), 0.0f, r.max); // the content follows the pointer
                u.scrolled_at = NowMs();
            }
            u.drag_y = u.my;
        }
        break;
    case Pointer::LeftDown: {
        UseMouse();
        if (u.pressed) break; // the same press, told twice
        u.pressed = true;
        u.dragged = false;
        u.press_x = u.mx, u.press_y = u.my;
        const Stop *s = StopAt(u.mx, u.my);
        u.press_id = s ? s->id : BesideModal(u.mx, u.my) ? "@scrim" : BesideColumn(u.mx) ? "@column" : "";
        u.press_hint = s || u.modal.open ? 0 : HintAt(u.mx, u.my);
        break;
    }
    case Pointer::LeftUp: {
        if (!u.pressed) break;
        u.pressed = false;
        if (u.dragged) break;
        const Stop *s = StopAt(u.mx, u.my);
        if (s && s->id == u.press_id) Tapped(*s);
        else if (!s && u.press_hint && HintAt(u.mx, u.my) == u.press_hint) { // a hint: that button pressed (HintBar.OnHint)
            const char b = u.press_hint;
            HandleNav(b == 'A' ? Nav::Accept : b == 'B' ? Nav::Back : b == 'X' ? Nav::X : Nav::Y);
        } else if ((u.press_id == "@scrim" && BesideModal(u.mx, u.my)) || (u.press_id == "@column" && BesideColumn(u.mx))) {
            Back(); // ModalHost's backdrop: the modal is dismissed (its no); beside a column: it slides away
        }
        break;
    }
    case Pointer::Wheel:
        if (const RegionBox *r = u.modal.open ? nullptr : RegionAt(u.mx, u.my)) { // ScrollMotion.WheelDistance: 80 a notch
            Scroll &sc = u.scroll[r->key];
            sc.target = std::clamp(sc.target - static_cast<float>(in.value) / 120.0f * 80.0f, 0.0f, r->max);
            u.scrolled_at = NowMs();
        }
        break;
    case Pointer::Back: // the mouse's back button: B
        UseMouse();
        HandleNav(Nav::Back);
        break;
    }
}

void ProcessInputs() {
    std::vector<Input> inputs;
    inputs.swap(g_inputs);
    for (const Input &in : inputs) {
        if (in.kind == Input::PointerIn) {
            HandlePointer(in);
            continue;
        }
        g_ui.keyboard = in.keyboard;
        g_ui.hands = in.keyboard ? Hands::Keyboard : Hands::Gamepad;
        HandleNav(static_cast<Nav>(in.value));
    }
}

// ---------------------------------------------------------------- the header, the list, the hint bar
// The header (c:PageHeader): the icon and the title; at the right the clock.
void DrawHeader(const OverlayView &v) {
    const OverlayPalette &p = g_p.pal;
    Icon("gamepad", 120, 68 - 25, 50, p.text, 1.8f);
    Text(Weight::Regular, 42, 188, 68 - 42 * kLine / 2, p.text, v.text.title);
    // the row is the clock's line high (34), centred on 68; right to left from the header's content edge
    const float row = 34 * kLine, top = 68 - row / 2;
    const float x = g_p.W - 116;
    const float cw = TextW(Weight::Regular, 34, v.clock);
    Text(Weight::Regular, 34, x - cw, top, p.text, v.clock);
    RectFill(72, 124.5f, g_p.W - 72, 126, p.rule);
}

void DrawList(const OverlayView &v) {
    const OverlayPalette &p = g_p.pal;
    const float x0 = 112, x1 = x0 + 460;
    float y = 140;
    for (const OverlayEntry &e : v.entries) {
        if (e.separator) {
            RectFill(x0 + 38, y + 10, x1 - 38, y + 11.5f, p.rule);
            y += 21.5f;
        }
        const float top = y + 6, bottom = top + 110;
        const std::string id = "list:" + e.key;
        const bool selected = g_ui.entry == e.key, focused = Focused(id);
        HoverFill(id, x0, top, x1, bottom, 12);
        if (focused) FocusRing(x0, top, x1, bottom, 12, p.focus_fill);
        if (selected) RectFill(x0 + 32, top + 19, x0 + 38, top + 91, p.accent, 1.5f);
        const uint32_t fg = selected ? p.accent : p.text;
        float tx = x0 + 68;
        if (!e.icon.empty()) {
            Icon(e.icon, tx, top + 35, 40, fg, 1.8f);
            tx += 40 + 18;
        }
        float right = x1 - 24;
        if (e.locked) {
            Icon("lock", right - 30, top + 40, 30, fg, 2, 0.6f);
            right -= 30 + 14;
        }
        TextFit(Weight::Regular, 32, tx, top + (110 - 32 * kLine) / 2, fg, e.title, right - tx);
        AddStop(id, x0, top, x1, bottom, kList, nullptr);
        y = bottom + 6;
    }
    RectFill(592, 148, 593.5f, g_p.H - 112, p.rule);
}

// The hint bar (HintBar): the rule from x0 to x1, what is in the player's hands at icon_x, the hints right to left from `right`;
// dy: moved down that much (a column sliding).
void DrawHintBar(float x0, float x1, float icon_x, float right, const std::vector<std::pair<char, std::string>> &hints, float dy = 0) {
    const OverlayPalette &p = g_p.pal;
    const float top = g_p.H - 112 + dy, cy = top + 56;
    RectFill(x0, top, x1, top + 1.5f, p.rule);
    // what is in the player's hands: the gamepad (with the "connected" dot), else the keyboard
    if (g_ui.keyboard) {
        Icon("keyboard", icon_x, cy - 31, 62, p.glyph, 1.8f);
    } else {
        FillIcon(g_p.dl, "fill.gamepad", P(icon_x, cy - 33), 66 * g_p.s, C(p.glyph), C(p.background));
        if (g_ui.pad_present) {
            Disc(icon_x + 66 + 6 - 11, cy + 33 - 4 - 11, 11, p.background);
            Disc(icon_x + 66 + 6 - 11, cy + 33 - 4 - 11, 7, p.good);
        }
    }
    // right to left: each hint is padding 14, the glyph, 14, the label (34), padding 14; 6 between
    float x = right;
    for (auto it = hints.rbegin(); it != hints.rend(); ++it) {
        const float lw = TextW(Weight::Regular, 34, it->second);
        const char *key = it->first == 'A' ? "Enter" : it->first == 'B' ? "Esc" : it->first == 'Y' ? "F" : "X";
        const float gw = g_ui.keyboard ? std::max(42.0f, TextW(Weight::ExtraBold, 22, key) + 26) : 42;
        const float w = 14 + gw + 14 + lw + 14;
        x -= w;
        // a hint is a button for the mouse (ChromeButton: Padding 14,10, Brush.Hover / Brush.Pressed), never a focus stop
        const float hy0 = cy - 21 - 10, hy1 = cy + 21 + 10;
        if (!g_ui.modal.open) {
            if (g_ui.hover_hint == it->first) {
                const bool down = g_ui.pressed && !g_ui.dragged && g_ui.press_hint == it->first;
                RectFill(x, hy0, x + w, hy1, down ? p.pressed : p.hover, 12);
            }
            g_ui.hints.push_back({x, hy0, x + w, hy1, it->first});
        }
        Glyph(it->first, x + 14, cy);
        Text(Weight::Regular, 34, x + 14 + gw + 14, cy - 34 * kLine / 2 - 1, p.text, it->second);
        x -= 6;
    }
}

void DrawHints(const OverlayView &v) {
    std::vector<std::pair<char, std::string>> hints;
    if (g_ui.modal.open) hints = {{'B', v.text.back}, {'A', v.text.ok}};
    else hints = {{'B', InPane() && g_ui.entry != "game" ? v.text.back : v.text.back_to_game}, {'A', v.text.ok}};
    DrawHintBar(72, g_p.W - 72, 98, g_p.W - 88, hints);
}

// The title row of every pane but Game: the title and its line, and the game small at the right (nothing beside it).
void DrawPaneTitle(const std::string &title, const std::string &subtitle, uint32_t subtitle_col) {
    const OverlayPalette &p = g_p.pal;
    const float x0 = 655.5f, x1 = g_p.W - 132, top = 156, h = 162;
    // the game, still there (its live picture is the game itself, around this page)
    const float tx0 = x1 - 288;
    RectFill(tx0, top, x1, top + h, p.surface_alt, 12);
    if (!GamePicture(tx0, top, x1, top + h, 12)) Icon("gamepad", tx0 + 144 - 32, top + 81 - 32, 64, p.text_faint, 1.8f);
    const float max = tx0 - 30 - x0;
    const float th = 42 * kLine, sh = subtitle.empty() ? 0.0f : 6.0f + 36;
    const float y = top + (h - th - sh) / 2;
    TextFit(Weight::SemiBold, 42, x0, y, p.text, title, max);
    if (!subtitle.empty()) TextFit(Weight::Regular, 25, x0, y + th + 6, subtitle_col, subtitle, max);
}

// ---------------------------------------------------------------- Game
void DrawGame(const OverlayView &v) {
    const OverlayPalette &p = g_p.pal;
    const float x0 = 655.5f, top = 156;
    RectFill(x0, top, x0 + 960, top + 540, p.surface_alt, 20);
    if (!GamePicture(x0, top, x0 + 960, top + 540, 20)) Icon("gamepad", x0 + 480 - 80, top + 270 - 80, 160, p.text_faint, 1.4f);
    const float ty = top + 540 + 24;
    TextFit(Weight::SemiBold, 42, x0 + 4, ty, p.text, v.game.name, g_p.W - 132 - x0);
    const float sy = ty + 42 * kLine + 8;
    Icon("globe", x0 + 4, sy + 4, 28, p.text_muted, 2);
    TextFit(Weight::Regular, 25, x0 + 4 + 28 + 12, sy, p.text_muted, v.game.status, g_p.W - 132 - x0 - 44);
    const float by = sy + 36 + 30;
    ButtonSpec back;
    back.id = "game.back";
    back.kind = Kind::Primary;
    back.icon = "play";
    back.label = v.game.back_to_game;
    back.em = 32;
    back.pad = 36;
    back.min_width = 340;
    const float bx = x0 + Button(back, x0, by, [] { CloseOverlay(); });
    if (v.game.quit.present) {
        ButtonSpec quit;
        quit.id = "game.quit";
        quit.kind = Kind::Danger;
        quit.icon = "power";
        quit.label = v.game.quit.label;
        quit.pad = 30;
        quit.enabled = !v.Busy("quitGame");
        const Confirm q = v.game.quit;
        Button(quit, bx + 18, by, [q] { Ask(q.title, q.body, q.yes, q.no, [] { Act("quitGame"); }); });
    }
}

// ---------------------------------------------------------------- Lobby
float DrawYouPill(const std::string &text, float right, float cy) {
    const float w = TextW(Weight::Bold, 20, text) + 32;
    RectFill(right - w, cy - 17, right, cy + 17, g_p.pal.accent_soft, 17);
    Text(Weight::Bold, 20, right - w + 16, cy - 20 * kLine / 2, g_p.pal.accent, text);
    return w;
}

// Every name a player used in this online session, oldest first: "TurboShell · xX_Drift_Xx · NotTurbo".
std::string JoinNames(const LobbyPlayer &pl) {
    std::string out;
    for (const std::string &n : pl.names) {
        if (!out.empty()) out += " \xC2\xB7 ";
        out += n;
    }
    return out;
}

// One player of the lobby (the launcher's Current Lobby row): the disc with the initials, the name, and under it every
// name used when there is more than one; at the right the "You" pill on the player's own row, the warning mark with its
// label on a renamed one, and the chevron where A opens the player. Its height.
float DrawPlayerRow(const LobbyPane &l, const LobbyPlayer &pl, float x0, float y, float x1) {
    const OverlayPalette &p = g_p.pal;
    const std::string id = "lobby.row:" + pl.id;
    const bool focused = Focused(id);
    const float h = 92;
    HoverFill(id, x0, y, x1, y + h, 12);
    if (focused) FocusRing(x0 - 4, y - 4, x1 + 4, y + h + 4, 16, p.focus_fill);
    Avatar(x0 + 16 + 36, y + 10 + 36, 72, pl.initials, pl.disc);
    float right = x1 - 20;
    if (!pl.you) { // A opens this player
        Icon("chevronRight", right - 34 + 6, y + (h - 34) / 2, 34, p.text_muted);
        right -= 34 - 6 + 14;
    }
    if (pl.you) right -= DrawYouPill(l.you, right, y + h / 2) + 16;
    if (pl.renamed && !pl.renamed_label.empty()) {
        const float lw = TextW(Weight::SemiBold, 25, pl.renamed_label);
        Text(Weight::SemiBold, 25, right - lw, y + (h - 25 * kLine) / 2, p.warn, pl.renamed_label);
        Icon("warning", right - lw - 12 - 30, y + (h - 30) / 2, 30, p.warn, 2);
        right -= lw + 12 + 30 + 16;
    }
    const float tx = x0 + 16 + 72 + 20, max = std::max(10.0f, right - 12 - tx);
    const std::string names = pl.renamed ? JoinNames(pl) : std::string();
    if (names.empty()) {
        TextFit(Weight::Regular, 30, tx, y + (h - 30 * kLine) / 2, p.text, pl.name, max);
    } else {
        const float block = 30 * kLine + 22 * kLine, ty = y + (h - block) / 2;
        TextFit(Weight::Regular, 30, tx, ty, p.text, pl.name, max);
        TextFit(Weight::Regular, 22, tx, ty + 30 * kLine, p.text_muted, names, max);
    }
    const std::string handle = pl.id;
    const bool you = pl.you;
    AddStop(id, x0, y, x1, y + h, kPane, [handle, you] {
        if (you) return; // nothing to do to yourself here
        OpenColumn(handle);
    });
    return h + 8;
}

void DrawLobby(const OverlayView &v, float dt) {
    const OverlayPalette &p = g_p.pal;
    const LobbyPane &l = v.lobby;
    const float top = 342, bottom = g_p.H - 112, cx0 = 593.5f + 50, cx1 = g_p.W - 72 - 48;
    Region r = BeginScroll("lobby", top, bottom, g_p.W - 72);
    const float off = ScrollOffset("lobby");
    float y = top + 6 - off;
    const float x0 = cx0 + 12, x1 = cx1 - 12;
    // the players, the player first
    for (const LobbyPlayer &pl : l.players) y += DrawPlayerRow(l, pl, x0, y, x1);
    // out of a lobby: only that
    if (!l.note.empty()) y += TextWrap(Weight::Regular, 25, x0, y, p.text_muted, l.note, x1 - x0, 36) + 6;
    EndScroll(r, y + off - top + 40, dt);
}

// ---------------------------------------------------------------- the column
// Half the screen wide, centred over the dimmed game; it slides up from below the screen until it reaches its top edge, and back
// down on B. Inside: the content, then the rule and the hint bar (the page's).
struct ColumnBox {
    float x0, x1, top; // its sides, where its top is now (stage units)
};

// A line centred on cx, cut with "…" to fit `max`.
void TextCentred(Weight w, float em, float cx, float y, uint32_t col, const std::string &t, float max, float a = 1) {
    TextFit(w, em, cx - std::min(max, TextW(w, em, t)) / 2, y, col, t, max, a);
}

// The first stop of a column in reading order, or a focus that waits (nothing to press there).
void FocusColumnFirst() {
    if (const Stop *first = FirstPaneStop()) SetFocus(first->id, kPane);
    else g_ui.focus = "col.pending.none";
}

// Report: the reason (the server's), then Send Report asked first.
void AskReport(const ReportTexts &r, const std::string &handle, const std::string &name) {
    Modal m;
    m.open = true;
    m.title = r.what;
    m.body = r.caption;
    m.back_focus = g_ui.focus;
    for (const auto &[code, label] : r.reasons) {
        m.actions.push_back({label, [r, handle, name, code = code, label = label] {
                                 CloseModal();
                                 Ask(r.title, name + " \xC2\xB7 " + label, r.send, r.no,
                                     [handle, code] { Act("report", Args({Str("id", handle), Str("reason", code)})); });
                             }});
    }
    m.bottom = r.no;
    m.on_bottom = [] { CloseModal(); };
    g_ui.modal = std::move(m);
    g_ui.focus = "modal.bottom";
    g_ui.focus_at = NowMs();
}

// A card of the column (Brush.SurfaceAlt, corner 14) from x0 to x1 at y, `h` high.
void CardBox(float x0, float y, float x1, float h) { RectFill(x0, y, x1, y + h, g_p.pal.surface_alt, 14); }

// A player of the lobby: the big disc, the name, what they are (you, or the renamed flag); the warning when they used more than
// one name; what can be done (Report to the server's host, Copy Report); the Player ID the server gave them; every name of this
// online session.
void DrawCardColumn(const OverlayView &v, const ColumnBox &c, float dt) {
    const OverlayPalette &p = g_p.pal;
    const LobbyPane &l = v.lobby;
    const LobbyPlayer *pl = nullptr;
    for (const LobbyPlayer &x : l.players)
        if (x.id == g_ui.card) pl = &x;
    if (!pl) { // they left the lobby: the column closes
        CloseColumn();
        return;
    }
    const float cx = (c.x0 + c.x1) / 2, x0 = c.x0 + 64, x1 = c.x1 - 64, bottom = c.top + g_p.H - 112;
    Region r = BeginScroll("column", c.top, bottom, c.x1 - 24, c.x0);
    const float off = ScrollOffset("column");
    float y = c.top + 84 - off;
    // the avatar (220), the name, the line under it
    Avatar(cx, y + 110, 220, pl->initials, pl->disc);
    y += 220 + 26;
    TextCentred(Weight::SemiBold, 42, cx, y, p.text, pl->name, x1 - x0);
    y += 42 * kLine + 4;
    if (pl->you) TextCentred(Weight::Regular, 25, cx, y, p.accent, l.you, x1 - x0);
    else if (pl->renamed) TextCentred(Weight::Regular, 25, cx, y, p.warn, pl->renamed_label, x1 - x0);
    y += 36 + 34;
    // more than one name under the same Player ID: the warning
    if (pl->renamed && !pl->renamed_body.empty()) {
        const auto lines = Lines(Weight::SemiBold, 25, pl->renamed_body, x1 - x0 - 32 - 38 - 20 - 32);
        const float h = std::max(38.0f, static_cast<float>(lines.size()) * 36) + 2 * 28;
        CardBox(x0, y, x1, h);
        Icon("warning", x0 + 32, y + (h - 38) / 2, 38, p.warn, 2);
        float ty = y + (h - static_cast<float>(lines.size()) * 36) / 2;
        for (const std::string &line : lines) {
            Text(Weight::SemiBold, 25, x0 + 32 + 38 + 20, ty, p.warn, line);
            ty += 36;
        }
        y += h + 14;
    }
    const std::string handle = pl->id, name = pl->name;
    // Report (asked first: the reason, then Send Report): only where the launcher offers it, greyed with its reason
    if (pl->can_report && v.report.present) {
        RowSpec report;
        report.id = "col.card.report";
        report.icon = "flag";
        report.label = l.report;
        report.caption = v.report.enabled ? v.report.caption : v.report.why;
        report.kind = RowKind::Chevron;
        report.enabled = v.report.enabled && !v.Busy("report");
        const ReportTexts texts = v.report;
        y += Row(report, x0, y, x1, [texts, handle, name] { AskReport(texts, handle, name); }) + 8;
    }
    // Copy Report: a text report on the clipboard; nothing is sent anywhere
    if (pl->can_copy_report) {
        RowSpec copy;
        copy.id = "col.card.copy";
        copy.icon = "copy";
        copy.label = l.copy_report;
        copy.caption = l.copy_report_caption;
        copy.enabled = !v.Busy("copyReport");
        y += Row(copy, x0, y, x1, [handle] { Act("copyReport", Args({Str("id", handle)})); }) + 8;
    }
    // the Player ID the server gave them
    {
        const auto caption = Lines(Weight::Regular, 22, l.id_caption, x1 - x0 - 64);
        const float h = 28 + 22 * kLine + 8 + 28 * kLine + (caption.empty() ? 0 : 10 + static_cast<float>(caption.size()) * 32) + 28;
        y += 14;
        CardBox(x0, y, x1, h);
        float ty = y + 28;
        TextFit(Weight::Regular, 22, x0 + 32, ty, p.text_muted, l.id_label, x1 - x0 - 64);
        ty += 22 * kLine + 8;
        TextFit(Weight::Regular, 28, x0 + 32, ty, p.text, pl->player_id, x1 - x0 - 64);
        ty += 28 * kLine + 10;
        for (const std::string &line : caption) {
            Text(Weight::Regular, 22, x0 + 32, ty, p.text_muted, line);
            ty += 32;
        }
        y += h;
    }
    // every name of this online session, oldest first
    if (!pl->names.empty()) {
        y += Section(FormatCount(l.names_header, pl->names.size()), x0, y, x1, 30);
        y += 14;
        for (const std::string &n : pl->names) {
            TextFit(Weight::Regular, 30, x0 + 21, y, n == pl->name ? p.text : p.text_muted, n, x1 - x0 - 42);
            y += 30 * kLine + 8;
        }
    }
    EndScroll(r, y + off - c.top + 40, dt);
    if (g_ui.focus == "col.pending.card") FocusColumnFirst();
    std::vector<std::pair<char, std::string>> hints;
    if (g_ui.modal.open) hints = {{'B', v.text.back}, {'A', v.text.ok}};
    else {
        hints.push_back({'B', v.text.back});
        if (FindStop(g_ui.focus)) hints.push_back({'A', v.text.select});
    }
    DrawHintBar(c.x0 + 40, c.x1 - 40, c.x0 + 52, c.x1 - 44, hints, c.top);
}

// How far the column is in (0 below the screen .. 1 at its top edge); a column that finished sliding out is gone here.
float ColumnProgress(uint64_t now) {
    Ui &u = g_ui;
    if (u.column == Column::None) return 0;
    const float t = static_cast<float>(now - u.column_at);
    if (!u.column_closing) return EaseOut(std::min(1.0f, t / kColumnIn));
    if (t < kColumnOut) return 1 - EaseIn(t / kColumnOut);
    u.column = Column::None;
    u.column_closing = false;
    u.card.clear();
    return 0;
}

void DrawColumn(const OverlayView &v, float k, float dt) {
    Ui &u = g_ui;
    const OverlayPalette &p = g_p.pal;
    RectFill(0, 0, g_p.W, g_p.H, p.dim, 0, k); // the game, dimmed
    const float w = g_p.W * 0.5f;
    ColumnBox c{(g_p.W - w) / 2, (g_p.W + w) / 2, (1 - k) * g_p.H};
    u.column_top = c.top;
    u.column_x0 = c.x0, u.column_x1 = c.x1;
    for (int i = 1; i <= 6; ++i) RectFill(c.x0 - 3.0f * static_cast<float>(i), c.top, c.x1 + 3.0f * static_cast<float>(i), c.top + g_p.H, 0xFF000000, 0, 0.03f); // its shadow
    RectFill(c.x0, c.top, c.x1, c.top + g_p.H, p.background);
    const size_t first = u.stops.size(), first_hint = u.hints.size(), first_region = u.regions.size();
    DrawCardColumn(v, c, dt);
    if (u.column_closing) { // sliding away: nothing in it to press or scroll
        u.stops.erase(u.stops.begin() + static_cast<std::ptrdiff_t>(first), u.stops.end());
        u.hints.erase(u.hints.begin() + static_cast<std::ptrdiff_t>(first_hint), u.hints.end());
        u.regions.erase(u.regions.begin() + static_cast<std::ptrdiff_t>(first_region), u.regions.end());
    }
}

// ---------------------------------------------------------------- the modal (c:ModalHost)
void DrawModal() {
    const OverlayPalette &p = g_p.pal;
    Modal &m = g_ui.modal;
    RectFill(0, 0, g_p.W, g_p.H, p.dim);
    const float w = 1080, x0 = (g_p.W - w) / 2, x1 = x0 + w;
    const float inner = w - 224;
    const auto title = Lines(Weight::Regular, 34, m.title, inner);
    const auto body = Lines(Weight::Regular, 27, m.body, inner - 20);
    float h = 88 + static_cast<float>(title.size()) * 34 * kLine;
    if (!m.body.empty()) h += 12 + static_cast<float>(body.size()) * 40;
    if (!m.actions.empty()) h += 40 + static_cast<float>(m.actions.size()) * (88 + 16);
    h += 56;
    const float bottom_h = m.bottom.empty() ? 0.0f : 112.0f;
    const float y0 = (g_p.H - h - bottom_h) / 2;
    RectFill(x0, y0, x1, y0 + h + bottom_h, p.modal, 14);
    g_ui.modal_x0 = x0, g_ui.modal_y0 = y0, g_ui.modal_x1 = x1, g_ui.modal_y1 = y0 + h + bottom_h; // a tap beside it dismisses it
    float y = y0 + 88;
    for (const auto &line : title) {
        Text(Weight::Regular, 34, x0 + 112, y, p.text, line);
        y += 34 * kLine;
    }
    if (!m.body.empty()) {
        y += 12;
        for (const auto &line : body) {
            Text(Weight::Regular, 27, x0 + 112, y, p.text_muted, line);
            y += 40;
        }
    }
    if (!m.actions.empty()) {
        y += 40;
        for (size_t i = 0; i < m.actions.size(); ++i) {
            ButtonSpec b;
            b.id = "modal.action:" + std::to_string(i);
            b.label = m.actions[i].first;
            b.width = 630;
            b.area = kModal;
            auto run = m.actions[i].second;
            Button(b, (g_p.W - 630) / 2, y + 8, run);
            y += 88 + 16;
        }
    }
    if (!m.bottom.empty()) {
        ButtonSpec b;
        b.id = "modal.bottom";
        b.kind = Kind::Bottom;
        b.label = m.bottom;
        b.width = w;
        b.height = 112;
        b.em = 34;
        b.weight = Weight::Regular;
        b.area = kModal;
        auto run = m.on_bottom;
        Button(b, x0, y0 + h, [run] { if (run) run(); else CloseModal(); });
    }
}

// ---------------------------------------------------------------- one frame
void OnOpened(const OverlayView &v) {
    Ui &u = g_ui;
    // the pane the launcher asks for: the lobby's players right away while in a lobby, else the Game pane with Back to the Game
    // focused (a console's guide); whatever was open is put away
    u.entry = "game";
    for (const OverlayEntry &e : v.entries)
        if (e.key == v.start) u.entry = e.key;
    u.focus = u.entry == "game" ? "game.back" : "pane.pending";
    u.focus_at = NowMs();
    u.column = Column::None;
    u.column_closing = false;
    u.card.clear();
    u.modal = Modal{};
    u.scroll.clear();
    u.remembered.clear();
    u.stops.clear();
    u.mouse_known = u.pressed = u.dragged = false;
    u.hover.clear();
    u.hover_hint = 0;
    u.scrolled_at = 0;
    u.hints.clear();
    u.regions.clear();
    // the press that opened it is not a press in the page (the mouse's place is kept)
    g_inputs.erase(std::remove_if(g_inputs.begin(), g_inputs.end(), [](const Input &in) { return in.kind != Input::PointerIn || in.pointer != Pointer::Move; }),
                   g_inputs.end());
}

// The cursor (the page draws its own: the game hides the system's), the arrow of a 32-pixel cursor at 1080, over everything.
void DrawCursor(float x, float y) {
    static const ImVec2 kArrow[] = {{0, 0}, {0, 25}, {6, 19.5f}, {10.5f, 29}, {14.5f, 27.2f}, {10.2f, 18}, {18, 18}};
    ImVec2 pts[std::size(kArrow)];
    for (size_t i = 0; i < std::size(kArrow); ++i) pts[i] = P(x + kArrow[i].x * 1.1f, y + kArrow[i].y * 1.1f);
    ImDrawList *dl = ImGui::GetForegroundDrawList();
    dl->AddConcavePolyFilled(pts, static_cast<int>(std::size(pts)), C(0xFFFFFFFF));
    dl->AddPolyline(pts, static_cast<int>(std::size(pts)), C(0xFF000000), ImDrawFlags_Closed, 1.6f * g_p.s);
}

void OnClosed() {
    g_ui.modal = Modal{};
    g_ui.column = Column::None;
    g_ui.column_closing = false;
}

} // namespace

// ================================================================ what core.cpp calls (internal.h)

void SetView(std::shared_ptr<const OverlayView> view) {
    std::lock_guard<std::mutex> lock(g_vlock);
    g_view = std::move(view);
}

std::shared_ptr<const OverlayView> View() {
    std::lock_guard<std::mutex> lock(g_vlock);
    return g_view;
}

bool PageOpen() { return g_open.load(); }
bool PageShowing() { return g_open.load() || g_ui.was_open || (g_ui.changed_at && NowMs() - g_ui.changed_at < 160); }

bool OpenPage() {
    if (!View()) return false;
    g_open = true;
    return true;
}

void ClosePage() { g_open = false; }

void PageNav(Nav nav, bool keyboard) {
    if (g_open.load() && nav != Nav::None && g_inputs.size() < 64) g_inputs.push_back({Input::NavIn, static_cast<int>(nav), keyboard});
}

void PagePointer(Pointer kind, float x, float y, int value) {
    if (!g_open.load()) return;
    Input in{Input::PointerIn, value, false};
    in.pointer = kind;
    in.x = x, in.y = y;
    // a run of moves is the last of them (a fast mouse sends hundreds a second)
    if (kind == Pointer::Move && !g_inputs.empty() && g_inputs.back().kind == Input::PointerIn && g_inputs.back().pointer == Pointer::Move) {
        g_inputs.back() = in;
        return;
    }
    if (g_inputs.size() < 64) g_inputs.push_back(in);
}

void PagePad(bool present) { g_ui.pad_present = present; }

void SetGameTexture(uint32_t texture, unsigned width, unsigned height) {
    g_game_texture = texture;
    g_game_w = width;
    g_game_h = height;
}

void DrawPage(float width, float height, float dt) {
    Ui &u = g_ui;
    bool open = g_open.load();
    const uint64_t now = NowMs();
    auto view = View();
    if (open && !view) { // the launcher went away: the pad goes back to the game
        g_open = false;
        open = false;
        Log("ingame: the page closed: the launcher stopped answering");
    }
    if (open != u.was_open) {
        u.was_open = open;
        u.changed_at = now ? now : 1;
        if (open) OnOpened(*view);
        else OnClosed();
        SendShow();
        Log("ingame: the page %s", open ? "opened" : "closed");
    }
    const float t = static_cast<float>(now - u.changed_at);
    const float alpha = open ? std::min(1.0f, t / 160.0f) : 1 - std::min(1.0f, t / 140.0f);
    if (!view || alpha <= 0 || !Fonts()) {
        if (!open) g_inputs.clear();
        return;
    }
    const OverlayView &v = *view;
    // the stage: 1080 units high (a screen narrower than 1600 units scales by its width)
    const float s = std::min(height / 1080.0f, width / 1600.0f);
    g_p.dl = ImGui::GetBackgroundDrawList();
    g_p.s = s;
    g_p.W = width / s;
    g_p.H = height / s;
    g_p.alpha = EaseOut(alpha);
    g_p.pal = v.palette;
    if (open) ProcessInputs();
    u.ring_angle = static_cast<float>(now % 5000) / 5000.0f * 6.2831853f;
    // under the mouse (while it shows): Brush.Hover on that stop or hint this frame
    u.hover.clear();
    u.hover_hint = 0;
    if (open && u.mouse_known && u.hands != Hands::Gamepad && !(u.pressed && u.dragged)) {
        if (const Stop *hit = StopAt(u.mx, u.my)) u.hover = hit->id;
        else if (!u.modal.open) u.hover_hint = HintAt(u.mx, u.my);
    }
    u.hints.clear();
    u.regions.clear();
    u.stops.clear();
    // the column's player left the lobby: it closes (the page's stops are this frame's again)
    if (ColumnUp() && std::none_of(v.lobby.players.begin(), v.lobby.players.end(), [&](const LobbyPlayer &pl) { return pl.id == u.card; })) CloseColumn();
    // a column: the page fades out as it slides in (and back as it slides out)
    const float column_k = ColumnProgress(now);
    const bool column = u.column != Column::None;
    const float page_alpha = g_p.alpha;
    if (column) g_p.alpha = page_alpha * (1 - column_k);
    // the page: the background, the header, the entries, the pane, the hint bar
    RectFill(0, 0, g_p.W, g_p.H, v.palette.background);
    DrawHeader(v);
    // the entry shown may have gone: back to Game
    if (std::none_of(v.entries.begin(), v.entries.end(), [&](const OverlayEntry &e) { return e.key == u.entry; })) {
        u.entry = "game";
        u.focus = "list:game";
        SendShow();
    }
    DrawList(v);
    const OverlayEntry *entry = nullptr;
    for (const OverlayEntry &e : v.entries)
        if (e.key == u.entry) entry = &e;
    if (u.entry == "game") {
        DrawGame(v);
    } else if (entry && !entry->soon && u.entry == "lobby") {
        DrawPaneTitle(v.lobby.title, v.lobby.subtitle, v.palette.text_muted);
        DrawLobby(v, dt);
    } else if (entry) {
        DrawPaneTitle(entry->title, "", v.palette.text_muted);
    }
    // the focus the opening asked for: the first stop of the pane once drawn, else its entry in the list
    if (u.focus == "pane.pending") {
        if (const Stop *first = FirstPaneStop()) SetFocus(first->id, kPane);
        else FocusList();
    }
    DrawHints(v);
    if (column) {
        g_p.alpha = page_alpha;
        if (ColumnUp()) { // the page under it takes nothing: the column's stops, hints and viewer only
            u.stops.clear();
            u.hints.clear();
            u.regions.clear();
        }
        DrawColumn(v, column_k, dt);
    }
    if (u.modal.open) {
        // the modal's stops only
        u.stops.erase(std::remove_if(u.stops.begin(), u.stops.end(), [](const Stop &st) { return st.area != kModal; }), u.stops.end());
        DrawModal();
    }
    // a focus that is no longer a stop (its line went): the nearest stop of the same place takes it
    if (!FindStop(u.focus) && u.focus.find("pending") == std::string::npos) {
        if (u.modal.open) u.focus = "modal.bottom";
        else if (ColumnUp()) FocusColumnFirst();
        else if (const Stop *first = FirstPaneStop(); first && u.focus.rfind("list:", 0) != 0) SetFocus(first->id, kPane);
        else FocusList();
    }
    // the cursor, as the launcher's: hidden while the pad is in use, shown for the keyboard and the mouse
    if (open && u.mouse_known && u.hands != Hands::Gamepad) DrawCursor(u.mx, u.my);
}

std::string PageState() {
    std::string text = "{\"open\":";
    text += g_open.load() ? "true" : "false";
    text += ",\"entry\":\"" + JsonText(g_ui.entry) + "\",\"focus\":\"" + JsonText(g_ui.focus) + "\",\"column\":\"" +
            (g_ui.column == Column::Card ? "card" : "") + "\",\"closing\":" + (g_ui.column_closing ? "true" : "false") + ",\"card\":\"" +
            JsonText(g_ui.card) + "\",\"modal\":\"" + JsonText(g_ui.modal.open ? g_ui.modal.title : "") + "\",\"shown\":\"" + JsonText(g_ui.shown) +
            "\",\"input\":\"" + (g_ui.hands == Hands::Mouse ? "mouse" : g_ui.hands == Hands::Keyboard ? "keyboard" : "gamepad") + "\",\"stops\":[";
    bool first = true;
    char buf[64];
    for (const Stop &s : g_ui.stops) {
        snprintf(buf, sizeof(buf), "\",\"x\":%d,\"y\":%d}", static_cast<int>((s.x0 + s.x1) / 2 * g_p.s), static_cast<int>((s.y0 + s.y1) / 2 * g_p.s));
        text += (first ? "{\"id\":\"" : ",{\"id\":\"") + JsonText(s.id) + buf;
        first = false;
    }
    text += "]}";
    return text;
}

namespace test {
void ResetPage() {
    g_open = false;
    g_ui = Ui{};
    g_inputs.clear();
    g_game_texture = 0;
    g_game_w = g_game_h = 0;
    std::lock_guard<std::mutex> lock(g_vlock);
    g_view.reset();
}
} // namespace test

} // namespace mkwmp::ov
