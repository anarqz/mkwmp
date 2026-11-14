//! Registered profiles: a name belongs to whoever registered it, proven by a
//! 6-digit PIN. The only state the seat server keeps across restarts, in one
//! SQLite database (WAL, one row per profile). PINs are stored as Argon2id
//! hashes; names unused for `expire_days` become free again, except reserved
//! ones (claimed by the admin, never by the API).
//!
//! Hashing (the slow part) never runs under the database lock; callers run
//! these methods on a blocking thread with a bounded number at once (see
//! http.rs), since each hash takes 8 MiB.
//!
//! An old `registry.json` (the previous format) next to the database, or
//! configured as the file, is imported once and renamed `*.json.imported`.

use std::path::{Path, PathBuf};
use std::sync::Mutex;

use argon2::password_hash::{rand_core::OsRng as HashRng, PasswordHash, PasswordHasher, PasswordVerifier, SaltString};
use argon2::{Algorithm, Argon2, Params, Version};
use rand::Rng;
use rusqlite::{params, Connection, OptionalExtension};
use serde::Deserialize;

use crate::names;

const DAY: u64 = 86_400;
/// last_seen is rewritten at most this often per name (fewer writes).
const SEEN_GRANULARITY: u64 = DAY;

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

impl From<rusqlite::Error> for RegistryError {
    fn from(e: rusqlite::Error) -> Self {
        RegistryError::Io(e.to_string())
    }
}

pub struct Registry {
    db: Mutex<Connection>,
    path: Option<PathBuf>,
    reserved: Vec<String>, // name keys
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

/// One row, as stored.
struct Row {
    name: String,
    pin_hash: String,
    last_seen: u64,
}

#[derive(Deserialize)]
struct JsonProfile {
    name: String,
    pin_hash: String,
    created: u64,
    last_seen: u64,
}

#[derive(Deserialize)]
struct JsonFile {
    profiles: Vec<JsonProfile>,
}

const SCHEMA: &str = "
    PRAGMA journal_mode = WAL;
    PRAGMA synchronous = NORMAL;
    CREATE TABLE IF NOT EXISTS profiles (
        key       TEXT PRIMARY KEY,   -- lower-case name
        name      TEXT NOT NULL,      -- spelling as registered
        pin_hash  TEXT NOT NULL,      -- Argon2id PHC string
        created   INTEGER NOT NULL,
        last_seen INTEGER NOT NULL
    ) WITHOUT ROWID;
    CREATE INDEX IF NOT EXISTS profiles_last_seen ON profiles(last_seen);
";

impl Registry {
    /// Open (or create) the database at `path`; `None`: in memory (tests).
    /// A `.json` path means the old format: the database goes next to it
    /// (`.db`) and the JSON is imported.
    pub fn open(path: Option<&Path>, reserved: &[String], expire_days: u64) -> Result<Self, RegistryError> {
        let (db_path, json) = match path {
            None => (None, None),
            Some(p) if p.extension().is_some_and(|e| e == "json") => (Some(p.with_extension("db")), Some(p.to_path_buf())),
            Some(p) => (Some(p.to_path_buf()), Some(p.with_extension("json"))),
        };
        let conn = match &db_path {
            Some(p) => Connection::open(p).map_err(|e| RegistryError::Io(format!("{}: {e}", p.display())))?,
            None => Connection::open_in_memory()?,
        };
        conn.busy_timeout(std::time::Duration::from_secs(5))?;
        conn.execute_batch(SCHEMA)?;
        let r = Self {
            db: Mutex::new(conn),
            path: db_path,
            reserved: reserved.iter().map(|n| names::key(n)).collect(),
            expire_secs: expire_days * DAY,
        };
        if let Some(j) = json.filter(|j| j.exists()) {
            r.import_json(&j)?;
        }
        Ok(r)
    }

    pub fn path(&self) -> Option<&Path> {
        self.path.as_deref()
    }

