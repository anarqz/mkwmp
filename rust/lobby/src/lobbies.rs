//! Lobbies: any number per mode, created on demand and dropped when empty (docs/MULTI-LOBBY.md).
//!
//! A ticket is placed when its TrackMatchmakingTicket stream opens (so every seat of a
//! gathering lobby has a tracker to receive its assignment):
//! - into the joinable lobby of its mode with the most players (oldest first on a tie), or
//! - into a new GATHERING lobby when every lobby of the mode is full or past its join window.
//!
//! A gathering lobby is assigned (one match assignment to every seat) when it reaches its
//! capacity, or [`Policy::min_start`] players and [`Policy::gather_secs`] have passed since.
//! Each lobby owns its Gamesync documents, listeners and referee: game document paths carry
//! no session id, so the lobby is resolved from the caller (token -> user -> seat).

use std::collections::{BTreeMap, HashMap, HashSet};

use uuid::Uuid;

use crate::docs::{Store, Write};
use crate::referee::Referee;
use crate::{ConnId, StreamId};

pub type LobbyId = Uuid;
pub type TicketId = Uuid;
pub type StreamKey = (ConnId, StreamId);

/// Matchmaking rules. Defaults use only client behaviour seen working live.
#[derive(Clone, Debug)]
pub struct Policy {
    /// Players per lobby, by matchmaking config name (`Normal24`, `Battle24`, ...).
    pub capacity: HashMap<String, usize>,
    /// Capacity of configs not listed.
    pub default_capacity: usize,
    /// Players needed before a gathering lobby is assigned.
    pub min_start: usize,
    /// Seconds a gathering lobby keeps waiting for more players once `min_start` is reached.
    pub gather_secs: f64,
    /// Seconds after assignment during which an OPEN lobby still takes players (0 = never;
    /// late join before the vote is not verified yet, MULTI-LOBBY E1).
    pub join_window_secs: f64,
    /// Lobbies between events take players (MULTI-LOBBY E2, not verified yet).
    pub rejoin: bool,
    /// `__gs/f` `maxu`; `None` = the lobby capacity.
    pub maxu: Option<u32>,
    /// Seconds a seat may go without any KeepUserSession stream before it counts as left
    /// (covers a reconnect of the stream).
    pub leave_grace_secs: f64,
    /// Seconds an assigned seat may take to open its first KeepUserSession stream.
    pub connect_timeout_secs: f64,
    /// Friends may join a public (matchmade) lobby through its `SessionId` between events
    /// (MULTI-LOBBY E1/E2, not verified yet). Friend rooms always accept joins between events.
    pub friend_join_public: bool,
}

impl Default for Policy {
    fn default() -> Self {
        Self {
            capacity: HashMap::new(),
            default_capacity: 24,
            min_start: 2,
            gather_secs: 10.0,
            join_window_secs: 0.0,
            rejoin: false,
            maxu: None,
            leave_grace_secs: 5.0,
            connect_timeout_secs: 120.0,
            friend_join_public: false,
        }
    }
}

