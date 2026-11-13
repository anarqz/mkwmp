//! The seat roster: every live session, N seats, a FIFO queue, and the
//! session-only friend graph. All in memory; nothing survives a restart.
//!
//! Time is passed in (unix milliseconds) so the rules are testable without
//! sleeping. Liveness: a session not heard from within its timeout is dropped
//! by [`Roster::sweep`] and the queue is promoted.

use std::collections::{HashMap, VecDeque};

use rand::RngCore;
use serde::Serialize;
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
        }
    }
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
            RosterError::NameTaken => f.write_str("that name is in use on this server"),
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
    friends: Vec<String>,
    seat: Option<Seat>,
    last_seen: u64,
}

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
    pub friends: Vec<FriendView>,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct TicketView {
    pub token: String,
    pub expires_at: u64,
    pub lobby: String,
}

/// One entry of the player's own list, as the launcher shows it.
#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct FriendView {
    pub name: String,
    /// Seated on this server right now.
    pub online: bool,
    /// They list you too: you are friends in game.
    pub mutual: bool,
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
    pub session_id: String,
    pub name: String,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Status {
    pub boot_id: String,
    pub name: String,
    /// NPLN `host:port` of this server's lobby.
    pub lobby_endpoint: String,
    pub seats_total: usize,
    pub seated: usize,
    pub queued: usize,
}

pub struct Roster {
    boot_id: String,
    rules: Rules,
    issuer: Issuer,
    sessions: HashMap<String, Session>,
    by_name: HashMap<String, String>, // name key -> sid
    queue: VecDeque<String>,
    seated: usize,
}

