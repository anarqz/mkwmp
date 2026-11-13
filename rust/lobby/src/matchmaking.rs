//! Matchmaking core: pair same-mode tickets into lobbies, assign a session.
//! Pure domain logic, no transport. Mirrors server/local_matchmaking.py.

use std::collections::HashMap;

pub type TicketId = u64;
pub type LobbyId = u64;

#[derive(Clone)]
pub struct Ticket {
    pub id: TicketId,
    pub user: String,
    pub mode: String,
    pub lobby: Option<LobbyId>,
}

pub struct Lobby {
    pub id: LobbyId,
    pub seats: [TicketId; 2],
}

#[derive(Debug, PartialEq, Eq)]
pub enum MatchError {
    ModeConflict,
}

#[derive(Default)]
pub struct Matchmaking {
    tickets: HashMap<TicketId, Ticket>,
    lobbies: HashMap<LobbyId, Lobby>,
    next: u64,
}

impl Matchmaking {
    /// Queue a ticket for `user` in `mode`, pairing it if a seat is waiting.
    /// Idempotent per user; a different mode for an active user is rejected.
    pub fn create(&mut self, user: &str, mode: &str) -> Result<TicketId, MatchError> {
        if let Some(t) = self.tickets.values().find(|t| t.user == user) {
            return if t.mode == mode { Ok(t.id) } else { Err(MatchError::ModeConflict) };
        }
        self.next += 1;
        let id = self.next;
        self.tickets.insert(id, Ticket { id, user: user.into(), mode: mode.into(), lobby: None });
        self.pair(mode);
        Ok(id)
    }

    fn pair(&mut self, mode: &str) {
        let waiting: Vec<TicketId> = self
            .tickets
            .values()
            .filter(|t| t.mode == mode && t.lobby.is_none())
            .map(|t| t.id)
            .collect();
        for pair in waiting.chunks_exact(2) {
            self.next += 1;
            let id = self.next;
            self.lobbies.insert(id, Lobby { id, seats: [pair[0], pair[1]] });
            for &seat in pair {
                self.tickets.get_mut(&seat).unwrap().lobby = Some(id);
            }
        }
    }

    pub fn ticket(&self, id: TicketId) -> Option<&Ticket> {
        self.tickets.get(&id)
    }

    pub fn lobby(&self, id: LobbyId) -> Option<&Lobby> {
        self.lobbies.get(&id)
    }

    /// Cancel a ticket. If its lobby had not started, the partner is requeued.
    pub fn cancel(&mut self, id: TicketId) -> bool {
        let Some(ticket) = self.tickets.remove(&id) else { return false };
        if let Some(lobby_id) = ticket.lobby {
            let lobby = self.lobbies.remove(&lobby_id).unwrap();
            for other in lobby.seats {
                if let Some(t) = self.tickets.get_mut(&other) {
                    t.lobby = None;
                }
            }
            self.pair(&ticket.mode);
        }
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn two_users_same_mode_share_a_lobby() {
        let mut m = Matchmaking::default();
        let a = m.create("ana", "Normal24").unwrap();
        let b = m.create("bia", "Normal24").unwrap();
        let la = m.ticket(a).unwrap().lobby;
        assert!(la.is_some());
        assert_eq!(la, m.ticket(b).unwrap().lobby);
    }

    #[test]
    fn cancel_requeues_the_partner() {
        let mut m = Matchmaking::default();
        let a = m.create("ana", "Normal24").unwrap();
        let b = m.create("bia", "Normal24").unwrap();
        assert!(m.cancel(a));
        assert!(m.ticket(b).unwrap().lobby.is_none());
    }

    #[test]
    fn different_mode_for_active_user_is_rejected() {
        let mut m = Matchmaking::default();
        m.create("ana", "Normal24").unwrap();
        assert_eq!(m.create("ana", "Battle"), Err(MatchError::ModeConflict));
    }
}
