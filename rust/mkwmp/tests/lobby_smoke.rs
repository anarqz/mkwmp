//! Smoke test of the real `mkwmp lobby` process: two game-like clients sign
//! in over TLS (NPLN), matchmake, both get the same assignment, open their
//! Gamesync sessions over h2c, and the UDP services answer. The game-protocol
//! details are the lobby crate's tests; this checks the wiring end to end.

use std::net::{SocketAddr, UdpSocket};
use std::process::{Child, Command, Stdio};
use std::sync::Arc;
use std::time::Duration;

use bytes::Bytes;
use h2::client::SendRequest;
use lobby::wire::{self, Msg};
use tokio::net::TcpStream;

const SIGN_IN: &str = "/nn.npln.auth.v1.Auth/IssuePrearrangedUserToken";
const CREATE: &str = "/nn.npln.matchmaking.v1.Matchmaker/CreateMatchmakingTicket";
const TRACK: &str = "/nn.npln.matchmaking.v1.Matchmaker/TrackMatchmakingTicket";
const KUS: &str = "/nn.npln.gamesync.v1.Gamesync/KeepUserSession";
const ISSUE: &str = "/nn.npln.gamesync.v1.Gamesync/IssueToken";

struct Lobby {
    child: Child,
    dir: std::path::PathBuf,
    npln: u16,
    gamesync: u16,
    turn: u16,
    nat: u16,
}

impl Drop for Lobby {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
        let _ = std::fs::remove_dir_all(&self.dir);
    }
}

fn free_port() -> u16 {
    std::net::TcpListener::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port()
}

fn free_udp() -> u16 {
    UdpSocket::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port()
}

