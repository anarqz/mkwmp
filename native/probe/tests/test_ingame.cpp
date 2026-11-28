// The in-game overlay (src/ingame.h, src/ingame/) against a fake runtime and a stub launcher, on the host: no game, no
// GPU, no socket. The fake plugin API 5 records what the mod hands it (triangles into vertex lists, textures, the
// input capture, rect and text calls on API 4); the stub launcher answers the mod's requests with the launcher's own
// fixtures (tests/data/ingame_toasts.json, ingame_view.json: the exact bytes its harness wrote) and records what the
// mod posts. The pollers and the action thread are stepped from here, and the clock is the test's.
//
//   python native/probe/build_plugin.py --test-ingame        (builds it for the host and runs it)
//
// What it checks: the two answers as the mod reads them; the tier by the sizes of the runtime's tables; the toasts'
// places and timing in both renderers (slide 240 ms, fade 220 ms, gone at 260 ms, the room left for the runtime's FPS
// display); the page's navigation by synthetic pad, key and mouse events; the actions it posts; the fault path.
#include "../src/ingame/internal.h"

#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <set>
#include <string>
#include <vector>

using namespace mkwmp::ov;

static int g_failures, g_checks;
static void Explain();
#define CHECK(c) do { ++g_checks; if (!(c)) { printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #c); g_failures++; Explain(); } } while (0)
#define NEAR(a, b, eps) (std::fabs(static_cast<double>(a) - static_cast<double>(b)) <= (eps))

// ---------------------------------------------------------------- the clock
static uint64_t g_now = 1000000;
static uint64_t Now() { return g_now; }

// ---------------------------------------------------------------- the fake runtime
static MkwOverlayFn g_draw;
static void (*g_fault)(void *);
static int g_shown, g_capture, g_capture_ok = 1, g_capture_calls, g_picture = -1, g_quit;
static uint32_t g_next_texture;
static std::set<uint32_t> g_textures;
static int g_atlas_w, g_atlas_h;
static const char *g_perf_setting = "off";
static std::vector<std::string> g_log;
static bool g_verbose;

static int FakeOverlaySet(MkwOverlayFn draw, void *) { g_draw = draw; return 1; }
static void FakeOverlayShow(MkwOverlayFn, int on) { g_shown = on; }
static uint32_t FakeTextureCreate(int w, int h, const uint8_t *rgba, int row_bytes, int) {
    if (!rgba || w < 1 || h < 1 || w > 8192 || h > 8192 || row_bytes < w * 4) return 0;
    g_atlas_w = w, g_atlas_h = h;
    g_textures.insert(++g_next_texture);
    return g_next_texture;
}
static int FakeTextureUpdate(uint32_t t, const uint8_t *, int) { return g_textures.count(t) != 0; }
static void FakeTextureDestroy(uint32_t t) { g_textures.erase(t); }
static int FakeCapture(MkwOverlayFn, int flags) {
    ++g_capture_calls;
    if (flags && !g_capture_ok) return 0;
    g_capture = flags;
    return 1;
}
static int FakeKeyState(int) { return 0; }
static int FakeOnFault(MkwOverlayFn, void (*cb)(void *), void *) { g_fault = cb; return 1; }
static int FakePicture(MkwOverlayFn, int max_height) { g_picture = max_height; return 1; }
static void FakeQuit(void) { ++g_quit; }
static const char *FakeCfg(const char *name) { return !strcmp(name, "MKW_PERF_OVERLAY") ? g_perf_setting : nullptr; }

static void FakeLog(const char *fmt, ...) {
    char line[600];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(line, sizeof line, fmt, ap);
    va_end(ap);
    g_log.push_back(line);
    if (g_verbose) printf("    | %s\n", line);
}
static bool Logged(const char *part) {
    for (const std::string &l : g_log)
        if (l.find(part) != std::string::npos) return true;
    return false;
}

// The plugin table of a runtime: `size` bytes of it are there (the rest is as if it did not exist).
static MkwPluginApi Api(size_t size = sizeof(MkwPluginApi)) {
    MkwPluginApi a;
    memset(&a, 0, sizeof a);
    a.size = static_cast<uint32_t>(size);
    a.version = size >= sizeof(MkwPluginApi) ? 5 : size > offsetof(MkwPluginApi, input_filter) ? 4 : 1;
    a.port_version = "0.1.18";
    a.cfg_get = FakeCfg;
    a.overlay_set = FakeOverlaySet;
    a.overlay_show = FakeOverlayShow;
    a.quit = FakeQuit;
    a.texture_create = FakeTextureCreate;
    a.texture_update = FakeTextureUpdate;
    a.texture_destroy = FakeTextureDestroy;
    a.overlay_capture = FakeCapture;
    a.key_state = FakeKeyState;
    a.overlay_on_fault = FakeOnFault;
    a.overlay_picture = FakePicture;
    return a;
}

// ---- one frame of the fake runtime: what the drawing handed over
struct RectCall {
    float x0, y0, x1, y1, rounding;
    uint32_t rgba;
};
struct FrameOut {
    std::vector<RectCall> rects;
    std::vector<std::string> texts;
    int lists = 0, commands = 0, vertices = 0, indices = 0;
    float min_x = 1e9f, min_y = 1e9f, max_x = -1e9f, max_y = -1e9f; // of the vertices that are not fully transparent
    bool bad_texture = false, bad_index = false;
};
static FrameOut g_out;

