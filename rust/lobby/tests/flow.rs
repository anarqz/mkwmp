//! End-to-end through the Core's transport API: game clients sign in, search, get matched,
//! join the Gamesync session, play a match through the referee and leave. Wire layouts are the
//! ones the game was verified with (resources/python-lobby/mkwmp_server.py).

use std::net::SocketAddr;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;

use lobby::docs::ChangeKind;
use lobby::value::{Fields, Value};
use lobby::wire::{self, Msg};
use lobby::{Call, Core, CoreConfig, Out, Policy};
use tokio::sync::mpsc::{unbounded_channel, UnboundedReceiver};

const SIGN_IN: &str = "/nn.npln.auth.v1.Auth/IssuePrearrangedUserToken";
const CREATE: &str = "/nn.npln.matchmaking.v1.Matchmaker/CreateMatchmakingTicket";
const TRACK: &str = "/nn.npln.matchmaking.v1.Matchmaker/TrackMatchmakingTicket";
const CANCEL: &str = "/nn.npln.matchmaking.v1.Matchmaker/CancelMatchmakingTicket";
const KUS: &str = "/nn.npln.gamesync.v1.Gamesync/KeepUserSession";
const WRITE: &str = "/nn.npln.gamesync.v1.Gamesync/WriteDocuments";
const GET: &str = "/nn.npln.gamesync.v1.Gamesync/GetDocument";
const ISSUE: &str = "/nn.npln.gamesync.v1.Gamesync/IssueToken";

struct World {
    core: Core,
    clock: Arc<AtomicU64>, // milliseconds
    next: AtomicU64,
    /// The address a connection comes from (default: loopback).
    peers: std::sync::Mutex<std::collections::HashMap<u64, SocketAddr>>,
}

impl World {
    fn new(policy: Policy) -> Self {
        let clock = Arc::new(AtomicU64::new(1_800_000_000_000));
        let c = clock.clone();
        let core = Core::with_clock(CoreConfig::default(), policy, Arc::new(move || c.load(Ordering::SeqCst) as f64 / 1000.0));
        Self { core, clock, next: AtomicU64::new(1), peers: Default::default() }
    }

    fn with_config(cfg: CoreConfig) -> Self {
        let clock = Arc::new(AtomicU64::new(1_800_000_000_000));
        let c = clock.clone();
        let core = Core::with_clock(cfg, Policy::default(), Arc::new(move || c.load(Ordering::SeqCst) as f64 / 1000.0));
        Self { core, clock, next: AtomicU64::new(1), peers: Default::default() }
    }

    fn advance(&self, secs: f64) {
        self.clock.fetch_add((secs * 1000.0) as u64, Ordering::SeqCst);
        self.core.tick();
    }

    fn id(&self) -> u64 {
        self.next.fetch_add(1, Ordering::SeqCst)
    }

    /// Open a stream, send `body` (if any), optionally half-close.
    fn call(&self, conn: u64, path: &str, bearer: Option<&str>, body: Option<&[u8]>, close: bool) -> (u64, UnboundedReceiver<Out>) {
        let (tx, rx) = unbounded_channel();
        let stream = self.id();
        let peer: SocketAddr = self.peers.lock().unwrap().get(&conn).copied().unwrap_or("127.0.0.1:5000".parse().unwrap());
        self.core.open(&Call { conn, stream, path, authority: "x", bearer, peer }, tx);
        if let Some(b) = body {
            self.core.message(conn, stream, b);
        }
        if close {
            self.core.half_closed(conn, stream);
        }
        (stream, rx)
    }

    fn unary(&self, conn: u64, path: &str, bearer: Option<&str>, body: &[u8]) -> Result<Vec<u8>, u32> {
        let (_, mut rx) = self.call(conn, path, bearer, Some(body), true);
        let mut msg = Vec::new();
        while let Ok(out) = rx.try_recv() {
            match out {
                Out::Message(m) => msg = m,
                Out::End { grpc_status: 0, .. } => return Ok(msg),
                Out::End { grpc_status, .. } => return Err(grpc_status),
            }
        }
        panic!("{path}: no reply");
    }
}

fn drain(rx: &mut UnboundedReceiver<Out>) -> Vec<Out> {
    let mut v = Vec::new();
    while let Ok(o) = rx.try_recv() {
        v.push(o);
    }
    v
}

fn messages(rx: &mut UnboundedReceiver<Out>) -> Vec<Vec<u8>> {
    drain(rx).into_iter().filter_map(|o| if let Out::Message(m) = o { Some(m) } else { None }).collect()
}

/// (target id, kind, document name, fields) of the Change in a session message.
fn changes(msgs: &[Vec<u8>]) -> Vec<(String, u64, String, Fields)> {
    msgs.iter()
        .filter_map(|m| {
            let c = wire::bytes_field(m, 2).unwrap()?;
            let tid = wire::str_field(c, 1).unwrap().unwrap();
            let kind = wire::varint_field(c, 2).unwrap().unwrap();
            let doc = wire::bytes_field(c, 3).unwrap().unwrap();
            let name = wire::str_field(doc, 1).unwrap().unwrap();
            let fields = wire::bytes_field(doc, 2).unwrap().map(|f| Fields::decode(f).unwrap()).unwrap_or_default();
            Some((tid, kind, name, fields))
        })
        .collect()
}

struct Player {
    conn: u64,
    gs_conn: u64,
    user: String,
    uid: String,
    token: String,
    ticket: String,
    track: Option<UnboundedReceiver<Out>>,
    kus: Option<(u64, UnboundedReceiver<Out>)>,
}

fn sign_in(w: &World, seat_token: &str) -> Player {
    sign_in_from(w, seat_token, None).unwrap()
}

/// Sign in from `ip` (None: loopback); the gRPC status when refused.
fn sign_in_from(w: &World, seat_token: &str, ip: Option<&str>) -> Result<Player, u32> {
    let (conn, gs_conn) = (w.id(), w.id());
    if let Some(ip) = ip {
        let peer: SocketAddr = format!("{ip}:5000").parse().unwrap();
        w.peers.lock().unwrap().extend([(conn, peer), (gs_conn, peer)]);
    }
    let body = Msg::new().bytes(1, "tenants/t-test").msg(3, &Msg::new().bytes(1, seat_token)).into_bytes();
    let reply = w.unary(conn, SIGN_IN, None, &body)?;
    let token = wire::bytes_field(&reply, 2).unwrap().unwrap();
    let user = wire::str_field(token, 1).unwrap().unwrap();
    let access = wire::str_field(token, 2).unwrap().unwrap();
    let uid = user.rsplit('/').next().unwrap().to_owned();
    Ok(Player { conn, gs_conn, user, uid, token: access, ticket: String::new(), track: None, kus: None })
}

fn search(w: &World, p: &mut Player, mode: &str) {
    let player = Msg::new().bytes(1, "tenants/current/users/current").bytes(2, "attrs");
    let body = Msg::new()
        .msg(2, &Msg::new().bytes(2, format!("tenants/current/matchmakingConfigs/{mode}")).msg(4, &player))
        .into_bytes();
    let reply = w.unary(p.conn, CREATE, Some(&p.token), &body).unwrap();
    let name = wire::str_field(&reply, 1).unwrap().unwrap();
    p.ticket = format!("tenants/current/matchmakingTickets/{}", name.rsplit('/').next().unwrap());
    let body = Msg::new().bytes(1, &p.ticket).into_bytes();
    let (_, rx) = w.call(p.conn, TRACK, Some(&p.token), Some(&body), true);
    p.track = Some(rx);
}

/// The assignment the tracker got: (GameSession name, member user resources).
fn assignment(p: &mut Player) -> Option<(String, Vec<String>)> {
    let out = drain(p.track.as_mut().unwrap());
    let msg = out.iter().find_map(|o| if let Out::Message(m) = o { Some(m.clone()) } else { None })?;
    assert!(out.contains(&Out::ok()));
    assert_eq!(wire::varint_field(&msg, 5).unwrap(), Some(3));
    let session = wire::bytes_field(&msg, 7).unwrap().unwrap();
    let name = wire::str_field(session, 1).unwrap().unwrap();
    let members = wire::repeated_bytes(session, 12).unwrap().iter().map(|m| wire::str_field(m, 2).unwrap().unwrap()).collect();
    // our own Player carries our real user resource
    let own = wire::bytes_field(&msg, 4).unwrap().unwrap();
    assert_eq!(wire::str_field(own, 1).unwrap().as_deref(), Some(p.user.as_str()));
    Some((name, members))
}

fn listen(id: u32, collection: bool, name: &str) -> Msg {
    let target = Msg::new().bytes(1, format!("userSessions/current/targets/{id}"))
        .msg(if collection { 3 } else { 2 }, &Msg::new().bytes(1, name));
    Msg::new().msg(1, &target)
}

fn join_session(w: &World, p: &mut Player) -> Vec<Vec<u8>> {
    // Gamesync runs on its own connection with the NPLN token as bearer.
    w.unary(p.gs_conn, ISSUE, Some(&p.token), &Msg::new().bytes(1, format!("userSessions/{}", p.uid)).bytes(2, &p.uid).0).unwrap();
    let frame = Msg::new().bytes(1, "userSessions/current").msg(3, &listen(1, false, &format!("docs/__us/{}", p.uid)));
    let (sid, mut rx) = w.call(p.gs_conn, KUS, Some(&p.token), Some(&frame.0), false);
    let got = messages(&mut rx);
    p.kus = Some((sid, rx));
    got
}

