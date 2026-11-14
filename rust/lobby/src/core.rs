//! The [`Core`]: routes each RPC to its handler, keeps the per-stream state, and resolves the
//! caller's lobby (token -> user -> seat). One mutex guards all state; handlers never block
//! or do I/O, they only queue [`Out`]s on the transport's channels.

use std::collections::{BTreeMap, HashMap};
use std::sync::{Arc, Mutex, Weak};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use tokio::sync::mpsc::UnboundedSender;
use tracing::{debug, info, warn};

use crate::docs::{ChangeKind, Write, WriteRequest};
use crate::lobbies::{Assignment, Lobby, LobbyId, Matchmaker, ModeCounts, Policy, State as LobbyState, StreamKey, Watcher};
use crate::msgs::{self, Member, OP_CURRENT, OP_SUCCEEDED};
use crate::referee::Verdict;
use crate::users::{self, Audience, Directory, Tokens};
use crate::value::{Document, Fields, Value};
use crate::{code, Call, ConnId, CoreConfig, Out, StreamId};

pub const AUTH_SIGN_IN: &str = "/nn.npln.auth.v1.Auth/IssuePrearrangedUserToken";
pub const GS_ISSUE_TOKEN: &str = "/nn.npln.gamesync.v1.Gamesync/IssueToken";
pub const GS_REFRESH_TOKEN: &str = "/nn.npln.gamesync.v1.Gamesync/RefreshToken";
pub const GS_GET_DOCUMENT: &str = "/nn.npln.gamesync.v1.Gamesync/GetDocument";
pub const GS_WRITE_DOCUMENTS: &str = "/nn.npln.gamesync.v1.Gamesync/WriteDocuments";
pub const GS_KEEP_USER_SESSION: &str = "/nn.npln.gamesync.v1.Gamesync/KeepUserSession";
pub const MM_CREATE: &str = "/nn.npln.matchmaking.v1.Matchmaker/CreateMatchmakingTicket";
pub const MM_TRACK: &str = "/nn.npln.matchmaking.v1.Matchmaker/TrackMatchmakingTicket";
pub const MM_CANCEL: &str = "/nn.npln.matchmaking.v1.Matchmaker/CancelMatchmakingTicket";
pub const ICE_SERVER_SET: &str = "/nn.npln.matchmaking.v1.GameSessionService/AllocateIceServerSet";
pub const BULK_GET: &str = "/nn.npln.ugcstore.v1.Ugcstore/BulkGetDocuments";
pub const PRESENCE_KEEPALIVE: &str = "/nn.npln.friends.v1.PresenceService/KeepAlive";
/// Server-streaming subscriptions answered with an empty snapshot and kept open.
pub const SUBSCRIPTIONS: [&str; 3] = [
    "/nn.npln.friends.v1.PresenceService/SubscribePresences",
    "/nn.npln.friends.v1.Friends/SubscribeFriendUsers",
    "/nn.npln.maintenance.v1.MaintenanceScheduleService/SubscribeMaintenanceSchedules",
];

const GS_FIXED: &str = "docs/__gs/f";
const US_PREFIX: &str = "docs/__us/";
const PLAYER_STATIC: &str = "docs/PlayerStatic/";

/// Wall clock in seconds (injectable for tests).
pub type Clock = Arc<dyn Fn() -> f64 + Send + Sync>;

fn system_clock() -> f64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map_or(0.0, |d| d.as_secs_f64())
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Kind {
    /// Reply once the client half-closes.
    Unary,
    /// Empty snapshot at half-close, then stays open.
    Subscription,
    Track,
    KeepUserSession,
    /// Every client message gets an empty reply.
    PresenceKeepAlive,
}

fn kind_of(path: &str) -> Kind {
    match path {
        MM_TRACK => Kind::Track,
        GS_KEEP_USER_SESSION => Kind::KeepUserSession,
        PRESENCE_KEEPALIVE => Kind::PresenceKeepAlive,
        p if SUBSCRIPTIONS.contains(&p) => Kind::Subscription,
        _ => Kind::Unary,
    }
}

struct Stream {
    path: String,
    kind: Kind,
    out: UnboundedSender<Out>,
    /// User resource from the bearer token, if it was one of ours.
    user: Option<String>,
    messages: Vec<Vec<u8>>,
}

type Sends = Vec<(StreamKey, Out)>;

/// Where a caller's documents live.
#[derive(Clone, Debug, PartialEq, Eq)]
enum Scope {
    Lobby(LobbyId),
    /// Not seated: a private single-player space (the reference server's "solo" session).
    Solo(String),
}

struct State {
    cfg: CoreConfig,
    tokens: Tokens,
    verifier: ticket::TicketVerifier,
    users: Directory,
    mm: Matchmaker,
    solos: HashMap<String, Lobby>,
    streams: HashMap<StreamKey, Stream>,
    conn_user: HashMap<ConnId, String>,
    /// KeepUserSession stream -> user id (from its own `docs/__us/<uid>` listen or bearer).
    session_user: HashMap<StreamKey, String>,
}

