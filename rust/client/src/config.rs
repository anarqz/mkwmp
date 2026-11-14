//! The one human-facing client file (`client.toml`). The probe's `mkwmp.toml`
//! is generated from this by the launcher.

use serde::{Deserialize, Serialize};
use std::path::PathBuf;

/// When the launcher takes a seat on the server.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum SeatMode {
    /// As soon as the launcher starts.
    Start,
    /// Only when the game goes online (Online Play), and released after the
    /// game stops asking for 90 s.
    Online,
}

#[derive(Debug, Clone, PartialEq, Deserialize, Serialize)]
pub struct ClientConfig {
    /// Seat server base URL, e.g. `https://mkw.anarqz.dev`.
    pub server: String,
    /// Your in-game name: 3-16 of letters, digits, '-' and '_'.
    pub name: String,
    /// The 6-digit PIN of your registered name (given once, at registration).
    #[serde(default)]
    pub pin: String,
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
    /// How to start the game when `launch` is on. Empty: `mkw.exe --play`.
    /// Steam Deck (the launcher is the Steam shortcut): a script that runs the
    /// game through Proton, e.g. "~/mkwmp/run-game.sh".
    #[serde(default)]
    pub launch_command: String,
    /// Tray, notifications and start-up behaviour (`[background]`).
    #[serde(default)]
    pub background: Background,
    /// Voice chat (`[voice]`, docs/contracts/VOICE_API.md §4).
    #[serde(default)]
    pub voice: Voice,
}

/// Lobby voice chat. The window edits the switches, devices and volumes; the
/// game (through the local API) reports its devices and the players muted from
/// its menus.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Voice {
    #[serde(default = "yes")]
    pub enabled: bool,
    /// Microphone device id as the game sees it; empty = system default.
    #[serde(default)]
    pub input: String,
    /// Output device id; empty = system default.
    #[serde(default)]
    pub output: String,
    /// Mic volume, 1-100.
    #[serde(default = "hundred")]
    pub input_gain: u8,
    /// Voice volume, 1-100.
    #[serde(default = "hundred")]
    pub output_gain: u8,
    /// Players muted from the game's menus, by account id.
    #[serde(default)]
    pub mutes: Vec<VoiceMute>,
    /// The devices the game last reported (a native launcher on the Deck cannot
    /// see Wine's).
    #[serde(default)]
    pub inputs: Vec<VoiceDevice>,
    #[serde(default)]
    pub outputs: Vec<VoiceDevice>,
}

