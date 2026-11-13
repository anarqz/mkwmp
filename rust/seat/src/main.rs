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
    let capacity = std::env::var("MKWMP_SEATS").ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(16);
    let _service = AuthService::new(Arc::new(MemoryRoster::new(capacity)));
    println!("seat server: {capacity} seats (gRPC wiring pending IssuePrearrangedUserToken port)");
}
