//! Friends and presence (contracts/nn/npln/friends/v1/friends.proto, resources/docs/RE-MAP.md A.2).
//!
//! On a community server every other signed-in player is a friend. The game only lists NPLN
//! friends that are also in the console's system friend list, so the probe fills
//! `nn::friends::GetFriendList` from the launcher roster with the same [`account_id`] per
//! name; this module maps those account ids to NPLN users (SubscribeFriendUsers) and carries
//! each player's presence (SubscribePresences / KeepAlive). The presence attribute `SessionId`
//! is how a friend's room is found (then BatchGetGameSessions + JoinGameSession).

use std::collections::BTreeMap;

use crate::value::{Fields, Value};
use crate::wire::{self, Msg, Result};

/// Seconds the client's presence/friends watchdogs are armed with; the core refreshes the
/// streams well within it.
pub const WATCHDOG_SECS: u64 = 60;

/// The console account id the probe gives a player in its system friend list:
/// the first 8 bytes of the user id UUID (big-endian), top bit cleared, never 0.
pub fn account_id(uid: &str) -> u64 {
    let Ok(id) = uuid::Uuid::parse_str(uid) else { return 1 };
    let b = id.as_bytes();
    let v = u64::from_be_bytes(b[..8].try_into().expect("16-byte uuid")) & 0x7fff_ffff_ffff_ffff;
    v.max(1)
}

/// One player's presence as other players see it.
#[derive(Clone, Debug, Default)]
pub struct Presence {
    /// User resource `tenants/<t>/users/<id>`.
    pub user: String,
    /// A KeepAlive stream is open.
    pub online: bool,
    /// `SessionId`, `DriverId`, `StickerId`, ... as the game published them.
    pub attributes: Fields,
}

impl Presence {
    pub fn name(&self) -> String {
        format!("{}/presence", self.user)
    }

    /// Presence (main+0x123d5a0): `{1 name, 2 state (1 online / 2 offline), 3 attributes}`.
    pub fn encode(&self) -> Msg {
        let mut m = Msg::new().bytes(1, self.name()).varint(2, if self.online { 1 } else { 2 });
        if self.online {
            for (k, v) in &self.attributes.0 {
                m = m.msg(3, &Msg::new().bytes(1, k).msg(2, &v.encode()));
            }
        }
        m
    }

    /// The game session the player published, if any (bare id).
    pub fn session_id(&self) -> Option<&str> {
        self.attributes.get("SessionId").and_then(Value::as_str).filter(|s| !s.is_empty())
            .map(|s| crate::users::tail(s))
    }
}

/// `SubscribeFriendUsersResponse`: one group per friend `{1 hex account id, 2 [{2 user}]}`, and
/// `3 Duration` for the client's watchdog.
pub fn friend_users<'a>(friends: impl IntoIterator<Item = &'a str>) -> Vec<u8> {
    let mut m = Msg::new();
    for user in friends {
        let uid = crate::users::tail(user);
        let group = Msg::new().bytes(1, format!("{:016x}", account_id(uid))).msg(2, &Msg::new().bytes(2, user));
        m = m.msg(1, &group);
    }
    m.msg(3, &wire::seconds(WATCHDOG_SECS)).into_bytes()
}

/// `SubscribePresencesResponse{1: PresenceBatch{1 repeated Presence}}`.
pub fn presence_batch<'a>(presences: impl IntoIterator<Item = &'a Presence>) -> Vec<u8> {
    let batch = presences.into_iter().fold(Msg::new(), |m, p| m.msg(1, &p.encode()));
    Msg::new().msg(1, &batch).into_bytes()
}

/// `KeepAliveTiming{1 Duration, 2 Duration}` (the client reads field 2 as seconds).
fn timing() -> Msg {
    Msg::new().msg(1, &wire::seconds(WATCHDOG_SECS)).msg(2, &wire::seconds(WATCHDOG_SECS / 2))
}

/// `SubscribePresencesResponse{3: timing}`: re-arms the subscription watchdog.
pub fn presence_timing() -> Vec<u8> {
    Msg::new().msg(3, &timing()).into_bytes()
}

/// `KeepAliveResponse{1: ack}`: lets the client's presence writer continue.
pub fn keepalive_ack() -> Vec<u8> {
    Msg::new().msg(1, &timing()).into_bytes()
}

/// A client KeepAlive frame.
#[derive(Debug, PartialEq)]
pub enum KeepAlive {
    Open,
    Heartbeat,
    /// Own attributes, and the `attributes.<key>` paths of the mask (a masked key without a
    /// value is removed).
    Update { attributes: Fields, mask: Vec<String> },
    Other,
}