impl Default for Voice {
    fn default() -> Self {
        Self {
            enabled: true,
            input: String::new(),
            output: String::new(),
            input_gain: 100,
            output_gain: 100,
            mutes: vec![],
            inputs: vec![],
            outputs: vec![],
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct VoiceMute {
    /// The account id (u64), decimal.
    pub account: String,
    pub name: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct VoiceDevice {
    pub id: String,
    pub name: String,
}

fn hundred() -> u8 {
    100
}

/// Running in the background: closing the window keeps the launcher in the
/// system tray (friends online, notifications); it never holds a seat by itself.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Background {
    /// Closing the window hides it to the tray instead of quitting.
    #[serde(default = "yes")]
    pub close_to_tray: bool,
    /// Start with the system, straight into the tray.
    #[serde(default)]
    pub start_with_system: bool,
    /// System notifications: a friend comes online / starts playing / asks to
    /// be your friend.
    #[serde(default = "yes")]
    pub notify_online: bool,
    #[serde(default = "yes")]
    pub notify_playing: bool,
    #[serde(default = "yes")]
    pub notify_requests: bool,
    /// The "still running in the tray" hint was shown once.
    #[serde(default)]
    pub tray_hint_shown: bool,
}

impl Default for Background {
    fn default() -> Self {
        Self {
            close_to_tray: true,
            start_with_system: false,
            notify_online: true,
            notify_playing: true,
            notify_requests: true,
            tray_hint_shown: false,
        }
    }
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

pub const OFFICIAL_SERVER: &str = "https://mkw.anarqz.dev";

/// SteamOS (Steam Deck and others): the game starts from Steam.
pub fn is_steamos() -> bool {
    std::fs::read_to_string("/etc/os-release").is_ok_and(|s| s.contains("ID=steamos"))
}

impl Default for ClientConfig {
    fn default() -> Self {
        // A launcher dropped into the game folder finds mkw.exe next to it.
        let here = std::env::current_exe().ok().and_then(|e| e.parent().map(PathBuf::from)).unwrap_or_default();
        let game = if here.join("mkw.exe").exists() { here } else { PathBuf::new() };
        Self {
            server: OFFICIAL_SERVER.into(),
            name: String::new(),
            pin: String::new(),
            game,
            launch: !is_steamos(),
            seat: SeatMode::Start,
            local_port: local_port(),
            update: true,
            slot: 1,
            launch_command: String::new(),
            background: Background::default(),
            voice: Voice::default(),
        }
    }
}

/// The same name rules as the server: 3-16 of [A-Za-z0-9_-].
pub fn valid_name(n: &str) -> bool {
    (3..=16).contains(&n.len()) && n.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_')
}

impl ClientConfig {
    /// Load, or the defaults when the file does not exist yet.
    pub fn load_or_default(path: &str) -> Result<Self, String> {
        if std::path::Path::new(path).exists() {
            Self::load(path)
        } else {
            Ok(Self::default())
        }
    }

    /// Write the file (the window's edits). Comments are not kept.
    pub fn save(&self, path: &str) -> Result<(), String> {
        let body = toml::to_string(self).map_err(|e| e.to_string())?;
        let text = format!("# mkwmp launcher settings (edited by the launcher window).\n# Guide: https://anarqz.github.io/mkwmp/\n\n{body}");
        let tmp = format!("{path}.tmp");
        std::fs::write(&tmp, text).map_err(|e| format!("write {path}: {e}"))?;
        std::fs::rename(&tmp, path).map_err(|e| format!("write {path}: {e}"))
    }

    /// What is missing before PLAY can work.
    pub fn problem(&self) -> Option<String> {
        if !valid_name(&self.name) {
            return Some("Pick a gamertag: 3-16 letters, digits, - or _ (no spaces)".into());
        }
        if !(self.pin.len() == 6 && self.pin.bytes().all(|b| b.is_ascii_digit())) {
            return Some("Register your gamertag, or enter its 6-digit PIN".into());
        }
        if self.server.trim().is_empty() {
            return Some("Set the server URL".into());
        }
        if !self.game.join("mkw.exe").exists() {
            return Some("Set the game folder (the one with mkw.exe)".into());
        }
        None
    }

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
        // An old `friends = [...]` line (friends now live on the server) still loads.
        assert_eq!((c.slot, c.launch, c.seat, c.local_port, c.update), (1, true, SeatMode::Start, 47820, true));
    }

    #[test]
    fn save_round_trips() {
        let p = std::env::temp_dir().join("mkwmp_client_save.toml");
        let c = ClientConfig { name: "Ana".into(), ..Default::default() };
        c.save(p.to_str().unwrap()).unwrap();
        assert_eq!(ClientConfig::load(p.to_str().unwrap()).unwrap(), c);
    }

    #[test]
    fn voice_defaults_and_round_trip() {
        let c: ClientConfig = toml::from_str("server=\"x\"\nname=\"Ana\"\ngame=\"g\"\n").unwrap();
        assert_eq!(c.voice, Voice::default());
        assert!(c.voice.enabled && c.voice.input_gain == 100 && c.voice.output_gain == 100);
        let p = std::env::temp_dir().join("mkwmp_client_voice.toml");
        let mut c = ClientConfig { name: "Ana".into(), ..Default::default() };
        c.voice.input = "{0.0.1.00000000}.{abc}".into();
        c.voice.mutes.push(VoiceMute { account: "123".into(), name: "Bia".into() });
        c.voice.inputs.push(VoiceDevice { id: "a".into(), name: "Mic".into() });
        c.save(p.to_str().unwrap()).unwrap();
        assert_eq!(ClientConfig::load(p.to_str().unwrap()).unwrap(), c);
    }

    #[test]
    fn names() {
        assert!(valid_name("Ana_1-x") && !valid_name("ab") && !valid_name("a b c"));
    }

    #[test]
    fn seat_online() {
        let c: ClientConfig = toml::from_str("server=\"x\"\nname=\"Ana\"\ngame=\"g\"\nseat=\"online\"\n").unwrap();
        assert_eq!(c.seat, SeatMode::Online);
    }
}
