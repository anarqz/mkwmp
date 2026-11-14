//! Minimal TURN relay (RFC 5766 subset) for Pia's nn::pia::turn::TurnProtocol,
//! ported from server/turn_relay.py.
//!
//! Pia's mesh attach waits until its TURN client reports ready, so the ICE
//! server set points here. Handles Binding, Allocate (long-term credentials:
//! 401 challenge, then MESSAGE-INTEGRITY), Refresh, CreatePermission,
//! ChannelBind, Send indications and ChannelData; peer traffic comes back as
//! ChannelData (bound peers) or Data indications.
//!
//! Beyond the Python: allocations expire after an hour without Allocate or
//! Refresh (the advertised lifetime stays 600 s), so the relay port range
//! doesn't leak; refreshes are logged; IPv4 only.

use std::collections::HashMap;
use std::net::{IpAddr, Ipv4Addr, SocketAddr, SocketAddrV4};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use hmac::{Hmac, Mac};
use md5::{Digest, Md5};
use rand::RngCore;
use sha1::Sha1;
use tokio::net::UdpSocket;
use tokio::task::JoinHandle;

use crate::limits::{self, Bucket, PerIp};
use crate::log::line;

pub const MAGIC: u32 = 0x2112_A442;
pub const REALM: &[u8] = b"mkwmp";
/// Lifetime advertised (what the Python relay sent).
const LIFETIME: u32 = 600;
const MAX_LIFETIME: u32 = 3600;
/// An allocation is dropped only after this long without Allocate/Refresh,
/// whatever lifetime was advertised: whether the game refreshes is unverified.
const KEEP: Duration = Duration::from_secs(3600);

pub const BINDING: u16 = 1;
pub const ALLOCATE: u16 = 3;
pub const REFRESH: u16 = 4;
pub const SEND: u16 = 6;
pub const DATA: u16 = 7;
pub const CREATE_PERMISSION: u16 = 8;
pub const CHANNEL_BIND: u16 = 9;
pub const REQUEST: u16 = 0x000;
pub const INDICATION: u16 = 0x010;
pub const SUCCESS: u16 = 0x100;
pub const ERROR: u16 = 0x110;

pub const A_USERNAME: u16 = 0x0006;
pub const A_MI: u16 = 0x0008;
pub const A_ERROR: u16 = 0x0009;
pub const A_CHANNEL: u16 = 0x000C;
pub const A_LIFETIME: u16 = 0x000D;
pub const A_PEER: u16 = 0x0012;
pub const A_DATA: u16 = 0x0013;
pub const A_REALM: u16 = 0x0014;
pub const A_NONCE: u16 = 0x0015;
pub const A_RELAYED: u16 = 0x0016;
pub const A_MAPPED: u16 = 0x0020;
pub const A_SOFTWARE: u16 = 0x8022;
pub const A_FINGERPRINT: u16 = 0x8028;

// ---- wire ------------------------------------------------------------------

pub fn msg_type(method: u16, class: u16) -> u16 {
    ((method & 0xF80) << 2) | ((method & 0x70) << 1) | (method & 0xF) | (class & 0x110)
}

pub fn split_type(t: u16) -> (u16, u16) {
    ((t & 0xF) | ((t >> 1) & 0x70) | ((t >> 2) & 0xF80), t & 0x110)
}

pub struct Msg<'a> {
    pub method: u16,
    pub class: u16,
    pub tid: [u8; 12],
    pub attrs: Vec<(u16, &'a [u8])>,
}

impl<'a> Msg<'a> {
    pub fn get(&self, at: u16) -> Option<&'a [u8]> {
        self.attrs.iter().find(|(a, _)| *a == at).map(|(_, v)| *v)
    }
}

