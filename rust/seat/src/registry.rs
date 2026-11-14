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
//!
//! Friendships live here too, one row per pair: `pending` (a request from
//! `requester` to `addressee`) or `accepted` (mutual). Every way a profile
//! goes away (owner delete, admin release, expiry sweep, takeover of an
//! expired name) deletes its row, and the foreign keys cascade its
//! friendships and requests with it.

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
    /// A friend request or answer that can't be done: (code, message).
    Friend(&'static str, String),
    Io(String),
}

impl std::fmt::Display for RegistryError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            RegistryError::Invalid(m) => f.write_str(m),
            RegistryError::Taken => f.write_str("that gametag is already registered"),
            RegistryError::Reserved => f.write_str("that gametag is reserved"),
            RegistryError::NotFound => f.write_str("that gametag is not registered yet"),
            RegistryError::WrongPin => f.write_str("wrong PIN for that gametag"),
            RegistryError::Friend(_, m) => f.write_str(m),
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
    CREATE TABLE IF NOT EXISTS friendships (
        requester TEXT NOT NULL REFERENCES profiles(key) ON DELETE CASCADE,
        addressee TEXT NOT NULL REFERENCES profiles(key) ON DELETE CASCADE,
        status    TEXT NOT NULL CHECK (status IN ('pending', 'accepted')),
        created   INTEGER NOT NULL,
        updated   INTEGER NOT NULL,
        PRIMARY KEY (requester, addressee)
    ) WITHOUT ROWID;
    CREATE INDEX IF NOT EXISTS friendships_addressee ON friendships(addressee);
";

/// Accepted friends per profile.
pub const MAX_FRIENDS: usize = 100;
/// Requests a profile may have waiting for an answer.
pub const MAX_OUTGOING: usize = 50;

/// What a request did.
#[derive(Debug, PartialEq, Eq)]
pub enum Requested {
    /// Waiting for them to accept.
    Pending,
    /// They had asked you already: you're friends now.
    Accepted,
}

/// One profile's friends and requests (display spellings).
#[derive(Debug, Default, PartialEq, Eq)]
pub struct FriendLists {
    pub friends: Vec<String>,
    /// (name, since) asking you.
    pub incoming: Vec<(String, u64)>,
    /// (name, since) you asked.
    pub outgoing: Vec<(String, u64)>,
}

fn friend_err(code: &'static str, msg: impl Into<String>) -> RegistryError {
    RegistryError::Friend(code, msg.into())
}

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
        // Per connection, and a no-op inside a transaction: set it first.
        conn.execute_batch("PRAGMA foreign_keys = ON;")?;
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
        let mut db = self.db.lock().unwrap();
        let tx = db.transaction()?;
        // Take over an expired row: delete it first, so its friendships and
        // requests go with it (an in-place update wouldn't cascade).
        if self.expire_secs > 0 && !self.is_reserved(name) {
            let cutoff = now.saturating_sub(self.expire_secs) as i64;
            tx.execute("DELETE FROM profiles WHERE key = ?1 AND last_seen < ?2", params![key, cutoff])?;
        }
        // A row that's still here is live: another registration won the race.
        let n = tx.execute(
            "INSERT OR IGNORE INTO profiles (key, name, pin_hash, created, last_seen) VALUES (?1, ?2, ?3, ?4, ?4)",
            params![key, name, hash, now as i64],
        )?;
        if n == 0 {
            return Err(RegistryError::Taken);
        }
        tx.commit()?;
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

impl Registry {
    /// The display name of a live profile, with the lock held.
    fn live_name(&self, db: &Connection, key: &str, now: u64) -> Result<Option<String>, RegistryError> {
        let row: Option<(String, i64)> = db
            .query_row("SELECT name, last_seen FROM profiles WHERE key = ?1", [key], |r| Ok((r.get(0)?, r.get(1)?)))
            .optional()?;
        Ok(row.filter(|(_, seen)| !self.expired_at(key, *seen as u64, now)).map(|(n, _)| n))
    }

    fn count_where(db: &Connection, sql: &str, key: &str) -> Result<usize, RegistryError> {
        Ok(db.query_row(sql, [key], |r| r.get::<_, i64>(0))? as usize)
    }