fn send_frame(w: &World, p: &mut Player, frame: Msg) -> Vec<Vec<u8>> {
    let (sid, rx) = p.kus.as_mut().unwrap();
    w.core.message(p.gs_conn, *sid, &frame.0);
    messages(rx)
}

fn pushes(p: &mut Player) -> Vec<Vec<u8>> {
    messages(&mut p.kus.as_mut().unwrap().1)
}

fn write(w: &World, p: &Player, name: &str, fields: Fields, mask: Option<&[&str]>) {
    let doc = Msg::new().bytes(1, name).msg(2, &fields.encode());
    let mut update = Msg::new().msg(1, &doc);
    if let Some(mask) = mask {
        update = update.msg(2, &mask.iter().fold(Msg::new(), |m, p| m.bytes(1, format!("`{p}`"))));
    }
    let body = Msg::new().msg(1, &Msg::new().msg(1, &update)).into_bytes();
    w.unary(p.gs_conn, WRITE, Some(&p.token), &body).unwrap();
}

fn get(w: &World, p: &Player, name: &str) -> Option<Fields> {
    let reply = w.unary(p.gs_conn, GET, Some(&p.token), &Msg::new().bytes(1, name).0).unwrap();
    wire::bytes_field(&reply, 2).unwrap().map(|f| Fields::decode(f).unwrap())
}

fn ussid_of(session_reply: &[Vec<u8>]) -> i64 {
    changes(session_reply)[0].3.get("ussid").and_then(Value::as_int).unwrap()
}

#[test]
fn two_players_match_play_and_leave() {
    let w = World::new(Policy::default());
    let mut a = sign_in(&w, "dev");
    let mut b = sign_in(&w, lobby::users::SECOND_LOCAL_TOKEN);
    assert_ne!(a.uid, b.uid);
    search(&w, &mut a, "Battle24");
    search(&w, &mut b, "Battle24");
    assert!(assignment(&mut a).is_none(), "gathering waits for more players");
    assert!(w.core.voice_room(&a.uid).is_none(), "no voice while gathering");
    w.advance(11.0);
    let (session_a, members) = assignment(&mut a).expect("assigned after the gather wait");
    let (session_b, _) = assignment(&mut b).unwrap();
    assert_eq!(session_a, session_b);
    assert_eq!(members, vec![a.user.clone(), b.user.clone()]);
    assert_eq!(w.core.counts()["Battle24"].players, 2);

    // Voice (VOICE_API.md §1): none in a matchmade lobby, only in friend rooms.
    assert!(w.core.voice_room(&a.uid).is_none(), "no voice in matchmaking");
    assert!(w.core.voice_room("00000000-0000-4000-8000-000000000099").is_none());
    assert!(w.core.voice_user("not-a-ticket").is_none());

    // Own user session: __us/<uid> (st 2) + the lobby's __gs/f, then "current" + "done" ops.
    let got = join_session(&w, &mut a);
    let c = changes(&got);
    assert_eq!(c.len(), 2);
    assert_eq!(c[0].2, format!("docs/__us/{}", a.uid));
    assert_eq!(c[0].3.get("st"), Some(&Value::Int(2)));
    assert_eq!(c[1].2, "docs/__gs/f");
    assert_eq!(c[1].3.get("mcn").and_then(Value::as_str), Some(a.uid.as_str()));
    assert_eq!(got.len(), 4);
    let ussid_a = ussid_of(&got);
    let ussid_b = ussid_of(&join_session(&w, &mut b));
    assert_ne!(ussid_a, ussid_b);

    // Member collection: one numeric-id document per member.
    let got = send_frame(&w, &mut a, Msg::new().bytes(1, "userSessions/current").msg(3, &listen(2, true, "docs/__us")));
    let names: Vec<String> = changes(&got).into_iter().map(|c| c.2).collect();
    assert_eq!(names, vec![format!("docs/__us/{ussid_a}"), format!("docs/__us/{ussid_b}")]);

    // P2P join order: B's __pus listen waits until A (joined earlier) wrote its __pus.
    let pus = "docs/__pgn/All/__pus";
    let got = send_frame(&w, &mut b, Msg::new().bytes(1, "userSessions/current").msg(3, &listen(3, true, pus)));
    assert!(got.is_empty(), "deferred");
    let got = send_frame(&w, &mut a, Msg::new().bytes(1, "userSessions/current").msg(3, &listen(3, true, pus)));
    assert_eq!(got.len(), 2, "first member: empty snapshot + ops");
    write(&w, &a, &format!("{pus}/{}", a.uid), Fields::new().with("uid", Value::str(&a.uid)), None);
    let got = pushes(&mut b);
    assert_eq!(changes(&got)[0].2, format!("{pus}/{}", a.uid), "released with A's document");

    // Merge writes and listeners on PlayerStatic.
    let ps = format!("docs/PlayerStatic/{ussid_a}");
    send_frame(&w, &mut b, Msg::new().bytes(1, "userSessions/current").msg(3, &listen(4, true, "docs/PlayerStatic")));
    write(&w, &a, &ps, Fields::new().with("p2p_sid", Value::Int(77)), Some(&["p2p_sid"]));
    write(&w, &a, &ps, Fields::new().with("vehicle", Value::Int(5)), Some(&["vehicle"]));
    let c = changes(&pushes(&mut b));
    assert_eq!((c[0].1, c[1].1), (ChangeKind::Added as u64, ChangeKind::Modified as u64));
    let doc = get(&w, &b, &ps).unwrap();
    assert_eq!(doc.get("p2p_sid"), Some(&Value::Int(77)));
    assert_eq!(doc.get("vehicle"), Some(&Value::Int(5)));

    // Nobody writes another member's PlayerStatic (the name and station others see) nor the server's __us.
    write(&w, &b, &ps, Fields::new().with("name", Value::str("impostor")).with("p2p_sid", Value::Int(1)), None);
    let doc = get(&w, &b, &ps).unwrap();
    assert_eq!(doc.get("p2p_sid"), Some(&Value::Int(77)), "B's write to A's PlayerStatic is refused");
    assert!(doc.get("name").is_none());
    let us_a = format!("docs/__us/{ussid_a}");
    write(&w, &b, &us_a, Fields::new().with("uid", Value::str("forged")).with("st", Value::Int(0)), None);
    write(&w, &a, &us_a, Fields::new().with("uid", Value::str("forged")).with("st", Value::Int(0)), None);
    assert!(get(&w, &a, &us_a).and_then(|d| d.get("uid").and_then(Value::as_str).map(str::to_owned)).as_deref() != Some("forged"), "__us is the server's");
    assert!(changes(&pushes(&mut a)).iter().all(|c| c.2 != us_a), "a refused write publishes nothing");

    // Referee: open, merge reports, final only after both reported.
    send_frame(&w, &mut a, Msg::new().bytes(1, "userSessions/current").msg(3, &listen(5, true, "docs/@@RefereeResult")));
    let request = Fields::new()
        .with("users", Value::List(vec![Value::Int(ussid_a), Value::Int(ussid_b)]))
        .with("ussid_map", Value::Map(Fields::new().with("0", Value::Int(ussid_a)).with("1", Value::Int(ussid_b))))
        .with("option", Value::Map(Fields::new().with("total_player_count", Value::Int(2))));
    write(&w, &a, &format!("docs/@@RefereeRequest/{ussid_a}/Request/key1"), request.clone(), None);
    write(&w, &b, &format!("docs/@@RefereeRequest/{ussid_b}/Request/key1"), request, None);
    let c = changes(&pushes(&mut a));
    assert_eq!(c.len(), 1, "one result per match");
    let result = c[0].2.clone();
    assert_eq!(c[0].3.get("status"), Some(&Value::Int(0)));
    assert_eq!(w.core.counts()["Battle24"].in_event, 1);
    let uuid = result.rsplit('/').next().unwrap();
    let order = Fields::new().with("result", Value::List(vec![Value::Int(1), Value::Int(0)]));
    write(&w, &a, &format!("docs/@@RefereeReport/{uuid}/User/{ussid_a}"), order.clone(), Some(&["result"]));
    assert_eq!(changes(&pushes(&mut a))[0].3.get("status"), Some(&Value::Int(0)));
    write(&w, &b, &format!("docs/@@RefereeReport/{uuid}/User/{ussid_b}"), order, Some(&["result"]));
    let c = changes(&pushes(&mut a));
    assert_eq!(c[0].3.get("status"), Some(&Value::Int(1)));
    assert!(c[0].3.get("ratings").is_some());
    assert_eq!(w.core.counts()["Battle24"].intermission, 1);

    // B's session ends: after the grace, A sees B's documents and member entry REMOVED.
    let (sid, _) = b.kus.take().unwrap();
    w.core.half_closed(b.gs_conn, sid);
    w.advance(1.0);
    assert!(pushes(&mut a).is_empty(), "a reconnect within the grace is not a leave");
    w.advance(5.0);
    let c = changes(&pushes(&mut a));
    assert!(c.iter().any(|c| c.1 == ChangeKind::Removed as u64 && c.2 == format!("docs/__us/{ussid_b}")), "{c:?}");
    assert_eq!(w.core.counts()["Battle24"].players, 1);

    // A leaves too: the lobby is gone.
    w.core.connection_closed(a.gs_conn);
    w.advance(6.0);
    assert!(w.core.counts().is_empty());
}

