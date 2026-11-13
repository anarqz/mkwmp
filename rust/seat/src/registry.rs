//! Registered profiles: a name belongs to whoever registered it, proven by a
//! 6-digit PIN. The only state the seat server keeps across restarts, in one
//! JSON file (written atomically on every change). PINs are stored as
//! Argon2id hashes; names unused for `expire_days` become free again, except
//! reserved ones (claimed by the admin, never by the API).

use std::collections::HashMap;
use std::path::{Path, PathBuf};

use argon2::password_hash::{rand_core::OsRng as HashRng, PasswordHash, PasswordHasher, PasswordVerifier, SaltString};
use argon2::{Algorithm, Argon2, Params, Version};
use rand::Rng;
use serde::{Deserialize, Serialize};

use crate::names;

const DAY: u64 = 86_400;
/// last_seen is rewritten at most this often per name (fewer file writes).
const SEEN_GRANULARITY: u64 = DAY;

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct Profile {
    /// Display spelling, as registered.
    pub name: String,
    /// Argon2id PHC string of the PIN.
    pub pin_hash: String,
    pub created: u64,
    pub last_seen: u64,
}

#[derive(Debug, Default, Serialize, Deserialize)]
struct File {
    profiles: Vec<Profile>,
}

#[derive(Debug, PartialEq, Eq)]
pub enum RegistryError {
    Invalid(String),
    Taken,
    Reserved,
    NotFound,
    WrongPin,
    Io(String),
}

impl std::fmt::Display for RegistryError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            RegistryError::Invalid(m) => f.write_str(m),
            RegistryError::Taken => f.write_str("that name is already registered"),
            RegistryError::Reserved => f.write_str("that name is reserved"),
            RegistryError::NotFound => f.write_str("that name is not registered yet"),
            RegistryError::WrongPin => f.write_str("wrong PIN for that name"),
            RegistryError::Io(m) => write!(f, "registry: {m}"),
        }
    }
}

pub struct Registry {
    path: Option<PathBuf>,
    profiles: HashMap<String, Profile>, // name key -> profile
    reserved: Vec<String>,              // name keys
    expire_secs: u64,
}

fn hasher() -> Argon2<'static> {
    // ~8 MiB, 2 passes: slow enough for a leaked file, cheap on a 1 GB VM.
    Argon2::new(Algorithm::Argon2id, Version::V0x13, Params::new(8 * 1024, 2, 1, None).expect("argon2 params"))
}

pub fn new_pin() -> String {
    format!("{:06}", rand::rngs::OsRng.gen_range(0..1_000_000u32))
}

fn hash_pin(pin: &str) -> String {
    let salt = SaltString::generate(&mut HashRng);
    hasher().hash_password(pin.as_bytes(), &salt).expect("argon2 hash").to_string()
}

fn pin_ok(pin: &str, hash: &str) -> bool {
    PasswordHash::new(hash).is_ok_and(|h| hasher().verify_password(pin.as_bytes(), &h).is_ok())
}

pub fn valid_pin(pin: &str) -> bool {
    pin.len() == 6 && pin.bytes().all(|b| b.is_ascii_digit())
}

impl Registry {
    /// Load `path` (missing: empty). `path = None`: in memory only (tests).
    pub fn open(path: Option<&Path>, reserved: &[String], expire_days: u64) -> Result<Self, RegistryError> {
        let mut profiles = HashMap::new();
        if let Some(p) = path {
            if p.exists() {
                let text = std::fs::read_to_string(p).map_err(|e| RegistryError::Io(format!("{}: {e}", p.display())))?;
                let f: File = serde_json::from_str(&text).map_err(|e| RegistryError::Io(format!("{}: {e}", p.display())))?;
                for pr in f.profiles {
                    profiles.insert(names::key(&pr.name), pr);
                }
            }
        }
        Ok(Self {
            path: path.map(Path::to_path_buf),
            profiles,
            reserved: reserved.iter().map(|n| names::key(n)).collect(),
            expire_secs: expire_days * DAY,
        })
    }

    fn save(&self) -> Result<(), RegistryError> {
        let Some(path) = &self.path else { return Ok(()) };
        let mut list: Vec<&Profile> = self.profiles.values().collect();
        list.sort_by_key(|p| names::key(&p.name));
        let text = serde_json::to_string_pretty(&serde_json::json!({ "profiles": list })).expect("json");
        let tmp = path.with_extension("tmp");
        std::fs::write(&tmp, text).map_err(|e| RegistryError::Io(e.to_string()))?;
        std::fs::rename(&tmp, path).map_err(|e| RegistryError::Io(e.to_string()))
    }

    pub fn is_reserved(&self, name: &str) -> bool {
        self.reserved.contains(&names::key(name))
    }

    pub fn exists(&self, name: &str, now: u64) -> bool {
        self.profiles.get(&names::key(name)).is_some_and(|p| !self.expired(p, now))
    }

    fn expired(&self, p: &Profile, now: u64) -> bool {
        self.expire_secs > 0 && !self.is_reserved(&p.name) && now.saturating_sub(p.last_seen) > self.expire_secs
    }

