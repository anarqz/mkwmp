// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-only; see LICENSE and NOTICE.
// The in-game overlay's model: the answers of the MKWMP launcher's GET /local/v1/toasts and GET /local/v1/overlay/view
// (docs/contracts/LAUNCHER_API.md, "In-game toasts (v1)" and "In-game overlay (v1)"), read into what the mod draws.
// Ported from Infinity's overlay (overlay/src/v4/toast_model.h, overlay_model.h): the JSON reader as it is, the view in
// MKWMP's shape (the Game and Lobby panes). Header only, no OS and no ImGui, so the host-side tests read the same bytes
// the same way. The launcher decides everything; unknown keys are ignored, a missing one keeps its default.
#pragma once

#include <cstdint>
#include <cstdlib>
#include <string>
#include <utility>
#include <vector>

namespace mkwmp::ov {

// ---------------------------------------------------------------- a small JSON reader (UTF-8 in, UTF-8 out)
struct Json {
    enum class Type : uint8_t { Null, Bool, Number, String, Array, Object };
    Type type = Type::Null;
    bool boolean = false;
    double number = 0;
    std::string text;
    std::vector<Json> items;                          // Array
    std::vector<std::pair<std::string, Json>> fields; // Object, in order

    const Json *Get(const char *key) const {
        if (type != Type::Object) return nullptr;
        for (const auto &f : fields)
            if (f.first == key) return &f.second;
        return nullptr;
    }
    std::string Str(const char *key, const char *fallback = "") const {
        const Json *v = Get(key);
        return v && v->type == Type::String ? v->text : std::string(fallback);
    }
    double Num(const char *key, double fallback = 0) const {
        const Json *v = Get(key);
        return v && v->type == Type::Number ? v->number : fallback;
    }
    bool Bool(const char *key, bool fallback = false) const {
        const Json *v = Get(key);
        return v && v->type == Type::Bool ? v->boolean : fallback;
    }
};

class JsonReader {
  public:
    // false: not JSON (or nested deeper than 32, or more than 1 MB)
    static bool Parse(const char *data, size_t size, Json &out) {
        if (size > (1u << 20)) return false;
        JsonReader r(data, size);
        r.Space();
        if (!r.Value(out, 0)) return false;
        r.Space();
        return r.at_ == r.end_;
    }

  private:
    JsonReader(const char *data, size_t size) : at_(data), end_(data + size) {}
    const char *at_, *end_;