/// MESH-RUST-DIFF.md H2 + LOBBY-RUST-PY-AUDIT.md 2: the game's mesh host is the lowest `upcsid` it
/// sees in `__pus`; `upcsid` is the join order inside the lobby (the real `__upcsidn` counter), not the
/// global user number. B signs in second (higher global number) but is seated first: B hosts, A waits.
#[test]
fn pus_host_is_the_first_seated_member_whatever_the_global_numbers() {
    let w = World::new(Policy::default());
    let mut a = sign_in(&w, "dev");
    let mut b = sign_in(&w, lobby::users::SECOND_LOCAL_TOKEN);
    search(&w, &mut b, "Battle24"); // B is seated first
    search(&w, &mut a, "Battle24");
    w.advance(11.0);
    assignment(&mut a).expect("assigned");
    assignment(&mut b).expect("assigned");
    let a_us = join_session(&w, &mut a);
    let b_us = join_session(&w, &mut b);
    let upcsid = |got: &[Vec<u8>]| changes(got)[0].3.get("upcsid").and_then(Value::as_int).unwrap();
    assert_eq!((upcsid(&b_us), upcsid(&a_us)), (1, 2), "upcsid = join order in the lobby");
    let pus = "docs/__pgn/All/__pus";
    let got = send_frame(&w, &mut a, Msg::new().bytes(1, "userSessions/current").msg(3, &listen(3, true, pus)));
    assert!(got.is_empty(), "the later member is deferred");
    let got = send_frame(&w, &mut b, Msg::new().bytes(1, "userSessions/current").msg(3, &listen(3, true, pus)));
    assert_eq!(got.len(), 2, "the first member: answered at once (it hosts)");
    write(&w, &b, &format!("{pus}/{}", b.uid), Fields::new().with("uid", Value::str(&b.uid)), None);
    let got = pushes(&mut a);
    assert_eq!(changes(&got)[0].2, format!("{pus}/{}", b.uid), "released with the host's document");
}

#[test]
fn lobbies_are_isolated_and_unlimited() {
    let mut policy = Policy::default();
    policy.capacity.insert("Battle24".into(), 2);
    let w = World::new(policy);
    let mut ps: Vec<Player> = ["ana", "bia", "caio", "duda"]
        .iter()
        .map(|n| sign_in(&w, &dev_ticket(n)))
        .collect();
    for p in &mut ps {
        search(&w, p, "Battle24");
    }
    // Capacity 2: two full lobbies, assigned at once.
    let sessions: Vec<String> = ps.iter_mut().map(|p| assignment(p).expect("full lobby assigns").0).collect();
    assert_eq!(sessions[0], sessions[1]);
    assert_eq!(sessions[2], sessions[3]);
    assert_ne!(sessions[0], sessions[2]);
    assert_eq!(w.core.counts()["Battle24"].lobbies, 2);

    for p in &mut ps {
        join_session(&w, p);
    }
    // Same document path in both lobbies, different contents.
    write(&w, &ps[0], "docs/Global/Session", Fields::new().with("short_session_id", Value::str("A")), None);
    write(&w, &ps[2], "docs/Global/Session", Fields::new().with("short_session_id", Value::str("B")), None);
    assert_eq!(get(&w, &ps[1], "docs/Global/Session").unwrap().get("short_session_id"), Some(&Value::str("A")));
    assert_eq!(get(&w, &ps[3], "docs/Global/Session").unwrap().get("short_session_id"), Some(&Value::str("B")));
    // Different __gs/f per lobby.
    let gs = |p: &Player| get(&w, p, "docs/__gs/f").unwrap().get("gsid").cloned();
    assert_eq!(gs(&ps[0]), gs(&ps[1]));
    assert_ne!(gs(&ps[0]), gs(&ps[2]));

    // A fifth player opens a third lobby.
    let mut e = sign_in(&w, &dev_ticket("enzo"));
    search(&w, &mut e, "Battle24");
    assert_eq!(w.core.counts()["Battle24"].lobbies, 3);
    // ...and cancelling the search drops it again, ending the tracker with CANCELLED.
    w.unary(e.conn, CANCEL, Some(&e.token), &Msg::new().bytes(1, &e.ticket).0).unwrap();
    assert!(drain(e.track.as_mut().unwrap()).contains(&Out::End { grpc_status: 1, message: None }));
    assert_eq!(w.core.counts()["Battle24"].lobbies, 2);
}

#[test]
fn launcher_names_replace_in_game_names() {
    let w = World::new(Policy::default());
    let mut a = sign_in(&w, &dev_ticket("Ana"));
    let mut b = sign_in(&w, &dev_ticket("Bia"));
    search(&w, &mut a, "Normal24");
    search(&w, &mut b, "Normal24");
    w.advance(11.0);
    let ussid = ussid_of(&join_session(&w, &mut a));
    join_session(&w, &mut b);
    let ps = format!("docs/PlayerStatic/{ussid}");
    // The game writes the launcher's name (its own spelling of it); others see the registered one.
    write(&w, &a, &ps, Fields::new().with("name", Value::str("ana")), Some(&["name"]));
    assert_eq!(get(&w, &b, &ps).unwrap().get("name"), Some(&Value::str("Ana")));
}

#[test]
fn production_requires_a_valid_seat_ticket() {
    let key = ticket::Issuer::new("k1", ed25519_dalek::SigningKey::from_bytes(&[9; 32]));
    let cfg = CoreConfig {
        require_ticket: true,
        ticket_keys: vec![format!("k1:{}", key.public_key_b64())],
        ..CoreConfig::default()
    };
    let core = Core::with_clock(cfg, Policy::default(), Arc::new(|| 1_800_000_000.0));
    let w = World { core, clock: Arc::new(AtomicU64::new(0)), next: AtomicU64::new(1), peers: Default::default() };
    let sign = |token: &str| {
        let body = Msg::new().bytes(1, "tenants/t").msg(3, &Msg::new().bytes(1, token)).into_bytes();
        w.unary(w.id(), SIGN_IN, None, &body)
    };
    assert_eq!(sign("dev"), Err(16));
    assert_eq!(sign(&dev_ticket("ana")), Err(16), "unsigned ticket");
    let good = key.sign(ticket::Claims {
        v: 1, iss: "seat".into(), kid: String::new(), sid: "s1".into(), name: "Ana".into(),
        iat: 1_799_999_000, exp: 1_800_000_600,
    });
    let reply = sign(&good).unwrap();
    let user = wire::str_field(wire::bytes_field(&reply, 2).unwrap().unwrap(), 1).unwrap().unwrap();
    assert!(user.ends_with(&lobby::users::ticket_user_id("ana")));
    // The voice relay checks the same ticket the same way.
    assert_eq!(w.core.voice_user(&good), Some(lobby::users::ticket_user_id("ana")));
    assert_eq!(w.core.voice_user(&dev_ticket("ana")), None);
}

const GSS: &str = "/nn.npln.matchmaking.v1.GameSessionService/";

