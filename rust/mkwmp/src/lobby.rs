//! `server lobby`: the gateway (TLS/h2, gRPC framing, TURN, Pia NAT) in front
//! of the lobby Core (the game protocol), plus the lobby-count report to the
//! seat server.

use std::error::Error;
use std::sync::Arc;
use std::time::Duration;

use tokio::sync::mpsc::{unbounded_channel, UnboundedSender};

/// The gateway's view of the Core. The two crates don't depend on each
/// other; this converts their call and reply types.
struct Adapter(lobby::Core);

impl gateway::Backend for Adapter {
    fn open(&self, call: &gateway::Call, out: UnboundedSender<gateway::Out>) {
        let (tx, mut rx) = unbounded_channel::<lobby::Out>();
        self.0.open(
            &lobby::Call {
                conn: call.conn,
                stream: call.stream,
                path: call.path,
                authority: call.authority,
                bearer: call.bearer,
                peer: call.peer,
            },
            tx,
        );
        // Ends when the Core drops the stream's sender (after End, or never
        // for a stream it keeps open).
        tokio::spawn(async move {
            while let Some(o) = rx.recv().await {
                let g = match o {
                    lobby::Out::Message(m) => gateway::Out::Message(m),
                    lobby::Out::End { grpc_status, message } => gateway::Out::End { grpc_status, message },
                };
                if out.send(g).is_err() {
                    break;
                }
            }
        });
    }
    fn message(&self, conn: u64, stream: u64, body: &[u8]) {
        self.0.message(conn, stream, body)
    }
    fn half_closed(&self, conn: u64, stream: u64) {
        self.0.half_closed(conn, stream)
    }
    fn reset(&self, conn: u64, stream: u64) {
        self.0.reset(conn, stream)
    }
    fn connection_closed(&self, conn: u64) {
        self.0.connection_closed(conn)
    }
}

/// The voice relay's view of the Core: ticket check, rooms, account ids.
struct VoiceRooms(lobby::Core);

impl gateway::voice::Rooms for VoiceRooms {
    fn user(&self, ticket: &str) -> Option<String> {
        self.0.voice_user(ticket)
    }
    fn room(&self, uid: &str) -> Option<(u128, Vec<String>)> {
        self.0.voice_room(uid).map(|r| (r.lobby.as_u128(), r.members))
    }
    fn account(&self, uid: &str) -> u64 {
        lobby::account_id(uid)
    }
    fn friends(&self, a: &str, b: &str) -> bool {
        self.0.voice_friends(a, b)
    }
}

fn policy(p: &config::LobbyPolicy) -> lobby::Policy {
    let mut out = lobby::Policy::default();
    out.capacity.extend(p.capacity.iter().map(|(k, v)| (k.clone(), *v)));
    if let Some(v) = p.default_capacity {
        out.default_capacity = v;
    }
    if let Some(v) = p.min_start {
        out.min_start = v;
    }
    if let Some(v) = p.gather_secs {
        out.gather_secs = v;
    }
    if let Some(v) = p.leave_grace_secs {
        out.leave_grace_secs = v;
    }
    if let Some(v) = p.connect_timeout_secs {
        out.connect_timeout_secs = v;
    }
    if p.maxu.is_some() {
        out.maxu = p.maxu;
    }
    if let Some(v) = p.friend_join_public {
        out.friend_join_public = v;
    }
    if let Some(v) = p.rejoin {
        out.rejoin = v;
    }
    if let Some(v) = p.join_window_secs {
        out.join_window_secs = v;
    }
    out
}

