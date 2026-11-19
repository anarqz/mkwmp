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

// ---- chat rooms (VOICE_API.md §6) and video (VIDEO_API.md) ----
pub const KIND_VIDEO: u16 = 0x3E95;
pub const ATTR_VIDEO_UP: u16 = 0xC0F7;
pub const ATTR_VIDEO_DOWN: u16 = 0xC0F8;
pub const OP_VIDEO: u8 = 6;
pub const SELF_CAMERA: u8 = 4;
pub const SELF_ROOM_MODE: u8 = 8;
pub const STATE_VERSION_ROOMS: u8 = 2;
pub const MEMBER_CAMERA: u8 = 1;
pub const MEMBER_BOUND: u8 = 2;
/// Faces a player sees at once (the game shows four).
pub const MAX_FACES: usize = 4;
pub const MAX_VIDEO_CHUNK: usize = 1100;
pub const MAX_VIDEO_CHUNKS: u8 = 64;
pub const MAX_VIDEO_DATAGRAM: usize = 20 + 4 + MAX_VIDEO_CHUNK;
pub const VIDEO_FLAG_KEY: u8 = 1;
pub const VIDEO_FLAG_LAST: u8 = 2;
pub const VIDEO_BYTES_PER_SEC: f64 = 65536.0;
pub const VIDEO_PACKETS_PER_SEC: f64 = 400.0;
pub const VIDEO_BUDGET: usize = 8 << 20;
/// Under half the budget this long, and the frame-rate divisor goes back to 1.
pub const VIDEO_CALM_SECS: f64 = 5.0;

/// The lobby as the relay sees it (the lobby Core, through an adapter).
/// Called from the relay task: implementations must be quick.
pub trait Rooms: Send + Sync + 'static {
    /// The user id a seat ticket names, if it verifies (else refuse).
    fn user(&self, ticket: &str) -> Option<String>;
    /// The lobby `uid` is in, and every member's user id.
    fn room(&self, uid: &str) -> Option<(u128, Vec<String>)>;
    /// The account id (u64) of a user id.
    fn account(&self, uid: &str) -> u64;
    /// Whether `a` may see `b`'s camera: mutual friends (VOICE_API.md §6). Default: never.
    fn friends(&self, _a: &str, _b: &str) -> bool {
        false
    }
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

/// Op 6, video subscribe (VIDEO_API.md §2): the accounts whose pictures this player wants.
pub fn parse_video_subscribe(data: &[u8]) -> Option<Vec<u64>> {
    if data.len() > MAX_CONTROL_DATAGRAM {
        return None;
    }
    let (_, v) = parse(data, KIND_CONTROL, ATTR_CONTROL_UP)?;
    if v.len() < 3 || v[0] != VERSION || v[1] != OP_VIDEO {
        return None;
    }
    let n = v[2] as usize;
    (n <= MAX_FACES && v.len() == 3 + n * 8)
        .then(|| (0..n).map(|i| u64::from_be_bytes(v[3 + i * 8..11 + i * 8].try_into().unwrap())).collect())
}

pub fn write_video_subscribe(accounts: &[u64], txid: &[u8; 12]) -> Vec<u8> {
    let mut v = vec![VERSION, OP_VIDEO, accounts.len() as u8];
    for a in accounts {
        v.extend_from_slice(&a.to_be_bytes());
    }
    write(KIND_CONTROL, txid, ATTR_CONTROL_UP, &v)
}

/// State down v2 (VOICE_API.md §6.5), sent only to room-mode subscribers.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StateV2 {
    pub in_room: bool,
    pub bundle: u8,
    pub seats: Vec<SeatState>,
    pub room_id: u64,
    pub fps: u8,
    /// The listener's mutual friends in the room: (account, flags).
    pub members: Vec<(u64, u8)>,
}

pub fn write_state_v2(st: &StateV2, txid: &[u8; 12]) -> Vec<u8> {
    let mut v = vec![STATE_VERSION_ROOMS, u8::from(st.in_room), st.bundle, st.seats.len() as u8];
    for s in &st.seats {
        v.push(s.seat);
        v.extend_from_slice(&s.account.to_be_bytes());
        v.push(s.state);
    }
    v.extend_from_slice(&st.room_id.to_be_bytes());
    v.push(st.fps);
    v.push(st.members.len() as u8);
    for (a, f) in &st.members {
        v.extend_from_slice(&a.to_be_bytes());
        v.push(*f);
    }
    write(KIND_CONTROL, txid, ATTR_STATE_DOWN, &v)
}

