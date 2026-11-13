//! Typed config for the mkwmp server services. Mirrors `config/server.toml`.
//! Path fields resolve relative to the file's directory so configs are
//! portable. The client config (`config/client.toml`) is read by the C probe,
//! not here.

use serde::Deserialize;
use std::path::{Path, PathBuf};

#[derive(Debug)]
pub enum ConfigError {
    Read(std::io::Error),
    Parse(toml::de::Error),
    Invalid(&'static str),
}

impl std::fmt::Display for ConfigError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ConfigError::Read(e) => write!(f, "reading config: {e}"),
            ConfigError::Parse(e) => write!(f, "parsing config: {e}"),
            ConfigError::Invalid(m) => write!(f, "invalid config: {m}"),
        }
    }
}
impl std::error::Error for ConfigError {}

// ---- server ---------------------------------------------------------------

#[derive(Debug, Deserialize)]
pub struct ServerConfig {
    pub server: Server,
    pub seats: Seats,
    #[serde(default)]
    pub ticket: Ticket,
    #[serde(default)]
    pub registry: RegistryCfg,
    pub lobby: Lobby,
    pub tls: Tls,
}

#[derive(Debug, Deserialize)]
pub struct Server {
    pub name: String,
    pub bind: String,
}

#[derive(Debug, Deserialize)]
pub struct Seats {
    /// Players seated at once; everyone past it queues.
    pub capacity: usize,
    /// HTTP bind of the seat API (what the Cloudflare tunnel points at).
    pub bind: String,
    #[serde(default = "d_max_queue")]
    pub max_queue: usize,
    #[serde(default = "d_heartbeat")]
    pub heartbeat_secs: u64,
    #[serde(default = "d_seat_timeout")]
    pub seat_timeout_secs: u64,
    #[serde(default = "d_queue_poll")]
    pub queue_poll_secs: u64,
    #[serde(default = "d_queue_timeout")]
    pub queue_timeout_secs: u64,
    /// NPLN endpoint (`host:port`) handed to seated players: the lobby.
    pub lobby_endpoint: String,
    /// File with one bearer token per line accepted on the lobby roster
    /// route. Absent: the route is disabled.
    #[serde(default)]
    pub lobby_tokens_file: Option<PathBuf>,
}

fn d_max_queue() -> usize { 500 }
fn d_heartbeat() -> u64 { 20 }
fn d_seat_timeout() -> u64 { 60 }
fn d_queue_poll() -> u64 { 5 }
fn d_queue_timeout() -> u64 { 30 }
fn d_ttl() -> u64 { 12 * 3600 }

/// Registered profiles (name + PIN). The only state kept across restarts.
#[derive(Debug, Deserialize)]
pub struct RegistryCfg {
    /// JSON file of profiles (hashed PINs). Absent: in memory only.
    #[serde(default)]
    pub file: Option<PathBuf>,
    /// Names only the admin can register (`mkwmp admin register <name>`).
    #[serde(default)]
    pub reserved_names: Vec<String>,
    /// Unused names become free after this many days (0 = never).
    #[serde(default = "d_expire")]
    pub expire_days: u64,
    /// File with the token `mkwmp admin` sends (loopback only).
    #[serde(default)]
    pub admin_token_file: Option<PathBuf>,
}

fn d_expire() -> u64 { 180 }

impl Default for RegistryCfg {
    fn default() -> Self {
        Self { file: None, reserved_names: vec![], expire_days: d_expire(), admin_token_file: None }
    }
}

#[derive(Debug, Deserialize)]
pub struct Ticket {
    /// Key id stamped in tickets this server signs.
    #[serde(default)]
    pub kid: String,
    /// File holding the base64url Ed25519 seed (`seat keygen`). Seat server only.
    #[serde(default)]
    pub private_key_file: Option<PathBuf>,
    /// `kid:base64url-public-key` entries a lobby trusts.
    #[serde(default)]
    pub public_keys: Vec<String>,
    #[serde(default = "d_ttl")]
    pub ttl_secs: u64,
}

impl Default for Ticket {
    fn default() -> Self {
        Self { kid: String::new(), private_key_file: None, public_keys: vec![], ttl_secs: d_ttl() }
    }
}

#[derive(Debug, Deserialize)]
pub struct Lobby {
    pub bind: String,
    pub lobby_size: usize,
    pub max_lobbies_per_worker: usize,
}

#[derive(Debug, Deserialize)]
pub struct Tls {
    pub cert: PathBuf,
    pub key: PathBuf,
}

impl ServerConfig {
    pub fn load(path: impl AsRef<Path>) -> Result<Self, ConfigError> {
        let path = path.as_ref();
        let mut cfg: ServerConfig = parse(path)?;
        let base = path.parent().unwrap_or(Path::new("."));
        cfg.tls.cert = resolve(base, &cfg.tls.cert);
        cfg.tls.key = resolve(base, &cfg.tls.key);
        cfg.ticket.private_key_file = cfg.ticket.private_key_file.map(|p| resolve(base, &p));
        cfg.seats.lobby_tokens_file = cfg.seats.lobby_tokens_file.map(|p| resolve(base, &p));
        cfg.registry.file = cfg.registry.file.map(|p| resolve(base, &p));
        cfg.registry.admin_token_file = cfg.registry.admin_token_file.map(|p| resolve(base, &p));
        if cfg.seats.capacity == 0 {
            return Err(ConfigError::Invalid("seats.capacity must be > 0"));
        }
        if cfg.seats.seat_timeout_secs <= cfg.seats.heartbeat_secs {
            return Err(ConfigError::Invalid("seats.seat_timeout_secs must exceed heartbeat_secs"));
        }
        if cfg.lobby.lobby_size < 2 {
            return Err(ConfigError::Invalid("lobby.lobby_size must be >= 2"));
        }
        Ok(cfg)
    }
}

// ---- helpers --------------------------------------------------------------

fn parse<T: for<'de> Deserialize<'de>>(path: &Path) -> Result<T, ConfigError> {
    let text = std::fs::read_to_string(path).map_err(ConfigError::Read)?;
    toml::from_str(&text).map_err(ConfigError::Parse)
}

fn resolve(base: &Path, p: &Path) -> PathBuf {
    if p.is_absolute() {
        p.to_path_buf()
    } else {
        base.join(p)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const REPO: &str = concat!(env!("CARGO_MANIFEST_DIR"), "/../..");

    #[test]
    fn server_config_loads_and_resolves_paths() {
        let cfg = ServerConfig::load(format!("{REPO}/config/server.toml")).unwrap();
        assert_eq!(cfg.seats.capacity, 50);
        assert_eq!(cfg.seats.heartbeat_secs, 20);
        assert!(cfg.ticket.private_key_file.unwrap().ends_with("seat-ed25519.key"));
        assert_eq!(cfg.lobby.lobby_size, 2);
        assert!(cfg.tls.cert.ends_with("capture-cert.pem"));
    }

    #[test]
    fn zero_capacity_is_rejected() {
        let p = std::env::temp_dir().join("mkwmp_bad_server.toml");
        std::fs::write(&p, concat!(
            "[server]\nname=\"x\"\nbind=\"x\"\n[seats]\ncapacity=0\nbind=\"x\"\nlobby_endpoint=\"x\"\n",
            "[lobby]\nbind=\"x\"\nlobby_size=2\nmax_lobbies_per_worker=1\n",
            "[tls]\ncert=\"c\"\nkey=\"k\"\n")).unwrap();
        assert!(matches!(ServerConfig::load(&p), Err(ConfigError::Invalid(_))));
    }
}
