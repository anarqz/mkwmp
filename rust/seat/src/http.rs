//! The seat HTTP API (docs/contracts/SEAT_API.md). JSON in and out; a session
//! authenticates with `Authorization: Bearer <sessionKey>`, a lobby with one of
//! the configured lobby tokens.

use std::net::SocketAddr;
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use axum::extract::{ConnectInfo, DefaultBodyLimit, Path, Query, State};
use axum::http::{header, HeaderMap, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::{delete, get, post};
use axum::{Json, Router};
use serde::Deserialize;
use serde_json::json;

use crate::limits::{self, Limiter};
use crate::presence::{Presence, Who};
use crate::registry::{Registry, RegistryError};
use crate::roster::{Roster, RosterError};

pub const MAX_ROSTER_IDS: usize = 256;

pub type Clock = Arc<dyn Fn() -> u64 + Send + Sync>;

pub fn system_clock() -> Clock {
    Arc::new(|| SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_millis() as u64)
}

/// Concurrent PIN hashes (each ~8 MiB and a few ms of one core).
const HASH_SLOTS: usize = 4;

#[derive(Clone)]
pub struct AppState {
    roster: Arc<Mutex<Roster>>,
    registry: Arc<Registry>,
    presence: Arc<Mutex<Presence>>,
    /// Bumped on every friendship change (the friends long-poll watches it).
    friend_changes: Arc<std::sync::atomic::AtomicU64>,
    /// Argon2 hashes in flight (8 MiB each): a burst of registrations queues
    /// here instead of exhausting memory or the async workers.
    hashing: Arc<tokio::sync::Semaphore>,
    limiter: Arc<Mutex<Limiter>>,
    lobby_tokens: Arc<Vec<String>>,
    admin_token: Arc<Option<String>>,
    clock: Clock,
}

impl AppState {
    pub fn new(
        roster: Roster,
        registry: Registry,
        lobby_tokens: Vec<String>,
        admin_token: Option<String>,
        clock: Clock,
    ) -> Self {
        Self {
            roster: Arc::new(Mutex::new(roster)),
            registry: Arc::new(registry),
            presence: Arc::new(Mutex::new(Presence::default())),
            friend_changes: Arc::new(std::sync::atomic::AtomicU64::new(0)),
            hashing: Arc::new(tokio::sync::Semaphore::new(HASH_SLOTS)),
            limiter: Arc::new(Mutex::new(Limiter::default())),
            lobby_tokens: Arc::new(lobby_tokens),
            admin_token: Arc::new(admin_token),
            clock,
        }
    }

    pub fn registry(&self) -> &Arc<Registry> {
        &self.registry
    }

    /// Run a registry call that hashes or verifies a PIN on a blocking
    /// thread, at most HASH_SLOTS at once.
    async fn hashing<T: Send + 'static>(&self, f: impl FnOnce(&Registry) -> Result<T, RegistryError> + Send + 'static) -> Result<T, RegistryError> {
        let _permit = self.hashing.acquire().await.map_err(|e| RegistryError::Io(e.to_string()))?;
        let r = self.registry.clone();
        tokio::task::spawn_blocking(move || f(&r)).await.map_err(|e| RegistryError::Io(e.to_string()))?
    }

    pub fn limiter(&self) -> &Arc<Mutex<Limiter>> {
        &self.limiter
    }

    pub fn roster(&self) -> &Arc<Mutex<Roster>> {
        &self.roster
    }

    pub fn presence(&self) -> &Arc<Mutex<Presence>> {
        &self.presence
    }

    /// Anything a friends list shows changed since this number moved.
    fn epoch(&self) -> u64 {
        let r = self.roster.lock().unwrap().changes();
        let p = self.presence.lock().unwrap().changes();
        r + p + self.friend_changes.load(std::sync::atomic::Ordering::Relaxed)
    }

    fn friends_changed(&self) {
        self.friend_changes.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    }

    pub fn now(&self) -> u64 {
        (self.clock)()
    }
}

pub fn router(state: AppState) -> Router {
    Router::new()
        .route("/seat/v1/status", get(status))
        .route("/seat/v1/keys", get(keys))
        .route("/seat/v1/profiles", post(register))
        .route("/seat/v1/profiles/:name", get(profile).delete(delete_profile))
        .route("/seat/v1/admin/profiles", get(admin_list).post(admin_action))
        .route("/seat/v1/sessions", post(create))
        .route("/seat/v1/sessions/:id", get(poll).delete(leave))
        .route("/seat/v1/presence", post(presence_sign_in).delete(presence_end))
        .route("/seat/v1/friends", get(friends_list).post(friend_request))
        .route("/seat/v1/friends/:name/accept", post(friend_accept))
        .route("/seat/v1/friends/:name", delete(friend_remove))
        .route("/seat/v1/lobby/roster", post(lobby_roster))
        .layer(DefaultBodyLimit::max(16 * 1024))
        .with_state(state)
}

