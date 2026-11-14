//! Launcher presence: a profile signed in with its PIN, without a seat. It
//! is what the friends API authenticates with, and it is what "online"
//! means for friends: the launcher is running (window or tray). It never
//! touches the seat pool. In memory: a seat-server restart signs launchers
//! out, and they sign in again with the PIN.
//!
//! A presence lasts `TTL_MS` past its last request (the friends long-poll
//! keeps it fresh), and is bound to the PIN hash it was made with: a new
//! PIN, a deleted or released profile, or a name taken over all end it.

use std::collections::HashMap;

use rand::RngCore;

use crate::names;

/// A presence without any request for this long is gone.
pub const TTL_MS: u64 = 90_000;
/// Presences per profile (several devices).
const MAX_PER_NAME: usize = 4;

struct Entry {
    key: String,
    name: String,
    pin_hash: String,
    last_seen: u64,
}

#[derive(Default)]
pub struct Presence {
    tokens: HashMap<String, Entry>,
    changes: u64,
}

pub struct Who {
    pub key: String,
    pub name: String,
    pub pin_hash: String,
}

impl Presence {
    /// A new presence for a profile whose PIN was just checked.
    pub fn sign_in(&mut self, name: &str, pin_hash: &str, now: u64) -> String {
        self.sweep(now);
        let key = names::key(name);
        // Oldest presences of this profile make room.
        let mut mine: Vec<(String, u64)> =
            self.tokens.iter().filter(|(_, e)| e.key == key).map(|(t, e)| (t.clone(), e.last_seen)).collect();
        mine.sort_by_key(|(_, seen)| *seen);
        let was_online = !mine.is_empty();
        while mine.len() >= MAX_PER_NAME {
            let (t, _) = mine.remove(0);
            self.tokens.remove(&t);
        }
        let mut b = [0u8; 32];
        rand::rngs::OsRng.fill_bytes(&mut b);
        let token: String = b.iter().map(|x| format!("{x:02x}")).collect();
        self.tokens.insert(token.clone(), Entry { key, name: name.to_string(), pin_hash: pin_hash.into(), last_seen: now });
        if !was_online {
            self.changes += 1;
        }
        token
    }

    /// The presence behind `token`, refreshed. The caller checks the PIN
    /// hash against the registry (and calls [`Presence::end`] if it moved).
    pub fn touch(&mut self, token: &str, now: u64) -> Option<Who> {
        let e = self.tokens.get_mut(token)?;
        if now.saturating_sub(e.last_seen) > TTL_MS {
            self.tokens.remove(token);
            self.changes += 1;
            return None;
        }
        e.last_seen = now;
        Some(Who { key: e.key.clone(), name: e.name.clone(), pin_hash: e.pin_hash.clone() })
    }

    pub fn end(&mut self, token: &str) {
        if let Some(e) = self.tokens.remove(token) {
            if !self.tokens.values().any(|x| x.key == e.key) {
                self.changes += 1;
            }
        }
    }

    /// End every presence of a profile (deleted, released, new PIN).
    pub fn end_name(&mut self, name: &str) {
        let key = names::key(name);
        let before = self.tokens.len();
        self.tokens.retain(|_, e| e.key != key);
        if self.tokens.len() != before {
            self.changes += 1;
        }
    }

    pub fn online(&self, key: &str, now: u64) -> bool {
        self.tokens.values().any(|e| e.key == key && now.saturating_sub(e.last_seen) <= TTL_MS)
    }

    pub fn sweep(&mut self, now: u64) {
        let before = self.tokens.len();
        self.tokens.retain(|_, e| now.saturating_sub(e.last_seen) <= TTL_MS);
        if self.tokens.len() != before {
            self.changes += 1;
        }
    }

    pub fn changes(&self) -> u64 {
        self.changes
    }

    pub fn count(&self) -> usize {
        self.tokens.len()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sign_in_touch_expire() {
        let mut p = Presence::default();
        let t = p.sign_in("Ana", "h1", 0);
        assert!(p.online("ana", 1));
        assert_eq!(p.touch(&t, 50_000).unwrap().name, "Ana");
        assert!(p.online("ana", 50_000 + TTL_MS));
        assert!(p.touch(&t, 50_001 + TTL_MS).is_none());
        assert!(!p.online("ana", 50_001 + TTL_MS));
    }

    #[test]
    fn at_most_four_per_profile_and_end_name() {
        let mut p = Presence::default();
        let first = p.sign_in("Ana", "h", 0);
        for i in 1..=4 {
            p.sign_in("ana", "h", i);
        }
        assert_eq!(p.count(), 4);
        assert!(p.touch(&first, 5).is_none(), "the oldest made room");
        let c = p.changes();
        p.end_name("ANA");
        assert_eq!((p.count(), p.changes()), (0, c + 1));
    }
}
