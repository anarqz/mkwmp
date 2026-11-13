//! Domain core for the mkwmp services.
//!
//! - [`seats`]: bounded server seats claimed by username.
//! - [`tickets`]: expiring player tickets issued for a seat.
//! - [`lobbies`]: lobbies opened on demand per game mode and closed when empty.
//!
//! Time and identifiers are injected ([`Clock`], [`IdSource`]) so every rule is
//! testable without I/O.

pub mod lobbies;
pub mod seats;
pub mod tickets;

use std::time::{Duration, SystemTime, UNIX_EPOCH};

/// Seconds since the Unix epoch.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct Instant(pub u64);

impl Instant {
    pub fn after(self, d: Duration) -> Self {
        Instant(self.0 + d.as_secs())
    }
}

pub trait Clock {
    fn now(&self) -> Instant;
}

pub struct SystemClock;

impl Clock for SystemClock {
    fn now(&self) -> Instant {
        Instant(SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |d| d.as_secs()))
    }
}

/// Source of unique opaque identifiers.
pub trait IdSource {
    fn next_id(&mut self) -> u64;
}

/// Monotonic ids; enough for a single process.
#[derive(Default)]
pub struct SequentialIds(u64);

impl IdSource for SequentialIds {
    fn next_id(&mut self) -> u64 {
        self.0 += 1;
        self.0
    }
}