fn error(status: StatusCode, code: &str, message: impl ToString) -> Response {
    (status, Json(json!({"error": {"code": code, "message": message.to_string()}}))).into_response()
}

impl IntoResponse for RosterError {
    fn into_response(self) -> Response {
        let (status, code) = match &self {
            RosterError::Name(_) => (StatusCode::BAD_REQUEST, "NAME_INVALID"),
            RosterError::NameTaken => (StatusCode::CONFLICT, "NAME_TAKEN"),
            RosterError::QueueFull => (StatusCode::SERVICE_UNAVAILABLE, "QUEUE_FULL"),
            RosterError::NotFound => (StatusCode::NOT_FOUND, "SESSION_NOT_FOUND"),
        };
        let mut r = error(status, code, &self);
        if matches!(self, RosterError::QueueFull) {
            r.headers_mut().insert(header::RETRY_AFTER, "30".parse().unwrap());
        }
        r
    }
}

fn bearer(headers: &HeaderMap) -> Option<&str> {
    headers.get(header::AUTHORIZATION)?.to_str().ok()?.strip_prefix("Bearer ")
}

fn no_store(body: impl serde::Serialize) -> Response {
    ([(header::CACHE_CONTROL, "no-store")], Json(body)).into_response()
}

/// Public and read-only, so any page may read it (the project site shows it).
async fn status(State(s): State<AppState>) -> Response {
    let now = s.now();
    let mut r = no_store(s.roster.lock().unwrap().status(now));
    r.headers_mut().insert(header::ACCESS_CONTROL_ALLOW_ORIGIN, "*".parse().unwrap());
    r
}

async fn keys(State(s): State<AppState>) -> Response {
    let r = s.roster.lock().unwrap();
    let i = r.issuer();
    Json(json!({"keys": [{"kid": i.kid(), "publicKey": i.public_key_b64()}]})).into_response()
}

#[derive(Deserialize)]
struct JoinBody {
    name: String,
    #[serde(default)]
    pin: String,
}

/// The client's address. Behind the Cloudflare tunnel every request comes
/// from loopback with CF-Connecting-IP set by Cloudflare; a direct request
/// is its peer address.
pub fn client_ip(peer: SocketAddr, h: &HeaderMap) -> String {
    if peer.ip().is_loopback() {
        if let Some(ip) = h.get("cf-connecting-ip").and_then(|v| v.to_str().ok()) {
            return ip.trim().to_string();
        }
    }
    peer.ip().to_string()
}

fn too_many(wait_ms: u64) -> Response {
    let secs = wait_ms.div_ceil(1000).max(1);
    let mut r = error(
        StatusCode::TOO_MANY_REQUESTS,
        "RATE_LIMITED",
        format!("too many tries; wait {} min", secs.div_ceil(60)),
    );
    r.headers_mut().insert(header::RETRY_AFTER, secs.to_string().parse().unwrap());
    r
}

fn registry_error(e: RegistryError) -> Response {
    let (status, code) = match &e {
        RegistryError::Invalid(_) => (StatusCode::BAD_REQUEST, "NAME_INVALID"),
        RegistryError::Taken => (StatusCode::CONFLICT, "NAME_REGISTERED"),
        RegistryError::Reserved => (StatusCode::FORBIDDEN, "NAME_RESERVED"),
        RegistryError::NotFound => (StatusCode::NOT_FOUND, "PROFILE_NOT_FOUND"),
        RegistryError::WrongPin => (StatusCode::UNAUTHORIZED, "PIN_WRONG"),
        RegistryError::Friend(code, _) => {
            let status = match *code {
                "FRIEND_UNKNOWN" | "REQUEST_NOT_FOUND" => StatusCode::NOT_FOUND,
                "FRIEND_SELF" => StatusCode::BAD_REQUEST,
                _ => StatusCode::CONFLICT,
            };
            (status, *code)
        }
        RegistryError::Io(_) => (StatusCode::INTERNAL_SERVER_ERROR, "REGISTRY_ERROR"),
    };
    error(status, code, e)
}

