// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-only; see LICENSE and NOTICE.
// The in-game toasts' cards: the launcher's list (GET /local/v1/toasts) turned into what is on screen, with the
// timing of the launcher's ToastHost. Ported from Infinity's overlay (overlay/src/v4/toasts_v4.cpp: Sync, the
// constants, the animation), without its invite toasts (no toast of the MKWMP launcher is one). No OS, no ImGui: both
// renderers (toasts_exact.cpp on plugin API 5, toasts_basic.cpp on API 4) draw from it, and the tests drive its clock.
//
// Threads: Offer / Drop on the poll thread, everything else on the present thread; the answer is copied under a lock
// held for the copy only.
#pragma once

#include "model.h"

#include <algorithm>
#include <cstddef>
#include <mutex>

namespace mkwmp::ov {

// The look (ToastHost.xaml, in stage units: 1080 high)
constexpr float kStackWidth = 640, kStackTop = 40, kStackRight = 48, kGap = 14;
constexpr float kRadius = 18, kRule = 1.5f;
constexpr float kCardPadX = 24, kCardPadY = 20, kCardDisc = 68, kCardGap = 20;
constexpr uint64_t kEnterMs = 240, kFadeInMs = 200, kToastOutMs = 220, kToastGoneMs = 260;

inline float EaseOut(float t) { t = t < 0 ? 0 : t > 1 ? 1 : t; const float u = 1 - t; return 1 - u * u * u; }
inline float EaseIn(float t) { t = t < 0 ? 0 : t > 1 ? 1 : t; return t * t * t; }

struct Card {
    Toast toast;
    uint64_t expires_at = 0; // the host's clock, ms
    uint64_t appeared = 0;
    uint64_t left = 0;       // 0: on screen; else when it started to go
};

struct CardAnim {
    float alpha = 1; // 0..1
    float shift = 0; // stage units to the right
};

class ToastStack {
  public:
    // ---- the poll thread
    void Offer(Answer answer, uint64_t now) {
        std::lock_guard<std::mutex> lock(lock_);
        answer_ = std::move(answer);
        answer_at_ = now ? now : 1;
        have_ = true;
    }
    // The launcher stopped answering: what it showed goes (the cards fade).
    void Drop() {
        std::lock_guard<std::mutex> lock(lock_);
        have_ = false;
        answer_ = Answer{};
    }
    bool Have() {
        std::lock_guard<std::mutex> lock(lock_);
        return have_;
    }
    // An answer the present thread has not taken yet lists a toast.
    bool Unseen() {
        std::lock_guard<std::mutex> lock(lock_);
        return have_ && (answer_.seq != seen_seq_ || answer_at_ != seen_at_) && !answer_.toasts.empty();
    }

    // ---- the present thread
    // The launcher's list into the cards (new ones come in at their place, those it no longer lists go out), then what
    // goes by itself: a toast whose time is over fades, a faded one is taken out.
    void Tick(uint64_t now) {
        Sync(now);
        for (Card &c : cards_)
            if (!c.left && now >= c.expires_at) c.left = now;
        cards_.erase(std::remove_if(cards_.begin(), cards_.end(), [&](const Card &c) { return c.left && now - c.left >= kToastGoneMs; }),
                     cards_.end());
    }
    const std::vector<Card> &Cards() const { return cards_; }
    const Palette &Colours() const { return palette_; }

    static CardAnim Anim(const Card &c, uint64_t now) {
        CardAnim a;
        const float in = static_cast<float>(now - c.appeared);
        if (c.appeared && !c.left) {
            a.shift = 120 * (1 - EaseOut(in / kEnterMs));
            a.alpha = std::min(1.0f, in / kFadeInMs);
        }
        if (c.left) {
            const float out = static_cast<float>(now - c.left);
            a.alpha = 1 - std::min(1.0f, out / kToastOutMs);
        }
        return a;
    }

  private:
    void Sync(uint64_t now) {
        Answer answer;
        uint64_t at = 0;
        bool have = false;
        {
            std::lock_guard<std::mutex> lock(lock_);
            have = have_;
            if (have && (answer_.seq != seen_seq_ || answer_at_ != seen_at_)) {
                answer = answer_;
                at = answer_at_;
                seen_seq_ = answer_.seq;
                seen_at_ = answer_at_;
            } else if (have) {
                return; // nothing new
            } else {
                seen_seq_ = ~0ull;
            }
        }
        if (!have) {
            for (Card &c : cards_)
                if (!c.left) c.left = now;
            return;
        }
        palette_ = answer.palette;
        std::vector<Card> next;
        for (const Toast &t : answer.toasts) {
            auto it = std::find_if(cards_.begin(), cards_.end(), [&](const Card &c) { return c.toast.id == t.id; });
            if (it != cards_.end() && it->left) continue; // going already (its time is over): it does not come back
            Card c = it != cards_.end() ? *it : Card{};
            if (it == cards_.end()) c.appeared = now ? now : 1;
            c.toast = t;
            c.expires_at = at + static_cast<uint64_t>(t.expires_in_ms);
            next.push_back(std::move(c));
        }
        // the cards it no longer lists go out where they were (the others close up once they are gone, as in the ToastHost)
        for (size_t i = 0; i < cards_.size(); ++i) {
            Card c = cards_[i];
            const bool listed = !c.left && std::any_of(answer.toasts.begin(), answer.toasts.end(), [&](const Toast &x) { return x.id == c.toast.id; });
            if (listed) continue;
            if (!c.left) c.left = now ? now : 1;
            next.insert(next.begin() + static_cast<std::ptrdiff_t>(std::min(i, next.size())), std::move(c));
        }
        cards_ = std::move(next);
    }

    std::mutex lock_;
    Answer answer_;          // the last answer read
    uint64_t answer_at_ = 0; // when it arrived (expiresInMs counts from it)
    bool have_ = false;      // an answer is held (cleared when the launcher stops answering)
    uint64_t seen_seq_ = ~0ull, seen_at_ = 0;

    std::vector<Card> cards_; // the present thread's
    Palette palette_;
};

} // namespace mkwmp::ov