static void FakeRect(float x0, float y0, float x1, float y1, uint32_t rgba, float rounding) { g_out.rects.push_back({x0, y0, x1, y1, rounding, rgba}); }
static void FakeText(float, float, uint32_t, const char *utf8) { g_out.texts.push_back(utf8 ? utf8 : ""); }
static void FakeTextSize(const char *utf8, float *w, float *h) {
    if (w) *w = 9.0f * static_cast<float>(utf8 ? strlen(utf8) : 0); // a fixed-pitch font, 9 wide
    if (h) *h = 18;
}
static void FakeTriangles(const MkwDrawList *lists, int n) {
    for (int k = 0; lists && k < n; ++k) {
        const MkwDrawList &l = lists[k];
        ++g_out.lists;
        g_out.vertices += static_cast<int>(l.n_vtx);
        g_out.indices += static_cast<int>(l.n_idx);
        for (uint32_t c = 0; c < l.n_cmd; ++c) {
            const MkwDrawCmd &m = l.cmd[c];
            ++g_out.commands;
            if (!g_textures.count(m.texture) && m.texture != 777) g_out.bad_texture = true; // 777: the fake game picture
            if (m.idx_count % 3 || m.idx_offset + m.idx_count > l.n_idx || m.vtx_offset >= l.n_vtx) { g_out.bad_index = true; continue; }
            for (uint32_t i = 0; i < m.idx_count; ++i) {
                const uint32_t v = m.vtx_offset + l.idx[m.idx_offset + i];
                if (v >= l.n_vtx) { g_out.bad_index = true; continue; }
                const MkwDrawVert &p = l.vtx[v];
                if (!(p.col >> 24)) continue;
                g_out.min_x = std::min(g_out.min_x, p.x), g_out.max_x = std::max(g_out.max_x, p.x);
                g_out.min_y = std::min(g_out.min_y, p.y), g_out.max_y = std::max(g_out.max_y, p.y);
            }
        }
    }
}

struct FrameIn {
    float w = 1920, h = 1080;
    size_t size = sizeof(MkwOverlay); // of the runtime's overlay table
    uint32_t flags = 0;
    const MkwPad *pad = nullptr;
    std::vector<MkwInputEvent> events;
    float perf[4] = {0, 0, 0, 0};
    uint32_t picture = 0;
};

// False: the drawing is not shown (the runtime would not call it).
static bool Frame(const FrameIn &in = FrameIn()) {
    g_out = FrameOut();
    if (!g_shown || !g_draw) return false;
    MkwOverlay o;
    memset(&o, 0, sizeof o);
    o.size = static_cast<uint32_t>(in.size);
    o.width = in.w, o.height = in.h, o.scale = in.h / 1080.0f, o.line_height = 18;
    o.rect = FakeRect, o.text = FakeText, o.text_size = FakeTextSize;
    if (in.size >= sizeof(MkwOverlay)) {
        o.flags = in.flags;
        o.dt = 0.016f;
        o.triangles = FakeTriangles;
        o.events = in.events.empty() ? nullptr : in.events.data();
        o.n_events = static_cast<uint32_t>(in.events.size());
        o.event_size = sizeof(MkwInputEvent);
        o.pad = in.pad;
        o.picture = in.picture, o.picture_w = in.picture ? 960 : 0, o.picture_h = in.picture ? 540 : 0;
        memcpy(o.perf, in.perf, sizeof o.perf);
    }
    g_draw(&o, nullptr);
    return true;
}

// ---------------------------------------------------------------- the stub launcher
struct Asked {
    std::string method, path, json;
};
static std::vector<Asked> g_asked;
static std::string g_toasts_body, g_view_body;
static int g_toasts_status = 200, g_view_status = 200, g_action_status = 200;

static int StubRequest(const char *method, const char *path, const char *json, uint32_t, char *buf, size_t cap, const char **answer) {
    g_asked.push_back({method, path, json ? json : ""});
    std::string body;
    int status = 404;
    if (!strncmp(path, "/local/v1/toasts", 16)) status = g_toasts_status, body = g_toasts_body;
    else if (!strncmp(path, "/local/v1/overlay/view", 22)) status = g_view_status, body = g_view_body;
    else if (!strcmp(path, "/local/v1/overlay/action")) status = g_action_status, body = status == 200 ? "{\"ok\":true}" : "{\"state\":\"refused\",\"message\":\"busy\"}";
    if (status != 200 && status != 409) body = "{\"state\":\"x\"}";
    snprintf(buf, cap, "%s", body.c_str());
    *answer = buf;
    return status;
}

static int g_page_closed;
static void PageClosed(void) { ++g_page_closed; }
static int g_shortcut;
static int Shortcut(const MkwPad *, uint64_t) { const int s = g_shortcut; g_shortcut = 0; return s; }

static MkwPluginApi g_api;
static int Install(size_t api_size = sizeof(MkwPluginApi), bool no_textures = false) {
    test::Reset();
    test::SetClock(Now);
    g_draw = nullptr, g_fault = nullptr;
    g_shown = g_capture = g_capture_calls = g_quit = g_page_closed = g_shortcut = 0;
    g_capture_ok = 1;
    g_picture = -1;
    g_perf_setting = "off";
    g_toasts_status = g_view_status = g_action_status = 200;
    g_asked.clear();
    g_log.clear();
    g_api = Api(api_size);
    if (no_textures) g_api.texture_create = nullptr;
    IngameHost host;
    memset(&host, 0, sizeof host);
    host.api = &g_api;
    host.request = StubRequest;
    host.log = FakeLog;
    host.now_ms = Now;
    host.page_closed = PageClosed;
    host.shortcut = Shortcut;
    return ingame_install(&host);
}

static std::string ReadFile(const std::string &path) {
    std::string out;
    FILE *f = fopen(path.c_str(), "rb");
    if (!f) return out;
    char buf[4096];
    size_t n;
    while ((n = fread(buf, 1, sizeof buf, f)) > 0) out.append(buf, n);
    fclose(f);
    return out;
}

// "key":"value" of the page's state line.
static std::string State(const char *key) {
    const std::string s = PageState(), pat = std::string("\"") + key + "\":";
    const size_t at = s.find(pat);
    if (at == std::string::npos) return "?";
    size_t from = at + pat.size();
    if (s[from] != '"') return s.substr(from, s.find_first_of(",}", from) - from);
    ++from;
    return s.substr(from, s.find('"', from) - from);
}
// The centre of a stop of the last frame, in pixels; false when it is not one.
static bool StopAt(const char *id, float &x, float &y) {
    const std::string s = PageState(), pat = std::string("{\"id\":\"") + id + "\",\"x\":";
    const size_t at = s.find(pat);
    if (at == std::string::npos) return false;
    int ix = 0, iy = 0;
    if (sscanf(s.c_str() + at + pat.size(), "%d,\"y\":%d", &ix, &iy) != 2) return false;
    x = static_cast<float>(ix), y = static_cast<float>(iy);
    return true;
}