struct Inner {
    state: Mutex<State>,
    clock: Clock,
}

/// The lobby core. Cheap to clone; all clones share one state.
#[derive(Clone)]
pub struct Core {
    inner: Arc<Inner>,
}

impl Core {
    /// Build the core. Inside a tokio runtime it also starts its own timer task (gathering,
    /// referee grace, leave detection) that stops when the last clone is dropped.
    pub fn new(cfg: CoreConfig) -> Self {
        Self::with_policy(cfg, Policy::default())
    }

    pub fn with_policy(cfg: CoreConfig, policy: Policy) -> Self {
        let core = Self::with_clock(cfg, policy, Arc::new(system_clock));
        if let Ok(rt) = tokio::runtime::Handle::try_current() {
            let weak: Weak<Inner> = Arc::downgrade(&core.inner);
            rt.spawn(async move {
                let mut every = tokio::time::interval(Duration::from_millis(500));
                loop {
                    every.tick().await;
                    let Some(inner) = weak.upgrade() else { break };
                    Core { inner }.tick();
                }
            });
        }
        core
    }

    /// Without the timer task; call [`Core::tick`] yourself (tests).
    pub fn with_clock(cfg: CoreConfig, policy: Policy, clock: Clock) -> Self {
        let verifier = ticket::TicketVerifier::from_entries(&cfg.ticket_keys).unwrap_or_else(|e| {
            warn!("ignoring invalid ticket public keys: {e}");
            ticket::TicketVerifier::default()
        });
        let secret = cfg.token_secret.clone().unwrap_or_else(|| rand::random::<[u8; 32]>().to_vec());
        let state = State {
            tokens: Tokens::new(secret),
            verifier,
            users: Directory::default(),
            mm: Matchmaker::new(policy),
            solos: HashMap::new(),
            streams: HashMap::new(),
            conn_user: HashMap::new(),
            session_user: HashMap::new(),
            cfg,
        };
        Self { inner: Arc::new(Inner { state: Mutex::new(state), clock }) }
    }

    fn run<R>(&self, f: impl FnOnce(&mut State, f64) -> R) -> R {
        let now = (self.inner.clock)();
        let mut state = self.inner.state.lock().unwrap_or_else(|p| p.into_inner());
        f(&mut state, now)
    }

    /// Request headers arrived.
    pub fn open(&self, call: &Call<'_>, out: UnboundedSender<Out>) {
        self.run(|s, now| {
            let user = call.bearer.and_then(|t| s.tokens.subject(t, now as u64));
            debug!(conn = call.conn, stream = call.stream, path = call.path, peer = %call.peer, user = ?user, "open");
            s.streams.insert(
                (call.conn, call.stream),
                Stream { path: call.path.to_owned(), kind: kind_of(call.path), out, user, messages: Vec::new() },
            );
        })
    }

    /// One complete client message (unframed).
    pub fn message(&self, conn: ConnId, stream: StreamId, body: &[u8]) {
        self.run(|s, now| {
            let key = (conn, stream);
            let Some(kind) = s.streams.get(&key).map(|st| st.kind) else { return };
            let mut sends = Sends::new();
            match kind {
                Kind::KeepUserSession => s.session_frame(key, body, now, &mut sends),
                Kind::PresenceKeepAlive => sends.push((key, Out::Message(Vec::new()))),
                _ => s.streams.get_mut(&key).expect("checked").messages.push(body.to_vec()),
            }
            s.flush(sends);
        })
    }

    /// The client finished sending (END_STREAM).
    pub fn half_closed(&self, conn: ConnId, stream: StreamId) {
        self.run(|s, now| {
            let key = (conn, stream);
            let Some(st) = s.streams.get(&key) else { return };
            let (kind, path) = (st.kind, st.path.clone());
            let body = st.messages.first().cloned().unwrap_or_default();
            let mut sends = Sends::new();
            match kind {
                Kind::Unary => s.unary(key, &path, &body, now, &mut sends),
                Kind::Subscription => sends.push((key, Out::Message(Vec::new()))),
                Kind::Track => s.track(key, &body, now, &mut sends),
                Kind::KeepUserSession | Kind::PresenceKeepAlive => {
                    sends.push((key, Out::ok()));
                    s.stream_ended(key, now);
                }
            }
            s.flush(sends);
        })
    }

    /// RST_STREAM from the client.
    pub fn reset(&self, conn: ConnId, stream: StreamId) {
        self.run(|s, now| {
            s.stream_ended((conn, stream), now);
            s.streams.remove(&(conn, stream));
        })
    }

    pub fn connection_closed(&self, conn: ConnId) {
        self.run(|s, now| {
            let keys: Vec<StreamKey> = s.streams.keys().filter(|k| k.0 == conn).copied().collect();
            for key in keys {
                s.stream_ended(key, now);
                s.streams.remove(&key);
            }
            s.conn_user.remove(&conn);
        })
    }

