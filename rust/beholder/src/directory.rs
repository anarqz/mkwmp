//! The listings and every limit, in memory. One mutex guards it all: an announce or a list is a
//! few hash lookups (the signature check runs before the lock is taken).

use std::collections::HashMap;

use serde::Serialize;

use crate::wire::{Announce, Fields};

/// A listing whose last announce is older than this is gone.
pub const TTL_MS: u64 = 90_000;
/// Items in one list answer (and listings kept): the global cap.
pub const MAX_SERVERS: usize = 500;
/// Live listings announced from one client address (a household may host a couple).
pub const MAX_KEYS_PER_IP: usize = 4;

#[derive(Debug, Clone, Copy)]
pub struct Limit {
    pub max: u32,
    pub window_ms: u64,
}

const MINUTE: u64 = 60_000;
pub const ANNOUNCE_PER_KEY: Limit = Limit { max: 12, window_ms: MINUTE };
pub const ANNOUNCE_PER_IP: Limit = Limit { max: 60, window_ms: MINUTE };
pub const LIST_PER_IP: Limit = Limit { max: 60, window_ms: MINUTE };
/// Every announce together: 500 servers at 2 a minute, with room for restarts.
pub const ANNOUNCE_GLOBAL: Limit = Limit { max: 1_500, window_ms: MINUTE };
/// Every list read together (Cloudflare caches the list for 10 s in front of this).
pub const LIST_GLOBAL: Limit = Limit { max: 6_000, window_ms: MINUTE };
/// Rate-limit keys tracked at once; past it, new client addresses wait for the next sweep.
const MAX_COUNTERS: usize = 100_000;

/// What the launcher sees (BEHOLDER_API.md §3). Instants are RFC 3339 UTC, whole seconds.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Listing {
    pub public_key: String,
    pub name: String,
    pub endpoint: String,
    pub region: String,
    pub version: String,
    pub capacity: u32,
    pub seated: u32,
    pub queued: u32,
    pub first_seen: String,
    pub last_seen: String,
}

struct Entry {
    fields: Fields,
    issued_at: u64,
    first_seen: u64,
    last_seen: u64,
    ip: String,
}

/// Why the directory turned an announce or a read away.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Refusal {
    /// 429, retry after this many ms.
    RateLimited(u64),
    /// 409: older than the listing's latest announce (a replay).
    Replayed,
    /// 403: this address already lists `MAX_KEYS_PER_IP` servers.
    TooManyKeys,
    /// 503: `MAX_SERVERS` listings, retry later.
    Full,
}

/// Fixed-window counters: (window start, hits).
#[derive(Default)]
struct Counters(HashMap<String, (u64, u32)>);

impl Counters {
    fn take(&mut self, key: &str, l: Limit, now: u64) -> Result<(), u64> {
        if !self.0.contains_key(key) && self.0.len() >= MAX_COUNTERS {
            return Err(1_000);
        }
        let c = self.0.entry(key.to_string()).or_insert((now, 0));
        if now.saturating_sub(c.0) >= l.window_ms {
            *c = (now, 0);
        }
        if c.1 >= l.max {
            return Err((c.0 + l.window_ms).saturating_sub(now).max(1));
        }
        c.1 += 1;
        Ok(())
    }

    fn sweep(&mut self, now: u64) {
        self.0.retain(|_, (start, _)| now.saturating_sub(*start) < MINUTE);
    }
}

#[derive(Default)]
pub struct Directory {
    entries: HashMap<String, Entry>,
    counters: Counters,
}

impl Directory {
    /// Before parsing: the client address and the global announce budget.
    pub fn admit_announce(&mut self, ip: &str, now: u64) -> Result<(), Refusal> {
        self.counters.take("announce", ANNOUNCE_GLOBAL, now).map_err(Refusal::RateLimited)?;
        self.counters.take(&format!("aip:{ip}"), ANNOUNCE_PER_IP, now).map_err(Refusal::RateLimited)
    }

    pub fn admit_list(&mut self, ip: &str, now: u64) -> Result<(), Refusal> {
        self.counters.take("list", LIST_GLOBAL, now).map_err(Refusal::RateLimited)?;
        self.counters.take(&format!("lip:{ip}"), LIST_PER_IP, now).map_err(Refusal::RateLimited)
    }

    /// A verified announce (the key's limit counts only verified ones, so nobody can spend
    /// another server's budget).
    pub fn announce(&mut self, a: &Announce, ip: &str, now: u64) -> Result<(), Refusal> {
        self.counters.take(&format!("key:{}", a.public_key), ANNOUNCE_PER_KEY, now).map_err(Refusal::RateLimited)?;
        self.expire(now);
        let from_ip = self.entries.values().filter(|x| x.ip == ip).count();
        let first_seen = match self.entries.get(&a.public_key) {
            Some(e) if a.issued_at < e.issued_at => return Err(Refusal::Replayed),
            Some(e) if e.ip != ip && from_ip >= MAX_KEYS_PER_IP => return Err(Refusal::TooManyKeys),
            Some(e) => e.first_seen,
            None if from_ip >= MAX_KEYS_PER_IP => return Err(Refusal::TooManyKeys),
            None if self.entries.len() >= MAX_SERVERS => return Err(Refusal::Full),
            None => now,
        };
        let entry = Entry { fields: a.fields(), issued_at: a.issued_at, first_seen, last_seen: now, ip: ip.to_string() };
        self.entries.insert(a.public_key.clone(), entry);
        Ok(())
    }

