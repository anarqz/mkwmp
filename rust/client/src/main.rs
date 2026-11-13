//! mkwmp client: join the server, show the queue, launch the game when seated.
//!
//!   client [client.toml]
//!
//! Connects to the seat server, prints queue status, and on GRANTED writes the
//! probe's mkwmp.toml and launches the game. The connection stays open for the
//! whole session so the seat is held until the game exits.

mod config;
mod launch;

use config::ClientConfig;
use contracts::mkwmp::seat::v1::seat_client::SeatClient;
use contracts::mkwmp::seat::v1::{JoinRequest, State};
use tokio_stream::StreamExt;

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    let path = std::env::args().nth(1).unwrap_or_else(|| "config/client.toml".into());
    let cfg = ClientConfig::load(&path)?;

    println!("mkwmp: connecting to {} as {}...", cfg.server, cfg.name);
    let mut client = SeatClient::connect(format!("http://{}", cfg.server))
        .await
        .map_err(|e| format!("cannot reach seat server {}: {e}", cfg.server))?;

    let mut updates = client
        .join_queue(JoinRequest { username: cfg.name.clone() })
        .await?
        .into_inner();

    let mut launched = false;
    while let Some(update) = updates.next().await {
        let u = update?;
        match State::try_from(u.state).unwrap_or(State::Unspecified) {
            State::Queued => {
                println!("mkwmp: waiting to join — {} in queue ({}/{} seats free)",
                         ordinal(u.position), u.seats_free, u.seats_total);
            }
            State::Granted if !launched => {
                launched = true;
                println!("mkwmp: seat granted — launching game");
                let toml = launch::probe_toml(&cfg, &u.ticket);
                launch::write_probe_toml(&cfg.game, &toml)?;
                let status = launch::run_game(&cfg.game)?;
                println!("mkwmp: game exited ({status}); releasing seat");
                break; // dropping the stream releases our seat
            }
            _ => {}
        }
    }
    Ok(())
}

fn ordinal(n: u32) -> String {
    let suffix = match (n % 10, n % 100) {
        (1, 11) | (2, 12) | (3, 13) => "th",
        (1, _) => "st",
        (2, _) => "nd",
        (3, _) => "rd",
        _ => "th",
    };
    format!("{n}{suffix}")
}
