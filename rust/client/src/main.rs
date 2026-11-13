//! mkwmp launcher: take a seat on the server (or wait in its queue), then
//! launch the game and keep the seat alive while it runs.
//!
//!   mkwmp-launcher [--no-launch] [client.toml]
//!
//! `--no-launch` (or `launch = false` in client.toml): take the seat and write
//! mkwmp.toml, but start nothing. Start the game yourself, e.g. from Steam on
//! SteamOS; the seat is held until you press Ctrl-C.
//!
//! Edit `friends` in client.toml while playing: the launcher picks it up on its
//! next heartbeat. If the server restarts, the launcher rejoins and rewrites
//! the game's mkwmp.toml with the fresh ticket.

mod api;
mod config;
mod launch;

use std::process::Child;
use std::time::Duration;

use api::{Api, ApiError, View};
use config::ClientConfig;

struct Session {
    sid: String,
    key: String,
    boot: String,
}

#[tokio::main]
async fn main() {
    let mut no_launch = false;
    let mut path = "client.toml".to_string();
    for a in std::env::args().skip(1) {
        match a.as_str() {
            "--no-launch" => no_launch = true,
            "-h" | "--help" => {
                println!("usage: mkwmp-launcher [--no-launch] [client.toml]");
                return;
            }
            _ => path = a,
        }
    }
    if let Err(e) = run(&path, no_launch).await {
        eprintln!("mkwmp: {e}");
        std::process::exit(1);
    }
}

async fn run(path: &str, no_launch: bool) -> Result<(), String> {
    let mut cfg = ClientConfig::load(path)?;
    let launch = cfg.launch && !no_launch;
    let api = Api::new(&cfg.server);
    println!("mkwmp: joining {} as {}", cfg.server, cfg.name);

    let mut session: Option<Session> = None;
    let mut game: Option<Child> = None;
    let mut ticket_written: Option<String> = None;
    let mut last_friends: Vec<api::Friend> = vec![];
    let mut rejected_friends: Option<Vec<String>> = None;
    let mut announced = false;

    loop {
        // Join (or rejoin after a restart / expiry).
        let result = match &session {
            None => api.join(&cfg.name, &cfg.friends).await,
            Some(s) => {
                // Pick up friend edits. A refused list is reported once and
                // never costs the seat; a transient failure retries next time.
                let edited = ClientConfig::load(path)
                    .ok()
                    .map(|f| f.friends)
                    .filter(|f| *f != cfg.friends && Some(f) != rejected_friends.as_ref());
                match edited {
                    Some(list) => match api.set_friends(&s.sid, &s.key, &list).await {
                        Ok(v) => {
                            cfg.friends = list;
                            rejected_friends = None;
                            Ok(v)
                        }
                        Err(ApiError::Refused(m)) => {
                            eprintln!("mkwmp: friends not updated: {m}");
                            rejected_friends = Some(list);
                            api.poll(&s.sid, &s.key).await
                        }
                        Err(e) => Err(e),
                    },
                    None => api.poll(&s.sid, &s.key).await,
                }
            }
        };

        let wait = match result {
            Ok(v) => {
                let restarted = session.as_ref().is_some_and(|s| s.boot != v.boot_id);
                if restarted {
                    println!("mkwmp: the server restarted; rejoining");
                    session = None;
                    continue;
                }
                if let Some(key) = v.session_key.clone() {
                    session = Some(Session { sid: v.session_id.clone(), key, boot: v.boot_id.clone() });
                }
                show(&v, &mut last_friends);
                if let Some(t) = &v.ticket {
                    if ticket_written.as_deref() != Some(&t.token) {
                        let toml = launch::probe_toml(&cfg, &t.lobby, &t.token);
                        launch::write_probe_toml(&cfg.game, &toml).map_err(|e| format!("writing mkwmp.toml: {e}"))?;
                        ticket_written = Some(t.token.clone());
                    }
                    if !launch && ticket_written.as_deref() == Some(&t.token) && !announced {
                        announced = true;
                        println!("mkwmp: seat granted. Start the game now; keep this window open (Ctrl-C frees the seat)");
                    }
                    if launch && game.is_none() {
                        println!("mkwmp: seat granted, launching the game");
                        match launch::spawn_game(&cfg.game) {
                            Ok(child) => game = Some(child),
                            Err(e) => {
                                api.leave(&v.session_id, session.as_ref().map_or("", |s| &s.key)).await;
                                return Err(format!("launching mkw.exe from {}: {e}", cfg.game.display()));
                            }
                        }
                    }
                }
                Duration::from_millis(v.next_poll_ms.clamp(1_000, 30_000))
            }
            Err(ApiError::Gone) => {
                println!("mkwmp: session expired; rejoining");
                session = None;
                Duration::from_secs(1)
            }
            Err(ApiError::Refused(m)) if game.is_none() => {
                if let Some(s) = &session {
                    api.leave(&s.sid, &s.key).await;
                }
                return Err(m);
            }
            Err(ApiError::Refused(m)) => {
                eprintln!("mkwmp: server refused: {m}; retrying");
                if session.is_none() {
                    Duration::from_secs(10)
                } else {
                    Duration::from_secs(5)
                }
            }
            Err(ApiError::Transient(m)) => {
                eprintln!("mkwmp: {m}; retrying");
                Duration::from_secs(5)
            }
        };

        // Wait, but notice the game exiting or Ctrl-C promptly.
        let deadline = tokio::time::Instant::now() + wait;
        loop {
            if let Some(child) = game.as_mut() {
                if let Ok(Some(status)) = child.try_wait() {
                    println!("mkwmp: game exited ({status}); releasing the seat");
                    if let Some(s) = &session {
                        api.leave(&s.sid, &s.key).await;
                    }
                    return Ok(());
                }
            }
            let now = tokio::time::Instant::now();
            if now >= deadline {
                break;
            }
            tokio::select! {
                _ = tokio::time::sleep((deadline - now).min(Duration::from_millis(500))) => {}
                _ = tokio::signal::ctrl_c() => {
                    println!("mkwmp: leaving");
                    if let Some(s) = &session {
                        api.leave(&s.sid, &s.key).await;
                    }
                    return Ok(());
                }
            }
        }
    }
}

fn show(v: &View, last_friends: &mut Vec<api::Friend>) {
    if v.state == "queued" {
        let pos = v.position.unwrap_or(0);
        println!("mkwmp: server full ({}/{} seats); you are {} in queue", v.seats_total - v.seats_free, v.seats_total, ordinal(pos));
    }
    if v.friends != *last_friends {
        for f in &v.friends {
            let s = match (f.online, f.mutual) {
                (true, true) => "online, friends in game",
                (true, false) => "online, hasn't added you",
                _ => "offline",
            };
            println!("mkwmp:   friend {:<16} {s}", f.name);
        }
        *last_friends = v.friends.clone();
    }
}

fn ordinal(n: usize) -> String {
    let suffix = match (n % 10, n % 100) {
        (_, 11..=13) => "th",
        (1, _) => "st",
        (2, _) => "nd",
        (3, _) => "rd",
        _ => "th",
    };
    format!("{n}{suffix}")
}