pub async fn serve(cfg: &config::ServerConfig) -> Result<(), Box<dyn Error>> {
    let l = &cfg.lobby;
    let token_secret = match &l.token_secret_file {
        Some(p) => {
            let b = std::fs::read(p).map_err(|e| format!("lobby.token_secret_file {}: {e}", p.display()))?;
            if b.len() < 32 {
                return Err(format!("lobby.token_secret_file {}: needs at least 32 bytes", p.display()).into());
            }
            Some(b)
        }
        None => {
            eprintln!("mkwmp lobby: no token_secret_file; a restart will forget running games");
            None
        }
    };
    for f in [&cfg.tls.cert, &cfg.tls.key] {
        if !f.exists() {
            return Err(format!("[tls] {} is missing (the pair the game patch trusts)", f.display()).into());
        }
    }
    let core = lobby::Core::with_policy(
        lobby::CoreConfig {
            public_host: l.public_host.clone(),
            turn_port: l.turn_port,
            nat_port: l.nat_port,
            gamesync_host: l.gamesync_host.clone(),
            gamesync_port: if l.gamesync_port == 0 { l.npln_port } else { l.gamesync_port },
            token_secret,
            ticket_keys: cfg.ticket.public_keys.clone(),
            require_ticket: l.require_ticket,
        },
        policy(&l.policy),
    );
    if l.require_ticket && cfg.ticket.public_keys.is_empty() {
        return Err("lobby.require_ticket needs [ticket] public_keys".into());
    }
    if let (Some(url), Some(token_file)) = (&l.roster_url, &l.roster_token_file) {
        let token = std::fs::read_to_string(token_file)
            .map_err(|e| format!("lobby.roster_token_file {}: {e}", token_file.display()))?
            .lines()
            .next()
            .unwrap_or("")
            .trim()
            .to_string();
        // Roster mode from the first sign-in: nobody is a friend until the seat server says so, and
        // a first friend-users answer waits for it.
        core.roster(Vec::new());
        tokio::spawn(roster_sync(core.clone(), url.trim_end_matches('/').to_string(), token.clone(), l.roster_secs.max(1)));
        // Bans: the ones an earlier run could not deliver, then the seat server's list.
        if let Some(file) = &l.ban_queue_file {
            core.requeue_bans(load_ban_queue(file));
        }
        tokio::spawn(ban_sync(core.clone(), url.trim_end_matches('/').to_string(), token, l.roster_secs.max(1), l.ban_queue_file.clone()));
    } else {
        eprintln!("mkwmp lobby: no roster_url; bans last until this process ends");
    }
    let gw = gateway::GatewayConfig {
        host: l.host.parse()?,
        public_host: l.public_host.parse()?,
        npln_port: l.npln_port,
        gamesync_port: l.gamesync_port,
        turn_port: l.turn_port,
        nat_port: l.nat_port,
        turn_username: lobby::ice::TURN_USERNAME.into(),
        turn_password: lobby::ice::TURN_PASSWORD.into(),
        relay_ports: l.relay_range()?,
        tls: Some((cfg.tls.cert.clone(), cfg.tls.key.clone())),
    };
    let voice = if l.voice_port != 0 {
        let addr = std::net::SocketAddr::new(gw.host, l.voice_port);
        let sock = tokio::net::UdpSocket::bind(addr).await.map_err(|e| format!("voice relay on {addr}: {e}"))?;
        gateway::log::line(format!("voice relay on {addr} (udp)"));
        Some(sock)
    } else {
        None
    };
    let rooms = Arc::new(VoiceRooms(core.clone()));
    let stats_secs = std::env::var("MKWMP_VOICE_STATS").ok().and_then(|v| v.parse().ok()).unwrap_or(0.0);
    let video = gateway::voice::Options { video: l.video, video_budget: l.video_budget, stats_secs };
    let voice = async move {
        match voice {
            Some(sock) => gateway::voice::run(sock, rooms, video).await,
            None => std::future::pending().await,
        }
    };
    tokio::select! {
        r = gateway::serve(gw, Arc::new(Adapter(core))) => r?,
        r = voice => r?,
    }
    Ok(())
}

