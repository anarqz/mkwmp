//! Rate limits and size caps for everything a client can ask of the lobby.
//!
//! Token buckets keyed by (rule, subject): the subject is the caller's IP for sign-in (before
//! a user exists), else the user id. Budgets sit far above what the game does: in a live
//! two-player run (scratch/run-lan11.log) the peaks were 17 KeepUserSession frames/s and
//! 13 WriteDocuments/s for both players together. Guessing a Room ID or a room password is
//! limited hardest.

use std::collections::HashMap;

/// A bucket's shape: `burst` tokens, refilled at `per_sec`.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Rate {
    pub burst: f64,
    pub per_sec: f64,
}

const fn rate(burst: f64, per_sec: f64) -> Rate {
    Rate { burst, per_sec }
}

/// What is being limited.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum Rule {
    /// IssuePrearrangedUserToken, per IP.
    SignIn,
    /// Gamesync IssueToken / RefreshToken, per user.
    GamesyncToken,
    /// CreateMatchmakingTicket, per user.
    Ticket,
    /// CreateGameSessionCreationTicket, per user.
    RoomCreate,
    /// GetGameSessionShortAlias / GetGameSession (Room ID lookups), per user.
    RoomLookup,
    /// JoinGameSession, per user.
    Join,
    /// A wrong room password, per user.
    WrongPassword,
    /// WriteDocuments, per user.
    Write,
    /// GetDocument / BulkGetDocuments, per user.
    Read,
    /// KeepUserSession client frames, per user.
    SessionFrame,
    /// PresenceService KeepAlive frames, per user.
    Presence,
    /// Opening a subscription stream (friends, presence, maintenance), per user.
    Subscribe,
    /// Any other RPC, per user.
    Other,
}

impl Rule {
    pub fn rate(self) -> Rate {
        match self {
            Rule::SignIn => rate(10.0, 0.2),          // 10 at once, then 12/min
            Rule::GamesyncToken => rate(10.0, 0.2),
            Rule::Ticket => rate(10.0, 0.2),
            Rule::RoomCreate => rate(5.0, 1.0 / 30.0), // 5, then 2/min
            Rule::RoomLookup => rate(10.0, 0.1),       // 10, then 6/min: no Room ID scanning
            Rule::Join => rate(10.0, 0.1),
            Rule::WrongPassword => rate(5.0, 1.0 / 60.0), // 5, then 1/min
            Rule::Write => rate(300.0, 60.0),
            Rule::Read => rate(200.0, 40.0),
            Rule::SessionFrame => rate(300.0, 60.0),
            Rule::Presence => rate(30.0, 5.0),
            Rule::Subscribe => rate(60.0, 1.0),
            Rule::Other => rate(100.0, 20.0),
        }
    }
}

/// Size caps on what a client can make the lobby store.
pub mod caps {
    /// Documents in one lobby.
    pub const DOCS_PER_LOBBY: usize = 4096;
    /// Encoded size of one stored document.
    pub const DOC_BYTES: usize = 16 * 1024;
    /// Writes in one WriteDocuments request.
    pub const WRITES_PER_REQUEST: usize = 64;
    /// Deferments kept per seat.
    pub const DEFERMENTS_PER_SEAT: usize = 64;
    /// Listens per KeepUserSession stream.
    pub const LISTENS_PER_STREAM: usize = 64;
    /// Presence attributes per player, and the encoded size of each.
    pub const PRESENCE_ATTRIBUTES: usize = 16;
    pub const PRESENCE_VALUE_BYTES: usize = 256;
    /// Names in one BulkGetDocuments / BatchGetGameSessions request.
    pub const BATCH_NAMES: usize = 64;
    /// Open streams per connection (subscriptions pile up if a client never closes them).
    pub const STREAMS_PER_CONNECTION: usize = 256;
}

#[derive(Clone, Copy, Debug)]
struct Bucket {
    tokens: f64,
    at: f64,
}

/// All buckets. Idle full buckets are dropped by [`Limiter::sweep`].
#[derive(Default)]
pub struct Limiter {
    buckets: HashMap<(Rule, String), Bucket>,
}

impl Limiter {
    /// Take one token; false = over the limit.
    pub fn allow(&mut self, rule: Rule, subject: &str, now: f64) -> bool {
        let r = rule.rate();
        let b = self.buckets.entry((rule, subject.to_owned())).or_insert(Bucket { tokens: r.burst, at: now });
        b.tokens = (b.tokens + (now - b.at).max(0.0) * r.per_sec).min(r.burst);
        b.at = now;
        if b.tokens >= 1.0 {
            b.tokens -= 1.0;
            true
        } else {
            false
        }
    }

    /// Forget buckets that have refilled completely.
    pub fn sweep(&mut self, now: f64) {
        self.buckets.retain(|(rule, _), b| {
            let r = rule.rate();
            b.tokens + (now - b.at) * r.per_sec < r.burst
        });
    }

    pub fn len(&self) -> usize {
        self.buckets.len()
    }

    pub fn is_empty(&self) -> bool {
        self.buckets.is_empty()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn burst_then_refill() {
        let mut l = Limiter::default();
        for _ in 0..5 {
            assert!(l.allow(Rule::WrongPassword, "u", 0.0));
        }
        assert!(!l.allow(Rule::WrongPassword, "u", 0.0));
        assert!(!l.allow(Rule::WrongPassword, "u", 30.0));
        assert!(l.allow(Rule::WrongPassword, "u", 61.0));
        assert!(l.allow(Rule::WrongPassword, "other", 0.0), "per subject");
    }

    #[test]
    fn game_traffic_never_hits_the_limits() {
        // 20 writes/s for 10 minutes per player (above the measured peak).
        let mut l = Limiter::default();
        for i in 0..12_000 {
            assert!(l.allow(Rule::Write, "u", i as f64 / 20.0));
        }
    }

    #[test]
    fn sweep_drops_refilled_buckets() {
        let mut l = Limiter::default();
        l.allow(Rule::Write, "u", 0.0);
        l.sweep(0.0);
        assert_eq!(l.len(), 1);
        l.sweep(10.0);
        assert!(l.is_empty());
    }
}