async fn create(
    State(s): State<AppState>,
    ConnectInfo(peer): ConnectInfo<SocketAddr>,
    h: HeaderMap,
    Json(b): Json<JoinBody>,
) -> Response {
    let now = s.now();
    let ip = client_ip(peer, &h);
    let name_key = format!("name:{}", crate::names::key(&b.name));
    {
        let mut l = s.limiter.lock().unwrap();
        if let Err(w) = l.take(&format!("join:{ip}"), limits::JOIN_PER_IP, now) {
            return too_many(w);
        }
        let w = l
            .wait_ms(&format!("pinfail:{ip}"), limits::PIN_FAIL_PER_IP, now)
            .max(l.wait_ms(&name_key, limits::PIN_FAIL_PER_NAME, now));
        if w > 0 {
            return too_many(w);
        }
    }
    // The name must be registered and the PIN must match.
    let (name, pin) = (b.name.clone(), b.pin.clone());
    let login = s.hashing(move |r| r.login(&name, &pin, now / 1000)).await;
    let name = match login {
        Ok(display) => display,
        Err(RegistryError::WrongPin) => {
            let mut l = s.limiter.lock().unwrap();
            l.record(&format!("pinfail:{ip}"), limits::PIN_FAIL_PER_IP, now);
            l.record(&name_key, limits::PIN_FAIL_PER_NAME, now);
            return registry_error(RegistryError::WrongPin);
        }
        Err(e) => return registry_error(e),
    };
    match s.roster.lock().unwrap().join(&name, now) {
        Ok(c) => {
            let mut v = serde_json::to_value(&c.view).unwrap();
            v["sessionKey"] = json!(c.session_key);
            (StatusCode::CREATED, [(header::CACHE_CONTROL, "no-store")], Json(v)).into_response()
        }
        Err(e) => e.into_response(),
    }
}

#[derive(Deserialize)]
struct RegisterBody {
    name: String,
}

/// Register a free name. The PIN is in this answer only.
async fn register(
    State(s): State<AppState>,
    ConnectInfo(peer): ConnectInfo<SocketAddr>,
    h: HeaderMap,
    Json(b): Json<RegisterBody>,
) -> Response {
    let now = s.now();
    let ip = client_ip(peer, &h);
    // Only successful registrations count: 3 new names per IP per hour.
    let wait = s.limiter.lock().unwrap().wait_ms(&format!("register:{ip}"), limits::REGISTER_PER_IP, now);
    if wait > 0 {
        return too_many(wait);
    }
    let name = b.name.clone();
    let result = s.hashing(move |r| r.register(&name, now / 1000, false)).await;
    match result {
        Ok(pin) => {
            s.presence.lock().unwrap().end_name(&b.name);
            s.friends_changed();
            s.limiter.lock().unwrap().record(&format!("register:{ip}"), limits::REGISTER_PER_IP, now);
            (StatusCode::CREATED, [(header::CACHE_CONTROL, "no-store")], Json(json!({"name": b.name, "pin": pin})))
                .into_response()
        }
        Err(e) => registry_error(e),
    }
}

#[derive(Deserialize)]
struct PinBody {
    pin: String,
}

/// The owner deletes their profile (PIN required, wrong PINs rate-limited
/// like joins). Its live session, if any, ends.
async fn delete_profile(
    State(s): State<AppState>,
    ConnectInfo(peer): ConnectInfo<SocketAddr>,
    Path(name): Path<String>,
    h: HeaderMap,
    Json(b): Json<PinBody>,
) -> Response {
    let now = s.now();
    let ip = client_ip(peer, &h);
    let name_key = format!("name:{}", crate::names::key(&name));
    {
        let mut l = s.limiter.lock().unwrap();
        let w = l
            .wait_ms(&format!("pinfail:{ip}"), limits::PIN_FAIL_PER_IP, now)
            .max(l.wait_ms(&name_key, limits::PIN_FAIL_PER_NAME, now));
        if w > 0 {
            return too_many(w);
        }
    }
    let (n, pin) = (name.clone(), b.pin.clone());
    let result = s.hashing(move |r| r.delete(&n, &pin, now / 1000)).await;
    match result {
        Ok(()) => {
            s.roster.lock().unwrap().drop_name(&name, now);
            s.presence.lock().unwrap().end_name(&name);
            s.friends_changed();
            StatusCode::NO_CONTENT.into_response()
        }
        Err(RegistryError::WrongPin) => {
            let mut l = s.limiter.lock().unwrap();
            l.record(&format!("pinfail:{ip}"), limits::PIN_FAIL_PER_IP, now);
            l.record(&name_key, limits::PIN_FAIL_PER_NAME, now);
            registry_error(RegistryError::WrongPin)
        }
        Err(e) => registry_error(e),
    }
}