    fn accepted_count(db: &Connection, key: &str) -> Result<usize, RegistryError> {
        Self::count_where(
            db,
            "SELECT COUNT(*) FROM friendships WHERE status = 'accepted' AND (requester = ?1 OR addressee = ?1)",
            key,
        )
    }

    /// `me` asks `other` to be friends. If `other` already asked `me`, that
    /// request is accepted instead. Returns the other's display name too.
    pub fn request_friend(&self, me: &str, other: &str, now: u64) -> Result<(Requested, String), RegistryError> {
        let (mk, ok) = (names::key(me), names::key(other));
        if mk == ok {
            return Err(friend_err("FRIEND_SELF", "that's your own gametag"));
        }
        let mut db = self.db.lock().unwrap();
        let tx = db.transaction()?;
        self.live_name(&tx, &mk, now)?.ok_or(RegistryError::NotFound)?;
        let other_name = self
            .live_name(&tx, &ok, now)?
            .ok_or_else(|| friend_err("FRIEND_UNKNOWN", format!("no player has the gametag {other}")))?;
        let existing: Option<(String, String)> = tx
            .query_row(
                "SELECT requester, status FROM friendships
                 WHERE (requester = ?1 AND addressee = ?2) OR (requester = ?2 AND addressee = ?1)",
                params![mk, ok],
                |r| Ok((r.get(0)?, r.get(1)?)),
            )
            .optional()?;
        let outcome = match existing {
            Some((_, status)) if status == "accepted" => {
                return Err(friend_err("FRIEND_EXISTS", format!("you and {other_name} are friends already")))
            }
            Some((requester, _)) if requester == mk => {
                return Err(friend_err("FRIEND_PENDING", format!("you asked {other_name} already")))
            }
            Some(_) => {
                // They asked first: accept.
                if Self::accepted_count(&tx, &mk)? >= MAX_FRIENDS || Self::accepted_count(&tx, &ok)? >= MAX_FRIENDS {
                    return Err(friend_err("FRIEND_LIMIT", format!("at most {MAX_FRIENDS} friends")));
                }
                tx.execute(
                    "UPDATE friendships SET status = 'accepted', updated = ?3 WHERE requester = ?2 AND addressee = ?1",
                    params![mk, ok, now as i64],
                )?;
                Requested::Accepted
            }
            None => {
                if Self::accepted_count(&tx, &mk)? >= MAX_FRIENDS {
                    return Err(friend_err("FRIEND_LIMIT", format!("at most {MAX_FRIENDS} friends")));
                }
                let out = Self::count_where(
                    &tx,
                    "SELECT COUNT(*) FROM friendships WHERE status = 'pending' AND requester = ?1",
                    &mk,
                )?;
                if out >= MAX_OUTGOING {
                    return Err(friend_err("FRIEND_LIMIT", format!("at most {MAX_OUTGOING} requests waiting")));
                }
                tx.execute(
                    "INSERT INTO friendships (requester, addressee, status, created, updated) VALUES (?1, ?2, 'pending', ?3, ?3)",
                    params![mk, ok, now as i64],
                )?;
                Requested::Pending
            }
        };
        tx.commit()?;
        Ok((outcome, other_name))
    }

    /// `me` accepts the request `other` sent.
    pub fn accept_friend(&self, me: &str, other: &str, now: u64) -> Result<(), RegistryError> {
        let (mk, ok) = (names::key(me), names::key(other));
        let mut db = self.db.lock().unwrap();
        let tx = db.transaction()?;
        if Self::accepted_count(&tx, &mk)? >= MAX_FRIENDS || Self::accepted_count(&tx, &ok)? >= MAX_FRIENDS {
            return Err(friend_err("FRIEND_LIMIT", format!("at most {MAX_FRIENDS} friends")));
        }
        let n = tx.execute(
            "UPDATE friendships SET status = 'accepted', updated = ?3
             WHERE requester = ?2 AND addressee = ?1 AND status = 'pending'",
            params![mk, ok, now as i64],
        )?;
        if n == 0 {
            return Err(friend_err("REQUEST_NOT_FOUND", format!("no request from {other}")));
        }
        tx.commit()?;
        Ok(())
    }