    void Space() {
        while (at_ < end_ && (*at_ == ' ' || *at_ == '\t' || *at_ == '\r' || *at_ == '\n')) ++at_;
    }
    bool Word(const char *w) {
        size_t n = 0;
        while (w[n]) ++n;
        if (static_cast<size_t>(end_ - at_) < n) return false;
        for (size_t i = 0; i < n; ++i)
            if (at_[i] != w[i]) return false;
        at_ += n;
        return true;
    }
    static void Utf8(std::string &out, uint32_t c) {
        if (c < 0x80) {
            out += static_cast<char>(c);
        } else if (c < 0x800) {
            out += static_cast<char>(0xC0 | (c >> 6));
            out += static_cast<char>(0x80 | (c & 0x3F));
        } else if (c < 0x10000) {
            out += static_cast<char>(0xE0 | (c >> 12));
            out += static_cast<char>(0x80 | ((c >> 6) & 0x3F));
            out += static_cast<char>(0x80 | (c & 0x3F));
        } else {
            out += static_cast<char>(0xF0 | (c >> 18));
            out += static_cast<char>(0x80 | ((c >> 12) & 0x3F));
            out += static_cast<char>(0x80 | ((c >> 6) & 0x3F));
            out += static_cast<char>(0x80 | (c & 0x3F));
        }
    }
    bool Hex4(uint32_t &c) {
        if (end_ - at_ < 4) return false;
        c = 0;
        for (int i = 0; i < 4; ++i) {
            const char h = *at_++;
            c <<= 4;
            if (h >= '0' && h <= '9') c |= static_cast<uint32_t>(h - '0');
            else if (h >= 'a' && h <= 'f') c |= static_cast<uint32_t>(h - 'a' + 10);
            else if (h >= 'A' && h <= 'F') c |= static_cast<uint32_t>(h - 'A' + 10);
            else return false;
        }
        return true;
    }
    bool String(std::string &out) {
        if (at_ >= end_ || *at_ != '"') return false;
        ++at_;
        while (at_ < end_) {
            const char c = *at_++;
            if (c == '"') return true;
            if (static_cast<unsigned char>(c) < 0x20) return false;
            if (c != '\\') {
                out += c;
                continue;
            }
            if (at_ >= end_) return false;
            const char e = *at_++;
            switch (e) {
            case '"': out += '"'; break;
            case '\\': out += '\\'; break;
            case '/': out += '/'; break;
            case 'b': out += '\b'; break;
            case 'f': out += '\f'; break;
            case 'n': out += '\n'; break;
            case 'r': out += '\r'; break;
            case 't': out += '\t'; break;
            case 'u': {
                uint32_t c1 = 0;
                if (!Hex4(c1)) return false;
                if (c1 >= 0xD800 && c1 <= 0xDBFF) { // a surrogate pair
                    uint32_t c2 = 0;
                    if (!Word("\\u") || !Hex4(c2) || c2 < 0xDC00 || c2 > 0xDFFF) return false;
                    c1 = 0x10000 + ((c1 - 0xD800) << 10) + (c2 - 0xDC00);
                } else if (c1 >= 0xDC00 && c1 <= 0xDFFF) {
                    return false;
                }
                Utf8(out, c1);
                break;
            }
            default: return false;
            }
        }
        return false;
    }
    bool Value(Json &out, int depth) {
        if (depth > 32 || at_ >= end_) return false;
        const char c = *at_;
        if (c == '{') {
            ++at_;
            out.type = Json::Type::Object;
            Space();
            if (at_ < end_ && *at_ == '}') { ++at_; return true; }
            for (;;) {
                Space();
                std::pair<std::string, Json> f;
                if (!String(f.first)) return false;
                Space();
                if (at_ >= end_ || *at_++ != ':') return false;
                Space();
                if (!Value(f.second, depth + 1)) return false;
                out.fields.push_back(std::move(f));
                Space();
                if (at_ >= end_) return false;
                if (*at_ == ',') { ++at_; continue; }
                if (*at_ == '}') { ++at_; return true; }
                return false;
            }
        }
        if (c == '[') {
            ++at_;
            out.type = Json::Type::Array;
            Space();
            if (at_ < end_ && *at_ == ']') { ++at_; return true; }
            for (;;) {
                Space();
                Json item;
                if (!Value(item, depth + 1)) return false;
                out.items.push_back(std::move(item));
                Space();
                if (at_ >= end_) return false;
                if (*at_ == ',') { ++at_; continue; }
                if (*at_ == ']') { ++at_; return true; }
                return false;
            }
        }
        if (c == '"') { out.type = Json::Type::String; return String(out.text); }
        if (Word("true")) { out.type = Json::Type::Bool; out.boolean = true; return true; }
        if (Word("false")) { out.type = Json::Type::Bool; return true; }
        if (Word("null")) return true;
        if (c == '-' || (c >= '0' && c <= '9')) {
            char buffer[64];
            size_t n = 0;
            while (at_ < end_ && n + 1 < sizeof(buffer) &&
                   ((*at_ >= '0' && *at_ <= '9') || *at_ == '-' || *at_ == '+' || *at_ == '.' || *at_ == 'e' || *at_ == 'E'))
                buffer[n++] = *at_++;
            buffer[n] = 0;
            char *stop = nullptr;
            out.type = Json::Type::Number;
            out.number = std::strtod(buffer, &stop);
            return stop == buffer + n && n > 0;
        }
        return false;
    }
};

// A JSON string's content (quotes, backslashes and control characters escaped).
inline std::string JsonText(const std::string &text) {
    static const char hex[] = "0123456789abcdef";
    std::string out;
    for (unsigned char c : text) {
        if (c == '"' || c == '\\') {
            out += '\\';
            out += static_cast<char>(c);
        } else if (c < 0x20) {
            out += "\\u00";
            out += hex[c >> 4];
            out += hex[c & 15];
        } else {
            out += static_cast<char>(c);
        }
    }
    return out;
}

// A path segment, percent-encoded.
inline std::string Escape(const std::string &id) {
    static const char hex[] = "0123456789ABCDEF";
    std::string out;
    for (unsigned char c : id) {
        if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~') {
            out += static_cast<char>(c);
        } else {
            out += '%';
            out += hex[c >> 4];
            out += hex[c & 15];
        }
    }
    return out;
}