impl Policy {
    pub fn capacity(&self, mode: &str) -> usize {
        self.capacity.get(mode).copied().unwrap_or(self.default_capacity).max(1)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum State {
    /// Tickets waiting; nothing sent to the clients yet.
    Gathering,
    /// Assigned; members connecting or in free roam, no match yet.
    Open,
    /// At least one referee result open.
    InEvent,
    /// Between matches, members back in the room.
    Intermission,
}

impl State {
    pub fn as_str(self) -> &'static str {
        match self {
            State::Gathering => "gathering",
            State::Open => "open",
            State::InEvent => "in_event",
            State::Intermission => "intermission",
        }
    }
}

/// A matchmaking ticket (one console; `players` local players).
#[derive(Clone, Debug)]
pub struct Ticket {
    pub id: TicketId,
    /// User resource, `tenants/<t>/users/<id>`.
    pub user: String,
    pub mode: String,
    /// The client's serialized Player (ticket field 4), echoed in assignments.
    pub player: Vec<u8>,
    pub players: usize,
    pub lobby: Option<LobbyId>,
    pub trackers: HashSet<StreamKey>,
}

impl Ticket {
    pub fn uid(&self) -> &str {
        crate::users::tail(&self.user)
    }
}

/// A KeepUserSession listen.
#[derive(Clone, Debug, PartialEq)]
pub struct Watcher {
    pub stream: StreamKey,
    pub session: String,
    pub target_id: u64,
    /// Document name, or collection ending in "/".
    pub target: String,
}

/// One user in one lobby.
#[derive(Debug)]
pub struct Seat {
    pub ticket: TicketId,
    pub user: String,
    pub ussid: u32,
    pub players: usize,
    /// Order inside the lobby (P2P initiation order).
    pub join_seq: u64,
    /// Seat has received its match assignment.
    pub assigned_at: Option<f64>,
    pub streams: HashSet<StreamKey>,
    pub connected: bool,
    pub streamless_since: Option<f64>,
    /// Deferment name -> writes to run when the seat ends.
    pub deferments: BTreeMap<String, Vec<Write>>,
}

impl Seat {
    pub fn uid(&self) -> &str {
        crate::users::tail(&self.user)
    }
}

#[derive(Debug)]
pub struct Lobby {
    pub id: LobbyId,
    pub mode: String,
    pub state: State,
    pub capacity: usize,
    pub created: f64,
    pub assigned_at: Option<f64>,
    pub gather_since: Option<f64>,
    pub seats: Vec<Seat>,
    next_seq: u64,
    /// `__gs/f` `mcn` (Pia mesh creator): user id of the first seat.
    pub host: Option<String>,
    /// The original host left: stop taking players (MULTI-LOBBY E4 not verified).
    pub closed: bool,
    /// `__gs/f` `rs`.
    pub secret: [u8; 32],
    pub docs: Store,
    pub referee: Referee,
    pub watchers: Vec<Watcher>,
    /// Deferred `__pus` collection listens (answered once earlier members wrote theirs).
    pub pending: Vec<Watcher>,
    /// Friend room (created with CreateGameSessionCreationTicket, not matchmade).
    pub room: Option<Room>,
}

/// What a friend room adds to a lobby.
#[derive(Debug, Default)]
pub struct Room {
    /// Empty = no password.
    pub password: String,
    /// The creator's GameSession attributes (field 11, encoded), shown to joiners.
    pub attributes: Vec<u8>,
    /// GameSessionCreationTicket id.
    pub creation_ticket: TicketId,
    /// Room ID shown to players (GameSessionShortAlias).
    pub alias: Option<String>,
}

impl Lobby {
    fn new(mode: &str, capacity: usize, now: f64) -> Self {
        let id = Uuid::new_v4();
        Self {
            id,
            mode: mode.to_owned(),
            state: State::Gathering,
            capacity,
            created: now,
            assigned_at: None,
            gather_since: None,
            seats: Vec::new(),
            next_seq: 0,
            host: None,
            closed: false,
            secret: rand::random(),
            docs: Store::default(),
            referee: Referee::new(id.to_string()),
            watchers: Vec::new(),
            pending: Vec::new(),
            room: None,
        }
    }

    /// Whether a JoinGameSession (friend room by Room ID, or a friend's `SessionId`) may enter.
    pub fn accepts_join(&self, policy: &Policy, players: usize) -> bool {
        let between_events = matches!(self.state, State::Open | State::Intermission);
        let allowed = if self.room.is_some() { true } else { policy.friend_join_public };
        allowed && between_events && !self.closed && self.players() + players <= self.capacity
    }

    /// A private one-player space for a user who is not seated anywhere (documents written
    /// before or outside a match). Not part of matchmaking.
    pub fn solo(user: &str, ussid: u32, now: f64) -> Self {
        let mut lobby = Self::new("solo", 1, now);
        lobby.state = State::Open;
        lobby.assigned_at = Some(now);
        let ticket = Ticket {
            id: Uuid::nil(),
            user: user.to_owned(),
            mode: "solo".into(),
            player: Vec::new(),
            players: 1,
            lobby: Some(lobby.id),
            trackers: HashSet::new(),
        };
        lobby.add_seat(&ticket, ussid);
        lobby.seats[0].assigned_at = Some(now);
        lobby
    }

