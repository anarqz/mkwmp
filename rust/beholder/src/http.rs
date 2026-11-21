//! The beholder's HTTP API (BEHOLDER_API.md §2-§4) and `server beholder`.

use std::net::SocketAddr;
use std::sync::{Arc, Mutex};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use axum::body::Body;
use axum::extract::{ConnectInfo, State};
use axum::http::{header, HeaderMap, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post};
use axum::{Json, Router};
use serde_json::json;

use crate::directory::{Directory, Refusal};
use crate::wire::{Announce, Reject, MAX_BODY};

pub type Clock = Arc<dyn Fn() -> u64 + Send + Sync>;

pub fn system_clock() -> Clock {
    Arc::new(|| SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_millis() as u64)
}

#[derive(Clone)]
pub struct AppState {
    dir: Arc<Mutex<Directory>>,
    /// Believe `CF-Connecting-IP` (only behind a Cloudflare tunnel, where every peer is the tunnel).
    trust_cf: bool,
    clock: Clock,
}

impl AppState {
    pub fn new(trust_cf: bool, clock: Clock) -> Self {
        Self { dir: Arc::new(Mutex::new(Directory::default())), trust_cf, clock }
    }

    fn now(&self) -> u64 {
        (self.clock)()
    }

    fn ip(&self, peer: SocketAddr, h: &HeaderMap) -> String {
        if self.trust_cf {
            if let Some(ip) = h.get("cf-connecting-ip").and_then(|v| v.to_str().ok()).and_then(|v| v.trim().parse::<std::net::IpAddr>().ok()) {
                return ip.to_string();
            }
        }
        peer.ip().to_string()
    }
}

pub fn router(state: AppState) -> Router {
    Router::new()
        .route("/beholder/v1/announce", post(announce))
        .route("/beholder/v1/servers", get(servers))
        .route("/health", get(|| async { "ok" }))
        .with_state(state)
}

fn error(status: StatusCode, code: &str, message: &str) -> Response {
    (status, [(header::CACHE_CONTROL, "no-store")], Json(json!({"error": {"code": code, "message": message}}))).into_response()
}

fn refused(r: Refusal) -> Response {
    match r {
        Refusal::RateLimited(ms) => {
            let mut res = error(StatusCode::TOO_MANY_REQUESTS, "RATE_LIMITED", "too many requests");
            res.headers_mut().insert(header::RETRY_AFTER, ms.div_ceil(1000).max(1).to_string().parse().unwrap());
            res
        }
        Refusal::Replayed => error(StatusCode::CONFLICT, "REPLAYED", "older than this server's latest announce"),
        Refusal::TooManyKeys => error(StatusCode::FORBIDDEN, "TOO_MANY_SERVERS", "this address already lists the most servers allowed"),
        Refusal::Full => {
            let mut res = error(StatusCode::SERVICE_UNAVAILABLE, "DIRECTORY_FULL", "the directory is full; try later");
            res.headers_mut().insert(header::RETRY_AFTER, "60".parse().unwrap());
            res
        }
    }
}

async fn announce(State(s): State<AppState>, ConnectInfo(peer): ConnectInfo<SocketAddr>, h: HeaderMap, body: Body) -> Response {
    let ip = s.ip(peer, &h);
    let now = s.now();
    if let Err(r) = s.dir.lock().unwrap().admit_announce(&ip, now) {
        return refused(r);
    }
    let Ok(bytes) = axum::body::to_bytes(body, MAX_BODY).await else {
        return error(StatusCode::PAYLOAD_TOO_LARGE, "TOO_LARGE", "the body is over 4 KiB");
    };
    let Ok(a) = serde_json::from_slice::<Announce>(&bytes) else {
        return invalid("body");
    };
    match a.check(now / 1000) {
        Ok(_) => {}
        Err(Reject::Invalid(field)) => return invalid(field),
        Err(Reject::Signature) => return error(StatusCode::UNAUTHORIZED, "SIGNATURE_INVALID", "the signature does not verify"),
        Err(Reject::Stale) => return error(StatusCode::UNAUTHORIZED, "STALE", "issuedAt is more than 120 s from the beholder's clock"),
    }
    match s.dir.lock().unwrap().announce(&a, &ip, now) {
        Ok(()) => StatusCode::NO_CONTENT.into_response(),
        Err(r) => refused(r),
    }
}

fn invalid(field: &str) -> Response {
    let body = json!({"error": {"code": "REQUEST_INVALID", "message": format!("{field} breaks its rule"), "field": field}});
    (StatusCode::BAD_REQUEST, [(header::CACHE_CONTROL, "no-store")], Json(body)).into_response()
}

async fn servers(State(s): State<AppState>, ConnectInfo(peer): ConnectInfo<SocketAddr>, h: HeaderMap) -> Response {
    let ip = s.ip(peer, &h);
    let now = s.now();
    let items = {
        let mut d = s.dir.lock().unwrap();
        if let Err(r) = d.admit_list(&ip, now) {
            return refused(r);
        }
        d.list(now)
    };
    (
        [(header::CACHE_CONTROL, "public, max-age=10"), (header::ACCESS_CONTROL_ALLOW_ORIGIN, "*")],
        Json(json!({"items": items})),
    )
        .into_response()
}

/// `server beholder`: serve until Ctrl-C.
pub async fn serve(bind: &str, trust_cf: bool) -> Result<(), Box<dyn std::error::Error>> {
    let state = AppState::new(trust_cf, system_clock());
    let sweeper = state.clone();
    tokio::spawn(async move {
        let mut tick = tokio::time::interval(Duration::from_secs(10));
        let mut last = usize::MAX;
        loop {
            tick.tick().await;
            let n = {
                let mut d = sweeper.dir.lock().unwrap();
                d.sweep(sweeper.now());
                d.len()
            };
            // Counts only: never a key, endpoint or address.
            if n != last {
                println!("beholder: {n} servers listed");
                last = n;
            }
        }
    });
    let listener = tokio::net::TcpListener::bind(bind).await?;
    println!(
        "beholder on http://{bind} (client address: {})",
        if trust_cf { "CF-Connecting-IP" } else { "TCP peer" }
    );
    axum::serve(listener, router(state).into_make_service_with_connect_info::<SocketAddr>())
        .with_graceful_shutdown(async {
            let _ = tokio::signal::ctrl_c().await;
        })
        .await?;
    Ok(())
}