    /// One-time import of the old JSON file; renamed afterwards so it never
    /// runs twice. Rows already in the database win.
    fn import_json(&self, file: &Path) -> Result<(), RegistryError> {
        let text = std::fs::read_to_string(file).map_err(|e| RegistryError::Io(format!("{}: {e}", file.display())))?;
        let f: JsonFile = serde_json::from_str(&text).map_err(|e| RegistryError::Io(format!("{}: {e}", file.display())))?;
        let mut db = self.db.lock().unwrap();
        let tx = db.transaction()?;
        for p in &f.profiles {
            tx.execute(
                "INSERT OR IGNORE INTO profiles (key, name, pin_hash, created, last_seen) VALUES (?1, ?2, ?3, ?4, ?5)",
                params![names::key(&p.name), p.name, p.pin_hash, p.created as i64, p.last_seen as i64],
            )?;
        }
        tx.commit()?;
        let done = file.with_extension("json.imported");
        std::fs::rename(file, &done).map_err(|e| RegistryError::Io(format!("{}: {e}", file.display())))?;
        eprintln!("registry: imported {} profiles from {} (now {})", f.profiles.len(), file.display(), done.display());
        Ok(())
    }

    pub fn is_reserved(&self, name: &str) -> bool {
        self.reserved.contains(&names::key(name))
    }

    fn expired_at(&self, key: &str, last_seen: u64, now: u64) -> bool {
        self.expire_secs > 0 && !self.reserved.iter().any(|r| r == key) && now.saturating_sub(last_seen) > self.expire_secs
    }

    fn row(&self, key: &str) -> Result<Option<Row>, RegistryError> {
        let db = self.db.lock().unwrap();
        Ok(db
            .query_row("SELECT name, pin_hash, last_seen FROM profiles WHERE key = ?1", [key], |r| {
                Ok(Row { name: r.get(0)?, pin_hash: r.get(1)?, last_seen: r.get::<_, i64>(2)? as u64 })
            })
            .optional()?)
    }

    /// The live (unexpired) row for `name`.
    fn live(&self, name: &str, now: u64) -> Result<Option<Row>, RegistryError> {
        let key = names::key(name);
        Ok(self.row(&key)?.filter(|r| !self.expired_at(&key, r.last_seen, now)))
    }

    pub fn exists(&self, name: &str, now: u64) -> bool {
        self.live(name, now).ok().flatten().is_some()
    }

    /// Register a free name; returns its PIN (shown once). `admin` may take
    /// reserved names.
    pub fn register(&self, name: &str, now: u64, admin: bool) -> Result<String, RegistryError> {
        names::validate(name).map_err(|e| RegistryError::Invalid(e.to_string()))?;
        if self.is_reserved(name) && !admin {
            return Err(RegistryError::Reserved);
        }
        if self.exists(name, now) {
            return Err(RegistryError::Taken);
        }
        let pin = new_pin();
        let hash = hash_pin(&pin); // outside the lock
        let key = names::key(name);
        let db = self.db.lock().unwrap();
        // Insert, or take over an expired row; a row that went live meanwhile
        // (another registration won the race) stays.
        let cutoff = if self.expire_secs > 0 && !self.is_reserved(name) {
            now.saturating_sub(self.expire_secs) as i64
        } else {
            i64::MIN
        };
        let n = db.execute(
            "INSERT INTO profiles (key, name, pin_hash, created, last_seen) VALUES (?1, ?2, ?3, ?4, ?4)
             ON CONFLICT(key) DO UPDATE SET name = excluded.name, pin_hash = excluded.pin_hash,
                 created = excluded.created, last_seen = excluded.last_seen
             WHERE profiles.last_seen < ?5",
            params![key, name, hash, now as i64, cutoff],
        )?;
        if n == 0 {
            return Err(RegistryError::Taken);
        }
        Ok(pin)
    }

    /// Check a PIN. On success returns the registered spelling and marks the
    /// profile as used.
    pub fn login(&self, name: &str, pin: &str, now: u64) -> Result<String, RegistryError> {
        let row = self.live(name, now)?.ok_or(RegistryError::NotFound)?;
        if !valid_pin(pin) || !pin_ok(pin, &row.pin_hash) {
            return Err(RegistryError::WrongPin);
        }
        if now.saturating_sub(row.last_seen) >= SEEN_GRANULARITY {
            let db = self.db.lock().unwrap();
            db.execute("UPDATE profiles SET last_seen = ?2 WHERE key = ?1", params![names::key(name), now as i64])?;
        }
        Ok(row.name)
    }