    /// Register a free name; returns its PIN (shown once). `admin` may take
    /// reserved names.
    pub fn register(&mut self, name: &str, now: u64, admin: bool) -> Result<String, RegistryError> {
        names::validate(name).map_err(|e| RegistryError::Invalid(e.to_string()))?;
        if self.is_reserved(name) && !admin {
            return Err(RegistryError::Reserved);
        }
        if self.exists(name, now) {
            return Err(RegistryError::Taken);
        }
        let pin = new_pin();
        let p = Profile { name: name.to_string(), pin_hash: hash_pin(&pin), created: now, last_seen: now };
        self.profiles.insert(names::key(name), p);
        self.save()?;
        Ok(pin)
    }

    /// Check a PIN. On success returns the registered spelling and marks the
    /// profile as used.
    pub fn login(&mut self, name: &str, pin: &str, now: u64) -> Result<String, RegistryError> {
        let key = names::key(name);
        let p = match self.profiles.get(&key) {
            Some(p) if !self.expired(p, now) => p,
            _ => return Err(RegistryError::NotFound),
        };
        if !valid_pin(pin) || !pin_ok(pin, &p.pin_hash) {
            return Err(RegistryError::WrongPin);
        }
        let display = p.name.clone();
        if now.saturating_sub(p.last_seen) >= SEEN_GRANULARITY {
            self.profiles.get_mut(&key).expect("profile").last_seen = now;
            self.save()?;
        }
        Ok(display)
    }

    /// Admin: a new PIN for an existing name.
    pub fn reset_pin(&mut self, name: &str) -> Result<String, RegistryError> {
        let p = self.profiles.get_mut(&names::key(name)).ok_or(RegistryError::NotFound)?;
        let pin = new_pin();
        p.pin_hash = hash_pin(&pin);
        self.save()?;
        Ok(pin)
    }

    /// Admin: free a name.
    pub fn release(&mut self, name: &str) -> Result<(), RegistryError> {
        self.profiles.remove(&names::key(name)).ok_or(RegistryError::NotFound)?;
        self.save()
    }

    /// Drop expired profiles. Returns how many.
    pub fn sweep(&mut self, now: u64) -> usize {
        let before = self.profiles.len();
        let expired: Vec<String> =
            self.profiles.iter().filter(|(_, p)| self.expired(p, now)).map(|(k, _)| k.clone()).collect();
        for k in expired {
            self.profiles.remove(&k);
        }
        let n = before - self.profiles.len();
        if n > 0 {
            let _ = self.save();
        }
        n
    }

    pub fn list(&self) -> Vec<(String, u64, u64, bool)> {
        let mut v: Vec<_> = self
            .profiles
            .values()
            .map(|p| (p.name.clone(), p.created, p.last_seen, self.is_reserved(&p.name)))
            .collect();
        v.sort();
        v
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn reg() -> Registry {
        Registry::open(None, &["anarqz".into()], 180).unwrap()
    }

    #[test]
    fn register_then_login_with_pin() {
        let mut r = reg();
        let pin = r.register("Ana", 0, false).unwrap();
        assert!(valid_pin(&pin));
        assert_eq!(r.login("ana", &pin, 10).unwrap(), "Ana");
        let wrong = if pin == "000000" { "111111" } else { "000000" };
        assert_eq!(r.login("ANA", wrong, 10), Err(RegistryError::WrongPin));
        assert_eq!(r.login("bia", &pin, 10), Err(RegistryError::NotFound));
        assert_eq!(r.register("ANA", 10, false), Err(RegistryError::Taken));
    }

    #[test]
    fn reserved_names_need_the_admin() {
        let mut r = reg();
        assert_eq!(r.register("Anarqz", 0, false), Err(RegistryError::Reserved));
        let pin = r.register("anarqz", 0, true).unwrap();
        assert_eq!(r.login("anarqz", &pin, 0).unwrap(), "anarqz");
    }

    #[test]
    fn unused_names_expire_but_reserved_do_not() {
        let mut r = reg();
        let pin = r.register("Ana", 0, false).unwrap();
        r.register("anarqz", 0, true).unwrap();
        let later = 181 * DAY;
        assert_eq!(r.login("ana", &pin, later), Err(RegistryError::NotFound));
        assert!(r.register("ana", later, false).is_ok());
        assert_eq!(r.sweep(later), 0); // the new "ana" is fresh; anarqz is reserved
        assert!(r.exists("anarqz", later * 3));
    }

    #[test]
    fn using_a_name_keeps_it() {
        let mut r = reg();
        let pin = r.register("Ana", 0, false).unwrap();
        r.login("ana", &pin, 170 * DAY).unwrap();
        assert!(r.login("ana", &pin, 300 * DAY).is_ok());
    }

    #[test]
    fn persists_hashes_not_pins() {
        let path = std::env::temp_dir().join("mkwmp_registry_test.json");
        let _ = std::fs::remove_file(&path);
        let pin = {
            let mut r = Registry::open(Some(&path), &[], 180).unwrap();
            r.register("Ana", 0, false).unwrap()
        };
        let text = std::fs::read_to_string(&path).unwrap();
        assert!(!text.contains(&format!("\"{pin}\"")) && text.contains("$argon2id$"));
        let mut r = Registry::open(Some(&path), &[], 180).unwrap();
        assert_eq!(r.login("ana", &pin, 1).unwrap(), "Ana");
        let new = r.reset_pin("ana").unwrap();
        assert!(r.login("ana", &new, 1).is_ok());
        r.release("ana").unwrap();
        assert!(!Registry::open(Some(&path), &[], 180).unwrap().exists("ana", 1));
    }
}
