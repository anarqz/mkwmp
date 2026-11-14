//! Server seats: a fixed number of players may be online at once.

use std::collections::HashMap;
use std::fmt;

#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub struct Username(String);

impl Username {
    /// 3–16 characters: ASCII letters, digits, `_` or `-`.
    pub fn parse(raw: &str) -> Result<Self, SeatError> {
        let ok = (3..=16).contains(&raw.len())
            && raw.chars().all(|c| c.is_ascii_alphanumeric() || c == '_' || c == '-');
        ok.then(|| Username(raw.to_owned())).ok_or(SeatError::InvalidUsername)
    }

    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl fmt::Display for Username {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct SeatId(pub u32);

#[derive(Debug, PartialEq, Eq)]
pub enum SeatError {
    InvalidUsername,
    UsernameTaken,
    Full,
}

/// Seats `0..capacity`; one per username.
pub struct SeatPool {
    capacity: u32,
    by_user: HashMap<Username, SeatId>,
    free: Vec<SeatId>,
}

impl SeatPool {
    pub fn new(capacity: u32) -> Self {
        SeatPool { capacity, by_user: HashMap::new(), free: (0..capacity).rev().map(SeatId).collect() }
    }

    pub fn claim(&mut self, user: Username) -> Result<SeatId, SeatError> {
        if self.by_user.contains_key(&user) {
            return Err(SeatError::UsernameTaken);
        }
        let seat = self.free.pop().ok_or(SeatError::Full)?;
        self.by_user.insert(user, seat);
        Ok(seat)
    }

    pub fn release(&mut self, user: &Username) -> bool {
        match self.by_user.remove(user) {
            Some(seat) => {
                self.free.push(seat);
                true
            }
            None => false,
        }
    }

    pub fn seat_of(&self, user: &Username) -> Option<SeatId> {
        self.by_user.get(user).copied()
    }

    pub fn occupied(&self) -> u32 {
        self.capacity - self.free.len() as u32
    }

    pub fn capacity(&self) -> u32 {
        self.capacity
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn user(name: &str) -> Username {
        Username::parse(name).unwrap()
    }

    #[test]
    fn usernames_are_validated() {
        assert!(Username::parse("ab").is_err());
        assert!(Username::parse("has space").is_err());
        assert!(Username::parse("racer_01").is_ok());
    }

    #[test]
    fn seats_are_bounded_unique_and_reusable() {
        let mut pool = SeatPool::new(2);
        let a = pool.claim(user("alice")).unwrap();
        assert_eq!(pool.claim(user("alice")), Err(SeatError::UsernameTaken));
        pool.claim(user("bob")).unwrap();
        assert_eq!(pool.claim(user("carol")), Err(SeatError::Full));
        assert!(pool.release(&user("alice")));
        assert_eq!(pool.claim(user("carol")), Ok(a));
        assert_eq!(pool.occupied(), 2);
    }
}
