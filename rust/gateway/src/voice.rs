//! Lobby voice relay (docs/contracts/VOICE_API.md, `voice/v1`): one UDP socket,
//! STUN-shaped indications, every game lobby one room.
//!
//! The relay only forwards Opus packets it never decodes. Who a datagram
//! belongs to is the endpoint a ticket-checked control message bound; which
//! room is the lobby the [`Rooms`] backend (the lobby Core) has that user in.
//! Seats (0..[`MAX_SEATS`]) are the relay's own, lowest free per room.
//!
//! [`Relay`] is the pure state machine (time passed in, datagrams returned);
//! [`run`] is the socket loop around it.

use std::collections::{HashMap, HashSet};
use std::net::SocketAddr;
use std::sync::Arc;
use std::time::{Duration, Instant};

use tokio::net::UdpSocket;

pub const MAGIC: u32 = 0x2112_A442;
pub const KIND_VOICE: u16 = 0x3E93;
pub const KIND_CONTROL: u16 = 0x3E94;
pub const ATTR_VOICE_UP: u16 = 0xC0F3;
pub const ATTR_VOICE_DOWN: u16 = 0xC0F4;
pub const ATTR_CONTROL_UP: u16 = 0xC0F5;
pub const ATTR_STATE_DOWN: u16 = 0xC0F6;

pub const VERSION: u8 = 1;
pub const OP_SUBSCRIBE: u8 = 1;
pub const OP_REPLACE_MUTES: u8 = 2;
pub const OP_LEAVE: u8 = 3;
pub const SELF_MIC_MUTED: u8 = 1;
pub const SELF_DEAFENED: u8 = 2;
pub const FLAG_END_OF_SPURT: u8 = 1;
pub const STATE_TALKING: u8 = 1;

pub const MAX_VOICE_DATAGRAM: usize = 548;
pub const MAX_OPUS: usize = 400;
pub const MAX_CONTROL_DATAGRAM: usize = 1200;
pub const MAX_MUTES: usize = 32;
pub const MAX_TICKET: usize = 900;
/// Seats per room: a lobby holds up to 24 players.
pub const MAX_SEATS: usize = 24;
pub const MAX_TALKERS: usize = 4;

pub const BINDING_SECS: f64 = 15.0;
pub const TALKING_SECS: f64 = 0.3;
pub const STATE_SECS: f64 = 1.0;
/// How long a sender's room lookup is reused for voice up.
pub const ROOM_CACHE_SECS: f64 = 0.25;
pub const PACKETS_PER_SEC: f64 = 60.0;
pub const BYTES_PER_SEC: f64 = 4096.0;
/// Voice egress over one second past which state down asks for bundle 6.
pub const EGRESS_BUDGET: usize = 2 << 20;
pub const BUNDLE: u8 = 3;
pub const BUNDLE_OVER_BUDGET: u8 = 6;

/// The lobby as the relay sees it (the lobby Core, through an adapter).
/// Called from the relay task: implementations must be quick.
pub trait Rooms: Send + Sync + 'static {
    /// The user id a seat ticket names, if it verifies (else refuse).
    fn user(&self, ticket: &str) -> Option<String>;
    /// The lobby `uid` is in, and every member's user id.
    fn room(&self, uid: &str) -> Option<(u128, Vec<String>)>;
    /// The account id (u64) of a user id.
    fn account(&self, uid: &str) -> u64;
}

// ---- wire -----------------------------------------------------------------------------------

/// A STUN indication: kind, transaction id and the value of attribute `attr`.
fn parse(data: &[u8], kind: u16, attr: u16) -> Option<([u8; 12], &[u8])> {
    if data.len() < 20 || u16::from_be_bytes([data[0], data[1]]) != kind {
        return None;
    }
    let len = u16::from_be_bytes([data[2], data[3]]) as usize;
    if u32::from_be_bytes(data[4..8].try_into().unwrap()) != MAGIC || len != data.len() - 20 || !len.is_multiple_of(4) {
        return None;
    }
    let txid: [u8; 12] = data[8..20].try_into().unwrap();
    let mut rest = &data[20..];
    while rest.len() >= 4 {
        let t = u16::from_be_bytes([rest[0], rest[1]]);
        let l = u16::from_be_bytes([rest[2], rest[3]]) as usize;
        let padded = (l + 3) & !3;
        if rest.len() < 4 + padded {
            return None;
        }
        if t == attr {
            return Some((txid, &rest[4..4 + l]));
        }
        rest = &rest[4 + padded..];
    }
    None
}

