//! The one human-facing client file (`client.toml`). The probe's `mkwmp.toml`
//! is generated from this plus the granted ticket.

use serde::Deserialize;
use std::path::PathBuf;

#[derive(Debug, Deserialize)]
pub struct ClientConfig {
    pub server: String,         // seat server address (host:port)
    pub name: String,           // username
    pub game: PathBuf,          // game directory (contains mkw.exe)
    #[serde(default = "one")]
    pub slot: u8,               // loopback only
}

fn one() -> u8 {
    1
}

impl ClientConfig {
    pub fn load(path: &str) -> Result<Self, String> {
        let text = std::fs::read_to_string(path).map_err(|e| format!("read {path}: {e}"))?;
        let cfg: ClientConfig = toml::from_str(&text).map_err(|e| format!("parse {path}: {e}"))?;
        if cfg.name.trim().is_empty() {
            return Err("name must not be empty".into());
        }
        Ok(cfg)
    }

    /// The NPLN server the game connects to. Same host as the seat server;
    /// the NPLN TLS port is fixed (8905) for now.
    pub fn npln_endpoint(&self) -> String {
        let host = self.server.rsplit_once(':').map(|(h, _)| h).unwrap_or(&self.server);
        format!("{host}:8905")
    }
}
