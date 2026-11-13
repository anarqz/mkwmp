//! The seat HTTP API (docs/contracts/SEAT_API.md). JSON in and out; a session
//! authenticates with `Authorization: Bearer <sessionKey>`, a lobby with one of
//! the configured lobby tokens.

use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use axum::extract::{DefaultBodyLimit, Path, State};
use axum::http::{header, HeaderMap, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post, put};
use axum::{Json, Router};
use serde::Deserialize;
use serde_json::json;

use crate::roster::{Roster, RosterError};

pub const MAX_ROSTER_IDS: usize = 256;

pub type Clock = Arc<dyn Fn() -> u64 + Send + Sync>;

pub fn system_clock() -> Clock {
    Arc::new(|| SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_millis() as u64)
}

#[derive(Clone)]
pub struct AppState {
    roster: Arc<Mutex<Roster>>,
    lobby_tokens: Arc<Vec<String>>,
    clock: Clock,
}

impl AppState {
    pub fn new(roster: Roster, lobby_tokens: Vec<String>, clock: Clock) -> Self {
        Self { roster: Arc::new(Mutex::new(roster)), lobby_tokens: Arc::new(lobby_tokens), clock }
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

async fn status(State(s): State<AppState>) -> Response {
    let now = s.now();
    no_store(s.roster.lock().unwrap().status(now))
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
    friends: Vec<String>,
}

async fn create(State(s): State<AppState>, Json(b): Json<JoinBody>) -> Response {
    let now = s.now();
    match s.roster.lock().unwrap().join(&b.name, &b.friends, now) {
        Ok(c) => {
            let mut v = serde_json::to_value(&c.view).unwrap();
            v["sessionKey"] = json!(c.session_key);
            (StatusCode::CREATED, [(header::CACHE_CONTROL, "no-store")], Json(v)).into_response()
        }
        Err(e) => e.into_response(),
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
    let sessions = r.roster(&b.session_ids, now);
    no_store(json!({"bootId": r.boot_id(), "sessions": sessions}))
}
