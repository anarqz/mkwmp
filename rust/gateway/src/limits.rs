//! Abuse limits for everything the gateway accepts: TCP connections per IP,
//! and token buckets for UDP requests and relayed bytes.
//!
//! | what                          | limit                         |
//! | TCP connections per IP        | 32 at once, 120 new a minute  |
//! | TLS handshake / h2 preface    | 10 s each                     |
//! | HTTP/2 streams per connection | 128 at once; headers 16 KiB   |
//! | connection with no streams    | closed after 5 min            |
//! | gRPC message                  | 4 MiB                         |
//! | TURN requests per IP          | 50/s (burst 100)              |
//! | TURN allocations per IP       | 8                             |
//! | TURN relayed bytes            | 512 KiB/s per allocation      |
//! | TURN channels per allocation  | 64                            |
//! | NAT probe replies per IP      | 20/s (burst 40)               |
//!
//! A household or LAN party shares one IP, hence the generous connection
//! numbers: a game holds a handful of connections.

use std::collections::{HashMap, VecDeque};
use std::net::IpAddr;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

pub const CONNS_PER_IP: usize = 32;
pub const NEW_CONNS_PER_MIN: usize = 120;
pub const HANDSHAKE_TIMEOUT: Duration = Duration::from_secs(10);
pub const MAX_STREAMS: u32 = 128;
pub const MAX_HEADER_LIST: u32 = 16 * 1024;
pub const IDLE_CONNECTION: Duration = Duration::from_secs(300);
pub const TURN_ALLOCATIONS_PER_IP: usize = 8;
pub const TURN_CHANNELS: usize = 64;
pub const TURN_BYTES_PER_SEC: f64 = 512.0 * 1024.0;

#[derive(Default)]
struct IpEntry {
    active: usize,
    recent: VecDeque<Instant>,
}

/// TCP admission per client IP.
#[derive(Default)]
pub struct Connections {
    ips: Mutex<HashMap<IpAddr, IpEntry>>,
}

/// Held for a connection's life; frees its slot on drop.
pub struct ConnGuard {
    owner: Arc<Connections>,
    ip: IpAddr,
}

impl Drop for ConnGuard {
    fn drop(&mut self) {
        let mut ips = self.owner.ips.lock().unwrap();
        if let Some(e) = ips.get_mut(&self.ip) {
            e.active = e.active.saturating_sub(1);
            if e.active == 0 && e.recent.back().is_none_or(|t| t.elapsed() > Duration::from_secs(60)) {
                ips.remove(&self.ip);
            }
        }
    }
}

impl Connections {
    pub fn admit(self: &Arc<Self>, ip: IpAddr) -> Result<ConnGuard, &'static str> {
        let mut ips = self.ips.lock().unwrap();
        let e = ips.entry(ip).or_default();
        while e.recent.front().is_some_and(|t| t.elapsed() > Duration::from_secs(60)) {
            e.recent.pop_front();
        }
        if e.active >= CONNS_PER_IP {
            return Err("too many connections from this address");
        }
        if e.recent.len() >= NEW_CONNS_PER_MIN {
            return Err("too many new connections from this address");
        }
        e.active += 1;
        e.recent.push_back(Instant::now());
        Ok(ConnGuard { owner: self.clone(), ip })
    }
}

/// A token bucket.
pub struct Bucket {
    rate: f64,
    burst: f64,
    tokens: f64,
    at: Instant,
}

impl Bucket {
    pub fn new(rate: f64, burst: f64) -> Self {
        Self { rate, burst, tokens: burst, at: Instant::now() }
    }

    /// Take `n` tokens if there are enough.
    pub fn take(&mut self, n: f64) -> bool {
        let now = Instant::now();
        self.tokens = (self.tokens + now.duration_since(self.at).as_secs_f64() * self.rate).min(self.burst);
        self.at = now;
        if self.tokens >= n {
            self.tokens -= n;
            true
        } else {
            false
        }
    }
}

/// Token buckets per IP (bounded memory: full buckets are forgotten).
pub struct PerIp {
    rate: f64,
    burst: f64,
    map: HashMap<IpAddr, Bucket>,
}

impl PerIp {
    pub fn new(rate: f64, burst: f64) -> Self {
        Self { rate, burst, map: HashMap::new() }
    }

    pub fn allow(&mut self, ip: IpAddr) -> bool {
        if self.map.len() > 50_000 {
            let full = Duration::from_secs_f64(self.burst / self.rate);
            self.map.retain(|_, b| b.at.elapsed() < full);
        }
        let (rate, burst) = (self.rate, self.burst);
        self.map.entry(ip).or_insert_with(|| Bucket::new(rate, burst)).take(1.0)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn connection_slots_free_on_drop() {
        let c = Arc::new(Connections::default());
        let ip: IpAddr = "198.51.100.1".parse().unwrap();
        let guards: Vec<_> = (0..CONNS_PER_IP).map(|_| c.admit(ip).unwrap()).collect();
        assert!(c.admit(ip).is_err());
        assert!(c.admit("198.51.100.2".parse().unwrap()).is_ok(), "other addresses are unaffected");
        drop(guards);
        assert!(c.admit(ip).is_ok());
    }

    #[test]
    fn new_connections_per_minute() {
        let c = Arc::new(Connections::default());
        let ip: IpAddr = "198.51.100.3".parse().unwrap();
        for _ in 0..NEW_CONNS_PER_MIN {
            drop(c.admit(ip).unwrap());
        }
        assert!(c.admit(ip).is_err());
    }

    #[test]
    fn buckets_refill() {
        let mut p = PerIp::new(1000.0, 3.0);
        let ip: IpAddr = "198.51.100.4".parse().unwrap();
        assert!(p.allow(ip) && p.allow(ip) && p.allow(ip));
        assert!(!p.allow(ip));
        std::thread::sleep(Duration::from_millis(5));
        assert!(p.allow(ip));
    }
}
