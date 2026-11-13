//! The seat HTTP API end to end, on a fake clock: join, queue, heartbeat,
//! promotion, friends and the lobby roster.

use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;

use std::net::SocketAddr;

use axum::body::Body;
use axum::extract::connect_info::MockConnectInfo;
use axum::http::{Request, StatusCode};
use ed25519_dalek::SigningKey;
use http_body_util::BodyExt;
use seat::http::{router, AppState};
use seat::registry::Registry;
use seat::roster::{Roster, Rules};
use serde_json::{json, Value};
use ticket::{Issuer, TicketVerifier};
use tower::ServiceExt;

struct Harness {
    app: axum::Router,
    clock: Arc<AtomicU64>,
    pins: std::sync::Mutex<std::collections::HashMap<String, String>>,
}

impl Harness {
    fn new(capacity: usize) -> Self {
        let clock = Arc::new(AtomicU64::new(1_700_000_000_000));
        let c = clock.clone();
        let rules = Rules { capacity, lobby_endpoint: "203.0.113.5:8905".into(), ..Rules::default() };
        let roster = Roster::new(rules, Issuer::new("k1", SigningKey::from_bytes(&[3; 32])));
        Self::with(roster, "203.0.113.9:5000", clock, c)
    }

    /// `peer` is the TCP peer the server sees (loopback = through the tunnel or local).
    fn with(roster: Roster, peer: &str, clock: Arc<AtomicU64>, c: Arc<AtomicU64>) -> Self {
        let registry = Registry::open(None, &["anarqz".into()], 180).unwrap();
        let state = AppState::new(
            roster,
            registry,
            vec!["lobby-secret".into()],
            Some("admin-secret".into()),
            Arc::new(move || c.load(Ordering::SeqCst)),
        );
        let peer: SocketAddr = peer.parse().unwrap();
        Self { app: router(state).layer(MockConnectInfo(peer)), clock, pins: Default::default() }
    }

    fn advance(&self, ms: u64) {
        self.clock.fetch_add(ms, Ordering::SeqCst);
    }

    async fn call(&self, method: &str, uri: &str, bearer: Option<&str>, body: Option<Value>) -> (StatusCode, Value) {
        self.call_h(method, uri, bearer, body, &[]).await
    }