/// Every `roster_secs`: the seat server learns the live lobby counts, and the Core
/// learns which signed-in seats are still alive and who their mutual friends
/// are (friend lists and presence show only those).
async fn roster_sync(core: lobby::Core, url: String, token: String, every: u64) {
    let http = reqwest::Client::builder().timeout(Duration::from_secs(5)).build().expect("http client");
    let mut seen = Seen::default();
    let mut failing = false;
    loop {
        let lobbies: std::collections::BTreeMap<String, usize> =
            core.counts().into_iter().map(|(mode, c)| (mode, c.lobbies)).collect();
        let sids = core.live_sessions();
        let rooms = core.rooms();
        let mut entries = vec![];
        let mut ok = true;
        // The seat server takes at most 256 ids per call; counts ride on the first.
        let chunks: Vec<&[String]> = if sids.is_empty() { vec![&[]] } else { sids.chunks(256).collect() };
        for (i, chunk) in chunks.into_iter().enumerate() {
            // Each seat's room (SEAT_API.md §2: rooms), for the seats of this chunk only.
            let chunk_rooms: serde_json::Map<String, serde_json::Value> = chunk
                .iter()
                .filter_map(|sid| {
                    let (lobby, ussid, ussids) = rooms.get(sid)?;
                    Some((sid.clone(), serde_json::json!({"lobby": lobby, "ussid": ussid, "ussids": ussids})))
                })
                .collect();
            let mut body = serde_json::json!({ "sessionIds": chunk, "rooms": chunk_rooms });
            if i == 0 {
                body["lobbies"] = serde_json::json!(lobbies);
            }
            let res = http.post(format!("{url}/seat/v1/lobby/roster")).bearer_auth(&token).json(&body).send().await;
            let reply = match res {
                Ok(r) if r.status().is_success() => r.json::<serde_json::Value>().await.ok(),
                Ok(r) => {
                    if !failing {
                        eprintln!("mkwmp lobby: roster call answered {}", r.status());
                    }
                    None
                }
                Err(e) => {
                    if !failing {
                        eprintln!("mkwmp lobby: roster call failed: {e}");
                    }
                    None
                }
            };
            match reply {
                Some(v) => entries.extend(seen.entries(&v)),
                None => {
                    ok = false;
                    break;
                }
            }
        }
        failing = !ok;
        if ok {
            seen.alive_in.retain(|sid, _| sids.contains(sid));
        }
        // Never act on a partial or failed answer: a missing seat would be
        // kicked mid-race.
        if ok && !sids.is_empty() {
            core.roster(entries);
        }
        tokio::time::sleep(Duration::from_secs(every)).await;
    }
}

/// Bans one call reports (the seat server's MAX_BAN_BATCH).
const BAN_BATCH: usize = 32;

fn ban_json(b: &lobby::BanReport) -> serde_json::Value {
    serde_json::json!({"name": b.name, "ip": b.ip, "reason": b.reason, "detail": b.detail, "at": b.at})
}

fn ban_from_json(v: &serde_json::Value) -> Option<lobby::BanReport> {
    Some(lobby::BanReport {
        name: v["name"].as_str()?.to_string(),
        ip: v["ip"].as_str().map(str::to_string),
        reason: v["reason"].as_str().unwrap_or("name spoofing").to_string(),
        detail: v["detail"].as_str().unwrap_or("").to_string(),
        at: v["at"].as_u64().unwrap_or(0),
    })
}

/// The bans a previous run made and the seat server never confirmed.
fn load_ban_queue(file: &std::path::Path) -> Vec<lobby::BanReport> {
    let Ok(text) = std::fs::read_to_string(file) else { return vec![] };
    let v: serde_json::Value = serde_json::from_str(&text).unwrap_or_default();
    let bans: Vec<_> = v.as_array().into_iter().flatten().filter_map(ban_from_json).collect();
    if !bans.is_empty() {
        eprintln!("mkwmp lobby: {} ban(s) from {} still to deliver", bans.len(), file.display());
    }
    bans
}