    pub fn players(&self) -> usize {
        self.seats.iter().map(|s| s.players).sum()
    }

    pub fn seat(&self, uid: &str) -> Option<&Seat> {
        self.seats.iter().find(|s| s.uid() == uid)
    }

    pub fn seat_mut(&mut self, uid: &str) -> Option<&mut Seat> {
        self.seats.iter_mut().find(|s| s.uid() == uid)
    }

    /// Members' ussids in join order.
    pub fn ussids(&self) -> Vec<u32> {
        self.seats.iter().map(|s| s.ussid).collect()
    }

    pub fn joinable(&self, policy: &Policy, players: usize, now: f64) -> bool {
        if self.closed || self.players() + players > self.capacity {
            return false;
        }
        match self.state {
            State::Gathering => true,
            State::Open => self.assigned_at.is_some_and(|t| now - t < policy.join_window_secs),
            State::Intermission => policy.rejoin,
            State::InEvent => false,
        }
    }

    pub fn maxu(&self, policy: &Policy) -> u32 {
        policy.maxu.unwrap_or(self.capacity as u32)
    }

    fn add_seat(&mut self, ticket: &Ticket, ussid: u32) {
        self.next_seq += 1;
        self.seats.push(Seat {
            ticket: ticket.id,
            user: ticket.user.clone(),
            ussid,
            players: ticket.players,
            join_seq: self.next_seq,
            assigned_at: None,
            streams: HashSet::new(),
            connected: false,
            streamless_since: None,
            deferments: BTreeMap::new(),
        });
        if self.host.is_none() {
            self.host = Some(ticket.uid().to_owned());
        }
    }

    /// Ready to assign its gathering seats.
    fn ready(&self, policy: &Policy, now: f64) -> bool {
        self.state == State::Gathering
            && (self.players() >= self.capacity
                || self.gather_since.is_some_and(|t| now - t >= policy.gather_secs))
    }

    /// Track the event state from the referee.
    pub fn refresh_state(&mut self) {
        if self.state == State::Gathering {
            return;
        }
        self.state = if self.referee.in_event() {
            State::InEvent
        } else if self.state == State::InEvent {
            State::Intermission
        } else {
            self.state
        };
    }
}

/// What the core must do after a matchmaking step.
#[derive(Debug, PartialEq, Eq)]
pub struct Assignment {
    pub lobby: LobbyId,
    /// Tickets that receive their assignment now.
    pub recipients: Vec<TicketId>,
    /// Joined an already-assigned lobby (existing members get pushes).
    pub late: bool,
}

/// All tickets and lobbies.
#[derive(Default)]
pub struct Matchmaker {
    pub policy: Policy,
    pub tickets: HashMap<TicketId, Ticket>,
    pub lobbies: HashMap<LobbyId, Lobby>,
}

impl Matchmaker {
    pub fn new(policy: Policy) -> Self {
        Self { policy, ..Default::default() }
    }

    /// The user's ticket, if any (one search per user).
    pub fn ticket_of(&self, uid: &str) -> Option<&Ticket> {
        self.tickets.values().find(|t| t.uid() == uid)
    }

    /// The lobby the user is seated in.
    pub fn lobby_of(&self, uid: &str) -> Option<LobbyId> {
        self.lobbies.values().find(|l| l.seat(uid).is_some()).map(|l| l.id)
    }

    pub fn create(&mut self, user: &str, mode: &str, player: Vec<u8>, players: usize) -> TicketId {
        let ticket = Ticket {
            id: Uuid::new_v4(),
            user: user.to_owned(),
            mode: mode.to_owned(),
            player,
            players: players.max(1),
            lobby: None,
            trackers: HashSet::new(),
        };
        let id = ticket.id;
        self.tickets.insert(id, ticket);
        id
    }

