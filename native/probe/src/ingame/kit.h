// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-only; see LICENSE and NOTICE.
// The in-game overlay's shared kit, for the toasts (toasts_exact.cpp) and the page (page.cpp): Nunito at the launcher's
// sizes, its line icons, its colours and a few text helpers. Ported from Infinity's overlay (overlay/src/v4/kit_v4.*)
// without its WinHTTP (the mod's own HTTP is used) and with the fonts as an atlas of the mod's own, built off the
// present thread (plugin API 5: the atlas is a texture of the plugin's).
#pragma once

#include <cstdint>
#include <string>
#include <utility>
#include <vector>

#include "imgui.h"

namespace mkwmp::ov {

// ---------------------------------------------------------------- the look
constexpr float kLine = 1.364f; // Nunito's line spacing (hhea 1011 + 353 per 1000): WPF's line height of a font size

ImU32 Col(uint32_t argb, float alpha = 1.0f);
// A font size in WPF units (the em) as ImGui's size (the line height) in pixels.
inline float Px(float em, float s) { return em * kLine * s; }

enum class Weight : uint8_t { Regular, SemiBold, Bold, ExtraBold };

// The toasts' own fonts (baked at their sizes).
struct ToastFonts {
    ImFont *regular = nullptr;   // 24
    ImFont *semibold = nullptr;  // 25
    ImFont *bold = nullptr;      // 28
    ImFont *extrabold = nullptr; // 30
};

// One bake of the fonts for a picture height: an atlas of its own, built on any thread (nothing of ImGui's context is
// touched), then handed to the present thread, which draws with it (UseFonts).
struct FontSet {
    struct Bake {
        Weight weight;
        float em;
        float px = 0; // the pixel size it was baked at
        ImFont *font = nullptr;
    };
    ImFontAtlas *atlas = nullptr;
    ToastFonts toast;
    std::vector<Bake> bakes;
    float scale = 1;      // the scale it was baked for (the picture's height / 1080, at least 0.5)
    float page_scale = 1; // the page's bakes: at most 1.5 (a 4K picture stays small in memory)
    bool nunito = false;  // false: the embedded fonts could not be read, ImGui's own font is in their place
};
// NULL when the atlas cannot be built. `scale`: the picture's height / 1080.
FontSet *BuildFonts(float scale);
void FreeFonts(FontSet *set);
// The present thread: the set every helper below draws with from now on (NULL: none).
void UseFonts(FontSet *set);
FontSet *Fonts();

const ToastFonts &Toasts();
// The baked font to draw `em` of that weight with at the scale the fonts were baked for (the nearest bake at least as large).
ImFont *FontFor(Weight weight, float em);

ImVec2 Measure(ImFont *font, float em, float s, const char *text, const char *end = nullptr);
// The lines of `text` wrapped at `width` pixels (WPF's TextWrapping=Wrap: at word breaks).
std::vector<std::pair<const char *, const char *>> Wrap(ImFont *font, float size, const std::string &text, float width);
// WPF's TextTrimming=CharacterEllipsis: as much as fits, then "…".
std::string Trim(ImFont *font, float size, const std::string &text, float width);

// The launcher's icon `name` (Themes/Icons.xaml, 24 x 24) in a box of `size` pixels at `origin`, stroked `thickness` grid units
// with round caps and joins (c:Icon).
void DrawIcon(ImDrawList *dl, const std::string &name, ImVec2 origin, float size, ImU32 color, float thickness = 2.0f);
// A solid icon (IconFill.*): its first figure filled, the others (its holes) filled with `hole`.
void FillIcon(ImDrawList *dl, const std::string &name, ImVec2 origin, float size, ImU32 color, ImU32 hole);

} // namespace mkwmp::ov
