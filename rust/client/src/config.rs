//! The one human-facing client file (`client.toml`). The probe's `mkwmp.toml`
//! is generated from this plus the granted ticket.

use serde::Deserialize;
use std::path::PathBuf;

#[derive(Debug, Clone, Deserialize)]
pub struct ClientConfig {
    /// Seat server base URL, e.g. `https://mkw.anarqz.dev`.
    pub server: String,
    /// Your in-game name: 3-16 of letters, digits, '-' and '_'.
    pub name: String,
    /// Up to 8 friend names. When a friend lists you too, you are friends in game.
    #[serde(default)]
    pub friends: Vec<String>,
    /// Game directory (contains mkw.exe).
    pub game: PathBuf,
    /// Start the game when seated. false: only hold the seat and write
    /// mkwmp.toml (start the game yourself, e.g. from Steam on SteamOS).
    #[serde(default = "yes")]
    pub launch: bool,
    /// Loopback testing only: player two sets slot = 2.
    #[serde(default = "one")]
    pub slot: u8,
}

fn yes() -> bool {
    true
}

fn one() -> u8 {
    1
}

impl ClientConfig {
    pub fn load(path: &str) -> Result<Self, String> {
        let text = std::fs::read_to_string(path).map_err(|e| format!("read {path}: {e}"))?;
        let mut cfg: ClientConfig = toml::from_str(&text).map_err(|e| format!("parse {path}: {e}"))?;
        if !cfg.server.contains("://") {
            cfg.server = format!("http://{}", cfg.server);
        }
        cfg.server = cfg.server.trim_end_matches('/').to_string();
        Ok(cfg)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn loads_friends_and_normalizes_server() {
        let p = std::env::temp_dir().join("mkwmp_client_test.toml");
        std::fs::write(&p, "server=\"10.0.0.5:7000/\"\nname=\"Ana\"\nfriends=[\"Bia\",\"Cid\"]\ngame=\"C:/game\"\n").unwrap();
        let c = ClientConfig::load(p.to_str().unwrap()).unwrap();
        assert_eq!(c.server, "http://10.0.0.5:7000");
        assert_eq!(c.friends, vec!["Bia", "Cid"]);
        assert_eq!(c.slot, 1);
        assert!(c.launch);
    }
}