    /// Timers: gathering lobbies, referee grace, seats that left without a clean close.
    pub fn tick(&self) {
        self.run(|s, now| s.tick(now))
    }

    /// Lobby counts per matchmaking config (for the seat roster and status).
    pub fn counts(&self) -> BTreeMap<String, ModeCounts> {
        self.run(|s, _| s.mm.counts())
    }
}

impl State {
    fn flush(&mut self, sends: Sends) {
        for (key, out) in sends {
            let end = matches!(out, Out::End { .. });
            if let Some(st) = self.streams.get(&key) {
                let _ = st.out.send(out);
            }
            if end {
                self.streams.remove(&key);
            }
        }
    }

    /// The caller's user resource: signed in on this connection, else the bearer token.
    fn caller(&self, key: StreamKey) -> Option<String> {
        self.conn_user.get(&key.0).cloned().or_else(|| self.streams.get(&key).and_then(|s| s.user.clone()))
    }

    fn scope_of(&self, uid: &str) -> Scope {
        self.mm.lobby_of(uid).map_or_else(|| Scope::Solo(uid.to_owned()), Scope::Lobby)
    }

    fn lobby_mut(&mut self, scope: &Scope) -> Option<&mut Lobby> {
        match scope {
            Scope::Lobby(id) => self.mm.lobbies.get_mut(id),
            Scope::Solo(uid) => self.solos.get_mut(uid),
        }
    }

    /// The solo space of an unseated user, created on first use.
    fn ensure_solo(&mut self, uid: &str, now: f64) {
        if !self.solos.contains_key(uid) {
            let user = format!("tenants/current/users/{uid}");
            let ussid = self.users.number(uid);
            self.solos.insert(uid.to_owned(), Lobby::solo(&user, ussid, now));
        }
    }

    fn unary(&mut self, key: StreamKey, path: &str, body: &[u8], now: f64, sends: &mut Sends) {
        let reply = match path {
            AUTH_SIGN_IN => self.sign_in(key, body, now),
            GS_ISSUE_TOKEN | GS_REFRESH_TOKEN => Ok(self.gamesync_token(key, body, now)),
            GS_GET_DOCUMENT => Ok(self.get_document(key, body, now)),
            GS_WRITE_DOCUMENTS => self.write_documents(key, body, now, sends),
            MM_CREATE => self.create_ticket(key, body, now, sends),
            MM_CANCEL => {
                self.cancel_ticket(body, now, sends);
                Ok(Vec::new())
            }
            ICE_SERVER_SET => Ok(msgs::ice_server_set(&self.cfg.public_host, self.cfg.turn_port, now as u64)),
            BULK_GET => {
                let names = msgs::bulk_get_names(body).unwrap_or_default();
                if names.is_empty() {
                    Err(Out::status(code::INVALID_ARGUMENT, "no documents requested"))
                } else {
                    for name in &names[..names.len() - 1] {
                        sends.push((key, Out::Message(msgs::bulk_get_result(name))));
                    }
                    Ok(msgs::bulk_get_result(&names[names.len() - 1]))
                }
            }
            _ => Ok(Vec::new()),
        };
        match reply {
            Ok(message) => {
                sends.push((key, Out::Message(message)));
                sends.push((key, Out::ok()));
            }
            Err(end) => sends.push((key, end)),
        }
    }

    // ---- auth -------------------------------------------------------------------------------

    fn sign_in(&mut self, key: StreamKey, body: &[u8], now: f64) -> Result<Vec<u8>, Out> {
        let req = msgs::parse_sign_in(body).map_err(|e| Out::status(code::INVALID_ARGUMENT, e.to_string()))?;
        let token = req.token.unwrap_or_default();
        let (uid, name) = if token.starts_with(&format!("{}.", ticket::PREFIX)) {
            let name = self.ticket_name(&token, now)?;
            (users::ticket_user_id(&name), Some(name))
        } else if self.cfg.require_ticket {
            return Err(Out::status(code::UNAUTHENTICATED, "sign in through the mkwmp launcher"));
        } else {
            (users::legacy_user(if token == users::SECOND_LOCAL_TOKEN { 2 } else { 1 }), None)
        };
        let user = format!("{}/users/{uid}", req.tenant);
        let number = self.users.number(&uid);
        if let Some(name) = &name {
            self.users.set_name(&uid, name);
        }
        let access = self.tokens.mint(&user, Audience::Npln, now as u64);
        self.conn_user.insert(key.0, user.clone());
        info!(user = %uid, name = ?name, number, "signed in");
        Ok(msgs::sign_in_response(&user, &access, number))
    }

