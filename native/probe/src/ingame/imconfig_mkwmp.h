// Dear ImGui's compile-time options for the mod's own copy (third_party/imgui, v1.91.8 as Infinity's overlay vendors
// it): every translation unit that includes imgui.h gets this first (-DIMGUI_USER_CONFIG, build_plugin.py). The mod
// uses ImGui as a draw-list library only (no windows, no widgets, no files), inside the game's process:
//   - no assert may end the game (a failed one would abort() outside the runtime's fault guard);
//   - nothing of the system's is touched: no clipboard, no IME, no shell, no ini or log file, no debug tools.
// None of these changes what is drawn.
#pragma once

#define IM_ASSERT(_EXPR) ((void)(_EXPR))
#define IMGUI_DISABLE_DEMO_WINDOWS
#define IMGUI_DISABLE_DEBUG_TOOLS
#define IMGUI_DISABLE_WIN32_FUNCTIONS
#define IMGUI_DISABLE_DEFAULT_SHELL_FUNCTIONS
#define IMGUI_DISABLE_FILE_FUNCTIONS
#define IMGUI_DISABLE_OBSOLETE_FUNCTIONS
