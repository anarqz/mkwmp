//! The seat roster: every live session, N seats, a FIFO queue, and the
//! session-only friend graph. All in memory; nothing survives a restart.
//!
//! Time is passed in (unix milliseconds) so the rules are testable without
//! sleeping. Liveness: a session not heard from within its timeout is dropped
//! by [`Roster::sweep`] and the queue is promoted.

use std::collections::{BTreeMap, HashMap, VecDeque};

use rand::RngCore;
use serde::{Deserialize, Serialize};
use ticket::{Claims, Issuer};

use crate::names::{self, NameError};

#[derive(Debug, Clone)]
pub struct Rules {
    pub capacity: usize,
    pub max_queue: usize,
    pub seat_timeout_ms: u64,
    pub queue_timeout_ms: u64,
    pub heartbeat_ms: u64,
    pub queue_poll_ms: u64,
    pub ticket_ttl_secs: u64,
    pub server_name: String,
    /// NPLN endpoint (`host:port`) of the lobby the seated game connects to.
    pub lobby_endpoint: String,
    /// The lobby's TLS certificate(s), PEM, for the game patch to trust
    /// (a self-hosted lobby has its own; see [`lobby_ca_from_pem`]).
    pub lobby_ca: Option<String>,
    /// UDP port of the lobby's voice relay (0 = voice off).
    pub voice_port: u16,
}

impl Default for Rules {
    fn default() -> Self {
        Self {
            capacity: 50,
            max_queue: 500,
            seat_timeout_ms: 60_000,
            queue_timeout_ms: 30_000,
            heartbeat_ms: 20_000,
            queue_poll_ms: 5_000,
            ticket_ttl_secs: 12 * 3600,
            server_name: "mkwmp".into(),
            lobby_endpoint: "127.0.0.1:8905".into(),
            lobby_ca: None,
            voice_port: 8907,
        }
    }
}

/// Largest `lobbyCa` the status carries (the patch reads at most 16 KiB).
pub const MAX_LOBBY_CA: usize = 16 * 1024;

/// Only the `CERTIFICATE` blocks of a PEM file: whatever else the file
/// holds (a private key by mistake, comments) never leaves the server.
pub fn lobby_ca_from_pem(pem: &str) -> Option<String> {
    let mut out = String::new();
    let mut rest = pem;
    while let Some(start) = rest.find("-----BEGIN CERTIFICATE-----") {
        let block = &rest[start..];
        let end = block.find("-----END CERTIFICATE-----")? + "-----END CERTIFICATE-----".len();
        out.push_str(&block[..end]);
        out.push('\n');
        rest = &block[end..];
    }
    (!out.is_empty() && out.len() <= MAX_LOBBY_CA).then_some(out)
}

#[derive(Debug, PartialEq, Eq)]
pub enum RosterError {
    Name(NameError),
    NameTaken,
    QueueFull,
    NotFound,
}

impl std::fmt::Display for RosterError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            RosterError::Name(e) => e.fmt(f),
            RosterError::NameTaken => f.write_str("that gamertag is in use on this server"),
            RosterError::QueueFull => f.write_str("the queue is full, try again later"),
            RosterError::NotFound => f.write_str("session not found or expired"),
        }
    }
}

impl From<NameError> for RosterError {
    fn from(e: NameError) -> Self {
        RosterError::Name(e)
    }
}

struct Session {
    key: String,
    name: String,
    seat: Option<Seat>,
    last_seen: u64,
    /// The lobby room this seat is in, as the lobby last reported it.
    room: Option<RoomView>,
}

/// A seat's room as the lobby knows it (sent with the roster call). Shown to that seat's own
/// launcher only (`room` in [`View`]), for its crash records: which lobby, its ussid, everyone's.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RoomView {
    pub lobby: String,
    pub ussid: u32,
    /// Every member's ussid, ascending: the first is the game's mesh host.
    pub ussids: Vec<u32>,
    /// When the seat server got it (ms since 1970).
    #[serde(default)]
    pub at: u64,
}