    /// Display name from a seat ticket. Verified when keys are configured (always in
    /// production); a development lobby without keys reads the name unverified.
    fn ticket_name(&self, token: &str, now: f64) -> Result<String, Out> {
        if self.cfg.ticket_keys.is_empty() && !self.cfg.require_ticket {
            return unverified_ticket_name(token).ok_or_else(|| Out::status(code::UNAUTHENTICATED, "malformed seat ticket"));
        }
        self.verifier
            .verify(token, now as u64)
            .map(|c| c.name)
            .map_err(|e| Out::status(code::UNAUTHENTICATED, format!("seat ticket: {e}")))
    }

    fn gamesync_token(&mut self, key: StreamKey, body: &[u8], now: f64) -> Vec<u8> {
        let caller = self.caller(key);
        let subject = crate::wire::str_field(body, 1).ok().flatten().or_else(|| caller.clone()).unwrap_or_default();
        let user = caller.or_else(|| {
            let id = crate::wire::str_field(body, 2).ok().flatten()?;
            uuid::Uuid::parse_str(&id).is_ok().then(|| format!("tenants/current/users/{id}"))
        });
        let access = match &user {
            Some(u) => self.tokens.mint(u, Audience::Gamesync, now as u64),
            None => format!("mkwmp-gamesync-anonymous-{}", now as u64),
        };
        msgs::gamesync_token_response(&subject, &access)
    }

    // ---- Gamesync documents -----------------------------------------------------------------

    fn caller_scope(&mut self, key: StreamKey, now: f64) -> Option<Scope> {
        let uid = self
            .session_user
            .get(&key)
            .cloned()
            .or_else(|| self.caller(key).map(|u| users::tail(&u).to_owned()))?;
        let scope = self.scope_of(&uid);
        if let Scope::Solo(uid) = &scope {
            self.ensure_solo(uid, now);
        }
        Some(scope)
    }

    fn get_document(&mut self, key: StreamKey, body: &[u8], now: f64) -> Vec<u8> {
        let name = crate::wire::str_field(body, 1).ok().flatten().unwrap_or_default();
        let Some(scope) = self.caller_scope(key, now) else { return Vec::new() };
        let (cfg, policy) = (self.cfg.clone(), self.mm.policy.clone());
        let Some(lobby) = self.lobby_mut(&scope) else { return Vec::new() };
        let doc = if name == GS_FIXED { Some(fixed_document(lobby, &cfg, &policy, now as u64)) } else { lobby.docs.get(&name).cloned() };
        debug!(%name, found = doc.is_some(), "GetDocument");
        doc.map(|d| d.encode().into_bytes()).unwrap_or_default()
    }

    fn write_documents(&mut self, key: StreamKey, body: &[u8], now: f64, sends: &mut Sends) -> Result<Vec<u8>, Out> {
        let req = WriteRequest::decode(body).map_err(|e| Out::status(code::INVALID_ARGUMENT, e.to_string()))?;
        let writer = self.caller(key).map(|u| users::tail(&u).to_owned());
        let writer_number = writer.as_deref().map_or(1, |w| self.users.number(w));
        let writer_name = writer.as_deref().and_then(|w| self.users.name(w)).map(str::to_owned);
        let number_of = |users: &mut Directory, name: &str| -> i64 {
            let id = users::tail(name);
            i64::from(if uuid::Uuid::parse_str(id).is_ok() { users.number(id) } else { writer_number })
        };
        let summary: Vec<(String, usize)> = req
            .writes
            .iter()
            .map(|w| match w {
                Write::Transform { name, count } => (name.clone(), *count),
                w => (w.name().to_owned(), 0),
            })
            .collect();
        // Transform results are stored in the document they belong to (`upcsid`).
        let mut transformed: HashMap<String, i64> = HashMap::new();
        for (name, count) in &summary {
            if *count > 0 {
                transformed.insert(name.clone(), number_of(&mut self.users, name));
            }
        }
        let response =
            msgs::write_response(&summary, |n| transformed.get(n).copied().unwrap_or(i64::from(writer_number)), now as u64);

        let scope = if writer.is_some() { self.caller_scope(key, now) } else { None };
        let Some(scope) = scope else {
            debug!(writes = req.writes.len(), "WriteDocuments from an unknown caller: acknowledged, not stored");
            return Ok(response);
        };
        let writer = writer.expect("scope implies writer");
        let lobby = self.lobby_mut(&scope).expect("scope exists");
        let members = lobby.ussids();
        for write in &req.writes {
            match write {
                Write::Update { name, fields, mask, exists } => {
                    let mut fields = fields.clone();
                    if let (Some(display), true) = (&writer_name, name.starts_with(PLAYER_STATIC)) {
                        if fields.contains("name") {
                            fields.set("name", Value::str(display)); // others see the launcher name
                        }
                    }
                    if let Some(n) = transformed.get(name) {
                        fields.set("upcsid", Value::Int(*n));
                    }
                    if let Some((doc, kind)) = lobby.docs.update(name, fields.clone(), mask.as_deref(), *exists, Some(&writer), now as u64) {
                        publish(lobby, &doc, kind, sends);
                    }
                    if let Some(v) = lobby.referee.on_write(name, &fields, &members, now as u64, now) {
                        info!(lobby = %lobby.id, result = %v.doc.name, opened = v.opened, final_ = v.final_, after = %name, "referee");
                        apply_verdict(lobby, v, sends);
                    }
                }
                Write::Delete { name } => {
                    if let Some(doc) = lobby.docs.delete(name) {
                        publish(lobby, &doc, ChangeKind::Removed, sends);
                    }
                }
                Write::Transform { .. } | Write::Other => {}
            }
        }
        if let Some(seat) = lobby.seat_mut(&writer) {
            for d in &req.deferments {
                seat.deferments.insert(d.name.clone(), d.writes.clone());
            }
        }
        lobby.refresh_state();
        release_pending(lobby, sends);
        debug!(lobby = %lobby.id, writes = req.writes.len(), stored = lobby.docs.len(), "WriteDocuments");
        Ok(response)
    }