    async fn call_h(
        &self,
        method: &str,
        uri: &str,
        bearer: Option<&str>,
        body: Option<Value>,
        headers: &[(&str, &str)],
    ) -> (StatusCode, Value) {
        let mut req = Request::builder().method(method).uri(uri);
        for (k, v) in headers {
            req = req.header(*k, *v);
        }
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

    /// Register (once) then join with the PIN.
    async fn join(&self, name: &str, friends: &[&str]) -> (StatusCode, Value) {
        let key = name.to_ascii_lowercase();
        let known = self.pins.lock().unwrap().get(&key).cloned();
        let pin = match known {
            Some(p) => p,
            None => {
                let (st, v) = self.call("POST", "/seat/v1/profiles", None, Some(json!({"name": name}))).await;
                if st != StatusCode::CREATED {
                    return (st, v);
                }
                let p = v["pin"].as_str().unwrap().to_string();
                self.pins.lock().unwrap().insert(key, p.clone());
                p
            }
        };
        self.call("POST", "/seat/v1/sessions", None, Some(json!({"name": name, "pin": pin, "friends": friends}))).await
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

    let (st, e) = h.join("ana", &[]).await; // same profile, already online
    assert_eq!((st, e["error"]["code"].as_str()), (StatusCode::CONFLICT, Some("NAME_TAKEN")));
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

fn harness_at(peer: &str) -> Harness {
    let clock = Arc::new(AtomicU64::new(1_700_000_000_000));
    let c = clock.clone();
    let roster = Roster::new(Rules::default(), Issuer::new("k1", SigningKey::from_bytes(&[3; 32])));
    Harness::with(roster, peer, clock, c)
}

#[tokio::test]
async fn profiles_register_once_and_need_the_pin() {
    let h = Harness::new(5);
    let (st, p) = h.call("GET", "/seat/v1/profiles/Ana", None, None).await;
    assert_eq!((st, p["registered"].as_bool()), (StatusCode::OK, Some(false)));
    let (st, r) = h.call("POST", "/seat/v1/profiles", None, Some(json!({"name": "Ana"}))).await;
    assert_eq!(st, StatusCode::CREATED);
    let pin = r["pin"].as_str().unwrap().to_string();
    assert_eq!(pin.len(), 6);
    let (_, p) = h.call("GET", "/seat/v1/profiles/ana", None, None).await;
    assert_eq!(p["registered"], true);

    // Someone else tries the name.
    let (st, e) = h.call("POST", "/seat/v1/profiles", None, Some(json!({"name": "ANA"}))).await;
    assert_eq!((st, e["error"]["code"].as_str()), (StatusCode::CONFLICT, Some("NAME_REGISTERED")));
    let wrong = if pin == "000000" { "111111" } else { "000000" };
    let (st, e) = h.call("POST", "/seat/v1/sessions", None, Some(json!({"name": "ana", "pin": wrong}))).await;
    assert_eq!((st, e["error"]["code"].as_str()), (StatusCode::UNAUTHORIZED, Some("PIN_WRONG")));
    let (st, e) = h.call("POST", "/seat/v1/sessions", None, Some(json!({"name": "Zed", "pin": "123456"}))).await;
    assert_eq!((st, e["error"]["code"].as_str()), (StatusCode::NOT_FOUND, Some("PROFILE_NOT_FOUND")));

    // The owner, with any capitalisation, plays under the registered spelling.
    let (st, v) = h.call("POST", "/seat/v1/sessions", None, Some(json!({"name": "ANA", "pin": pin}))).await;
    assert_eq!((st, v["name"].as_str()), (StatusCode::CREATED, Some("Ana")));

    let (st, e) = h.call("POST", "/seat/v1/profiles", None, Some(json!({"name": "anarqz"}))).await;
    assert_eq!((st, e["error"]["code"].as_str()), (StatusCode::FORBIDDEN, Some("NAME_RESERVED")));
}

#[tokio::test]
async fn pin_guessing_is_rate_limited_per_name() {
    let h = Harness::new(5);
    let (_, r) = h.call("POST", "/seat/v1/profiles", None, Some(json!({"name": "Ana"}))).await;
    let pin = r["pin"].as_str().unwrap().to_string();
    let wrong = if pin == "000000" { "111111" } else { "000000" };
    for _ in 0..10 {
        let (st, _) = h.call("POST", "/seat/v1/sessions", None, Some(json!({"name": "ana", "pin": wrong}))).await;
        assert_eq!(st, StatusCode::UNAUTHORIZED);
    }
    // Locked for this name, even with the right PIN, until the window passes.
    let (st, e) = h.call("POST", "/seat/v1/sessions", None, Some(json!({"name": "ana", "pin": pin}))).await;
    assert_eq!((st, e["error"]["code"].as_str()), (StatusCode::TOO_MANY_REQUESTS, Some("RATE_LIMITED")));
    h.advance(3_600_000);
    let (st, _) = h.call("POST", "/seat/v1/sessions", None, Some(json!({"name": "ana", "pin": pin}))).await;
    assert_eq!(st, StatusCode::CREATED);
}

#[tokio::test]
async fn registrations_are_limited_per_client_ip_behind_the_tunnel() {
    let h = harness_at("127.0.0.1:40000"); // cloudflared on the same machine
    for (i, ip) in ["198.51.100.1", "198.51.100.1", "198.51.100.1", "198.51.100.1", "198.51.100.2"].iter().enumerate() {
        let name = format!("player{i}");
        let (st, _) = h
            .call_h("POST", "/seat/v1/profiles", None, Some(json!({"name": name})), &[("cf-connecting-ip", ip)])
            .await;
        let want = if i == 3 { StatusCode::TOO_MANY_REQUESTS } else { StatusCode::CREATED };
        assert_eq!(st, want, "{name} from {ip}");
    }
}

#[tokio::test]
async fn admin_only_from_this_machine_with_the_token() {
    let remote = harness_at("203.0.113.9:5000");
    let body = json!({"action": "register", "name": "anarqz"});
    let (st, _) = remote.call("POST", "/seat/v1/admin/profiles", Some("admin-secret"), Some(body.clone())).await;
    assert_eq!(st, StatusCode::FORBIDDEN);

    let local = harness_at("127.0.0.1:40000");
    let (st, _) = local
        .call_h("POST", "/seat/v1/admin/profiles", Some("admin-secret"), Some(body.clone()), &[("cf-connecting-ip", "198.51.100.1")])
        .await;
    assert_eq!(st, StatusCode::FORBIDDEN, "through the tunnel");
    let (st, _) = local.call("POST", "/seat/v1/admin/profiles", Some("wrong"), Some(body.clone())).await;
    assert_eq!(st, StatusCode::FORBIDDEN);
    let (st, v) = local.call("POST", "/seat/v1/admin/profiles", Some("admin-secret"), Some(body)).await;
    assert_eq!(st, StatusCode::OK);
    let pin = v["pin"].as_str().unwrap();
    let (st, _) = local.call("POST", "/seat/v1/sessions", None, Some(json!({"name": "anarqz", "pin": pin}))).await;
    assert_eq!(st, StatusCode::CREATED);
    let (_, l) = local.call("GET", "/seat/v1/admin/profiles", Some("admin-secret"), None).await;
    assert_eq!(l["profiles"][0]["reserved"], true);
}

#[tokio::test]
async fn owner_deletes_profile_with_pin_and_its_session_ends() {
    let h = Harness::new(5);
    let (st, _) = h.join("Ana", &[]).await;
    assert_eq!(st, StatusCode::CREATED);
    let pin = h.pins.lock().unwrap()["ana"].clone();
    let wrong = if pin == "000000" { "111111" } else { "000000" };
    let (st, _) = h.call("DELETE", "/seat/v1/profiles/ana", None, Some(json!({"pin": wrong}))).await;
    assert_eq!(st, StatusCode::UNAUTHORIZED);
    let (st, _) = h.call("DELETE", "/seat/v1/profiles/ANA", None, Some(json!({"pin": pin}))).await;
    assert_eq!(st, StatusCode::NO_CONTENT);
    let (_, s) = h.call("GET", "/seat/v1/status", None, None).await;
    assert_eq!(s["seated"], 0);
    let (st, _) = h.call("POST", "/seat/v1/profiles", None, Some(json!({"name": "ana"}))).await;
    assert_eq!(st, StatusCode::CREATED, "the name is free again");
}
