// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-only; see LICENSE and NOTICE.
// The in-game overlay's shared kit (kit.h). Nunito is embedded (OFL, fonts.c); the icons are the launcher's
// Themes/Icons.xaml paths, stroked or filled here. Ported from Infinity's overlay (overlay/src/v4/kit_v4.cpp).
#include "kit.h"

#include <algorithm>
#include <cfloat>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>

extern "C" {
extern const unsigned char mkwmp_font_regular[], mkwmp_font_semibold[], mkwmp_font_bold[], mkwmp_font_extrabold[];
extern const size_t mkwmp_font_regular_size, mkwmp_font_semibold_size, mkwmp_font_bold_size, mkwmp_font_extrabold_size;
}

namespace mkwmp::ov {

// ---------------------------------------------------------------- the look

ImU32 Col(uint32_t argb, float alpha) {
    const float a = static_cast<float>(argb >> 24) * (alpha < 0 ? 0 : alpha > 1 ? 1 : alpha);
    return IM_COL32((argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF, static_cast<int>(a + 0.5f));
}

ImVec2 Measure(ImFont *font, float em, float s, const char *text, const char *end) {
    return font->CalcTextSizeA(Px(em, s), FLT_MAX, 0, text, end);
}

std::vector<std::pair<const char *, const char *>> Wrap(ImFont *font, float size, const std::string &text, float width) {
    std::vector<std::pair<const char *, const char *>> lines;
    const char *at = text.c_str(), *end = at + text.size();
    const float scale = size / font->FontSize;
    while (at < end) {
        const char *cut = font->CalcWordWrapPositionA(scale, at, end, width);
        if (cut == at) cut = at + 1; // a glyph wider than the line: one per line
        const char *line_end = cut;
        while (line_end > at && (line_end[-1] == ' ' || line_end[-1] == '\n')) --line_end;
        lines.emplace_back(at, line_end);
        at = cut;
        while (at < end && (*at == ' ' || *at == '\n')) ++at;
    }
    if (lines.empty()) lines.emplace_back(at, at);
    return lines;
}

std::string Trim(ImFont *font, float size, const std::string &text, float width) {
    if (font->CalcTextSizeA(size, FLT_MAX, 0, text.c_str()).x <= width) return text;
    std::string cut = text;
    while (!cut.empty()) {
        size_t n = cut.size() - 1;
        while (n > 0 && (static_cast<unsigned char>(cut[n]) & 0xC0) == 0x80) --n;
        cut.resize(n);
        const std::string shown = cut + "\xE2\x80\xA6";
        if (font->CalcTextSizeA(size, FLT_MAX, 0, shown.c_str()).x <= width) return shown;
    }
    return "\xE2\x80\xA6";
}

// ---------------------------------------------------------------- the icons (Themes/Icons.xaml: 24 x 24, stroked 2, round)
namespace {

struct SubPath {
    std::vector<ImVec2> points;
    std::vector<bool> corner; // a vertex of a line command (gets a round join), not a point of a curve
    bool closed = false;
};

std::vector<SubPath> ParsePath(const char *d) {
    std::vector<SubPath> paths;
    ImVec2 at(0, 0), start(0, 0);
    char command = 0;
    auto number = [&d]() {
        while (*d == ' ' || *d == ',') ++d;
        char *stop = nullptr;
        const float v = strtof(d, &stop);
        d = stop;
        return v;
    };
    auto add = [&paths](ImVec2 p, bool corner) {
        paths.back().points.push_back(p);
        paths.back().corner.push_back(corner);
    };
    while (*d) {
        while (*d == ' ' || *d == ',') ++d;
        if (!*d) break;
        if ((*d >= 'A' && *d <= 'Z')) command = *d++;
        switch (command) {
        case 'M': {
            const float x = number(), y = number();
            at = start = ImVec2(x, y);
            paths.push_back({});
            add(at, true);
            command = 'L';
            break;
        }
        case 'L': { const float x = number(), y = number(); at = ImVec2(x, y); add(at, true); break; }
        case 'H': at.x = number(); add(at, true); break;
        case 'V': at.y = number(); add(at, true); break;
        case 'C': {
            const float x1 = number(), y1 = number(), x2 = number(), y2 = number(), x = number(), y = number();
            const ImVec2 p0 = at;
            for (int i = 1; i <= 16; ++i) {
                const float t = i / 16.0f, u = 1 - t;
                add(ImVec2(u * u * u * p0.x + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x,
                           u * u * u * p0.y + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y), i == 16);
            }
            at = ImVec2(x, y);
            break;
        }
        case 'A': { // SVG's endpoint arc (circular here: rx == ry, no rotation)
            float r = number();
            number(); // ry (== rx in these icons)
            number(); // the rotation (0)
            const bool large = number() != 0, sweep = number() != 0;
            const float x2 = number(), y2 = number();
            const float x1 = at.x, y1 = at.y;
            const float hx = (x1 - x2) / 2, hy = (y1 - y2) / 2;
            const float lambda = (hx * hx + hy * hy) / (r * r);
            if (lambda > 1) r *= std::sqrt(lambda);
            const float num = r * r * r * r - r * r * hy * hy - r * r * hx * hx, den = r * r * hy * hy + r * r * hx * hx;
            const float coef = (large != sweep ? 1.0f : -1.0f) * std::sqrt(std::max(0.0f, den > 0 ? num / den : 0.0f));
            const float cxp = coef * hy, cyp = -coef * hx;
            const float cx = cxp + (x1 + x2) / 2, cy = cyp + (y1 + y2) / 2;
            const float a1 = std::atan2((hy - cyp) / r, (hx - cxp) / r);
            float da = std::atan2((-hy - cyp) / r, (-hx - cxp) / r) - a1;
            const float pi = 3.14159265f;
            if (!sweep && da > 0) da -= 2 * pi;
            if (sweep && da < 0) da += 2 * pi;
            const int steps = std::max(8, static_cast<int>(std::fabs(da) / (pi / 24)));
            for (int i = 1; i <= steps; ++i) {
                const float a = a1 + da * static_cast<float>(i) / static_cast<float>(steps);
                add(ImVec2(cx + r * std::cos(a), cy + r * std::sin(a)), i == steps);
            }
            at = ImVec2(x2, y2);
            break;
        }
        case 'Z':
            if (!paths.empty()) {
                paths.back().closed = true;
                auto &p = paths.back().points;
                if (p.size() > 1 && std::fabs(p.back().x - start.x) < 1e-3f && std::fabs(p.back().y - start.y) < 1e-3f) {
                    p.pop_back();
                    paths.back().corner.pop_back();
                }
            }
            at = start;
            command = 0;
            break;
        default: ++d; break; // not in these icons
        }
    }
    return paths;
}

struct Icon {
    const char *name;
    const char *path;
    std::vector<SubPath> parsed;
};

Icon g_icons[] = {
    {"info", "M3,12 A9,9 0 1 0 21,12 A9,9 0 1 0 3,12 Z M12,11 V17 M12,7.4 L12,7.5", {}},
    {"check", "M4.5,12.5 L10,18 L19.5,6.5", {}},
    {"warning", "M12,3.5 L22,20.5 H2 Z M12,10 V15 M12,17.7 L12,17.8", {}},
    {"error", "M3,12 A9,9 0 1 0 21,12 A9,9 0 1 0 3,12 Z M9,9 L15,15 M15,9 L9,15", {}},
    {"friends", "M5.5,7.5 A3.5,3.5 0 1 0 12.5,7.5 A3.5,3.5 0 1 0 5.5,7.5 Z M2.5,20 C2.5,16 5.5,14 9,14 C12.5,14 15.5,16 15.5,20 "
                "M15.5,4.3 A3.5,3.5 0 0 1 15.5,10.7 M17.5,14.3 C19.8,15 21.5,17 21.5,20", {}},
    {"download", "M12,3.5 V15 M7,10.5 L12,15.5 L17,10.5 M4,20 H20", {}},
    {"play", "M8,5 L19,12 L8,19 Z", {}},
    {"userPlus", "M5.5,8 A3.5,3.5 0 1 0 12.5,8 A3.5,3.5 0 1 0 5.5,8 Z M2.5,20 C2.5,16 5.5,14.5 9,14.5 C12.5,14.5 15.5,16 15.5,20 M19,8 V14 M16,11 H22", {}},
    {"micOff", "M12,3 A3,3 0 0 1 15,6 V11 A3,3 0 0 1 9,11 V6 A3,3 0 0 1 12,3 Z M5.5,11 A6.5,6.5 0 0 0 18.5,11 M12,17.5 V21 M8.5,21 H15.5 M4,3.5 L20,20.5", {}},
    {"gamepad", "M7,7 H17 A5,5 0 0 1 22,12 V15 A3,3 0 0 1 16.6,16.8 L15.4,15 H8.6 L7.4,16.8 A3,3 0 0 1 2,15 V12 A5,5 0 0 1 7,7 Z M7.5,10 V13 M6,11.5 H9 "
                "M15.5,10.6 L15.5,10.7 M17.8,12.6 L17.8,12.7", {}},
    {"headset", "M4.5,14 V12 A7.5,7.5 0 0 1 19.5,12 V14 M4.5,13.5 H7 V19 H5.5 A1,1 0 0 1 4.5,18 Z M19.5,13.5 H17 V19 H18.5 A1,1 0 0 0 19.5,18 Z", {}},
    {"settings", "M10.66,2.49 L13.34,2.49 L13.86,5.05 L15.6,5.76 L17.78,4.33 L19.67,6.22 L18.24,8.4 L18.95,10.14 L21.51,10.66 L21.51,13.34 L18.95,13.86 "
                 "L18.24,15.6 L19.67,17.78 L17.78,19.67 L15.6,18.24 L13.86,18.95 L13.34,21.51 L10.66,21.51 L10.14,18.95 L8.4,18.24 L6.22,19.67 L4.33,17.78 "
                 "L5.76,15.6 L5.05,13.86 L2.49,13.34 L2.49,10.66 L5.05,10.14 L5.76,8.4 L4.33,6.22 L6.22,4.33 L8.4,5.76 L10.14,5.05 Z M8.8,12 A3.2,3.2 0 1 0 15.2,12 "
                 "A3.2,3.2 0 1 0 8.8,12 Z", {}},
    {"power", "M12,3 V12 M7,5.8 A8,8 0 1 0 17,5.8", {}},
    {"refresh", "M20,12 A8,8 0 1 1 17.6,6.3 M18.5,2.8 V7 H14.3", {}},
    {"globe", "M3,12 A9,9 0 1 0 21,12 A9,9 0 1 0 3,12 Z M3,12 H21 M12,3 C8.5,6 8.5,18 12,21 M12,3 C15.5,6 15.5,18 12,21", {}},
    {"signal", "M2.5,9.5 A13.5,13.5 0 0 1 21.5,9.5 M5.8,13 A8.8,8.8 0 0 1 18.2,13 M9,16.5 A4.3,4.3 0 0 1 15,16.5 M12,20 L12,20.1", {}},
    {"chevronRight", "M9,5 L16,12 L9,19", {}},
    {"close", "M5,5 L19,19 M19,5 L5,19", {}},
    {"plus", "M12,5 V19 M5,12 H19", {}},
    {"menu", "M4,7 H20 M4,12 H20 M4,17 H20", {}},
    {"flag", "M5,21.5 V3.5 M5,4.5 H19 L16,9 L19,13.5 H5", {}},
    {"search", "M4,10.5 A6.5,6.5 0 1 0 17,10.5 A6.5,6.5 0 1 0 4,10.5 Z M15.5,15.5 L21,21", {}},
    {"user", "M8,8 A4,4 0 1 0 16,8 A4,4 0 1 0 8,8 Z M4,21 C4,16.5 7.5,14.5 12,14.5 C16.5,14.5 20,16.5 20,21", {}},
    {"trash", "M4,6.5 H20 M9,6.5 V4 H15 V6.5 M6,6.5 L7,20.5 H17 L18,6.5 M10,10.5 V16.5 M14,10.5 V16.5", {}},
    {"exit", "M14,4 H19.5 V20 H14 M4,12 H15 M8,8 L4,12 L8,16", {}},
    {"lock", "M6,10.5 H18 A1.5,1.5 0 0 1 19.5,12 V19.5 A1.5,1.5 0 0 1 18,21 H6 A1.5,1.5 0 0 1 4.5,19.5 V12 A1.5,1.5 0 0 1 6,10.5 Z M8,10.5 V7.5 A4,4 0 0 1 16,7.5 V10.5", {}},
    {"block", "M3,12 A9,9 0 1 0 21,12 A9,9 0 1 0 3,12 Z M5.6,5.6 L18.4,18.4", {}},
    {"keyboard", "M3.5,6 H20.5 A1.5,1.5 0 0 1 22,7.5 V16.5 A1.5,1.5 0 0 1 20.5,18 H3.5 A1.5,1.5 0 0 1 2,16.5 V7.5 A1.5,1.5 0 0 1 3.5,6 Z M6,10 H6.1 "
                 "M9.9,10 H10 M13.9,10 H14 M17.9,10 H18 M7.5,14 H16.5", {}},
    // MKWMP's own (not in Infinity's set): two sheets, for Copy Report
    {"copy", "M9,8.5 H19.5 V20.5 H9 Z M5.5,15.5 H4.5 V3.5 H15 V5", {}},
    // solid ones (IconFill.*): the first figure is the shape, the others are its holes
    {"fill.gamepad", "M7,5.5 H17 C20,5.5 21.6,8 22.1,11.5 L22.9,16.8 C23.2,19.4 20.4,20.6 18.9,18.7 L16.7,16 H7.3 L5.1,18.7 C3.6,20.6 0.8,19.4 1.1,16.8 "
                     "L1.9,11.5 C2.4,8 4,5.5 7,5.5 Z M6.6,8.4 H8.4 V9.8 H9.8 V11.6 H8.4 V13 H6.6 V11.6 H5.2 V9.8 H6.6 Z M15.6,9.2 A0.95,0.95 0 1 0 17.5,9.2 "
                     "A0.95,0.95 0 1 0 15.6,9.2 Z M17.5,10.7 A0.95,0.95 0 1 0 19.4,10.7 A0.95,0.95 0 1 0 17.5,10.7 Z M13.7,10.7 A0.95,0.95 0 1 0 15.6,10.7 "
                     "A0.95,0.95 0 1 0 13.7,10.7 Z M15.6,12.2 A0.95,0.95 0 1 0 17.5,12.2 A0.95,0.95 0 1 0 15.6,12.2 Z", {}},
};

Icon *FindIcon(const std::string &name) {
    for (auto &i : g_icons)
        if (name == i.name) {
            if (i.parsed.empty()) i.parsed = ParsePath(i.path);
            return &i;
        }
    return nullptr;
}

} // namespace

void DrawIcon(ImDrawList *dl, const std::string &name, ImVec2 origin, float size, ImU32 color, float thickness) {
    Icon *icon = FindIcon(name);
    if (!icon) icon = FindIcon("info");
    const float k = size / 24.0f, w = thickness * k;
    for (const SubPath &p : icon->parsed) {
        if (p.points.empty()) continue;
        for (const ImVec2 &q : p.points) dl->PathLineTo(ImVec2(origin.x + q.x * k, origin.y + q.y * k));
        dl->PathStroke(color, p.closed ? ImDrawFlags_Closed : ImDrawFlags_None, w);
        for (size_t i = 0; i < p.points.size(); ++i) {
            const bool end = !p.closed && (i == 0 || i + 1 == p.points.size());
            if (end || p.corner[i]) dl->AddCircleFilled(ImVec2(origin.x + p.points[i].x * k, origin.y + p.points[i].y * k), w / 2, color, 12);
        }
    }
}

void FillIcon(ImDrawList *dl, const std::string &name, ImVec2 origin, float size, ImU32 color, ImU32 hole) {
    Icon *icon = FindIcon(name);
    if (!icon) return;
    const float k = size / 24.0f;
    bool first = true;
    for (const SubPath &p : icon->parsed) {
        if (p.points.size() < 3) continue;
        std::vector<ImVec2> points;
        points.reserve(p.points.size());
        for (const ImVec2 &q : p.points) points.emplace_back(origin.x + q.x * k, origin.y + q.y * k);
        dl->AddConcavePolyFilled(points.data(), static_cast<int>(points.size()), first ? color : hole);
        first = false;
    }
}

// ---------------------------------------------------------------- the fonts
namespace {

FontSet *g_fonts = nullptr; // the present thread's
const ToastFonts g_no_fonts;

struct Embedded {
    const unsigned char *data;
    size_t size;
};

Embedded FontOf(Weight w) {
    switch (w) {
    case Weight::Regular: return {mkwmp_font_regular, mkwmp_font_regular_size};
    case Weight::SemiBold: return {mkwmp_font_semibold, mkwmp_font_semibold_size};
    case Weight::Bold: return {mkwmp_font_bold, mkwmp_font_bold_size};
    default: return {mkwmp_font_extrabold, mkwmp_font_extrabold_size};
    }
}

ImFont *EmbeddedFont(FontSet &set, Weight weight, float size, const ImWchar *ranges) {
    const Embedded e = FontOf(weight);
    if (!e.data || e.size < 1024) {
        set.nunito = false;
        return set.atlas->AddFontDefault();
    }
    ImFontConfig config;
    config.FontDataOwnedByAtlas = false; // the mod's own bytes
    config.OversampleH = 1;              // baked at the size drawn, snapped to pixels: as sharp, and a quarter of the memory
    config.OversampleV = 1;
    config.PixelSnapH = true;
    ImFont *font = set.atlas->AddFontFromMemoryTTF(const_cast<unsigned char *>(e.data), static_cast<int>(e.size), size, &config, ranges);
    if (!font) {
        set.nunito = false;
        return set.atlas->AddFontDefault();
    }
    return font;
}

} // namespace

FontSet *BuildFonts(float scale) {
    FontSet *set = new FontSet;
    set->atlas = new ImFontAtlas;
    set->atlas->Flags |= ImFontAtlasFlags_NoMouseCursors; // the page draws its own cursor
    set->nunito = true;
    const float s = scale < 0.5f ? 0.5f : scale;
    // the toasts: Latin with Latin-1 and Extended-A, the Romanian and Cyrillic letters, the general punctuation (… – — “ ” •), € and ™
    static const ImWchar ranges[] = {0x0020, 0x017F, 0x0218, 0x021B, 0x0400, 0x045F, 0x2010, 0x2027, 0x2030, 0x203A, 0x20AC, 0x20AC, 0x2122, 0x2122, 0};
    // the overlay page: the launcher's own texts (English, Portuguese: Latin-1, the general punctuation). MKWMP's page also shows
    // the names of the lobby's players, so the two bakes they are drawn with (Regular 32, SemiBold 42) have the toasts' letters
    static const ImWchar page[] = {0x0020, 0x00FF, 0x0152, 0x0153, 0x2010, 0x2027, 0x2030, 0x203A, 0x20AC, 0x20AC, 0x2122, 0x2122, 0x2190, 0x2193, 0};
    static const ImWchar initials[] = {0x0020, 0x005F, 0x00C0, 0x00DE, 0x0400, 0x042F, 0};
    set->toast.regular = EmbeddedFont(*set, Weight::Regular, Px(24, s), ranges);
    set->toast.semibold = EmbeddedFont(*set, Weight::SemiBold, Px(25, s), ranges);
    set->toast.bold = EmbeddedFont(*set, Weight::Bold, Px(28, s), ranges);
    set->toast.extrabold = EmbeddedFont(*set, Weight::ExtraBold, Px(30, s), ranges);
    // the overlay page: a few sizes each, drawn at the nearest larger one; baked at most for 1.5x (a 1620-line screen), so 4K
    // stays small in memory
    const float ps = s > 1.5f ? 1.5f : s;
    set->scale = s;
    set->page_scale = ps;
    set->bakes = {{Weight::Regular, 24, Px(24, s), set->toast.regular},     {Weight::SemiBold, 25, Px(25, s), set->toast.semibold},
                  {Weight::Bold, 28, Px(28, s), set->toast.bold},           {Weight::ExtraBold, 30, Px(30, s), set->toast.extrabold},
                  {Weight::Regular, 32, Px(32, ps), nullptr}, {Weight::Regular, 42, Px(42, ps), nullptr}, {Weight::SemiBold, 34, Px(34, ps), nullptr},
                  {Weight::SemiBold, 42, Px(42, ps), nullptr}, {Weight::Bold, 22, Px(22, ps), nullptr}, {Weight::ExtraBold, 60, Px(60, ps), nullptr}};
    for (FontSet::Bake &b : set->bakes) {
        if (b.font) continue;
        const bool names = (b.weight == Weight::Regular && b.em == 32) || (b.weight == Weight::SemiBold && b.em == 42);
        b.font = EmbeddedFont(*set, b.weight, b.px, b.weight == Weight::ExtraBold ? initials : names ? ranges : page);
        if (ps < s) b.px = Px(b.em, s); // drawn as if baked at the scale (upscaled a little on a large screen)
    }
    if (!set->atlas->Build()) {
        FreeFonts(set);
        return nullptr;
    }
    return set;
}

void FreeFonts(FontSet *set) {
    if (!set) return;
    if (g_fonts == set) g_fonts = nullptr;
    delete set->atlas;
    delete set;
}

void UseFonts(FontSet *set) { g_fonts = set; }
FontSet *Fonts() { return g_fonts; }

const ToastFonts &Toasts() { return g_fonts ? g_fonts->toast : g_no_fonts; }

ImFont *FontFor(Weight weight, float em) {
    if (!g_fonts) return nullptr;
    const float want = Px(em, g_fonts->scale) * 0.98f;
    ImFont *best = nullptr, *largest = nullptr;
    float best_px = FLT_MAX, largest_px = 0;
    for (const FontSet::Bake &b : g_fonts->bakes) {
        if (b.weight != weight || !b.font) continue;
        if (b.px >= want && b.px < best_px) { best = b.font; best_px = b.px; }
        if (b.px > largest_px) { largest = b.font; largest_px = b.px; }
    }
    return best ? best : largest ? largest : g_fonts->toast.regular;
}

} // namespace mkwmp::ov
