//! Privacy consent on the seat API (GET /privacy, PUT /seat/v1/privacy, the PRIVACY_REQUIRED gate),
//! GET /ping, and the announcer posting to a real beholder over HTTP.

use std::net::SocketAddr;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;

use axum::body::Body;
use axum::extract::connect_info::MockConnectInfo;
use axum::http::{HeaderMap, Request, StatusCode};
use ed25519_dalek::SigningKey;
use http_body_util::BodyExt;
use seat::http::{router, AppState};
use seat::privacy::Policy;
use seat::registry::Registry;
use seat::roster::{Roster, Rules};
use serde_json::{json, Value};
use ticket::Issuer;
use tower::ServiceExt;

const NOW: u64 = 1_800_000_000_000;

fn state(policy: Policy, clock: Arc<AtomicU64>) -> AppState {
    let rules = Rules { capacity: 5, lobby_endpoint: "203.0.113.5:8905".into(), ..Rules::default() };
    let roster = Roster::new(rules, Issuer::new("k1", SigningKey::from_bytes(&[3; 32])));
    let registry = Registry::open(None, &[], 30).unwrap();
    AppState::new(roster, registry, vec![], None, Arc::new(move || clock.load(Ordering::SeqCst))).with_privacy(policy)
}

struct H {
    app: axum::Router,
}

impl H {
    fn new(policy: Policy) -> Self {
        let st = state(policy, Arc::new(AtomicU64::new(NOW)));
        Self { app: router(st).layer(MockConnectInfo("198.51.100.7:4000".parse::<SocketAddr>().unwrap())) }
    }

    async fn raw(&self, method: &str, uri: &str, bearer: Option<&str>, body: Option<Value>, headers: &[(&str, &str)]) -> (StatusCode, HeaderMap, Vec<u8>) {
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
        let (status, headers) = (res.status(), res.headers().clone());
        (status, headers, res.into_body().collect().await.unwrap().to_bytes().to_vec())
    }

    async fn call(&self, method: &str, uri: &str, bearer: Option<&str>, body: Option<Value>) -> (StatusCode, Value) {
        let (st, _, b) = self.raw(method, uri, bearer, body, &[]).await;
        (st, if b.is_empty() { Value::Null } else { serde_json::from_slice(&b).unwrap() })
    }

    /// Register `name`: its PIN.
    async fn register(&self, name: &str) -> String {
        let (st, v) = self.call("POST", "/seat/v1/profiles", None, Some(json!({"name": name}))).await;
        assert_eq!(st, StatusCode::CREATED, "{v}");
        v["pin"].as_str().unwrap().to_string()
    }

    async fn presence(&self, name: &str, pin: &str) -> Value {
        let (st, v) = self.call("POST", "/seat/v1/presence", None, Some(json!({"name": name, "pin": pin}))).await;
        assert_eq!(st, StatusCode::OK, "{v}");
        v
    }

    async fn join(&self, name: &str, pin: &str) -> (StatusCode, Value) {
        self.call("POST", "/seat/v1/sessions", None, Some(json!({"name": name, "pin": pin}))).await
    }
}

fn code(v: &Value) -> &str {
    v["error"]["code"].as_str().unwrap_or("")
}

#[tokio::test]
async fn privacy_text_etag_and_status() {
    let p = Policy::new("# Policy\n\nWe keep your gamertag.\n", false);
    let version = p.version.clone();
    let h = H::new(p);
    let (st, hd, body) = h.raw("GET", "/privacy", None, None, &[]).await;
    assert_eq!(st, StatusCode::OK);
    assert_eq!(hd["content-type"], "text/markdown; charset=utf-8");
    assert_eq!(hd["etag"].to_str().unwrap(), format!("\"{version}\""));
    assert_eq!(String::from_utf8(body).unwrap(), "# Policy\n\nWe keep your gamertag.\n");
    let etag = format!("\"{version}\"");
    let (st, _, body) = h.raw("GET", "/privacy", None, None, &[("if-none-match", &etag)]).await;
    assert_eq!((st, body.len()), (StatusCode::NOT_MODIFIED, 0));
    let (st, _, _) = h.raw("GET", "/privacy", None, None, &[("if-none-match", "\"other\"")]).await;
    assert_eq!(st, StatusCode::OK);

    let (_, s) = h.call("GET", "/seat/v1/status", None, None).await;
    assert_eq!(s["privacy"], json!({"version": version, "enforced": false}));
    let (st, _, body) = h.raw("GET", "/ping", None, None, &[]).await;
    assert_eq!((st, body.len()), (StatusCode::NO_CONTENT, 0));
}

