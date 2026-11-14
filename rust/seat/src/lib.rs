//! Seat server: N seats, a FIFO queue, signed tickets and the session-only
//! friend graph, served over HTTP (docs/contracts/SEAT_API.md). The binary
//! (`main.rs`) loads config and serves [`http::router`].

pub mod http;
pub mod limits;
pub mod names;
pub mod presence;
pub mod registry;
pub mod roster;
pub mod serve;

pub use serve::{keygen, serve};