pub fn parse(data: &[u8]) -> Option<Msg<'_>> {
    if data.len() < 20 {
        return None;
    }
    let t = u16::from_be_bytes([data[0], data[1]]);
    let len = u16::from_be_bytes([data[2], data[3]]) as usize;
    let magic = u32::from_be_bytes(data[4..8].try_into().unwrap());
    if magic != MAGIC || t & 0xC000 != 0 || 20 + len > data.len() {
        return None;
    }
    let mut attrs = vec![];
    let mut pos = 20;
    while pos + 4 <= 20 + len {
        let at = u16::from_be_bytes([data[pos], data[pos + 1]]);
        let al = u16::from_be_bytes([data[pos + 2], data[pos + 3]]) as usize;
        let end = (pos + 4 + al).min(20 + len);
        attrs.push((at, &data[pos + 4..end]));
        pos += 4 + al + (4 - al % 4) % 4;
    }
    let (method, class) = split_type(t);
    Some(Msg { method, class, tid: data[8..20].try_into().unwrap(), attrs })
}

pub fn xor_addr(a: SocketAddrV4) -> Vec<u8> {
    let mut v = vec![0, 1];
    v.extend((a.port() ^ (MAGIC >> 16) as u16).to_be_bytes());
    v.extend((u32::from(*a.ip()) ^ MAGIC).to_be_bytes());
    v
}

pub fn unxor_addr(v: &[u8]) -> Option<SocketAddrV4> {
    if v.len() < 8 || v[1] != 1 {
        return None;
    }
    let port = u16::from_be_bytes([v[2], v[3]]) ^ (MAGIC >> 16) as u16;
    let ip = u32::from_be_bytes(v[4..8].try_into().unwrap()) ^ MAGIC;
    Some(SocketAddrV4::new(Ipv4Addr::from(ip), port))
}

fn attr(out: &mut Vec<u8>, at: u16, v: &[u8]) {
    out.extend(at.to_be_bytes());
    out.extend((v.len() as u16).to_be_bytes());
    out.extend_from_slice(v);
    out.resize(out.len() + (4 - v.len() % 4) % 4, 0);
}

fn header(t: u16, len: usize, tid: &[u8; 12]) -> Vec<u8> {
    let mut h = Vec::with_capacity(20);
    h.extend(t.to_be_bytes());
    h.extend((len as u16).to_be_bytes());
    h.extend(MAGIC.to_be_bytes());
    h.extend_from_slice(tid);
    h
}

/// A message with optional MESSAGE-INTEGRITY (`key`) and a FINGERPRINT.
pub fn build(t: u16, tid: &[u8; 12], attrs: &[(u16, &[u8])], key: Option<&[u8]>) -> Vec<u8> {
    let mut body = vec![];
    for (a, v) in attrs {
        attr(&mut body, *a, v);
    }
    if let Some(key) = key {
        let head = header(t, body.len() + 24, tid);
        let mut mac = <Hmac<Sha1> as Mac>::new_from_slice(key).expect("hmac key");
        mac.update(&head);
        mac.update(&body);
        attr(&mut body, A_MI, &mac.finalize().into_bytes());
    }
    let mut out = header(t, body.len() + 8, tid);
    out.extend(&body);
    let crc = crc32fast::hash(&out) ^ 0x5354_554E;
    attr(&mut out, A_FINGERPRINT, &crc.to_be_bytes());
    out
}

pub fn long_term_key(username: &str, realm: &[u8], password: &str) -> Vec<u8> {
    let mut h = Md5::new();
    h.update(username.as_bytes());
    h.update(b":");
    h.update(realm);
    h.update(b":");
    h.update(password.as_bytes());
    h.finalize().to_vec()
}

fn error_code(class: u8, number: u8, reason: &str) -> Vec<u8> {
    let mut v = vec![0, 0, class, number];
    v.extend_from_slice(reason.as_bytes());
    v
}

fn channel_data(channel: u16, data: &[u8]) -> Vec<u8> {
    let mut f = Vec::with_capacity(4 + data.len() + 3);
    f.extend(channel.to_be_bytes());
    f.extend((data.len() as u16).to_be_bytes());
    f.extend_from_slice(data);
    f.resize(f.len() + (4 - data.len() % 4) % 4, 0);
    f
}

// ---- server ----------------------------------------------------------------

