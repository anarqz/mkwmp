//! The lobby's transport, everything below the game protocol:
//!
//! - NPLN (`npln_port`, 8905) and Gamesync (`gamesync_port`, 8906): TCP
//!   listeners that sniff TLS (first byte 0x16) vs plaintext h2c, then speak
//!   HTTP/2 with gRPC framing. Every call goes to a [`Backend`] as unframed
//!   protobuf messages; the backend answers through an [`Out`] channel.
//! - TURN relay (`turn_port`, 3478 UDP): the RFC 5766 subset Pia needs.
//! - Pia NAT responder (`nat_port`, 10025 UDP).
//!
//! No NPLN knowledge here: paths and bytes only. Ported from the verified
//! Python reference (resources/python-lobby/mkwmp_server.py, turn_relay.py, pia_capture.py).

use std::net::{IpAddr, Ipv4Addr, SocketAddr};
use std::path::PathBuf;
use std::sync::Arc;

use tokio::sync::mpsc::UnboundedSender;

pub mod grpc;
pub mod h2conn;
pub mod limits;
pub mod log;
pub mod nat;
pub mod tls;
pub mod turn;
pub mod voice;

pub type ConnId = u64;
pub type StreamId = u64;

/// One gRPC call, as the backend sees it when it opens.
#[derive(Debug, Clone)]
pub struct Call<'a> {
    pub conn: ConnId,
    pub stream: StreamId,
    /// `/package.Service/Method`
    pub path: &'a str,
    pub authority: &'a str,
    /// The Authorization header minus "Bearer " / "bearer ".
    pub bearer: Option<&'a str>,
    /// The client's address as the gateway sees it.
    pub peer: SocketAddr,
}

/// What the backend sends on a call.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Out {
    /// One protobuf message, unframed.
    Message(Vec<u8>),
    /// Trailers; the stream is finished.
    End { grpc_status: u32, message: Option<String> },
}

/// The game protocol behind the gateway (the lobby crate's Core). Called
/// from tokio tasks on any thread: implementations must not block.
pub trait Backend: Send + Sync + 'static {
    /// A call opened (headers received). Response headers are already on
    /// their way; reply on `out`, ending with [`Out::End`].
    fn open(&self, call: &Call, out: UnboundedSender<Out>);
    /// One complete client message (unframed).
    fn message(&self, conn: ConnId, stream: StreamId, body: &[u8]);
    /// The client half-closed (END_STREAM).
    fn half_closed(&self, conn: ConnId, stream: StreamId);
    /// The client reset the stream, or its connection broke mid-call.
    fn reset(&self, conn: ConnId, stream: StreamId);
    fn connection_closed(&self, conn: ConnId);
}

#[derive(Debug, Clone)]
pub struct GatewayConfig {
    /// Address every listener binds to (0.0.0.0 on a server).
    pub host: IpAddr,
    /// Address clients reach this server at; TURN relay addresses use it.
    pub public_host: Ipv4Addr,
    pub npln_port: u16,
    /// 0 or equal to `npln_port`: no separate Gamesync listener.
    pub gamesync_port: u16,
    /// 0 disables the TURN relay.
    pub turn_port: u16,
    /// 0 disables the NAT responder.
    pub nat_port: u16,
    /// TURN long-term credentials (the ones the backend advertises in its
    /// ICE server set: lobby::ice::{TURN_USERNAME, TURN_PASSWORD}).
    pub turn_username: String,
    pub turn_password: String,
    /// UDP range for TURN relay sockets (firewall); None = ephemeral ports.
    pub relay_ports: Option<(u16, u16)>,
    /// PEM pair; None = plaintext h2c only.
    pub tls: Option<(PathBuf, PathBuf)>,
}

/// Run every listener until one fails.
pub async fn serve<B: Backend>(cfg: GatewayConfig, backend: Arc<B>) -> std::io::Result<()> {
    let acceptor = match &cfg.tls {
        Some((cert, key)) => Some(tls::acceptor(cert, key)?),
        None => None,
    };
    let mut tasks = tokio::task::JoinSet::new();
    let ids = Arc::new(std::sync::atomic::AtomicU64::new(1));
    let conns = Arc::new(limits::Connections::default());
    let mut ports = vec![cfg.npln_port];
    if cfg.gamesync_port != 0 && cfg.gamesync_port != cfg.npln_port {
        ports.push(cfg.gamesync_port);
    }
    for port in ports {
        let listener = tokio::net::TcpListener::bind(SocketAddr::new(cfg.host, port)).await?;
        log::line(format!(
            "listening on {}:{port} (tcp, {})",
            cfg.host,
            if acceptor.is_some() { "TLS or h2c, sniffed" } else { "h2c" }
        ));
        tasks.spawn(h2conn::accept_loop(listener, acceptor.clone(), backend.clone(), ids.clone(), conns.clone()));
    }
    if cfg.turn_port != 0 {
        let relay = turn::Relay::bind(
            SocketAddr::new(cfg.host, cfg.turn_port),
            turn::Options {
                relay_bind: cfg.host,
                public_host: cfg.public_host,
                relay_ports: cfg.relay_ports,
                username: cfg.turn_username.clone(),
                password: cfg.turn_password.clone(),
            },
        )
        .await?;
        tasks.spawn(relay.run());
    }
    if cfg.nat_port != 0 {
        let sock = tokio::net::UdpSocket::bind(SocketAddr::new(cfg.host, cfg.nat_port)).await?;
        log::line(format!("Pia NAT responder on {}:{} (udp)", cfg.host, cfg.nat_port));
        tasks.spawn(nat::run(sock));
    }
    match tasks.join_next().await {
        Some(Ok(r)) => r,
        Some(Err(e)) => Err(std::io::Error::other(e)),
        None => Ok(()),
    }
}