fn start() -> (Lobby, rcgen::CertifiedKey) {
    let dir = std::env::temp_dir().join(format!("mkwmp-lobby-smoke-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    let cert = rcgen::generate_simple_self_signed(vec!["lobby.test".into()]).unwrap();
    std::fs::write(dir.join("cert.pem"), cert.cert.pem()).unwrap();
    std::fs::write(dir.join("key.pem"), cert.key_pair.serialize_pem()).unwrap();
    std::fs::write(dir.join("secret"), [7u8; 32]).unwrap();
    let (npln, gamesync, turn, nat) = (free_port(), free_port(), free_udp(), free_udp());
    let toml = format!(
        r#"
[server]
name = "smoke"
bind = "127.0.0.1:0"
[seats]
capacity = 10
bind = "127.0.0.1:0"
lobby_endpoint = "127.0.0.1:{npln}"
[lobby]
npln_port = {npln}
gamesync_port = {gamesync}
turn_port = {turn}
nat_port = {nat}
gamesync_host = "lobby.test"
token_secret_file = "secret"
[lobby.policy]
gather_secs = 1
[tls]
cert = "cert.pem"
key = "key.pem"
"#
    );
    std::fs::write(dir.join("server.toml"), toml).unwrap();
    let child = Command::new(env!("CARGO_BIN_EXE_mkwmp"))
        .args(["lobby", dir.join("server.toml").to_str().unwrap()])
        .stdout(Stdio::null())
        .stderr(if std::env::var_os("SMOKE_LOG").is_some() { Stdio::inherit() } else { Stdio::null() })
        .spawn()
        .unwrap();
    for _ in 0..100 {
        if std::net::TcpStream::connect(("127.0.0.1", gamesync)).is_ok() {
            break;
        }
        std::thread::sleep(Duration::from_millis(50));
    }
    (Lobby { child, dir, npln, gamesync, turn, nat }, cert)
}

async fn h2c(port: u16) -> SendRequest<Bytes> {
    let tcp = TcpStream::connect(("127.0.0.1", port)).await.unwrap();
    let (send, conn) = h2::client::handshake(tcp).await.unwrap();
    tokio::spawn(async move {
        let _ = conn.await;
    });
    send
}

async fn tls(port: u16, cert: &rcgen::CertifiedKey) -> SendRequest<Bytes> {
    let mut roots = rustls::RootCertStore::empty();
    roots.add(cert.cert.der().clone()).unwrap();
    let mut cfg = rustls::ClientConfig::builder_with_provider(Arc::new(rustls::crypto::ring::default_provider()))
        .with_safe_default_protocol_versions()
        .unwrap()
        .with_root_certificates(roots)
        .with_no_client_auth();
    cfg.alpn_protocols = vec![b"h2".to_vec()];
    let tcp = TcpStream::connect(("127.0.0.1", port)).await.unwrap();
    let name = rustls_pki_types::ServerName::try_from("lobby.test").unwrap();
    let s = tokio_rustls::TlsConnector::from(Arc::new(cfg)).connect(name, tcp).await.unwrap();
    let (send, conn) = h2::client::handshake(s).await.unwrap();
    tokio::spawn(async move {
        let _ = conn.await;
    });
    send
}

fn request(path: &str, bearer: Option<&str>) -> http::Request<()> {
    let mut r = http::Request::builder()
        .method("POST")
        .uri(format!("https://lobby.test{path}"))
        .header("content-type", "application/grpc")
        .header("te", "trailers");
    if let Some(b) = bearer {
        r = r.header("authorization", format!("Bearer {b}"));
    }
    r.body(()).unwrap()
}

fn frame(m: &[u8]) -> Bytes {
    Bytes::from(gateway::grpc::frame(m))
}

/// A streaming call's replies, one message at a time.
struct Replies {
    body: h2::RecvStream,
    deframer: gateway::grpc::Deframer,
    queue: std::collections::VecDeque<Vec<u8>>,
}

impl Replies {
    async fn open(h2: &mut SendRequest<Bytes>, path: &str, bearer: Option<&str>, body: &[u8], close: bool) -> (Self, h2::SendStream<Bytes>) {
        let (resp, mut send) = h2.send_request(request(path, bearer), false).unwrap();
        send.send_data(frame(body), close).unwrap();
        let resp = tokio::time::timeout(Duration::from_secs(5), resp).await.expect("response headers").unwrap();
        assert_eq!(resp.status(), 200, "{path}");
        (Self { body: resp.into_body(), deframer: Default::default(), queue: Default::default() }, send)
    }

    /// The next message, or None when the stream ended / nothing came in `wait`.
    async fn next(&mut self, wait: Duration) -> Option<Vec<u8>> {
        loop {
            if let Some(m) = self.queue.pop_front() {
                return Some(m);
            }
            let d = tokio::time::timeout(wait, self.body.data()).await.ok()??.ok()?;
            let _ = self.body.flow_control().release_capacity(d.len());
            self.queue.extend(self.deframer.push(&d).unwrap());
        }
    }

    async fn status(mut self) -> String {
        while self.next(Duration::from_secs(5)).await.is_some() {}
        let t = self.body.trailers().await.unwrap().unwrap();
        t["grpc-status"].to_str().unwrap().to_string()
    }
}

async fn unary(h2: &mut SendRequest<Bytes>, path: &str, bearer: Option<&str>, body: &[u8]) -> Vec<u8> {
    let (mut r, _send) = Replies::open(h2, path, bearer, body, true).await;
    let m = r.next(Duration::from_secs(5)).await.unwrap_or_default();
    assert_eq!(r.status().await, "0", "{path}");
    m
}

struct Player {
    npln: SendRequest<Bytes>,
    token: String,
    uid: String,
}

async fn sign_in(lobby: &Lobby, cert: &rcgen::CertifiedKey, seat: &str) -> Player {
    let mut npln = tls(lobby.npln, cert).await;
    let body = Msg::new().bytes(1, "tenants/t-test").msg(3, &Msg::new().bytes(1, seat)).into_bytes();
    let reply = unary(&mut npln, SIGN_IN, None, &body).await;
    let token = wire::bytes_field(&reply, 2).unwrap().unwrap();
    let user = wire::str_field(token, 1).unwrap().unwrap();
    let access = wire::str_field(token, 2).unwrap().unwrap();
    Player { npln, token: access, uid: user.rsplit('/').next().unwrap().to_string() }
}

async fn search(p: &mut Player, mode: &str) -> Replies {
    let player = Msg::new().bytes(1, "tenants/current/users/current").bytes(2, "attrs");
    let body = Msg::new()
        .msg(2, &Msg::new().bytes(2, format!("tenants/current/matchmakingConfigs/{mode}")).msg(4, &player))
        .into_bytes();
    let reply = unary(&mut p.npln, CREATE, Some(&p.token), &body).await;
    let name = wire::str_field(&reply, 1).unwrap().unwrap();
    let ticket = format!("tenants/current/matchmakingTickets/{}", name.rsplit('/').next().unwrap());
    let (track, _send) = Replies::open(&mut p.npln, TRACK, Some(&p.token), &Msg::new().bytes(1, &ticket).into_bytes(), true).await;
    track
}

fn session_of(assignment: &[u8]) -> String {
    let s = wire::bytes_field(assignment, 7).unwrap().unwrap();
    wire::str_field(s, 1).unwrap().unwrap()
}

#[tokio::test(flavor = "multi_thread")]
async fn two_players_reach_a_session_through_the_real_process() {
    let (lobby, cert) = start();
    let mut a = sign_in(&lobby, &cert, "dev").await;
    let mut b = sign_in(&lobby, &cert, lobby::users::SECOND_LOCAL_TOKEN).await;
    assert_ne!(a.uid, b.uid);

    let mut ta = search(&mut a, "Battle24").await;
    let mut tb = search(&mut b, "Battle24").await;
    // Gathering (1 s here), then both trackers get the same session.
    let ma = ta.next(Duration::from_secs(5)).await.expect("assignment for A");
    let mb = tb.next(Duration::from_secs(5)).await.expect("assignment for B");
    assert_eq!(session_of(&ma), session_of(&mb));
    assert_eq!(ta.status().await, "0");

    // Gamesync over h2c: token, then the user session stream.
    let mut gs = h2c(lobby.gamesync).await;
    unary(&mut gs, ISSUE, Some(&a.token), &Msg::new().bytes(1, format!("userSessions/{}", a.uid)).bytes(2, &a.uid).0).await;
    let target = Msg::new()
        .bytes(1, "userSessions/current/targets/1")
        .msg(2, &Msg::new().bytes(1, format!("docs/__us/{}", a.uid)));
    let frame1 = Msg::new().bytes(1, "userSessions/current").msg(3, &Msg::new().msg(1, &target));
    let (mut kus, _keep) = Replies::open(&mut gs, KUS, Some(&a.token), &frame1.0, false).await;
    let mut got = 0;
    while kus.next(Duration::from_millis(500)).await.is_some() {
        got += 1;
    }
    assert_eq!(got, 4, "__us + __gs/f + current + done");

    // UDP: NAT probe and a TURN Binding.
    let u = UdpSocket::bind("127.0.0.1:0").unwrap();
    u.set_read_timeout(Some(Duration::from_secs(2))).unwrap();
    let mut probe = [0u8; 16];
    probe[3] = 101;
    u.send_to(&probe, ("127.0.0.1", lobby.nat)).unwrap();
    let mut buf = [0u8; 256];
    let (n, _) = u.recv_from(&mut buf).unwrap();
    assert_eq!((n, &buf[..4]), (16, &[0, 0, 0, 101][..]));
    let SocketAddr::V4(me) = u.local_addr().unwrap() else { panic!() };
    assert_eq!(u16::from_be_bytes([buf[6], buf[7]]), me.port());
    let bind = gateway::turn::build(gateway::turn::msg_type(gateway::turn::BINDING, gateway::turn::REQUEST), &[1; 12], &[], None);
    u.send_to(&bind, ("127.0.0.1", lobby.turn)).unwrap();
    let (n, _) = u.recv_from(&mut buf).unwrap();
    let m = gateway::turn::parse(&buf[..n]).unwrap();
    assert_eq!(m.class, gateway::turn::SUCCESS);
    drop(b);
}