#[derive(Debug, Clone)]
pub struct Options {
    /// Address relay sockets bind to.
    pub relay_bind: IpAddr,
    /// Address advertised as the relayed address (what peers reach).
    pub public_host: Ipv4Addr,
    pub relay_ports: Option<(u16, u16)>,
    pub username: String,
    pub password: String,
}

struct Allocation {
    relay: Arc<UdpSocket>,
    /// Relayed bytes, both directions (limits::TURN_BYTES_PER_SEC).
    bytes: Arc<Mutex<Bucket>>,
    relay_addr: SocketAddrV4,
    channels: Arc<Mutex<Channels>>,
    expires: Instant,
    task: JoinHandle<()>,
}

#[derive(Default)]
struct Channels {
    by_channel: HashMap<u16, SocketAddr>,
    by_peer: HashMap<SocketAddr, u16>,
}

impl Drop for Allocation {
    fn drop(&mut self) {
        self.task.abort();
    }
}

pub struct Relay {
    sock: Arc<UdpSocket>,
    /// STUN requests per client IP.
    requests: Mutex<PerIp>,
    opts: Options,
    nonce: Vec<u8>,
    allocations: Mutex<HashMap<SocketAddr, Allocation>>,
}

impl Relay {
    pub async fn bind(addr: SocketAddr, opts: Options) -> std::io::Result<Arc<Self>> {
        let sock = Arc::new(UdpSocket::bind(addr).await?);
        let mut n = [0u8; 8];
        rand::rngs::OsRng.fill_bytes(&mut n);
        let span = match opts.relay_ports {
            Some((lo, hi)) => format!("ports {lo}-{hi}"),
            None => "ephemeral ports".into(),
        };
        line(format!(
            "TURN relay on {addr} (udp, user {:?}, relays on {} advertised as {}, {span})",
            opts.username, opts.relay_bind, opts.public_host
        ));
        Ok(Arc::new(Self {
            sock,
            requests: Mutex::new(PerIp::new(50.0, 100.0)),
            opts,
            nonce: n.iter().map(|b| format!("{b:02x}")).collect::<String>().into_bytes(),
            allocations: Mutex::new(HashMap::new()),
        }))
    }

    pub fn local_addr(&self) -> std::io::Result<SocketAddr> {
        self.sock.local_addr()
    }

    pub fn allocation_count(&self) -> usize {
        self.allocations.lock().unwrap().len()
    }

    pub async fn run(self: Arc<Self>) -> std::io::Result<()> {
        let sweeper = {
            let me = Arc::downgrade(&self);
            tokio::spawn(async move {
                loop {
                    tokio::time::sleep(Duration::from_secs(15)).await;
                    let Some(me) = me.upgrade() else { return };
                    let now = Instant::now();
                    let mut a = me.allocations.lock().unwrap();
                    let before = a.len();
                    a.retain(|_, x| x.expires > now);
                    if a.len() < before {
                        line(format!("TURN: {} allocation(s) expired, {} live", before - a.len(), a.len()));
                    }
                }
            })
        };
        let mut buf = vec![0u8; 65536];
        let r = loop {
            let (n, client) = match self.sock.recv_from(&mut buf).await {
                Ok(x) => x,
                Err(e) if e.kind() == std::io::ErrorKind::ConnectionReset => continue,
                Err(e) => break Err(e),
            };
            for (dest, frame) in self.handle(&buf[..n], client).await {
                match dest {
                    Dest::Client => {
                        let _ = self.sock.send_to(&frame, client).await;
                    }
                    Dest::Peer(relay, peer) => {
                        let _ = relay.send_to(&frame, peer).await;
                    }
                }
            }
        };
        sweeper.abort();
        r
    }