pub fn parse_state_v2(data: &[u8]) -> Option<StateV2> {
    let (_, v) = parse(data, KIND_CONTROL, ATTR_STATE_DOWN)?;
    if v.len() < 4 || v[0] != STATE_VERSION_ROOMS {
        return None;
    }
    let at = 4 + v[3] as usize * 10;
    if v.len() < at + 10 {
        return None;
    }
    let n = v[at + 9] as usize;
    if v.len() != at + 10 + n * 9 {
        return None;
    }
    let seats = v[4..at]
        .chunks(10)
        .map(|c| SeatState { seat: c[0], account: u64::from_be_bytes(c[1..9].try_into().unwrap()), state: c[9] })
        .collect();
    let members = v[at + 10..].chunks(9).map(|c| (u64::from_be_bytes(c[..8].try_into().unwrap()), c[8])).collect();
    Some(StateV2 {
        in_room: v[1] & 1 != 0,
        bundle: v[2],
        seats,
        room_id: u64::from_be_bytes(v[at..at + 8].try_into().unwrap()),
        fps: v[at + 8],
        members,
    })
}

/// Video up (VIDEO_API.md §1): (txid, chunk).
pub fn parse_video_up(data: &[u8]) -> Option<([u8; 12], &[u8])> {
    if data.len() > MAX_VIDEO_DATAGRAM {
        return None;
    }
    let (txid, chunk) = parse(data, KIND_VIDEO, ATTR_VIDEO_UP)?;
    let (n, i) = (txid[5], txid[4]);
    (txid[0] == VERSION && (1..=MAX_VIDEO_CHUNKS).contains(&n) && i < n && (1..=MAX_VIDEO_CHUNK).contains(&chunk.len()))
        .then_some((txid, chunk))
}

/// Video down: the sender's txid with its seat in the last byte.
pub fn write_video_down(up_txid: &[u8; 12], seat: u8, chunk: &[u8]) -> Vec<u8> {
    let mut txid = *up_txid;
    txid[11] = seat;
    write(KIND_VIDEO, &txid, ATTR_VIDEO_DOWN, chunk)
}

// ---- relay ----------------------------------------------------------------------------------

type Room = u128;
pub type Datagrams = Vec<(SocketAddr, Vec<u8>)>;

/// The server owner's switches (`[lobby] video`, `video_budget`).
#[derive(Debug, Clone, Copy)]
pub struct Options {
    pub video: bool,
    pub video_budget: usize,
}

impl Default for Options {
    fn default() -> Self {
        Self { video: true, video_budget: VIDEO_BUDGET }
    }
}

/// A token bucket holding at most one second of allowance.
#[derive(Debug, Clone, Copy)]
struct Bucket {
    packets: f64,
    bytes: f64,
    at: f64,
    pps: f64,
    bps: f64,
}

impl Bucket {
    fn new(now: f64, pps: f64, bps: f64) -> Self {
        Self { packets: pps, bytes: bps, at: now, pps, bps }
    }
    fn take(&mut self, now: f64, bytes: usize) -> bool {
        let dt = (now - self.at).max(0.0);
        self.at = now;
        self.packets = (self.packets + dt * self.pps).min(self.pps);
        self.bytes = (self.bytes + dt * self.bps).min(self.bps);
        if self.packets < 1.0 || self.bytes < bytes as f64 {
            return false;
        }
        self.packets -= 1.0;
        self.bytes -= bytes as f64;
        true
    }
}

struct Sub {
    endpoint: SocketAddr,
    account: u64,
    last_control: f64,
    self_flags: u8,
    mutes: HashSet<u64>,
    /// Room and its members as last looked up, and when.
    room: Option<(Room, HashSet<String>)>,
    room_at: f64,
    /// The members of the room that are this subscriber's mutual friends, as last looked up.
    friends: HashSet<String>,
    seat: Option<u8>,
    last_voice: Option<f64>,
    /// Talking as last reported in state down.
    talking: bool,
    rate: Bucket,
    /// Accounts whose pictures this subscriber asked for (op 6, at most 4).
    video_want: HashSet<u64>,
    video_rate: Bucket,
}

impl Sub {
    fn room_id(&self) -> Option<Room> {
        self.room.as_ref().map(|r| r.0)
    }
    fn talking_at(&self, now: f64) -> bool {
        self.last_voice.is_some_and(|t| now - t < TALKING_SECS)
    }
    fn room_mode(&self) -> bool {
        self.self_flags & SELF_ROOM_MODE != 0
    }
    fn camera(&self) -> bool {
        self.self_flags & SELF_CAMERA != 0
    }
}

