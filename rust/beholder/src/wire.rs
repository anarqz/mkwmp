//! The announce body and its signed message (BEHOLDER_API.md §2), shared by the beholder (which
//! checks it) and the seat server (which signs it).

use base64::engine::general_purpose::STANDARD as B64;
use base64::Engine;
use ed25519_dalek::{Signature, Signer, SigningKey, Verifier, VerifyingKey};
use serde::{Deserialize, Serialize};

/// First line of every signed message.
pub const DOMAIN: &str = "mkwmp-beholder-announce-v1";
/// Largest announce body.
pub const MAX_BODY: usize = 4096;
/// How far `issuedAt` may be from the beholder's clock.
pub const SKEW_SECS: u64 = 120;
/// The only kind today (relays may follow).
pub const KIND_SERVER: &str = "server";
/// Largest `capacity`, `seated` and `queued`.
pub const MAX_COUNT: u32 = 100_000;

/// `POST /beholder/v1/announce` body. Unknown fields are refused.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Announce {
    pub kind: String,
    pub public_key: String,
    pub endpoint: String,
    pub name: String,
    pub region: String,
    pub version: String,
    pub capacity: u32,
    pub seated: u32,
    pub queued: u32,
    pub issued_at: u64,
    pub signature: String,
}

/// What a server announces, before signing.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Fields {
    pub endpoint: String,
    pub name: String,
    pub region: String,
    pub version: String,
    pub capacity: u32,
    pub seated: u32,
    pub queued: u32,
}

/// Why an announce is refused.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Reject {
    /// The field that breaks its rule (`body` for a body that isn't the JSON shape).
    Invalid(&'static str),
    Signature,
    Stale,
}

pub fn public_key_b64(key: &SigningKey) -> String {
    B64.encode(key.verifying_key().as_bytes())
}

/// The UTF-8 text that is signed: the domain and every field but the signature, one per line,
/// no trailing newline.
pub fn message(kind: &str, public_key: &str, f: &Fields, issued_at: u64) -> String {
    [
        DOMAIN,
        kind,
        public_key,
        &f.endpoint,
        &f.name,
        &f.region,
        &f.version,
        &f.capacity.to_string(),
        &f.seated.to_string(),
        &f.queued.to_string(),
        &issued_at.to_string(),
    ]
    .join("\n")
}

impl Announce {
    /// Sign `f` with the server's key.
    pub fn sign(key: &SigningKey, f: &Fields, issued_at: u64) -> Self {
        let public_key = public_key_b64(key);
        let sig = key.sign(message(KIND_SERVER, &public_key, f, issued_at).as_bytes());
        Self {
            kind: KIND_SERVER.into(),
            public_key,
            endpoint: f.endpoint.clone(),
            name: f.name.clone(),
            region: f.region.clone(),
            version: f.version.clone(),
            capacity: f.capacity,
            seated: f.seated,
            queued: f.queued,
            issued_at,
            signature: B64.encode(sig.to_bytes()),
        }
    }

    pub fn fields(&self) -> Fields {
        Fields {
            endpoint: self.endpoint.clone(),
            name: self.name.clone(),
            region: self.region.clone(),
            version: self.version.clone(),
            capacity: self.capacity,
            seated: self.seated,
            queued: self.queued,
        }
    }

    /// Every rule of §2, then the clock window, then the signature (the costly part last).
    pub fn check(&self, now_secs: u64) -> Result<VerifyingKey, Reject> {
        if self.kind != KIND_SERVER {
            return Err(Reject::Invalid("kind"));
        }
        let key = decode_key(&self.public_key).ok_or(Reject::Invalid("publicKey"))?;
        check_fields(&self.fields())?;
        if self.issued_at.abs_diff(now_secs) > SKEW_SECS {
            return Err(Reject::Stale);
        }
        let sig: [u8; 64] = B64
            .decode(&self.signature)
            .ok()
            .and_then(|b| b.try_into().ok())
            .ok_or(Reject::Invalid("signature"))?;
        let msg = message(&self.kind, &self.public_key, &self.fields(), self.issued_at);
        key.verify(msg.as_bytes(), &Signature::from_bytes(&sig)).map_err(|_| Reject::Signature)?;
        Ok(key)
    }
}

fn decode_key(s: &str) -> Option<VerifyingKey> {
    let b: [u8; 32] = B64.decode(s).ok()?.try_into().ok()?;
    VerifyingKey::from_bytes(&b).ok()
}