// ---------------------------------------------------------------- the toasts: what the launcher shows
struct Toast {
    std::string id, kind, title, body, icon;
    int64_t expires_in_ms = 0; // from the answer's arrival
    int64_t stay_ms = 0;
};

struct Palette {
    // 0xAARRGGBB, the launcher's dark theme until it says otherwise
    uint32_t toast = 0xFF4E4E55, rule = 0xFF5E5E64, accent = 0xFF5CB8FF, good = 0xFF3DD27A, warn = 0xFFFFB933, bad = 0xFFFF6B61,
             text = 0xFFF3F3F3, text_muted = 0xFFB1B1B6, track = 0xFF646469, glyph = 0xFFF3F3F3, on_glyph = 0xFF2A2A2D,
             on_accent = 0xFF0F1A26, surface = 0xFF434349;
};

struct Answer {
    uint64_t seq = 0;
    std::vector<Toast> toasts;
    Palette palette;
};

// "#AARRGGBB" (or "#RRGGBB"): true with the colour
inline bool ParseColour(const std::string &s, uint32_t &out) {
    if ((s.size() != 9 && s.size() != 7) || s[0] != '#') return false;
    uint32_t v = 0;
    for (size_t i = 1; i < s.size(); ++i) {
        const char h = s[i];
        v <<= 4;
        if (h >= '0' && h <= '9') v |= static_cast<uint32_t>(h - '0');
        else if (h >= 'a' && h <= 'f') v |= static_cast<uint32_t>(h - 'a' + 10);
        else if (h >= 'A' && h <= 'F') v |= static_cast<uint32_t>(h - 'A' + 10);
        else return false;
    }
    out = s.size() == 7 ? 0xFF000000u | v : v;
    return true;
}

// At most this many toasts are read (the launcher shows four).
constexpr size_t kMaxToasts = 12;
constexpr size_t kMaxText = 512; // bytes of one text (longer: cut at a character boundary)

inline std::string Cut(std::string s) {
    if (s.size() <= kMaxText) return s;
    size_t n = kMaxText;
    while (n > 0 && (static_cast<unsigned char>(s[n]) & 0xC0) == 0x80) --n;
    s.resize(n);
    return s;
}

// false: not an answer this overlay reads (then nothing changes)
inline bool ParseAnswer(const char *data, size_t size, Answer &out) {
    Json root;
    if (!JsonReader::Parse(data, size, root) || root.type != Json::Type::Object) return false;
    const Json *seq = root.Get("seq");
    const Json *list = root.Get("toasts");
    if (!seq || seq->type != Json::Type::Number || seq->number < 0 || !list || list->type != Json::Type::Array) return false;
    Answer a;
    a.seq = static_cast<uint64_t>(seq->number);
    for (const Json &t : list->items) {
        if (a.toasts.size() >= kMaxToasts) break;
        if (t.type != Json::Type::Object) continue;
        Toast x;
        x.id = Cut(t.Str("id"));
        if (x.id.empty()) continue;
        x.kind = t.Str("kind", "info");
        x.title = Cut(t.Str("title"));
        x.body = Cut(t.Str("body"));
        x.icon = t.Str("icon");
        x.expires_in_ms = static_cast<int64_t>(t.Num("expiresInMs", 5000));
        x.stay_ms = static_cast<int64_t>(t.Num("stayMs", 5000));
        if (x.expires_in_ms < 0) x.expires_in_ms = 0;
        if (x.stay_ms < 1) x.stay_ms = 1;
        a.toasts.push_back(std::move(x));
    }
    if (const Json *p = root.Get("palette"); p && p->type == Json::Type::Object) {
        struct Role { const char *name; uint32_t Palette::*field; };
        static const Role roles[] = {{"toast", &Palette::toast}, {"rule", &Palette::rule}, {"accent", &Palette::accent},
                                     {"good", &Palette::good}, {"warn", &Palette::warn}, {"bad", &Palette::bad},
                                     {"text", &Palette::text}, {"textMuted", &Palette::text_muted}, {"track", &Palette::track},
                                     {"glyph", &Palette::glyph}, {"onGlyph", &Palette::on_glyph}, {"onAccent", &Palette::on_accent},
                                     {"surface", &Palette::surface}};
        for (const Role &r : roles) ParseColour(p->Str(r.name), a.palette.*r.field);
    }
    out = std::move(a);
    return true;
}

