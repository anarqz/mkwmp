//! The seat HTTP API (docs/contracts/SEAT_API.md). JSON in and out; a session
//! authenticates with `Authorization: Bearer <sessionKey>`, a lobby with one of
//! the configured lobby tokens.

use std::net::SocketAddr;
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use axum::extract::{ConnectInfo, DefaultBodyLimit, Path, State};
use axum::http::{header, HeaderMap, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post, put};
use axum::{Json, Router};
use serde::Deserialize;
use serde_json::json;

use crate::limits::{self, Limiter};
use crate::registry::{Registry, RegistryError};
use crate::roster::{Roster, RosterError};

pub const MAX_ROSTER_IDS: usize = 256;

pub type Clock = Arc<dyn Fn() -> u64 + Send + Sync>;

pub fn system_clock() -> Clock {
    Arc::new(|| SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_millis() as u64)
}

#[derive(Clone)]
pub struct AppState {
    roster: Arc<Mutex<Roster>>,
    registry: Arc<Mutex<Registry>>,
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
            registry: Arc::new(Mutex::new(registry)),
            limiter: Arc::new(Mutex::new(Limiter::default())),
            lobby_tokens: Arc::new(lobby_tokens),
            admin_token: Arc::new(admin_token),
            clock,
        }
    }

    pub fn registry(&self) -> &Arc<Mutex<Registry>> {
        &self.registry
    }

    pub fn limiter(&self) -> &Arc<Mutex<Limiter>> {
        &self.limiter
    }

    pub fn roster(&self) -> &Arc<Mutex<Roster>> {
        &self.roster
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
        .route("/seat/v1/sessions/:id/friends", put(set_friends))
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
    #[serde(default)]
    friends: Vec<String>,
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
    let login = s.registry.lock().unwrap().login(&b.name, &b.pin, now / 1000);
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
    match s.roster.lock().unwrap().join(&name, &b.friends, now) {
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
    let result = s.registry.lock().unwrap().register(&b.name, now / 1000, false);
    match result {
        Ok(pin) => {
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
    let result = s.registry.lock().unwrap().delete(&name, &b.pin, now / 1000);
    match result {
        Ok(()) => {
            s.roster.lock().unwrap().drop_name(&name, now);
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
    let r = s.registry.lock().unwrap();
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
        .lock()
        .unwrap()
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
    let mut r = s.registry.lock().unwrap();
    let result = match b.action.as_str() {
        "register" => r.register(&b.name, now, true).map(Some),
        "reset-pin" => r.reset_pin(&b.name).map(Some),
        "release" => r.release(&b.name).map(|_| None),
        _ => return error(StatusCode::BAD_REQUEST, "BAD_ACTION", "action: register | reset-pin | release"),
    };
    match result {
        Ok(pin) => no_store(json!({"name": b.name, "pin": pin})),
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

#[derive(Deserialize)]
struct FriendsBody {
    friends: Vec<String>,
}

async fn set_friends(
    State(s): State<AppState>,
    Path(id): Path<String>,
    h: HeaderMap,
    Json(b): Json<FriendsBody>,
) -> Response {
    let now = s.now();
    let key = bearer(&h).unwrap_or("");
    match s.roster.lock().unwrap().set_friends(&id, key, &b.friends, now) {
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
    let mut r = s.roster.lock().unwrap();
    if let Some(l) = &b.lobbies {
        r.report_lobbies(l, now);
    }
    let sessions = r.roster(&b.session_ids, now);
    no_store(json!({"bootId": r.boot_id(), "sessions": sessions}))
}