/// Keep the undelivered bans on disk (written before each delivery try; empty once delivered).
fn save_ban_queue(file: Option<&std::path::Path>, bans: &[lobby::BanReport], last: &mut Option<String>) {
    let Some(file) = file else { return };
    let text = serde_json::Value::Array(bans.iter().map(ban_json).collect()).to_string();
    if last.as_deref() == Some(text.as_str()) || (last.is_none() && bans.is_empty() && !file.exists()) {
        return;
    }
    match std::fs::write(file, &text) {
        Ok(()) => *last = Some(text),
        Err(e) => eprintln!("mkwmp lobby: ban queue {}: {e}", file.display()),
    }
}

/// Bans (SEAT_API.md §2d): the Core bans a spoofer and drops it at once; here the seat server is
/// told (`POST /seat/v1/lobby/bans`), at once and again until it answers, so the ban is kept
/// across restarts and applies to seats too. The answer is every ban in force, which the Core
/// then enforces: bans survive a lobby restart, and one the admin lifted is lifted here within
/// `every` seconds. The same report twice is harmless (the seat server ignores a name it has).
async fn ban_sync(core: lobby::Core, url: String, token: String, every: u64, queue: Option<std::path::PathBuf>) {
    let http = reqwest::Client::builder().timeout(Duration::from_secs(5)).build().expect("http client");
    let signal = core.ban_signal();
    let (mut rev, mut failing, mut saved) = (String::new(), false, None);
    loop {
        let pending = core.pending_bans();
        save_ban_queue(queue.as_deref(), &pending, &mut saved);
        let batch = &pending[..pending.len().min(BAN_BATCH)];
        let body = serde_json::json!({"rev": rev, "bans": batch.iter().map(ban_json).collect::<Vec<_>>()});
        let res = http.post(format!("{url}/seat/v1/lobby/bans")).bearer_auth(&token).json(&body).send().await;
        let reply = match res {
            Ok(r) if r.status().is_success() => r.json::<serde_json::Value>().await.ok(),
            Ok(r) => {
                if !failing {
                    eprintln!("mkwmp lobby: ban call answered {} ({} ban(s) waiting)", r.status(), pending.len());
                }
                None
            }
            Err(e) => {
                if !failing {
                    eprintln!("mkwmp lobby: ban call failed: {e} ({} ban(s) waiting)", pending.len());
                }
                None
            }
        };
        failing = reply.is_none();
        let mut more = pending.len() > batch.len();
        if let Some(v) = reply {
            core.bans_delivered(batch);
            if let (Some(names), Some(ips)) = (v["names"].as_array(), v["ips"].as_array()) {
                let names: Vec<String> = names.iter().filter_map(|n| n.as_str().map(str::to_string)).collect();
                let ips: Vec<(String, u64)> =
                    ips.iter().filter_map(|i| Some((i["ip"].as_str()?.to_string(), i["expires"].as_u64()?))).collect();
                core.set_bans(&names, &ips);
            }
            rev = v["rev"].as_str().unwrap_or("").to_string();
            let left = core.pending_bans();
            save_ban_queue(queue.as_deref(), &left, &mut saved);
            more |= !left.is_empty();
        }
        // A ban waiting: again in 2 s; else at the next ban, or the periodic refresh.
        let wait = Duration::from_secs(if failing && !pending.is_empty() { 2 } else { every });
        if !more || failing {
            tokio::select! {
                _ = signal.notified() => {}
                _ = tokio::time::sleep(wait) => {}
            }
        }
    }
}

/// Which seats each seat-server boot has confirmed alive. A seat is reported
/// dead to the Core only when the same boot that saw it alive says it's gone
/// (left or timed out); a seat server restart forgets every seat, and that
/// must not end running games.
#[derive(Default)]
struct Seen {
    alive_in: std::collections::HashMap<String, (String, String)>, // sid -> (bootId, name)
}

