// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-only; see LICENSE and NOTICE.
// The in-game overlay's core (../ingame.h): the tier, the drawing the runtime calls on its present thread, the mod's
// own Dear ImGui frame handed over as triangles (plugin API 5: the counterpart of Infinity's dx12.cpp, a renderer
// back end of a hundred lines), the input the runtime hands the page, the fonts (baked on a thread of their own, an
// atlas that is a texture of the plugin's), and the threads that talk to the launcher: the two long polls and the
// actions (Infinity's PollThread, ViewThread and ActionThread, with their back-off).
//
// The present thread never waits: it copies what the pollers left under locks held for the copy only, and everything
// that talks to the launcher or bakes a font is another thread's. Nothing here reads guest memory or runs guest code.
// Every field of the runtime's tables is used only when the table's size reaches it, so a runtime with a shorter (or
// longer) table than the header this was built against works with what it has.
#include "internal.h"
#include "kit.h"

#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstdarg>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <vector>

#include "imgui.h"

namespace mkwmp::ov {

static_assert(sizeof(MkwDrawVert) == sizeof(ImDrawVert) && offsetof(MkwDrawVert, x) == offsetof(ImDrawVert, pos) &&
              offsetof(MkwDrawVert, u) == offsetof(ImDrawVert, uv) && offsetof(MkwDrawVert, col) == offsetof(ImDrawVert, col),
              "ImDrawVert is handed to the runtime as MkwDrawVert");
static_assert(sizeof(ImDrawIdx) == sizeof(uint16_t), "ImDrawIdx is handed to the runtime as MkwDrawList.idx");
static_assert(IM_COL32_R_SHIFT == 0 && IM_COL32_A_SHIFT == 24, "MkwDrawVert.col is the bytes R, G, B, A");

// A field of a size-versioned table of the runtime's is there when the table reaches past it.
#define OV_HAS(o, field) ((o)->size >= offsetof(MkwOverlay, field) + sizeof((o)->field))
#define API_HAS(a, field) ((a)->size >= offsetof(MkwPluginApi, field) + sizeof((a)->field) && (a)->field)

namespace {

void Draw(const MkwOverlay *o, void *user);

// ================================================================ the state
IngameHost g_host;
const MkwPluginApi *g_api = nullptr;
std::atomic<int> g_tier{INGAME_NONE};
std::atomic<bool> g_faulted{false};  // the drawing faulted: off for good, nothing is asked any more
std::atomic<bool> g_drawn{false};    // the runtime called the drawing once: the picture's size is known
std::atomic<float> g_height{0};      // the picture's, at the last frame
std::atomic<int> g_cards{0};         // cards on screen at the last frame
std::atomic<int> g_perf_mode{0};     // the runtime's own display at the top right, by its setting: 0 off, 1 fps, 2 full
std::atomic<bool> g_view_ok{false};  // the launcher answers GET /local/v1/overlay/view
std::atomic<bool> g_captured{false}; // the page has the input (overlay_capture)
std::atomic<uint64_t> g_toggled_at{0};
uint64_t (*g_clock)() = nullptr;     // the tests' clock

ToastStack *g_stack = new ToastStack;
std::vector<ToastBox> g_boxes; // the present thread's: this frame's cards

// ---- the fonts (the font thread bakes, the present thread adopts)
std::mutex g_font_lock;
std::condition_variable g_font_cv;
float g_font_want = 0;         // the scale asked for (0: none yet); under g_font_lock
float g_font_done = 0;         // the scale the last bake was for (a failed one too)
FontSet *g_font_ready = nullptr; // baked and uploaded, not adopted yet
uint32_t g_font_ready_tex = 0;
std::atomic<bool> g_font_failed{false};
// the present thread's
ImGuiContext *g_ctx = nullptr;
FontSet *g_set = nullptr;
uint32_t g_set_tex = 0;

// ---- the page's input (the present thread)
uint64_t g_pad_was = 0;
bool g_pad_primed = false;
Nav g_held = Nav::None;
uint64_t g_next_repeat = 0;
bool g_picture_on = false;

// ---- the player's actions, waiting for the action thread
struct Job {
    std::string action, args; // args: a JSON object
};
std::mutex g_jlock;
std::condition_variable g_jcv;
std::deque<Job> g_jobs;

// ================================================================ what the runtime's tables hold
bool ExactApi(const MkwPluginApi *api) {
    return API_HAS(api, texture_create) && API_HAS(api, texture_destroy) && API_HAS(api, overlay_capture);
}
bool ExactOverlay(const MkwOverlay *o) { return OV_HAS(o, pad) && o->triangles; }

void Show(int on) {
    if (g_api && API_HAS(g_api, overlay_show)) g_api->overlay_show(Draw, on);
}

// ================================================================ the fonts
// Any thread but the present thread: one bake for `scale`, uploaded as a texture of the plugin's.
void BakeFonts(float scale) {
    FontSet *set = BuildFonts(scale);
    uint32_t texture = 0;
    int w = 0, h = 0;
    if (set) {
        unsigned char *pixels = nullptr;
        set->atlas->GetTexDataAsRGBA32(&pixels, &w, &h);
        if (pixels && g_api && API_HAS(g_api, texture_create)) texture = g_api->texture_create(w, h, pixels, w * 4, 0);
        set->atlas->SetTexID(static_cast<ImTextureID>(texture));
        set->atlas->ClearTexData(); // the runtime has its copy
    }
    FontSet *stale = nullptr;
    uint32_t stale_tex = 0;
    {
        std::lock_guard<std::mutex> lock(g_font_lock);
        g_font_done = scale;
        if (set && texture) {
            stale = g_font_ready, stale_tex = g_font_ready_tex; // one never adopted (the picture's size changed twice)
            g_font_ready = set, g_font_ready_tex = texture;
        }
    }
    if (stale) {
        if (stale_tex && API_HAS(g_api, texture_destroy)) g_api->texture_destroy(stale_tex);
        FreeFonts(stale);
    }
    if (set && texture) {
        Log("ingame: %s baked at scale %.2f (the page's text for %.2f), an atlas of %d x %d", set->nunito ? "Nunito" : "no Nunito: ImGui's own font",
            static_cast<double>(set->scale), static_cast<double>(set->page_scale), w, h);
        Show(1); // the present thread adopts it at its next frame
        return;
    }
    Log("ingame: the fonts could not be %s (an atlas of %d x %d): the toasts are drawn with the runtime's own font, the overlay page stays "
        "the launcher's window", set ? "handed to the runtime" : "baked", w, h);
    FreeFonts(set);
    g_font_failed = true;
    Show(1);
}

void FontThread(void *) {
    for (;;) {
        float scale = 0;
        {
            std::unique_lock<std::mutex> lock(g_font_lock);
            g_font_cv.wait(lock, [] { return g_font_want != 0 && g_font_want != g_font_done; });
            scale = g_font_want;
        }
        if (g_faulted.load()) return;
        BakeFonts(scale);
    }
}

// The present thread: the fonts for this picture, baked elsewhere. True once there is a set to draw with (it may be
// the last size's for a few frames after the picture's size changed).
bool EnsureFonts(float height) {
    const float scale = height / 1080.0f;
    FontSet *ready = nullptr;
    uint32_t ready_tex = 0;
    {
        std::lock_guard<std::mutex> lock(g_font_lock);
        if (g_font_want != scale) {
            g_font_want = scale;
            g_font_cv.notify_one();
        }
        ready = g_font_ready, ready_tex = g_font_ready_tex;
        g_font_ready = nullptr, g_font_ready_tex = 0;
    }
    if (ready) {
        // a context of its own for each set: nothing of the old atlas is kept (the page and the toasts keep no ImGui state)
        if (g_ctx) ImGui::DestroyContext(g_ctx);
        if (g_set_tex && API_HAS(g_api, texture_destroy)) g_api->texture_destroy(g_set_tex);
        FontSet *old = g_set;
        g_set = ready, g_set_tex = ready_tex;
        UseFonts(g_set);
        FreeFonts(old);
        g_ctx = ImGui::CreateContext(g_set->atlas);
        ImGuiIO &io = ImGui::GetIO();
        io.IniFilename = nullptr;
        io.LogFilename = nullptr;
        io.BackendRendererName = "mkwmp";
        io.BackendFlags |= ImGuiBackendFlags_RendererHasVtxOffset;
        io.ConfigFlags |= ImGuiConfigFlags_NoMouseCursorChange;
    }
    return g_set && g_ctx;
}

// ================================================================ the renderer back end: ImGui's lists as the runtime's triangles
void Submit(const MkwOverlay *o, const ImDrawData *dd) {
    static std::vector<MkwDrawList> lists;
    static std::vector<std::vector<MkwDrawCmd>> commands;
    if (!dd || dd->CmdListsCount <= 0) return;
    lists.clear();
    if (commands.size() < static_cast<size_t>(dd->CmdListsCount)) commands.resize(static_cast<size_t>(dd->CmdListsCount));
    for (int n = 0; n < dd->CmdListsCount; ++n) {
        const ImDrawList *dl = dd->CmdLists[n];
        std::vector<MkwDrawCmd> &out = commands[static_cast<size_t>(n)];
        out.clear();
        for (const ImDrawCmd &c : dl->CmdBuffer) {
            if (c.UserCallback || !c.ElemCount) continue;
            MkwDrawCmd m;
            m.clip[0] = c.ClipRect.x - dd->DisplayPos.x, m.clip[1] = c.ClipRect.y - dd->DisplayPos.y;
            m.clip[2] = c.ClipRect.z - dd->DisplayPos.x, m.clip[3] = c.ClipRect.w - dd->DisplayPos.y;
            m.texture = static_cast<uint32_t>(c.GetTexID());
            m.vtx_offset = c.VtxOffset;
            m.idx_offset = c.IdxOffset;
            m.idx_count = c.ElemCount;
            out.push_back(m);
        }
        if (out.empty() || dl->VtxBuffer.Size <= 0 || dl->IdxBuffer.Size <= 0) continue;
        MkwDrawList l;
        l.vtx = reinterpret_cast<const MkwDrawVert *>(dl->VtxBuffer.Data);
        l.idx = dl->IdxBuffer.Data;
        l.cmd = out.data();
        l.n_vtx = static_cast<uint32_t>(dl->VtxBuffer.Size);
        l.n_idx = static_cast<uint32_t>(dl->IdxBuffer.Size);
        l.n_cmd = static_cast<uint32_t>(out.size());
        lists.push_back(l);
    }
    if (!lists.empty()) o->triangles(lists.data(), static_cast<int>(lists.size()));
}

// ================================================================ where the toasts start
// 40 units down, or under the runtime's own display at the top right (its FPS line, its performance panel), which is
// drawn over the plugin's layer. Plugin API 5 says where it is; before that only its setting is known (MKW_PERF_OVERLAY,
// read by the poll thread), so a fixed room is left: the FPS line's box as menu.cpp lays it out (12 from the top, the
// text and 6 around it), or the height of the panel at its usual size.
constexpr float kPerfGap = 12, kPerfPanelRoom = 480;

float StackTop(const MkwOverlay *o) {
    const float s = o->height / 1080.0f;
    float top = kStackTop * s;
    if (OV_HAS(o, perf)) {
        const bool on = o->perf[2] > o->perf[0] && o->perf[3] > o->perf[1];
        if (on && o->perf[2] > o->width - (kStackRight + kStackWidth) * s) top = std::max(top, o->perf[3] + kPerfGap * s);
        return top;
    }
    const float scale = o->scale > 0 ? o->scale : s;
    const int mode = g_perf_mode.load(std::memory_order_relaxed);
    if (mode == 1) top = std::max(top, (12 + 6 + 6) * scale + o->line_height + kPerfGap * s);
    else if (mode == 2) top = std::max(top, kPerfPanelRoom * scale + kPerfGap * s);
    return top;
}

// ================================================================ the page's input (the present thread, with MKW_OV_INPUT)
void ReleaseInput() {
    if (!g_captured.exchange(false)) return;
    if (!g_faulted.load() && API_HAS(g_api, overlay_capture)) g_api->overlay_capture(Draw, 0);
    if (g_host.page_closed) g_host.page_closed();
}

void Picture(bool on, float height) {
    if (on == g_picture_on || !g_api || !API_HAS(g_api, overlay_picture)) return;
    g_picture_on = on;
    // the Game pane's box is 540 units high: the picture is never drawn larger
    g_api->overlay_picture(Draw, on ? std::max(16, static_cast<int>(height / 2) + 1) : 0);
}

bool Debounced(uint64_t now) {
    const uint64_t last = g_toggled_at.load();
    if (last && now - last < 300) return true; // the same press, seen by the pad filter, the keys and the page
    g_toggled_at = now ? now : 1;
    return false;
}

void CloseByShortcut(uint64_t now, const char *by) {
    if (!PageOpen() || Debounced(now)) return;
    Log("ingame: the page closes (%s)", by);
    ClosePage();
}

constexpr uint64_t kBit = 1;
constexpr int32_t kStickDead = 16000;

Nav Direction(const MkwPad &pad) {
    if ((pad.buttons & (kBit << MKW_PAD_UP)) || pad.ly > kStickDead) return Nav::Up;
    if ((pad.buttons & (kBit << MKW_PAD_DOWN)) || pad.ly < -kStickDead) return Nav::Down;
    if ((pad.buttons & (kBit << MKW_PAD_LEFT)) || pad.lx < -kStickDead) return Nav::Left;
    if ((pad.buttons & (kBit << MKW_PAD_RIGHT)) || pad.lx > kStickDead) return Nav::Right;
    return Nav::None;
}

void PadInput(const MkwPad *given, uint64_t now) {
    PagePad(given != nullptr);
    if (!given) {
        g_held = Nav::None;
        return;
    }
    MkwPad pad;
    memset(&pad, 0, sizeof pad);
    memcpy(&pad, given, std::min<size_t>(sizeof pad, given->size >= 16 ? given->size : sizeof pad));
    if (!g_pad_primed) { // what is held as the page opens (the shortcut itself) is not a press in the page
        g_pad_primed = true;
        g_pad_was = pad.buttons;
        g_held = Direction(pad);
        g_next_repeat = now + 380;
        return;
    }
    if (g_host.shortcut && g_host.shortcut(given, now)) {
        CloseByShortcut(now, "the shortcut");
        g_pad_was = pad.buttons;
        return;
    }
    const uint64_t pressed = pad.buttons & ~g_pad_was;
    g_pad_was = pad.buttons;
    if (pressed & (kBit << MKW_PAD_A)) PageNav(Nav::Accept, false);
    if (pressed & (kBit << MKW_PAD_X)) PageNav(Nav::X, false);
    if (pressed & (kBit << MKW_PAD_Y)) PageNav(Nav::Y, false);
    if (pressed & (kBit << MKW_PAD_B)) PageNav(Nav::Back, false);
    // the D-pad and the left stick, with repeat (380 ms, then every 110 ms)
    const Nav dir = Direction(pad);
    if (dir != g_held) {
        g_held = dir;
        if (dir != Nav::None) {
            PageNav(dir, false);
            g_next_repeat = now + 380;
        }
    } else if (dir != Nav::None && now >= g_next_repeat) {
        PageNav(dir, false);
        g_next_repeat = now + 110;
    }
}

// USB HID usages (MKW_EV_KEY's code)
enum { kKeyF = 9, kKeyX = 27, kKeyEnter = 40, kKeyEsc = 41, kKeyBackspace = 42, kKeyTab = 43, kKeySpace = 44, kKeyRight = 79,
       kKeyLeft = 80, kKeyDown = 81, kKeyUp = 82, kKeyPadEnter = 88 };

void EventInput(const MkwOverlay *o, uint64_t now) {
    const size_t step = o->event_size;
    if (!o->events || step < 8) return;
    for (uint32_t i = 0; i < o->n_events && i < 1024; ++i) {
        MkwInputEvent e;
        memset(&e, 0, sizeof e);
        memcpy(&e, reinterpret_cast<const char *>(o->events) + static_cast<size_t>(i) * step, std::min(step, sizeof e));
        switch (e.type) {
        case MKW_EV_KEY:
            if (!e.down) break;
            switch (e.code) {
            case kKeyUp: PageNav(Nav::Up, true); break;
            case kKeyDown: PageNav(Nav::Down, true); break;
            case kKeyLeft: PageNav(Nav::Left, true); break;
            case kKeyRight: PageNav(Nav::Right, true); break;
            default:
                if (e.repeat) break;
                if (e.code == kKeyEnter || e.code == kKeyPadEnter || e.code == kKeySpace) PageNav(Nav::Accept, true);
                else if (e.code == kKeyEsc || e.code == kKeyBackspace) PageNav(Nav::Back, true);
                else if (e.code == kKeyF) PageNav(Nav::Y, true);
                else if (e.code == kKeyX) PageNav(Nav::X, true);
                else if (e.code == kKeyTab && (e.mods & MKW_MOD_SHIFT)) CloseByShortcut(now, "Shift+Tab");
                break;
            }
            break;
        case MKW_EV_MOUSE_POS:
            if (o->width > 0 && o->height > 0) PagePointer(Pointer::Move, e.x / o->width, e.y / o->height, 0);
            break;
        case MKW_EV_MOUSE_BUTTON:
            if (o->width <= 0 || o->height <= 0) break;
            if (e.code == 0) PagePointer(e.down ? Pointer::LeftDown : Pointer::LeftUp, e.x / o->width, e.y / o->height, 0);
            else if (e.code == 3 && e.down) PagePointer(Pointer::Back, kNoPoint, kNoPoint, 0);
            break;
        case MKW_EV_MOUSE_WHEEL:
            PagePointer(Pointer::Wheel, kNoPoint, kNoPoint, static_cast<int>(e.y * 120));
            break;
        default: break; // focus, and what a later runtime adds
        }
    }
}

// ================================================================ the drawing (the runtime's present thread)
void Hide() {
    Show(0);
    // a toast that arrived or a page that opened meanwhile turned it on before this turned it off: on again
    if (!g_faulted.load() && (g_stack->Unseen() || PageOpen())) Show(1);
}

void Draw(const MkwOverlay *o, void *) {
    if (!o || g_faulted.load()) return;
    const uint64_t now = NowMs();
    int tier = g_tier.load();
    if (tier == INGAME_EXACT && (!ExactOverlay(o) || g_font_failed.load())) {
        // the plugin table promised API 5, the overlay's own table does not hold it (or the fonts cannot be drawn with)
        tier = INGAME_BASIC;
        g_tier = tier;
        if (!g_font_failed.load())
            Log("ingame: this runtime's overlay table (%u bytes) has no triangles: the toasts with its rect and text, the overlay page "
                "stays the launcher's window", static_cast<unsigned>(o->size));
        ClosePage();
        SetView(nullptr);
        g_view_ok = false;
    }
    g_height = o->height;
    if (!g_drawn.exchange(true))
        Log("ingame: the first frame (%d x %d, UI scale %.2f, overlay table %u bytes): asking the launcher for its toasts%s",
            static_cast<int>(o->width), static_cast<int>(o->height), static_cast<double>(o->scale), static_cast<unsigned>(o->size),
            tier == INGAME_EXACT ? " and its overlay page" : "");
    if (tier == INGAME_NONE || o->width < 16 || o->height < 16) {
        Hide();
        return;
    }
    const float top = StackTop(o);
    g_boxes.clear();
    bool waiting = false;
    if (tier == INGAME_BASIC) {
        ReleaseInput();
        g_stack->Tick(now);
        DrawToastsBasic(*g_stack, o, top, now, &g_boxes);
    } else if (!EnsureFonts(o->height)) {
        waiting = true; // the first bake is under way: the toasts wait for it (their time counts from the answer)
    } else {
        ImGui::SetCurrentContext(g_ctx);
        ImGuiIO &io = ImGui::GetIO();
        io.DisplaySize = ImVec2(o->width, o->height);
        io.DisplayFramebufferScale = ImVec2(1, 1);
        const float dt = o->dt > 0.0001f ? (o->dt < 0.1f ? o->dt : 0.1f) : 0.0001f;
        io.DeltaTime = dt;
        ImGui::NewFrame();
        const bool open = PageOpen();
        if (open && (o->flags & MKW_OV_INPUT)) {
            EventInput(o, now);
            PadInput(o->pad, now);
        } else if (!open) {
            g_pad_primed = false;
        }
        Picture(open, o->height);
        const bool picture = OV_HAS(o, picture_h) && o->picture && o->picture_w > 0 && o->picture_h > 0;
        SetGameTexture(picture ? o->picture : 0, picture ? static_cast<unsigned>(o->picture_w) : 0, picture ? static_cast<unsigned>(o->picture_h) : 0);
        DrawPage(o->width, o->height, dt); // the page (when open), under the toasts: they keep showing over it
        if (!PageOpen()) ReleaseInput(); // B, Esc, the shortcut, or the launcher went away: the game's input back
        g_stack->Tick(now);
        DrawToastsExact(*g_stack, o->width, o->height, top, now, &g_boxes);
        ImGui::Render();
        Submit(o, ImGui::GetDrawData());
    }
    g_cards = static_cast<int>(g_stack->Cards().size());
    if (g_stack->Cards().empty() && !PageShowing() && !(waiting && g_stack->Unseen())) Hide();
}

// The runtime's fault guard caught a fault in Draw (plugin API 5): it turned the drawing off for good and gave the
// input back; this runs once, on the present thread. Nothing here takes a lock the drawing may have held.
void Faulted(void *) {
    g_faulted = true;
    g_tier = INGAME_NONE;
    g_view_ok = false;
    const bool had_page = PageOpen();
    ClosePage();
    if (g_captured.exchange(false) && g_host.page_closed) g_host.page_closed();
    Log("ingame: the overlay's drawing faulted: it is off until the game is started again. The mod stops asking the launcher for its "
        "toasts and its overlay page, so the launcher shows them itself (its own window) within 15 s%s",
        had_page ? "; the page that was open is closed" : "");
    // the launcher is told the page is closed (the existing action "show" with no pane), unless the fault left the queue's lock held
    if (had_page && g_jlock.try_lock()) {
        g_jobs.clear();
        g_jobs.push_back({"show", "{\"pane\":\"\"}"});
        g_jlock.unlock();
        g_jcv.notify_one();
    }
}

// ================================================================ the launcher: the two long polls and the actions
struct Poll {
    uint64_t seq = 0;
    bool waiting = false; // the next request waits for a change of seq
    unsigned failures = 0;
    int logged = 1;
    std::vector<char> buffer;
};
Poll g_toast_poll, g_view_poll;

int Request(const char *method, const std::string &path, const char *json, uint32_t timeout_ms, std::vector<char> &buffer, size_t cap,
            const char **body) {
    if (buffer.size() < cap) buffer.resize(cap);
    buffer[0] = 0;
    *body = buffer.data();
    if (!g_host.request) return 0;
    const int status = g_host.request(method, path.c_str(), json, timeout_ms, buffer.data(), buffer.size(), body);
    if (!*body) *body = buffer.data();
    return status < 0 ? 0 : status;
}

const char *Why(int status) {
    return status == 404 ? "this launcher has none" : status == 401 ? "the launcher refused the session's token"
         : status == 429 ? "asked too often; slowing down" : status == 0 ? "the launcher's local API does not answer"
         : "the launcher's answer is not one this mod reads";
}

uint32_t BackOff(int status) { return status == 404 || status == 401 ? 30000 : status == 429 ? 1000 : 2000; }

// One request of the toast poll: what to sleep before the next.
uint32_t ToastStep() {
    Poll &p = g_toast_poll;
    // the runtime's own display at the top right, by its setting, where the runtime does not say where it is (plugin API 4)
    if (g_api && g_api->cfg_get) {
        const char *mode = g_api->cfg_get("MKW_PERF_OVERLAY");
        g_perf_mode = mode && !strcmp(mode, "full") ? 2 : mode && !strcmp(mode, "fps") ? 1 : 0;
    }
    char path[96];
    if (p.waiting) snprintf(path, sizeof path, "/local/v1/toasts?wait=10&seq=%llu", static_cast<unsigned long long>(p.seq));
    else snprintf(path, sizeof path, "/local/v1/toasts");
    const char *body = nullptr;
    const int status = Request("GET", path, nullptr, 15000, p.buffer, 64 << 10, &body);
    Answer answer;
    const bool ok = status == 200 && ParseAnswer(body, strlen(body), answer);
    if (ok) {
        p.seq = answer.seq;
        const bool show = !answer.toasts.empty() || g_cards.load() > 0;
        g_stack->Offer(std::move(answer), NowMs());
        if (show) Show(1);
        if (p.logged != 200)
            Log("ingame: the launcher answers GET /local/v1/toasts: its toasts are drawn in the game (%s)",
                g_tier.load() == INGAME_EXACT ? "the launcher's look, plugin API 5" : "the runtime's rect and text, plugin API 4");
        p.logged = 200;
        p.failures = 0;
        p.waiting = true;
        return 100; // never more than ten answers a second
    }
    if (++p.failures >= 3 && g_stack->Have()) {
        g_stack->Drop(); // the launcher stopped answering: what it showed goes (the cards fade)
        if (g_cards.load() > 0) Show(1);
    }
    p.waiting = false;
    if (status != p.logged) {
        p.logged = status;
        Log("ingame: toasts: %s (HTTP %d)%s", Why(status), status, status == 404 ? ": the launcher shows them itself" : "");
    }
    return BackOff(status);
}

// One request of the view poll.
uint32_t ViewStep() {
    Poll &p = g_view_poll;
    char path[96];
    if (p.waiting) snprintf(path, sizeof path, "/local/v1/overlay/view?wait=10&seq=%llu", static_cast<unsigned long long>(p.seq));
    else snprintf(path, sizeof path, "/local/v1/overlay/view");
    const char *body = nullptr;
    const int status = Request("GET", path, nullptr, 15000, p.buffer, 256 << 10, &body);
    auto view = std::make_shared<OverlayView>();
    const bool ok = status == 200 && ParseView(body, strlen(body), *view);
    if (ok) {
        p.seq = view->seq;
        SetView(std::move(view));
        if (p.logged != 200)
            Log("ingame: the launcher answers GET /local/v1/overlay/view: the overlay shortcut opens its page in the game");
        p.logged = 200;
        p.failures = 0;
        g_view_ok = true;
        p.waiting = true;
        return 50; // never more than twenty answers a second
    }
    if (++p.failures >= 3) {
        SetView(nullptr); // the launcher stopped answering: the page closes (the input goes back to the game)
        g_view_ok = false;
    }
    p.waiting = false;
    if (status != p.logged) {
        p.logged = status;
        Log("ingame: overlay page: %s (HTTP %d)%s", Why(status), status, status == 404 ? ": its own window is the overlay" : "");
    }
    return BackOff(status);
}

void Nap(uint32_t ms) {
    if (g_host.sleep_ms) g_host.sleep_ms(ms);
}

void ToastThread(void *) {
    // only once the overlay draws: until then the launcher's own window shows its toasts
    while (!g_drawn.load() && !g_faulted.load()) Nap(250);
    while (!g_faulted.load() && g_tier.load() != INGAME_NONE) Nap(ToastStep());
}

void ViewThread(void *) {
    while (!g_drawn.load() && !g_faulted.load()) Nap(250);
    // only while the page can be drawn here: a mod that asks for the view tells the launcher the shortcut is the mod's
    while (!g_faulted.load() && g_tier.load() == INGAME_EXACT) Nap(ViewStep());
    SetView(nullptr);
    g_view_ok = false;
}

bool ActionOne(bool wait) {
    Job job;
    {
        std::unique_lock<std::mutex> lock(g_jlock);
        if (wait) g_jcv.wait(lock, [] { return !g_jobs.empty(); });
        if (g_jobs.empty()) return false;
        job = std::move(g_jobs.front());
        g_jobs.pop_front();
    }
    static std::vector<char> buffer;
    const std::string json = "{\"action\":\"" + JsonText(job.action) + "\",\"args\":" + job.args + "}";
    const char *body = nullptr;
    const int status = Request("POST", "/local/v1/overlay/action", json.c_str(), 20000, buffer, 8 << 10, &body);
    Log("ingame: %s sent to the launcher (HTTP %d%s)", job.action.c_str(), status,
        status == 200 ? "" : status == 202 ? ": still at it" : status == 404 ? ": gone" : status == 409 ? ": refused" : "");
    if (job.action == "quitGame" && status == 200) {
        // Quit Game, asked first in the page and taken by the launcher: the runtime's own clean end (the mod's on_exit gives
        // the seat back, the saves finish)
        if (g_api && API_HAS(g_api, quit)) {
            Log("ingame: Quit Game: the game ends through the runtime");
            g_api->quit();
        } else {
            Log("ingame: Quit Game: this mkw-recomp has no quit entry");
        }
    }
    return true;
}

void ActionThread(void *) {
    for (;;) ActionOne(true);
}

} // namespace

// ================================================================ internal.h
const IngameHost &Host() { return g_host; }

void Log(const char *format, ...) {
    if (!g_host.log) return;
    char line[512];
    va_list ap;
    va_start(ap, format);
    vsnprintf(line, sizeof line, format, ap);
    va_end(ap);
    g_host.log("%s", line);
}

uint64_t NowMs() { return g_clock ? g_clock() : g_host.now_ms ? g_host.now_ms() : 0; }

void Act(const std::string &action, const std::string &args) {
    {
        std::lock_guard<std::mutex> lock(g_jlock);
        if (action == "show") // only the last pane shown matters
            g_jobs.erase(std::remove_if(g_jobs.begin(), g_jobs.end(), [](const Job &j) { return j.action == "show"; }), g_jobs.end());
        if (g_jobs.size() < 64) g_jobs.push_back({action, args});
    }
    g_jcv.notify_one();
}

namespace test {

void Reset() {
    ResetPage();
    g_host = IngameHost{};
    g_api = nullptr;
    g_tier = INGAME_NONE;
    g_faulted = false;
    g_drawn = false;
    g_height = 0;
    g_cards = 0;
    g_perf_mode = 0;
    g_view_ok = false;
    g_captured = false;
    g_toggled_at = 0;
    delete g_stack;
    g_stack = new ToastStack;
    g_boxes.clear();
    g_font_failed = false;
    g_pad_was = 0;
    g_pad_primed = false;
    g_held = Nav::None;
    g_picture_on = false;
    g_toast_poll = Poll{};
    g_view_poll = Poll{};
    std::lock_guard<std::mutex> lock(g_jlock);
    g_jobs.clear();
}

uint32_t ToastPollStep() { return ToastStep(); }
uint32_t ViewPollStep() { return ViewStep(); }
bool ActionStep() { return ActionOne(false); }

void BuildFontsNow() {
    float scale = 0;
    {
        std::lock_guard<std::mutex> lock(g_font_lock);
        if (g_font_want == 0 || g_font_want == g_font_done) return;
        scale = g_font_want;
    }
    BakeFonts(scale);
}

ToastStack &Stack() { return *g_stack; }
const std::vector<ToastBox> &Boxes() { return g_boxes; }
void SetClock(uint64_t (*now)()) { g_clock = now; }

} // namespace test

} // namespace mkwmp::ov