pub struct Created {
    pub session_key: String,
    pub view: View,
}

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

    /// A player connects with a name and a friend list. Seated at once if a
    /// seat is free, else queued.
    pub fn join(&mut self, name: &str, friends: &[String], now: u64) -> Result<Created, RosterError> {
        self.sweep(now);
        names::validate(name)?;
        let friends = names::friends(name, friends)?;
        if self.by_name.contains_key(&names::key(name)) {
            return Err(RosterError::NameTaken);
        }
        if self.queue.len() >= self.rules.max_queue && self.seated >= self.rules.capacity {
            return Err(RosterError::QueueFull);
        }
        let sid = random_hex(16);
        let key = random_hex(32);
        self.sessions.insert(
            sid.clone(),
            Session { key: key.clone(), name: name.to_string(), friends, seat: None, last_seen: now },
        );
        self.by_name.insert(names::key(name), sid.clone());
        self.queue.push_back(sid.clone());
        self.promote(now);
        Ok(Created { session_key: key, view: self.view(&sid) })
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

    /// Replace the friend list. Also counts as a heartbeat.
    pub fn set_friends(&mut self, sid: &str, key: &str, friends: &[String], now: u64) -> Result<View, RosterError> {
        self.sweep(now);
        let s = self.session_mut(sid, key)?;
        s.friends = names::friends(&s.name, friends)?;
        s.last_seen = now;
        Ok(self.view(sid))
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
        }
    }

    /// The seated session named `name` (case-insensitive), if any.
    fn seated_by_name(&self, name: &str) -> Option<(&String, &Session)> {
        let sid = self.by_name.get(&names::key(name))?;
        let s = self.sessions.get(sid)?;
        s.seat.as_ref().map(|_| (sid, s))
    }

    fn lists(s: &Session, other: &str) -> bool {
        let k = names::key(other);
        s.friends.iter().any(|f| names::key(f) == k)
    }

    /// Mutual friends of `s` that are seated, sorted by name (case-insensitive).
    fn mutual(&self, s: &Session) -> Vec<RosterFriend> {
        let mut out: Vec<RosterFriend> = s
            .friends
            .iter()
            .filter_map(|f| self.seated_by_name(f))
            .filter(|(_, other)| Self::lists(other, &s.name))
            .map(|(sid, other)| RosterFriend { session_id: sid.clone(), name: other.name.clone() })
            .collect();
        out.sort_by_key(|f| names::key(&f.name));
        out
    }

    fn view(&self, sid: &str) -> View {
        let s = &self.sessions[sid];
        let position = if s.seat.is_none() {
            self.queue.iter().position(|q| q == sid).map(|p| p + 1)
        } else {
            None
        };
        let friends = s
            .friends
            .iter()
            .map(|f| {
                let other = self.seated_by_name(f);
                FriendView {
                    name: other.map(|(_, o)| o.name.clone()).unwrap_or_else(|| f.clone()),
                    online: other.is_some(),
                    mutual: other.is_some_and(|(_, o)| Self::lists(o, &s.name)),
                }
            })
            .collect();
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
            friends,
        }
    }

    /// The lobby's view of its players: alive = still seated.
    pub fn roster(&mut self, sids: &[String], now: u64) -> Vec<RosterEntry> {
        self.sweep(now);
        sids.iter()
            .map(|sid| match self.sessions.get(sid).filter(|s| s.seat.is_some()) {
                Some(s) => RosterEntry {
                    session_id: sid.clone(),
                    name: Some(s.name.clone()),
                    alive: true,
                    friends: self.mutual(s),
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
            seats_total: self.rules.capacity,
            seated: self.seated,
            queued: self.queue.len(),
        }
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

    fn f(names: &[&str]) -> Vec<String> {
        names.iter().map(|s| s.to_string()).collect()
    }

    #[test]
    fn seats_then_queues_then_promotes_on_leave() {
        let mut r = roster(1);
        let a = r.join("ana", &[], 0).unwrap();
        assert_eq!(a.view.state, "seated");
        assert!(a.view.ticket.is_some());
        let b = r.join("bia", &[], 0).unwrap();
        assert_eq!((b.view.state, b.view.position), ("queued", Some(1)));
        assert!(b.view.ticket.is_none());
        let c = r.join("cid", &[], 0).unwrap();
        assert_eq!(c.view.position, Some(2));

        r.leave(&a.view.session_id, &a.session_key, 1);
        let b2 = r.touch(&b.view.session_id, &b.session_key, 2).unwrap();
        assert_eq!(b2.state, "seated");
        assert_eq!(r.touch(&c.view.session_id, &c.session_key, 2).unwrap().position, Some(1));
    }

    #[test]
    fn ticket_verifies_and_names_the_session() {
        let mut r = roster(1);
        let a = r.join("Ana", &[], 1_700_000_000_000).unwrap();
        let t = a.view.ticket.unwrap();
        let v = TicketVerifier::from_entries(&[format!("t:{}", r.issuer.public_key_b64())]).unwrap();
        let c = v.verify(&t.token, 1_700_000_001).unwrap();
        assert_eq!((c.sid.as_str(), c.name.as_str()), (a.view.session_id.as_str(), "Ana"));
        assert_eq!(c.exp, 1_700_000_000 + 12 * 3600);
    }

    #[test]
    fn names_unique_case_insensitive_and_freed_on_leave() {
        let mut r = roster(5);
        let a = r.join("Ana", &[], 0).unwrap();
        assert_eq!(r.join("ANA", &[], 0).err(), Some(RosterError::NameTaken));
        r.leave(&a.view.session_id, &a.session_key, 0);
        assert!(r.join("ana", &[], 0).is_ok());
    }

    #[test]
    fn invalid_names_rejected() {
        let mut r = roster(5);
        assert!(matches!(r.join("a b", &[], 0), Err(RosterError::Name(_))));
        assert!(matches!(r.join("ana", &f(&["ok_name", "x"]), 0), Err(RosterError::Name(_))));
    }

    #[test]
    fn silent_seat_expires_and_queue_promotes() {
        let mut r = roster(1);
        let a = r.join("ana", &[], 0).unwrap();
        let b = r.join("bia", &[], 0).unwrap();
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
        let a = r.join("ana", &[], 0).unwrap();
        let b = r.join("bia", &[], 0).unwrap();
        r.touch(&a.view.session_id, &a.session_key, 30_001).unwrap();
        assert_eq!(r.touch(&b.view.session_id, &b.session_key, 30_002), Err(RosterError::NotFound));
    }

    #[test]
    fn long_session_gets_a_fresh_ticket() {
        let rules = Rules { capacity: 1, seat_timeout_ms: u64::MAX / 2, ..Rules::default() };
        let mut r = Roster::new(rules, Issuer::new("t", SigningKey::from_bytes(&[1; 32])));
        let a = r.join("ana", &[], 0).unwrap();
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
        r.join("aaa", &[], 0).unwrap();
        for n in ["bbb", "ccc", "ddd"] {
            r.join(n, &[], 0).unwrap();
        }
        assert_eq!(r.join("eee", &[], 0).err(), Some(RosterError::QueueFull));
    }

    #[test]
    fn wrong_key_is_not_found() {
        let mut r = roster(1);
        let a = r.join("ana", &[], 0).unwrap();
        assert_eq!(r.touch(&a.view.session_id, "nope", 0), Err(RosterError::NotFound));
        r.leave(&a.view.session_id, "nope", 0);
        assert!(r.touch(&a.view.session_id, &a.session_key, 0).is_ok());
    }

    #[test]
    fn friends_are_mutual_seated_and_sorted() {
        let mut r = roster(3);
        let ana = r.join("Ana", &f(&["zed", "BIA", "cid", "dan"]), 0).unwrap();
        let bia = r.join("bia", &f(&["ana"]), 0).unwrap();
        let _cid = r.join("cid", &f(&["someone"]), 0).unwrap(); // one-sided
        let zed = r.join("Zed", &f(&["ana"]), 0).unwrap(); // mutual but queued
        assert_eq!(zed.view.state, "queued");

        let e = &r.roster(std::slice::from_ref(&ana.view.session_id), 1)[0];
        assert!(e.alive);
        let names: Vec<&str> = e.friends.iter().map(|x| x.name.as_str()).collect();
        assert_eq!(names, vec!["bia"]);
        assert_eq!(e.friends[0].session_id, bia.view.session_id);

        let v = r.touch(&ana.view.session_id, &ana.session_key, 1).unwrap();
        let summary: Vec<(String, bool, bool)> =
            v.friends.into_iter().map(|x| (x.name, x.online, x.mutual)).collect();
        assert_eq!(
            summary,
            vec![
                ("zed".into(), false, false),
                ("bia".into(), true, true),
                ("cid".into(), true, false),
                ("dan".into(), false, false),
            ]
        );

        // Zed gets a seat once Bia leaves; the sorted roster then has both.
        r.leave(&bia.view.session_id, &bia.session_key, 2);
        r.set_friends(&bia.view.session_id, &bia.session_key, &[], 2).unwrap_err();
        let e = &r.roster(std::slice::from_ref(&ana.view.session_id), 3)[0];
        let names: Vec<&str> = e.friends.iter().map(|x| x.name.as_str()).collect();
        assert_eq!(names, vec!["Zed"]);
    }

    #[test]
    fn editing_friends_takes_effect() {
        let mut r = roster(2);
        let ana = r.join("ana", &[], 0).unwrap();
        let _bia = r.join("bia", &f(&["ana"]), 0).unwrap();
        assert!(r.roster(std::slice::from_ref(&ana.view.session_id), 0)[0].friends.is_empty());
        r.set_friends(&ana.view.session_id, &ana.session_key, &f(&["bia"]), 1).unwrap();
        assert_eq!(r.roster(std::slice::from_ref(&ana.view.session_id), 1)[0].friends.len(), 1);
    }

    #[test]
    fn roster_reports_gone_and_queued_as_not_alive() {
        let mut r = roster(1);
        let _a = r.join("ana", &[], 0).unwrap();
        let b = r.join("bia", &[], 0).unwrap();
        let out = r.roster(&[b.view.session_id.clone(), "unknown".into()], 0);
        assert!(out.iter().all(|e| !e.alive && e.name.is_none()));
    }
}
