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
//!
//! Both are rate-limited (burst 10, then 1/s per endpoint): the probe calls
//! seat every <= 20 s and friends every 15 s. Over the limit: 429.
//!
//! Voice chat (docs/contracts/VOICE_API.md §4), same token, any time (no PLAY
//! needed):
//!
//!   GET  /local/v1/voice          200 {"enabled","input","output","inputGain","outputGain","mutes":[{"account","name"}]}
//!   POST /local/v1/voice/devices  {"inputs":[{"id","name"}],"outputs":[...]} as the game sees them: 204
//!   POST /local/v1/voice/mute     {"account":"<u64 decimal>","name","muted"}: 204
//!
//! GET is polled every 2 s (burst 10, 2/s); the two POSTs share a bucket
//! (burst 20, 2/s). A malformed body: 400.

use std::sync::{Arc, Mutex};
use std::time::Instant;

use axum::extract::State;
use axum::http::{header, HeaderMap, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post};
use axum::{Json, Router};
use serde_json::json;

use crate::core::Core;
use crate::seat::Answer;

pub fn router(core: Arc<Core>) -> Router {
    Router::new()
        .route("/local/v1/seat", post(want))
        .route("/local/v1/friends", get(friends))
        .route("/local/v1/voice", get(voice))
        .route("/local/v1/voice/devices", post(voice_devices))
        .route("/local/v1/voice/mute", post(voice_mute))
        .with_state(core)
}

/// A token bucket per endpoint (one client: the game on this machine).
struct Bucket {
    tokens: f64,
    at: Option<Instant>,
    burst: f64,
    per_sec: f64,
}

impl Bucket {
    const fn new(burst: f64, per_sec: f64) -> Self {
        Self { tokens: burst, at: None, burst, per_sec }
    }
}

static SEAT_LIMIT: Mutex<Bucket> = Mutex::new(Bucket::new(10.0, 1.0));
static FRIENDS_LIMIT: Mutex<Bucket> = Mutex::new(Bucket::new(10.0, 1.0));
static VOICE_LIMIT: Mutex<Bucket> = Mutex::new(Bucket::new(10.0, 2.0));
static VOICE_POST_LIMIT: Mutex<Bucket> = Mutex::new(Bucket::new(20.0, 2.0));

fn allow(bucket: &Mutex<Bucket>) -> bool {
    let mut b = bucket.lock().unwrap_or_else(|p| p.into_inner());
    let now = Instant::now();
    if let Some(at) = b.at {
        b.tokens = (b.tokens + now.duration_since(at).as_secs_f64() * b.per_sec).min(b.burst);
    }
    b.at = Some(now);
    if b.tokens >= 1.0 {
        b.tokens -= 1.0;
        true
    } else {
        false
    }
}

