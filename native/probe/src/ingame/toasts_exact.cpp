// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-only; see LICENSE and NOTICE.
// The in-game toasts, plugin API 5: the launcher's ToastHost card (Controls/ToastHost.xaml) drawn with the mod's own
// Dear ImGui: sizes in the launcher's stage units (1080 high) scaled to the picture, its colours (sent in the answer:
// the player's theme), Nunito, its line icons. Ported from Infinity's overlay (overlay/src/v4/toasts_v4.cpp: Frame,
// ToastCard, DrawToasts), without the invite toasts.
#include "internal.h"
#include "kit.h"

#include <algorithm>
#include <cmath>

#include "imgui.h"

namespace mkwmp::ov {

namespace {

// The card's frame: its background, its outline (WPF's Border: the outline inside the outer edge, corner radius 18).
void Frame(ImDrawList *dl, const Palette &pal, ImVec2 a, ImVec2 b, float rule, uint32_t outline, float alpha, float s) {
    const float r = kRadius * s, w = rule * s;
    dl->AddRectFilled(ImVec2(a.x + w * 0.5f, a.y + w * 0.5f), ImVec2(b.x - w * 0.5f, b.y - w * 0.5f), Col(pal.toast, alpha), r - w * 0.5f);
    dl->AddRect(ImVec2(a.x + w * 0.5f, a.y + w * 0.5f), ImVec2(b.x - w * 0.5f, b.y - w * 0.5f), Col(outline, alpha), r - w * 0.5f, 0, w);
}

uint32_t KindColour(const Palette &pal, const std::string &kind) {
    return kind == "success" ? pal.good : kind == "warning" ? pal.warn : kind == "error" ? pal.bad : pal.accent;
}

// A toast: the disc with the kind's icon, the title, the body. Its height in pixels.
float ToastCard(ImDrawList *dl, const Palette &pal, const Card &c, float x, float y, float alpha, float s, bool draw) {
    const Toast &t = c.toast;
    const float rule = kRule;
    const float pad_x = kCardPadX, pad_y = kCardPadY, disc = kCardDisc, gap = kCardGap;
    const float text_w = (kStackWidth - 2 * rule - 2 * pad_x - disc - gap) * s;
    const auto title = Wrap(Toasts().bold, Px(28, s), t.title, text_w);
    const auto body = t.body.empty() ? std::vector<std::pair<const char *, const char *>>{} : Wrap(Toasts().regular, Px(24, s), t.body, text_w);
    const float title_line = 28 * kLine, body_line = std::max(32.0f, 24 * kLine); // the body's LineHeight 32 (a line is never shorter than the font's)
    const float text_h = static_cast<float>(title.size()) * title_line + (body.empty() ? 0 : 2 + static_cast<float>(body.size()) * body_line);
    const float content = std::max(disc, text_h);
    const float h = (2 * rule + 2 * pad_y + content) * s;
    if (!draw) return h;
    const ImVec2 a(x, y), b(x + kStackWidth * s, y + h);
    Frame(dl, pal, a, b, rule, t.kind == "error" ? pal.bad : pal.rule, alpha, s);
    const float left = x + (rule + pad_x) * s, top = y + (rule + pad_y) * s;
    const uint32_t tint = KindColour(pal, t.kind);
    const bool update = t.kind == "update";
    const ImVec2 centre(left + disc * s / 2, top + disc * s / 2);
    dl->AddCircleFilled(centre, disc * s / 2, Col(tint, alpha * (update ? 1.0f : 0.13f)), 64);
    DrawIcon(dl, t.icon, ImVec2(centre.x - 19 * s, centre.y - 19 * s), 38 * s, Col(update ? pal.on_accent : tint, alpha));
    float ty = top + (content - text_h) / 2 * s;
    const float tx = left + (disc + gap) * s;
    for (const auto &line : title) {
        dl->AddText(Toasts().bold, Px(28, s), ImVec2(tx, ty), Col(pal.text, alpha), line.first, line.second);
        ty += title_line * s;
    }
    if (!body.empty()) {
        ty += 2 * s;
        for (const auto &line : body) {
            dl->AddText(Toasts().regular, Px(24, s), ImVec2(tx, ty + (body_line - 24 * kLine) / 2 * s), Col(pal.text_muted, alpha), line.first, line.second);
            ty += body_line * s;
        }
    }
    return h;
}

} // namespace

void DrawToastsExact(const ToastStack &stack, float width, float height, float top, uint64_t now, std::vector<ToastBox> *boxes) {
    if (!Toasts().bold) return;
    ImDrawList *dl = ImGui::GetForegroundDrawList();
    const float s = height / 1080.0f;
    const float x0 = width - (kStackRight + kStackWidth) * s;
    float y = top;
    for (const Card &c : stack.Cards()) {
        const CardAnim anim = ToastStack::Anim(c, now);
        const float x = x0 + anim.shift * s;
        const float h = ToastCard(dl, stack.Colours(), c, x, y, anim.alpha, s, anim.alpha > 0);
        if (boxes) boxes->push_back({c.toast.id, x, y, kStackWidth * s, h, anim.alpha});
        y += h + kGap * s;
    }
}

} // namespace mkwmp::ov
