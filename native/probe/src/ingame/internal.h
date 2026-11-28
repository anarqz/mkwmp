// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-only; see LICENSE and NOTICE.
// The in-game overlay's parts, for each other and for the host-side tests (tests/test_ingame.cpp). C++; what the C
// core calls is ../ingame.h.
#pragma once

#include "../ingame.h"
#include "../mkw_plugin.h"
#include "cards.h"
#include "model.h"

#include <cstdint>
#include <memory>
#include <string>
#include <vector>

namespace mkwmp::ov {

// ---------------------------------------------------------------- the host (core.cpp)
const IngameHost &Host();
void Log(const char *format, ...) __attribute__((format(printf, 1, 2)));
uint64_t NowMs();

// ---------------------------------------------------------------- the toasts
// Where a card was drawn this frame (pixels), for the tests and the state line.
struct ToastBox {
    std::string id;
    float x = 0, y = 0, w = 0, h = 0, alpha = 0;
};
// The stack's cards from `top` pixels down, at the right edge. Exact: Infinity's card on ImGui's foreground list
// (inside the mod's own ImGui frame; plugin API 5). Basic: the same layout and timing with the runtime's rect and text
// (plugin API 4: its one font, a letter for the icon).
void DrawToastsExact(const ToastStack &stack, float width, float height, float top, uint64_t now, std::vector<ToastBox> *boxes);
void DrawToastsBasic(const ToastStack &stack, const MkwOverlay *o, float top, uint64_t now, std::vector<ToastBox> *boxes);

// ---------------------------------------------------------------- the page (page.cpp; plugin API 5)
enum class Nav : uint8_t { None, Up, Down, Left, Right, Accept, Back, X, Y };
enum class Pointer : uint8_t { Move, LeftDown, LeftUp, Wheel, Back };

// The launcher's view, from the view poller (any thread); null: none, or the launcher stopped answering.
void SetView(std::shared_ptr<const OverlayView> view);
std::shared_ptr<const OverlayView> View();

bool PageOpen();            // any thread
bool PageShowing();         // the present thread: open, or still fading out
// Any thread: opens the page (false: no view to show), closes it. The present thread does the rest at its next frame.
bool OpenPage();
void ClosePage();
// The present thread, before DrawPage, while the page has the input.
void PageNav(Nav nav, bool keyboard);
void PagePointer(Pointer kind, float x, float y, int value); // x, y: the picture's fraction (kNoPoint: where it was)
void PagePad(bool present);                                  // a pad is there (the hint bar's "connected" dot)
constexpr float kNoPoint = -1e9f;
// The game's picture for the Game pane's box and the pane title's (0: none yet; the box shows the gamepad icon).
void SetGameTexture(uint32_t texture, unsigned width, unsigned height);
// One frame of the page on ImGui's background list (nothing while it is closed and faded out).
void DrawPage(float width, float height, float dt);
// The page's state for the tests and the log: {"open","entry","focus","column","card","modal","stops":[ids]}.
std::string PageState();

// ---------------------------------------------------------------- the player's actions (core.cpp)
// POST /local/v1/overlay/action {"action","args"} on the action thread; `args` is a JSON object.
void Act(const std::string &action, const std::string &args = "{}");

// ---------------------------------------------------------------- the tests' handles (core.cpp)
namespace test {
// Everything back to "not installed" (the fonts are kept: they are slow to bake).
void Reset();
void ResetPage(); // page.cpp's part of it
// One step of each thread's loop, on the caller's thread: what the thread would sleep before its next step, in ms.
uint32_t ToastPollStep();
uint32_t ViewPollStep();
bool ActionStep();   // false: no action was waiting
void BuildFontsNow(); // what the font thread does, for the size the last frame had
ToastStack &Stack();
const std::vector<ToastBox> &Boxes(); // the last frame's cards
void SetClock(uint64_t (*now)());
} // namespace test

} // namespace mkwmp::ov
