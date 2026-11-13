//! Seat server: NPLN Auth over the roster. One instance, fixed seat count.
//! Handler depends on the `Roster` trait, not on a concrete store.

mod roster;

use roster::{MemoryRoster, Roster};
use std::sync::Arc;

pub struct AuthService {
    roster: Arc<dyn Roster>,
}

impl AuthService {
    pub fn new(roster: Arc<dyn Roster>) -> Self {
        Self { roster }
    }
}

// TODO(contract): implement contracts::nn::npln::auth::v1::auth_server::Auth
// for AuthService once IssuePrearrangedUserToken is ported from the Python
// prototype (see docs/contracts/WORKFLOW.md). The roster is the backing store.

fn main() {
    let path = std::env::args().nth(1).unwrap_or_else(|| "config/server.toml".into());
    let cfg = config::ServerConfig::load(&path).expect("load server config");
    let _service = AuthService::new(Arc::new(MemoryRoster::new(cfg.seats.capacity)));
    println!(
        "seat server '{}': {} seats on {} (db {}); gRPC wiring pending IssuePrearrangedUserToken port",
        cfg.server.name, cfg.seats.capacity, cfg.server.bind, cfg.database.path.display()
    );
}