#[test]
fn friend_room_create_room_id_and_join() {
    let w = World::new(Policy::default());
    let mut host = sign_in(&w, &dev_ticket("Ana"));
    let mut guest = sign_in(&w, &dev_ticket("Bia"));
    // Create (Private24, password "1234"), then track the creation ticket: state 2 + session.
    let player = Msg::new().bytes(1, "tenants/current/users/current");
    let session = Msg::new().bytes(6, "1234").msg(11, &Msg::new().bytes(1, "attrs"));
    let ticket = Msg::new().bytes(2, "tenants/current/matchmakingConfigs/Private24").msg(3, &player).msg(6, &session);
    let reply = w.unary(host.conn, &format!("{GSS}CreateGameSessionCreationTicket"), Some(&host.token),
        &Msg::new().bytes(1, "tenants/current").msg(2, &ticket).0).unwrap();
    let ct = wire::str_field(&reply, 1).unwrap().unwrap();
    assert!(ct.contains("/gameSessionCreationTickets/"));
    let (_, mut rx) = w.call(host.conn, &format!("{GSS}TrackGameSessionCreationTicket"), Some(&host.token),
        Some(&Msg::new().bytes(1, &ct).0), true);
    let out = drain(&mut rx);
    assert!(out.contains(&Out::ok()));
    let Out::Message(created) = &out[0] else { panic!() };
    assert_eq!(wire::varint_field(created, 4).unwrap(), Some(2));
    let gs = wire::bytes_field(created, 6).unwrap().unwrap();
    let gs_name = wire::str_field(gs, 1).unwrap().unwrap();
    assert_eq!(wire::str_field(gs, 6).unwrap().as_deref(), Some("*"), "password masked");
    assert_eq!(wire::repeated_bytes(created, 5).unwrap().len(), 1);
    join_session(&w, &mut host);

    // Room ID: stable, six digits; resolvable by the guest.
    let alias_req = Msg::new().bytes(1, "tenants/current").msg(2, &Msg::new().bytes(2, &gs_name));
    let a1 = w.unary(host.gs_conn, &format!("{GSS}CreateGameSessionShortAlias"), Some(&host.token), &alias_req.0).unwrap();
    let a2 = w.unary(host.gs_conn, &format!("{GSS}CreateGameSessionShortAlias"), Some(&host.token), &alias_req.0).unwrap();
    let alias = wire::str_field(&a1, 1).unwrap().unwrap();
    assert_eq!(alias, wire::str_field(&a2, 1).unwrap().unwrap());
    let room_id = alias.rsplit('/').next().unwrap();
    assert_eq!(room_id.len(), 6);
    let got = w.unary(guest.conn, &format!("{GSS}GetGameSessionShortAlias"), Some(&guest.token),
        &Msg::new().bytes(1, format!("tenants/current/GameSessionShortAliases/{room_id}")).0).unwrap();
    assert_eq!(wire::str_field(&got, 2).unwrap().as_deref(), Some(gs_name.as_str()));
    assert_eq!(w.unary(guest.conn, &format!("{GSS}GetGameSessionShortAlias"), Some(&guest.token),
        &Msg::new().bytes(1, "tenants/current/GameSessionShortAliases/000000x").0), Err(5));

    // Join: wrong password refused; right one seats the guest and announces it to the host.
    let host_members = send_frame(&w, &mut host, Msg::new().bytes(1, "userSessions/current").msg(3, &listen(2, true, "docs/__us")));
    assert_eq!(changes(&host_members).len(), 1);
    let join = |pw: &str| Msg::new().bytes(1, &gs_name).bytes(2, pw).msg(3, &player).into_bytes();
    assert_eq!(w.unary(guest.conn, &format!("{GSS}JoinGameSession"), Some(&guest.token), &join("0000")), Err(7));
    let joined = w.unary(guest.conn, &format!("{GSS}JoinGameSession"), Some(&guest.token), &join("1234")).unwrap();
    let mp = wire::bytes_field(&joined, 1).unwrap().unwrap();
    assert_eq!(wire::str_field(mp, 3).unwrap().as_deref(), Some(guest.uid.as_str()));
    let gs = wire::bytes_field(&joined, 2).unwrap().unwrap();
    assert_eq!(wire::repeated_bytes(gs, 12).unwrap().len(), 2);
    // SYNCSTATE-RE.md 9: a room's attributes carry the integer `rule` = 3 (the game never writes
    // it; without it the joiner plays the room as a public Race lobby and the race never starts).
    let attrs = wire::bytes_field(gs, 11).unwrap().unwrap();
    let rule = wire::repeated_bytes(attrs, 1).unwrap().into_iter()
        .find(|e| wire::str_field(e, 1).ok().flatten().as_deref() == Some("rule"))
        .expect("rule attribute");
    assert_eq!(Value::decode(wire::bytes_field(rule, 2).unwrap().unwrap()).unwrap(), Value::Int(3));
    let c = changes(&pushes(&mut host));
    assert_eq!(c[0].1, ChangeKind::Added as u64, "host sees the new member");
    // The guest's Gamesync session is the room's.
    join_session(&w, &mut guest);
    assert_eq!(get(&w, &guest, "docs/__gs/f").unwrap().get("gsid"), get(&w, &host, "docs/__gs/f").unwrap().get("gsid"));
    // Voice (VOICE_API.md §1): a friend room is one voice room, members in seat order.
    let room = w.core.voice_room(&host.uid).expect("the host is in a voice room");
    assert_eq!(room.members, vec![host.uid.clone(), guest.uid.clone()]);
    assert_eq!(w.core.voice_room(&guest.uid), Some(room));
    // A dev lobby has no roster: everyone counts as a friend.
    assert!(w.core.voice_friends(&host.uid, &guest.uid));
    assert!(!w.core.voice_friends(&host.uid, &host.uid));
}

#[test]
fn presence_reaches_friends_and_offline_is_announced() {
    let w = World::new(Policy::default());
    let a = sign_in(&w, &dev_ticket("Ana"));
    let b = sign_in(&w, &dev_ticket("Bia"));
    // B subscribes to friend users and presences.
    let (_, mut friends) = w.call(b.conn, "/nn.npln.friends.v1.Friends/SubscribeFriendUsers", Some(&b.token),
        Some(&Msg::new().bytes(1, "tenants/current/users/current").0), true);
    let snap = messages(&mut friends);
    let group = wire::repeated_bytes(&snap[0], 1).unwrap()[0];
    let fu = wire::bytes_field(group, 2).unwrap().unwrap();
    assert_eq!(wire::str_field(fu, 2).unwrap().as_deref(), Some(a.user.as_str()));
    let (_, mut presences) = w.call(b.conn, "/nn.npln.friends.v1.PresenceService/SubscribePresences", Some(&b.token),
        Some(&Msg::new().bytes(1, "tenants/current/users/current").0), true);
    messages(&mut presences);
    // A opens KeepAlive (ack), then publishes SessionId.
    let (ka, mut acks) = w.call(a.conn, "/nn.npln.friends.v1.PresenceService/KeepAlive", Some(&a.token),
        Some(&Msg::new().msg(3, &Msg::new().bytes(1, "tenants/current/users/current")).0), false);
    assert!(wire::bytes_field(&messages(&mut acks)[0], 1).unwrap().is_some(), "ack");
    let p = Msg::new().bytes(1, format!("{}/presence", a.user))
        .msg(3, &Msg::new().bytes(1, "SessionId").msg(2, &Value::str("room-1").encode()));
    w.core.message(a.conn, ka, &Msg::new().msg(1, &Msg::new().msg(1, &p).msg(2, &Msg::new().bytes(1, "attributes.SessionId"))).0);
    let got = messages(&mut presences);
    let last = got.last().unwrap();
    let presence = wire::bytes_field(wire::bytes_field(last, 1).unwrap().unwrap(), 1).unwrap().unwrap();
    assert_eq!(wire::varint_field(presence, 2).unwrap(), Some(1));
    let attr = wire::bytes_field(presence, 3).unwrap().unwrap();
    assert_eq!(wire::str_field(attr, 1).unwrap().as_deref(), Some("SessionId"));
    // The stream is paced by the server (LOBBY-RUST-PY-AUDIT.md 1): client frames (an update, then
    // heartbeats {2: {}}) are never answered — an answer makes the client send another heartbeat at
    // once (the ~220 frames/s loop); the timer sends one KeepAliveResponse per social refresh.
    for _ in 0..5 {
        w.core.message(a.conn, ka, &Msg::new().msg(2, &Msg::new()).0);
    }
    assert!(messages(&mut acks).is_empty(), "no answer per client frame");
    w.advance(21.0);
    w.core.tick();
    let pings = messages(&mut acks);
    assert_eq!(pings.len(), 1, "one paced KeepAliveResponse per refresh");
    assert!(wire::bytes_field(&pings[0], 1).unwrap().is_some());
    // A goes away: offline (state 2) reaches B. A stays in B's friend list: the list names friends,
    // presence says who is online.
    w.core.connection_closed(a.conn);
    let got = messages(&mut presences);
    let presence = wire::bytes_field(wire::bytes_field(got.last().unwrap(), 1).unwrap().unwrap(), 1).unwrap().unwrap();
    assert_eq!(wire::varint_field(presence, 2).unwrap(), Some(2));
    let snap = messages(&mut friends);
    assert_eq!(wire::repeated_bytes(snap.last().unwrap(), 1).unwrap().len(), 1);
}