    /// Reject a request from `other`, cancel one to `other`, or end a
    /// friendship: whatever links the two goes.
    pub fn remove_friend(&self, me: &str, other: &str) -> Result<(), RegistryError> {
        let (mk, ok) = (names::key(me), names::key(other));
        let db = self.db.lock().unwrap();
        let n = db.execute(
            "DELETE FROM friendships WHERE (requester = ?1 AND addressee = ?2) OR (requester = ?2 AND addressee = ?1)",
            params![mk, ok],
        )?;
        if n == 0 {
            return Err(friend_err("REQUEST_NOT_FOUND", format!("{other} isn't on your friends list")));
        }
        Ok(())
    }

    /// `me`'s friends, incoming and outgoing requests, alphabetical.
    pub fn friend_lists(&self, me: &str) -> Result<FriendLists, RegistryError> {
        let mk = names::key(me);
        let db = self.db.lock().unwrap();
        let mut st = db.prepare(
            "SELECT p.name, f.status, f.requester = ?1, f.created
             FROM friendships f JOIN profiles p
               ON p.key = CASE WHEN f.requester = ?1 THEN f.addressee ELSE f.requester END
             WHERE f.requester = ?1 OR f.addressee = ?1
             ORDER BY p.key",
        )?;
        let rows = st.query_map([&mk], |r| {
            Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?, r.get::<_, bool>(2)?, r.get::<_, i64>(3)? as u64))
        })?;
        let mut out = FriendLists::default();
        for row in rows {
            let (name, status, mine, since) = row?;
            match (status.as_str(), mine) {
                ("accepted", _) => out.friends.push(name),
                (_, true) => out.outgoing.push((name, since)),
                (_, false) => out.incoming.push((name, since)),
            }
        }
        Ok(out)
    }

    /// Accepted friendships among `names` (keys), as (key, friend display
    /// name) pairs in both directions: one query for a whole roster call.
    pub fn friends_among(&self, keys: &[String]) -> Result<Vec<(String, String)>, RegistryError> {
        if keys.is_empty() {
            return Ok(vec![]);
        }
        let marks = vec!["?"; keys.len()].join(",");
        let sql = format!(
            "SELECT f.requester, f.addressee, pr.name, pa.name FROM friendships f
             JOIN profiles pr ON pr.key = f.requester JOIN profiles pa ON pa.key = f.addressee
             WHERE f.status = 'accepted' AND (f.requester IN ({marks}) OR f.addressee IN ({marks}))"
        );
        let db = self.db.lock().unwrap();
        let mut st = db.prepare(&sql)?;
        let params: Vec<&dyn rusqlite::ToSql> = keys.iter().chain(keys.iter()).map(|k| k as &dyn rusqlite::ToSql).collect();
        let rows = st.query_map(params.as_slice(), |r| {
            Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?, r.get::<_, String>(2)?, r.get::<_, String>(3)?))
        })?;
        let mut out = vec![];
        for row in rows {
            let (rk, ak, rn, an) = row?;
            out.push((rk, an));
            out.push((ak, rn));
        }
        out.retain(|(k, _)| keys.contains(k));
        Ok(out)
    }

    /// The PIN hash of a profile (tokens are bound to it).
    pub fn pin_hash(&self, name: &str) -> Option<String> {
        self.row(&names::key(name)).ok().flatten().map(|r| r.pin_hash)
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

    fn friends(r: &Registry, who: &str) -> (Vec<String>, Vec<String>, Vec<String>) {
        let l = r.friend_lists(who).unwrap();
        (l.friends, l.incoming.into_iter().map(|x| x.0).collect(), l.outgoing.into_iter().map(|x| x.0).collect())
    }

    #[test]
    fn requests_accepts_and_removes() {
        let r = reg();
        for n in ["Ana", "Bia", "Cid"] {
            r.register(n, 0, false).unwrap();
        }
        assert_eq!(r.request_friend("ana", "BIA", 1).unwrap(), (Requested::Pending, "Bia".into()));
        assert_eq!(friends(&r, "ana"), (vec![], vec![], vec!["Bia".into()]));
        assert_eq!(friends(&r, "bia"), (vec![], vec!["Ana".into()], vec![]));
        assert!(matches!(r.request_friend("ana", "bia", 2), Err(RegistryError::Friend("FRIEND_PENDING", _))));
        assert!(matches!(r.request_friend("ana", "ana", 2), Err(RegistryError::Friend("FRIEND_SELF", _))));
        assert!(matches!(r.request_friend("ana", "zed", 2), Err(RegistryError::Friend("FRIEND_UNKNOWN", _))));
        r.accept_friend("bia", "ana", 3).unwrap();
        assert_eq!(friends(&r, "ana").0, vec!["Bia".to_string()]);
        assert!(matches!(r.request_friend("bia", "ana", 4), Err(RegistryError::Friend("FRIEND_EXISTS", _))));
        // Asking someone who asked you accepts.
        r.request_friend("cid", "ana", 5).unwrap();
        assert_eq!(r.request_friend("ana", "cid", 6).unwrap().0, Requested::Accepted);
        assert_eq!(friends(&r, "ana").0, vec!["Bia".to_string(), "Cid".to_string()]);
        let among = r.friends_among(&["ana".into(), "cid".into()]).unwrap();
        assert!(among.contains(&("ana".into(), "Bia".into())) && among.contains(&("cid".into(), "Ana".into())));
        assert!(!among.iter().any(|(k, _)| k == "bia"));
        r.remove_friend("bia", "ana").unwrap();
        assert_eq!(friends(&r, "ana").0, vec!["Cid".to_string()]);
        assert!(matches!(r.accept_friend("ana", "bia", 7), Err(RegistryError::Friend("REQUEST_NOT_FOUND", _))));
    }

    #[test]
    fn outgoing_requests_are_capped() {
        let r = reg();
        r.register("Ana", 0, false).unwrap();
        for i in 0..=MAX_OUTGOING {
            let n = format!("P{i:03}");
            r.register(&n, 0, false).unwrap();
            let res = r.request_friend("ana", &n, 1);
            assert_eq!(res.is_ok(), i < MAX_OUTGOING, "{i}");
        }
    }

    /// Every way a profile goes away takes its friendships and requests with
    /// it: a new owner of the name starts with none.
    #[test]
    fn friendships_never_outlive_their_profile() {
        let setup = || {
            let r = reg();
            for n in ["Ana", "Bia", "Cid"] {
                r.register(n, 0, false).unwrap();
            }
            r.request_friend("ana", "bia", 0).unwrap();
            r.accept_friend("bia", "ana", 0).unwrap();
            r.request_friend("ana", "cid", 0).unwrap();
            r.request_friend("ana", "cid", 0).unwrap_err();
            r
        };
        let clean = |r: &Registry, now: u64| {
            r.register("Ana", now, false).unwrap();
            assert_eq!(friends(r, "ana"), (vec![], vec![], vec![]));
            assert_eq!(friends(r, "bia"), (vec![], vec![], vec![]));
            assert_eq!(friends(r, "cid"), (vec![], vec![], vec![]));
        };
        // Owner delete.
        let r = setup();
        let pin = { r.release("ana").unwrap(); r.register("Ana", 0, false).unwrap() };
        let _ = pin;
        assert_eq!(friends(&r, "bia").0, Vec::<String>::new());
        let r = setup();
        r.reset_pin("ana").unwrap();
        assert_eq!(friends(&r, "bia").0, vec!["Ana".to_string()], "a new PIN keeps friends");
        let r = setup();
        let p = r.register("Zed", 0, false).unwrap();
        r.request_friend("zed", "ana", 0).unwrap();
        r.delete("zed", &p, 0).unwrap();
        assert_eq!(friends(&r, "ana").1, Vec::<String>::new(), "owner delete");
        // Admin release.
        let r = setup();
        r.release("ana").unwrap();
        clean(&r, 0);
        // Expiry sweep (bia and cid stay fresh).
        let r = setup();
        let later = 181 * DAY;
        r.db.lock().unwrap().execute("UPDATE profiles SET last_seen = ?1 WHERE key != 'ana'", [later as i64]).unwrap();
        assert_eq!(r.sweep(later), 1);
        clean(&r, later);
        // Takeover of an expired name without a sweep.
        let r = setup();
        r.db.lock().unwrap().execute("UPDATE profiles SET last_seen = ?1 WHERE key != 'ana'", [later as i64]).unwrap();
        clean(&r, later);
    }
}
