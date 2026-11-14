//! The launcher's local API: the game (the probe) asks for a seat here.
//!
//!   POST http://127.0.0.1:<local_port>/local/v1/seat
//!   Authorization: Bearer <launcher_token from mkwmp.toml>
//!
//! 200 {"state":"seated","ticket","lobby","name"}
//! 200 {"state":"queued","position","seatsTotal"}
//! 409 {"state":"refused","message"}   (fix client.toml; retrying won't help)
//! 503 {"state":"unavailable","message"} (server unreachable; transient)
//! 401 wrong or missing token
//!
//! The call is idempotent and doubles as the keepalive: in seat = "online"
//! mode the seat is released after 90 s without one.
//!
//!   GET http://127.0.0.1:<local_port>/local/v1/friends   (same token)
//!
//! 200 {"friends":[{"name":"Ana"}, ...]}: every accepted friend (stored on
//! the server), playing and online first; at most 100. The game reads its
//! system friend list once per sign-in, so offline friends are there too;
//! who's online in the game comes from the lobby's presence.
//! 503 before PLAY; 401 as above.

use std::sync::Arc;

use axum::extract::State;
use axum::http::{header, HeaderMap, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post};
use axum::{Json, Router};
use serde_json::json;

use crate::core::Core;
use crate::seat::Answer;

pub fn router(core: Arc<Core>) -> Router {
    Router::new().route("/local/v1/seat", post(want)).route("/local/v1/friends", get(friends)).with_state(core)
}

fn authorized(core: &Core, h: &HeaderMap) -> bool {
    h.get(header::AUTHORIZATION)
        .and_then(|v| v.to_str().ok())
        .and_then(|v| v.strip_prefix("Bearer "))
        .is_some_and(|t| t == core.token())
}

fn unauthorized() -> Response {
    (StatusCode::UNAUTHORIZED, Json(json!({"state": "unauthorized"}))).into_response()
}

const MAX_FRIENDS: usize = 100;

async fn friends(State(core): State<Arc<Core>>, h: HeaderMap) -> Response {
    if !authorized(&core, &h) {
        return unauthorized();
    }
    if core.seat().await.is_none() {
        let m = "press PLAY in the mkwmp launcher";
        return (StatusCode::SERVICE_UNAVAILABLE, Json(json!({"state": "unavailable", "message": m}))).into_response();
    }
    // The server already orders them: playing, online, offline; by name.
    let all = core.ui().friends.list.map(|f| f.friends).unwrap_or_default();
    let list: Vec<_> = all.into_iter().take(MAX_FRIENDS).map(|f| json!({"name": f.name})).collect();
    Json(json!({"friends": list})).into_response()
}

async fn want(State(core): State<Arc<Core>>, h: HeaderMap) -> Response {
    if !authorized(&core, &h) {
        return unauthorized();
    }
    let Some(seat) = core.seat().await else {
        let m = "press PLAY in the mkwmp launcher";
        return (StatusCode::SERVICE_UNAVAILABLE, Json(json!({"state": "unavailable", "message": m}))).into_response();
    };
    match seat.want().await {
        Answer::Seated { ticket, lobby, name } => {
            Json(json!({"state": "seated", "ticket": ticket, "lobby": lobby, "name": name})).into_response()
        }
        Answer::Queued { position, seats_total } => {
            Json(json!({"state": "queued", "position": position, "seatsTotal": seats_total})).into_response()
        }
        Answer::Refused(m) => (StatusCode::CONFLICT, Json(json!({"state": "refused", "message": m}))).into_response(),
        Answer::Unavailable(m) => {
            (StatusCode::SERVICE_UNAVAILABLE, Json(json!({"state": "unavailable", "message": m}))).into_response()
        }
    }
}