    /// One datagram from a client; returns what to send where.
    async fn handle(self: &Arc<Self>, data: &[u8], client: SocketAddr) -> Vec<(Dest, Vec<u8>)> {
        // ChannelData: 0x4000..=0x7FFF channel numbers.
        if data.len() >= 4 && (0x40..=0x7F).contains(&data[0]) {
            let channel = u16::from_be_bytes([data[0], data[1]]);
            let len = u16::from_be_bytes([data[2], data[3]]) as usize;
            let a = self.allocations.lock().unwrap();
            if let Some(alloc) = a.get(&client) {
                if let Some(peer) = alloc.channels.lock().unwrap().by_channel.get(&channel) {
                    let end = (4 + len).min(data.len());
                    if alloc.bytes.lock().unwrap().take((end - 4) as f64) {
                        return vec![(Dest::Peer(alloc.relay.clone(), *peer), data[4..end].to_vec())];
                    }
                }
            }
            return vec![];
        }
        let Some(msg) = parse(data) else { return vec![] };
        if msg.class == INDICATION && msg.method == SEND {
            let a = self.allocations.lock().unwrap();
            if let (Some(alloc), Some(peer), Some(d)) =
                (a.get(&client), msg.get(A_PEER).and_then(unxor_addr), msg.get(A_DATA))
            {
                if alloc.bytes.lock().unwrap().take(d.len() as f64) {
                    return vec![(Dest::Peer(alloc.relay.clone(), SocketAddr::V4(peer)), d.to_vec())];
                }
            }
            return vec![];
        }
        if msg.class != REQUEST || !self.requests.lock().unwrap().allow(client.ip()) {
            return vec![];
        }
        let SocketAddr::V4(client4) = client else { return vec![] };
        let tid = msg.tid;
        if msg.method == BINDING {
            return vec![(Dest::Client, build(msg_type(BINDING, SUCCESS), &tid, &[(A_MAPPED, &xor_addr(client4))], None))];
        }
        if msg.get(A_MI).is_none() {
            // Long-term credentials: challenge with realm + nonce.
            let err = error_code(4, 1, "Unauthorized");
            return vec![(
                Dest::Client,
                build(msg_type(msg.method, ERROR), &tid, &[(A_ERROR, &err), (A_REALM, REALM), (A_NONCE, &self.nonce)], None),
            )];
        }
        let username = msg.get(A_USERNAME).map(|u| String::from_utf8_lossy(u).into_owned()).unwrap_or_else(|| self.opts.username.clone());
        let key = long_term_key(&username, msg.get(A_REALM).unwrap_or(REALM), &self.opts.password);
        let ok = |method: u16, attrs: &[(u16, &[u8])]| vec![(Dest::Client, build(msg_type(method, SUCCESS), &tid, attrs, Some(&key)))];
        match msg.method {
            ALLOCATE => match self.allocate(client, client4).await {
                Err(e) if e.kind() == std::io::ErrorKind::QuotaExceeded => {
                    line(format!("TURN allocation for {client} refused: {e}"));
                    let err = error_code(4, 86, "Allocation Quota Reached");
                    vec![(Dest::Client, build(msg_type(ALLOCATE, ERROR), &tid, &[(A_ERROR, &err)], Some(&key)))]
                }
                Ok(relay_addr) => ok(
                    ALLOCATE,
                    &[
                        (A_RELAYED, &xor_addr(relay_addr)),
                        (A_MAPPED, &xor_addr(client4)),
                        (A_LIFETIME, &LIFETIME.to_be_bytes()),
                        (A_SOFTWARE, b"mkwmp-turn"),
                    ],
                ),
                Err(e) => {
                    line(format!("TURN allocation for {client} refused: {e}"));
                    let err = error_code(5, 8, "Insufficient Capacity");
                    vec![(Dest::Client, build(msg_type(ALLOCATE, ERROR), &tid, &[(A_ERROR, &err)], Some(&key)))]
                }
            },
            REFRESH => {
                let lifetime = msg
                    .get(A_LIFETIME)
                    .and_then(|v| v.get(..4))
                    .map(|v| u32::from_be_bytes(v.try_into().unwrap()))
                    .unwrap_or(LIFETIME)
                    .min(MAX_LIFETIME);
                let mut a = self.allocations.lock().unwrap();
                if lifetime == 0 {
                    if a.remove(&client).is_some() {
                        line(format!("TURN allocation for {client} released"));
                    }
                } else if let Some(alloc) = a.get_mut(&client) {
                    alloc.expires = Instant::now() + KEEP.max(Duration::from_secs(lifetime.into()));
                    line(format!("TURN refresh from {client} (lifetime {lifetime} s)"));
                }
                drop(a);
                ok(REFRESH, &[(A_LIFETIME, &lifetime.to_be_bytes())])
            }
            CREATE_PERMISSION => ok(CREATE_PERMISSION, &[]),
            CHANNEL_BIND => {
                let a = self.allocations.lock().unwrap();
                let full = a.get(&client).is_some_and(|al| {
                    let c = al.channels.lock().unwrap();
                    c.by_channel.len() >= limits::TURN_CHANNELS
                        && msg.get(A_CHANNEL).and_then(|v| v.get(..2)).is_none_or(|v| !c.by_channel.contains_key(&u16::from_be_bytes([v[0], v[1]])))
                });
                if full {
                    let err = error_code(5, 8, "Insufficient Capacity");
                    return vec![(Dest::Client, build(msg_type(CHANNEL_BIND, ERROR), &tid, &[(A_ERROR, &err)], Some(&key)))];
                }
                if let (Some(alloc), Some(ch), Some(peer)) = (
                    a.get(&client),
                    msg.get(A_CHANNEL).and_then(|v| v.get(..2)).map(|v| u16::from_be_bytes([v[0], v[1]])),
                    msg.get(A_PEER).and_then(unxor_addr),
                ) {
                    let mut c = alloc.channels.lock().unwrap();
                    c.by_channel.insert(ch, SocketAddr::V4(peer));
                    c.by_peer.insert(SocketAddr::V4(peer), ch);
                    line(format!("TURN {client}: channel {ch:#x} -> {peer}"));
                }
                drop(a);
                ok(CHANNEL_BIND, &[])
            }
            _ => vec![],
        }
    }

