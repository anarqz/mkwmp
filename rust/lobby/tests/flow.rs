//! End-to-end through the Core's transport API: game clients sign in, search, get matched,
//! join the Gamesync session, play a match through the referee and leave. Wire layouts are the
//! ones the game was verified with (server/mkwmp_server.py).

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
}

impl World {
    fn new(policy: Policy) -> Self {
        let clock = Arc::new(AtomicU64::new(1_800_000_000_000));
        let c = clock.clone();
        let core = Core::with_clock(CoreConfig::default(), policy, Arc::new(move || c.load(Ordering::SeqCst) as f64 / 1000.0));
        Self { core, clock, next: AtomicU64::new(1) }
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
        let peer: SocketAddr = "127.0.0.1:5000".parse().unwrap();
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
    let conn = w.id();
    let body = Msg::new().bytes(1, "tenants/t-test").msg(3, &Msg::new().bytes(1, seat_token)).into_bytes();
    let reply = w.unary(conn, SIGN_IN, None, &body).unwrap();
    let token = wire::bytes_field(&reply, 2).unwrap().unwrap();
    let user = wire::str_field(token, 1).unwrap().unwrap();
    let access = wire::str_field(token, 2).unwrap().unwrap();
    let uid = user.rsplit('/').next().unwrap().to_owned();
    Player { conn, gs_conn: w.id(), user, uid, token: access, ticket: String::new(), track: None, kus: None }
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
    w.advance(11.0);
    let (session_a, members) = assignment(&mut a).expect("assigned after the gather wait");
    let (session_b, _) = assignment(&mut b).unwrap();
    assert_eq!(session_a, session_b);
    assert_eq!(members, vec![a.user.clone(), b.user.clone()]);
    assert_eq!(w.core.counts()["Battle24"].players, 2);

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
    write(&w, &a, &ps, Fields::new().with("name", Value::str("Local")), Some(&["name"]));
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
    let w = World { core, clock: Arc::new(AtomicU64::new(0)), next: AtomicU64::new(1) };
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
    let c = changes(&pushes(&mut host));
    assert_eq!(c[0].1, ChangeKind::Added as u64, "host sees the new member");
    // The guest's Gamesync session is the room's.
    join_session(&w, &mut guest);
    assert_eq!(get(&w, &guest, "docs/__gs/f").unwrap().get("gsid"), get(&w, &host, "docs/__gs/f").unwrap().get("gsid"));
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
    // A goes away: offline (state 2) reaches B, and B's friend list drops A.
    w.core.connection_closed(a.conn);
    let got = messages(&mut presences);
    let presence = wire::bytes_field(wire::bytes_field(got.last().unwrap(), 1).unwrap().unwrap(), 1).unwrap().unwrap();
    assert_eq!(wire::varint_field(presence, 2).unwrap(), Some(2));
    let snap = messages(&mut friends);
    assert!(wire::repeated_bytes(snap.last().unwrap(), 1).unwrap().is_empty());
}

/// An unsigned seat ticket (accepted only by development lobbies without keys).
fn dev_ticket(name: &str) -> String {
    use base64::Engine;
    let payload = base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(format!(r#"{{"name":"{name}"}}"#));
    format!("mkw1.{payload}.sig")
}
