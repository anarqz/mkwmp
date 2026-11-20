//! The server's privacy policy: its text (`GET /privacy`), its version (the SHA-256 of the text) and
//! whether consent to the current version gates seats, friends and reports.

use sha2::{Digest, Sha256};

/// The policy served when the config names no file.
pub const DEFAULT_TEXT: &str = include_str!("privacy-default.md");

#[derive(Debug, Clone)]
pub struct Policy {
    pub text: String,
    /// Lower-case hex SHA-256 of `text`'s bytes.
    pub version: String,
    pub enforce: bool,
}

impl Policy {
    pub fn new(text: impl Into<String>, enforce: bool) -> Self {
        let text = text.into();
        let version = Sha256::digest(text.as_bytes()).iter().map(|b| format!("{b:02x}")).collect();
        Self { text, version, enforce }
    }

    /// From the config: the file if one is set (it must be readable and not empty), else the default.
    pub fn load(cfg: &config::PrivacyCfg) -> Result<Self, String> {
        let text = match &cfg.file {
            Some(p) => std::fs::read_to_string(p).map_err(|e| format!("reading {}: {e}", p.display()))?,
            None => DEFAULT_TEXT.to_string(),
        };
        if text.trim().is_empty() {
            return Err("the [privacy] file is empty".into());
        }
        Ok(Self::new(text, cfg.enforce))
    }
}

impl Default for Policy {
    fn default() -> Self {
        Self::new(DEFAULT_TEXT, false)
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn version_is_the_sha256_of_the_text() {
        let p = super::Policy::new("abc", false);
        assert_eq!(p.version, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }
}