    // ---- KeepUserSession --------------------------------------------------------------------

    fn session_frame(&mut self, key: StreamKey, body: &[u8], now: f64, sends: &mut Sends) {
        let frame = match msgs::parse_session_frame(body) {
            Ok(f) => f,
            Err(e) => {
                warn!(?key, "unparseable KeepUserSession frame: {e}");
                return;
            }
        };
        // The stream's user: its own `docs/__us/<uid>` listen, else the bearer.
        for l in &frame.listens {
            if let Some(uid) = l.target.strip_prefix(US_PREFIX).filter(|u| !u.is_empty() && !u.contains('/')) {
                self.session_user.insert(key, uid.to_owned());
            }
        }
        if !self.session_user.contains_key(&key) {
            if let Some(u) = self.caller(key) {
                self.session_user.insert(key, users::tail(&u).to_owned());
            }
        }
        let Some(uid) = self.session_user.get(&key).cloned() else {
            warn!(?key, "KeepUserSession from an unknown user");
            return;
        };
        let Some(scope) = self.caller_scope(key, now) else { return };
        let (cfg, policy) = (self.cfg.clone(), self.mm.policy.clone());
        let lobby = self.lobby_mut(&scope).expect("scope exists");
        if let Some(seat) = lobby.seat_mut(&uid) {
            seat.streams.insert(key);
            seat.connected = true;
            seat.streamless_since = None;
        }
        for tid in &frame.removed {
            lobby.watchers.retain(|w| !(w.stream == key && w.target_id == *tid));
            lobby.pending.retain(|w| !(w.stream == key && w.target_id == *tid));
        }
        for l in frame.listens {
            let w = Watcher { stream: key, session: frame.session.clone(), target_id: l.target_id, target: l.target.clone() };
            if l.target.ends_with("/__pus/") && !earlier_members_present(lobby, &uid, &l.target) {
                debug!(lobby = %lobby.id, target = %l.target, "__pus listen deferred until earlier members joined");
                lobby.pending.push(w);
                continue;
            }
            answer(lobby, &cfg, &policy, w, &uid, now as u64, sends);
        }
    }

    // ---- matchmaking ------------------------------------------------------------------------

    fn create_ticket(&mut self, key: StreamKey, body: &[u8], now: f64, sends: &mut Sends) -> Result<Vec<u8>, Out> {
        let user = self.caller(key).ok_or_else(|| Out::status(code::UNAUTHENTICATED, "not signed in"))?;
        let req = msgs::parse_ticket_request(body).ok_or_else(|| Out::status(code::INVALID_ARGUMENT, "invalid ticket"))?;
        let uid = users::tail(&user).to_owned();
        let tenant = users::tenant(&user).to_owned();
        // One search per player: a repeated request while still gathering is the same ticket;
        // anything else ends the previous search / session.
        if let Some(old) = self.mm.ticket_of(&uid) {
            let gathering = old.lobby.and_then(|l| self.mm.lobbies.get(&l)).map_or(true, |l| l.state == LobbyState::Gathering);
            if old.mode == req.mode && gathering {
                return Ok(ticket_name(&tenant, &old.id.to_string()));
            }
            let old_id = old.id;
            self.end_trackers(old_id, code::CANCELLED, sends);
            if let Some((lobby, uid)) = self.mm.cancel(old_id) {
                self.leave(lobby, &uid, "new search", now, sends);
            }
        }
        let player = req.players.first().cloned().unwrap_or_default();
        let id = self.mm.create(&user, &req.mode, player, req.players.len());
        info!(user = %uid, mode = %req.mode, ticket = %id, "ticket created");
        Ok(ticket_name(&tenant, &id.to_string()))
    }