static void Explain() {
    if (g_verbose) printf("    state %s\n    shown %d capture %d boxes %d\n", PageState().c_str(), g_shown, g_capture, static_cast<int>(test::Boxes().size()));
}

// The last action the mod posted, taken from the queue by one step of the action thread ("" when none waited).
static std::string Posted() {
    const size_t before = g_asked.size();
    if (!test::ActionStep()) return "";
    return g_asked.size() > before ? g_asked.back().json : "";
}
static void DrainActions() {
    while (test::ActionStep()) {}
}

// ================================================================ the answers as the mod reads them
static void TestModel() {
    Answer a;
    CHECK(ParseAnswer(g_toasts_body.data(), g_toasts_body.size(), a));
    CHECK(a.seq == 4 && a.toasts.size() == 4);
    CHECK(a.toasts[0].id == "t1" && a.toasts[0].kind == "friend" && a.toasts[0].title == "Luna" && a.toasts[0].icon == "play");
    CHECK(a.toasts[0].expires_in_ms == 4998 && a.toasts[0].stay_ms == 5000);
    CHECK(a.toasts[3].kind == "error" && a.toasts[3].body.empty() && a.toasts[3].expires_in_ms == 8000);
    CHECK(a.palette.toast == 0xFF4E4E55 && a.palette.bad == 0xFFFF6B61 && a.palette.text_muted == 0xFFB1B1B6);
    Answer b;
    CHECK(!ParseAnswer("{\"seq\":1}", 9, b) && !ParseAnswer("[]", 2, b) && !ParseAnswer("{\"seq\":1,\"toasts\":[", 19, b));
    const char *more = "{\"seq\":9,\"toasts\":[{\"id\":\"a\",\"new\":{\"x\":[1,2]},\"title\":\"\\u00e9\\ud83d\\ude00\"},{\"title\":\"no id\"}],\"later\":true}";
    CHECK(ParseAnswer(more, strlen(more), b) && b.toasts.size() == 1 && b.toasts[0].kind == "info" && b.toasts[0].expires_in_ms == 5000);
    CHECK(b.toasts[0].title == "\xC3\xA9\xF0\x9F\x98\x80"); // unknown keys ignored, defaults kept, an emoji arrives as itself

    OverlayView v;
    CHECK(ParseView(g_view_body.data(), g_view_body.size(), v));
    CHECK(v.seq == 1 && v.start == "lobby" && v.shortcut == "guide_or_select_start" && v.clock == "10:21 PM");
    CHECK(v.entries.size() == 2 && v.entries[0].key == "game" && v.entries[1].key == "lobby" && v.entries[1].icon == "friends");
    CHECK(v.palette.background == 0xFFEBEBEB && v.palette.accent_soft == 0x1F1E64E6 && v.palette.dim == 0x73000000);
    CHECK(v.text.title == "MKWMP" && v.text.back_to_game == "Back to the Game");
    CHECK(v.game.name == "MKW" && v.game.quit.present && v.game.quit.title == "Quit MKW?");
    CHECK(v.lobby.in_lobby && v.lobby.network == "mkwmp" && v.lobby.players.size() == 6);
    CHECK(v.lobby.players[0].you && !v.lobby.players[0].can_report && v.lobby.players[0].disc == 0xFFC23B6B);
    const LobbyPlayer &p5 = v.lobby.players[4];
    CHECK(p5.id == "p5" && p5.renamed && p5.renamed_label == "Used 3 names" && p5.names.size() == 3 && p5.names[0] == "TurboShell");
    CHECK(p5.player_id == "0000000f-4c1e-5a2b-9d3f-00000001d001" && p5.can_report && p5.can_copy_report);
    CHECK(FormatCount(v.lobby.names_header, 3) == "NAMES \xC2\xB7 3");
    CHECK(v.report.present && v.report.enabled && v.report.reasons.size() == 4 && v.report.reasons[0].first == "cheating");
    OverlayView w;
    CHECK(!ParseView("{\"seq\":1}", 9, w) && !ParseView("{\"seq\":1,\"entries\":[]}", 22, w));
}

// ================================================================ the tier, by the sizes of the runtime's tables
static void TestTiers() {
    // plugin API 5, whole: the exact tier
    CHECK(Install() == INGAME_EXACT && ingame_tier() == INGAME_EXACT && g_shown == 1 && g_fault);
    CHECK(Logged("ingame: plugin API 5"));
    // the official 0.1.18's table (API 4: it ends with quit): toasts with rect and text
    CHECK(Install(offsetof(MkwPluginApi, texture_create)) == INGAME_BASIC && !g_fault);
    CHECK(Logged("with the runtime's rect and text") && Logged("the overlay page stays the launcher's window"));
    // API 1's table (it ends with overlay_show)
    CHECK(Install(offsetof(MkwPluginApi, language)) == INGAME_BASIC);
    // a table as long as API 5's whose texture entry is empty
    CHECK(Install(sizeof(MkwPluginApi), true) == INGAME_BASIC);
    // API 5 without the last entry (the game's picture): still exact
    CHECK(Install(offsetof(MkwPluginApi, overlay_picture)) == INGAME_EXACT);
    // a table that ends before the overlay: nothing
    CHECK(Install(offsetof(MkwPluginApi, overlay_show)) == INGAME_NONE && g_shown == 0 && Logged("ingame: off"));
    // a longer table than this build knows (a later API): what it knows is used
    {
        struct Longer { MkwPluginApi api; void *more[4]; } longer;
        memset(&longer, 0, sizeof longer);
        CHECK(Install() == INGAME_EXACT);
        longer.api = g_api;
        longer.api.size = sizeof longer;
        test::Reset();
        test::SetClock(Now);
        IngameHost host;
        memset(&host, 0, sizeof host);
        host.api = &longer.api, host.request = StubRequest, host.log = FakeLog, host.now_ms = Now;
        CHECK(ingame_install(&host) == INGAME_EXACT);
    }
    // the plugin table says API 5 but the overlay's own table is the old one: decided at the first frame
    CHECK(Install() == INGAME_EXACT);
    FrameIn old;
    old.size = offsetof(MkwOverlay, flags);
    CHECK(Frame(old) && ingame_tier() == INGAME_BASIC && Logged("has no triangles"));
    CHECK(ingame_page_toggle("guide") == 0); // no page on this tier: the launcher's window overlay
}