// ---------------------------------------------------------------- the overlay page: what the launcher shows
struct OverlayPalette {
    // 0xAARRGGBB, the launcher's light theme (Themes/Colors.Light.xaml) until it says otherwise
    uint32_t background = 0xFFEBEBEB, surface = 0xFFFFFFFF, surface_alt = 0xFFF6F6F6, modal = 0xFFF0F0F0, text = 0xFF2B2B2B,
             text_muted = 0xFF6C6C6C, text_faint = 0xFFAEAEAE, accent = 0xFF1E64E6, accent_soft = 0x1F1E64E6, on_accent = 0xFFFFFFFF,
             rule = 0xFFC6C6C6, bar = 0xFF2B2B2B, good = 0xFF16A34A, warn = 0xFFD98200, bad = 0xFFE0352B, dim = 0x73000000,
             focus_fill = 0xFFFFFFFF, track = 0xFFCFCFCF, knob = 0xFFFFFFFF, hover = 0x0D000000, pressed = 0x1A000000, glyph = 0xFF2B2B2B,
             on_glyph = 0xFFEBEBEB, toast = 0xFFFFFFFF, outline = 0xFF8A8A8A;
};

struct OverlayTexts {
    std::string title = "MKWMP", ok = "OK", back = "Back", back_to_game = "Back to the Game", close = "Close", cancel = "Cancel",
                select = "Select";
};

struct OverlayEntry {
    std::string key, title, icon;
    bool separator = false, locked = false, soon = false;
};

struct Confirm { // a question asked first (Quit Game)
    bool present = false;
    std::string label, title, body, yes, no;
};

struct GamePane {
    std::string name, status, back_to_game = "Back to the Game";
    Confirm quit;
};

struct LobbyPlayer {
    std::string id, name, initials, renamed_label, renamed_body, player_id;
    std::vector<std::string> names; // every name of this online session, oldest first
    uint32_t disc = 0xFF2F6BFF;
    bool you = false, renamed = false, can_report = false, can_copy_report = false;
};

struct LobbyPane {
    std::string title, subtitle, network, note, you = "You";
    bool in_lobby = false;
    std::vector<LobbyPlayer> players;
    // the player column's texts
    std::string names_header = "NAMES \xC2\xB7 {n}", id_label = "Player ID", id_caption, report = "Report", copy_report = "Copy Report",
                copy_report_caption, no = "Cancel";
};

// Report (null on Nextendo and offline): the reasons to pick from, then Send Report asked first.
struct ReportTexts {
    bool present = false, enabled = false;
    std::string label, caption, title, what, send, no = "Cancel", why;
    std::vector<std::pair<std::string, std::string>> reasons; // code, label
};

struct OverlayView {
    uint64_t seq = 0;
    std::string shortcut, clock, start = "game";
    OverlayPalette palette;
    OverlayTexts text;
    std::vector<OverlayEntry> entries;
    std::vector<std::string> busy;
    GamePane game;
    LobbyPane lobby;
    ReportTexts report;

    bool Busy(const std::string &action) const {
        for (const auto &b : busy)
            if (b == action) return true;
        return false;
    }
};

