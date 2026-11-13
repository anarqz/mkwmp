//! Lobby manager: NPLN Matchmaker / GameSessionService / Gamesync for one set
//! of lobbies. Stateless across workers; a worker with no lobbies may exit.

mod matchmaking;

use matchmaking::Matchmaking;

// TODO(contract): implement the Matchmaker and Gamesync server traits from the
// `contracts` crate over `Matchmaking`, porting handlers from the Python
// prototype one RPC at a time (docs/contracts/WORKFLOW.md).

fn main() {
    let path = std::env::args().nth(1).unwrap_or_else(|| "config/server.toml".into());
    let cfg = config::ServerConfig::load(&path).expect("load server config");
    let _mm = Matchmaking::default();
    println!(
        "lobby manager on {} (lobby_size {}, max {}/worker); gRPC wiring pending Track/Gamesync ports",
        cfg.lobby.bind, cfg.lobby.lobby_size, cfg.lobby.max_lobbies_per_worker
    );
}