/// The kind of a datagram, if it carries the magic cookie.
pub fn kind_of(data: &[u8]) -> Option<u16> {
    (data.len() >= 20 && u32::from_be_bytes(data[4..8].try_into().unwrap()) == MAGIC)
        .then(|| u16::from_be_bytes([data[0], data[1]]))
}

pub fn write(kind: u16, txid: &[u8; 12], attr: u16, value: &[u8]) -> Vec<u8> {
    let padded = (value.len() + 3) & !3;
    let mut out = Vec::with_capacity(24 + padded);
    out.extend_from_slice(&kind.to_be_bytes());
    out.extend_from_slice(&((4 + padded) as u16).to_be_bytes());
    out.extend_from_slice(&MAGIC.to_be_bytes());
    out.extend_from_slice(txid);
    out.extend_from_slice(&attr.to_be_bytes());
    out.extend_from_slice(&(value.len() as u16).to_be_bytes());
    out.extend_from_slice(value);
    out.resize(24 + padded, 0);
    out
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Control {
    pub op: u8,
    pub self_flags: u8,
    pub mutes: Vec<u64>,
    pub ticket: String,
}

pub fn parse_control(data: &[u8]) -> Option<Control> {
    if data.len() > MAX_CONTROL_DATAGRAM {
        return None;
    }
    let (_, v) = parse(data, KIND_CONTROL, ATTR_CONTROL_UP)?;
    if v.len() < 6 || v[0] != VERSION || !(OP_SUBSCRIBE..=OP_LEAVE).contains(&v[1]) {
        return None;
    }
    let count = v[3] as usize;
    if count > MAX_MUTES || v.len() < 4 + count * 8 + 2 {
        return None;
    }
    let mutes = (0..count).map(|i| u64::from_be_bytes(v[4 + i * 8..12 + i * 8].try_into().unwrap())).collect();
    let at = 4 + count * 8;
    let tl = u16::from_be_bytes([v[at], v[at + 1]]) as usize;
    if tl == 0 || tl > MAX_TICKET || v.len() != at + 2 + tl {
        return None;
    }
    let ticket = std::str::from_utf8(&v[at + 2..]).ok()?.to_string();
    Some(Control { op: v[1], self_flags: v[2], mutes, ticket })
}

pub fn write_control(c: &Control, txid: &[u8; 12]) -> Vec<u8> {
    let mut v = vec![VERSION, c.op, c.self_flags, c.mutes.len() as u8];
    for m in &c.mutes {
        v.extend_from_slice(&m.to_be_bytes());
    }
    v.extend_from_slice(&(c.ticket.len() as u16).to_be_bytes());
    v.extend_from_slice(c.ticket.as_bytes());
    write(KIND_CONTROL, txid, ATTR_CONTROL_UP, &v)
}

/// Voice up: (txid, opus packet).
pub fn parse_voice_up(data: &[u8]) -> Option<([u8; 12], &[u8])> {
    if data.len() > MAX_VOICE_DATAGRAM {
        return None;
    }
    let (txid, opus) = parse(data, KIND_VOICE, ATTR_VOICE_UP)?;
    (txid[0] == VERSION && (1..=MAX_OPUS).contains(&opus.len())).then_some((txid, opus))
}

/// Voice down: ver, flags, seq, ts copied from the talker's; its seat; 3 random bytes.
pub fn write_voice_down(up_txid: &[u8; 12], seat: u8, opus: &[u8], random: [u8; 3]) -> Vec<u8> {
    let mut txid = [0u8; 12];
    txid[..8].copy_from_slice(&up_txid[..8]);
    txid[8] = seat;
    txid[9..].copy_from_slice(&random);
    write(KIND_VOICE, &txid, ATTR_VOICE_DOWN, opus)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SeatState {
    pub seat: u8,
    pub account: u64,
    pub state: u8,
}

pub fn write_state(in_room: bool, bundle: u8, seats: &[SeatState], txid: &[u8; 12]) -> Vec<u8> {
    let mut v = vec![VERSION, u8::from(in_room), bundle, seats.len() as u8];
    for s in seats {
        v.push(s.seat);
        v.extend_from_slice(&s.account.to_be_bytes());
        v.push(s.state);
    }
    write(KIND_CONTROL, txid, ATTR_STATE_DOWN, &v)
}

/// State down: (in a room, bundle, seats).
pub fn parse_state(data: &[u8]) -> Option<(bool, u8, Vec<SeatState>)> {
    let (_, v) = parse(data, KIND_CONTROL, ATTR_STATE_DOWN)?;
    if v.len() < 4 || v[0] != VERSION || v.len() != 4 + v[3] as usize * 10 {
        return None;
    }
    let seats = v[4..]
        .chunks(10)
        .map(|c| SeatState { seat: c[0], account: u64::from_be_bytes(c[1..9].try_into().unwrap()), state: c[9] })
        .collect();
    Some((v[1] & 1 != 0, v[2], seats))
}

// ---- relay ----------------------------------------------------------------------------------

type Room = u128;
pub type Datagrams = Vec<(SocketAddr, Vec<u8>)>;

struct Sub {
    endpoint: SocketAddr,
    account: u64,
    last_control: f64,
    self_flags: u8,
    mutes: HashSet<u64>,
    /// Room and its members as last looked up, and when.
    room: Option<(Room, HashSet<String>)>,
    room_at: f64,
    seat: Option<u8>,
    last_voice: Option<f64>,
    /// Talking as last reported in state down.
    talking: bool,
    packets: f64,
    bytes: f64,
    rate_at: f64,
}

impl Sub {
    fn room_id(&self) -> Option<Room> {
        self.room.as_ref().map(|r| r.0)
    }
    fn talking_at(&self, now: f64) -> bool {
        self.last_voice.is_some_and(|t| now - t < TALKING_SECS)
    }
    fn take_rate(&mut self, now: f64, bytes: usize) -> bool {
        let dt = (now - self.rate_at).max(0.0);
        self.rate_at = now;
        self.packets = (self.packets + dt * PACKETS_PER_SEC).min(PACKETS_PER_SEC);
        self.bytes = (self.bytes + dt * BYTES_PER_SEC).min(BYTES_PER_SEC);
        if self.packets < 1.0 || self.bytes < bytes as f64 {
            return false;
        }
        self.packets -= 1.0;
        self.bytes -= bytes as f64;
        true
    }
}

pub struct Relay<R: Rooms> {
    rooms: Arc<R>,
    subs: HashMap<String, Sub>,
    by_endpoint: HashMap<SocketAddr, String>,
    seats: HashMap<Room, [Option<String>; MAX_SEATS]>,
    /// Rooms whose state down is due now.
    dirty: HashSet<Room>,
    /// Subscribers without a room whose (empty) state down is due now.
    due: HashSet<String>,
    last_state: f64,
    egress: (f64, usize, usize), // (window start, bytes this window, bytes last window)
}

impl<R: Rooms> Relay<R> {
    pub fn new(rooms: Arc<R>) -> Self {
        Self {
            rooms,
            subs: HashMap::new(),
            by_endpoint: HashMap::new(),
            seats: HashMap::new(),
            dirty: HashSet::new(),
            due: HashSet::new(),
            last_state: f64::NEG_INFINITY,
            egress: (0.0, 0, 0),
        }
    }

    pub fn subscribers(&self) -> usize {
        self.subs.len()
    }

    /// One datagram from `from` at `now` (seconds); what to send back out.
    pub fn handle(&mut self, now: f64, from: SocketAddr, data: &[u8]) -> Datagrams {
        let mut out = vec![];
        match kind_of(data) {
            Some(KIND_CONTROL) => {
                if let Some(c) = parse_control(data) {
                    self.control(now, from, c);
                }
            }
            Some(KIND_VOICE) => {
                if let Some((txid, opus)) = parse_voice_up(data) {
                    self.voice(now, from, &txid, opus, data.len(), &mut out);
                }
            }
            _ => {}
        }
        self.flush(now, &mut out);
        out
    }

    /// Expiry, room reconciliation, talking timeouts and the 1 s state down.
    pub fn tick(&mut self, now: f64) -> Datagrams {
        let expired: Vec<String> =
            self.subs.iter().filter(|(_, s)| now - s.last_control >= BINDING_SECS).map(|(u, _)| u.clone()).collect();
        for uid in expired {
            self.drop_sub(&uid);
        }
        let uids: Vec<String> = self.subs.keys().cloned().collect();
        for uid in uids {
            if now - self.subs[&uid].room_at >= STATE_SECS {
                self.refresh_room(&uid, now);
            }
            let s = self.subs.get_mut(&uid).expect("listed");
            if s.talking && !s.talking_at(now) {
                s.talking = false;
                if let Some(r) = s.room_id() {
                    self.dirty.insert(r);
                }
            }
        }
        let mut out = vec![];
        if now - self.last_state >= STATE_SECS {
            self.last_state = now;
            self.dirty.extend(self.subs.values().filter_map(Sub::room_id));
            self.due.extend(self.subs.iter().filter(|(_, s)| s.room.is_none()).map(|(u, _)| u.clone()));
        }
        self.flush(now, &mut out);
        out
    }

    fn control(&mut self, now: f64, from: SocketAddr, c: Control) {
        let Some(uid) = self.rooms.user(&c.ticket) else { return };
        if c.op == OP_LEAVE {
            // Only the endpoint bound to that user may end its binding.
            if self.subs.get(&uid).is_some_and(|s| s.endpoint == from) {
                self.drop_sub(&uid);
            }
            return;
        }
        // An endpoint that spoke for someone else now speaks for this user.
        if let Some(other) = self.by_endpoint.get(&from).filter(|u| **u != uid).cloned() {
            self.drop_sub(&other);
        }
        let account = self.rooms.account(&uid);
        let fresh = self.subs.get(&uid).is_none_or(|s| s.endpoint != from);
        let s = self.subs.entry(uid.clone()).or_insert_with(|| Sub {
            endpoint: from,
            account,
            last_control: now,
            self_flags: 0,
            mutes: HashSet::new(),
            room: None,
            room_at: f64::NEG_INFINITY,
            seat: None,
            last_voice: None,
            talking: false,
            packets: PACKETS_PER_SEC,
            bytes: BYTES_PER_SEC,
            rate_at: now,
        });
        if s.endpoint != from {
            self.by_endpoint.remove(&s.endpoint);
            s.endpoint = from;
        }
        self.by_endpoint.insert(from, uid.clone());
        s.last_control = now;
        s.self_flags = c.self_flags;
        s.mutes = c.mutes.into_iter().collect();
        self.refresh_room(&uid, now);
        // The subscriber learns its room at once; the room hears about it only
        // when the binding is new (a seat change dirties the room by itself).
        match self.subs[&uid].room_id() {
            Some(r) if fresh => {
                self.dirty.insert(r);
            }
            _ => {
                self.due.insert(uid);
            }
        }
    }

    fn voice(&mut self, now: f64, from: SocketAddr, txid: &[u8; 12], opus: &[u8], size: usize, out: &mut Datagrams) {
        let Some(uid) = self.by_endpoint.get(&from).cloned() else { return };
        if now - self.subs[&uid].room_at >= ROOM_CACHE_SECS {
            self.refresh_room(&uid, now);
        }
        let s = self.subs.get_mut(&uid).expect("bound");
        if s.self_flags & SELF_MIC_MUTED != 0 || !s.take_rate(now, size) {
            return;
        }
        let (Some((room, members)), Some(seat)) = (s.room.clone(), s.seat) else { return };
        let account = s.account;
        let was_talking = s.talking_at(now);
        if !was_talking {
            let talkers = self
                .subs
                .iter()
                .filter(|(u, o)| **u != uid && o.room_id() == Some(room) && o.talking_at(now))
                .count();
            if talkers >= MAX_TALKERS {
                return;
            }
        }
        let s = self.subs.get_mut(&uid).expect("bound");
        if txid[1] & FLAG_END_OF_SPURT != 0 {
            s.last_voice = None;
        } else {
            s.last_voice = Some(now);
        }
        let talking = s.talking_at(now);
        if talking != s.talking {
            s.talking = talking;
            self.dirty.insert(room);
        }
        for (u, l) in &self.subs {
            if *u == uid
                || l.room_id() != Some(room)
                || !members.contains(u)
                || l.self_flags & SELF_DEAFENED != 0
                || l.mutes.contains(&account)
            {
                continue;
            }
            let d = write_voice_down(txid, seat, opus, rand::random());
            self.egress.1 += d.len();
            out.push((l.endpoint, d));
        }
    }

    /// Look the user's room up again; move its seat if the room changed.
    fn refresh_room(&mut self, uid: &str, now: f64) {
        let found = self.rooms.room(uid).map(|(r, m)| (r, m.into_iter().collect::<HashSet<_>>()));
        let s = self.subs.get_mut(uid).expect("subscribed");
        s.room_at = now;
        let old = s.room_id();
        let new = found.as_ref().map(|r| r.0);
        s.room = found;
        if old == new && (new.is_none() || s.seat.is_some()) {
            return;
        }
        if let Some(r) = old.filter(|r| Some(*r) != new) {
            self.free_seat(r, uid);
            self.dirty.insert(r);
            self.subs.get_mut(uid).expect("subscribed").seat = None;
        }
        if let Some(r) = new {
            let seats = self.seats.entry(r).or_insert_with(|| std::array::from_fn(|_| None));
            let seat = seats.iter().position(|x| x.as_deref() == Some(uid)).or_else(|| {
                let free = seats.iter().position(Option::is_none)?;
                seats[free] = Some(uid.to_string());
                Some(free)
            });
            self.subs.get_mut(uid).expect("subscribed").seat = seat.map(|x| x as u8);
            self.dirty.insert(r);
        }
    }

    fn free_seat(&mut self, room: Room, uid: &str) {
        if let Some(seats) = self.seats.get_mut(&room) {
            for x in seats.iter_mut().filter(|x| x.as_deref() == Some(uid)) {
                *x = None;
            }
            if seats.iter().all(Option::is_none) {
                self.seats.remove(&room);
            }
        }
    }

    fn drop_sub(&mut self, uid: &str) {
        if let Some(s) = self.subs.remove(uid) {
            self.by_endpoint.remove(&s.endpoint);
            if let Some(r) = s.room_id() {
                self.free_seat(r, uid);
                self.dirty.insert(r);
            }
        }
        self.due.remove(uid);
    }

    fn bundle(&mut self, now: f64) -> u8 {
        if now - self.egress.0 >= 1.0 {
            self.egress = (now, 0, self.egress.1);
        }
        if self.egress.2.max(self.egress.1) > EGRESS_BUDGET {
            BUNDLE_OVER_BUDGET
        } else {
            BUNDLE
        }
    }

    fn flush(&mut self, now: f64, out: &mut Datagrams) {
        let bundle = self.bundle(now);
        let dirty = std::mem::take(&mut self.dirty);
        for &room in &dirty {
            let msg = self.room_state(room, bundle, now);
            for s in self.subs.values().filter(|s| s.room_id() == Some(room)) {
                out.push((s.endpoint, msg.clone()));
            }
        }
        for uid in std::mem::take(&mut self.due) {
            let Some(s) = self.subs.get(&uid) else { continue };
            match s.room_id() {
                None => out.push((s.endpoint, write_state(false, bundle, &[], &rand::random()))),
                Some(r) if !dirty.contains(&r) => out.push((s.endpoint, self.room_state(r, bundle, now))),
                Some(_) => {} // just sent with its room
            }
        }
    }

    fn room_state(&self, room: Room, bundle: u8, now: f64) -> Vec<u8> {
        let mut seats: Vec<SeatState> = self
            .subs
            .values()
            .filter(|s| s.room_id() == Some(room))
            .filter_map(|s| {
                let state = if s.talking_at(now) { STATE_TALKING } else { 0 };
                s.seat.map(|seat| SeatState { seat, account: s.account, state })
            })
            .collect();
        seats.sort_by_key(|s| s.seat);
        write_state(true, bundle, &seats, &rand::random())
    }
}

/// The socket loop: datagrams in, the relay's answers out, a 100 ms tick.
pub async fn run<R: Rooms>(sock: UdpSocket, rooms: Arc<R>) -> std::io::Result<()> {
    let mut relay = Relay::new(rooms);
    let start = Instant::now();
    let mut buf = vec![0u8; 2048];
    let mut tick = tokio::time::interval(Duration::from_millis(100));
    tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
    // Control up is the one unauthenticated path (and costs a signature check):
    // cap it per source address. A LAN party shares one public address.
    let mut per_ip = crate::limits::PerIp::new(20.0, 40.0);
    loop {
        let out = tokio::select! {
            r = sock.recv_from(&mut buf) => {
                let Ok((n, peer)) = r else { continue };
                let data = &buf[..n];
                if kind_of(data) == Some(KIND_CONTROL) && !per_ip.allow(peer.ip()) {
                    continue;
                }
                relay.handle(start.elapsed().as_secs_f64(), peer, data)
            }
            _ = tick.tick() => relay.tick(start.elapsed().as_secs_f64()),
        };
        for (to, d) in out {
            // Never wait on a full send buffer: voice is late or it is useless.
            let _ = sock.try_send_to(&d, to);
        }
    }
}

#[cfg(test)]
mod tests;