// ================================================================ the toasts on plugin API 4 (rect and text)
static void TestToastsBasic() {
    CHECK(Install(offsetof(MkwPluginApi, texture_create)) == INGAME_BASIC);
    FrameIn in;
    in.size = offsetof(MkwOverlay, flags);
    CHECK(Frame(in) && Logged("ingame: the first frame (1920 x 1080"));
    CHECK(g_out.rects.empty() && g_shown == 0); // nothing to show: off, at no cost
    // the poll: at once first, then waiting on seq
    CHECK(test::ToastPollStep() == 100 && g_asked.back().path == "/local/v1/toasts" && g_asked.back().method == "GET");
    CHECK(g_shown == 1 && Logged("the launcher answers GET /local/v1/toasts"));
    const uint64_t t0 = g_now;
    CHECK(test::ToastPollStep() == 100 && g_asked.back().path == "/local/v1/toasts?wait=10&seq=4");
    // (the second answer re-arms the times: they count from the answer's arrival)
    CHECK(Frame(in));
    const auto &b = test::Boxes();
    CHECK(b.size() == 4 && b[0].id == "t1" && b[3].id == "t4");
    CHECK(NEAR(b[0].x, 1920 - 48 - 640 + 120, 0.01) && NEAR(b[0].alpha, 0, 0.001) && g_out.rects.empty()); // coming in from 120 to the right
    g_now = t0 + 120;
    CHECK(Frame(in) && test::Boxes()[0].x > 1232 && test::Boxes()[0].x < 1352 && NEAR(test::Boxes()[0].alpha, 0.6, 0.001));
    g_now = t0 + 240;
    CHECK(Frame(in));
    CHECK(NEAR(b[0].x, 1232, 0.01) && NEAR(b[0].y, 40, 0.01) && NEAR(b[0].w, 640, 0.01) && NEAR(b[0].alpha, 1, 0.001));
    // one line of title, one of body: the launcher's line heights (28 and 24 units of Nunito, the body's 32)
    CHECK(NEAR(b[0].h, 3 + 40 + 28 * 1.364 + 2 + 32.736, 0.01));
    CHECK(NEAR(b[1].y, b[0].y + b[0].h + 14, 0.01) && NEAR(b[2].y, b[1].y + b[1].h + 14, 0.01));
    CHECK(NEAR(b[3].h, 3 + 40 + 68, 0.01)); // no body: the disc's height
    // the card: its outline, its fill inside it, the disc; the texts
    CHECK(g_out.rects.size() == 12);
    CHECK(NEAR(g_out.rects[0].x0, 1232, 0.01) && NEAR(g_out.rects[0].x1, 1872, 0.01) && NEAR(g_out.rects[0].y0, 40, 0.01) && NEAR(g_out.rects[0].rounding, 18, 0.01));
    CHECK(g_out.rects[0].rgba == 0x5E5E64FF && g_out.rects[1].rgba == 0x4E4E55FF && NEAR(g_out.rects[1].x0, 1233.5, 0.01));
    CHECK(NEAR(g_out.rects[2].x1 - g_out.rects[2].x0, 68, 0.01) && NEAR(g_out.rects[2].rounding, 34, 0.01) && (g_out.rects[2].rgba & 0xFF) == 33); // 13 %
    CHECK(g_out.rects[9].rgba == 0xFF6B61FF); // the error card's outline is the bad colour
    bool luna = false, body = false;
    for (const std::string &t : g_out.texts) luna |= t == "Luna", body |= t == "is racing online now";
    CHECK(luna && body);
    // t1's time is over 4998 ms after the answer: it fades for 220 ms and is gone at 260 ms; the others close up
    g_now = t0 + 4997;
    CHECK(Frame(in) && NEAR(test::Boxes()[0].alpha, 1, 0.001));
    g_now = t0 + 4998;
    CHECK(Frame(in) && NEAR(test::Boxes()[0].alpha, 1, 0.001));
    g_now = t0 + 4998 + 110;
    CHECK(Frame(in) && test::Boxes().size() == 4 && NEAR(test::Boxes()[0].alpha, 0.5, 0.01) && NEAR(test::Boxes()[0].x, 1232, 0.01));
    g_now = t0 + 4998 + 259;
    CHECK(Frame(in) && test::Boxes().size() == 4 && NEAR(test::Boxes()[0].alpha, 0, 0.001));
    g_now = t0 + 4998 + 260 + 16;
    CHECK(Frame(in) && test::Boxes().size() == 3 && test::Boxes()[0].id == "t2" && NEAR(test::Boxes()[0].y, 40, 0.01));
    // the launcher lists another set: the new card comes in at its place, the ones it dropped fade where they were
    g_toasts_body = "{\"seq\":5,\"toasts\":[{\"id\":\"t3\",\"kind\":\"warning\",\"title\":\"Still here\",\"body\":\"\",\"icon\":\"warning\","
                    "\"expiresInMs\":4000,\"stayMs\":6000},{\"id\":\"t9\",\"kind\":\"info\",\"title\":\"New\",\"body\":\"\",\"icon\":\"info\","
                    "\"expiresInMs\":5000,\"stayMs\":5000}]}";
    CHECK(test::ToastPollStep() == 100 && g_asked.back().path == "/local/v1/toasts?wait=10&seq=4");
    const uint64_t t1 = g_now;
    CHECK(Frame(in) && test::Boxes().size() == 4);
    CHECK(test::Boxes()[0].id == "t2" && test::Boxes()[1].id == "t3" && test::Boxes()[2].id == "t4" && test::Boxes()[3].id == "t9");
    CHECK(NEAR(test::Boxes()[1].alpha, 1, 0.001) && NEAR(test::Boxes()[3].alpha, 0, 0.001));
    g_now = t1 + 300;
    CHECK(Frame(in) && test::Boxes().size() == 2 && test::Boxes()[0].id == "t3" && test::Boxes()[1].id == "t9" && NEAR(test::Boxes()[1].x, 1232, 0.01));
    // the runtime's own display at the top right (its setting is all API 4 tells): the toasts start under it
    g_perf_setting = "fps";
    CHECK(test::ToastPollStep() == 100 && Frame(in) && NEAR(test::Boxes()[0].y, 24 + 18 + 12, 0.01));
    g_perf_setting = "full";
    CHECK(test::ToastPollStep() == 100 && Frame(in) && NEAR(test::Boxes()[0].y, 480 + 12, 0.01));
    g_perf_setting = "off";
    CHECK(test::ToastPollStep() == 100 && Frame(in) && NEAR(test::Boxes()[0].y, 40, 0.01));
    // the launcher stops answering: two failures change nothing, the third drops what it showed (the cards fade), then off
    g_toasts_status = 0;
    CHECK(test::ToastPollStep() == 2000 && g_asked.back().path == "/local/v1/toasts?wait=10&seq=5");
    CHECK(test::ToastPollStep() == 2000 && g_asked.back().path == "/local/v1/toasts" && test::Stack().Have());
    CHECK(Frame(in) && test::Boxes().size() == 2 && NEAR(test::Boxes()[0].alpha, 1, 0.001));
    CHECK(test::ToastPollStep() == 2000 && !test::Stack().Have() && Logged("the launcher's local API does not answer"));
    CHECK(Frame(in) && test::Boxes().size() == 2);
    g_now += 100;
    CHECK(Frame(in) && test::Boxes().size() == 2 && test::Boxes()[0].alpha < 0.6f && g_shown == 1);
    g_now += 200;
    CHECK(Frame(in) && test::Boxes().empty() && g_shown == 0);
    CHECK(!Frame(in)); // the runtime no longer calls it
    // the back-off
    g_toasts_status = 404;
    CHECK(test::ToastPollStep() == 30000 && Logged("this launcher has none"));
    g_toasts_status = 401;
    CHECK(test::ToastPollStep() == 30000);
    g_toasts_status = 429;
    CHECK(test::ToastPollStep() == 1000);
    g_toasts_status = 200;
    g_toasts_body = "not json";
    CHECK(test::ToastPollStep() == 2000 && g_shown == 0);
    // no view is ever asked for on this tier (asking tells the launcher the shortcut is the mod's)
    for (const Asked &a : g_asked) CHECK(a.path.find("/overlay/") == std::string::npos);
}