impl Seen {
    fn entries(&mut self, v: &serde_json::Value) -> Vec<lobby::RosterEntry> {
        let boot = v["bootId"].as_str().unwrap_or("").to_string();
        let mut out = vec![];
        for s in v["sessions"].as_array().into_iter().flatten() {
            let Some(sid) = s["sessionId"].as_str() else { continue };
            let alive = s["alive"].as_bool().unwrap_or(false);
            let mut name = s["name"].as_str().unwrap_or("").to_string();
            if alive {
                self.alive_in.insert(sid.to_string(), (boot.clone(), name.clone()));
            } else {
                match self.alive_in.get(sid) {
                    // A dropped seat has no name in the roster: use the last one seen.
                    Some((b, n)) if *b == boot => {
                        name = n.clone();
                        self.alive_in.remove(sid);
                    }
                    // Unknown to this boot but never confirmed by it: leave it be.
                    _ => continue,
                }
            }
            out.push(lobby::RosterEntry {
                session_id: sid.to_string(),
                name,
                alive,
                friends: s["friends"]
                    .as_array()
                    .into_iter()
                    .flatten()
                    .filter_map(|f| f["name"].as_str().map(str::to_string))
                    .collect(),
            });
        }
        out
    }
}

#[cfg(test)]
mod tests {
    use super::Seen;
    use serde_json::json;

    #[test]
    fn dead_only_when_the_same_boot_saw_it_alive() {
        let mut s = Seen::default();
        let alive = json!({"bootId": "b1", "sessions": [
            {"sessionId": "a", "name": "Ana", "alive": true, "friends": [{"sessionId": "b", "name": "Bia"}]},
            {"sessionId": "x", "alive": false, "friends": []}]});
        let e = s.entries(&alive);
        assert_eq!(e.len(), 1, "never-seen dead seat is skipped");
        assert_eq!((e[0].name.as_str(), e[0].friends.clone()), ("Ana", vec!["Bia".to_string()]));
        // Seat server restarted: "a" is unknown to b2; no kick.
        let restarted = json!({"bootId": "b2", "sessions": [{"sessionId": "a", "alive": false, "friends": []}]});
        assert!(s.entries(&restarted).is_empty());
        // Same boot drops it: reported dead once.
        let dropped = json!({"bootId": "b1", "sessions": [{"sessionId": "a", "alive": false, "friends": []}]});
        let e = s.entries(&dropped);
        assert!(e.len() == 1 && !e[0].alive && e[0].name == "Ana");
        assert!(s.entries(&dropped).is_empty());
    }

    const SIGN_IN: &str = "/nn.npln.auth.v1.Auth/IssuePrearrangedUserToken";
    const WRITE: &str = "/nn.npln.gamesync.v1.Gamesync/WriteDocuments";