/// Is a name registered? (Names are public; PINs never leave a register answer.)
async fn profile(State(s): State<AppState>, Path(name): Path<String>) -> Response {
    let now = s.now() / 1000;
    let r = &s.registry;
    no_store(json!({"name": name, "registered": r.exists(&name, now), "reserved": r.is_reserved(&name)}))
}

/// Admin calls: only from this machine (loopback, not through the tunnel)
/// and with the admin token.
fn admin_ok(s: &AppState, peer: SocketAddr, h: &HeaderMap) -> bool {
    let local = peer.ip().is_loopback() && !h.contains_key("cf-connecting-ip");
    let token = s.admin_token.as_ref().as_ref();
    local && token.is_some_and(|t| bearer(h) == Some(t.as_str()))
}

async fn admin_list(State(s): State<AppState>, ConnectInfo(peer): ConnectInfo<SocketAddr>, h: HeaderMap) -> Response {
    if !admin_ok(&s, peer, &h) {
        return error(StatusCode::FORBIDDEN, "FORBIDDEN", "admin only");
    }
    let list: Vec<_> = s
        .registry
        .list()
        .into_iter()
        .map(|(name, created, last_seen, reserved)| json!({"name": name, "created": created, "lastSeen": last_seen, "reserved": reserved}))
        .collect();
    no_store(json!({"profiles": list}))
}

#[derive(Deserialize)]
struct AdminBody {
    action: String, // register | reset-pin | release
    name: String,
}

async fn admin_action(
    State(s): State<AppState>,
    ConnectInfo(peer): ConnectInfo<SocketAddr>,
    h: HeaderMap,
    Json(b): Json<AdminBody>,
) -> Response {
    if !admin_ok(&s, peer, &h) {
        return error(StatusCode::FORBIDDEN, "FORBIDDEN", "admin only");
    }
    let now = s.now() / 1000;
    let name = b.name.clone();
    let result = match b.action.as_str() {
        "register" => s.hashing(move |r| r.register(&name, now, true).map(Some)).await,
        "reset-pin" => s.hashing(move |r| r.reset_pin(&name).map(Some)).await,
        "release" => s.registry.release(&b.name).map(|_| None),
        _ => return error(StatusCode::BAD_REQUEST, "BAD_ACTION", "action: register | reset-pin | release"),
    };
    match result {
        Ok(pin) => {
            s.presence.lock().unwrap().end_name(&b.name);
            s.friends_changed();
            no_store(json!({"name": b.name, "pin": pin}))
        }
        Err(e) => registry_error(e),
    }
}

async fn poll(State(s): State<AppState>, Path(id): Path<String>, h: HeaderMap) -> Response {
    let now = s.now();
    let key = bearer(&h).unwrap_or("");
    match s.roster.lock().unwrap().touch(&id, key, now) {
        Ok(v) => no_store(v),
        Err(e) => e.into_response(),
    }
}

