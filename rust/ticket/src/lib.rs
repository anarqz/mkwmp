//! Seat tickets, format v1 (docs/contracts/SEAT_API.md §3).
//!
//!   mkw1.<payload>.<sig>
//!
//! `payload` is base64url (no padding) of compact JSON [`Claims`]; `sig` is
//! base64url (no padding) of the Ed25519 signature over the ASCII bytes
//! `mkw1.<payload>`. The seat server signs with its private key; lobbies verify
//! with public keys only, so a self-hosted lobby can never mint tickets.
//!
//! The signature proves the seat server issued the session. Whether the seat is
//! still live is the lobby's roster check, not the ticket's.

use base64::engine::general_purpose::URL_SAFE_NO_PAD as B64;
use base64::Engine;
use ed25519_dalek::{Signature, Signer, SigningKey, Verifier as _, VerifyingKey};
use serde::{Deserialize, Serialize};

pub const PREFIX: &str = "mkw1";
/// Longest ticket a lobby (or the probe's buffer) must accept.
pub const MAX_LEN: usize = 512;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Claims {
    pub v: u32,
    pub iss: String,
    pub kid: String,
    pub sid: String,
    pub name: String,
    pub iat: u64,
    pub exp: u64,
}

#[derive(Debug, PartialEq, Eq)]
pub enum TicketError {
    TooLong,
    Malformed,
    UnknownKey,
    BadSignature,
    Expired,
    WrongVersion,
}

impl std::fmt::Display for TicketError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let s = match self {
            TicketError::TooLong => "ticket too long",
            TicketError::Malformed => "malformed ticket",
            TicketError::UnknownKey => "ticket signed by an unknown key",
            TicketError::BadSignature => "bad ticket signature",
            TicketError::Expired => "ticket expired",
            TicketError::WrongVersion => "unsupported ticket version",
        };
        f.write_str(s)
    }
}
impl std::error::Error for TicketError {}

/// The seat server's signing key with its id.
pub struct Issuer {
    kid: String,
    key: SigningKey,
}

impl Issuer {
    pub fn new(kid: impl Into<String>, key: SigningKey) -> Self {
        Self { kid: kid.into(), key }
    }

    /// Load from the 32-byte seed encoded as base64url (the `seat keygen` output).
    pub fn from_seed_b64(kid: impl Into<String>, seed: &str) -> Result<Self, TicketError> {
        let bytes = B64.decode(seed.trim()).map_err(|_| TicketError::Malformed)?;
        let seed: [u8; 32] = bytes.try_into().map_err(|_| TicketError::Malformed)?;
        Ok(Self::new(kid, SigningKey::from_bytes(&seed)))
    }

    pub fn generate() -> SigningKey {
        SigningKey::generate(&mut rand::rngs::OsRng)
    }

    pub fn kid(&self) -> &str {
        &self.kid
    }

    pub fn public_key_b64(&self) -> String {
        B64.encode(self.key.verifying_key().as_bytes())
    }

    /// Sign `claims`; its `kid` is overwritten with this issuer's.
    pub fn sign(&self, mut claims: Claims) -> String {
        claims.kid = self.kid.clone();
        let payload = B64.encode(serde_json::to_vec(&claims).expect("claims serialize"));
        let input = format!("{PREFIX}.{payload}");
        let sig = self.key.sign(input.as_bytes());
        format!("{input}.{}", B64.encode(sig.to_bytes()))
    }
}

pub fn seed_b64(key: &SigningKey) -> String {
    B64.encode(key.to_bytes())
}

/// Trusted public keys, by kid.
#[derive(Default, Clone)]
pub struct TicketVerifier {
    keys: Vec<(String, VerifyingKey)>,
}


impl TicketVerifier {
    /// Parse `kid:base64url-key` entries (config `[ticket] public_keys`).
    pub fn from_entries<S: AsRef<str>>(entries: &[S]) -> Result<Self, TicketError> {
        let mut v = Self::default();
        for e in entries {
            let (kid, key) = e.as_ref().split_once(':').ok_or(TicketError::Malformed)?;
            v.add(kid, key)?;
        }
        Ok(v)
    }

    pub fn add(&mut self, kid: &str, key_b64: &str) -> Result<(), TicketError> {
        let bytes = B64.decode(key_b64.trim()).map_err(|_| TicketError::Malformed)?;
        let bytes: [u8; 32] = bytes.try_into().map_err(|_| TicketError::Malformed)?;
        let key = VerifyingKey::from_bytes(&bytes).map_err(|_| TicketError::Malformed)?;
        self.keys.push((kid.to_string(), key));
        Ok(())
    }

