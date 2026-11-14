//! Sliding-window rate limits for the profile endpoints, in memory.
//!
//! | bucket             | key        | limit          |
//! | register           | ip         | 5 per hour     |
//! | pin failures       | ip         | 10 per 15 min  |
//! | pin failures       | name       | 10 per hour    |
//! | joins              | ip         | 30 per minute  |
//! | launcher sign-ins  | ip         | 30 per minute  |
//! | friend requests    | profile    | 20 per hour    |
//! | friend answers     | profile    | 120 per hour   |
//! | friends reads      | presence   | 120 per minute |
//! | poll / heartbeat   | session    | 60 per minute  |
//! | lobby roster       | token      | 120 per minute |
//! | status, profile    | ip         | 120 per minute |
//!
//! Every refusal is a 429 with Retry-After.
//!
//! With 10 wrong PINs per name per hour, guessing a 6-digit PIN takes on the
//! order of 5 years on average.

use std::collections::{HashMap, VecDeque};

#[derive(Debug, Clone, Copy)]
pub struct Limit {
    pub max: usize,
    pub window_ms: u64,
}

pub const REGISTER_PER_IP: Limit = Limit { max: 5, window_ms: 3_600_000 };
pub const PIN_FAIL_PER_IP: Limit = Limit { max: 10, window_ms: 900_000 };
pub const PIN_FAIL_PER_NAME: Limit = Limit { max: 10, window_ms: 3_600_000 };
pub const JOIN_PER_IP: Limit = Limit { max: 30, window_ms: 60_000 };
pub const PRESENCE_PER_IP: Limit = Limit { max: 30, window_ms: 60_000 };
pub const FRIEND_ADD_PER_NAME: Limit = Limit { max: 20, window_ms: 3_600_000 };
pub const FRIEND_ACT_PER_NAME: Limit = Limit { max: 120, window_ms: 3_600_000 };
pub const FRIENDS_GET_PER_TOKEN: Limit = Limit { max: 120, window_ms: 60_000 };
pub const POLL_PER_SESSION: Limit = Limit { max: 60, window_ms: 60_000 };
pub const ROSTER_PER_TOKEN: Limit = Limit { max: 120, window_ms: 60_000 };
pub const READS_PER_IP: Limit = Limit { max: 120, window_ms: 60_000 };

#[derive(Default)]
pub struct Limiter {
    hits: HashMap<String, VecDeque<u64>>,
}

impl Limiter {
    fn trim(q: &mut VecDeque<u64>, window_ms: u64, now: u64) {
        while q.front().is_some_and(|&t| now.saturating_sub(t) >= window_ms) {
            q.pop_front();
        }
    }

    /// Milliseconds until `key` may act again, or 0 if it may now.
    pub fn wait_ms(&mut self, key: &str, l: Limit, now: u64) -> u64 {
        let Some(q) = self.hits.get_mut(key) else { return 0 };
        Self::trim(q, l.window_ms, now);
        if q.len() < l.max {
            0
        } else {
            (q.front().copied().unwrap_or(now) + l.window_ms).saturating_sub(now)
        }
    }

    pub fn record(&mut self, key: &str, l: Limit, now: u64) {
        let q = self.hits.entry(key.to_string()).or_default();
        Self::trim(q, l.window_ms, now);
        q.push_back(now);
    }

    /// Check and count in one step.
    pub fn take(&mut self, key: &str, l: Limit, now: u64) -> Result<(), u64> {
        match self.wait_ms(key, l, now) {
            0 => {
                self.record(key, l, now);
                Ok(())
            }
            w => Err(w),
        }
    }

    /// Forget idle keys (memory bound).
    pub fn sweep(&mut self, now: u64) {
        self.hits.retain(|_, q| q.back().is_some_and(|&t| now.saturating_sub(t) < 3_600_000));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn window_slides() {
        let mut l = Limiter::default();
        let lim = Limit { max: 2, window_ms: 1000 };
        assert!(l.take("a", lim, 0).is_ok());
        assert!(l.take("a", lim, 100).is_ok());
        assert_eq!(l.take("a", lim, 200), Err(800));
        assert!(l.take("b", lim, 200).is_ok());
        assert!(l.take("a", lim, 1000).is_ok());
    }
}
