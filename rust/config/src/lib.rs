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
    pub capacity: usize,
    pub bind: String,
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
        if cfg.seats.capacity == 0 {
            return Err(ConfigError::Invalid("seats.capacity must be > 0"));
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
        assert!(cfg.seats.capacity >= 1);
        assert_eq!(cfg.lobby.lobby_size, 2);
        assert!(cfg.tls.cert.ends_with("capture-cert.pem"));
    }

    #[test]
    fn zero_capacity_is_rejected() {
        let p = std::env::temp_dir().join("mkwmp_bad_server.toml");
        std::fs::write(&p, concat!(
            "[server]\nname=\"x\"\nbind=\"x\"\n[seats]\ncapacity=0\nbind=\"x\"\n",
            "[lobby]\nbind=\"x\"\nlobby_size=2\nmax_lobbies_per_worker=1\n",
            "[tls]\ncert=\"c\"\nkey=\"k\"\n")).unwrap();
        assert!(matches!(ServerConfig::load(&p), Err(ConfigError::Invalid(_))));
    }
}