namespace detail {

inline std::string S(const Json *o, const char *key, const char *fallback = "") { return o ? Cut(o->Str(key, fallback)) : std::string(fallback); }
inline bool B(const Json *o, const char *key, bool fallback = false) { return o ? o->Bool(key, fallback) : fallback; }
inline const Json *O(const Json *o, const char *key) {
    const Json *v = o ? o->Get(key) : nullptr;
    return v && v->type == Json::Type::Object ? v : nullptr;
}
inline const Json *A(const Json *o, const char *key) {
    const Json *v = o ? o->Get(key) : nullptr;
    return v && v->type == Json::Type::Array ? v : nullptr;
}
inline uint32_t C(const Json *o, const char *key, uint32_t fallback) {
    uint32_t c = fallback;
    if (o) ParseColour(o->Str(key), c);
    return c;
}
inline Confirm ReadConfirm(const Json *o) {
    Confirm c;
    if (!o) return c;
    c.present = true;
    c.label = S(o, "label");
    c.title = S(o, "title");
    c.body = S(o, "body");
    c.yes = S(o, "yes", "OK");
    c.no = S(o, "no", "Cancel");
    return c;
}

} // namespace detail

constexpr size_t kMaxRows = 32; // the lobby's players (the launcher sends at most 32)

// "NAMES · {n}" with n
inline std::string FormatCount(const std::string &format, size_t n) {
    std::string out;
    const std::string number = std::to_string(n);
    for (size_t i = 0; i < format.size(); ++i) {
        if (format.compare(i, 3, "{n}") == 0) {
            out += number;
            i += 2;
        } else {
            out += format[i];
        }
    }
    return out;
}