fn limited() -> Response {
    (StatusCode::TOO_MANY_REQUESTS, [(header::RETRY_AFTER, "1")], Json(json!({"state": "limited"}))).into_response()
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
    if !allow(&FRIENDS_LIMIT) {
        return limited();
    }
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
    if !allow(&SEAT_LIMIT) {
        return limited();
    }
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

// ---- voice ----------------------------------------------------------------------------------

/// What the launcher keeps of what the game sends.
const MAX_DEVICES: usize = 64;
const MAX_MUTES: usize = 500;
const MAX_TEXT: usize = 256;

fn bad_request(m: &str) -> Response {
    (StatusCode::BAD_REQUEST, Json(json!({"state": "invalid", "message": m}))).into_response()
}

pub fn voice_json(v: &crate::config::Voice) -> serde_json::Value {
    json!({
        "enabled": v.enabled,
        "input": v.input,
        "output": v.output,
        "inputGain": v.input_gain.clamp(1, 100),
        "outputGain": v.output_gain.clamp(1, 100),
        "mutes": v.mutes.iter().map(|m| json!({"account": m.account, "name": m.name})).collect::<Vec<_>>(),
    })
}

async fn voice(State(core): State<Arc<Core>>, h: HeaderMap) -> Response {
    if !allow(&VOICE_LIMIT) {
        return limited();
    }
    if !authorized(&core, &h) {
        return unauthorized();
    }
    Json(voice_json(&core.ui().cfg.voice)).into_response()
}

fn text(s: &str) -> String {
    s.chars().filter(|c| !c.is_control()).take(MAX_TEXT).collect()
}

/// `[{"id","name"}]`, deduplicated by id, without empty ids (that is "System default").
pub fn devices(v: &serde_json::Value) -> Option<Vec<crate::config::VoiceDevice>> {
    let mut out: Vec<crate::config::VoiceDevice> = vec![];
    for d in v.as_array()? {
        let (id, name) = (d["id"].as_str()?, d["name"].as_str().unwrap_or(""));
        let id = text(id);
        if id.is_empty() || out.iter().any(|x| x.id == id) || out.len() == MAX_DEVICES {
            continue;
        }
        let name = if name.trim().is_empty() { id.clone() } else { text(name) };
        out.push(crate::config::VoiceDevice { id, name });
    }
    Some(out)
}

async fn voice_devices(State(core): State<Arc<Core>>, h: HeaderMap, body: axum::body::Bytes) -> Response {
    if !allow(&VOICE_POST_LIMIT) {
        return limited();
    }
    if !authorized(&core, &h) {
        return unauthorized();
    }
    let Ok(v) = serde_json::from_slice::<serde_json::Value>(&body) else { return bad_request("not JSON") };
    let (Some(inputs), Some(outputs)) = (devices(&v["inputs"]), devices(&v["outputs"])) else {
        return bad_request("inputs and outputs: [{id, name}]");
    };
    match core.edit_voice(|voice| {
        voice.inputs = inputs;
        voice.outputs = outputs;
    }) {
        Ok(()) => StatusCode::NO_CONTENT.into_response(),
        Err(e) => (StatusCode::INTERNAL_SERVER_ERROR, Json(json!({"state": "error", "message": e}))).into_response(),
    }
}

/// Apply one mute or unmute to the list (by account; the newest name wins).
pub fn apply_mute(mutes: &mut Vec<crate::config::VoiceMute>, account: &str, name: &str, muted: bool) {
    mutes.retain(|m| m.account != account);
    if muted {
        mutes.push(crate::config::VoiceMute { account: account.to_string(), name: text(name) });
        if mutes.len() > MAX_MUTES {
            mutes.remove(0);
        }
    }
}

async fn voice_mute(State(core): State<Arc<Core>>, h: HeaderMap, body: axum::body::Bytes) -> Response {
    if !allow(&VOICE_POST_LIMIT) {
        return limited();
    }
    if !authorized(&core, &h) {
        return unauthorized();
    }
    let Ok(v) = serde_json::from_slice::<serde_json::Value>(&body) else { return bad_request("not JSON") };
    let account = v["account"].as_str().and_then(|a| a.parse::<u64>().ok().filter(|n| n.to_string() == a));
    let (Some(account), Some(muted)) = (account, v["muted"].as_bool()) else {
        return bad_request("account (u64 decimal string) and muted (bool)");
    };
    let name = v["name"].as_str().unwrap_or("").to_string();
    match core.edit_voice(|voice| apply_mute(&mut voice.mutes, &account.to_string(), &name, muted)) {
        Ok(()) => StatusCode::NO_CONTENT.into_response(),
        Err(e) => (StatusCode::INTERNAL_SERVER_ERROR, Json(json!({"state": "error", "message": e}))).into_response(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn voice_json_matches_the_contract() {
        let mut v = crate::config::Voice::default();
        apply_mute(&mut v.mutes, "123", "Bia", true);
        apply_mute(&mut v.mutes, "123", "Bia2", true);
        apply_mute(&mut v.mutes, "9", "Cid", true);
        apply_mute(&mut v.mutes, "9", "", false);
        assert_eq!(
            voice_json(&v),
            json!({"enabled": true, "input": "", "output": "", "inputGain": 100, "outputGain": 100,
                   "mutes": [{"account": "123", "name": "Bia2"}]})
        );
    }

    #[tokio::test]
    async fn voice_routes_over_http() {
        let path = std::env::temp_dir().join(format!("mkwmp_local_voice_{}.toml", std::process::id()));
        let core = Core::new(path.to_str().unwrap().into(), crate::config::ClientConfig::default());
        let l = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let base = format!("http://{}", l.local_addr().unwrap());
        let app = router(core.clone());
        tokio::spawn(async move { axum::serve(l, app).await });
        let http = reqwest::Client::new();
        let tok = core.token().to_string();
        assert_eq!(http.get(format!("{base}/local/v1/voice")).send().await.unwrap().status(), 401);
        let r = http.get(format!("{base}/local/v1/voice")).bearer_auth(&tok).send().await.unwrap();
        assert_eq!(r.json::<serde_json::Value>().await.unwrap()["inputGain"], 100);
        let post = |p: &str, b: serde_json::Value| http.post(format!("{base}{p}")).bearer_auth(&tok).json(&b).send();
        let r = post("/local/v1/voice/devices", json!({"inputs": [{"id": "w1", "name": "Mic"}], "outputs": []})).await.unwrap();
        assert_eq!(r.status(), 204);
        let r = post("/local/v1/voice/mute", json!({"account": "18446744073709551615", "name": "Bia", "muted": true})).await.unwrap();
        assert_eq!(r.status(), 204);
        assert_eq!(post("/local/v1/voice/mute", json!({"account": 5, "muted": true})).await.unwrap().status(), 400);
        assert_eq!(post("/local/v1/voice/mute", json!({"account": "05", "muted": true})).await.unwrap().status(), 400);
        let v: serde_json::Value =
            http.get(format!("{base}/local/v1/voice")).bearer_auth(&tok).send().await.unwrap().json().await.unwrap();
        assert_eq!(v["mutes"], json!([{"account": "18446744073709551615", "name": "Bia"}]));
        // Persisted; a window save keeps the game's lists.
        let saved = crate::config::ClientConfig::load(path.to_str().unwrap()).unwrap();
        assert_eq!((saved.voice.inputs.len(), saved.voice.mutes.len()), (1, 1));
        let mut from_window = crate::config::ClientConfig::default();
        from_window.voice.enabled = false;
        core.set_config(from_window).unwrap();
        let v = core.ui().cfg.voice;
        assert!(!v.enabled && v.mutes.len() == 1 && v.inputs.len() == 1);
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn devices_are_cleaned() {
        let d = devices(&json!([{"id": "a", "name": "Mic"}, {"id": "a", "name": "dup"}, {"id": "", "name": "x"}, {"id": "b"}]))
            .unwrap();
        assert_eq!(d.iter().map(|x| (x.id.as_str(), x.name.as_str())).collect::<Vec<_>>(), vec![("a", "Mic"), ("b", "b")]);
        assert!(devices(&json!({"id": "a"})).is_none());
    }
}
