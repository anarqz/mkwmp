//! The seat HTTP API end to end, on a fake clock: join, queue, heartbeat,
//! promotion, friends and the lobby roster.

use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;

use axum::body::Body;
use axum::http::{Request, StatusCode};
use ed25519_dalek::SigningKey;
use http_body_util::BodyExt;
use seat::http::{router, AppState};
use seat::roster::{Roster, Rules};
use serde_json::{json, Value};
use ticket::{Issuer, TicketVerifier};
use tower::ServiceExt;

struct Harness {
    app: axum::Router,
    clock: Arc<AtomicU64>,
}

impl Harness {
    fn new(capacity: usize) -> Self {
        let clock = Arc::new(AtomicU64::new(1_700_000_000_000));
        let c = clock.clone();
        let rules = Rules { capacity, lobby_endpoint: "203.0.113.5:8905".into(), ..Rules::default() };
        let roster = Roster::new(rules, Issuer::new("k1", SigningKey::from_bytes(&[3; 32])));
        let state = AppState::new(roster, vec!["lobby-secret".into()], Arc::new(move || c.load(Ordering::SeqCst)));
        Self { app: router(state), clock }
    }

    fn advance(&self, ms: u64) {
        self.clock.fetch_add(ms, Ordering::SeqCst);
    }

    async fn call(&self, method: &str, uri: &str, bearer: Option<&str>, body: Option<Value>) -> (StatusCode, Value) {
        let mut req = Request::builder().method(method).uri(uri);
        if let Some(b) = bearer {
            req = req.header("authorization", format!("Bearer {b}"));
        }
        let req = match body {
            Some(b) => req.header("content-type", "application/json").body(Body::from(b.to_string())),
            None => req.body(Body::empty()),
        }
        .unwrap();
        let res = self.app.clone().oneshot(req).await.unwrap();
        let status = res.status();
        let bytes = res.into_body().collect().await.unwrap().to_bytes();
        let v = if bytes.is_empty() { Value::Null } else { serde_json::from_slice(&bytes).unwrap() };
        (status, v)
    }

    async fn join(&self, name: &str, friends: &[&str]) -> (StatusCode, Value) {
        self.call("POST", "/seat/v1/sessions", None, Some(json!({"name": name, "friends": friends}))).await
    }
}

fn sid_key(v: &Value) -> (String, String) {
    (v["sessionId"].as_str().unwrap().into(), v["sessionKey"].as_str().unwrap().into())
}

#[tokio::test]
async fn join_queue_heartbeat_promote() {
    let h = Harness::new(1);
    let (st, a) = h.join("Ana", &["bia"]).await;
    assert_eq!(st, StatusCode::CREATED);
    assert_eq!(a["state"], "seated");
    assert_eq!(a["ticket"]["lobby"], "203.0.113.5:8905");
    assert!(a["bootId"].as_str().unwrap().len() == 16);

    // The ticket verifies with the published key.
    let (_, keys) = h.call("GET", "/seat/v1/keys", None, None).await;
    let entry = format!("{}:{}", keys["keys"][0]["kid"].as_str().unwrap(), keys["keys"][0]["publicKey"].as_str().unwrap());
    let claims = TicketVerifier::from_entries(&[entry])
        .unwrap()
        .verify(a["ticket"]["token"].as_str().unwrap(), 1_700_000_001)
        .unwrap();
    assert_eq!(claims.name, "Ana");

    let (_, b) = h.join("bia", &["ANA"]).await;
    assert_eq!((b["state"].as_str(), b["position"].as_u64()), (Some("queued"), Some(1)));
    assert!(b.get("ticket").is_none());

    let (st, _) = h.join("ana", &[]).await;
    assert_eq!(st, StatusCode::CONFLICT);
    let (st, e) = h.join("no spaces", &[]).await;
    assert_eq!((st, e["error"]["code"].as_str()), (StatusCode::BAD_REQUEST, Some("NAME_INVALID")));

    // Ana heartbeats; Bia polls; both stay.
    let (a_sid, a_key) = sid_key(&a);
    let (b_sid, b_key) = sid_key(&b);
    h.advance(20_000);
    let (st, _) = h.call("GET", &format!("/seat/v1/sessions/{a_sid}"), Some(&a_key), None).await;
    assert_eq!(st, StatusCode::OK);
    let (st, _) = h.call("GET", &format!("/seat/v1/sessions/{a_sid}"), Some("wrong"), None).await;
    assert_eq!(st, StatusCode::NOT_FOUND);

    // Ana leaves; Bia is seated on her next poll.
    let (st, _) = h.call("DELETE", &format!("/seat/v1/sessions/{a_sid}"), Some(&a_key), None).await;
    assert_eq!(st, StatusCode::NO_CONTENT);
    let (_, b2) = h.call("GET", &format!("/seat/v1/sessions/{b_sid}"), Some(&b_key), None).await;
    assert_eq!(b2["state"], "seated");
    assert!(b2["ticket"]["token"].as_str().unwrap().starts_with("mkw1."));
}

#[tokio::test]
async fn lobby_roster_reports_liveness_and_mutual_friends() {
    let h = Harness::new(5);
    let (_, a) = h.join("ana", &["Bia", "cid"]).await;
    let (_, b) = h.join("bia", &[]).await;
    let (a_sid, _) = sid_key(&a);
    let (b_sid, b_key) = sid_key(&b);

    let body = json!({"sessionIds": [a_sid, "gone"]});
    let (st, _) = h.call("POST", "/seat/v1/lobby/roster", Some("nope"), Some(body.clone())).await;
    assert_eq!(st, StatusCode::UNAUTHORIZED);

    let (st, r) = h.call("POST", "/seat/v1/lobby/roster", Some("lobby-secret"), Some(body.clone())).await;
    assert_eq!(st, StatusCode::OK);
    assert_eq!(r["sessions"][0]["alive"], true);
    assert_eq!(r["sessions"][0]["friends"], json!([]));
    assert_eq!(r["sessions"][1]["alive"], false);

    // Bia adds Ana back: now mutual.
    let (st, v) = h
        .call("PUT", &format!("/seat/v1/sessions/{b_sid}/friends"), Some(&b_key), Some(json!({"friends": ["ana"]})))
        .await;
    assert_eq!(st, StatusCode::OK);
    assert_eq!(v["friends"][0], json!({"name": "ana", "online": true, "mutual": true}));
    let (_, r) = h.call("POST", "/seat/v1/lobby/roster", Some("lobby-secret"), Some(body.clone())).await;
    assert_eq!(r["sessions"][0]["friends"], json!([{"sessionId": b_sid, "name": "bia"}]));

    // Ana goes silent past the seat timeout: the lobby sees her gone.
    h.advance(61_000);
    let (_, r) = h.call("POST", "/seat/v1/lobby/roster", Some("lobby-secret"), Some(body)).await;
    assert_eq!(r["sessions"][0]["alive"], false);
}

#[tokio::test]
async fn status_is_public() {
    let h = Harness::new(2);
    h.join("ana", &[]).await;
    let res = h
        .app
        .clone()
        .oneshot(Request::builder().uri("/seat/v1/status").body(Body::empty()).unwrap())
        .await
        .unwrap();
    assert_eq!(res.headers()["access-control-allow-origin"], "*");
    let (st, s) = h.call("GET", "/seat/v1/status", None, None).await;
    assert_eq!(st, StatusCode::OK);
    assert_eq!((s["seatsTotal"].as_u64(), s["seated"].as_u64(), s["queued"].as_u64()), (Some(2), Some(1), Some(0)));
    assert_eq!(s["lobbyEndpoint"], "203.0.113.5:8905");
}