// ================================================================ ../ingame.h
using namespace mkwmp::ov;

int ingame_install(const IngameHost *host) {
    if (!host || !host->api) return INGAME_NONE;
    g_host = *host;
    g_api = host->api;
    const MkwPluginApi *api = g_api;
    if (!API_HAS(api, overlay_set) || !API_HAS(api, overlay_show)) {
        Log("ingame: off: this mkw-recomp has no overlay entry (plugin table %u bytes); the launcher shows its toasts and its overlay itself",
            static_cast<unsigned>(api->size));
        return INGAME_NONE;
    }
    if (!api->overlay_set(Draw, nullptr)) {
        Log("ingame: off: the runtime refused the overlay drawing; the launcher shows its toasts and its overlay itself");
        return INGAME_NONE;
    }
    const bool exact = ExactApi(api);
    g_tier = exact ? INGAME_EXACT : INGAME_BASIC;
    const bool guarded = API_HAS(api, overlay_on_fault) && api->overlay_on_fault(Draw, Faulted, nullptr);
    if (exact)
        Log("ingame: plugin API 5 (plugin table %u bytes): the launcher's toasts and its overlay page are drawn in the game's own "
            "frames with the launcher's look (the mod's own Dear ImGui %s, Nunito); the game runs on behind the page%s%s",
            static_cast<unsigned>(api->size), IMGUI_VERSION, API_HAS(api, overlay_picture) ? ", its live picture in the Game pane" : "",
            guarded ? "; a fault in the drawing turns it off, not the game" : "; no fault guard in this runtime");
    else
        Log("ingame: plugin API %u (plugin table %u bytes, no triangles or textures): the launcher's toasts are drawn in the game's own "
            "frames with the runtime's rect and text (same places and timing, its font); the overlay page stays the launcher's window",
            static_cast<unsigned>(api->version), static_cast<unsigned>(api->size));
    if (g_host.thread_start) {
        if (exact && !g_host.thread_start(FontThread, nullptr)) {
            Log("ingame: no thread for the fonts: the toasts with the runtime's rect and text");
            g_font_failed = true;
        }
        if (!g_host.thread_start(ToastThread, nullptr)) Log("ingame: no thread for the toasts: none are drawn");
        if (exact) {
            if (!g_host.thread_start(ViewThread, nullptr)) Log("ingame: no thread for the overlay page: it stays the launcher's window");
            if (!g_host.thread_start(ActionThread, nullptr)) Log("ingame: no thread for the page's actions");
        }
    }
    Show(1); // one frame now: the picture's size (the fonts are baked for it before the first toast)
    return g_tier.load();
}