    fn track(&mut self, key: StreamKey, body: &[u8], now: f64, sends: &mut Sends) {
        let Some(id) = msgs::parse_ticket_name(body) else {
            sends.push((key, Out::status(code::INVALID_ARGUMENT, "invalid ticket name")));
            return;
        };
        let caller = self.caller(key);
        let Some(ticket) = self.mm.tickets.get(&id).filter(|t| caller.as_deref() == Some(t.user.as_str())) else {
            sends.push((key, Out::status(code::NOT_FOUND, "no such ticket")));
            return;
        };
        let ussid = self.users.number(ticket.uid());
        if let Some(a) = self.mm.track(id, key, ussid, now) {
            self.assign(a, now, sends);
        }
    }

    fn cancel_ticket(&mut self, body: &[u8], now: f64, sends: &mut Sends) {
        let Some(id) = msgs::parse_ticket_name(body) else { return };
        // The game waits for the tracking stream to end before leaving the menu (CANCELLED).
        self.end_trackers(id, code::CANCELLED, sends);
        if let Some((lobby, uid)) = self.mm.cancel(id) {
            self.leave(lobby, &uid, "ticket cancelled", now, sends);
        }
        info!(ticket = %id, "ticket cancelled");
    }

    fn end_trackers(&mut self, id: uuid::Uuid, status: u32, sends: &mut Sends) {
        if let Some(t) = self.mm.tickets.get_mut(&id) {
            for k in t.trackers.drain() {
                sends.push((k, Out::End { grpc_status: status, message: None }));
            }
        }
    }

    /// Send match assignments; for a late joiner, also announce it to the lobby.
    fn assign(&mut self, a: Assignment, now: f64, sends: &mut Sends) {
        let Some(lobby) = self.mm.lobbies.get(&a.lobby) else { return };
        let members: Vec<(String, Vec<u8>)> = lobby
            .seats
            .iter()
            .filter_map(|s| self.mm.tickets.get(&s.ticket).map(|t| (s.user.clone(), t.player.clone())))
            .collect();
        let member_refs: Vec<Member<'_>> = members.iter().map(|(u, p)| Member { user: u, player: p }).collect();
        let gamesync = (self.cfg.gamesync_host.as_str(), self.cfg.gamesync_port);
        let mut done = Vec::new();
        for tid in &a.recipients {
            let Some(t) = self.mm.tickets.get(tid) else { continue };
            let msg = msgs::assignment(
                users::tenant(&t.user),
                &a.lobby.to_string(),
                &t.id.to_string(),
                &t.mode,
                &Member { user: &t.user, player: &t.player },
                &member_refs,
                gamesync,
                now as u64,
            );
            for k in &t.trackers {
                sends.push((*k, Out::Message(msg.clone())));
                sends.push((*k, Out::ok()));
            }
            done.push(*tid);
        }
        for tid in done {
            if let Some(t) = self.mm.tickets.get_mut(&tid) {
                t.trackers.clear();
                self.solos.remove(users::tail(&t.user));
            }
        }
        info!(lobby = %a.lobby, recipients = a.recipients.len(), members = members.len(), late = a.late, "assignment sent");
        if a.late {
            let lobby = self.mm.lobbies.get_mut(&a.lobby).expect("exists");
            for tid in &a.recipients {
                if let Some(seat) = lobby.seats.iter().find(|s| s.ticket == *tid) {
                    let doc = user_session_document(&format!("{US_PREFIX}{}", seat.ussid), seat.uid(), seat.ussid, &lobby.id.to_string(), now as u64);
                    publish(lobby, &doc, ChangeKind::Added, sends);
                }
            }
        }
    }

    // ---- leaving ----------------------------------------------------------------------------

    fn stream_ended(&mut self, key: StreamKey, now: f64) {
        let Some(kind) = self.streams.get(&key).map(|s| s.kind) else { return };
        match kind {
            Kind::Track => {
                for id in self.mm.untrack(key) {
                    info!(ticket = %id, "search ended (tracker closed)");
                }
            }
            Kind::KeepUserSession => {
                let Some(uid) = self.session_user.remove(&key) else { return };
                for lobby in self.mm.lobbies.values_mut().chain(self.solos.values_mut()) {
                    lobby.watchers.retain(|w| w.stream != key);
                    lobby.pending.retain(|w| w.stream != key);
                    if let Some(seat) = lobby.seat_mut(&uid) {
                        if seat.streams.remove(&key) && seat.streams.is_empty() {
                            seat.streamless_since = Some(now);
                        }
                    }
                }
                if !self.session_user.values().any(|u| *u == uid) {
                    self.solos.remove(&uid);
                }
            }
            _ => {}
        }
    }