pub fn parse_keepalive(buf: &[u8]) -> Result<KeepAlive> {
    if let Some(u) = wire::bytes_field(buf, 1)? {
        let presence = wire::bytes_field(u, 1)?.unwrap_or_default();
        let mut attributes = Fields::new();
        for entry in wire::repeated_bytes(presence, 3)? {
            let key = wire::str_field(entry, 1)?.unwrap_or_default();
            if let Some(v) = wire::bytes_field(entry, 2)? {
                attributes.set(&key, Value::decode(v)?);
            }
        }
        let mask = match wire::bytes_field(u, 2)? {
            Some(m) => wire::repeated_bytes(m, 1)?
                .into_iter()
                .filter_map(|p| String::from_utf8_lossy(p).strip_prefix("attributes.").map(|k| k.trim_matches('`').to_owned()))
                .collect(),
            None => attributes.0.iter().map(|(k, _)| k.clone()).collect(),
        };
        return Ok(KeepAlive::Update { attributes, mask });
    }
    if wire::bytes_field(buf, 2)?.is_some() {
        return Ok(KeepAlive::Heartbeat);
    }
    if wire::bytes_field(buf, 3)?.is_some() {
        return Ok(KeepAlive::Open);
    }
    Ok(KeepAlive::Other)
}

/// Presence of every player that signed in, by user id.
#[derive(Default)]
pub struct Social {
    pub presences: BTreeMap<String, Presence>,
}

impl Social {
    /// Apply a KeepAlive update; returns true when something changed.
    pub fn update(&mut self, uid: &str, user: &str, attributes: Fields, mask: &[String]) -> bool {
        let p = self.presences.entry(uid.to_owned()).or_insert_with(|| Presence { user: user.to_owned(), ..Default::default() });
        let before = p.attributes.clone();
        for key in mask {
            if !p.attributes.contains(key) && p.attributes.0.len() >= crate::limits::caps::PRESENCE_ATTRIBUTES {
                continue;
            }
            match attributes.get(key).filter(|v| v.encode().0.len() <= crate::limits::caps::PRESENCE_VALUE_BYTES) {
                Some(v) => p.attributes.set(key, v.clone()),
                None => p.attributes.remove(key),
            }
        }
        p.attributes != before
    }

    /// Mark online/offline; returns true when it changed.
    pub fn set_online(&mut self, uid: &str, user: &str, online: bool) -> bool {
        let p = self.presences.entry(uid.to_owned()).or_insert_with(|| Presence { user: user.to_owned(), ..Default::default() });
        let changed = p.online != online;
        p.online = online;
        if !online {
            p.attributes = Fields::new();
        }
        changed
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn account_ids_are_stable_positive_and_distinct() {
        let a = account_id(&crate::users::ticket_user_id("ana"));
        let b = account_id(&crate::users::ticket_user_id("bia"));
        assert_ne!(a, b);
        assert!(a > 0 && a < 1 << 63);
        // python: int.from_bytes(uuid5(NAMESPACE_URL, "mkwmp-user/ana").bytes[:8], "big") & (2**63 - 1)
        let id = uuid::Uuid::new_v5(&uuid::Uuid::NAMESPACE_URL, b"mkwmp-user/ana");
        assert_eq!(a, u64::from_be_bytes(id.as_bytes()[..8].try_into().unwrap()) & (u64::MAX >> 1));
    }

    #[test]
    fn keepalive_update_merges_by_mask() {
        let presence = Msg::new()
            .bytes(1, "tenants/t/users/u/presence")
            .msg(3, &Msg::new().bytes(1, "SessionId").msg(2, &Value::str("abc").encode()));
        let mask = Msg::new().bytes(1, "attributes.SessionId").bytes(1, "attributes.DriverId");
        let frame = Msg::new().msg(1, &Msg::new().msg(1, &presence).msg(2, &mask));
        let KeepAlive::Update { attributes, mask } = parse_keepalive(&frame.0).unwrap() else { panic!() };
        assert_eq!(mask, vec!["SessionId", "DriverId"]);
        let mut s = Social::default();
        s.presences.insert("u".into(), Presence {
            user: "tenants/t/users/u".into(),
            online: true,
            attributes: Fields::new().with("DriverId", Value::Int(3)),
        });
        assert!(s.update("u", "tenants/t/users/u", attributes, &mask));
        let p = &s.presences["u"];
        assert_eq!(p.session_id(), Some("abc"));
        assert!(!p.attributes.contains("DriverId"));
        assert_eq!(parse_keepalive(&Msg::new().msg(3, &Msg::new().bytes(1, "x")).0).unwrap(), KeepAlive::Open);
    }

    #[test]
    fn friend_users_groups_carry_hex_account_ids() {
        let uid = crate::users::ticket_user_id("ana");
        let user = format!("tenants/t/users/{uid}");
        let m = friend_users([user.as_str()]);
        let group = wire::repeated_bytes(&m, 1).unwrap()[0];
        let hex = wire::str_field(group, 1).unwrap().unwrap();
        assert_eq!(u64::from_str_radix(&hex, 16).unwrap(), account_id(&uid));
        let fu = wire::bytes_field(group, 2).unwrap().unwrap();
        assert_eq!(wire::str_field(fu, 2).unwrap().as_deref(), Some(user.as_str()));
    }
}
