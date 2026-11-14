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
        tokio::spawn(report_counts(core.clone(), url.trim_end_matches('/').to_string(), token));
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

/// Live lobbies per mode → the seat server's public status, every 15 s.
async fn report_counts(core: lobby::Core, url: String, token: String) {
    let http = reqwest::Client::builder().timeout(Duration::from_secs(5)).build().expect("http client");
    let mut failing = false;
    loop {
        let lobbies: std::collections::BTreeMap<String, usize> =
            core.counts().into_iter().map(|(mode, c)| (mode, c.lobbies)).collect();
        let body = serde_json::json!({ "sessionIds": [], "lobbies": lobbies });
        let res = http.post(format!("{url}/seat/v1/lobby/roster")).bearer_auth(&token).json(&body).send().await;
        match res.map(|r| r.status()) {
            Ok(s) if s.is_success() => failing = false,
            other if !failing => {
                failing = true;
                eprintln!("mkwmp lobby: roster report failed: {other:?}");
            }
            _ => {}
        }
        tokio::time::sleep(Duration::from_secs(15)).await;
    }
}