/// The game maps the FIRST SubscribeFriendUsers message onto its system friend list and never
/// reads another (FRIEND-ROOMS-RE.md): that message must name friends that are not signed in yet
/// and friends connected with a token of an earlier sign-in, and it must wait for the roster.
#[test]
fn first_friend_users_message_names_friends_before_they_sign_in() {
    const FRIENDS: &str = "/nn.npln.friends.v1.Friends/SubscribeFriendUsers";
    const PRESENCES: &str = "/nn.npln.friends.v1.PresenceService/SubscribePresences";
    const KEEPALIVE: &str = "/nn.npln.friends.v1.PresenceService/KeepAlive";
    let parent = Msg::new().bytes(1, "tenants/current/users/current").into_bytes();
    let users_of = |m: &Vec<u8>| -> Vec<String> {
        wire::repeated_bytes(m, 1).unwrap().into_iter()
            .map(|g| wire::str_field(wire::bytes_field(g, 2).unwrap().unwrap(), 2).unwrap().unwrap()).collect()
    };
    let e = |name: &str, sid: &str, friends: &[&str]| lobby::RosterEntry {
        session_id: sid.into(), name: name.into(), alive: true, friends: friends.iter().map(|s| s.to_string()).collect() };
    let ana = format!("tenants/t-test/users/{}", lobby::users::ticket_user_id("ana"));

    // Roster mode (mkwmp lobby with roster_url): Bia signs in first, Ana holds a seat but her game
    // has not signed in yet.
    let w = World::new(Policy::default());
    w.core.roster(Vec::new());
    let b = sign_in(&w, &seat_ticket("Bia", "s-bia"));
    let (_, mut friends) = w.call(b.conn, FRIENDS, Some(&b.token), Some(&parent), true);
    w.advance(1.0);
    assert!(messages(&mut friends).is_empty(), "nothing before the roster names Bia's friends");
    w.core.roster(vec![e("Bia", "s-bia", &["Ana"])]);
    let first = messages(&mut friends);
    assert_eq!(users_of(&first[0]), vec![ana.clone()], "Ana is listed before she signs in");
    // Ana signs in, creates a room and publishes it: Bia's presence stream carries the SessionId.
    let (_, mut presences) = w.call(b.conn, PRESENCES, Some(&b.token), Some(&parent), true);
    messages(&mut presences);
    let a = sign_in(&w, &seat_ticket("Ana", "s-ana"));
    w.core.roster(vec![e("Bia", "s-bia", &["Ana"]), e("Ana", "s-ana", &["Bia"])]);
    let (ka, _acks) = w.call(a.conn, KEEPALIVE, Some(&a.token), Some(&Msg::new().msg(3, &Msg::new().bytes(1, "tenants/current/users/current")).0), false);
    let p = Msg::new().bytes(1, format!("{}/presence", a.user))
        .msg(3, &Msg::new().bytes(1, "SessionId").msg(2, &Value::str("room-1").encode()));
    w.core.message(a.conn, ka, &Msg::new().msg(1, &Msg::new().msg(1, &p).msg(2, &Msg::new().bytes(1, "attributes.SessionId"))).0);
    let got = messages(&mut presences);
    let presence = wire::bytes_field(wire::bytes_field(got.last().unwrap(), 1).unwrap().unwrap(), 1).unwrap().unwrap();
    assert_eq!(wire::str_field(presence, 1).unwrap().unwrap(), format!("{ana}/presence"), "same user id as the friend user");
    assert_eq!(wire::varint_field(presence, 2).unwrap(), Some(1));
    // A roster that never names the caller: answered at the deadline.
    let c = sign_in(&w, &seat_ticket("Cid", "s-cid"));
    let (_, mut late) = w.call(c.conn, FRIENDS, Some(&c.token), Some(&parent), true);
    w.advance(19.0);
    assert!(messages(&mut late).is_empty());
    w.advance(2.0);
    assert!(!messages(&mut late).is_empty());

    // Development (no roster): a player whose connection carries the token of an earlier sign-in
    // (lobby restarted, 2026-11-15) is listed too.
    let w = World::new(Policy::default());
    let a = sign_in(&w, &dev_ticket("Ana"));
    w.core.connection_closed(a.conn);
    let (_ka, _acks) = w.call(w.id(), KEEPALIVE, Some(&a.token), Some(&Msg::new().msg(3, &Msg::new().bytes(1, "tenants/current/users/current")).0), false);
    let b = sign_in(&w, &dev_ticket("Bia"));
    let (_, mut friends) = w.call(b.conn, FRIENDS, Some(&b.token), Some(&parent), true);
    assert_eq!(users_of(&messages(&mut friends)[0]), vec![ana]);
}

