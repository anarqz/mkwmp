//! Real HTTP/2 clients (h2c and TLS) against the gateway with a fake backend.
//!
//! Backend behaviour by path:
//! - `/echo/Unary`: replies with the concatenated messages at half-close.
//! - `/sub/Stream`: never ends by itself; echoes each message right away.
//! - `/big/Push`: on open, pushes one 200 KiB message (past the client window).

use std::collections::HashMap;
use std::net::SocketAddr;
use std::sync::atomic::AtomicU64;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use bytes::Bytes;
use gateway::{grpc, Backend, Call, ConnId, Out, StreamId};
use http::Request;
use tokio::net::{TcpListener, TcpStream};
use tokio::sync::mpsc::UnboundedSender;

#[derive(Default)]
struct Fake {
    calls: Mutex<HashMap<(ConnId, StreamId), (String, UnboundedSender<Out>, Vec<u8>)>>,
    events: Mutex<Vec<String>>,
}

impl Fake {
    fn ev(&self, e: String) {
        self.events.lock().unwrap().push(e);
    }
    fn events(&self) -> Vec<String> {
        self.events.lock().unwrap().clone()
    }
}

impl Backend for Fake {
    fn open(&self, call: &Call, out: UnboundedSender<Out>) {
        self.ev(format!("open {} {}", call.path, call.bearer.unwrap_or("-")));
        if call.path == "/big/Push" {
            let _ = out.send(Out::Message(vec![7u8; 200 * 1024]));
        }
        self.calls.lock().unwrap().insert((call.conn, call.stream), (call.path.to_string(), out, vec![]));
    }
    fn message(&self, conn: ConnId, stream: StreamId, body: &[u8]) {
        self.ev(format!("message {}", body.len()));
        let mut c = self.calls.lock().unwrap();
        let (path, out, acc) = c.get_mut(&(conn, stream)).unwrap();
        if path == "/sub/Stream" {
            let _ = out.send(Out::Message(body.to_vec()));
        } else {
            acc.extend_from_slice(body);
        }
    }
    fn half_closed(&self, conn: ConnId, stream: StreamId) {
        self.ev("half_closed".into());
        let c = self.calls.lock().unwrap();
        let (path, out, acc) = c.get(&(conn, stream)).unwrap();
        if path == "/echo/Unary" {
            let _ = out.send(Out::Message(acc.clone()));
            let _ = out.send(Out::End { grpc_status: 0, message: None });
        }
    }
    fn reset(&self, conn: ConnId, stream: StreamId) {
        self.ev("reset".into());
        self.calls.lock().unwrap().remove(&(conn, stream));
    }
    fn connection_closed(&self, _conn: ConnId) {
        self.ev("connection_closed".into());
    }
}

async fn start(tls: Option<tokio_rustls::TlsAcceptor>) -> (Arc<Fake>, SocketAddr) {
    let fake = Arc::new(Fake::default());
    let l = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = l.local_addr().unwrap();
    tokio::spawn(gateway::h2conn::accept_loop(l, tls, fake.clone(), Arc::new(AtomicU64::new(1))));
    (fake, addr)
}

async fn client<T>(io: T) -> h2::client::SendRequest<Bytes>
where
    T: tokio::io::AsyncRead + tokio::io::AsyncWrite + Unpin + Send + 'static,
{
    let (send, conn) = h2::client::Builder::new()
        .initial_window_size(65_535) // the default small window
        .handshake(io)
        .await
        .unwrap();
    tokio::spawn(async move {
        let _ = conn.await;
    });
    send
}

fn req(path: &str) -> Request<()> {
    Request::builder()
        .method("POST")
        .uri(format!("https://lobby.test{path}"))
        .header("content-type", "application/grpc")
        .header("authorization", "Bearer tok123")
        .body(())
        .unwrap()
}

async fn wait_for(fake: &Fake, what: &str) {
    for _ in 0..100 {
        if fake.events().iter().any(|e| e == what) {
            return;
        }
        tokio::time::sleep(Duration::from_millis(20)).await;
    }
    panic!("never saw {what:?}; events: {:?}", fake.events());
}

async fn read_all(body: &mut h2::RecvStream) -> Vec<u8> {
    let mut all = vec![];
    while let Some(d) = body.data().await {
        let d = d.unwrap();
        let _ = body.flow_control().release_capacity(d.len());
        all.extend_from_slice(&d);
    }
    all
}