    /// The owner deletes their profile: the name becomes free.
    pub fn delete(&self, name: &str, pin: &str, now: u64) -> Result<(), RegistryError> {
        let row = self.live(name, now)?.ok_or(RegistryError::NotFound)?;
        if !valid_pin(pin) || !pin_ok(pin, &row.pin_hash) {
            return Err(RegistryError::WrongPin);
        }
        // Only the row whose PIN was checked (not a re-registration since).
        let db = self.db.lock().unwrap();
        db.execute("DELETE FROM profiles WHERE key = ?1 AND pin_hash = ?2", params![names::key(name), row.pin_hash])?;
        Ok(())
    }

    /// Admin: a new PIN for an existing name.
    pub fn reset_pin(&self, name: &str) -> Result<String, RegistryError> {
        if self.row(&names::key(name))?.is_none() {
            return Err(RegistryError::NotFound);
        }
        let pin = new_pin();
        let hash = hash_pin(&pin);
        let db = self.db.lock().unwrap();
        match db.execute("UPDATE profiles SET pin_hash = ?2 WHERE key = ?1", params![names::key(name), hash])? {
            0 => Err(RegistryError::NotFound),
            _ => Ok(pin),
        }
    }

    /// Admin: free a name.
    pub fn release(&self, name: &str) -> Result<(), RegistryError> {
        let db = self.db.lock().unwrap();
        match db.execute("DELETE FROM profiles WHERE key = ?1", [names::key(name)])? {
            0 => Err(RegistryError::NotFound),
            _ => Ok(()),
        }
    }

    /// Drop expired profiles. Returns how many.
    pub fn sweep(&self, now: u64) -> usize {
        if self.expire_secs == 0 {
            return 0;
        }
        let cutoff = now.saturating_sub(self.expire_secs) as i64;
        let db = self.db.lock().unwrap();
        let mut n = 0;
        let keys: Vec<String> = db
            .prepare("SELECT key FROM profiles WHERE last_seen < ?1")
            .and_then(|mut st| st.query_map([cutoff], |r| r.get(0))?.collect())
            .unwrap_or_default();
        for k in keys.iter().filter(|k| !self.reserved.contains(k)) {
            n += db.execute("DELETE FROM profiles WHERE key = ?1 AND last_seen < ?2", params![k, cutoff]).unwrap_or(0);
        }
        n
    }

    pub fn count(&self) -> usize {
        let db = self.db.lock().unwrap();
        db.query_row("SELECT COUNT(*) FROM profiles", [], |r| r.get::<_, i64>(0)).unwrap_or(0) as usize
    }

    pub fn list(&self) -> Vec<(String, u64, u64, bool)> {
        let db = self.db.lock().unwrap();
        let rows: Vec<(String, u64, u64)> = db
            .prepare("SELECT name, created, last_seen FROM profiles ORDER BY key")
            .and_then(|mut st| {
                st.query_map([], |r| Ok((r.get(0)?, r.get::<_, i64>(1)? as u64, r.get::<_, i64>(2)? as u64)))?.collect()
            })
            .unwrap_or_default();
        rows.into_iter().map(|(n, c, l)| (n.clone(), c, l, self.is_reserved(&n))).collect()
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
        let r = reg();
        let pin = r.register("Ana", 0, false).unwrap();
        assert!(valid_pin(&pin));
        assert_eq!(r.login("ana", &pin, 10).unwrap(), "Ana");
        let wrong = if pin == "000000" { "111111" } else { "000000" };
        assert_eq!(r.login("ANA", wrong, 10), Err(RegistryError::WrongPin));
        assert_eq!(r.login("bia", &pin, 10), Err(RegistryError::NotFound));
        assert_eq!(r.register("ANA", 10, false), Err(RegistryError::Taken));
    }

