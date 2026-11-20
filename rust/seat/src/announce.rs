//! Announcing this seat server to a beholder (docs/contracts/BEHOLDER_API.md §5): a signed
//! announce at start, then every 30 s ± 3 s. A 429 waits its Retry-After (default 60 s, at most
//! 300 s); any other answer or a network failure keeps the cadence. Logs the outcome when it
//! changes, never the key.

use std::path::Path;
use std::time::Duration;

use base64::engine::general_purpose::URL_SAFE_NO_PAD as B64;
use base64::Engine;
use beholder::wire::{check_fields, Announce, Fields, Reject};
use ed25519_dalek::SigningKey;
use rand::Rng;

use crate::http::AppState;

pub const INTERVAL_SECS: u64 = 30;
const JITTER_SECS: u64 = 3;
const TIMEOUT: Duration = Duration::from_secs(10);

/// Everything an announce needs but the live counts.
pub struct Settings {
    /// `<beholder>/beholder/v1/announce`.
    pub url: String,
    pub endpoint: String,
    pub name: String,
    pub region: String,
    pub capacity: u32,
    pub key: SigningKey,
}

impl Settings {
    /// From the config; `Ok(None)` when no beholder is set. Breaking a field rule is an error, so
    /// the server refuses to start rather than be refused every 30 s.
    pub fn from_config(cfg: &config::ServerConfig) -> Result<Option<Self>, String> {
        let a = &cfg.announce;
        let base = a.beholder_url.trim().trim_end_matches('/');
        if base.is_empty() {
            return Ok(None);
        }
        if !(base.starts_with("https://") || base.starts_with("http://")) {
            return Err("[announce] beholder_url must start with http:// or https://".into());
        }
        let key_file = match (&a.key_file, &cfg.ticket.private_key_file) {
            (Some(p), _) => p.clone(),
            (None, Some(t)) => t.with_file_name("beholder-ed25519.key"),
            (None, None) => return Err("set [announce] key_file (or [ticket] private_key_file)".into()),
        };
        let name = if a.name.trim().is_empty() { cfg.server.name.trim() } else { a.name.trim() };
        let s = Self {
            url: format!("{base}/beholder/v1/announce"),
            endpoint: a.endpoint.trim().trim_end_matches('/').to_string(),
            name: name.to_string(),
            region: a.region.trim().to_string(),
            capacity: u32::try_from(cfg.seats.capacity).unwrap_or(u32::MAX),
            key: load_or_create_key(&key_file)?,
        };
        if let Err(Reject::Invalid(field)) = check_fields(&s.fields(0, 0)) {
            let setting = match field {
                "endpoint" => "[announce] endpoint (http(s)://host[:port], public, no path)",
                "name" => "[announce] name (1-48 characters)",
                "region" => "[announce] region (up to 16 of A-Z a-z 0-9 -)",
                "capacity" => "[seats] capacity (at most 100000 to announce)",
                other => other,
            };
            return Err(format!("{setting} can't be announced"));
        }
        Ok(Some(s))
    }

    pub fn fields(&self, seated: usize, queued: usize) -> Fields {
        let clamp = |n: usize| u32::try_from(n).unwrap_or(u32::MAX);
        Fields {
            endpoint: self.endpoint.clone(),
            name: self.name.clone(),
            region: self.region.clone(),
            version: beholder::VERSION.into(),
            capacity: self.capacity,
            seated: clamp(seated).min(self.capacity),
            queued: clamp(queued).min(beholder::wire::MAX_COUNT),
        }
    }

    pub fn public_key(&self) -> String {
        beholder::wire::public_key_b64(&self.key)
    }
}

/// The announce key: read the base64url seed, or make one (0600) on first start.
pub fn load_or_create_key(path: &Path) -> Result<SigningKey, String> {
    let shown = |e: std::io::Error| format!("{}: {e}", path.display());
    match std::fs::read_to_string(path) {
        Ok(text) => {
            let seed: [u8; 32] = B64
                .decode(text.trim())
                .ok()
                .and_then(|b| b.try_into().ok())
                .ok_or_else(|| format!("{}: not a base64url Ed25519 seed", path.display()))?;
            Ok(SigningKey::from_bytes(&seed))
        }
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
            let key = SigningKey::generate(&mut rand::rngs::OsRng);
            write_secret(path, format!("{}\n", B64.encode(key.to_bytes())).as_bytes()).map_err(shown)?;
            println!("announce: made a new server key in {}", path.display());
            Ok(key)
        }
        Err(e) => Err(shown(e)),
    }
}

fn write_secret(path: &Path, bytes: &[u8]) -> std::io::Result<()> {
    use std::io::Write;
    if let Some(dir) = path.parent().filter(|d| !d.as_os_str().is_empty()) {
        std::fs::create_dir_all(dir)?;
    }
    let mut o = std::fs::OpenOptions::new();
    o.write(true).create_new(true);
    #[cfg(unix)]
    std::os::unix::fs::OpenOptionsExt::mode(&mut o, 0o600);
    o.open(path)?.write_all(bytes)
}

/// One announce now: the outcome (`listed`, `429`, `HTTP <code> <error>`, `timeout`, `connect`,
/// `other`) and the wait before the next.
pub async fn once(http: &reqwest::Client, s: &Settings, state: &AppState) -> (String, Duration) {
    let (seated, queued) = {
        let st = state.roster().lock().unwrap().status(state.now());
        (st.seated, st.queued)
    };
    let body = Announce::sign(&s.key, &s.fields(seated, queued), state.now() / 1000);
    let normal = Duration::from_secs(INTERVAL_SECS - JITTER_SECS + rand::thread_rng().gen_range(0..=2 * JITTER_SECS));
    match http.post(&s.url).json(&body).timeout(TIMEOUT).send().await {
        Ok(r) if r.status() == reqwest::StatusCode::TOO_MANY_REQUESTS => {
            let wait = r
                .headers()
                .get(reqwest::header::RETRY_AFTER)
                .and_then(|v| v.to_str().ok()?.trim().parse::<u64>().ok())
                .unwrap_or(60)
                .clamp(1, 300);
            ("429".into(), Duration::from_secs(wait))
        }
        Ok(r) if r.status().is_success() => ("listed".into(), normal),
        Ok(r) => {
            let code = r.status().as_u16();
            let text = r.text().await.unwrap_or_default();
            let why = serde_json::from_str::<serde_json::Value>(&text)
                .ok()
                .and_then(|v| {
                    let e = &v["error"];
                    Some(format!("{} {}", e["code"].as_str()?, e["field"].as_str().unwrap_or("")).trim().to_string())
                })
                .unwrap_or_default();
            (format!("HTTP {code} {why}").trim().to_string(), normal)
        }
        Err(e) if e.is_timeout() => ("timeout".into(), normal),
        Err(e) if e.is_connect() => ("connect".into(), normal),
        Err(_) => ("other".into(), normal),
    }
}

/// Announce until the process ends.
pub fn spawn(s: Settings, state: AppState) {
    println!("announce: to a beholder as \"{}\" ({}), every {INTERVAL_SECS} s", s.name, s.endpoint);
    tokio::spawn(async move {
        let http = reqwest::Client::builder().user_agent(format!("mkwmp-seat/{}", beholder::VERSION)).build().unwrap_or_default();
        let mut last = String::new();
        loop {
            let (outcome, wait) = once(&http, &s, &state).await;
            if outcome != last {
                println!("announce: {outcome}");
                last = outcome;
            }
            tokio::time::sleep(wait).await;
        }
    });
}