#[tokio::test]
async fn unary_over_h2c() {
    let (fake, addr) = start(None).await;
    let mut h2 = client(TcpStream::connect(addr).await.unwrap()).await;
    let (resp, mut send) = h2.send_request(req("/echo/Unary"), false).unwrap();
    // Two messages, the first split across DATA frames.
    let mut wire = grpc::frame(b"hello ");
    wire.extend(grpc::frame(b"world"));
    send.send_data(Bytes::copy_from_slice(&wire[..4]), false).unwrap();
    send.send_data(Bytes::copy_from_slice(&wire[4..]), true).unwrap();
    let resp = resp.await.unwrap();
    assert_eq!(resp.status(), 200);
    assert_eq!(resp.headers()["content-type"], "application/grpc");
    let mut body = resp.into_body();
    let data = read_all(&mut body).await;
    assert_eq!(data, grpc::frame(b"hello world"));
    let trailers = body.trailers().await.unwrap().unwrap();
    assert_eq!(trailers["grpc-status"], "0");
    assert_eq!(fake.events()[..4], ["open /echo/Unary tok123", "message 6", "message 5", "half_closed"]);
}

#[tokio::test]
async fn subscription_headers_come_first_then_reset_reaches_backend() {
    let (fake, addr) = start(None).await;
    let mut h2 = client(TcpStream::connect(addr).await.unwrap()).await;
    let (resp, mut send) = h2.send_request(req("/sub/Stream"), false).unwrap();
    // Headers arrive with no data sent by either side.
    let resp = tokio::time::timeout(Duration::from_secs(2), resp).await.expect("headers first").unwrap();
    let mut body = resp.into_body();
    send.send_data(Bytes::from(grpc::frame(b"ping")), false).unwrap();
    let d = tokio::time::timeout(Duration::from_secs(2), body.data()).await.unwrap().unwrap().unwrap();
    assert_eq!(&d[..], &grpc::frame(b"ping")[..]);
    send.send_reset(h2::Reason::CANCEL);
    wait_for(&fake, "reset").await;
    // The connection lives on: a second call works.
    let (resp, mut send) = h2.send_request(req("/echo/Unary"), false).unwrap();
    send.send_data(Bytes::from(grpc::frame(b"x")), true).unwrap();
    assert_eq!(resp.await.unwrap().status(), 200);
    drop(h2);
}

#[tokio::test]
async fn large_push_waits_for_the_window_and_close_is_reported() {
    let (fake, addr) = start(None).await;
    let mut h2 = client(TcpStream::connect(addr).await.unwrap()).await;
    let (resp, _send) = h2.send_request(req("/big/Push"), false).unwrap();
    let mut body = resp.await.unwrap().into_body();
    let mut got = 0;
    while got < 200 * 1024 + 5 {
        let d = tokio::time::timeout(Duration::from_secs(5), body.data()).await.expect("stalled").unwrap().unwrap();
        let _ = body.flow_control().release_capacity(d.len());
        got += d.len();
    }
    assert_eq!(got, 200 * 1024 + 5);
    drop(body);
    drop(h2);
    drop(_send);
    wait_for(&fake, "connection_closed").await;
}

#[tokio::test]
async fn tls_with_alpn_h2() {
    let cert = rcgen::generate_simple_self_signed(vec!["lobby.test".into()]).unwrap();
    let dir = std::env::temp_dir().join(format!("gw-tls-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    let (cp, kp) = (dir.join("cert.pem"), dir.join("key.pem"));
    std::fs::write(&cp, cert.cert.pem()).unwrap();
    std::fs::write(&kp, cert.key_pair.serialize_pem()).unwrap();
    let acceptor = gateway::tls::acceptor(&cp, &kp).unwrap();
    let (fake, addr) = start(Some(acceptor)).await;

    let mut roots = rustls::RootCertStore::empty();
    roots.add(cert.cert.der().clone()).unwrap();
    let mut cfg = rustls::ClientConfig::builder_with_provider(Arc::new(rustls::crypto::ring::default_provider()))
        .with_safe_default_protocol_versions()
        .unwrap()
        .with_root_certificates(roots)
        .with_no_client_auth();
    cfg.alpn_protocols = vec![b"h2".to_vec()];
    let tcp = TcpStream::connect(addr).await.unwrap();
    let name = rustls_pki_types::ServerName::try_from("lobby.test").unwrap();
    let tls = tokio_rustls::TlsConnector::from(Arc::new(cfg)).connect(name, tcp).await.unwrap();
    assert_eq!(tls.get_ref().1.alpn_protocol(), Some(&b"h2"[..]));
    let mut h2 = client(tls).await;
    let (resp, mut send) = h2.send_request(req("/echo/Unary"), false).unwrap();
    send.send_data(Bytes::from(grpc::frame(b"secure")), true).unwrap();
    let mut body = resp.await.unwrap().into_body();
    assert_eq!(read_all(&mut body).await, grpc::frame(b"secure"));
    assert!(fake.events().contains(&"half_closed".to_string()));
    std::fs::remove_dir_all(&dir).ok();
}