async fn leave(State(s): State<AppState>, Path(id): Path<String>, h: HeaderMap) -> StatusCode {
    let now = s.now();
    s.roster.lock().unwrap().leave(&id, bearer(&h).unwrap_or(""), now);
    StatusCode::NO_CONTENT
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RosterBody {
    session_ids: Vec<String>,
    /// Live lobbies per mode, e.g. {"Normal24": 3}. Optional.
    #[serde(default)]
    lobbies: Option<std::collections::BTreeMap<String, usize>>,
}

async fn lobby_roster(State(s): State<AppState>, h: HeaderMap, Json(b): Json<RosterBody>) -> Response {
    if s.lobby_tokens.is_empty() {
        return error(StatusCode::FORBIDDEN, "ROSTER_DISABLED", "no lobby tokens configured");
    }
    let ok = bearer(&h).is_some_and(|t| s.lobby_tokens.iter().any(|k| k == t));
    if !ok {
        return error(StatusCode::UNAUTHORIZED, "UNAUTHORIZED", "bad lobby token");
    }
    if b.session_ids.len() > MAX_ROSTER_IDS {
        return error(StatusCode::BAD_REQUEST, "TOO_MANY", format!("at most {MAX_ROSTER_IDS} ids"));
    }
    let now = s.now();
    let (mut sessions, boot) = {
        let mut r = s.roster.lock().unwrap();
        if let Some(l) = &b.lobbies {
            r.report_lobbies(l, now);
        }
        (r.roster(&b.session_ids, now), r.boot_id().to_string())
    };
    // Accepted friends from the registry (one query, no roster lock held),
    // then which of them are seated.
    let keys: Vec<String> = sessions.iter().filter_map(|e| e.name.as_deref().map(crate::names::key)).collect();
    let pairs = match s.registry.friends_among(&keys) {
        Ok(p) => p,
        Err(e) => return registry_error(e),
    };
    let r = s.roster.lock().unwrap();
    for e in sessions.iter_mut().filter(|e| e.alive) {
        let key = crate::names::key(e.name.as_deref().unwrap_or(""));
        let mut friends: Vec<crate::roster::RosterFriend> = pairs
            .iter()
            .filter(|(k, _)| *k == key)
            .filter_map(|(_, friend)| r.seated(friend))
            .map(|(session_id, name)| crate::roster::RosterFriend { session_id, name })
            .collect();
        friends.sort_by_key(|f| crate::names::key(&f.name));
        e.friends = friends;
    }
    no_store(json!({"bootId": boot, "sessions": sessions}))
}

// ---- launcher presence and friends ------------------------------------------

#[derive(Deserialize)]
struct PresenceBody {
    name: String,
    pin: String,
}

/// The launcher signs in with name + PIN (never takes a seat). Same PIN
/// limits as joins; the token then authenticates the friends API.
async fn presence_sign_in(
    State(s): State<AppState>,
    ConnectInfo(peer): ConnectInfo<SocketAddr>,
    h: HeaderMap,
    Json(b): Json<PresenceBody>,
) -> Response {
    let now = s.now();
    let ip = client_ip(peer, &h);
    let name_key = format!("name:{}", crate::names::key(&b.name));
    {
        let mut l = s.limiter.lock().unwrap();
        if let Err(w) = l.take(&format!("presence:{ip}"), limits::PRESENCE_PER_IP, now) {
            return too_many(w);
        }
        let w = l
            .wait_ms(&format!("pinfail:{ip}"), limits::PIN_FAIL_PER_IP, now)
            .max(l.wait_ms(&name_key, limits::PIN_FAIL_PER_NAME, now));
        if w > 0 {
            return too_many(w);
        }
    }
    let (name, pin) = (b.name.clone(), b.pin.clone());
    let login = s.hashing(move |r| r.login(&name, &pin, now / 1000).map(|n| (r.pin_hash(&n), n))).await;
    match login {
        Ok((Some(hash), display)) => {
            let token = s.presence.lock().unwrap().sign_in(&display, &hash, now);
            no_store(json!({"token": token, "name": display, "ttlMs": crate::presence::TTL_MS}))
        }
        Ok((None, _)) => registry_error(RegistryError::NotFound),
        Err(RegistryError::WrongPin) => {
            let mut l = s.limiter.lock().unwrap();
            l.record(&format!("pinfail:{ip}"), limits::PIN_FAIL_PER_IP, now);
            l.record(&name_key, limits::PIN_FAIL_PER_NAME, now);
            registry_error(RegistryError::WrongPin)
        }
        Err(e) => registry_error(e),
    }
}

/// The launcher quits: offline at once.
async fn presence_end(State(s): State<AppState>, h: HeaderMap) -> StatusCode {
    if let Some(t) = bearer(&h) {
        s.presence.lock().unwrap().end(t);
    }
    StatusCode::NO_CONTENT
}

/// The presence behind the bearer token, still bound to the profile's
/// current PIN; else 401 (sign in again).
fn me(s: &AppState, h: &HeaderMap) -> Result<(String, Who), Response> {
    let unauthorized = || error(StatusCode::UNAUTHORIZED, "PRESENCE_NOT_FOUND", "sign in again");
    let token = bearer(h).ok_or_else(unauthorized)?.to_string();
    let who = s.presence.lock().unwrap().touch(&token, s.now()).ok_or_else(unauthorized)?;
    if s.registry.pin_hash(&who.name).as_deref() != Some(who.pin_hash.as_str()) {
        s.presence.lock().unwrap().end(&token);
        return Err(unauthorized());
    }
    Ok((token, who))
}

#[derive(Deserialize)]
struct Since {
    since: Option<u64>,
}

/// How long a friends long-poll waits for a change.
const LONG_POLL_MS: u64 = 25_000;

/// Friends with their state, and requests both ways. With `?since=<cursor>`
/// from the last answer, waits (up to 25 s) until something changes.
async fn friends_list(State(s): State<AppState>, Query(q): Query<Since>, h: HeaderMap) -> Response {
    let (token, who) = match me(&s, &h) {
        Ok(x) => x,
        Err(r) => return r,
    };
    if let Err(w) = s.limiter.lock().unwrap().take(&format!("fget:{token}"), limits::FRIENDS_GET_PER_TOKEN, s.now()) {
        return too_many(w);
    }
    if let Some(since) = q.since {
        let until = s.now() + LONG_POLL_MS;
        while s.epoch() == since && s.now() < until {
            tokio::time::sleep(std::time::Duration::from_millis(500)).await;
        }
        // Keep the presence fresh across the wait.
        s.presence.lock().unwrap().touch(&token, s.now());
    }
    let cursor = s.epoch();
    let lists = match s.registry.friend_lists(&who.name) {
        Ok(l) => l,
        Err(e) => return registry_error(e),
    };
    let now = s.now();
    let (r, p) = (s.roster.lock().unwrap(), s.presence.lock().unwrap());
    let mut friends: Vec<(u8, String, &str)> = lists
        .friends
        .into_iter()
        .map(|name| {
            let state = match r.state_of(&name) {
                Some("seated") => "playing",
                Some(_) => "online",
                None if p.online(&crate::names::key(&name), now) => "online",
                None => "offline",
            };
            let rank = match state { "playing" => 0, "online" => 1, _ => 2 };
            (rank, name, state)
        })
        .collect();
    friends.sort_by_key(|(rank, name, _)| (*rank, crate::names::key(name)));
    let list = |v: Vec<(String, u64)>| v.into_iter().map(|(name, since)| json!({"name": name, "since": since})).collect::<Vec<_>>();
    no_store(json!({
        "cursor": cursor,
        "friends": friends.into_iter().map(|(_, name, state)| json!({"name": name, "state": state})).collect::<Vec<_>>(),
        "incoming": list(lists.incoming),
        "outgoing": list(lists.outgoing),
    }))
}

#[derive(Deserialize)]
struct FriendBody {
    name: String,
}

/// Ask someone to be friends (or accept, if they asked first).
async fn friend_request(State(s): State<AppState>, h: HeaderMap, Json(b): Json<FriendBody>) -> Response {
    let (_, who) = match me(&s, &h) {
        Ok(x) => x,
        Err(r) => return r,
    };
    let now = s.now();
    if let Err(w) = s.limiter.lock().unwrap().take(&format!("fadd:{}", who.key), limits::FRIEND_ADD_PER_NAME, now) {
        return too_many(w);
    }
    match s.registry.request_friend(&who.name, b.name.trim(), now / 1000) {
        Ok((outcome, name)) => {
            s.friends_changed();
            let state = match outcome {
                crate::registry::Requested::Pending => "pending",
                crate::registry::Requested::Accepted => "accepted",
            };
            no_store(json!({"name": name, "state": state}))
        }
        Err(RegistryError::NotFound) => error(StatusCode::UNAUTHORIZED, "PRESENCE_NOT_FOUND", "sign in again"),
        Err(e) => registry_error(e),
    }
}

fn friend_act(s: &AppState, who: &Who) -> Result<(), Response> {
    s.limiter.lock().unwrap().take(&format!("fact:{}", who.key), limits::FRIEND_ACT_PER_NAME, s.now()).map_err(too_many)
}

async fn friend_accept(State(s): State<AppState>, Path(name): Path<String>, h: HeaderMap) -> Response {
    let (_, who) = match me(&s, &h) {
        Ok(x) => x,
        Err(r) => return r,
    };
    if let Err(r) = friend_act(&s, &who) {
        return r;
    }
    match s.registry.accept_friend(&who.name, &name, s.now() / 1000) {
        Ok(()) => {
            s.friends_changed();
            StatusCode::NO_CONTENT.into_response()
        }
        Err(e) => registry_error(e),
    }
}

/// Reject a request, cancel yours, or remove a friend.
async fn friend_remove(State(s): State<AppState>, Path(name): Path<String>, h: HeaderMap) -> Response {
    let (_, who) = match me(&s, &h) {
        Ok(x) => x,
        Err(r) => return r,
    };
    if let Err(r) = friend_act(&s, &who) {
        return r;
    }
    match s.registry.remove_friend(&who.name, &name) {
        Ok(()) => {
            s.friends_changed();
            StatusCode::NO_CONTENT.into_response()
        }
        Err(e) => registry_error(e),
    }
}
