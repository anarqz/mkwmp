//! Beholder: the public directory of mkwmp seat servers (docs/contracts/BEHOLDER_API.md).
//!
//! Seat servers announce themselves every 30 s with an Ed25519-signed body ([`wire`]); launchers
//! read `GET /beholder/v1/servers` ([`http`]). Listings live in memory ([`directory`]) and expire
//! 90 s after their last announce. The beholder never contacts an announced endpoint and never
//! measures latency: a listing is a hint, the launcher checks the server it picks.

pub mod directory;
pub mod http;
pub mod wire;

/// The mkwmp release this crate was built for (the workspace version), sent as `version`.
pub const VERSION: &str = env!("CARGO_PKG_VERSION");
