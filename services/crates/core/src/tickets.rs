//! Player tickets: proof that a seated player may request matchmaking.

use std::collections::HashMap;
use std::time::Duration;

use crate::seats::{SeatId, Username};
use crate::{Clock, IdSource, Instant};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct TicketId(pub u64);

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PlayerTicket {
    pub id: TicketId,
    pub user: Username,
    pub seat: SeatId,
    pub expires_at: Instant,
}

#[derive(Debug, PartialEq, Eq)]
pub enum TicketError {
    Unknown,
    Expired,
}

/// Issues and validates tickets with a fixed lifetime.
pub struct TicketBook<C: Clock, I: IdSource> {
    clock: C,
    ids: I,
    ttl: Duration,
    live: HashMap<TicketId, PlayerTicket>,
}

impl<C: Clock, I: IdSource> TicketBook<C, I> {
    pub fn new(clock: C, ids: I, ttl: Duration) -> Self {
        TicketBook { clock, ids, ttl, live: HashMap::new() }
    }

    pub fn issue(&mut self, user: Username, seat: SeatId) -> PlayerTicket {
        let ticket = PlayerTicket {
            id: TicketId(self.ids.next_id()),
            user,
            seat,
            expires_at: self.clock.now().after(self.ttl),
        };
        self.live.insert(ticket.id, ticket.clone());
        ticket
    }

    pub fn validate(&self, id: TicketId) -> Result<&PlayerTicket, TicketError> {
        let ticket = self.live.get(&id).ok_or(TicketError::Unknown)?;
        if self.clock.now() >= ticket.expires_at {
            return Err(TicketError::Expired);
        }
        Ok(ticket)
    }

    pub fn revoke(&mut self, id: TicketId) -> Option<PlayerTicket> {
        self.live.remove(&id)
    }

    /// Drops expired tickets and returns them so their seats can be freed.
    pub fn sweep(&mut self) -> Vec<PlayerTicket> {
        let now = self.clock.now();
        let expired: Vec<TicketId> =
            self.live.values().filter(|t| now >= t.expires_at).map(|t| t.id).collect();
        expired.into_iter().filter_map(|id| self.live.remove(&id)).collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::SequentialIds;
    use std::cell::Cell;
    use std::rc::Rc;

    #[derive(Clone)]
    struct FakeClock(Rc<Cell<u64>>);
    impl Clock for FakeClock {
        fn now(&self) -> Instant {
            Instant(self.0.get())
        }
    }

    #[test]
    fn tickets_expire_and_are_swept() {
        let time = Rc::new(Cell::new(100));
        let mut book = TicketBook::new(FakeClock(time.clone()), SequentialIds::default(), Duration::from_secs(60));
        let t = book.issue(Username::parse("alice").unwrap(), SeatId(0));
        assert!(book.validate(t.id).is_ok());
        time.set(160);
        assert_eq!(book.validate(t.id), Err(TicketError::Expired));
        assert_eq!(book.sweep(), vec![t]);
        assert_eq!(book.validate(TicketId(1)), Err(TicketError::Unknown));
    }
}