    /// A seat ends (MULTI-LOBBY 2.4): run its deferments and clean up its documents, announce
    /// REMOVED, retire it from open matches, hand over the host, drop the lobby when empty.
    fn leave(&mut self, lobby_id: LobbyId, uid: &str, reason: &str, now: f64, sends: &mut Sends) {
        let (cfg, policy) = (self.cfg.clone(), self.mm.policy.clone());
        let Some(lobby) = self.mm.lobbies.get_mut(&lobby_id) else { return };
        let Some(seat) = lobby.seat(uid) else { return };
        let (ussid, streams) = (seat.ussid, seat.streams.clone());
        let deferred: Vec<Write> = seat.deferments.values().flatten().cloned().collect();
        let t = now as u64;
        for w in deferred {
            match w {
                Write::Delete { name } => {
                    if let Some(doc) = lobby.docs.delete(&name) {
                        publish(lobby, &doc, ChangeKind::Removed, sends);
                    }
                }
                Write::Update { name, fields, mask, exists } => {
                    if let Some((doc, kind)) = lobby.docs.update(&name, fields, mask.as_deref(), exists, None, t) {
                        publish(lobby, &doc, kind, sends);
                    }
                }
                _ => {}
            }
        }
        for name in lobby.docs.written_by(uid) {
            if name.starts_with("docs/@@Referee") {
                continue;
            }
            if let Some(doc) = lobby.docs.delete(&name) {
                publish(lobby, &doc, ChangeKind::Removed, sends);
            }
        }
        let gone = Document::new(format!("{US_PREFIX}{ussid}"), Fields::new(), t);
        publish(lobby, &gone, ChangeKind::Removed, sends);
        for v in lobby.referee.retire(ussid, t, now) {
            apply_verdict(lobby, v, sends);
        }
        lobby.watchers.retain(|w| !streams.contains(&w.stream));
        lobby.pending.retain(|w| !streams.contains(&w.stream));
        let was_host = lobby.host.as_deref() == Some(uid);
        let lobby_mode = lobby.mode.clone();
        self.mm.remove_seat(lobby_id, uid);
        info!(lobby = %lobby_id, mode = %lobby_mode, user = %uid, reason, "seat left");
        if let Some(lobby) = self.mm.lobbies.get_mut(&lobby_id) {
            if was_host {
                lobby.host = lobby.seats.first().map(|s| s.uid().to_owned());
                lobby.closed = true;
                let doc = fixed_document(lobby, &cfg, &policy, t);
                publish(lobby, &doc, ChangeKind::Modified, sends);
            }
            lobby.refresh_state();
            release_pending(lobby, sends);
        }
    }

    fn tick(&mut self, now: f64) {
        let mut sends = Sends::new();
        for a in self.mm.tick(now) {
            self.assign(a, now, &mut sends);
        }
        let policy = self.mm.policy.clone();
        let mut leaving = Vec::new();
        for lobby in self.mm.lobbies.values_mut() {
            for v in lobby.referee.tick(now as u64, now) {
                info!(lobby = %lobby.id, result = %v.doc.name, "referee final after grace (missing reports)");
                apply_verdict(lobby, v, &mut sends);
            }
            lobby.refresh_state();
            for seat in &lobby.seats {
                let streamless = seat.streamless_since.is_some_and(|t| now - t >= policy.leave_grace_secs);
                let never = !seat.connected && seat.assigned_at.is_some_and(|t| now - t >= policy.connect_timeout_secs);
                if streamless || never {
                    leaving.push((lobby.id, seat.uid().to_owned(), if never { "never connected" } else { "session closed" }));
                }
            }
        }
        for (lobby, uid, reason) in leaving {
            self.leave(lobby, &uid, reason, now, &mut sends);
        }
        self.flush(sends);
    }
}

// ---- lobby-level helpers (no access to other lobbies) ----------------------------------------

/// Queue a change to every listener of `lobby` whose target covers the document.
fn publish(lobby: &Lobby, doc: &Document, kind: ChangeKind, sends: &mut Sends) {
    for w in lobby.watchers.iter().filter(|w| crate::docs::covers(&w.target, &doc.name)) {
        sends.push((w.stream, Out::Message(msgs::change(&w.session, w.target_id, kind, doc))));
    }
}

fn apply_verdict(lobby: &mut Lobby, v: Verdict, sends: &mut Sends) {
    let kind = lobby.docs.put(v.doc.clone());
    publish(lobby, &v.doc, kind, sends);
    lobby.refresh_state();
}

/// Every member who joined before `uid` (and is still here) has its document in `collection`,
/// so the newcomer's snapshot contains them: the earlier member initiates the P2P handshake.
fn earlier_members_present(lobby: &Lobby, uid: &str, collection: &str) -> bool {
    let Some(me) = lobby.seat(uid) else { return true };
    let have = lobby.docs.ids_in(collection);
    lobby.seats.iter().filter(|s| s.join_seq < me.join_seq).all(|s| have.contains(&s.uid()))
}

