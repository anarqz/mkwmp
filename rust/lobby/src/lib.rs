//! The game-facing lobby core: NPLN auth, matchmaking, Gamesync documents, referee, friends,
//! for any number of lobbies at once. Pure protocol logic, no sockets: the transport
//! (`gateway` crate) owns the listeners, frames gRPC and calls the five [`Core`] methods.
//!
//! The boundary is bytes: each [`Out::Message`] is one protobuf message, unframed; each
//! client message handed to [`Core::message`] is one unframed protobuf message.
//! Behaviour is ported from the verified Python reference (`resources/python-lobby/mkwmp_server.py`) and
//! the RE maps (`resources/docs/RE-MAP.md`, `resources/docs/MULTI-LOBBY.md`).

use std::net::SocketAddr;

mod core;
pub mod docs;
pub mod limits;
pub mod lobbies;
pub mod msgs;
pub mod referee;
pub mod social;
pub mod users;
pub mod value;
pub mod wire;

pub use crate::core::Core;
pub use crate::lobbies::{ModeCounts, Policy};
pub use crate::msgs::ice;

/// One per TCP connection, allocated by the transport.
pub type ConnId = u64;
/// One per HTTP/2 stream within a connection, allocated by the transport.
pub type StreamId = u64;

/// Lobby configuration (from `config/server.toml` `[lobby]`).
#[derive(Clone, Debug)]
pub struct CoreConfig {
    /// Address the game's peers and NAT probes reach this lobby at (LAN or public IPv4).
    pub public_host: String,
    /// TURN relay port (UDP), advertised by AllocateIceServerSet.
    pub turn_port: u16,
    /// Pia NAT probe responder port (UDP), advertised in `__gs/f`.
    pub nat_port: u16,
    /// Gamesync endpoint host advertised in the match assignment. A dotted name the probe
    /// redirects and the TLS certificate covers (a single label gets ".localdomain" for SNI).
    pub gamesync_host: String,
    /// Gamesync endpoint port (TCP), advertised in the match assignment.
    pub gamesync_port: u16,
    /// HMAC key of the lobby's access tokens. Keep it across restarts so running games stay
    /// recognised; `None` = random per process.
    pub token_secret: Option<Vec<u8>>,
    /// Seat-ticket public keys, `"kid:base64url"` (see rust/ticket).
    pub ticket_keys: Vec<String>,
    /// Refuse sign-ins without a valid seat ticket (production). Off for local tests,
    /// where the probe sends its development tokens.
    pub require_ticket: bool,
}

impl Default for CoreConfig {
    fn default() -> Self {
        Self {
            public_host: "127.0.0.1".into(),
            turn_port: 3478,
            nat_port: 10025,
            gamesync_host: "gamesync.npln.nintendo.net".into(),
            gamesync_port: 8906,
            token_secret: None,
            ticket_keys: Vec::new(),
            require_ticket: false,
        }
    }
}

/// One seat of the seat server's roster (`POST /seat/v1/lobby/roster`), as handed to
/// [`Core::roster`].
#[derive(Clone, Debug, Default)]
pub struct RosterEntry {
    pub session_id: String,
    pub name: String,
    pub alive: bool,
    /// Names of the mutual friends that are seated now.
    pub friends: Vec<String>,
}

/// An incoming RPC, as the transport saw its request headers.
#[derive(Clone, Debug)]
pub struct Call<'a> {
    pub conn: ConnId,
    pub stream: StreamId,
    /// gRPC path, e.g. `/nn.npln.auth.v1.Auth/IssuePrearrangedUserToken`.
    pub path: &'a str,
    /// HTTP/2 `:authority`.
    pub authority: &'a str,
    /// `authorization` header without its `Bearer `/`bearer ` prefix.
    pub bearer: Option<&'a str>,
    /// The client's address as the transport saw it.
    pub peer: SocketAddr,
}

/// What the Core sends back on a stream.
#[derive(Clone, Debug, PartialEq)]
pub enum Out {
    /// One protobuf message (unframed).
    Message(Vec<u8>),
    /// Trailers: `grpc-status` (and `grpc-message` when set); ends the stream.
    End { grpc_status: u32, message: Option<String> },
}

impl Out {
    pub fn ok() -> Self {
        Out::End { grpc_status: 0, message: None }
    }
    pub fn status(code: u32, message: impl Into<String>) -> Self {
        Out::End { grpc_status: code, message: Some(message.into()) }
    }
}

/// gRPC status codes used by the Core.
pub mod code {
    pub const OK: u32 = 0;
    pub const CANCELLED: u32 = 1;
    pub const INVALID_ARGUMENT: u32 = 3;
    pub const NOT_FOUND: u32 = 5;
    pub const PERMISSION_DENIED: u32 = 7;
    pub const FAILED_PRECONDITION: u32 = 9;
    pub const UNAUTHENTICATED: u32 = 16;
}
