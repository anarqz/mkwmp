//! mkwmp launcher.
//!
//!   mkwmp-launcher [--no-launch] [--no-update] [--version] [client.toml]
//!
//! 1. Updates itself and the game patch from GitHub Releases (verified
//!    signatures; failures never block play).
//! 2. Writes `<game>/mkwmp.toml`: the lobby, your name, and how the game
//!    reaches this launcher (local API on 127.0.0.1).
//! 3. Takes a seat: at start (`seat = "start"`), or when the game goes online
//!    (`seat = "online"`). Shows the queue, keeps the seat alive, picks up
//!    friend edits in client.toml, rejoins after a server restart.
//! 4. Starts the game (unless `--no-launch` / `launch = false`, e.g. SteamOS:
//!    start it from Steam) and frees the seat when it exits or on Ctrl-C.

mod api;
mod config;
mod launch;
mod local;
mod probe;
mod seat;
mod update;

use std::process::Child;
use std::sync::Arc;
use std::time::Duration;

use config::{ClientConfig, SeatMode};
use rand::RngCore;
use seat::Seat;

struct Args {
    path: String,
    no_launch: bool,
    no_update: bool,
}

fn args() -> Args {
    let mut a = Args { path: "client.toml".into(), no_launch: false, no_update: false };
    for arg in std::env::args().skip(1) {
        match arg.as_str() {
            "--no-launch" => a.no_launch = true,
            "--no-update" => a.no_update = true,
            "--version" | "-V" => {
                println!("mkwmp-launcher {}", update::VERSION);
                std::process::exit(0);
            }
            "-h" | "--help" => {
                println!("usage: mkwmp-launcher [--no-launch] [--no-update] [--version] [client.toml]");
                std::process::exit(0);
            }
            _ => a.path = arg,
        }
    }
    a
}

#[tokio::main]
async fn main() {
    let a = args();
    if let Err(e) = run(a).await {
        eprintln!("mkwmp: {e}");
        std::process::exit(1);
    }
}

async fn run(a: Args) -> Result<(), String> {
    let cfg = ClientConfig::load(&a.path)?;
    println!("mkwmp-launcher {}", update::VERSION);
    update::clean_old();

    if cfg.update && !a.no_update {
        updates(&cfg, &a).await;
    }
    if let Some(msg) = probe::ensure_host(&cfg.game) {
        println!("mkwmp: winmm_host.dll: {msg}");
    }

    let seat = Arc::new(Seat::new(&cfg, &a.path));

    // Where the game connects: the server's lobby.
    let lobby = loop {
        match seat.api().lobby_endpoint().await {
            Ok(l) => break l,
            Err(e) => {
                eprintln!("mkwmp: {e}; retrying");
                tokio::select! {
                    _ = tokio::time::sleep(Duration::from_secs(5)) => {}
                    _ = tokio::signal::ctrl_c() => return Ok(()),
                }
            }
        }
    };

    // The local API the game calls for its seat.
    let addr = format!("127.0.0.1:{}", cfg.local_port);
    let listener = tokio::net::TcpListener::bind(&addr)
        .await
        .map_err(|e| format!("cannot open {addr} ({e}); is another launcher running?"))?;
    let mut token = [0u8; 32];
    rand::rngs::OsRng.fill_bytes(&mut token);
    let token: String = token.iter().map(|b| format!("{b:02x}")).collect();
    tokio::spawn({
        let app = local::router(seat.clone(), token.clone());
        async move {
            let _ = axum::serve(listener, app).await;
        }
    });

    let write = |ticket: Option<&str>, lobby: &str| {
        let toml = launch::probe_toml(
            &cfg,
            &launch::ProbeConfig { lobby, launcher: &addr, launcher_token: &token, ticket },
        );
        launch::write_probe_toml(&cfg.game, &toml).map_err(|e| format!("writing mkwmp.toml: {e}"))
    };
    write(None, &lobby)?;
    println!("mkwmp: {} as {} (lobby {lobby})", cfg.server, cfg.name);
    match cfg.seat {
        SeatMode::Start => println!("mkwmp: taking a seat"),
        SeatMode::Online => println!("mkwmp: you'll get a seat when you go online in the game"),
    }

    let launch = cfg.launch && !a.no_launch;
    let mut game: Option<Child> = None;
    if launch {
        println!("mkwmp: starting the game");
        game = Some(launch::spawn_game(&cfg.game).map_err(|e| format!("starting mkw.exe from {}: {e}", cfg.game.display()))?);
    } else {
        println!("mkwmp: start the game now and keep this window open (Ctrl-C frees the seat)");
    }

    loop {
        if let Some((ticket, seated_lobby)) = seat.tick().await {
            // seat = "start": the ticket also goes in mkwmp.toml for probes
            // that read it there.
            let t = (cfg.seat == SeatMode::Start).then_some(ticket.as_str());
            write(t, &seated_lobby)?;
        }
        if let Some(child) = game.as_mut() {
            if let Ok(Some(status)) = child.try_wait() {
                println!("mkwmp: game exited ({status}); releasing the seat");
                seat.release().await;
                return Ok(());
            }
        }
        tokio::select! {
            _ = tokio::time::sleep(Duration::from_secs(1)) => {}
            _ = tokio::signal::ctrl_c() => {
                println!("mkwmp: leaving");
                seat.release().await;
                return Ok(());
            }
        }
    }
}

/// Self-update, then bring the game patch in line with this release.
/// Never fatal.
async fn updates(cfg: &ClientConfig, a: &Args) {
    let u = update::Updater::new();
    if std::env::var_os("MKWMP_UPDATED").is_none() {
        match update::self_update(&u).await {
            Ok(Some(exe)) => {
                println!("mkwmp: updated; restarting");
                restart(&exe, a);
            }
            Ok(None) => {}
            Err(e) => eprintln!("mkwmp: update check failed: {e} (continuing)"),
        }
    }
    match update::own_release(&u).await {
        Ok((r, sums)) => match u.asset(&r, &sums, update::PROBE_ASSET).await {
            Ok(bytes) => match probe::install_probe(&cfg.game, &bytes, probe::game_running()) {
                Ok(probe::ProbeAction::UpToDate) => {}
                Ok(probe::ProbeAction::Installed { backup }) => {
                    println!("mkwmp: installed the game patch {} into {}", r.tag_name, cfg.game.display());
                    if let Some(b) = backup {
                        println!("mkwmp: your previous winmm.dll is saved as {}", b.display());
                    }
                }
                Ok(probe::ProbeAction::GameRunning) => {
                    println!("mkwmp: a game patch update is ready; close the game and start the launcher again")
                }
                Err(e) => eprintln!("mkwmp: installing the game patch failed: {e} (continuing)"),
            },
            Err(e) => eprintln!("mkwmp: game patch download failed: {e} (continuing)"),
        },
        Err(e) => eprintln!("mkwmp: game patch check failed: {e} (continuing)"),
    }
}

/// Run the updated launcher with the same arguments and exit with its code.
fn restart(exe: &std::path::Path, _a: &Args) -> ! {
    let status = std::process::Command::new(exe)
        .args(std::env::args_os().skip(1))
        .env("MKWMP_UPDATED", "1")
        .status();
    std::process::exit(status.map(|s| s.code().unwrap_or(0)).unwrap_or(1))
}