    /// The client's allocation (reused if it has one); returns the relayed address.
    async fn allocate(self: &Arc<Self>, client: SocketAddr, client4: SocketAddrV4) -> std::io::Result<SocketAddrV4> {
        {
            let mut a = self.allocations.lock().unwrap();
            if let Some(x) = a.get_mut(&client) {
                x.expires = Instant::now() + KEEP;
                return Ok(x.relay_addr);
            }
            if a.keys().filter(|c| c.ip() == client.ip()).count() >= limits::TURN_ALLOCATIONS_PER_IP {
                return Err(std::io::Error::new(std::io::ErrorKind::QuotaExceeded, "too many allocations from this address"));
            }
        }
        let relay = Arc::new(self.bind_relay().await?);
        let port = relay.local_addr()?.port();
        let relay_addr = SocketAddrV4::new(self.opts.public_host, port);
        let channels = Arc::new(Mutex::new(Channels::default()));
        let bytes = Arc::new(Mutex::new(Bucket::new(limits::TURN_BYTES_PER_SEC, 2.0 * limits::TURN_BYTES_PER_SEC)));
        let task = tokio::spawn(from_peers(relay.clone(), channels.clone(), bytes.clone(), self.sock.clone(), client4));
        let alloc = Allocation { relay, bytes, relay_addr, channels, expires: Instant::now() + KEEP, task };
        let mut a = self.allocations.lock().unwrap();
        // Two Allocates raced: keep the first.
        if let Some(existing) = a.get(&client) {
            return Ok(existing.relay_addr);
        }
        a.insert(client, alloc);
        line(format!("TURN allocation for {client} relayed at {relay_addr} ({} live)", a.len()));
        Ok(relay_addr)
    }

    async fn bind_relay(&self) -> std::io::Result<UdpSocket> {
        match self.opts.relay_ports {
            None => UdpSocket::bind(SocketAddr::new(self.opts.relay_bind, 0)).await,
            Some((lo, hi)) => {
                // Start at a random port so released ports aren't reused at once.
                let span = u32::from(hi - lo) + 1;
                let start = rand::random::<u32>() % span;
                for i in 0..span {
                    let port = lo + ((start + i) % span) as u16;
                    if let Ok(s) = UdpSocket::bind(SocketAddr::new(self.opts.relay_bind, port)).await {
                        return Ok(s);
                    }
                }
                Err(std::io::Error::other(format!("no free relay port in {lo}-{hi}")))
            }
        }
    }
}