// ================================================================ the toasts on plugin API 5 (the mod's own ImGui)
static void TestToastsExact(const std::string &toasts) {
    g_toasts_body = toasts;
    CHECK(Install() == INGAME_EXACT);
    const size_t textures = g_textures.size();
    CHECK(Frame() && g_out.lists == 0); // the first frame: the size is known, the fonts are asked for; nothing is drawn yet
    test::BuildFontsNow();
    CHECK(g_textures.size() == textures + 1 && g_atlas_w >= 512 && g_atlas_h >= 512 && g_atlas_w <= 8192 && g_atlas_h <= 8192);
    CHECK(Logged("ingame: Nunito baked at scale 1.00"));
    CHECK(g_shown == 1 && Frame() && g_out.lists == 0 && g_shown == 0); // adopted; nothing to show: off
    CHECK(test::ToastPollStep() == 100 && g_shown == 1);
    const uint64_t t0 = g_now;
    CHECK(Frame() && test::Boxes().size() == 4 && NEAR(test::Boxes()[0].x, 1232 + 120, 0.01));
    g_now = t0 + 240;
    CHECK(Frame());
    const auto &b = test::Boxes();
    // the same places as the launcher's ToastHost: a 640-wide column 48 from the right edge, from 40 down, 14 between
    CHECK(b.size() == 4 && NEAR(b[0].x, 1232, 0.01) && NEAR(b[0].y, 40, 0.01) && NEAR(b[0].w, 640, 0.01) && NEAR(b[0].alpha, 1, 0.001));
    CHECK(NEAR(b[0].h, 3 + 40 + 28 * 1.364 + 2 + 32.736, 0.01)); // a title line (28) and a body line (LineHeight 32, never under the font's)
    CHECK(NEAR(b[1].y, b[0].y + b[0].h + 14, 0.01) && NEAR(b[3].h, 3 + 40 + 68, 0.01));
    // what was handed to the runtime: triangles, every command with a texture of the mod's, inside the cards' column
    CHECK(g_out.lists >= 1 && g_out.commands >= 1 && g_out.vertices > 400 && g_out.indices % 3 == 0 && !g_out.bad_texture && !g_out.bad_index);
    CHECK(g_out.rects.empty() && g_out.texts.empty()); // nothing through the runtime's own rect and text
    CHECK(NEAR(g_out.min_x, 1232, 1.6) && NEAR(g_out.max_x, 1872, 1.6) && NEAR(g_out.min_y, 40, 1.6));
    CHECK(NEAR(g_out.max_y, b[3].y + b[3].h, 1.6));
    // fading: fewer opaque vertices do not move the cards
    g_now = t0 + 4998;
    CHECK(Frame());
    g_now = t0 + 4998 + 110;
    CHECK(Frame() && test::Boxes().size() == 4 && NEAR(test::Boxes()[0].alpha, 0.5, 0.01));
    g_now = t0 + 4998 + 300;
    CHECK(Frame() && test::Boxes().size() == 3 && test::Boxes()[0].id == "t2" && NEAR(g_out.min_y, 40, 1.6));
    // plugin API 5 says where the runtime's own display is: under it when it is over the column, unmoved otherwise
    FrameIn perf;
    perf.perf[0] = 1700, perf.perf[1] = 12, perf.perf[2] = 1908, perf.perf[3] = 46;
    CHECK(Frame(perf) && NEAR(test::Boxes()[0].y, 46 + 12, 0.01));
    perf.perf[0] = 20, perf.perf[2] = 300; // at the left: not in the way
    CHECK(Frame(perf) && NEAR(test::Boxes()[0].y, 40, 0.01));
    g_perf_setting = "full"; // its setting is not used when the runtime says where it is
    {
        std::string later = toasts; // (the launcher no longer lists t1)
        const size_t from = later.find("{\"id\":\"t1\""), to = later.find("{\"id\":\"t2\"");
        CHECK(from != std::string::npos && to != std::string::npos);
        later.erase(from, to - from);
        g_toasts_body = later;
    }
    CHECK(test::ToastPollStep() == 100 && Frame() && test::Boxes().size() == 3 && NEAR(test::Boxes()[0].y, 40, 0.01));
    g_perf_setting = "off";
    // another picture size (2160 lines): the cards scale at once, the fonts follow from their thread
    FrameIn big;
    big.w = 3840, big.h = 2160;
    CHECK(Frame(big) && NEAR(test::Boxes()[0].x, 3840 - (48 + 640) * 2, 0.01) && NEAR(test::Boxes()[0].w, 1280, 0.01) && NEAR(test::Boxes()[0].y, 80, 0.01));
    const uint32_t old_atlas = *g_textures.rbegin();
    test::BuildFontsNow();
    CHECK(Logged("baked at scale 2.00 (the page's text for 1.50)") && g_textures.size() == textures + 2);
    CHECK(Frame(big) && g_textures.size() == textures + 1 && !g_textures.count(old_atlas) && !g_out.bad_texture && g_out.vertices > 400);
    CHECK(NEAR(g_out.min_x, 2464, 2.5) && NEAR(g_out.max_x, 3744, 2.5));
    // back at 1080 for what follows
    CHECK(Frame());
    test::BuildFontsNow();
    CHECK(Frame() && !g_out.bad_texture);
    // gone: off
    g_now += 9000;
    CHECK(Frame() && test::Boxes().size() == 3); // their time is over: they start to fade
    g_now += 300;
    CHECK(Frame() && test::Boxes().empty() && g_shown == 0);
}

