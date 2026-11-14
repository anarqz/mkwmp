//! Lobby: NPLN Matchmaker / GameSessionService / Gamesync for a set of
//! lobbies. The game-facing handlers are still being proven in the Python
//! reference server (server/mkwmp_server.py); they move here once verified,
//! one RPC at a time (docs/contracts/WORKFLOW.md).

pub mod matchmaking;

/// `mkwmp lobby`. Not wired yet: says what to run instead.
pub async fn serve(cfg: &config::ServerConfig) -> Result<(), Box<dyn std::error::Error>> {
    let _mm = matchmaking::Matchmaking::default();
    Err(format!(
        "the Rust lobby is not ready yet (would serve {}:{}). \
         Run the Python reference lobby for now: python server/mkwmp_server.py",
        cfg.lobby.host, cfg.lobby.npln_port
    )
    .into())
}