    /// A TrackMatchmakingTicket stream opened: place the ticket (first tracker) and return
    /// what can be assigned now.
    pub fn track(&mut self, ticket_id: TicketId, stream: StreamKey, ussid: u32, now: f64) -> Option<Assignment> {
        let ticket = self.tickets.get_mut(&ticket_id)?;
        ticket.trackers.insert(stream);
        if let Some(lobby_id) = ticket.lobby {
            // A reconnecting tracker of an assigned seat would wait forever; re-send.
            let lobby = self.lobbies.get(&lobby_id)?;
            return (lobby.state != State::Gathering)
                .then(|| Assignment { lobby: lobby_id, recipients: vec![ticket_id], late: false });
        }
        let ticket = ticket.clone();
        let lobby_id = self.place(&ticket, now);
        let lobby = self.lobbies.get_mut(&lobby_id).expect("placed");
        lobby.add_seat(&ticket, ussid);
        self.tickets.get_mut(&ticket_id).expect("exists").lobby = Some(lobby_id);
        let lobby = self.lobbies.get_mut(&lobby_id).expect("placed");
        if lobby.state == State::Gathering {
            if lobby.gather_since.is_none() && lobby.players() >= self.policy.min_start {
                lobby.gather_since = Some(now);
            }
            return self.assign_if_ready(lobby_id, now);
        }
        let seat = lobby.seat_mut(ticket.uid()).expect("seated");
        seat.assigned_at = Some(now);
        Some(Assignment { lobby: lobby_id, recipients: vec![ticket_id], late: true })
    }

    fn place(&mut self, ticket: &Ticket, now: f64) -> LobbyId {
        let best = self
            .lobbies
            .values()
            .filter(|l| l.mode == ticket.mode && l.joinable(&self.policy, ticket.players, now))
            .max_by(|a, b| a.players().cmp(&b.players()).then(b.created.total_cmp(&a.created)));
        if let Some(l) = best {
            return l.id;
        }
        let lobby = Lobby::new(&ticket.mode, self.policy.capacity(&ticket.mode), now);
        let id = lobby.id;
        tracing::info!(lobby = %id, mode = %ticket.mode, "lobby created");
        self.lobbies.insert(id, lobby);
        id
    }

    fn assign_if_ready(&mut self, lobby_id: LobbyId, now: f64) -> Option<Assignment> {
        let policy = &self.policy;
        let lobby = self.lobbies.get_mut(&lobby_id)?;
        if !lobby.ready(policy, now) {
            return None;
        }
        lobby.state = State::Open;
        lobby.assigned_at = Some(now);
        for seat in &mut lobby.seats {
            seat.assigned_at = Some(now);
        }
        tracing::info!(lobby = %lobby_id, mode = %lobby.mode, players = lobby.players(), "lobby assigned");
        Some(Assignment { lobby: lobby_id, recipients: lobby.seats.iter().map(|s| s.ticket).collect(), late: false })
    }

    /// A friend room with its creator seated (no gathering: assigned at once).
    pub fn create_room(&mut self, user: &str, mode: &str, player: Vec<u8>, players: usize, room: Room, ussid: u32, now: f64) -> (LobbyId, TicketId) {
        let mut lobby = Lobby::new(mode, self.policy.capacity(mode), now);
        lobby.state = State::Open;
        lobby.assigned_at = Some(now);
        lobby.room = Some(room);
        let id = lobby.id;
        let ticket = self.create(user, mode, player, players);
        let t = self.tickets.get_mut(&ticket).expect("created");
        t.lobby = Some(id);
        let t = t.clone();
        lobby.add_seat(&t, ussid);
        lobby.seats[0].assigned_at = Some(now);
        tracing::info!(lobby = %id, mode, "room created");
        self.lobbies.insert(id, lobby);
        (id, ticket)
    }

    /// Seat a user in an existing lobby (JoinGameSession). The caller checked `accepts_join`.
    pub fn join(&mut self, lobby_id: LobbyId, user: &str, player: Vec<u8>, players: usize, ussid: u32, now: f64) -> Option<TicketId> {
        let lobby = self.lobbies.get(&lobby_id)?;
        let mode = lobby.mode.clone();
        let ticket = self.create(user, &mode, player, players);
        let t = self.tickets.get_mut(&ticket).expect("created");
        t.lobby = Some(lobby_id);
        let t = t.clone();
        let lobby = self.lobbies.get_mut(&lobby_id).expect("exists");
        lobby.add_seat(&t, ussid);
        lobby.seat_mut(t.uid()).expect("seated").assigned_at = Some(now);
        Some(ticket)
    }