// ================================================================ the page
static MkwPad Pad(uint64_t buttons, int32_t lx = 0, int32_t ly = 0) {
    MkwPad p;
    memset(&p, 0, sizeof p);
    p.size = sizeof p;
    p.buttons = buttons, p.lx = lx, p.ly = ly;
    return p;
}
static void PadFrame(const MkwPad &pad) {
    FrameIn in;
    in.flags = MKW_OV_INPUT;
    in.pad = &pad;
    in.picture = 777;
    CHECK(Frame(in));
    g_now += 16;
}
// A button pressed and let go (one frame each).
static void Press(int bit) {
    PadFrame(Pad(1ull << bit));
    PadFrame(Pad(0));
}
static void Key(uint32_t code, uint8_t mods = 0) {
    FrameIn in;
    in.flags = MKW_OV_INPUT;
    MkwInputEvent e;
    memset(&e, 0, sizeof e);
    e.type = MKW_EV_KEY, e.down = 1, e.code = code, e.mods = mods;
    in.events.push_back(e);
    e.down = 0;
    in.events.push_back(e);
    CHECK(Frame(in));
    g_now += 16;
}
static void Mouse(uint8_t type, float x, float y, uint32_t code = 0, uint8_t down = 0) {
    FrameIn in;
    in.flags = MKW_OV_INPUT;
    MkwInputEvent e;
    memset(&e, 0, sizeof e);
    e.type = type, e.x = x, e.y = y, e.code = code, e.down = down;
    in.events.push_back(e);
    CHECK(Frame(in));
    g_now += 16;
}
// Frames for that long (none while the drawing is off: the runtime does not call it then).
static void Settle(int ms) {
    for (int t = 0; t < ms; t += 16) {
        if (g_shown) PadFrame(Pad(0));
        else g_now += 16;
    }
}

