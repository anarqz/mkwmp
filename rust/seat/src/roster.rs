//! Seat roster: a fixed number of seats, each held by a username, each with a
//! ticket (bearer token). In-memory today; the trait lets a distributed store
//! replace it without touching the Auth handler.

use std::collections::HashMap;
use std::sync::Mutex;

#[derive(Debug, PartialEq, Eq)]
pub enum SeatError {
    Full,
    NameTaken,
}

pub trait Roster: Send + Sync {
    /// Claim a seat for `username`, returning its ticket. Idempotent per name.
    fn claim(&self, username: &str) -> Result<String, SeatError>;
    fn release(&self, ticket: &str) -> bool;
    fn user_of(&self, ticket: &str) -> Option<String>;
}

pub struct MemoryRoster {
    capacity: usize,
    state: Mutex<State>,
}

#[derive(Default)]
struct State {
    by_ticket: HashMap<String, String>, // ticket -> username
    by_name: HashMap<String, String>,   // username -> ticket
    next: u64,
}

impl MemoryRoster {
    pub fn new(capacity: usize) -> Self {
        Self { capacity, state: Mutex::new(State::default()) }
    }
}

impl Roster for MemoryRoster {
    fn claim(&self, username: &str) -> Result<String, SeatError> {
        let mut s = self.state.lock().unwrap();
        if let Some(ticket) = s.by_name.get(username) {
            return Ok(ticket.clone());
        }
        if s.by_ticket.len() >= self.capacity {
            return Err(SeatError::Full);
        }
        s.next += 1;
        let ticket = format!("seat-{:016x}", s.next);
        s.by_ticket.insert(ticket.clone(), username.to_string());
        s.by_name.insert(username.to_string(), ticket.clone());
        Ok(ticket)
    }

    fn release(&self, ticket: &str) -> bool {
        let mut s = self.state.lock().unwrap();
        if let Some(name) = s.by_ticket.remove(ticket) {
            s.by_name.remove(&name);
            true
        } else {
            false
        }
    }

    fn user_of(&self, ticket: &str) -> Option<String> {
        self.state.lock().unwrap().by_ticket.get(ticket).cloned()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn claim_is_idempotent_per_name() {
        let r = MemoryRoster::new(2);
        let a = r.claim("ana").unwrap();
        assert_eq!(a, r.claim("ana").unwrap());
        assert_eq!(r.user_of(&a).as_deref(), Some("ana"));
    }

    #[test]
    fn capacity_is_enforced_and_release_frees_a_seat() {
        let r = MemoryRoster::new(1);
        let t = r.claim("ana").unwrap();
        assert_eq!(r.claim("bia"), Err(SeatError::Full));
        assert!(r.release(&t));
        assert!(r.claim("bia").is_ok());
    }
}
