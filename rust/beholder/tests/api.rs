//! The beholder over HTTP on a fake clock: announce answers, the list, limits and the client address.

use std::net::SocketAddr;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;

use axum::body::Body;
use axum::extract::connect_info::MockConnectInfo;
use axum::http::{HeaderMap, Request, StatusCode};
use beholder::http::{router, AppState};
use beholder::wire::{Announce, Fields};
use ed25519_dalek::SigningKey;
use http_body_util::BodyExt;
use serde_json::Value;
use tower::ServiceExt;

const NOW_MS: u64 = 1_800_000_000_000;

struct H {
    app: axum::Router,
    clock: Arc<AtomicU64>,
}

impl H {
    fn new(trust_cf: bool, peer: &str) -> Self {
        let clock = Arc::new(AtomicU64::new(NOW_MS));
        let c = clock.clone();
        let st = AppState::new(trust_cf, Arc::new(move || c.load(Ordering::SeqCst)));
        Self { app: router(st).layer(MockConnectInfo(peer.parse::<SocketAddr>().unwrap())), clock }
    }

    fn secs(&self) -> u64 {
        self.clock.load(Ordering::SeqCst) / 1000
    }

    async fn send(&self, method: &str, uri: &str, body: Option<String>, headers: &[(&str, &str)]) -> (StatusCode, HeaderMap, Value) {
        let mut req = Request::builder().method(method).uri(uri);
        for (k, v) in headers {
            req = req.header(*k, *v);
        }
        let req = req.header("content-type", "application/json").body(body.map(Body::from).unwrap_or_default()).unwrap();
        let res = self.app.clone().oneshot(req).await.unwrap();
        let (st, hd) = (res.status(), res.headers().clone());
        let b = res.into_body().collect().await.unwrap().to_bytes();
        (st, hd, if b.is_empty() { Value::Null } else { serde_json::from_slice(&b).unwrap_or(Value::Null) })
    }

    async fn announce(&self, a: &Announce, headers: &[(&str, &str)]) -> (StatusCode, HeaderMap, Value) {
        self.send("POST", "/beholder/v1/announce", Some(serde_json::to_string(a).unwrap()), headers).await
    }

    async fn list(&self, headers: &[(&str, &str)]) -> (StatusCode, HeaderMap, Value) {
        self.send("GET", "/beholder/v1/servers", None, headers).await
    }
}

fn signed(seed: u8, seated: u32, issued_at: u64) -> Announce {
    let f = Fields {
        endpoint: format!("https://s{seed}.example.net"),
        name: format!("Server {seed}"),
        region: "eu".into(),
        version: "1.3.1".into(),
        capacity: 50,
        seated,
        queued: 0,
    };
    Announce::sign(&SigningKey::from_bytes(&[seed; 32]), &f, issued_at)
}

fn code(v: &Value) -> (&str, &str) {
    (v["error"]["code"].as_str().unwrap_or(""), v["error"]["field"].as_str().unwrap_or(""))
}

#[tokio::test]
async fn announce_then_list() {
    let h = H::new(false, "203.0.113.20:5000");
    let (st, _, _) = h.announce(&signed(1, 7, h.secs()), &[]).await;
    assert_eq!(st, StatusCode::NO_CONTENT);
    let (st, hd, v) = h.list(&[]).await;
    assert_eq!(st, StatusCode::OK);
    assert_eq!(hd["cache-control"], "public, max-age=10");
    let item = &v["items"][0];
    assert_eq!(item["name"], "Server 1");
    assert_eq!(item["seated"], 7);
    assert_eq!(item["firstSeen"], "2027-01-15T08:00:00Z");
    assert!(item.get("ip").is_none());

    // 91 s without an announce: gone.
    h.clock.fetch_add(91_000, Ordering::SeqCst);
    assert_eq!(h.list(&[]).await.2["items"], Value::Array(vec![]));
}