    /// Verify signature, version and expiry at `now` (unix seconds).
    pub fn verify(&self, ticket: &str, now: u64) -> Result<Claims, TicketError> {
        if ticket.len() > MAX_LEN {
            return Err(TicketError::TooLong);
        }
        let (input, sig_b64) = ticket.rsplit_once('.').ok_or(TicketError::Malformed)?;
        let (prefix, payload_b64) = input.split_once('.').ok_or(TicketError::Malformed)?;
        if prefix != PREFIX {
            return Err(TicketError::WrongVersion);
        }
        let payload = B64.decode(payload_b64).map_err(|_| TicketError::Malformed)?;
        let claims: Claims = serde_json::from_slice(&payload).map_err(|_| TicketError::Malformed)?;
        let key = self
            .keys
            .iter()
            .find(|(kid, _)| *kid == claims.kid)
            .map(|(_, k)| k)
            .ok_or(TicketError::UnknownKey)?;
        let sig = B64.decode(sig_b64).map_err(|_| TicketError::Malformed)?;
        let sig = Signature::from_slice(&sig).map_err(|_| TicketError::Malformed)?;
        key.verify(input.as_bytes(), &sig).map_err(|_| TicketError::BadSignature)?;
        if claims.v != 1 {
            return Err(TicketError::WrongVersion);
        }
        if claims.exp <= now {
            return Err(TicketError::Expired);
        }
        Ok(claims)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Fixed seed so the vectors in resources/tools/ticket/vectors.json are reproducible.
    pub const TEST_SEED: [u8; 32] = [7; 32];

    fn issuer() -> Issuer {
        Issuer::new("test1", SigningKey::from_bytes(&TEST_SEED))
    }

    fn claims() -> Claims {
        Claims {
            v: 1,
            iss: "mkwmp".into(),
            kid: String::new(),
            sid: "0123456789abcdef0123456789abcdef".into(),
            name: "Player_One-16chr".into(),
            iat: 1_700_000_000,
            exp: 1_700_043_200,
        }
    }

    fn verifier(i: &Issuer) -> TicketVerifier {
        TicketVerifier::from_entries(&[format!("{}:{}", i.kid(), i.public_key_b64())]).unwrap()
    }

    #[test]
    fn round_trip() {
        let i = issuer();
        let t = i.sign(claims());
        assert!(t.starts_with("mkw1."));
        assert!(t.len() <= MAX_LEN, "len {}", t.len());
        assert!(t.bytes().all(|b| b.is_ascii_alphanumeric() || b"._-".contains(&b)));
        let c = verifier(&i).verify(&t, 1_700_000_001).unwrap();
        assert_eq!(c.kid, "test1");
        assert_eq!(c.name, "Player_One-16chr");
    }

    #[test]
    fn expired_rejected() {
        let i = issuer();
        let t = i.sign(claims());
        assert_eq!(verifier(&i).verify(&t, 1_700_043_200), Err(TicketError::Expired));
    }

    #[test]
    fn tampered_payload_rejected() {
        let i = issuer();
        let t = i.sign(claims());
        let mut c = claims();
        c.name = "Mallory".into();
        c.kid = "test1".into();
        let forged_payload = B64.encode(serde_json::to_vec(&c).unwrap());
        let parts: Vec<&str> = t.split('.').collect();
        let forged = format!("mkw1.{forged_payload}.{}", parts[2]);
        assert_eq!(verifier(&i).verify(&forged, 1_700_000_001), Err(TicketError::BadSignature));
    }

    #[test]
    fn other_key_rejected() {
        let other = Issuer::new("test1", Issuer::generate());
        let t = other.sign(claims());
        assert_eq!(verifier(&issuer()).verify(&t, 1_700_000_001), Err(TicketError::BadSignature));
        let stranger = Issuer::new("nope", Issuer::generate());
        assert_eq!(
            verifier(&issuer()).verify(&stranger.sign(claims()), 1_700_000_001),
            Err(TicketError::UnknownKey)
        );
    }

    #[test]
    fn self_made_unsigned_ticket_rejected() {
        let v = verifier(&issuer());
        assert!(v.verify("eyJhbGciOiJub25lIn0.YWJj.", 0).is_err());
        assert!(v.verify("mkw1.e30.", 0).is_err());
        assert_eq!(v.verify(&"a".repeat(600), 0), Err(TicketError::TooLong));
    }

    /// Prints the cross-language vectors: `cargo test -p ticket print_vectors -- --ignored --nocapture`.
    #[test]
    #[ignore]
    fn print_vectors() {
        let i = issuer();
        let good = i.sign(claims());
        let mut tampered: Vec<u8> = good.clone().into_bytes();
        let n = tampered.len();
        tampered[n - 2] = if tampered[n - 2] == b'A' { b'B' } else { b'A' };
        println!(
            "{}",
            serde_json::json!({
                "publicKeys": [format!("test1:{}", i.public_key_b64())],
                "now": 1_700_000_001u64,
                "valid": [{"ticket": good, "claims": serde_json::to_value({let mut c = claims(); c.kid = "test1".into(); c}).unwrap()}],
                "invalid": [
                    {"ticket": String::from_utf8(tampered).unwrap(), "why": "bad signature"},
                    {"ticket": Issuer::new("test1", SigningKey::from_bytes(&[9; 32])).sign(claims()), "why": "signed by another key"},
                    {"ticket": "eyJhbGciOiJub25lIn0.YWJj.", "why": "unsigned, not mkw1"},
                ],
                "expiredAt": 1_700_043_200u64,
            })
        );
    }
}