static void TestPage(const std::string &toasts, const std::string &view) {
    g_toasts_body = toasts, g_view_body = view;
    CHECK(Install() == INGAME_EXACT);
    CHECK(Frame());
    test::BuildFontsNow(); // (baked for this size by the test before: kept)
    if (g_shown) Frame();
    CHECK(g_shown == 0);
    // the shortcut before the launcher answered its page: not here (the caller opens the launcher's window overlay)
    CHECK(ingame_page_toggle("guide") == 0 && !ingame_page_open() && Logged("does not answer its overlay page"));
    CHECK(test::ViewPollStep() == 50 && g_asked.back().path == "/local/v1/overlay/view");
    CHECK(test::ViewPollStep() == 50 && g_asked.back().path == "/local/v1/overlay/view?wait=10&seq=1");
    CHECK(Logged("the launcher answers GET /local/v1/overlay/view"));
    // the shortcut: the page opens in the game, with the input; the game is not paused (nothing else is asked of the runtime)
    g_asked.clear();
    CHECK(ingame_page_toggle("guide") == 1 && ingame_page_open() && g_shown == 1 && g_capture == MKW_CAPTURE_INPUT);
    CHECK(Logged("ingame: the page opens (guide)"));
    PadFrame(Pad(0));
    PadFrame(Pad(0)); // (the first frame is the fade's start: nothing is drawn yet)
    CHECK(State("open") == "true" && State("entry") == "lobby" && State("focus") == "lobby.row:p1"); // "start": the lobby's players right away
    CHECK(g_picture == 541); // the game's picture is asked for while the page is up, half the picture high
    CHECK(Posted() == "{\"action\":\"show\",\"args\":{\"pane\":\"lobby\"}}" && g_asked.back().path == "/local/v1/overlay/action" && g_asked.back().method == "POST");
    Settle(200);
    CHECK(g_out.vertices > 2000 && !g_out.bad_texture && !g_out.bad_index); // (the game's picture, texture 777, is one of the commands)
    CHECK(NEAR(g_out.min_x, 0, 1.5) && NEAR(g_out.min_y, 0, 1.5) && NEAR(g_out.max_x, 1920, 1.5) && NEAR(g_out.max_y, 1080, 1.5));
    // the pad: down the rows, A on another player opens their column
    Press(MKW_PAD_A); // your own row: nothing to do to yourself
    CHECK(State("column") == "" && State("focus") == "lobby.row:p1");
    Press(MKW_PAD_DOWN);
    CHECK(State("focus") == "lobby.row:p2");
    PadFrame(Pad(0, 0, -30000)); // the left stick does what the D-pad does
    PadFrame(Pad(0));
    CHECK(State("focus") == "lobby.row:p3");
    PadFrame(Pad(1ull << MKW_PAD_UP)); // held: it repeats after 380 ms, then every 110 ms
    CHECK(State("focus") == "lobby.row:p2");
    for (int t = 0; t < 352; t += 16) PadFrame(Pad(1ull << MKW_PAD_UP));
    CHECK(State("focus") == "lobby.row:p2");
    for (int t = 0; t < 48; t += 16) PadFrame(Pad(1ull << MKW_PAD_UP));
    CHECK(State("focus") == "lobby.row:p1");
    PadFrame(Pad(0));
    Press(MKW_PAD_DOWN);
    Press(MKW_PAD_A);
    CHECK(State("column") == "card" && State("card") == "p2");
    Settle(320); // it slid up to the top edge
    CHECK(State("focus") == "col.card.report");
    CHECK(NEAR(g_out.min_x, 0, 1.5) && NEAR(g_out.max_x, 1920, 1.5)); // the game dimmed around it
    // Report: the reason first, then Send Report asked
    Press(MKW_PAD_A);
    CHECK(State("modal") == "What Happened" && State("focus") == "modal.bottom");
    Press(MKW_PAD_UP);
    CHECK(State("focus") == "modal.action:3");
    Press(MKW_PAD_UP), Press(MKW_PAD_UP), Press(MKW_PAD_UP);
    CHECK(State("focus") == "modal.action:0");
    Press(MKW_PAD_A);
    CHECK(State("modal") == "Report Abuse" && State("focus") == "modal.bottom");
    Press(MKW_PAD_B); // Cancel: nothing is sent
    CHECK(State("modal") == "" && State("focus") == "col.card.report" && Posted() == "");
    Press(MKW_PAD_A), Press(MKW_PAD_UP), Press(MKW_PAD_UP), Press(MKW_PAD_UP);
    CHECK(State("focus") == "modal.action:1");
    Press(MKW_PAD_A), Press(MKW_PAD_UP);
    CHECK(State("focus") == "modal.action:0");
    Press(MKW_PAD_A);
    CHECK(State("modal") == "" && Posted() == "{\"action\":\"report\",\"args\":{\"id\":\"p2\",\"reason\":\"abuse\"}}");
    CHECK(Logged("ingame: report sent to the launcher (HTTP 200)"));
    // Copy Report; a refusal is the launcher's to tell (its toast): the mod only logs it
    Press(MKW_PAD_DOWN);
    CHECK(State("focus") == "col.card.copy");
    g_action_status = 409;
    Press(MKW_PAD_A);
    CHECK(Posted() == "{\"action\":\"copyReport\",\"args\":{\"id\":\"p2\"}}" && Logged("ingame: copyReport sent to the launcher (HTTP 409: refused)"));
    g_action_status = 200;
    // a player who leaves the lobby: their open column closes
    {
        std::string without = view;
        const size_t from = without.find("{\"id\":\"p2\""), to = without.find("{\"id\":\"p3\"");
        CHECK(from != std::string::npos && to != std::string::npos);
        without.erase(from, to - from);
        without.replace(without.find("\"seq\":1"), 7, "\"seq\":2");
        g_view_body = without;
        CHECK(test::ViewPollStep() == 50);
        PadFrame(Pad(0));
        CHECK(State("closing") == "true");
        Settle(260);
        CHECK(State("column") == "" && State("card") == "" && State("focus") == "lobby.row:p1"); // their row went: the first one
        g_view_body = view;
        CHECK(test::ViewPollStep() == 50);
        Settle(32);
    }
    // B: back to the list, then the other entry (the launcher is told which pane shows), into its pane
    Press(MKW_PAD_B);
    CHECK(State("focus") == "list:lobby" && State("open") == "true");
    Press(MKW_PAD_UP);
    CHECK(State("focus") == "list:game" && State("entry") == "game" && Posted() == "{\"action\":\"show\",\"args\":{\"pane\":\"game\"}}");
    Press(MKW_PAD_RIGHT);
    CHECK(State("focus") == "game.back");
    Press(MKW_PAD_RIGHT);
    CHECK(State("focus") == "game.quit");
    // Quit Game asks first; taken by the launcher, the runtime ends the game
    Press(MKW_PAD_A);
    CHECK(State("modal") == "Quit MKW?" && g_quit == 0);
    Press(MKW_PAD_UP), Press(MKW_PAD_A);
    CHECK(Posted() == "{\"action\":\"quitGame\",\"args\":{}}" && g_quit == 1 && Logged("Quit Game: the game ends through the runtime"));
    // the keyboard: the arrows, Enter, Esc; the hints show key caps
    Key(80); // Left
    CHECK(State("focus") == "game.back" && State("input") == "keyboard");
    Key(41); // Esc on the Game pane: back to the game
    CHECK(!ingame_page_open() && g_capture == 0 && g_page_closed == 1);
    PadFrame(Pad(0));
    CHECK(State("open") == "false" && Posted() == "{\"action\":\"show\",\"args\":{\"pane\":\"\"}}" && Logged("ingame: the page closed"));
    CHECK(g_picture == 0);
    Settle(200);
    CHECK(g_shown == 0 && !Frame()); // faded out: the drawing is off again
    // the mouse: a tap on an entry selects it; the wheel and the back button
    g_now += 400;
    CHECK(ingame_page_toggle("keys") == 1 && g_capture == MKW_CAPTURE_INPUT);
    Settle(200);
    CHECK(State("entry") == "lobby");
    float x = 0, y = 0;
    CHECK(StopAt("list:game", x, y));
    Mouse(MKW_EV_MOUSE_POS, x - 40, y);
    Mouse(MKW_EV_MOUSE_POS, x, y);
    Mouse(MKW_EV_MOUSE_BUTTON, x, y, 0, 1);
    Mouse(MKW_EV_MOUSE_BUTTON, x, y, 0, 0);
    CHECK(State("entry") == "game" && State("focus") == "list:game" && State("input") == "mouse");
    CHECK(StopAt("game.quit", x, y));
    Mouse(MKW_EV_MOUSE_POS, x, y);
    Mouse(MKW_EV_MOUSE_BUTTON, x, y, 0, 1);
    Mouse(MKW_EV_MOUSE_BUTTON, x, y, 0, 0);
    CHECK(State("modal") == "Quit MKW?");
    Mouse(MKW_EV_MOUSE_BUTTON, 30, 30, 0, 1); // a tap beside the modal dismisses it
    Mouse(MKW_EV_MOUSE_BUTTON, 30, 30, 0, 0);
    CHECK(State("modal") == "" && g_quit == 1);
    Mouse(MKW_EV_MOUSE_BUTTON, 0, 0, 3, 1); // the mouse's back button is B: on the Game pane, back to the game
    CHECK(!ingame_page_open() && g_page_closed == 2);
    Settle(200);
    DrainActions();
    // the shortcut again closes it: seen by the page's own reading of the pad, and by the overlay's thread (once, not twice)
    g_now += 400;
    CHECK(ingame_page_toggle("chord") == 1 && ingame_page_open());
    PadFrame(Pad((1ull << MKW_PAD_MINUS) | (1ull << MKW_PAD_PLUS))); // still held from the opening: not a press
    PadFrame(Pad(0));
    CHECK(ingame_page_open());
    g_now += 400;
    g_shortcut = 1;
    PadFrame(Pad((1ull << MKW_PAD_MINUS) | (1ull << MKW_PAD_PLUS)));
    CHECK(!ingame_page_open() && Logged("ingame: the page closes (the shortcut)"));
    CHECK(ingame_page_toggle("chord") == 1 && !ingame_page_open()); // the same press, from the pad filter: not opened again
    PadFrame(Pad(0));
    CHECK(g_page_closed == 3 && g_capture == 0);
    g_now += 400;
    CHECK(ingame_page_toggle("guide") == 1 && ingame_page_open());
    PadFrame(Pad(0));
    g_now += 400;
    CHECK(ingame_page_toggle("guide") == 1 && !ingame_page_open()); // open: the shortcut closes it
    PadFrame(Pad(0));
    Settle(200);
    // Shift+Tab closes it too
    g_now += 400;
    CHECK(ingame_page_toggle("keys") == 1);
    PadFrame(Pad(0));
    g_now += 400;
    Key(43, MKW_MOD_SHIFT);
    CHECK(!ingame_page_open());
    Settle(200);
    // the toasts keep showing over the page
    g_now += 400;
    CHECK(ingame_page_toggle("guide") == 1);
    Settle(200);
    CHECK(test::ToastPollStep() == 100);
    Settle(260);
    CHECK(test::Boxes().size() == 4 && NEAR(test::Boxes()[0].x, 1232, 0.01) && State("open") == "true");
    // the runtime's menu over the page (F1): no input comes, the page stays
    {
        FrameIn menu;
        menu.flags = MKW_OV_MENU;
        CHECK(Frame(menu) && State("open") == "true");
    }
    // the launcher stops answering: the page closes by itself and the input goes back to the game
    g_view_status = 0;
    const int closed_before = g_page_closed;
    CHECK(test::ViewPollStep() == 2000 && test::ViewPollStep() == 2000 && ingame_page_open());
    CHECK(test::ViewPollStep() == 2000);
    PadFrame(Pad(0));
    CHECK(!ingame_page_open() && g_page_closed == closed_before + 1 && g_capture == 0 && Logged("the launcher stopped answering"));
    CHECK(ingame_page_toggle("guide") == 0); // and the shortcut is the launcher's window overlay's again
    g_view_status = 200;
    // the runtime refuses the input (another plugin's overlay has it): not here
    CHECK(test::ViewPollStep() == 50);
    g_now += 400;
    g_capture_ok = 0;
    CHECK(ingame_page_toggle("guide") == 0 && !ingame_page_open() && Logged("did not give the page the input"));
    g_capture_ok = 1;
}

