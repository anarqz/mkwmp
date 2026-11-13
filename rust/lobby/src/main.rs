//! Lobby manager: NPLN Matchmaker / GameSessionService / Gamesync for one set
//! of lobbies. Stateless across workers; a worker with no lobbies may exit.

mod matchmaking;

use matchmaking::Matchmaking;

// TODO(contract): implement the Matchmaker and Gamesync server traits from the
// `contracts` crate over `Matchmaking`, porting handlers from the Python
// prototype one RPC at a time (docs/contracts/WORKFLOW.md).

fn main() {
    let _mm = Matchmaking::default();
    println!("lobby manager: ready (gRPC wiring pending Track/Gamesync ports)");
}
