//! The one human-facing client file (`client.toml`). The probe's `mkwmp.toml`
//! is generated from this by the launcher.

use serde::Deserialize;
use std::path::PathBuf;

/// When the launcher takes a seat on the server.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum SeatMode {
    /// As soon as the launcher starts.
    Start,
    /// Only when the game goes online (Online Play), and released after the
    /// game stops asking for 90 s.
    Online,
}

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
    /// Start the game. false: only hold the seat (start the game yourself,
    /// e.g. from Steam on SteamOS).
    #[serde(default = "yes")]
    pub launch: bool,
    /// When to take a seat: "start" (default) or "online".
    #[serde(default = "start")]
    pub seat: SeatMode,
    /// Loopback port of the launcher's local API (the game calls it).
    #[serde(default = "local_port")]
    pub local_port: u16,
    /// Check GitHub Releases for a newer launcher and game patch at start.
    #[serde(default = "yes")]
    pub update: bool,
    /// Loopback testing only: player two sets slot = 2.
    #[serde(default = "one")]
    pub slot: u8,
}

fn yes() -> bool {
    true
}
fn start() -> SeatMode {
    SeatMode::Start
}
fn local_port() -> u16 {
    47820
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
    fn loads_with_defaults() {
        let p = std::env::temp_dir().join("mkwmp_client_test.toml");
        std::fs::write(&p, "server=\"10.0.0.5:7000/\"\nname=\"Ana\"\nfriends=[\"Bia\",\"Cid\"]\ngame=\"C:/game\"\n").unwrap();
        let c = ClientConfig::load(p.to_str().unwrap()).unwrap();
        assert_eq!(c.server, "http://10.0.0.5:7000");
        assert_eq!(c.friends, vec!["Bia", "Cid"]);
        assert_eq!((c.slot, c.launch, c.seat, c.local_port, c.update), (1, true, SeatMode::Start, 47820, true));
    }

    #[test]
    fn seat_online() {
        let c: ClientConfig = toml::from_str("server=\"x\"\nname=\"Ana\"\ngame=\"g\"\nseat=\"online\"\n").unwrap();
        assert_eq!(c.seat, SeatMode::Online);
    }
}