    #[test]
    fn owner_deletes_with_the_pin() {
        let r = reg();
        let pin = r.register("Ana", 0, false).unwrap();
        let wrong = if pin == "000000" { "111111" } else { "000000" };
        assert_eq!(r.delete("ana", wrong, 1), Err(RegistryError::WrongPin));
        r.delete("ANA", &pin, 1).unwrap();
        assert!(!r.exists("ana", 1));
        assert!(r.register("ana", 2, false).is_ok());
    }

    #[test]
    fn reserved_names_need_the_admin() {
        let r = reg();
        assert_eq!(r.register("Anarqz", 0, false), Err(RegistryError::Reserved));
        let pin = r.register("anarqz", 0, true).unwrap();
        assert_eq!(r.login("anarqz", &pin, 0).unwrap(), "anarqz");
    }

    #[test]
    fn unused_names_expire_but_reserved_do_not() {
        let r = reg();
        let pin = r.register("Ana", 0, false).unwrap();
        r.register("anarqz", 0, true).unwrap();
        let later = 181 * DAY;
        assert_eq!(r.login("ana", &pin, later), Err(RegistryError::NotFound));
        assert!(r.register("ana", later, false).is_ok(), "an expired name can be taken over");
        assert_eq!(r.sweep(later), 0); // the new "ana" is fresh; anarqz is reserved
        assert!(r.exists("anarqz", later * 3));
        assert_eq!(r.sweep(later * 3), 1);
        assert_eq!(r.count(), 1);
    }

    #[test]
    fn using_a_name_keeps_it() {
        let r = reg();
        let pin = r.register("Ana", 0, false).unwrap();
        r.login("ana", &pin, 170 * DAY).unwrap();
        assert!(r.login("ana", &pin, 300 * DAY).is_ok());
    }

    fn temp(name: &str) -> PathBuf {
        let d = std::env::temp_dir().join(format!("mkwmp-registry-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    #[test]
    fn persists_hashes_not_pins() {
        let dir = temp("persist");
        let path = dir.join("registry.db");
        let pin = Registry::open(Some(&path), &[], 180).unwrap().register("Ana", 0, false).unwrap();
        let raw = std::fs::read(&path).unwrap_or_default();
        let wal = std::fs::read(path.with_extension("db-wal")).unwrap_or_default();
        let all = String::from_utf8_lossy(&[raw, wal].concat()).into_owned();
        assert!(all.contains("$argon2id$") && !all.contains(&pin));
        let r = Registry::open(Some(&path), &[], 180).unwrap();
        assert_eq!(r.login("ana", &pin, 1).unwrap(), "Ana");
        let new = r.reset_pin("ana").unwrap();
        assert!(r.login("ana", &new, 1).is_ok());
        r.release("ana").unwrap();
        assert!(!Registry::open(Some(&path), &[], 180).unwrap().exists("ana", 1));
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn imports_the_old_json_once() {
        let dir = temp("import");
        let json = dir.join("registry.json");
        let hash = hash_pin("123456");
        std::fs::write(
            &json,
            serde_json::json!({"profiles": [{"name": "Ana", "pin_hash": hash, "created": 5, "last_seen": 7}]}).to_string(),
        )
        .unwrap();
        // The config still names the .json: the database goes next to it.
        let r = Registry::open(Some(&json), &[], 180).unwrap();
        assert_eq!(r.path(), Some(dir.join("registry.db").as_path()));
        assert_eq!(r.login("ana", "123456", 8).unwrap(), "Ana");
        assert!(!json.exists() && dir.join("registry.json.imported").exists());
        drop(r);
        let r = Registry::open(Some(&dir.join("registry.db")), &[], 180).unwrap();
        assert_eq!(r.list(), vec![("Ana".to_string(), 5, 7, false)]);
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn concurrent_registrations_of_one_name_have_one_winner() {
        let r = std::sync::Arc::new(reg());
        let wins: usize = (0..8)
            .map(|_| {
                let r = r.clone();
                std::thread::spawn(move || r.register("Race", 0, false).is_ok() as usize)
            })
            .collect::<Vec<_>>()
            .into_iter()
            .map(|h| h.join().unwrap())
            .sum();
        assert_eq!(wins, 1);
    }
}