#[tokio::test]
async fn refusals() {
    let h = H::new(false, "203.0.113.21:5000");
    let now = h.secs();

    let mut a = signed(2, 1, now);
    a.seated = 2;
    let (st, _, v) = h.announce(&a, &[]).await;
    assert_eq!((st, code(&v).0), (StatusCode::UNAUTHORIZED, "SIGNATURE_INVALID"));

    let (st, _, v) = h.announce(&signed(2, 1, now - 121), &[]).await;
    assert_eq!((st, code(&v).0), (StatusCode::UNAUTHORIZED, "STALE"));

    let mut a = signed(2, 1, now);
    a.endpoint = "http://10.0.0.5:7000".into();
    let (st, _, v) = h.announce(&a, &[]).await;
    assert_eq!((st, code(&v)), (StatusCode::BAD_REQUEST, ("REQUEST_INVALID", "endpoint")));

    let mut body: Value = serde_json::to_value(signed(2, 1, now)).unwrap();
    body["extra"] = Value::Bool(true);
    let (st, _, v) = h.send("POST", "/beholder/v1/announce", Some(body.to_string()), &[]).await;
    assert_eq!((st, code(&v)), (StatusCode::BAD_REQUEST, ("REQUEST_INVALID", "body")));

    let big = format!("{{\"kind\":\"{}\"}}", "x".repeat(5000));
    let (st, _, v) = h.send("POST", "/beholder/v1/announce", Some(big), &[]).await;
    assert_eq!((st, code(&v).0), (StatusCode::PAYLOAD_TOO_LARGE, "TOO_LARGE"));

    // A replay of an older announce of a listed key.
    assert_eq!(h.announce(&signed(3, 1, now), &[]).await.0, StatusCode::NO_CONTENT);
    let (st, _, v) = h.announce(&signed(3, 1, now - 5), &[]).await;
    assert_eq!((st, code(&v).0), (StatusCode::CONFLICT, "REPLAYED"));
}

#[tokio::test]
async fn per_address_limits_answer_429_with_retry_after() {
    let h = H::new(false, "203.0.113.22:5000");
    for _ in 0..60 {
        assert_eq!(h.list(&[]).await.0, StatusCode::OK);
    }
    let (st, hd, v) = h.list(&[]).await;
    assert_eq!((st, code(&v).0), (StatusCode::TOO_MANY_REQUESTS, "RATE_LIMITED"));
    assert_eq!(hd["retry-after"], "60");

    // 60 announces a minute per address, valid or not (garbage is counted before parsing).
    for _ in 0..60 {
        h.send("POST", "/beholder/v1/announce", Some("{}".into()), &[]).await;
    }
    let (st, hd, _) = h.announce(&signed(4, 0, h.secs()), &[]).await;
    assert_eq!(st, StatusCode::TOO_MANY_REQUESTS);
    assert!(hd.contains_key("retry-after"));
    h.clock.fetch_add(60_000, Ordering::SeqCst);
    assert_eq!(h.announce(&signed(4, 0, h.secs()), &[]).await.0, StatusCode::NO_CONTENT);
}

#[tokio::test]
async fn cf_connecting_ip_only_behind_the_switch() {
    // Off: the header is ignored, so one peer is one address however it spoofs the header.
    let h = H::new(false, "127.0.0.1:5000");
    for i in 0..60 {
        let ip = format!("198.51.100.{i}");
        assert_eq!(h.list(&[("cf-connecting-ip", &ip)]).await.0, StatusCode::OK);
    }
    assert_eq!(h.list(&[("cf-connecting-ip", "198.51.100.200")]).await.0, StatusCode::TOO_MANY_REQUESTS);

    // On: each CF-Connecting-IP has its own budget.
    let h = H::new(true, "127.0.0.1:5000");
    for _ in 0..60 {
        h.list(&[("cf-connecting-ip", "198.51.100.1")]).await;
    }
    assert_eq!(h.list(&[("cf-connecting-ip", "198.51.100.1")]).await.0, StatusCode::TOO_MANY_REQUESTS);
    assert_eq!(h.list(&[("cf-connecting-ip", "198.51.100.2")]).await.0, StatusCode::OK);
}
