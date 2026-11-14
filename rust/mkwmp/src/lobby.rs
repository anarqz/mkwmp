//! `mkwmp lobby`: the gateway (TLS/h2, gRPC framing, TURN, Pia NAT) in front
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
        tokio::spawn(roster_sync(core.clone(), url.trim_end_matches('/').to_string(), token, l.roster_secs.max(1)));
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
    gateway::serve(gw, Arc::new(Adapter(core))).await?;
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
        let mut entries = vec![];
        let mut ok = true;
        // The seat server takes at most 256 ids per call; counts ride on the first.
        let chunks: Vec<&[String]> = if sids.is_empty() { vec![&[]] } else { sids.chunks(256).collect() };
        for (i, chunk) in chunks.into_iter().enumerate() {
            let mut body = serde_json::json!({ "sessionIds": chunk });
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
}
