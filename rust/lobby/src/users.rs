//! Who a caller is: user ids, the small per-user numbers the game needs, launcher names,
//! and the access tokens the lobby hands out.
//!
//! - A registered player signs in with a seat ticket (`mkw1.…`, rust/ticket); their user id
//!   is `uuid5(URL, "mkwmp-user/" + lower(name))`, the same on every server and restart.
//! - Local development builds of the probe sign in with fixed test users
//!   `00000000-0000-4000-8000-0000000000NN` (only accepted when tickets are not required).
//! - Access tokens are HS256 JWTs `{sub: user resource, iss, aud, iat, exp}`. They are
//!   self-describing, so with a persistent secret a restarted lobby still knows which player
//!   a running game is (the game keeps its token for an hour).

use std::collections::{HashMap, HashSet};

use base64::engine::general_purpose::URL_SAFE_NO_PAD as B64;
use base64::Engine;
use hmac::{Hmac, Mac};
use serde::{Deserialize, Serialize};
use sha2::Sha256;
use uuid::Uuid;

/// Prefix of the probe's local test users; the last 12 hex digits are the player number.
pub const LEGACY_USER: &str = "00000000-0000-4000-8000-";
/// The second local test user's sign-in token (probe slot 2), sent verbatim by the probe.
pub const SECOND_LOCAL_TOKEN: &str = "eyJhbGciOiJub25lIn0.eyJzdWIiOiJta3dtcC1sb2NhbC0yIiwiaXNzIjoibWt3bXAifQ.";
/// Token lifetime advertised to the game (Duration in the Token message).
pub const TOKEN_TTL_SECS: u64 = 3600;
const ISSUER: &str = "mkwmp";

/// Stable user id of a registered (PIN-proven) seat-ticket name.
pub fn ticket_user_id(name: &str) -> String {
    Uuid::new_v5(&Uuid::NAMESPACE_URL, format!("mkwmp-user/{}", name.to_lowercase()).as_bytes()).to_string()
}

/// Last path component: the user id of `tenants/<t>/users/<id>` or of a document name.
pub fn tail(resource: &str) -> &str {
    resource.rsplit('/').next().unwrap_or(resource)
}

/// Tenant part of a user resource (`tenants/<t>`).
pub fn tenant(user_resource: &str) -> &str {
    user_resource.split_once("/users/").map_or("tenants/current", |(t, _)| t)
}

/// Which service a token is for.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Audience {
    Npln,
    Gamesync,
}

#[derive(Serialize, Deserialize)]
struct Claims {
    sub: String,
    iss: String,
    aud: Audience,
    iat: u64,
    exp: u64,
}

/// Mints and checks the lobby's access tokens.
pub struct Tokens {
    secret: Vec<u8>,
}

impl Tokens {
    pub fn new(secret: Vec<u8>) -> Self {
        Self { secret }
    }

    fn mac(&self, input: &str) -> String {
        let mut mac = Hmac::<Sha256>::new_from_slice(&self.secret).expect("hmac accepts any key");
        mac.update(input.as_bytes());
        B64.encode(mac.finalize().into_bytes())
    }

