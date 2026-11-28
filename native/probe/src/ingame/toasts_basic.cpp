// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-only; see LICENSE and NOTICE.
// The in-game toasts on plugin API 1 to 4 (the official mkw-recomp 0.1.18): the same cards, places and timing as
// toasts_exact.cpp, with what MkwOverlay offers there: filled rounded rectangles and text in the runtime menu's one
// font at its one size. So: no Nunito and no bold (the title is told apart by its colour), a letter in the disc in
// place of the line icon, the outline as two nested rectangles. No ImGui of the mod's is used here.
#include "internal.h"

#include <algorithm>
#include <string>
#include <vector>

namespace mkwmp::ov {

namespace {

constexpr float kNunitoLine = 1.364f; // the launcher font's line spacing: the cards keep its line heights

// 0xAARRGGBB with an alpha of 0..1 as MkwOverlay's 0xRRGGBBAA.
uint32_t Rgba(uint32_t argb, float alpha) {
    const float a = static_cast<float>(argb >> 24) * (alpha < 0 ? 0 : alpha > 1 ? 1 : alpha);
    return (argb & 0x00FFFFFF) << 8 | static_cast<uint32_t>(a + 0.5f);
}

float TextWidth(const MkwOverlay *o, const std::string &text) {
    float w = 0, h = 0;
    o->text_size(text.c_str(), &w, &h);
    return w;
}

// The lines of `text` at `width` pixels, broken at spaces (a word wider than the line is cut at a character).
std::vector<std::string> WrapBasic(const MkwOverlay *o, const std::string &text, float width) {
    std::vector<std::string> lines;
    std::string line;
    size_t at = 0;
    while (at < text.size()) {
        while (at < text.size() && (text[at] == ' ' || text[at] == '\n')) ++at;
        size_t end = at;
        while (end < text.size() && text[end] != ' ' && text[end] != '\n') ++end;
        if (end == at) break;
        std::string word = text.substr(at, end - at);
        at = end;
        const std::string joined = line.empty() ? word : line + " " + word;
        if (TextWidth(o, joined) <= width) {
            line = joined;
            continue;
        }
        if (!line.empty()) lines.push_back(line);
        line.clear();
        while (word.size() > 1 && TextWidth(o, word) > width) { // one word wider than the line: cut at a character
            size_t n = word.size();
            while (n > 0) {
                --n;
                while (n > 0 && (static_cast<unsigned char>(word[n]) & 0xC0) == 0x80) --n;
                if (n == 0 || TextWidth(o, word.substr(0, n)) <= width) break;
            }
            if (n == 0) break; // one character wider than the line: it stays whole
            lines.push_back(word.substr(0, n));
            word.erase(0, n);
        }
        line = word;
        if (lines.size() > 16) break; // a toast is a line or two
    }
    if (!line.empty()) lines.push_back(line);
    return lines;
}

uint32_t KindColour(const Palette &pal, const std::string &kind) {
    return kind == "success" ? pal.good : kind == "warning" ? pal.warn : kind == "error" ? pal.bad : pal.accent;
}

// The letter that stands for the launcher's icon (the runtime's font has no icons).
const char *IconLetter(const Toast &t) {
    if (t.icon == "check" || t.kind == "success") return "OK";
    if (t.icon == "warning" || t.kind == "warning") return "!";
    if (t.icon == "error" || t.kind == "error") return "X";
    if (t.icon == "userPlus") return "+";
    if (t.icon == "play") return ">";
    return "i";
}

float Card1(const MkwOverlay *o, const Palette &pal, const Card &c, float x, float y, float alpha, float s, bool draw) {
    const Toast &t = c.toast;
    const float rule = kRule, lh = o->line_height > 1 ? o->line_height : 18 * s;
    const float text_w = (kStackWidth - 2 * rule - 2 * kCardPadX - kCardDisc - kCardGap) * s;
    const auto title = WrapBasic(o, t.title, text_w);
    const auto body = t.body.empty() ? std::vector<std::string>{} : WrapBasic(o, t.body, text_w);
    // the launcher's line heights (28 and 24 units of Nunito; the body's LineHeight 32), never shorter than the runtime font's
    const float title_line = std::max(lh, 28 * kNunitoLine * s), body_line = std::max(lh, std::max(32.0f, 24 * kNunitoLine) * s);
    const float text_h = static_cast<float>(std::max<size_t>(1, title.size())) * title_line +
                         (body.empty() ? 0 : 2 * s + static_cast<float>(body.size()) * body_line);
    const float content = std::max(kCardDisc * s, text_h);
    const float h = (2 * rule + 2 * kCardPadY) * s + content;
    if (!draw) return h;
    const float w = kStackWidth * s, r = kRadius * s, e = rule * s;
    o->rect(x, y, x + w, y + h, Rgba(t.kind == "error" ? pal.bad : pal.rule, alpha), r);
    o->rect(x + e, y + e, x + w - e, y + h - e, Rgba(pal.toast, alpha), r - e);
    const float left = x + (rule + kCardPadX) * s, top = y + (rule + kCardPadY) * s;
    const uint32_t tint = KindColour(pal, t.kind);
    const float disc = kCardDisc * s;
    o->rect(left, top, left + disc, top + disc, Rgba(tint, alpha * 0.13f), disc / 2);
    const char *letter = IconLetter(t);
    float gw = 0, gh = 0;
    o->text_size(letter, &gw, &gh);
    o->text(left + (disc - gw) / 2, top + (disc - gh) / 2, Rgba(tint, alpha), letter);
    float ty = top + (content - text_h) / 2;
    const float tx = left + disc + kCardGap * s;
    for (const std::string &line : title) {
        o->text(tx, ty + (title_line - lh) / 2, Rgba(pal.text, alpha), line.c_str());
        ty += title_line;
    }
    if (title.empty()) ty += title_line;
    if (!body.empty()) {
        ty += 2 * s;
        for (const std::string &line : body) {
            o->text(tx, ty + (body_line - lh) / 2, Rgba(pal.text_muted, alpha), line.c_str());
            ty += body_line;
        }
    }
    return h;
}

} // namespace

void DrawToastsBasic(const ToastStack &stack, const MkwOverlay *o, float top, uint64_t now, std::vector<ToastBox> *boxes) {
    if (!o || !o->rect || !o->text || !o->text_size) return;
    const float s = o->height / 1080.0f;
    const float x0 = o->width - (kStackRight + kStackWidth) * s;
    float y = top;
    for (const Card &c : stack.Cards()) {
        const CardAnim anim = ToastStack::Anim(c, now);
        const float x = x0 + anim.shift * s;
        const float h = Card1(o, stack.Colours(), c, x, y, anim.alpha, s, anim.alpha > 0);
        if (boxes) boxes->push_back({c.toast.id, x, y, kStackWidth * s, h, anim.alpha});
        y += h + kGap * s;
    }
}

} // namespace mkwmp::ov