    /// The room with this creation ticket.
    pub fn room_by_ticket(&self, ticket: TicketId) -> Option<&Lobby> {
        self.lobbies.values().find(|l| l.room.as_ref().is_some_and(|r| r.creation_ticket == ticket))
    }

    /// The room with this Room ID.
    pub fn room_by_alias(&self, alias: &str) -> Option<&Lobby> {
        self.lobbies.values().find(|l| l.room.as_ref().and_then(|r| r.alias.as_deref()) == Some(alias))
    }

    /// Gathering lobbies whose wait is over.
    pub fn tick(&mut self, now: f64) -> Vec<Assignment> {
        let ids: Vec<LobbyId> = self.lobbies.values().filter(|l| l.state == State::Gathering).map(|l| l.id).collect();
        ids.into_iter().filter_map(|id| self.assign_if_ready(id, now)).collect()
    }

    /// A tracker stream ended. Returns true when that was the last tracker of a ticket that
    /// was never assigned (the search is over: the ticket is cancelled).
    pub fn untrack(&mut self, stream: StreamKey) -> Vec<TicketId> {
        let mut dropped = Vec::new();
        for t in self.tickets.values_mut() {
            if t.trackers.remove(&stream) && t.trackers.is_empty() {
                let assigned = t.lobby.and_then(|l| self.lobbies.get(&l)).is_some_and(|l| l.state != State::Gathering);
                if !assigned {
                    dropped.push(t.id);
                }
            }
        }
        for id in &dropped {
            self.cancel(*id);
        }
        dropped
    }

    /// Cancel a ticket. A seat in a gathering lobby is removed; an assigned seat is left to
    /// the core's leave handling (returned as Some(lobby, uid)).
    pub fn cancel(&mut self, ticket_id: TicketId) -> Option<(LobbyId, String)> {
        let ticket = self.tickets.remove(&ticket_id)?;
        let lobby_id = ticket.lobby?;
        let lobby = self.lobbies.get_mut(&lobby_id)?;
        if lobby.state == State::Gathering {
            lobby.seats.retain(|s| s.ticket != ticket_id);
            if lobby.players() < self.policy.min_start {
                lobby.gather_since = None;
            }
            if lobby.host.as_deref() == Some(ticket.uid()) {
                lobby.host = lobby.seats.first().map(|s| s.uid().to_owned());
            }
            if lobby.seats.is_empty() {
                self.lobbies.remove(&lobby_id);
                tracing::info!(lobby = %lobby_id, "lobby dropped (empty)");
            }
            return None;
        }
        Some((lobby_id, ticket.uid().to_owned()))
    }

    /// Remove a seat (the core has done the leave pushes). Drops the lobby when empty.
    pub fn remove_seat(&mut self, lobby_id: LobbyId, uid: &str) -> Option<Seat> {
        let lobby = self.lobbies.get_mut(&lobby_id)?;
        let pos = lobby.seats.iter().position(|s| s.uid() == uid)?;
        let seat = lobby.seats.remove(pos);
        self.tickets.remove(&seat.ticket);
        if lobby.seats.is_empty() {
            self.lobbies.remove(&lobby_id);
            tracing::info!(lobby = %lobby_id, "lobby dropped (empty)");
        }
        Some(seat)
    }