// ================================================================ a fault in the drawing
static void TestFault(const std::string &toasts, const std::string &view) {
    g_toasts_body = toasts, g_view_body = view;
    CHECK(Install() == INGAME_EXACT && g_fault);
    CHECK(Frame());
    test::BuildFontsNow();
    CHECK(test::ViewPollStep() == 50 && test::ToastPollStep() == 100);
    g_now += 400;
    CHECK(ingame_page_toggle("guide") == 1);
    Settle(100);
    DrainActions();
    g_asked.clear();
    // the runtime caught a fault in the drawing: it turned it off and gave the input back; the mod is told once
    g_shown = 0, g_capture = 0;
    g_fault(nullptr);
    CHECK(ingame_tier() == INGAME_NONE && !ingame_page_open() && g_page_closed == 1);
    CHECK(Logged("ingame: the overlay's drawing faulted") && Logged("the launcher shows them itself"));
    CHECK(ingame_page_toggle("guide") == 0); // the launcher's window overlay from now on
    CHECK(Posted() == "{\"action\":\"show\",\"args\":{\"pane\":\"\"}}"); // the launcher is told the page is closed
    CHECK(g_shown == 0 && g_capture_calls == 1); // nothing more is asked of the runtime
    g_shown = 1;
    CHECK(Frame() && g_out.lists == 0 && g_out.rects.empty()); // and nothing is drawn if it were called
}

int main(int argc, char **argv) {
    const std::string data = argc > 1 ? argv[1] : "native/probe/tests/data";
    g_verbose = argc > 2 && !strcmp(argv[2], "-v");
    const std::string toasts = ReadFile(data + "/ingame_toasts.json"), view = ReadFile(data + "/ingame_view.json");
    if (toasts.empty() || view.empty()) {
        printf("test_ingame: the launcher's fixtures are not in %s\n", data.c_str());
        return 2;
    }
    g_toasts_body = toasts, g_view_body = view;
    TestModel();
    TestTiers();
    g_toasts_body = toasts;
    TestToastsBasic();
    TestToastsExact(toasts);
    TestPage(toasts, view);
    TestFault(toasts, view);
    printf("test_ingame: %d checks, %d failed\n", g_checks, g_failures);
    return g_failures ? 1 : 0;
}