    /// One unary call on the Core from `peer`: the reply, or the gRPC status.
    fn unary(core: &lobby::Core, conn: u64, path: &str, bearer: Option<&str>, peer: &str, body: &[u8]) -> Result<Vec<u8>, u32> {
        static NEXT: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1);
        let stream = NEXT.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        let (tx, mut rx) = tokio::sync::mpsc::unbounded_channel();
        core.open(&lobby::Call { conn, stream, path, authority: "x", bearer, peer: peer.parse().unwrap() }, tx);
        core.message(conn, stream, body);
        core.half_closed(conn, stream);
        let mut msg = Vec::new();
        while let Ok(out) = rx.try_recv() {
            match out {
                lobby::Out::Message(m) => msg = m,
                lobby::Out::End { grpc_status: 0, .. } => return Ok(msg),
                lobby::Out::End { grpc_status, .. } => return Err(grpc_status),
            }
        }
        panic!("{path}: no reply");
    }

    /// Bia's game signs in (a development ticket: this lobby has no keys); (user id, access token).
    fn sign_in(core: &lobby::Core, conn: u64, peer: &str) -> Result<(String, String), u32> {
        use lobby::wire::{self, Msg};
        let ticket = "mkw1.eyJuYW1lIjoiQmlhIn0.sig"; // {"name":"Bia"}
        let body = Msg::new().bytes(1, "tenants/t").msg(3, &Msg::new().bytes(1, ticket)).into_bytes();
        let reply = unary(core, conn, SIGN_IN, None, peer, &body)?;
        let token = wire::bytes_field(&reply, 2).unwrap().unwrap();
        let user = wire::str_field(token, 1).unwrap().unwrap();
        Ok((user.rsplit('/').next().unwrap().to_owned(), wire::str_field(token, 2).unwrap().unwrap()))
    }

    async fn until(what: &str, mut ok: impl FnMut() -> bool) {
        for _ in 0..100 {
            if ok() {
                return;
            }
            tokio::time::sleep(std::time::Duration::from_millis(100)).await;
        }
        panic!("timed out: {what}");
    }

    /// The whole path: the Core bans a spoofer, the ban waits on disk while the seat server is
    /// away, reaches it when it answers, is kept there, and an admin unban comes back to the Core.
    #[tokio::test]
    async fn a_ban_reaches_the_seat_server_and_an_unban_comes_back() {
        use lobby::value::{Fields, Value};
        use lobby::wire::Msg;
        let queue = std::env::temp_dir().join(format!("mkwmp-ban-queue-{}.json", std::process::id()));
        let _ = std::fs::remove_file(&queue);
        let core = lobby::Core::new(lobby::CoreConfig::default());

        let (uid, token) = sign_in(&core, 1, "203.0.113.2:5000").unwrap();
        let doc = Msg::new().bytes(1, format!("docs/PlayerStatic/{uid}")).msg(2, &Fields::new().with("name", Value::str("Ana")).encode());
        let body = Msg::new().msg(1, &Msg::new().msg(1, &Msg::new().msg(1, &doc))).into_bytes();
        assert_eq!(unary(&core, 2, WRITE, Some(&token), "203.0.113.2:5000", &body), Err(lobby::code::PERMISSION_DENIED));
        assert_eq!(core.pending_bans().len(), 1);

        // The seat server is bound but not answering yet: the ban is on disk meanwhile.
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let url = format!("http://{}", listener.local_addr().unwrap());
        tokio::spawn(super::ban_sync(core.clone(), url, "tok".into(), 1, Some(queue.clone())));
        until("the queue file", || std::fs::read_to_string(&queue).is_ok_and(|t| t.contains("\"Bia\""))).await;
        let queued = super::load_ban_queue(&queue);
        assert_eq!(queued, core.pending_bans(), "a restarted lobby would deliver the same ban");

        let key = ticket::Issuer::generate();
        let roster = seat::roster::Roster::new(seat::roster::Rules::default(), ticket::Issuer::new("k1", key));
        let registry = seat::registry::Registry::open(None, &[], 30).unwrap();
        let state = seat::http::AppState::new(roster, registry, vec!["tok".into()], None, seat::http::system_clock());
        let registry = state.registry().clone();
        tokio::spawn(async move {
            let app = seat::http::router(state).into_make_service_with_connect_info::<std::net::SocketAddr>();
            axum::serve(listener, app).await.unwrap();
        });
        until("the seat server has the ban", || registry.banned_name("bia")).await;
        let now = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_secs();
        assert!(registry.banned_ip("203.0.113.2", now) && !registry.banned_ip("203.0.113.2", now + 14 * 86_400 + 5));
        until("the queue is empty", || core.pending_bans().is_empty() && std::fs::read_to_string(&queue).is_ok_and(|t| t == "[]")).await;
        assert_eq!(sign_in(&core, 3, "198.51.100.9:5000").err(), Some(lobby::code::PERMISSION_DENIED));

        // The admin lifts the ban on the seat server: the lobby follows at its next refresh.
        assert_eq!(registry.unban("Bia").unwrap(), 2);
        until("the unban reaches the lobby", || sign_in(&core, 4, "203.0.113.2:5000").is_ok()).await;
        let _ = std::fs::remove_file(&queue);
    }
}