    /// Live listings, most seated first, then by name.
    pub fn list(&mut self, now: u64) -> Vec<Listing> {
        self.expire(now);
        let mut v: Vec<Listing> = self
            .entries
            .iter()
            .map(|(k, e)| Listing {
                public_key: k.clone(),
                name: e.fields.name.clone(),
                endpoint: e.fields.endpoint.clone(),
                region: e.fields.region.clone(),
                version: e.fields.version.clone(),
                capacity: e.fields.capacity,
                seated: e.fields.seated,
                queued: e.fields.queued,
                first_seen: rfc3339(e.first_seen / 1000),
                last_seen: rfc3339(e.last_seen / 1000),
            })
            .collect();
        v.sort_by(|a, b| b.seated.cmp(&a.seated).then_with(|| a.name.to_lowercase().cmp(&b.name.to_lowercase())));
        v
    }

    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    fn expire(&mut self, now: u64) {
        self.entries.retain(|_, e| now.saturating_sub(e.last_seen) <= TTL_MS);
    }

    /// Drop expired listings and idle counters (the server runs this every few seconds).
    pub fn sweep(&mut self, now: u64) {
        self.expire(now);
        self.counters.sweep(now);
    }
}

/// Unix seconds as `YYYY-MM-DDTHH:MM:SSZ`.
pub fn rfc3339(secs: u64) -> String {
    let days = (secs / 86_400) as i64;
    let rem = secs % 86_400;
    // Civil date from days since 1970-01-01 (Howard Hinnant's algorithm).
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = yoe + era * 400 + i64::from(m <= 2);
    format!("{y:04}-{m:02}-{d:02}T{:02}:{:02}:{:02}Z", rem / 3600, rem % 3600 / 60, rem % 60)
}

#[cfg(test)]
mod tests {
    use super::*;
    use ed25519_dalek::SigningKey;

    fn ann(seed: u8, issued: u64) -> Announce {
        let f = Fields {
            endpoint: format!("https://s{seed}.example.net"),
            name: format!("Server {seed}"),
            region: String::new(),
            version: "1.3.0".into(),
            capacity: 50,
            seated: u32::from(seed),
            queued: 0,
        };
        Announce::sign(&SigningKey::from_bytes(&[seed; 32]), &f, issued)
    }

    #[test]
    fn dates() {
        assert_eq!(rfc3339(0), "1970-01-01T00:00:00Z");
        assert_eq!(rfc3339(1_795_219_200), "2026-11-21T00:00:00Z");
        assert_eq!(rfc3339(951_782_400), "2000-02-29T00:00:00Z");
    }

    #[test]
    fn listings_refresh_expire_and_sort() {
        let mut d = Directory::default();
        d.announce(&ann(1, 100), "1.1.1.1", 0).unwrap();
        d.announce(&ann(2, 100), "2.2.2.2", 0).unwrap();
        let l = d.list(1_000);
        assert_eq!(l.iter().map(|x| x.seated).collect::<Vec<_>>(), vec![2, 1]);
        d.announce(&ann(1, 130), "1.1.1.1", 60_000).unwrap();
        let l = d.list(TTL_MS + 1);
        assert_eq!(l.len(), 1);
        assert_eq!(l[0].first_seen, rfc3339(0));
        assert_eq!(l[0].last_seen, rfc3339(60));
        assert!(d.list(60_000 + TTL_MS + 1).is_empty());
    }

    #[test]
    fn replays_and_key_limits() {
        let mut d = Directory::default();
        d.announce(&ann(1, 200), "1.1.1.1", 0).unwrap();
        assert_eq!(d.announce(&ann(1, 199), "1.1.1.1", 10), Err(Refusal::Replayed));
        for i in 2..=MAX_KEYS_PER_IP as u8 {
            d.announce(&ann(i, 200), "1.1.1.1", 10).unwrap();
        }
        assert_eq!(d.announce(&ann(99, 200), "1.1.1.1", 10), Err(Refusal::TooManyKeys));
        assert!(d.announce(&ann(99, 200), "9.9.9.9", 10).is_ok());
        // The per-key budget: 12 a minute.
        let mut d = Directory::default();
        for i in 0..ANNOUNCE_PER_KEY.max {
            d.announce(&ann(5, 300 + u64::from(i)), "5.5.5.5", 100).unwrap();
        }
        assert!(matches!(d.announce(&ann(5, 400), "5.5.5.5", 100), Err(Refusal::RateLimited(_))));
        assert!(d.announce(&ann(5, 400), "5.5.5.5", 100 + MINUTE).is_ok());
    }

    #[test]
    fn address_budget() {
        let mut d = Directory::default();
        for _ in 0..LIST_PER_IP.max {
            d.admit_list("3.3.3.3", 0).unwrap();
        }
        assert_eq!(d.admit_list("3.3.3.3", 1_000), Err(Refusal::RateLimited(MINUTE - 1_000)));
        assert!(d.admit_list("4.4.4.4", 1_000).is_ok());
    }
}
