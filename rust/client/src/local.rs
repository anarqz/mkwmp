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

use std::sync::Arc;

use axum::extract::State;
use axum::http::{header, HeaderMap, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::post;
use axum::{Json, Router};
use serde_json::json;

use crate::seat::{Answer, Seat};

#[derive(Clone)]
struct Local {
    seat: Arc<Seat>,
    token: Arc<String>,
}

pub fn router(seat: Arc<Seat>, token: String) -> Router {
    Router::new()
        .route("/local/v1/seat", post(want))
        .with_state(Local { seat, token: Arc::new(token) })
}

async fn want(State(l): State<Local>, h: HeaderMap) -> Response {
    let ok = h
        .get(header::AUTHORIZATION)
        .and_then(|v| v.to_str().ok())
        .and_then(|v| v.strip_prefix("Bearer "))
        .is_some_and(|t| t == l.token.as_str());
    if !ok {
        return (StatusCode::UNAUTHORIZED, Json(json!({"state": "unauthorized"}))).into_response();
    }
    match l.seat.want().await {
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