int ingame_tier(void) { return g_tier.load(); }

int ingame_page_open(void) { return !g_faulted.load() && PageOpen(); }

int ingame_page_toggle(const char *by) {
    if (g_faulted.load() || g_tier.load() != INGAME_EXACT) return 0;
    const uint64_t now = NowMs();
    if (PageOpen()) {
        CloseByShortcut(now, by ? by : "the shortcut");
        return 1;
    }
    if (!g_view_ok.load() || !View()) {
        Log("ingame: the shortcut (%s), but the launcher does not answer its overlay page: the launcher's window overlay", by ? by : "?");
        return 0;
    }
    {
        std::lock_guard<std::mutex> lock(g_font_lock);
        if (g_font_done == 0 && !g_font_ready) return 0; // not baked yet (the very first seconds): the window overlay
    }
    if (Debounced(now)) return 1;
    if (!OpenPage()) return 0;
    Show(1);
    if (!g_api->overlay_capture(Draw, MKW_CAPTURE_INPUT)) {
        ClosePage();
        Log("ingame: the shortcut (%s), but the runtime did not give the page the input: the launcher's window overlay", by ? by : "?");
        return 0;
    }
    g_captured = true;
    Log("ingame: the page opens (%s): the game runs on and reads idle input", by ? by : "?");
    return 1;
}