/// The field rules (also run by the seat server on its settings before it starts announcing).
pub fn check_fields(f: &Fields) -> Result<(), Reject> {
    if !valid_endpoint(&f.endpoint) {
        return Err(Reject::Invalid("endpoint"));
    }
    let n = f.name.chars().count();
    if n == 0 || n > 48 || f.name.trim() != f.name || f.name.chars().any(char::is_control) {
        return Err(Reject::Invalid("name"));
    }
    if f.region.len() > 16 || !f.region.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-') {
        return Err(Reject::Invalid("region"));
    }
    if f.version.is_empty() || f.version.len() > 32 || !f.version.bytes().all(|b| b.is_ascii_alphanumeric() || b"+.-".contains(&b)) {
        return Err(Reject::Invalid("version"));
    }
    if f.capacity == 0 || f.capacity > MAX_COUNT {
        return Err(Reject::Invalid("capacity"));
    }
    if f.seated > f.capacity {
        return Err(Reject::Invalid("seated"));
    }
    if f.queued > MAX_COUNT {
        return Err(Reject::Invalid("queued"));
    }
    Ok(())
}

/// `http(s)://host[:port]`: no path (a lone `/` is not allowed either), query or user info; the
/// host a dotted ASCII DNS name or a public IPv4 literal (no `localhost`, private or loopback
/// address: a directory entry nobody else can reach is noise).
pub fn valid_endpoint(e: &str) -> bool {
    let Some(rest) = e.strip_prefix("https://").or_else(|| e.strip_prefix("http://")) else { return false };
    if e.len() > 200 || rest.is_empty() || rest.bytes().any(|b| b"/?#@\\ ".contains(&b) || b.is_ascii_control()) {
        return false;
    }
    let (host, port) = match rest.rsplit_once(':') {
        Some((h, p)) => (h, Some(p)),
        None => (rest, None),
    };
    if let Some(p) = port {
        if p.is_empty() || p.len() > 5 || !p.bytes().all(|b| b.is_ascii_digit()) || !matches!(p.parse::<u32>(), Ok(1..=65535)) {
            return false;
        }
    }
    if let Ok(ip) = host.parse::<std::net::Ipv4Addr>() {
        return !(ip.is_private() || ip.is_loopback() || ip.is_link_local() || ip.is_unspecified() || ip.is_broadcast() || ip.is_documentation() || ip.octets()[0] == 0);
    }
    // A DNS name: labels of [a-z0-9-], not starting or ending with '-', at least two of them,
    // the last not all digits (that would be a malformed IP).
    let labels: Vec<&str> = host.split('.').collect();
    labels.len() >= 2
        && host.len() <= 253
        && labels.iter().all(|l| {
            !l.is_empty() && l.len() <= 63 && !l.starts_with('-') && !l.ends_with('-') && l.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-')
        })
        && !labels.last().unwrap().bytes().all(|b| b.is_ascii_digit())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fields() -> Fields {
        Fields {
            endpoint: "https://mkw.example.net".into(),
            name: "Example São Paulo".into(),
            region: "sa-east".into(),
            version: "1.3.0".into(),
            capacity: 50,
            seated: 3,
            queued: 0,
        }
    }

    #[test]
    fn signed_announce_verifies_and_tampering_breaks_it() {
        let key = SigningKey::from_bytes(&[7; 32]);
        let a = Announce::sign(&key, &fields(), 1_800_000_000);
        assert_eq!(a.check(1_800_000_010).unwrap(), key.verifying_key());
        let mut b = a.clone();
        b.seated = 4;
        assert_eq!(b.check(1_800_000_010), Err(Reject::Signature));
        assert_eq!(a.check(1_800_000_000 + SKEW_SECS + 1), Err(Reject::Stale));
    }

    #[test]
    fn message_layout_is_fixed() {
        let m = message("server", "PK", &fields(), 42);
        assert_eq!(m, "mkwmp-beholder-announce-v1\nserver\nPK\nhttps://mkw.example.net\nExample São Paulo\nsa-east\n1.3.0\n50\n3\n0\n42");
    }

    #[test]
    fn endpoint_rules() {
        for ok in ["https://mkw.anarqz.dev", "http://216.128.169.210:7000", "https://a-b.example.org:443"] {
            assert!(valid_endpoint(ok), "{ok}");
        }
        for bad in [
            "mkw.anarqz.dev",
            "https://mkw.anarqz.dev/",
            "https://mkw.anarqz.dev/seat",
            "https://user@mkw.anarqz.dev",
            "https://localhost:7000",
            "http://127.0.0.1:7000",
            "http://192.168.1.5:7000",
            "https://mkw.anarqz.dev:0",
            "https://mkw.anarqz.dev:99999",
            "https://-bad.example.com",
            "https://1.2.3",
            "ftp://mkw.anarqz.dev",
        ] {
            assert!(!valid_endpoint(bad), "{bad}");
        }
    }

    #[test]
    fn field_rules() {
        let mut f = fields();
        f.seated = 51;
        assert_eq!(check_fields(&f), Err(Reject::Invalid("seated")));
        let mut f = fields();
        f.name = " padded".into();
        assert_eq!(check_fields(&f), Err(Reject::Invalid("name")));
        let mut f = fields();
        f.region = "sa east".into();
        assert_eq!(check_fields(&f), Err(Reject::Invalid("region")));
        let mut f = fields();
        f.version = String::new();
        assert_eq!(check_fields(&f), Err(Reject::Invalid("version")));
    }
}
