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
    seat: Option<Child>,
    log: Arc<std::sync::Mutex<String>>,
    seat_port: u16,
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
        if let Some(s) = self.seat.as_mut() {
            let _ = s.kill();
            let _ = s.wait();
        }
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
    start_with(false)
}

/// `with_seat`: also run `mkwmp server` with signing keys and the roster
/// enabled, and point the lobby at it (sync every second).
fn start_with(with_seat: bool) -> (Lobby, rcgen::CertifiedKey) {
    let dir = std::env::temp_dir().join(format!("mkwmp-lobby-smoke-{}-{with_seat}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    let cert = rcgen::generate_simple_self_signed(vec!["lobby.test".into()]).unwrap();
    std::fs::write(dir.join("cert.pem"), cert.cert.pem()).unwrap();
    std::fs::write(dir.join("key.pem"), cert.key_pair.serialize_pem()).unwrap();
    std::fs::write(dir.join("secret"), [7u8; 32]).unwrap();
    let (npln, gamesync, turn, nat, seat_port) = (free_port(), free_port(), free_udp(), free_udp(), free_port());
    std::fs::write(dir.join("lobby-tokens.txt"), "lt-smoke\n").unwrap();
    // A fixed test key pair (`mkwmp keygen t1`).
    std::fs::write(dir.join("seat.key"), "G7pdXMxFuREdTbldNlub-3RQ2ihATuJMBrfdG9Tx7AY\n").unwrap();
    let seat_cfg = if with_seat {
        format!(
            r#"lobby_tokens_file = "lobby-tokens.txt"
[ticket]
kid = "t1"
private_key_file = "seat.key"
public_keys = ["t1:ffHoVXEaR0CTOos3YCcffxs6uVsmPT7Vp2y3zcI025w"]
[registry]
"#
        )
    } else {
        String::new()
    };
    let roster = if with_seat {
        format!("roster_url = \"http://127.0.0.1:{seat_port}\"\nroster_token_file = \"lobby-tokens.txt\"\nroster_secs = 1\n")
    } else {
        String::new()
    };
    let toml = format!(
        r#"
[server]
name = "smoke"
bind = "127.0.0.1:0"
[seats]
capacity = 10
bind = "127.0.0.1:{seat_port}"
lobby_endpoint = "127.0.0.1:{npln}"
{seat_cfg}
[lobby]
{roster}
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
    let cfg = dir.join("server.toml");
    let seat = with_seat.then(|| {
        let c = Command::new(env!("CARGO_BIN_EXE_mkwmp")).args(["server", cfg.to_str().unwrap()]).stdout(Stdio::null()).stderr(Stdio::null()).spawn().unwrap();
        for _ in 0..100 {
            if std::net::TcpStream::connect(("127.0.0.1", seat_port)).is_ok() {
                break;
            }
            std::thread::sleep(Duration::from_millis(50));
        }
        c
    });
    let mut child = Command::new(env!("CARGO_BIN_EXE_mkwmp"))
        .args(["lobby", cfg.to_str().unwrap()])
        .stdout(Stdio::null())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    // Keep the lobby's log for assertions (and echo it with SMOKE_LOG=1).
    let log = Arc::new(std::sync::Mutex::new(String::new()));
    let (stderr, sink) = (child.stderr.take().unwrap(), log.clone());
    std::thread::spawn(move || {
        use std::io::BufRead;
        for line in std::io::BufReader::new(stderr).lines().map_while(Result::ok) {
            if std::env::var_os("SMOKE_LOG").is_some() {
                eprintln!("{line}");
            }
            sink.lock().unwrap().push_str(&line);
            sink.lock().unwrap().push('\n');
        }
    });
    for _ in 0..100 {
        if std::net::TcpStream::connect(("127.0.0.1", gamesync)).is_ok() {
            break;
        }
        std::thread::sleep(Duration::from_millis(50));
    }
    (Lobby { child, seat, log, seat_port, dir, npln, gamesync, turn, nat }, cert)
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

/// Seat server JSON over plain HTTP/1.1 (std only; the test needs nothing fancy).
fn seat_call(port: u16, method: &str, path: &str, bearer: Option<&str>, body: &str) -> serde_json::Value {
    use std::io::{Read, Write};
    let mut s = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
    let auth = bearer.map(|b| format!("authorization: Bearer {b}\r\n")).unwrap_or_default();
    write!(s, "{method} {path} HTTP/1.1\r\nhost: x\r\ncontent-type: application/json\r\n{auth}content-length: {}\r\nconnection: close\r\n\r\n{body}", body.len()).unwrap();
    let mut out = String::new();
    s.read_to_string(&mut out).unwrap();
    let body = out.split_once("\r\n\r\n").map(|x| x.1).unwrap_or("");
    serde_json::from_str(body).unwrap_or(serde_json::Value::Null)
}

#[tokio::test(flavor = "multi_thread")]
async fn roster_sync_ends_a_dropped_seat() {
    let (lobby, cert) = start_with(true);
    let p = lobby.seat_port;
    let mut tickets = vec![];
    for (name, friend) in [("Ana", "bia"), ("Bia", "ana")] {
        let pin = seat_call(p, "POST", "/seat/v1/profiles", None, &format!(r#"{{"name":"{name}"}}"#))["pin"].as_str().unwrap().to_string();
        let v = seat_call(p, "POST", "/seat/v1/sessions", None, &format!(r#"{{"name":"{name}","pin":"{pin}","friends":["{friend}"]}}"#));
        let ticket = v["ticket"]["token"].as_str().expect("seated with a ticket").to_string();
        tickets.push((v["sessionId"].as_str().unwrap().to_string(), v["sessionKey"].as_str().unwrap().to_string(), ticket));
    }
    let _a = sign_in(&lobby, &cert, &tickets[0].2).await;
    let _b = sign_in(&lobby, &cert, &tickets[1].2).await;
    // A sync confirms both alive; then Ana's seat is dropped at the seat server.
    tokio::time::sleep(Duration::from_millis(2500)).await;
    assert!(!lobby.log.lock().unwrap().contains("seat no longer alive"));
    seat_call(p, "DELETE", &format!("/seat/v1/sessions/{}", tickets[0].0), Some(&tickets[0].1), "");
    for _ in 0..40 {
        if lobby.log.lock().unwrap().contains("seat no longer alive") {
            break;
        }
        tokio::time::sleep(Duration::from_millis(100)).await;
    }
    let log = lobby.log.lock().unwrap().clone();
    let kicked: Vec<&str> = log.lines().filter(|l| l.contains("seat no longer alive")).collect();
    assert_eq!(kicked.len(), 1, "{log}");
    assert!(kicked[0].contains("Ana"), "{}", kicked[0]);
    assert!(!log.contains("roster call"), "roster errors: {log}");
}