    /// Per-mode counts for the seat roster / status.
    pub fn counts(&self) -> BTreeMap<String, ModeCounts> {
        let mut out: BTreeMap<String, ModeCounts> = BTreeMap::new();
        for l in self.lobbies.values() {
            let c = out.entry(l.mode.clone()).or_default();
            c.lobbies += 1;
            c.players += l.players();
            match l.state {
                State::Gathering => c.gathering += 1,
                State::Open => c.open += 1,
                State::InEvent => c.in_event += 1,
                State::Intermission => c.intermission += 1,
            }
        }
        out
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ModeCounts {
    pub lobbies: usize,
    pub players: usize,
    pub gathering: usize,
    pub open: usize,
    pub in_event: usize,
    pub intermission: usize,
}

#[cfg(test)]
mod tests {
    use super::*;

    fn mm() -> Matchmaker {
        let mut p = Policy::default();
        p.capacity.insert("Battle24".into(), 3);
        Matchmaker::new(p)
    }

    fn join(m: &mut Matchmaker, name: &str, mode: &str, n: u64, now: f64) -> (TicketId, Option<Assignment>) {
        let t = m.create(&format!("tenants/t/users/{}", crate::users::ticket_user_id(name)), mode, vec![], 1);
        let a = m.track(t, (n, 1), n as u32, now);
        (t, a)
    }

    #[test]
    fn gathers_then_assigns_everyone_together() {
        let mut m = mm();
        let (a, none) = join(&mut m, "a", "Normal24", 1, 0.0);
        assert!(none.is_none());
        let (b, none) = join(&mut m, "b", "Normal24", 2, 1.0);
        assert!(none.is_none());
        assert!(m.tick(5.0).is_empty());
        let got = m.tick(11.0);
        assert_eq!(got.len(), 1);
        assert_eq!(got[0].recipients, vec![a, b]);
        assert_eq!(m.lobbies[&got[0].lobby].state, State::Open);
    }

    #[test]
    fn full_lobby_assigns_at_once_and_the_next_player_opens_a_new_one() {
        let mut m = mm();
        join(&mut m, "a", "Battle24", 1, 0.0);
        join(&mut m, "b", "Battle24", 2, 0.0);
        let (_, a) = join(&mut m, "c", "Battle24", 3, 0.0);
        let first = a.expect("capacity reached").lobby;
        let (_, none) = join(&mut m, "d", "Battle24", 4, 0.0);
        assert!(none.is_none());
        assert_eq!(m.lobbies.len(), 2);
        assert_ne!(m.lobby_of(&crate::users::ticket_user_id("d")), Some(first));
        let c = m.counts()["Battle24"];
        assert_eq!((c.lobbies, c.players, c.open, c.gathering), (2, 4, 1, 1));
    }

    #[test]
    fn modes_never_mix() {
        let mut m = mm();
        join(&mut m, "a", "Normal24", 1, 0.0);
        join(&mut m, "b", "Battle24", 2, 0.0);
        assert_eq!(m.lobbies.len(), 2);
        assert!(m.tick(100.0).is_empty()); // one player each: nobody starts
    }

    #[test]
    fn fills_the_fullest_joinable_lobby() {
        let mut m = mm();
        m.policy.rejoin = true;
        join(&mut m, "a", "Battle24", 1, 0.0);
        join(&mut m, "b", "Battle24", 2, 0.0);
        let first = m.tick(20.0)[0].lobby;
        m.lobbies.get_mut(&first).unwrap().state = State::Intermission;
        let (_, a) = join(&mut m, "c", "Battle24", 3, 21.0);
        let a = a.expect("late join");
        assert!(a.late);
        assert_eq!(a.lobby, first);
    }

    #[test]
    fn a_lost_tracker_cancels_a_waiting_ticket_and_empty_lobbies_drop() {
        let mut m = mm();
        let (t, _) = join(&mut m, "a", "Normal24", 1, 0.0);
        assert_eq!(m.untrack((1, 1)), vec![t]);
        assert!(m.tickets.is_empty());
        assert!(m.lobbies.is_empty());
    }

    #[test]
    fn assigned_seats_leave_through_the_core() {
        let mut m = mm();
        let (a, _) = join(&mut m, "a", "Normal24", 1, 0.0);
        join(&mut m, "b", "Normal24", 2, 0.0);
        let lobby = m.tick(20.0)[0].lobby;
        assert!(m.untrack((1, 1)).is_empty()); // assignment ends the tracker: not a cancel
        let uid = crate::users::ticket_user_id("a");
        assert_eq!(m.cancel(a), Some((lobby, uid.clone())));
        m.remove_seat(lobby, &uid);
        m.remove_seat(lobby, &crate::users::ticket_user_id("b"));
        assert!(m.lobbies.is_empty());
    }
}