/// An unsigned seat ticket with a seat session id.
fn seat_ticket(name: &str, sid: &str) -> String {
    use base64::Engine;
    let payload = base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(format!(r#"{{"name":"{name}","sid":"{sid}"}}"#));
    format!("mkw1.{payload}.sig")
}

/// An unsigned seat ticket (accepted only by development lobbies without keys).
fn dev_ticket(name: &str) -> String {
    use base64::Engine;
    let payload = base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(format!(r#"{{"name":"{name}"}}"#));
    format!("mkw1.{payload}.sig")
}

#[test]
fn roster_limits_friends_to_mutual_and_ends_dead_seats() {
    let w = World::new(Policy::default());
    let a = sign_in(&w, &dev_ticket("Ana"));
    let _b = sign_in(&w, &dev_ticket("Bia"));
    let _c = sign_in(&w, &dev_ticket("Cid"));
    let (_, mut friends) = w.call(a.conn, "/nn.npln.friends.v1.Friends/SubscribeFriendUsers", Some(&a.token),
        Some(&Msg::new().bytes(1, "tenants/current/users/current").0), true);
    let count = |m: &Vec<u8>| wire::repeated_bytes(m, 1).unwrap().len();
    assert_eq!(count(&messages(&mut friends)[0]), 2, "dev: everyone online");
    let e = |name: &str, friends: &[&str]| lobby::RosterEntry {
        session_id: format!("s-{name}"), name: name.into(), alive: true, friends: friends.iter().map(|s| s.to_string()).collect() };
    w.core.roster(vec![e("Ana", &["Bia"]), e("Bia", &["Ana"]), e("Cid", &[])]);
    let got = messages(&mut friends);
    let last = got.last().unwrap();
    assert_eq!(count(last), 1, "only the mutual friend");
    let fu = wire::bytes_field(wire::repeated_bytes(last, 1).unwrap()[0], 2).unwrap().unwrap();
    assert!(wire::str_field(fu, 2).unwrap().unwrap().ends_with(&lobby::users::ticket_user_id("bia")));
}

#[test]
fn room_id_scanning_and_sign_in_floods_are_rate_limited() {
    let w = World::new(Policy::default());
    let p = sign_in(&w, &dev_ticket("Eve"));
    let lookup = |id: u32| w.unary(p.conn, &format!("{GSS}GetGameSessionShortAlias"), Some(&p.token),
        &Msg::new().bytes(1, format!("tenants/current/GameSessionShortAliases/{id:06}")).0);
    let results: Vec<_> = (0..20).map(lookup).collect();
    assert!(results[..10].iter().all(|r| *r == Err(5)), "unknown ids: NOT_FOUND");
    assert!(results[10..].iter().all(|r| *r == Err(8)), "then RESOURCE_EXHAUSTED");
    w.advance(60.0);
    assert_eq!(lookup(1), Err(5), "refills over time");

    // Sign-in is limited per IP before any user exists.
    let body = Msg::new().bytes(1, "tenants/t").msg(3, &Msg::new().bytes(1, "dev")).into_bytes();
    let got: Vec<_> = (0..45).map(|_| w.unary(w.id(), SIGN_IN, None, &body).map(|_| ())).collect();
    assert!(got[..39].iter().all(|r| r.is_ok()), "a LAN party behind one address signs in");
    assert!(got.contains(&Err(8)));
}

/// A Player as game 1.0.0 (rom_id, batching_key, course_id) or 1.8.0 (+ npln_user_id, clutch_id) sends it.
fn versioned_player(game_180: bool) -> Msg {
    let mut attrs = Fields::new()
        .with("rom_id", Value::str("cv1"))
        .with("batching_key", Value::str("cv1_world0"))
        .with("course_id", Value::Int(0));
    if game_180 {
        attrs = attrs.with("npln_user_id", Value::str("0123456789abcdef")).with("clutch_id", Value::str(""));
    }
    Msg::new().bytes(1, "tenants/current/users/current").msg(2, &attrs.encode())
}

fn search_as(w: &World, p: &mut Player, mode: &str, game_180: bool) -> Result<(), u32> {
    let body = Msg::new()
        .msg(2, &Msg::new().bytes(2, format!("tenants/current/matchmakingConfigs/{mode}")).msg(4, &versioned_player(game_180)))
        .into_bytes();
    let reply = w.unary(p.conn, CREATE, Some(&p.token), &body)?;
    let name = wire::str_field(&reply, 1).unwrap().unwrap();
    p.ticket = format!("tenants/current/matchmakingTickets/{}", name.rsplit('/').next().unwrap());
    let (_, rx) = w.call(p.conn, TRACK, Some(&p.token), Some(&Msg::new().bytes(1, &p.ticket).0), true);
    p.track = Some(rx);
    Ok(())
}

#[test]
fn game_versions_never_meet() {
    // Pia 1.0.0 and 1.8.0 drop each other's packets (GAME-1.8.0-PROTOCOL.md 3): never one lobby.
    let mut policy = Policy::default();
    policy.capacity.insert("Normal24".into(), 2);
    let w = World::new(policy);
    let mut ps: Vec<Player> = ["ana", "bia", "caio", "duda"].iter().map(|n| sign_in(&w, &dev_ticket(n))).collect();
    for (i, p) in ps.iter_mut().enumerate() {
        search_as(&w, p, "Normal24", i % 2 == 1).unwrap(); // 1.0.0, 1.8.0, 1.0.0, 1.8.0
    }
    let s: Vec<String> = ps.iter_mut().map(|p| assignment(p).expect("full lobby assigns").0).collect();
    assert_eq!(s[0], s[2], "the two 1.0.0 players");
    assert_eq!(s[1], s[3], "the two 1.8.0 players");
    assert_ne!(s[0], s[1]);

    // Clutch modes (1.8.0 mode 7) are refused until captured.
    let mut e = sign_in(&w, &dev_ticket("enzo"));
    assert_eq!(search_as(&w, &mut e, "Clutch24", true), Err(9));
    assert_eq!(search_as(&w, &mut e, "ClutchSurvival24", true), Err(9));

    // New 1.8.0 services answer empty/OK.
    for path in lobby::STUBS_180 {
        assert_eq!(w.unary(e.conn, path, Some(&e.token), b""), Ok(Vec::new()), "{path}");
    }

    // Rating documents: 1.8.0 callers get the extra keys, 1.0.0 callers exactly what they always got.
    let bulk = Msg::new().bytes(2, "docs/Rating/x").into_bytes();
    let rating = |p: &Player| {
        let r = w.unary(p.conn, "/nn.npln.ugcstore.v1.Ugcstore/BulkGetDocuments", Some(&p.token), &bulk).unwrap();
        let doc = wire::bytes_field(&r, 1).unwrap().unwrap();
        Fields::decode(wire::bytes_field(doc, 2).unwrap().unwrap()).unwrap()
    };
    let old = rating(&ps[0]);
    assert_eq!(old, Fields::new().with("Rating", Value::Double(1500.0)));
    let new = rating(&ps[1]);
    assert_eq!(new.get("RatingCoefficient"), Some(&Value::Double(1.0)));
    assert!(new.contains("RaceCount") && new.contains("RatingDisplay") && new.contains("DriverId") && new.contains("StickerId"));
}

#[test]
fn friend_rooms_stay_with_their_game_version() {
    let w = World::new(Policy::default());
    let mut host = sign_in(&w, &dev_ticket("Ana"));
    let old = sign_in(&w, &dev_ticket("Bia"));
    let new = sign_in(&w, &dev_ticket("Caio"));
    // Host on 1.8.0 creates a room and a Room ID.
    let session = Msg::new().msg(11, &Fields::new().with("rom_id", Value::str("cv1")).encode());
    let ticket = Msg::new().bytes(2, "tenants/current/matchmakingConfigs/Private24").msg(3, &versioned_player(true)).msg(6, &session);
    let reply = w.unary(host.conn, &format!("{GSS}CreateGameSessionCreationTicket"), Some(&host.token),
        &Msg::new().bytes(1, "tenants/current").msg(2, &ticket).0).unwrap();
    let ct = wire::str_field(&reply, 1).unwrap().unwrap();
    let (_, mut rx) = w.call(host.conn, &format!("{GSS}TrackGameSessionCreationTicket"), Some(&host.token), Some(&Msg::new().bytes(1, &ct).0), true);
    let out = drain(&mut rx);
    let Out::Message(created) = &out[0] else { panic!() };
    let gs_name = wire::str_field(wire::bytes_field(created, 6).unwrap().unwrap(), 1).unwrap().unwrap();
    join_session(&w, &mut host);
    let a = w.unary(host.gs_conn, &format!("{GSS}CreateGameSessionShortAlias"), Some(&host.token),
        &Msg::new().bytes(1, "tenants/current").msg(2, &Msg::new().bytes(2, &gs_name)).0).unwrap();
    let room_id = wire::str_field(&a, 1).unwrap().unwrap().rsplit('/').next().unwrap().to_owned();

    // The 1.0.0 player (known from its search) can't find or join it; the 1.8.0 player can.
    let mut o = old;
    search_as(&w, &mut o, "Normal24", false).unwrap();
    let lookup = Msg::new().bytes(1, format!("tenants/current/GameSessionShortAliases/{room_id}")).into_bytes();
    assert_eq!(w.unary(o.conn, &format!("{GSS}GetGameSessionShortAlias"), Some(&o.token), &lookup), Err(5));
    let join = |p: &Player, v180: bool| {
        w.unary(p.conn, &format!("{GSS}JoinGameSession"), Some(&p.token),
            &Msg::new().bytes(1, &gs_name).msg(3, &versioned_player(v180)).into_bytes())
    };
    assert_eq!(join(&o, false), Err(5));
    let batch = Msg::new().bytes(2, &gs_name).into_bytes();
    let listed = |p: &Player| wire::repeated_bytes(&w.unary(p.conn, &format!("{GSS}BatchGetGameSessions"), Some(&p.token), &batch).unwrap(), 1).unwrap().len();
    assert_eq!(listed(&o), 0, "a friend's other-version room is hidden");
    assert!(w.unary(new.conn, &format!("{GSS}GetGameSessionShortAlias"), Some(&new.token), &lookup).is_ok(), "unknown version: allowed");
    assert!(join(&new, true).is_ok());
    assert_eq!(listed(&new), 1);
}

const REFRESH: &str = "/nn.npln.auth.v1.Auth/RefreshToken";

/// Issue #15: the game refreshes its NPLN token ~58 min after signing in, on a new connection, and
/// aborts (NN_ABORT) unless the answer's Token.user parses as "tenants/<t>/users/<u>".
#[test]
fn token_refresh_answers_a_token_for_the_same_user() {
    let w = World::new(Policy::default());
    let conn = w.id();
    let body = Msg::new().bytes(1, "tenants/t-test").msg(3, &Msg::new().bytes(1, dev_ticket("Ana"))).into_bytes();
    let reply = w.unary(conn, SIGN_IN, None, &body).unwrap();
    let token = wire::bytes_field(&reply, 2).unwrap().unwrap();
    let user = wire::str_field(token, 1).unwrap().unwrap();
    let refresh = wire::str_field(token, 3).unwrap().unwrap();
    assert!(!refresh.starts_with("mkwmp-local-refresh-token"), "the refresh token proves the user");

    // 58 minutes later, a new connection, no bearer, the refresh token in the request.
    w.advance(58.0 * 60.0);
    let req = Msg::new().bytes(1, &user).bytes(2, &refresh).into_bytes();
    let reply = w.unary(w.id(), REFRESH, None, &req).expect("refreshed");
    let t = wire::bytes_field(&reply, 2).unwrap().expect("a Token in field 2");
    assert_eq!(wire::str_field(t, 1).unwrap().unwrap(), user, "Token.user = tenants/<t>/users/<u>");
    let access = wire::str_field(t, 2).unwrap().unwrap();
    assert!(!access.is_empty() && !wire::str_field(t, 3).unwrap().unwrap().is_empty());

    // The new access token works as a bearer: a refresh proven by it alone.
    let reply = w.unary(w.id(), REFRESH, Some(&access), &[]).expect("bearer refresh");
    assert_eq!(wire::str_field(wire::bytes_field(&reply, 2).unwrap().unwrap(), 1).unwrap().unwrap(), user);

    // No proof, the old guessable token, or a forged one: refused, never an empty OK.
    assert_eq!(w.unary(w.id(), REFRESH, None, &[]), Err(16));
    let old = Msg::new().bytes(2, "mkwmp-local-refresh-token-1").into_bytes();
    assert_eq!(w.unary(w.id(), REFRESH, None, &old), Err(16));
    let forged = Msg::new().bytes(2, format!("{refresh}x")).into_bytes();
    assert_eq!(w.unary(w.id(), REFRESH, None, &forged), Err(16));
}

#[test]
fn known_empty_calls_stay_ok_and_unknown_ones_are_unimplemented() {
    let w = World::new(Policy::default());
    let p = sign_in(&w, &dev_ticket("Bia"));
    assert_eq!(w.unary(p.conn, "/nn.npln.friends.v1.Friends/ActivateUser", Some(&p.token), &[]), Ok(Vec::new()));
    assert_eq!(w.unary(p.conn, "/nn.npln.auth.v1.Auth/ValidateToken", Some(&p.token), &[]), Ok(Vec::new()));
    assert_eq!(w.unary(p.conn, "/nn.npln.auth.v1.Auth/RefreshAnonymousUserToken", Some(&p.token), &[]), Err(12));
}

const DELEGATION: &str = "/nn.npln.matchmaking.v1.GameSessionService/IssueUserDelegationToken";
/// A follower's IssueUserDelegationToken: request {1 parent, 2 user, 3 user, 5 packed actions}.
fn delegate(w: &World, follower: &Player, leader_user: &str) -> Vec<u8> {
    let body = Msg::new()
        .bytes(1, "tenants/current")
        .bytes(2, &follower.user)
        .bytes(3, leader_user)
        .bytes(5, [1u8])
        .into_bytes();
    w.unary(follower.conn, DELEGATION, Some(&follower.token), &body).expect("delegation answered")
}

#[test]
fn delegation_reply_has_the_shape_the_game_parses() {
    let w = World::new(Policy::default());
    let ana = sign_in(&w, &dev_ticket("Ana"));
    let bia = sign_in(&w, &dev_ticket("Bia"));
    let reply = delegate(&w, &bia, &ana.user);
    let t = wire::bytes_field(&reply, 1).unwrap().expect("1: UserDelegationToken");
    assert_eq!(wire::str_field(t, 1).unwrap().as_deref(), Some(bia.user.as_str()), "echo of request 2");
    assert_eq!(wire::str_field(t, 2).unwrap().as_deref(), Some(ana.user.as_str()), "echo of request 3");
    assert_eq!(wire::bytes_field(t, 4).unwrap(), Some(&[1u8][..]), "packed actions [1]");
    assert!(wire::str_field(t, 5).unwrap().unwrap().starts_with("mkwmp-delegation-"));
    let d = wire::bytes_field(t, 6).unwrap().unwrap();
    assert_eq!(wire::varint_field(d, 1).unwrap(), Some(12 * 3600));
    // A malformed user resource is never echoed (the game aborts on one).
    let body = Msg::new().bytes(2, "nonsense").bytes(3, &ana.user).into_bytes();
    let reply = w.unary(bia.conn, DELEGATION, Some(&bia.token), &body).unwrap();
    let t = wire::bytes_field(&reply, 1).unwrap().unwrap();
    assert_eq!(wire::str_field(t, 1).unwrap(), None);
    // Not signed in: refused, never an empty OK.
    assert_eq!(w.unary(w.id(), DELEGATION, None, &body), Err(16));
}

fn lobby_name(p: &mut Player) -> String {
    assignment(p).expect("assigned").0
}

#[test]
fn a_follower_lands_in_its_leaders_lobby_and_follows_it_to_the_next() {
    let mut policy = Policy::default();
    policy.min_start = 1;
    policy.gather_secs = 0.0;
    policy.join_window_secs = 600.0;
    let w = World::new(policy);
    let mut ana = sign_in(&w, &dev_ticket("Ana"));
    let mut bia = sign_in(&w, &dev_ticket("Bia"));
    delegate(&w, &bia, &ana.user); // Bia follows Ana
    search(&w, &mut ana, "Normal24");
    let first = lobby_name(&mut ana);
    search(&w, &mut bia, "Normal24");
    assert_eq!(lobby_name(&mut bia), first, "the follower joins the leader's lobby");

    // The leader moves on: a new search ends the old seat; the follower follows to the new lobby.
    w.advance(1.0);
    search(&w, &mut ana, "Battle24");
    let second = lobby_name(&mut ana);
    assert_ne!(second, first);
    search(&w, &mut bia, "Battle24");
    assert_eq!(lobby_name(&mut bia), second, "the follower follows the leader");
}

#[test]
fn a_leaders_ticket_with_the_token_binds_the_follower() {
    let mut policy = Policy::default();
    policy.min_start = 1;
    policy.gather_secs = 0.0;
    policy.join_window_secs = 600.0;
    let w = World::new(policy);
    let mut ana = sign_in(&w, &dev_ticket("Ana"));
    let mut bia = sign_in(&w, &dev_ticket("Bia"));
    // A delegation that names no leader binds nobody yet...
    let body = Msg::new().bytes(2, "tenants/current/users/current").bytes(5, [1u8]).into_bytes();
    let reply = w.unary(bia.conn, DELEGATION, Some(&bia.token), &body).unwrap();
    let token = wire::str_field(wire::bytes_field(&reply, 1).unwrap().unwrap(), 5).unwrap().unwrap();
    // ...until the leader's ticket carries it (CreateMatchmakingTicket field 3).
    let player = Msg::new().bytes(1, "tenants/current/users/current").bytes(2, "attrs");
    let body = Msg::new()
        .msg(2, &Msg::new().bytes(2, "tenants/current/matchmakingConfigs/Normal24").msg(4, &player))
        .bytes(3, &token)
        .into_bytes();
    let reply = w.unary(ana.conn, CREATE, Some(&ana.token), &body).unwrap();
    let name = wire::str_field(&reply, 1).unwrap().unwrap();
    ana.ticket = format!("tenants/current/matchmakingTickets/{}", name.rsplit('/').next().unwrap());
    let (_, rx) = w.call(ana.conn, TRACK, Some(&ana.token), Some(&Msg::new().bytes(1, &ana.ticket).into_bytes()), true);
    ana.track = Some(rx);
    let first = lobby_name(&mut ana);
    search(&w, &mut bia, "Normal24");
    assert_eq!(lobby_name(&mut bia), first);
}

#[test]
fn a_companion_document_binds_and_unbinds() {
    let mut policy = Policy::default();
    policy.min_start = 1;
    policy.gather_secs = 0.0;
    policy.join_window_secs = 600.0;
    let w = World::new(policy);
    let mut ana = sign_in(&w, &dev_ticket("Ana"));
    let mut bia = sign_in(&w, &dev_ticket("Bia"));
    let companion = |state: i64, leader: &str| {
        let mut f = Fields::default();
        f.set("companion_state", Value::Int(state));
        f.set("leader_user_id", Value::str(leader));
        f
    };
    // Bia (not yet seated: its own solo space) says it follows Ana.
    write(&w, &bia, "docs/Companion/2", companion(3, &ana.uid), None);
    search(&w, &mut ana, "Normal24");
    let first = lobby_name(&mut ana);
    search(&w, &mut bia, "Normal24");
    assert_eq!(lobby_name(&mut bia), first);
    // State 0: no follower any more (the group is over; placement is normal again).
    write(&w, &bia, "docs/Companion/2", companion(0, ""), None);
}

// ---- bans for name spoofing ------------------------------------------------------------------

const BANNED: u32 = lobby::code::PERMISSION_DENIED;

fn try_write(w: &World, p: &Player, name: &str, fields: Fields) -> Result<Vec<u8>, u32> {
    let body = Msg::new().msg(1, &Msg::new().msg(1, &Msg::new().msg(1, &Msg::new().bytes(1, name).msg(2, &fields.encode())))).into_bytes();
    w.unary(p.gs_conn, WRITE, Some(&p.token), &body)
}

fn name_field(name: &str) -> Fields {
    Fields::new().with("name", Value::str(name)).with("play_history_key", Value::Bytes(vec![7; 8]))
}

/// Two players matched and in their session, both listening to the member list and PlayerStatic.
fn matched(w: &World, a: &mut Player, b: &mut Player) -> (i64, i64) {
    search(w, a, "Battle24");
    search(w, b, "Battle24");
    w.advance(11.0);
    assignment(a).expect("assigned");
    assignment(b).expect("assigned");
    let (ua, ub) = (ussid_of(&join_session(w, a)), ussid_of(&join_session(w, b)));
    for p in [a, b] {
        send_frame(w, p, Msg::new().bytes(1, "userSessions/current").msg(3, &listen(2, true, "docs/__us")));
        send_frame(w, p, Msg::new().bytes(1, "userSessions/current").msg(3, &listen(4, true, "docs/PlayerStatic")));
    }
    (ua, ub)
}

#[test]
fn with_bans_off_a_claimed_name_is_only_logged_and_replaced() {
    let w = World::with_config(CoreConfig { ban_enforce: false, ..CoreConfig::default() });
    let mut ana = sign_in_from(&w, &dev_ticket("Ana"), Some("203.0.113.1")).unwrap();
    let mut bia = sign_in_from(&w, &dev_ticket("Bia"), Some("203.0.113.2")).unwrap();
    let (_, ussid_b) = matched(&w, &mut ana, &mut bia);
    let ps = format!("docs/PlayerStatic/{ussid_b}");
    pushes(&mut ana);
    try_write(&w, &bia, &ps, name_field("Ana")).expect("not refused");
    try_write(&w, &bia, &ps, name_field("Local")).expect("not refused");
    assert!(w.core.pending_bans().is_empty(), "nothing for the seat server");
    assert_eq!(get(&w, &ana, &ps).unwrap().get("name").and_then(Value::as_str), Some("Bia"), "others still see the ticket's name");
    let c = changes(&pushes(&mut ana));
    assert!(c.iter().all(|c| c.1 != ChangeKind::Removed as u64), "nobody leaves: {c:?}");
    assert_eq!(w.core.counts()["Battle24"].players, 2);
    assert!(sign_in_from(&w, &dev_ticket("Bia"), Some("203.0.113.2")).is_ok(), "name and address still sign in");
}

#[test]
fn a_game_claiming_another_name_is_banned_and_dropped_at_once() {
    let w = World::new(Policy::default());
    let mut ana = sign_in_from(&w, &dev_ticket("Ana"), Some("203.0.113.1")).unwrap();
    let mut bia = sign_in_from(&w, &dev_ticket("Bia"), Some("203.0.113.2")).unwrap();
    let (_, ussid_b) = matched(&w, &mut ana, &mut bia);
    let ps = format!("docs/PlayerStatic/{ussid_b}");

    // Its own name, as the unmodified game writes it: stored, nobody banned.
    try_write(&w, &bia, &ps, name_field("Bia")).unwrap();
    assert_eq!(get(&w, &ana, &ps).unwrap().get("name").and_then(Value::as_str), Some("Bia"));
    assert!(w.core.pending_bans().is_empty());
    pushes(&mut ana);

    // Another name: refused, banned, and gone from the lobby without the reconnect grace.
    assert_eq!(try_write(&w, &bia, &ps, name_field("Ana")), Err(BANNED));
    let c = changes(&pushes(&mut ana));
    let removed = |name: &str| c.iter().any(|c| c.1 == ChangeKind::Removed as u64 && c.2 == name);
    assert!(removed(&format!("docs/__us/{ussid_b}")) && removed(&ps), "the others see the member and its documents removed: {c:?}");
    assert!(c.iter().all(|c| c.3.get("name").and_then(Value::as_str) != Some("Ana")), "the claimed name reached nobody");
    assert_eq!(w.core.counts()["Battle24"].players, 1);
    assert!(get(&w, &ana, &ps).is_none());
    // Its streams ended, its tokens are dead, the name and the address can't sign in.
    let ended = drain(&mut bia.kus.as_mut().unwrap().1);
    assert!(ended.iter().any(|o| matches!(o, Out::End { grpc_status: BANNED, message: Some(m) } if m == lobby::BAN_MESSAGE)), "{ended:?}");
    let player = Msg::new().bytes(1, "tenants/current/users/current").bytes(2, "attrs");
    let ticket = Msg::new().msg(2, &Msg::new().bytes(2, "tenants/current/matchmakingConfigs/Battle24").msg(4, &player)).into_bytes();
    assert_eq!(w.unary(bia.conn, CREATE, Some(&bia.token), &ticket), Err(BANNED));
    assert_eq!(w.unary(w.id(), CREATE, Some(&bia.token), &ticket), Err(BANNED), "on a new connection too");
    assert!(w.core.voice_user(&dev_ticket("Bia")).is_none());
    assert_eq!(sign_in_from(&w, &dev_ticket("bia"), Some("198.51.100.9")).err(), Some(BANNED), "the name, from anywhere");
    assert_eq!(sign_in_from(&w, &dev_ticket("Cid"), Some("203.0.113.2")).err(), Some(BANNED), "the address, under any name");
    assert!(sign_in_from(&w, &dev_ticket("Cid"), Some("203.0.113.3")).is_ok());

    // What the seat server is told, once.
    let pending = w.core.pending_bans();
    assert_eq!(pending.len(), 1);
    assert_eq!((pending[0].name.as_str(), pending[0].ip.as_deref(), pending[0].reason.as_str()), ("Bia", Some("203.0.113.2"), "name spoofing"));
    assert!(pending[0].detail.contains("\"Ana\""), "{}", pending[0].detail);

    w.core.bans_delivered(&pending);
    assert!(w.core.pending_bans().is_empty());
    assert_eq!(sign_in_from(&w, &dev_ticket("Bia"), Some("198.51.100.9")).err(), Some(BANNED), "delivered: still enforced here");

    // The seat server's list is what holds from then on: the address for 14 days, the name for good.
    let at = pending[0].at;
    w.core.set_bans(&["bia".into()], &[("203.0.113.2".into(), at + 14 * 86_400)]);
    assert_eq!(sign_in_from(&w, &dev_ticket("Bia"), Some("198.51.100.9")).err(), Some(BANNED));
    w.advance(14.0 * 86_400.0 - 60.0);
    assert_eq!(sign_in_from(&w, &dev_ticket("Cid"), Some("203.0.113.2")).err(), Some(BANNED), "day 13: still banned");
    w.advance(120.0);
    assert!(sign_in_from(&w, &dev_ticket("Cid"), Some("203.0.113.2")).is_ok(), "the address ban ended");
    assert_eq!(sign_in_from(&w, &dev_ticket("Bia"), Some("203.0.113.2")).err(), Some(BANNED), "the name ban never ends");
    // ...until the admin lifts it.
    w.core.set_bans(&[], &[]);
    assert!(sign_in_from(&w, &dev_ticket("Bia"), Some("203.0.113.2")).is_ok());
}

#[test]
fn a_ban_from_the_seat_server_drops_a_connected_player() {
    let w = World::new(Policy::default());
    let mut ana = sign_in(&w, &dev_ticket("Ana"));
    let mut bia = sign_in(&w, &dev_ticket("Bia"));
    let (_, ussid_b) = matched(&w, &mut ana, &mut bia);
    pushes(&mut ana);
    w.core.set_bans(&["BIA".into()], &[]);
    let c = changes(&pushes(&mut ana));
    assert!(c.iter().any(|c| c.1 == ChangeKind::Removed as u64 && c.2 == format!("docs/__us/{ussid_b}")), "{c:?}");
    assert!(drain(&mut bia.kus.as_mut().unwrap().1).iter().any(|o| matches!(o, Out::End { grpc_status: BANNED, .. })));
    assert!(w.core.pending_bans().is_empty(), "nothing to report back");
}

#[test]
fn own_names_legacy_users_and_refused_documents_ban_nobody() {
    let w = World::new(Policy::default());
    let mut dev = sign_in(&w, "dev"); // a local test user: no ticket, no registered name
    let mut ana = sign_in(&w, &dev_ticket("Ana"));
    let (ussid_d, ussid_a) = matched(&w, &mut dev, &mut ana);
    assert_ne!(ussid_a, ussid_d);
    let (ps_a, ps_d) = (format!("docs/PlayerStatic/{ussid_a}"), format!("docs/PlayerStatic/{ussid_d}"));
    for own in ["Ana", "ana", "ANA", "  Ana ", ""] {
        try_write(&w, &ana, &ps_a, name_field(own)).unwrap();
        assert_eq!(get(&w, &dev, &ps_a).unwrap().get("name").and_then(Value::as_str), Some("Ana"), "claimed {own:?}");
    }
    // Without a name in it, and in other documents: nothing to compare.
    try_write(&w, &ana, &ps_a, Fields::new().with("p2p_sid", Value::Int(7))).unwrap();
    try_write(&w, &ana, &format!("docs/Companion/{ussid_a}"), Fields::new().with("name", Value::str("Zed"))).unwrap();
    // Another member's PlayerStatic: refused as before (may_write), not a name claimed for oneself.
    try_write(&w, &ana, &ps_d, name_field("Zed")).unwrap();
    assert!(get(&w, &ana, &ps_d).is_none());
    // A user without a ticket name writes whatever its console says.
    try_write(&w, &dev, &ps_d, name_field("Local")).unwrap();
    assert_eq!(get(&w, &ana, &ps_d).unwrap().get("name").and_then(Value::as_str), Some("Local"));
    assert!(w.core.pending_bans().is_empty());
    assert_eq!(w.core.counts()["Battle24"].players, 2);
}

/// Split screen (live, 2026-11-27): one console with two local players is ONE user, one ussid and
/// one seat whose ticket carries two Players; both local players write the launcher's name into
/// the seat's one PlayerStatic. Never a ban.
#[test]
fn a_split_screen_seat_writes_its_one_name_for_both_local_players() {
    let mut policy = Policy::default();
    policy.min_start = 1;
    policy.gather_secs = 0.0;
    let w = World::new(policy);
    let mut ana = sign_in(&w, &dev_ticket("Ana"));
    let player = Msg::new().bytes(1, "tenants/current/users/current").bytes(2, "attrs");
    let body = Msg::new()
        .msg(2, &Msg::new().bytes(2, "tenants/current/matchmakingConfigs/Battle24").msg(4, &player).msg(4, &player))
        .into_bytes();
    let reply = w.unary(ana.conn, CREATE, Some(&ana.token), &body).unwrap();
    let name = wire::str_field(&reply, 1).unwrap().unwrap();
    ana.ticket = format!("tenants/current/matchmakingTickets/{}", name.rsplit('/').next().unwrap());
    let (_, rx) = w.call(ana.conn, TRACK, Some(&ana.token), Some(&Msg::new().bytes(1, &ana.ticket).into_bytes()), true);
    ana.track = Some(rx);
    w.advance(1.0);
    assignment(&mut ana).expect("assigned");
    assert_eq!(w.core.counts()["Battle24"].players, 2, "one seat, two players");
    let ussid = ussid_of(&join_session(&w, &mut ana));
    let ps = format!("docs/PlayerStatic/{ussid}");
    for lid in [0, 1] {
        try_write(&w, &ana, &ps, Fields::new().with("p2p_sid", Value::Int(77)).with("p2p_lid", Value::Int(lid))).unwrap();
        try_write(&w, &ana, &ps, name_field("Ana")).unwrap();
    }
    // A second document for the second local player (the game writes none today): not this seat's
    // own, so refused and never compared.
    try_write(&w, &ana, &format!("docs/PlayerStatic/{}", ussid + 1), name_field("Guest")).unwrap();
    assert!(w.core.pending_bans().is_empty());
    assert_eq!(w.core.counts()["Battle24"].players, 2);
    assert_eq!(get(&w, &ana, &ps).unwrap().get("name").and_then(Value::as_str), Some("Ana"));
}

#[test]
fn a_spoofer_on_this_machine_loses_the_name_not_the_address() {
    let w = World::new(Policy::default());
    let mut ana = sign_in(&w, &dev_ticket("Ana"));
    let mut bia = sign_in(&w, &dev_ticket("Bia"));
    let (_, ussid_b) = matched(&w, &mut ana, &mut bia);
    assert_eq!(try_write(&w, &bia, &format!("docs/PlayerStatic/{ussid_b}"), name_field("Nintendo")), Err(BANNED));
    let pending = w.core.pending_bans();
    assert_eq!((pending[0].name.as_str(), pending[0].ip.as_deref()), ("Bia", None), "loopback is never banned");
    assert_eq!(sign_in_from(&w, &dev_ticket("Bia"), None).err(), Some(BANNED));
    assert!(sign_in_from(&w, &dev_ticket("Cid"), None).is_ok());
    // Queued again by a restarted lobby (its queue file): enforced and still to deliver.
    let w2 = World::new(Policy::default());
    w2.core.requeue_bans(pending.clone());
    assert_eq!(sign_in_from(&w2, &dev_ticket("Bia"), None).err(), Some(BANNED));
    assert_eq!(w2.core.pending_bans(), pending);
}