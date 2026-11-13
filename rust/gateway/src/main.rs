//! Gateway: the only TLS/h2 endpoint. Terminates the game's NPLN connection and
//! routes by service prefix to the seat server (nn.npln.auth.*) or a lobby
//! manager (nn.npln.matchmaking.*, nn.npln.gamesync.*). Behind it, plain gRPC.
//!
//! Skeleton only: routing table and TLS wiring are pending the Rust port.

fn main() {
    println!("gateway: pending (see docs/ARCHITECTURE.md)");
}