#[tokio::test]
async fn not_enforced_reports_consent_but_lets_everyone_play() {
    let h = H::new(Policy::new("text", false));
    let pin = h.register("ana").await;
    let v = h.presence("ana", &pin).await;
    assert_eq!((v["privacyAccepted"].as_bool(), v["privacyEnforced"].as_bool()), (Some(false), Some(false)));
    assert_eq!(v["privacyVersion"].as_str().unwrap().len(), 64);
    let (st, _) = h.join("ana", &pin).await;
    assert_eq!(st, StatusCode::CREATED);
    let (st, _) = h.call("GET", "/seat/v1/friends", v["token"].as_str(), None).await;
    assert_eq!(st, StatusCode::OK);
}

#[tokio::test]
async fn enforced_consent_gates_seats_friends_and_reports() {
    let policy = Policy::new("the policy", true);
    let version = policy.version.clone();
    let h = H::new(policy);
    let pin = h.register("ana").await;
    let v = h.presence("ana", &pin).await;
    let token = v["token"].as_str().unwrap().to_string();
    let t = Some(token.as_str());
    assert_eq!(v["privacyAccepted"], false);

    // Before consent: no seat, no friends, no reports; the answer names the version to show.
    let (st, e) = h.join("ana", &pin).await;
    assert_eq!((st, code(&e)), (StatusCode::FORBIDDEN, "PRIVACY_REQUIRED"));
    assert_eq!(e["error"]["privacyVersion"], version.as_str());
    for (m, uri, body) in [
        ("GET", "/seat/v1/friends", None),
        ("POST", "/seat/v1/friends", Some(json!({"name": "bia"}))),
        ("POST", "/seat/v1/friends/bia/accept", None),
        ("GET", "/seat/v1/reports", None),
        ("POST", "/seat/v1/reports", Some(json!({"name": "bia", "reason": "other"}))),
    ] {
        let (st, e) = h.call(m, uri, t, body).await;
        assert_eq!((st, code(&e)), (StatusCode::FORBIDDEN, "PRIVACY_REQUIRED"), "{m} {uri}");
    }
    // Removing data stays allowed.
    let (st, e) = h.call("DELETE", "/seat/v1/friends/bia", t, None).await;
    assert_ne!(code(&e), "PRIVACY_REQUIRED", "{st}");
    let (st, e) = h.call("DELETE", "/seat/v1/reports", t, None).await;
    assert_ne!(code(&e), "PRIVACY_REQUIRED", "{st}");

    // A stale version, a missing one, no token.
    let (st, e) = h.call("PUT", "/seat/v1/privacy", t, Some(json!({"accepted": true, "version": "0".repeat(64)}))).await;
    assert_eq!((st, code(&e)), (StatusCode::CONFLICT, "PRIVACY_VERSION"));
    assert_eq!(e["error"]["privacyVersion"], version.as_str());
    let (st, e) = h.call("PUT", "/seat/v1/privacy", t, Some(json!({"accepted": true}))).await;
    assert_eq!((st, code(&e)), (StatusCode::BAD_REQUEST, "REQUEST_INVALID"));
    let (st, _) = h.call("PUT", "/seat/v1/privacy", None, Some(json!({"accepted": true, "version": version}))).await;
    assert_eq!(st, StatusCode::UNAUTHORIZED);

    // Accept: seats and friends open; the next sign-in says so.
    let (st, _) = h.call("PUT", "/seat/v1/privacy", t, Some(json!({"accepted": true, "version": version}))).await;
    assert_eq!(st, StatusCode::NO_CONTENT);
    assert_eq!(h.presence("ana", &pin).await["privacyAccepted"], true);
    let (st, _) = h.call("GET", "/seat/v1/friends", t, None).await;
    assert_eq!(st, StatusCode::OK);
    let (st, s) = h.join("ana", &pin).await;
    assert_eq!(st, StatusCode::CREATED);
    let (sid, key) = (s["sessionId"].as_str().unwrap(), s["sessionKey"].as_str().unwrap());
    h.call("DELETE", &format!("/seat/v1/sessions/{sid}"), Some(key), None).await;

    // Withdraw: gated again.
    let (st, _) = h.call("PUT", "/seat/v1/privacy", t, Some(json!({"accepted": false}))).await;
    assert_eq!(st, StatusCode::NO_CONTENT);
    let (st, e) = h.join("ana", &pin).await;
    assert_eq!((st, code(&e)), (StatusCode::FORBIDDEN, "PRIVACY_REQUIRED"));
    assert_eq!(h.presence("ana", &pin).await["privacyAccepted"], false);
}