fn release_pending(lobby: &mut Lobby, sends: &mut Sends) {
    let pending = std::mem::take(&mut lobby.pending);
    let mut still = Vec::new();
    for w in pending {
        let uid = lobby
            .seats
            .iter()
            .find(|s| s.streams.contains(&w.stream))
            .map(|s| s.uid().to_owned());
        match uid {
            Some(uid) if !earlier_members_present(lobby, &uid, &w.target) => still.push(w),
            Some(_) | None => {
                debug!(lobby = %lobby.id, target = %w.target, "deferred __pus listen released");
                let target = w.target.clone();
                lobby.watchers.push(w.clone());
                for doc in lobby.docs.matching(&target) {
                    sends.push((w.stream, Out::Message(msgs::change(&w.session, w.target_id, ChangeKind::Added, doc))));
                }
                sends.push((w.stream, Out::Message(msgs::op(&w.session, w.target_id, OP_CURRENT))));
                sends.push((w.stream, Out::Message(msgs::op(&w.session, w.target_id, OP_SUCCEEDED))));
            }
        }
    }
    lobby.pending.extend(still);
}

/// Answer one listen: its documents as ADDED, then "in sync" and "op done".
fn answer(lobby: &mut Lobby, cfg: &CoreConfig, policy: &Policy, w: Watcher, uid: &str, now: u64, sends: &mut Sends) {
    let (key, session, tid) = (w.stream, w.session.clone(), w.target_id);
    let mut docs: Vec<Document> = Vec::new();
    let pgn = lobby.id.to_string();
    if w.target == US_PREFIX {
        // Every member, named by its numeric ussid (the listener keys members by strtoul(id)).
        for s in &lobby.seats {
            docs.push(user_session_document(&format!("{US_PREFIX}{}", s.ussid), s.uid(), s.ussid, &pgn, now));
        }
    } else if w.target == "docs/__gs/" {
        docs.push(fixed_document(lobby, cfg, policy, now));
        docs.extend(lobby.docs.matching(&w.target).into_iter().filter(|d| d.name != GS_FIXED).cloned());
    } else if let Some(id) = w.target.strip_prefix(US_PREFIX).filter(|id| !id.is_empty()) {
        let ussid = lobby.seat(id).or_else(|| lobby.seat(uid)).map_or(1, |s| s.ussid);
        docs.push(user_session_document(&w.target, id, ussid, &pgn, now));
        docs.push(fixed_document(lobby, cfg, policy, now));
    } else {
        docs.extend(lobby.docs.matching(&w.target).into_iter().cloned());
    }
    for doc in &docs {
        sends.push((key, Out::Message(msgs::change(&session, tid, ChangeKind::Added, doc))));
    }
    sends.push((key, Out::Message(msgs::op(&session, tid, OP_CURRENT))));
    sends.push((key, Out::Message(msgs::op(&session, tid, OP_SUCCEEDED))));
    let single_us = w.target.starts_with(US_PREFIX) && w.target != US_PREFIX;
    if !w.target.is_empty() && !single_us {
        lobby.watchers.push(w);
    }
}

/// `__us/<id>` (SDK parser FUN_0128b2b4, game parser FUN_02255158). `st` 2 = present; the
/// member listener (FUN_02254e4c) erases members with any other state.
fn user_session_document(name: &str, uid: &str, ussid: u32, pgn: &str, now: u64) -> Document {
    let fields = Fields::new()
        .with("uid", Value::str(uid))
        .with("ussid", Value::Int(ussid.into()))
        .with("ucsid", Value::Int(ussid.into()))
        .with("upcsid", Value::Int(ussid.into()))
        .with("pgn", Value::str(pgn))
        .with("st", Value::Int(2));
    Document::new(name, fields, now)
}

/// `__gs/f` (parser FUN_01267698): gsid, addr, p (NAT probe port), mcn (mesh creator),
/// maxu, rs (shared random bytes).
fn fixed_document(lobby: &Lobby, cfg: &CoreConfig, policy: &Policy, now: u64) -> Document {
    let fields = Fields::new()
        .with("gsid", Value::str(format!("gs-{}", lobby.id)))
        .with("addr", Value::str(&cfg.public_host))
        .with("p", Value::Int(cfg.nat_port.into()))
        .with("mcn", Value::str(lobby.host.clone().unwrap_or_default()))
        .with("maxu", Value::Int(lobby.maxu(policy).into()))
        .with("rs", Value::Bytes(lobby.secret.to_vec()));
    Document::new(GS_FIXED, fields, now)
}

fn ticket_name(tenant: &str, id: &str) -> Vec<u8> {
    crate::wire::Msg::new().bytes(1, format!("{tenant}/matchmakingTickets/{id}")).into_bytes()
}

/// Name claim of a seat ticket without checking its signature (development lobbies only).
fn unverified_ticket_name(token: &str) -> Option<String> {
    use base64::Engine;
    let payload = token.split('.').nth(1)?;
    let bytes = base64::engine::general_purpose::URL_SAFE_NO_PAD.decode(payload.trim_end_matches('=')).ok()?;
    let v: serde_json::Value = serde_json::from_slice(&bytes).ok()?;
    v.get("name")?.as_str().filter(|n| !n.is_empty()).map(str::to_owned)
}

#[cfg(test)]
fn _assert_send_sync() {
    fn is<T: Send + Sync>() {}
    is::<Core>();
}