// false: not an answer this overlay reads (then nothing changes)
inline bool ParseView(const char *data, size_t size, OverlayView &out) {
    using namespace detail;
    Json root;
    if (!JsonReader::Parse(data, size, root) || root.type != Json::Type::Object) return false;
    const Json *seq = root.Get("seq");
    const Json *entries = A(&root, "entries");
    if (!seq || seq->type != Json::Type::Number || seq->number < 0 || !entries) return false;
    OverlayView v;
    v.seq = static_cast<uint64_t>(seq->number);
    v.shortcut = S(&root, "shortcut");
    v.clock = S(&root, "clock");
    v.start = S(&root, "start", "game");
    if (const Json *p = O(&root, "palette")) {
        struct Role { const char *name; uint32_t OverlayPalette::*field; };
        static const Role roles[] = {
            {"background", &OverlayPalette::background}, {"surface", &OverlayPalette::surface}, {"surfaceAlt", &OverlayPalette::surface_alt},
            {"modal", &OverlayPalette::modal}, {"text", &OverlayPalette::text}, {"textMuted", &OverlayPalette::text_muted},
            {"textFaint", &OverlayPalette::text_faint}, {"accent", &OverlayPalette::accent}, {"accentSoft", &OverlayPalette::accent_soft},
            {"onAccent", &OverlayPalette::on_accent}, {"rule", &OverlayPalette::rule}, {"bar", &OverlayPalette::bar}, {"good", &OverlayPalette::good},
            {"warn", &OverlayPalette::warn}, {"bad", &OverlayPalette::bad}, {"dim", &OverlayPalette::dim}, {"focusFill", &OverlayPalette::focus_fill},
            {"track", &OverlayPalette::track}, {"knob", &OverlayPalette::knob}, {"hover", &OverlayPalette::hover}, {"pressed", &OverlayPalette::pressed},
            {"glyph", &OverlayPalette::glyph}, {"onGlyph", &OverlayPalette::on_glyph}, {"toast", &OverlayPalette::toast}, {"outline", &OverlayPalette::outline}};
        for (const Role &r : roles) ParseColour(p->Str(r.name), v.palette.*r.field);
    }
    if (const Json *t = O(&root, "text")) {
        OverlayTexts d;
        v.text.title = S(t, "title", d.title.c_str());
        v.text.ok = S(t, "ok", d.ok.c_str());
        v.text.back = S(t, "back", d.back.c_str());
        v.text.back_to_game = S(t, "backToGame", d.back_to_game.c_str());
        v.text.close = S(t, "close", d.close.c_str());
        v.text.cancel = S(t, "cancel", d.cancel.c_str());
        v.text.select = S(t, "select", d.select.c_str());
    }
    for (const Json &e : entries->items) {
        if (e.type != Json::Type::Object || v.entries.size() >= 12) continue;
        OverlayEntry x;
        x.key = S(&e, "key");
        if (x.key.empty()) continue;
        x.title = S(&e, "title");
        x.icon = S(&e, "icon");
        x.separator = B(&e, "separator");
        x.locked = B(&e, "locked");
        x.soon = B(&e, "soon");
        v.entries.push_back(std::move(x));
    }
    if (v.entries.empty()) return false;
    if (const Json *busy = A(&root, "busy"))
        for (const Json &b : busy->items)
            if (b.type == Json::Type::String && v.busy.size() < 64) v.busy.push_back(Cut(b.text));
    if (const Json *g = O(&root, "game")) {
        v.game.name = S(g, "name");
        v.game.status = S(g, "status");
        v.game.back_to_game = S(g, "backToGame", "Back to the Game");
        v.game.quit = ReadConfirm(O(g, "quit"));
    }
    if (const Json *l = O(&root, "lobby")) {
        LobbyPane &p = v.lobby;
        p.title = S(l, "title");
        p.subtitle = S(l, "subtitle");
        p.network = S(l, "network");
        p.note = S(l, "note");
        p.you = S(l, "you", "You");
        p.in_lobby = B(l, "inLobby");
        if (const Json *players = A(l, "players")) {
            for (const Json &e : players->items) {
                if (e.type != Json::Type::Object || p.players.size() >= kMaxRows) continue;
                LobbyPlayer x;
                x.id = S(&e, "id");
                x.name = S(&e, "name");
                if (x.id.empty() || x.name.empty()) continue;
                x.initials = S(&e, "initials", "?");
                x.disc = C(&e, "disc", x.disc);
                x.you = B(&e, "you");
                x.renamed = B(&e, "renamed");
                x.renamed_label = S(&e, "renamedLabel");
                x.renamed_body = S(&e, "renamedBody");
                x.player_id = S(&e, "playerId");
                x.can_report = B(&e, "canReport");
                x.can_copy_report = B(&e, "canCopyReport");
                if (const Json *names = A(&e, "names"))
                    for (const Json &n : names->items)
                        if (n.type == Json::Type::String && x.names.size() < 32) x.names.push_back(Cut(n.text));
                p.players.push_back(std::move(x));
            }
        }
        const Json *pl = O(l, "player");
        LobbyPane d;
        p.names_header = S(pl, "namesHeader", d.names_header.c_str());
        p.id_label = S(pl, "idLabel", d.id_label.c_str());
        p.id_caption = S(pl, "idCaption");
        p.report = S(pl, "report", d.report.c_str());
        p.copy_report = S(pl, "copyReport", d.copy_report.c_str());
        p.copy_report_caption = S(pl, "copyReportCaption");
        p.no = S(pl, "no", d.no.c_str());
    }
    if (const Json *r = O(&root, "report")) {
        ReportTexts &x = v.report;
        x.present = true;
        x.enabled = B(r, "enabled");
        x.label = S(r, "label", "Report");
        x.caption = S(r, "caption");
        x.title = S(r, "title");
        x.what = S(r, "what");
        x.send = S(r, "send", "Send Report");
        x.no = S(r, "no", "Cancel");
        x.why = S(r, "why");
        if (const Json *reasons = A(r, "reasons"))
            for (const Json &e : reasons->items) {
                if (e.type != Json::Type::Object || x.reasons.size() >= 16) continue;
                std::string code = S(&e, "code"), label = S(&e, "label");
                if (!code.empty()) x.reasons.emplace_back(std::move(code), label.empty() ? code : std::move(label));
            }
        if (x.reasons.empty()) x.enabled = false;
    }
    out = std::move(v);
    return true;
}

} // namespace mkwmp::ov