#[test]
fn consent_to_an_older_text_is_not_consent() {
    let r = Registry::open(None, &[], 30).unwrap();
    r.register("ana", 100, false).unwrap();
    let old = Policy::new("v1", true);
    r.set_privacy("ana", Some(&old.version), 200).unwrap();
    assert_eq!(r.privacy("ana"), Some((old.version.clone(), 200)));
    let new = Policy::new("v2", true);
    assert_ne!(r.privacy("ana").map(|(v, _)| v), Some(new.version));
    r.set_privacy("ana", None, 300).unwrap();
    assert_eq!(r.privacy("ana"), None);
    assert!(r.set_privacy("nobody", None, 300).is_err());
}

#[test]
fn old_databases_gain_the_privacy_columns() {
    let dir = std::env::temp_dir().join(format!("mkwmp-privacy-migrate-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    let db = dir.join("registry.db");
    {
        // A 1.3.0 database: profiles without the privacy columns.
        let c = rusqlite::Connection::open(&db).unwrap();
        c.execute_batch(
            "CREATE TABLE profiles (key TEXT PRIMARY KEY, name TEXT NOT NULL, pin_hash TEXT NOT NULL, created INTEGER NOT NULL, last_seen INTEGER NOT NULL) WITHOUT ROWID;
             INSERT INTO profiles VALUES ('ana', 'Ana', 'x', 1, 1);",
        )
        .unwrap();
    }
    let r = Registry::open(Some(&db), &[], 0).unwrap();
    assert_eq!(r.privacy("ana"), None);
    r.set_privacy("ana", Some("abc"), 5).unwrap();
    drop(r);
    let r = Registry::open(Some(&db), &[], 0).unwrap();
    assert_eq!(r.privacy("Ana"), Some(("abc".into(), 5)));
    let _ = std::fs::remove_dir_all(&dir);
}

#[tokio::test]
async fn announcer_lists_this_server_in_a_real_beholder() {
    // A beholder on a loopback port, with the real clock (the announce is signed with it).
    let beholder = beholder::http::AppState::new(false, beholder::http::system_clock());
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    tokio::spawn(async move {
        axum::serve(listener, beholder::http::router(beholder).into_make_service_with_connect_info::<SocketAddr>()).await.unwrap();
    });

    let now = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_millis() as u64;
    let seat = state(Policy::default(), Arc::new(AtomicU64::new(now)));
    let settings = seat::announce::Settings {
        url: format!("http://{addr}/beholder/v1/announce"),
        endpoint: "https://mkw.example.net".into(),
        name: "Example".into(),
        region: "sa-east".into(),
        capacity: 5,
        key: SigningKey::from_bytes(&[9; 32]),
    };
    let http = reqwest::Client::new();
    let (outcome, wait) = seat::announce::once(&http, &settings, &seat).await;
    assert_eq!(outcome, "listed");
    assert!((27..=33).contains(&wait.as_secs()), "{wait:?}");

    let list: Value = http.get(format!("http://{addr}/beholder/v1/servers")).send().await.unwrap().json().await.unwrap();
    let item = &list["items"][0];
    assert_eq!(item["publicKey"], settings.public_key().as_str());
    assert_eq!(item["endpoint"], "https://mkw.example.net");
    assert_eq!((item["capacity"].as_u64(), item["seated"].as_u64(), item["queued"].as_u64()), (Some(5), Some(0), Some(0)));
    assert_eq!(item["version"], beholder::VERSION);

    // Past the key's 12 announces a minute the beholder says 429 and the announcer waits its Retry-After.
    let mut last = String::new();
    for _ in 0..12 {
        last = seat::announce::once(&http, &settings, &seat).await.0;
    }
    assert_eq!(last, "429");
}