/// A room older than this (the lobby stopped reporting) is no longer shown.
pub const ROOM_STALE_MS: u64 = 60_000;
/// Members a reported room may list.
const MAX_ROOM_USSIDS: usize = 64;

struct Seat {
    ticket: String,
    expires_at: u64,
}

/// What the launcher sees on create, poll and heartbeat.
#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct View {
    /// Random per process start: a change means the seat server restarted and
    /// this session is gone; rejoin for a fresh ticket.
    pub boot_id: String,
    pub session_id: String,
    pub name: String,
    pub state: &'static str, // "queued" | "seated"
    #[serde(skip_serializing_if = "Option::is_none")]
    pub position: Option<usize>,
    pub seats_total: usize,
    pub seats_free: usize,
    pub queued: usize,
    /// Poll/heartbeat again within this many ms.
    pub next_poll_ms: u64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ticket: Option<TicketView>,
    /// The seat's lobby room (`null` when in none, or the lobby hasn't said).
    pub room: Option<RoomView>,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct TicketView {
    pub token: String,
    pub expires_at: u64,
    pub lobby: String,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RosterEntry {
    pub session_id: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    pub alive: bool,
    pub friends: Vec<RosterFriend>,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RosterFriend {
    /// The friend's seated session; absent when not seated.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub session_id: Option<String>,
    pub name: String,
    pub seated: bool,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Status {
    pub boot_id: String,
    pub name: String,
    /// NPLN `host:port` of this server's lobby.
    pub lobby_endpoint: String,
    /// The lobby's certificate(s), PEM: the launcher hands them to the game
    /// patch, which trusts them for this server's lobby.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub lobby_ca: Option<String>,
    /// UDP port of the lobby's voice relay, on the lobby host (0 = voice off).
    pub voice_port: u16,
    pub seats_total: usize,
    pub seated: usize,
    pub queued: usize,
    /// Live lobbies per matchmaking mode, as the lobby last reported them
    /// (empty when it hasn't, or stopped reporting). Lobbies have no cap:
    /// they open and close with the players.
    pub lobbies: BTreeMap<String, usize>,
    /// What a report can be about (VIDEO_API.md §4). Absent on servers older than chat rooms.
    pub report_kinds: &'static [&'static str],
}

/// A lobby report older than this no longer shows in the status.
const LOBBIES_STALE_MS: u64 = 60_000;
/// At most this many modes per report; longer names are dropped.
const MAX_LOBBY_MODES: usize = 16;
const MAX_MODE_LEN: usize = 32;

pub struct Roster {
    boot_id: String,
    rules: Rules,
    issuer: Issuer,
    sessions: HashMap<String, Session>,
    by_name: HashMap<String, String>, // name key -> sid
    queue: VecDeque<String>,
    seated: usize,
    changes: u64,
    lobbies: BTreeMap<String, usize>,
    lobbies_at: u64,
    /// The latest `now` seen (rooms go stale against it).
    now: u64,
}

pub struct Created {
    pub session_key: String,
    pub view: View,
}

/// How far past its poll interval a session must be silent before a
/// correct-PIN join of the same name takes it over.
pub const STALE_GRACE_MS: u64 = 5_000;

fn random_hex(bytes: usize) -> String {
    let mut b = vec![0u8; bytes];
    rand::rngs::OsRng.fill_bytes(&mut b);
    b.iter().map(|x| format!("{x:02x}")).collect()
}

impl Roster {
    pub fn new(rules: Rules, issuer: Issuer) -> Self {
        Self {
            boot_id: random_hex(8),
            rules,
            issuer,
            sessions: HashMap::new(),
            by_name: HashMap::new(),
            queue: VecDeque::new(),
            seated: 0,
            changes: 0,
            lobbies: BTreeMap::new(),
            lobbies_at: 0,
            now: 0,
        }
    }

    pub fn boot_id(&self) -> &str {
        &self.boot_id
    }

    pub fn issuer(&self) -> &Issuer {
        &self.issuer
    }

    pub fn rules(&self) -> &Rules {
        &self.rules
    }

    /// A player asks for a seat (their PIN already checked). Seated at once if
    /// one is free, else queued. The name's own session, silent past its poll
    /// interval (+ [`STALE_GRACE_MS`]: a launcher that crashed or was killed),
    /// is taken over: the new session keeps its seat or place in line. A live
    /// one (another launcher heartbeating) is `NameTaken`.
    pub fn join(&mut self, name: &str, now: u64) -> Result<Created, RosterError> {
        self.sweep(now);
        names::validate(name)?;
        if let Some(old) = self.by_name.get(&names::key(name)).cloned() {
            let s = &self.sessions[&old];
            let poll = if s.seat.is_some() { self.rules.heartbeat_ms } else { self.rules.queue_poll_ms };
            if now.saturating_sub(s.last_seen) <= poll + STALE_GRACE_MS {
                return Err(RosterError::NameTaken);
            }
            return Ok(self.take_over(&old, name, now));
        }
        if self.queue.len() >= self.rules.max_queue && self.seated >= self.rules.capacity {
            return Err(RosterError::QueueFull);
        }
        let sid = random_hex(16);
        let key = random_hex(32);
        self.sessions.insert(
            sid.clone(),
            Session { key: key.clone(), name: name.to_string(), seat: None, last_seen: now, room: None },
        );
        self.by_name.insert(names::key(name), sid.clone());
        self.queue.push_back(sid.clone());
        self.changes += 1;
        self.promote(now);
        Ok(Created { session_key: key, view: self.view(&sid) })
    }

    /// Replace the stale session `old` with a new one (new id and key) in the
    /// same seat or place in line.
    fn take_over(&mut self, old: &str, name: &str, now: u64) -> Created {
        let prev = self.sessions.remove(old).expect("by_name points at a session");
        let sid = random_hex(16);
        let key = random_hex(32);
        let seated = prev.seat.is_some();
        self.sessions.insert(sid.clone(), Session { key: key.clone(), name: name.to_string(), seat: None, last_seen: now, room: None });
        self.by_name.insert(names::key(name), sid.clone());
        if seated {
            let seat = self.sign(&sid, now); // the ticket names the new session
            self.sessions.get_mut(&sid).expect("just inserted").seat = Some(seat);
        } else if let Some(q) = self.queue.iter_mut().find(|q| *q == old) {
            *q = sid.clone();
        }
        self.changes += 1;
        Created { session_key: key, view: self.view(&sid) }
    }

    fn session_mut(&mut self, sid: &str, key: &str) -> Result<&mut Session, RosterError> {
        match self.sessions.get_mut(sid) {
            Some(s) if s.key == key => Ok(s),
            _ => Err(RosterError::NotFound),
        }
    }

    /// Poll (queued) or heartbeat (seated): keeps the session alive.
    pub fn touch(&mut self, sid: &str, key: &str, now: u64) -> Result<View, RosterError> {
        self.sweep(now);
        self.session_mut(sid, key)?.last_seen = now;
        self.renew_ticket(sid, now);
        Ok(self.view(sid))
    }

    /// Past half its lifetime, a seated session's ticket is re-signed so a
    /// long session never presents an expired one.
    fn renew_ticket(&mut self, sid: &str, now: u64) {
        let half = self.rules.ticket_ttl_secs / 2;
        let due = self.sessions[sid]
            .seat
            .as_ref()
            .is_some_and(|seat| seat.expires_at.saturating_sub(now / 1000) < half);
        if due {
            let seat = self.sign(sid, now);
            self.sessions.get_mut(sid).expect("touched session exists").seat = Some(seat);
        }
    }

    fn sign(&self, sid: &str, now: u64) -> Seat {
        let s = &self.sessions[sid];
        let iat = now / 1000;
        let exp = iat + self.rules.ticket_ttl_secs;
        let ticket = self.issuer.sign(Claims {
            v: 1,
            iss: self.rules.server_name.clone(),
            kid: String::new(),
            sid: sid.to_string(),
            name: s.name.clone(),
            iat,
            exp,
        });
        Seat { ticket, expires_at: exp }
    }

    /// Leave: frees the seat or the place in line. Idempotent.
    pub fn leave(&mut self, sid: &str, key: &str, now: u64) {
        if self.session_mut(sid, key).is_ok() {
            self.remove(sid);
            self.promote(now);
        }
    }

    /// End the live session using `name`, if any (its profile was deleted).
    pub fn drop_name(&mut self, name: &str, now: u64) {
        if let Some(sid) = self.by_name.get(&names::key(name)).cloned() {
            self.remove(&sid);
            self.promote(now);
        }
    }

    /// Drop sessions not heard from in time, then fill free seats.
    pub fn sweep(&mut self, now: u64) {
        self.now = self.now.max(now);
        let (seat_to, queue_to) = (self.rules.seat_timeout_ms, self.rules.queue_timeout_ms);
        let dead: Vec<String> = self
            .sessions
            .iter()
            .filter(|(_, s)| {
                let to = if s.seat.is_some() { seat_to } else { queue_to };
                now.saturating_sub(s.last_seen) > to
            })
            .map(|(sid, _)| sid.clone())
            .collect();
        for sid in &dead {
            self.remove(sid);
        }
        self.promote(now);
    }

    fn remove(&mut self, sid: &str) {
        if let Some(s) = self.sessions.remove(sid) {
            self.changes += 1;
            self.by_name.remove(&names::key(&s.name));
            if s.seat.is_some() {
                self.seated -= 1;
            } else {
                self.queue.retain(|q| q != sid);
            }
        }
    }

    fn promote(&mut self, now: u64) {
        while self.seated < self.rules.capacity {
            let Some(sid) = self.queue.pop_front() else { break };
            let seat = self.sign(&sid, now);
            let s = self.sessions.get_mut(&sid).expect("queued session exists");
            s.seat = Some(seat);
            // A newly seated player gets a full heartbeat window.
            s.last_seen = now;
            self.seated += 1;
            self.changes += 1;
        }
    }

    /// The seated session named `name` (case-insensitive), if any.
    fn seated_by_name(&self, name: &str) -> Option<(&String, &Session)> {
        let sid = self.by_name.get(&names::key(name))?;
        let s = self.sessions.get(sid)?;
        s.seat.as_ref().map(|_| (sid, s))
    }

    /// "seated" or "queued" for the live session named `name`.
    pub fn state_of(&self, name: &str) -> Option<&'static str> {
        let s = self.sessions.get(self.by_name.get(&names::key(name))?)?;
        Some(if s.seat.is_some() { "seated" } else { "queued" })
    }

    /// The seated session id and spelling for `name`, if seated.
    pub fn seated(&self, name: &str) -> Option<(String, String)> {
        self.seated_by_name(name).map(|(sid, s)| (sid.clone(), s.name.clone()))
    }

    /// Bumped whenever a session comes, goes or gets its seat (friends'
    /// launchers long-poll on it).
    pub fn changes(&self) -> u64 {
        self.changes
    }

    fn view(&self, sid: &str) -> View {
        let s = &self.sessions[sid];
        let position = if s.seat.is_none() {
            self.queue.iter().position(|q| q == sid).map(|p| p + 1)
        } else {
            None
        };
        View {
            boot_id: self.boot_id.clone(),
            session_id: sid.to_string(),
            name: s.name.clone(),
            state: if s.seat.is_some() { "seated" } else { "queued" },
            position,
            seats_total: self.rules.capacity,
            seats_free: self.rules.capacity - self.seated,
            queued: self.queue.len(),
            next_poll_ms: if s.seat.is_some() { self.rules.heartbeat_ms } else { self.rules.queue_poll_ms },
            ticket: s.seat.as_ref().map(|seat| TicketView {
                token: seat.ticket.clone(),
                expires_at: seat.expires_at,
                lobby: self.rules.lobby_endpoint.clone(),
            }),
            room: s.room.clone().filter(|r| self.now.saturating_sub(r.at) < ROOM_STALE_MS),
        }
    }

    /// The lobby's rooms for the seats of its roster call: each of `sids` gets its entry in
    /// `rooms`, or none (not in a room). Malformed entries count as none.
    pub fn report_rooms(&mut self, sids: &[String], rooms: &HashMap<String, RoomView>, now: u64) {
        for sid in sids {
            if let Some(s) = self.sessions.get_mut(sid) {
                s.room = rooms
                    .get(sid)
                    .filter(|r| !r.lobby.is_empty() && r.lobby.len() <= 64 && r.ussids.len() <= MAX_ROOM_USSIDS)
                    .map(|r| {
                        let mut ussids = r.ussids.clone();
                        ussids.sort_unstable();
                        ussids.dedup();
                        RoomView { lobby: r.lobby.clone(), ussid: r.ussid, ussids, at: now }
                    });
            }
        }
    }

    /// The lobby's view of its players: alive = still seated. `friends` is
    /// filled by the caller (from the registry).
    pub fn roster(&mut self, sids: &[String], now: u64) -> Vec<RosterEntry> {
        self.sweep(now);
        sids.iter()
            .map(|sid| match self.sessions.get(sid).filter(|s| s.seat.is_some()) {
                Some(s) => RosterEntry {
                    session_id: sid.clone(),
                    name: Some(s.name.clone()),
                    alive: true,
                    friends: vec![],
                },
                None => RosterEntry { session_id: sid.clone(), name: None, alive: false, friends: vec![] },
            })
            .collect()
    }

    pub fn status(&mut self, now: u64) -> Status {
        self.sweep(now);
        Status {
            boot_id: self.boot_id.clone(),
            name: self.rules.server_name.clone(),
            lobby_endpoint: self.rules.lobby_endpoint.clone(),
            lobby_ca: self.rules.lobby_ca.clone(),
            voice_port: self.rules.voice_port,
            seats_total: self.rules.capacity,
            seated: self.seated,
            queued: self.queue.len(),
            lobbies: if now.saturating_sub(self.lobbies_at) < LOBBIES_STALE_MS { self.lobbies.clone() } else { BTreeMap::new() },
            report_kinds: &crate::registry::REPORT_KINDS,
        }
    }

    /// The lobby's count of live lobbies per mode (sent with its roster).
    pub fn report_lobbies(&mut self, lobbies: &BTreeMap<String, usize>, now: u64) {
        self.lobbies = lobbies
            .iter()
            .filter(|(m, _)| !m.is_empty() && m.len() <= MAX_MODE_LEN && m.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_' || b == b'-'))
            .take(MAX_LOBBY_MODES)
            .map(|(m, n)| (m.clone(), *n))
            .collect();
        self.lobbies_at = now;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ed25519_dalek::SigningKey;
    use ticket::TicketVerifier;

    fn roster(capacity: usize) -> Roster {
        let rules = Rules { capacity, max_queue: 3, ..Rules::default() };
        Roster::new(rules, Issuer::new("t", SigningKey::from_bytes(&[1; 32])))
    }

    #[test]
    fn seats_then_queues_then_promotes_on_leave() {
        let mut r = roster(1);
        let a = r.join("ana", 0).unwrap();
        assert_eq!(a.view.state, "seated");
        assert!(a.view.ticket.is_some());
        let b = r.join("bia", 0).unwrap();
        assert_eq!((b.view.state, b.view.position), ("queued", Some(1)));
        assert!(b.view.ticket.is_none());
        let c = r.join("cid", 0).unwrap();
        assert_eq!(c.view.position, Some(2));

        r.leave(&a.view.session_id, &a.session_key, 1);
        let b2 = r.touch(&b.view.session_id, &b.session_key, 2).unwrap();
        assert_eq!(b2.state, "seated");
        assert_eq!(r.touch(&c.view.session_id, &c.session_key, 2).unwrap().position, Some(1));
    }

    #[test]
    fn ticket_verifies_and_names_the_session() {
        let mut r = roster(1);
        let a = r.join("Ana", 1_700_000_000_000).unwrap();
        let t = a.view.ticket.unwrap();
        let v = TicketVerifier::from_entries(&[format!("t:{}", r.issuer.public_key_b64())]).unwrap();
        let c = v.verify(&t.token, 1_700_000_001).unwrap();
        assert_eq!((c.sid.as_str(), c.name.as_str()), (a.view.session_id.as_str(), "Ana"));
        assert_eq!(c.exp, 1_700_000_000 + 12 * 3600);
    }

    #[test]
    fn names_unique_case_insensitive_and_freed_on_leave() {
        let mut r = roster(5);
        let a = r.join("Ana", 0).unwrap();
        assert_eq!(r.join("ANA", 0).err(), Some(RosterError::NameTaken));
        r.leave(&a.view.session_id, &a.session_key, 0);
        assert!(r.join("ana", 0).is_ok());
    }

    #[test]
    fn a_stale_session_of_the_name_is_taken_over() {
        let mut r = roster(1);
        let a = r.join("Ana", 0).unwrap();
        let q = r.join("Bia", 0).unwrap();
        assert_eq!(q.view.state, "queued");
        // Ana's launcher heartbeats on time: a second launcher is refused.
        let hb = Rules::default().heartbeat_ms;
        assert_eq!(r.join("ana", hb + STALE_GRACE_MS).err(), Some(RosterError::NameTaken));
        // Silent past heartbeat + grace (killed): the new join keeps the seat, ahead of Bia.
        let b = r.join("ana", hb + STALE_GRACE_MS + 1).unwrap();
        assert_eq!(b.view.state, "seated");
        assert_ne!(b.view.session_id, a.view.session_id);
        assert!(r.touch(&a.view.session_id, &a.session_key, hb + STALE_GRACE_MS + 2).is_err(), "the old session is gone");
        assert_eq!(r.state_of("Bia"), Some("queued"));
        let (sid, _) = r.seated("Ana").unwrap();
        assert_eq!(sid, b.view.session_id);
        // Bia (queued) is replaced in her place in line.
        r.touch(&b.view.session_id, &b.session_key, 30_000).unwrap();
        let qp = Rules::default().queue_poll_ms;
        let q2 = r.join("Bia", 30_000.max(qp + STALE_GRACE_MS + 1)).unwrap();
        assert_eq!((q2.view.state, q2.view.position), ("queued", Some(1)));
    }

    #[test]
    fn invalid_names_rejected() {
        let mut r = roster(5);
        assert!(matches!(r.join("a b", 0), Err(RosterError::Name(_))));
        assert!(matches!(r.join("x", 0), Err(RosterError::Name(_))));
    }

    #[test]
    fn silent_seat_expires_and_queue_promotes() {
        let mut r = roster(1);
        let a = r.join("ana", 0).unwrap();
        let b = r.join("bia", 0).unwrap();
        // bia keeps polling, ana stops heartbeating.
        for t in (5_000..=60_000).step_by(5_000) {
            r.touch(&b.view.session_id, &b.session_key, t).unwrap();
        }
        assert_eq!(r.touch(&b.view.session_id, &b.session_key, 60_001).unwrap().state, "seated");
        assert_eq!(r.touch(&a.view.session_id, &a.session_key, 60_001), Err(RosterError::NotFound));
    }

    #[test]
    fn silent_queued_player_drops_out() {
        let mut r = roster(1);
        let a = r.join("ana", 0).unwrap();
        let b = r.join("bia", 0).unwrap();
        r.touch(&a.view.session_id, &a.session_key, 30_001).unwrap();
        assert_eq!(r.touch(&b.view.session_id, &b.session_key, 30_002), Err(RosterError::NotFound));
    }

    #[test]
    fn long_session_gets_a_fresh_ticket() {
        let rules = Rules { capacity: 1, seat_timeout_ms: u64::MAX / 2, ..Rules::default() };
        let mut r = Roster::new(rules, Issuer::new("t", SigningKey::from_bytes(&[1; 32])));
        let a = r.join("ana", 0).unwrap();
        let t0 = a.view.ticket.unwrap().token;
        let early = r.touch(&a.view.session_id, &a.session_key, 3_600_000).unwrap();
        assert_eq!(early.ticket.unwrap().token, t0);
        let late = r.touch(&a.view.session_id, &a.session_key, 6 * 3_600_000 + 1_000).unwrap();
        let t1 = late.ticket.unwrap();
        assert_ne!(t1.token, t0);
        assert_eq!(t1.expires_at, 6 * 3600 + 1 + 12 * 3600);
    }

    #[test]
    fn queue_cap() {
        let mut r = roster(1);
        r.join("aaa", 0).unwrap();
        for n in ["bbb", "ccc", "ddd"] {
            r.join(n, 0).unwrap();
        }
        assert_eq!(r.join("eee", 0).err(), Some(RosterError::QueueFull));
    }

    #[test]
    fn wrong_key_is_not_found() {
        let mut r = roster(1);
        let a = r.join("ana", 0).unwrap();
        assert_eq!(r.touch(&a.view.session_id, "nope", 0), Err(RosterError::NotFound));
        r.leave(&a.view.session_id, "nope", 0);
        assert!(r.touch(&a.view.session_id, &a.session_key, 0).is_ok());
    }

    #[test]
    fn roster_reports_gone_and_queued_as_not_alive() {
        let mut r = roster(1);
        let _a = r.join("ana", 0).unwrap();
        let b = r.join("bia", 0).unwrap();
        let out = r.roster(&[b.view.session_id.clone(), "unknown".into()], 0);
        assert!(out.iter().all(|e| !e.alive && e.name.is_none()));
    }

    #[test]
    fn lobby_counts_show_until_stale() {
        let mut r = roster(2);
        assert!(r.status(0).lobbies.is_empty());
        let mut l = BTreeMap::new();
        l.insert("Normal24".to_string(), 3);
        l.insert("bad mode!".to_string(), 1);
        r.report_lobbies(&l, 1_000);
        assert_eq!(r.status(2_000).lobbies.into_iter().collect::<Vec<_>>(), vec![("Normal24".to_string(), 3)]);
        assert!(r.status(1_000 + LOBBIES_STALE_MS).lobbies.is_empty());
    }

    #[test]
    fn lobby_ca_keeps_only_certificates() {
        let pem = "junk\n-----BEGIN CERTIFICATE-----\nAAA\n-----END CERTIFICATE-----\n-----BEGIN PRIVATE KEY-----\nSECRET\n-----END PRIVATE KEY-----\n-----BEGIN CERTIFICATE-----\nBBB\n-----END CERTIFICATE-----\n";
        let ca = lobby_ca_from_pem(pem).unwrap();
        assert!(!ca.contains("SECRET") && !ca.contains("junk"));
        assert_eq!(ca.matches("BEGIN CERTIFICATE").count(), 2);
        assert!(lobby_ca_from_pem("-----BEGIN PRIVATE KEY-----\nx\n-----END PRIVATE KEY-----\n").is_none());
        assert!(lobby_ca_from_pem(&format!("-----BEGIN CERTIFICATE-----\n{}\n-----END CERTIFICATE-----\n", "A".repeat(MAX_LOBBY_CA))).is_none());
    }
}