enum Dest {
    Client,
    Peer(Arc<UdpSocket>, SocketAddr),
}

/// Peer traffic on a relay socket → the client, as ChannelData when the peer
/// has a channel, else a Data indication.
async fn from_peers(
    relay: Arc<UdpSocket>,
    channels: Arc<Mutex<Channels>>,
    bytes: Arc<Mutex<Bucket>>,
    server: Arc<UdpSocket>,
    client: SocketAddrV4,
) {
    let mut buf = vec![0u8; 65536];
    loop {
        let (n, peer) = match relay.recv_from(&mut buf).await {
            Ok(x) => x,
            Err(_) => continue,
        };
        if !bytes.lock().unwrap().take(n as f64) {
            continue;
        }
        let channel = channels.lock().unwrap().by_peer.get(&peer).copied();
        let frame = match (channel, peer) {
            (Some(ch), _) => channel_data(ch, &buf[..n]),
            (None, SocketAddr::V4(p4)) => {
                let mut tid = [0u8; 12];
                rand::rngs::OsRng.fill_bytes(&mut tid);
                build(msg_type(DATA, INDICATION), &tid, &[(A_PEER, &xor_addr(p4)), (A_DATA, &buf[..n])], None)
            }
            (None, SocketAddr::V6(_)) => continue,
        };
        let _ = server.send_to(&frame, SocketAddr::V4(client)).await;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn types_round_trip() {
        for m in [BINDING, ALLOCATE, REFRESH, SEND, DATA, CREATE_PERMISSION, CHANNEL_BIND] {
            for c in [REQUEST, INDICATION, SUCCESS, ERROR] {
                assert_eq!(split_type(msg_type(m, c)), (m, c));
            }
        }
        assert_eq!(msg_type(ALLOCATE, ERROR), 0x0113);
        assert_eq!(msg_type(DATA, INDICATION), 0x0017);
    }

    #[test]
    fn xor_address_matches_rfc5769() {
        // RFC 5769 2.2: 192.0.2.1:32853 -> port 0xa147, addr 0xe112a643
        let a: SocketAddrV4 = "192.0.2.1:32853".parse().unwrap();
        assert_eq!(xor_addr(a), vec![0x00, 0x01, 0xa1, 0x47, 0xe1, 0x12, 0xa6, 0x43]);
        assert_eq!(unxor_addr(&xor_addr(a)), Some(a));
    }

    #[test]
    fn built_messages_parse_and_verify() {
        let tid = [9u8; 12];
        let key = long_term_key("mkwmp", REALM, "mkwmp");
        let m = build(msg_type(REFRESH, SUCCESS), &tid, &[(A_LIFETIME, &600u32.to_be_bytes()), (A_SOFTWARE, b"abc")], Some(&key));
        let p = parse(&m).unwrap();
        assert_eq!((p.method, p.class, p.tid), (REFRESH, SUCCESS, tid));
        assert_eq!(p.get(A_SOFTWARE), Some(&b"abc"[..]));
        // FINGERPRINT covers everything before it, with the final length.
        let fp_at = m.len() - 8;
        assert_eq!(&m[fp_at + 4..], &(crc32fast::hash(&m[..fp_at]) ^ 0x5354_554E).to_be_bytes());
        // MESSAGE-INTEGRITY: HMAC over the header (length up to MI) + attributes before it.
        let mi_at = fp_at - 24;
        let mut head = m[..mi_at].to_vec();
        head[2..4].copy_from_slice(&((mi_at - 20 + 24) as u16).to_be_bytes());
        let mut mac = <Hmac<Sha1> as Mac>::new_from_slice(&key).unwrap();
        mac.update(&head);
        assert_eq!(&m[mi_at + 4..mi_at + 24], &mac.finalize().into_bytes()[..]);
    }
}