pub struct Relay<R: Rooms> {
    rooms: Arc<R>,
    opts: Options,
    subs: HashMap<String, Sub>,
    by_endpoint: HashMap<SocketAddr, String>,
    seats: HashMap<Room, [Option<String>; MAX_SEATS]>,
    /// Rooms whose state down is due now.
    dirty: HashSet<Room>,
    /// Subscribers without a room whose (empty) state down is due now.
    due: HashSet<String>,
    last_state: f64,
    egress: (f64, usize, usize), // (window start, bytes this window, bytes last window)
    video_egress: (f64, usize),
    fps: u8,
    calm_since: Option<f64>,
}

impl<R: Rooms> Relay<R> {
    pub fn new(rooms: Arc<R>) -> Self {
        Self::with_options(rooms, Options::default())
    }

    pub fn with_options(rooms: Arc<R>, opts: Options) -> Self {
        Self {
            rooms,
            opts,
            subs: HashMap::new(),
            by_endpoint: HashMap::new(),
            seats: HashMap::new(),
            dirty: HashSet::new(),
            due: HashSet::new(),
            last_state: f64::NEG_INFINITY,
            egress: (0.0, 0, 0),
            video_egress: (0.0, 0),
            fps: 1,
            calm_since: None,
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
                } else if let Some(want) = parse_video_subscribe(data) {
                    self.video_subscribe(from, want);
                }
            }
            Some(KIND_VOICE) => {
                if let Some((txid, opus)) = parse_voice_up(data) {
                    self.voice(now, from, &txid, opus, data.len(), &mut out);
                }
            }
            Some(KIND_VIDEO) => {
                if let Some((txid, chunk)) = parse_video_up(data) {
                    self.video(now, from, &txid, chunk, data.len(), &mut out);
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
        self.video_window(now);
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
            friends: HashSet::new(),
            seat: None,
            last_voice: None,
            talking: false,
            rate: Bucket::new(now, PACKETS_PER_SEC, BYTES_PER_SEC),
            video_want: HashSet::new(),
            video_rate: Bucket::new(now, VIDEO_PACKETS_PER_SEC, VIDEO_BYTES_PER_SEC),
        });
        if s.endpoint != from {
            self.by_endpoint.remove(&s.endpoint);
            s.endpoint = from;
        }
        self.by_endpoint.insert(from, uid.clone());
        s.last_control = now;
        // The others' state shows this one's camera.
        let seen = (s.self_flags ^ c.self_flags) & (SELF_CAMERA | SELF_ROOM_MODE) != 0;
        s.self_flags = c.self_flags;
        if !s.room_mode() {
            s.video_want.clear();
        }
        s.mutes = c.mutes.into_iter().collect();
        self.refresh_room(&uid, now);
        // The subscriber learns its room at once; the room hears about it only
        // when the binding is new (a seat change dirties the room by itself).
        match self.subs[&uid].room_id() {
            Some(r) if fresh || seen => {
                self.dirty.insert(r);
            }
            _ => {
                self.due.insert(uid);
            }
        }
    }

    /// Op 6, from a bound room-mode endpoint only.
    fn video_subscribe(&mut self, from: SocketAddr, want: Vec<u64>) {
        let Some(uid) = self.by_endpoint.get(&from) else { return };
        let s = self.subs.get_mut(uid).expect("bound");
        if s.room_mode() {
            s.video_want = want.into_iter().collect();
        }
    }

    fn voice(&mut self, now: f64, from: SocketAddr, txid: &[u8; 12], opus: &[u8], size: usize, out: &mut Datagrams) {
        let Some(uid) = self.by_endpoint.get(&from).cloned() else { return };
        if now - self.subs[&uid].room_at >= ROOM_CACHE_SECS {
            self.refresh_room(&uid, now);
        }
        let s = self.subs.get_mut(&uid).expect("bound");
        if s.self_flags & SELF_MIC_MUTED != 0 || !s.rate.take(now, size) {
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

    /// VIDEO_API.md §3: to the sender's mutual friends in its room who asked for its pictures.
    fn video(&mut self, now: f64, from: SocketAddr, txid: &[u8; 12], chunk: &[u8], size: usize, out: &mut Datagrams) {
        if !self.opts.video {
            return;
        }
        let Some(uid) = self.by_endpoint.get(&from).cloned() else { return };
        if now - self.subs[&uid].room_at >= ROOM_CACHE_SECS {
            self.refresh_room(&uid, now);
        }
        let s = self.subs.get_mut(&uid).expect("bound");
        let (Some((room, members)), Some(seat)) = (s.room.clone(), s.seat) else { return };
        if !s.room_mode() || !s.camera() || !s.video_rate.take(now, size) {
            return;
        }
        let account = s.account;
        let s = &self.subs[&uid];
        for f in &s.friends {
            let Some(l) = self.subs.get(f) else { continue };
            if l.room_id() != Some(room)
                || !members.contains(f)
                || !l.room_mode()
                || !l.friends.contains(&uid)
                || !l.video_want.contains(&account)
                || l.mutes.contains(&account)
            {
                continue;
            }
            let d = write_video_down(txid, seat, chunk);
            self.video_egress.1 += d.len();
            out.push((l.endpoint, d));
        }
    }

    /// Once a second: the frame-rate divisor from the last second's video egress.
    fn video_window(&mut self, now: f64) {
        if now - self.video_egress.0 < 1.0 {
            return;
        }
        let last = std::mem::replace(&mut self.video_egress, (now, 0)).1;
        let budget = self.opts.video_budget;
        let old = self.fps;
        if last > budget {
            self.fps = (self.fps * 2).min(4);
            self.calm_since = None;
        } else if last * 2 < budget {
            let since = *self.calm_since.get_or_insert(now);
            if now - since >= VIDEO_CALM_SECS {
                self.fps = 1;
            }
        } else {
            self.calm_since = None;
        }
        if self.fps != old {
            self.dirty.extend(self.subs.values().filter(|s| s.room_mode()).filter_map(Sub::room_id));
        }
    }

    /// Look the user's room up again; move its seat if the room changed.
    fn refresh_room(&mut self, uid: &str, now: f64) {
        let found = self.rooms.room(uid).map(|(r, m)| (r, m.into_iter().collect::<HashSet<_>>()));
        let friends: HashSet<String> = found
            .as_ref()
            .map(|(_, m)| m.iter().filter(|o| o.as_str() != uid && self.rooms.friends(uid, o)).cloned().collect())
            .unwrap_or_default();
        let s = self.subs.get_mut(uid).expect("subscribed");
        s.room_at = now;
        let old = s.room_id();
        let new = found.as_ref().map(|r| r.0);
        s.room = found;
        if s.friends != friends {
            s.friends = friends;
            if let Some(r) = new {
                self.dirty.insert(r);
            }
        }
        let s = &self.subs[uid];
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
            let seats = self.seats_of(room, now);
            let v1 = write_state(true, bundle, &seats, &rand::random());
            for (u, s) in self.subs.iter().filter(|(_, s)| s.room_id() == Some(room)) {
                let msg = if s.room_mode() { self.state_v2(u, bundle, seats.clone()) } else { v1.clone() };
                out.push((s.endpoint, msg));
            }
        }
        for uid in std::mem::take(&mut self.due) {
            let Some(s) = self.subs.get(&uid) else { continue };
            match s.room_id() {
                Some(r) if dirty.contains(&r) => {} // just sent with its room
                None if s.room_mode() => out.push((s.endpoint, self.state_v2(&uid, bundle, vec![]))),
                None => out.push((s.endpoint, write_state(false, bundle, &[], &rand::random()))),
                Some(r) => {
                    let seats = self.seats_of(r, now);
                    let msg = if s.room_mode() {
                        self.state_v2(&uid, bundle, seats)
                    } else {
                        write_state(true, bundle, &seats, &rand::random())
                    };
                    out.push((s.endpoint, msg));
                }
            }
        }
    }

    fn seats_of(&self, room: Room, now: f64) -> Vec<SeatState> {
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
        seats
    }

    /// State down v2 for room-mode subscriber `uid` (VOICE_API.md §6.3): v1's fields, then the
    /// room, the frame-rate divisor and the bound mutual friends in the room.
    fn state_v2(&self, uid: &str, bundle: u8, seats: Vec<SeatState>) -> Vec<u8> {
        let me = &self.subs[uid];
        let room = me.room_id();
        let mut members: Vec<(u8, u64, u8)> = me
            .friends
            .iter()
            .filter_map(|f| self.subs.get(f))
            .filter(|s| s.room_id() == room && s.friends.contains(uid))
            .map(|s| {
                let cam = self.opts.video && s.room_mode() && s.camera();
                (s.seat.unwrap_or(u8::MAX), s.account, MEMBER_BOUND | if cam { MEMBER_CAMERA } else { 0 })
            })
            .collect();
        members.sort_unstable();
        let st = StateV2 {
            in_room: room.is_some(),
            bundle,
            seats,
            room_id: room.map(|r| (r as u64).max(1)).unwrap_or(0),
            fps: self.fps,
            members: members.into_iter().map(|(_, a, f)| (a, f)).collect(),
        };
        write_state_v2(&st, &rand::random())
    }
}

/// The socket loop: datagrams in, the relay's answers out, a 100 ms tick.
pub async fn run<R: Rooms>(sock: UdpSocket, rooms: Arc<R>, opts: Options) -> std::io::Result<()> {
    let mut relay = Relay::with_options(rooms, opts);
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