    pub fn mint(&self, user: &str, aud: Audience, now: u64) -> String {
        let header = B64.encode(br#"{"alg":"HS256","typ":"JWT"}"#);
        let claims = Claims { sub: user.into(), iss: ISSUER.into(), aud, iat: now, exp: now + TOKEN_TTL_SECS };
        let payload = B64.encode(serde_json::to_vec(&claims).expect("claims serialize"));
        let input = format!("{header}.{payload}");
        let sig = self.mac(&input);
        format!("{input}.{sig}")
    }

    /// The user resource of a valid, unexpired token of ours (any audience).
    /// Expiry is checked with a grace hour: the game refreshes late after a long race.
    pub fn subject(&self, token: &str, now: u64) -> Option<String> {
        let (input, sig) = token.rsplit_once('.')?;
        let expected = self.mac(input);
        if !constant_time_eq(sig.as_bytes(), expected.as_bytes()) {
            return None;
        }
        let payload = input.split_once('.')?.1;
        let claims: Claims = serde_json::from_slice(&B64.decode(payload).ok()?).ok()?;
        (claims.iss == ISSUER && claims.exp + TOKEN_TTL_SECS > now && claims.sub.contains("/users/"))
            .then_some(claims.sub)
    }
}

fn constant_time_eq(a: &[u8], b: &[u8]) -> bool {
    a.len() == b.len() && a.iter().zip(b).fold(0u8, |acc, (x, y)| acc | (x ^ y)) == 0
}

/// Small player numbers (`ussid`, `ucsid`, `upcsid`, document ids like `PlayerStatic/<n>`)
/// and display names, per user id. Numbers are unique across all lobbies and kept for the
/// life of the process, so a player who rejoins keeps theirs.
#[derive(Default)]
pub struct Directory {
    numbers: HashMap<String, u32>,
    used: HashSet<u32>,
    next: u32,
    names: HashMap<String, String>,
}

impl Directory {
    /// Number of a user id (or of a resource / document name ending in one).
    pub fn number(&mut self, user: &str) -> u32 {
        let id = tail(user);
        if let Some(n) = legacy_number(id) {
            self.used.insert(n);
            return n;
        }
        if let Some(&n) = self.numbers.get(id) {
            return n;
        }
        if Uuid::parse_str(id).is_err() {
            return 1; // not a user id (matches the reference server's fallback)
        }
        loop {
            self.next += 1;
            if self.used.insert(self.next) {
                break;
            }
        }
        self.numbers.insert(id.to_owned(), self.next);
        self.next
    }

    pub fn set_name(&mut self, user: &str, name: &str) {
        self.names.insert(tail(user).to_owned(), name.to_owned());
    }

    /// Launcher name of a seat-ticket player.
    pub fn name(&self, user: &str) -> Option<&str> {
        self.names.get(tail(user)).map(String::as_str)
    }
}

fn legacy_number(id: &str) -> Option<u32> {
    let rest = id.strip_prefix(LEGACY_USER)?;
    Some(u32::from_str_radix(rest, 16).ok().filter(|&n| n != 0).unwrap_or(1))
}

/// A local test user's id.
pub fn legacy_user(n: u32) -> String {
    format!("{LEGACY_USER}{n:012}")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ticket_ids_match_the_reference_server() {
        // python: str(uuid5(NAMESPACE_URL, "mkwmp-user/" + "alex"))
        assert_eq!(ticket_user_id("Alex"), ticket_user_id("alex"));
        assert_eq!(ticket_user_id("alex"), Uuid::new_v5(&Uuid::NAMESPACE_URL, b"mkwmp-user/alex").to_string());
    }

    #[test]
    fn tokens_roundtrip_and_reject_tampering() {
        let t = Tokens::new(b"secret".to_vec());
        let tok = t.mint("tenants/x/users/abc", Audience::Npln, 1000);
        assert_eq!(t.subject(&tok, 1001).as_deref(), Some("tenants/x/users/abc"));
        assert_eq!(t.subject(&tok, 1000 + 3 * TOKEN_TTL_SECS), None);
        assert_eq!(Tokens::new(b"other".to_vec()).subject(&tok, 1001), None);
        let mut bad = tok.clone();
        bad.insert(40, 'A');
        assert_eq!(t.subject(&bad, 1001), None);
    }

    #[test]
    fn numbers_are_small_unique_and_skip_legacy() {
        let mut d = Directory::default();
        assert_eq!(d.number(&legacy_user(1)), 1);
        let a = ticket_user_id("ana");
        let b = ticket_user_id("bia");
        assert_eq!(d.number(&a), 2);
        assert_eq!(d.number(&format!("tenants/t/users/{b}")), 3);
        assert_eq!(d.number(&format!("docs/__pgn/All/__pus/{a}")), 2);
        assert_eq!(d.number(&legacy_user(2)), 2); // legacy keeps its own number
        assert_eq!(d.number("not-a-user"), 1);
    }
}
