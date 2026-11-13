//! `mkwmp server`: load the key, build the roster, serve the seat API.

use std::time::Duration;

use crate::http::{router, system_clock, AppState};
use crate::roster::{Roster, Rules};
use ticket::Issuer;

/// Print a new Ed25519 signing key: the private seed and the public entry.
pub fn keygen(kid: &str) {
    let key = Issuer::generate();
    let issuer = Issuer::new(kid, key.clone());
    println!("# private seed: save to the file named by [ticket] private_key_file; never share it");
    println!("{}", ticket::seed_b64(&key));
    println!("# public key: give lobbies this [ticket] public_keys entry");
    println!("{kid}:{}", issuer.public_key_b64());
}

/// Serve the seat API until Ctrl-C.
pub async fn serve(cfg: &config::ServerConfig) -> Result<(), Box<dyn std::error::Error>> {
    let key_file = cfg
        .ticket
        .private_key_file
        .as_ref()
        .ok_or("[ticket] private_key_file is required to run the seat server")?;
    let seed = std::fs::read_to_string(key_file)
        .map_err(|e| format!("reading {}: {e} (create one with `seat keygen`)", key_file.display()))?;
    if cfg.ticket.kid.is_empty() {
        return Err("[ticket] kid must be set".into());
    }
    let issuer = Issuer::from_seed_b64(cfg.ticket.kid.clone(), &seed)
        .map_err(|e| format!("{}: {e}", key_file.display()))?;
    let lobby_tokens: Vec<String> = match &cfg.seats.lobby_tokens_file {
        Some(p) => std::fs::read_to_string(p)
            .map_err(|e| format!("reading {}: {e}", p.display()))?
            .lines()
            .map(str::trim)
            .filter(|l| !l.is_empty() && !l.starts_with('#'))
            .map(String::from)
            .collect(),
        None => vec![],
    };
    let s = &cfg.seats;
    let rules = Rules {
        capacity: s.capacity,
        max_queue: s.max_queue,
        seat_timeout_ms: s.seat_timeout_secs * 1000,
        queue_timeout_ms: s.queue_timeout_secs * 1000,
        heartbeat_ms: s.heartbeat_secs * 1000,
        queue_poll_ms: s.queue_poll_secs * 1000,
        ticket_ttl_secs: cfg.ticket.ttl_secs,
        server_name: cfg.server.name.clone(),
        lobby_endpoint: s.lobby_endpoint.clone(),
    };
    let public = format!("{}:{}", issuer.kid(), issuer.public_key_b64());
    let roster = Roster::new(rules, issuer);
    let boot = roster.boot_id().to_string();
    let state = AppState::new(roster, lobby_tokens.clone(), system_clock());

    // Sweep silent sessions even when no request arrives.
    let sweeper = state.clone();
    tokio::spawn(async move {
        let mut tick = tokio::time::interval(Duration::from_secs(5));
        loop {
            tick.tick().await;
            let now = sweeper.now();
            sweeper.roster().lock().unwrap().sweep(now);
        }
    });

    let listener = tokio::net::TcpListener::bind(&s.bind).await?;
    println!(
        "seat server '{}' on http://{} — {} seats, queue {}, boot {boot}",
        cfg.server.name, s.bind, s.capacity, s.max_queue
    );
    println!("ticket key {public}; lobby roster {}", if lobby_tokens.is_empty() { "disabled" } else { "enabled" });
    axum::serve(listener, router(state))
        .with_graceful_shutdown(async {
            let _ = tokio::signal::ctrl_c().await;
        })
        .await?;
    Ok(())
}
