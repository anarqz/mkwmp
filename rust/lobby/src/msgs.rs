//! NPLN request parsers and response encoders. Every layout here is the one the verified
//! Python reference sends (server/mkwmp_server.py), with its serializer address.

use crate::value::{Document, Fields, Value};
use crate::wire::{self, Msg, Result};

/// `google.protobuf.Duration` of one token lifetime.
fn token_duration() -> Msg {
    wire::seconds(crate::users::TOKEN_TTL_SECS)
}

/// Auth `IssuePrearrangedUserToken` request (main+0xec3cc0): `{1 tenant, ..., 3 {1 token}}`.
pub struct SignIn {
    pub tenant: String,
    pub token: Option<String>,
}

pub fn parse_sign_in(buf: &[u8]) -> Result<SignIn> {
    let tenant = wire::str_field(buf, 1)?.unwrap_or_else(|| "tenants/t-e96531c6-lp1".into());
    let token = match wire::bytes_field(buf, 3)? {
        Some(c) => wire::str_field(c, 1)?,
        None => None,
    };
    Ok(SignIn { tenant, token })
}

/// Auth response (main+0xec7100): `{2: Token{1 user, 2 access, 3 refresh, 4 Duration}}`.
pub fn sign_in_response(user: &str, access: &str, number: u32) -> Vec<u8> {
    let token = Msg::new()
        .bytes(1, user)
        .bytes(2, access)
        .bytes(3, format!("mkwmp-local-refresh-token-{number}"))
        .msg(4, &token_duration());
    Msg::new().msg(2, &token).into_bytes()
}

/// Gamesync `IssueToken` / `RefreshToken` (request main+0x12a1ee0 `{1, 2}`; response
/// main+0x12a26fc `{1: Token}`, Token main+0x12b94c4 same layout as Auth's).
pub fn gamesync_token_response(subject: &str, access: &str) -> Vec<u8> {
    let token = Msg::new()
        .bytes(1, subject)
        .bytes(2, access)
        .bytes(3, format!("{access}-refresh"))
        .msg(4, &token_duration());
    Msg::new().msg(1, &token).into_bytes()
}

/// Fixed TURN credentials (the relay accepts exactly these).
pub mod ice {
    pub const TURN_USERNAME: &str = "mkwmp";
    pub const TURN_PASSWORD: &str = "mkwmp";
    pub const TURN_REALM: &str = "mkwmp";
}

/// `AllocateIceServerSet` (serializer main+0x1410640, consumer FUN_016297a0):
/// `1 name, 2 {1 host, 2 port}, 3 repeated IceServer{1 host, 2 port, 3 type (1 TURN),
/// 4 username, 5 credential}, 4 Duration, 5 Timestamp (expiry), 6 Duration`.
pub fn ice_server_set(host: &str, port: u16, now: u64) -> Vec<u8> {
    let server = Msg::new()
        .bytes(1, host)
        .varint(2, port.into())
        .varint(3, 1)
        .bytes(4, ice::TURN_USERNAME)
        .bytes(5, ice::TURN_PASSWORD);
    Msg::new()
        .bytes(1, "tenants/current/iceServerSets/local")
        .msg(2, &Msg::new().bytes(1, host).varint(2, port.into()))
        .msg(3, &server)
        .msg(4, &wire::seconds(3600))
        .msg(5, &wire::seconds(now + 3600))
        .msg(6, &wire::seconds(3600))
        .into_bytes()
}

/// `CreateMatchmakingTicket` request: `{2: Ticket{2 config, 4 repeated Player}}`.
pub struct TicketRequest {
    pub mode: String,
    pub players: Vec<Vec<u8>>,
}

pub const CONFIG_PREFIX: &str = "tenants/current/matchmakingConfigs/";
pub const TICKET_PREFIX: &str = "tenants/current/matchmakingTickets/";

pub fn parse_ticket_request(buf: &[u8]) -> Option<TicketRequest> {
    let ticket = wire::bytes_field(buf, 2).ok()??;
    let config = wire::str_field(ticket, 2).ok()??;
    let mode = config.strip_prefix(CONFIG_PREFIX).filter(|m| !m.is_empty())?.to_owned();
    let players = wire::repeated_bytes(ticket, 4).ok()?.into_iter().map(<[u8]>::to_vec).collect();
    Some(TicketRequest { mode, players })
}

/// Ticket id from `{1 "tenants/current/matchmakingTickets/<uuid>"}` (Track / Cancel).
pub fn parse_ticket_name(buf: &[u8]) -> Option<uuid::Uuid> {
    let name = wire::str_field(buf, 1).ok()??;
    let (prefix, id) = name.rsplit_once('/')?;
    (prefix == TICKET_PREFIX.trim_end_matches('/')).then_some(())?;
    uuid::Uuid::parse_str(id).ok()
}

/// The client's Player (main+0x140d68c: 1 user, 2 attributes, 3 map, 4 string) with its real
/// user resource; the client sends `tenants/current/users/current`.
pub fn player_for(user: &str, player: &[u8]) -> Vec<u8> {
    // Every field but 1 is copied verbatim; an unparseable Player is dropped to the user alone.
    let rest = wire::raw_fields(player).unwrap_or_default();
    rest.into_iter()
        .filter(|(n, _)| *n != 1)
        .fold(Msg::new().bytes(1, user), |m, (_, raw)| m.raw(raw))
        .into_bytes()
}

/// One member of a match assignment.
pub struct Member<'a> {
    pub user: &'a str,
    pub player: &'a [u8],
}

/// `TrackMatchmakingTicket` reply announcing the session (docs/NETCODE.md):
///   reply main+0x1408a40: 1 ticket, 2 config, 4 Player, 5 state (3 = matched),
///                         6 MatchedPlayer*, 7 GameSession
///   MatchedPlayer main+0x140c708: 1 Player, 2 userSession, 3 user id
///   GameSession main+0x140b440: 1 name, 8 Gamesync host, 9 port, 10 Timestamp, 12 Member*
///   Member main+0x1405fec: 1 userSession, 2 user, 4 Timestamp
pub fn assignment(
    tenant: &str,
    lobby: &str,
    ticket: &str,
    mode: &str,
    recipient: &Member<'_>,
    members: &[Member<'_>],
    gamesync: (&str, u16),
    now: u64,
) -> Vec<u8> {
    let name = format!("{tenant}/gameSessions/{lobby}");
    let mut out = Msg::new()
        .bytes(1, format!("{tenant}/matchmakingTickets/{ticket}"))
        .bytes(2, format!("{tenant}/matchmakingConfigs/{mode}"))
        .bytes(4, player_for(recipient.user, recipient.player))
        .varint(5, 3);
    let mut session = Msg::new().bytes(1, &name).bytes(8, gamesync.0).varint(9, gamesync.1.into()).msg(10, &wire::seconds(now + 600));
    for m in members {
        let uid = crate::users::tail(m.user);
        let user_session = format!("{name}/userSessions/{uid}");
        out = out.msg(6, &Msg::new().bytes(1, player_for(m.user, m.player)).bytes(2, &user_session).bytes(3, uid));
        session = session.msg(12, &Msg::new().bytes(1, &user_session).bytes(2, m.user).msg(4, &wire::seconds(now)));
    }
    out.msg(7, &session).into_bytes()
}

// ---- GameSessionService: rooms, short aliases, friend sessions (docs/RE-MAP.md 3.2-3.5) ------

/// A GameSession as the room / friend-session RPCs show it (serializer main+0x140b440):
/// 1 name, 2 players, 3 capacity, 4 joinable, 5 visible, 6 "*" when password-protected (never
/// the password), 8/9 Gamesync endpoint, 10 Timestamp, 11 creator attributes, 12 Member*.
pub struct SessionView<'a> {
    pub tenant: &'a str,
    pub lobby: &'a str,
    pub players: usize,
    pub capacity: usize,
    pub joinable: bool,
    pub password: bool,
    /// Encoded `Attributes` message (GameSession field 11 as the creator sent it).
    pub attributes: &'a [u8],
    pub members: &'a [Member<'a>],
    pub gamesync: (&'a str, u16),
    pub now: u64,
}

pub fn session_name(tenant: &str, lobby: &str) -> String {
    format!("{tenant}/gameSessions/{lobby}")
}

pub fn game_session(v: &SessionView<'_>) -> Msg {
    let name = session_name(v.tenant, v.lobby);
    let mut m = Msg::new()
        .bytes(1, &name)
        .varint(2, v.players as u64)
        .varint(3, v.capacity as u64)
        .varint(4, u64::from(v.joinable))
        .varint(5, 1);
    if v.password {
        m = m.bytes(6, "*");
    }
    m = m.bytes(8, v.gamesync.0).varint(9, v.gamesync.1.into()).msg(10, &wire::seconds(v.now + 600));
    if !v.attributes.is_empty() {
        m = m.bytes(11, v.attributes);
    }
    for mem in v.members {
        let uid = crate::users::tail(mem.user);
        m = m.msg(12, &Msg::new().bytes(1, format!("{name}/userSessions/{uid}")).bytes(2, mem.user).msg(4, &wire::seconds(v.now)));
    }
    m
}

/// MatchedPlayer (main+0x140c708): `{1 Player, 2 userSession, 3 user id}`.
pub fn matched_player(session: &str, m: &Member<'_>) -> Msg {
    let uid = crate::users::tail(m.user);
    Msg::new().bytes(1, player_for(m.user, m.player)).bytes(2, format!("{session}/userSessions/{uid}")).bytes(3, uid)
}

/// `CreateGameSessionCreationTicket` request (FUN_0136d600):
/// `{1 parent, 2 GameSessionCreationTicket{2 config, 3 Player*, 6 GameSession{6 password, 11 attrs}}}`.
pub struct RoomRequest {
    pub mode: String,
    pub players: Vec<Vec<u8>>,
    pub password: String,
    pub attributes: Vec<u8>,
}

pub fn parse_room_request(buf: &[u8]) -> Option<RoomRequest> {
    let ticket = wire::bytes_field(buf, 2).ok()??;
    let config = wire::str_field(ticket, 2).ok()?.unwrap_or_default();
    let mode = config.strip_prefix(CONFIG_PREFIX).filter(|m| !m.is_empty()).unwrap_or("Private24").to_owned();
    let players = wire::repeated_bytes(ticket, 3).ok()?.into_iter().map(<[u8]>::to_vec).collect();
    let session = wire::bytes_field(ticket, 6).ok()?.unwrap_or_default();
    let password = wire::str_field(session, 6).ok()?.unwrap_or_default();
    let attributes = wire::bytes_field(session, 11).ok()?.unwrap_or_default().to_vec();
    Some(RoomRequest { mode, players, password, attributes })
}

pub const CREATION_TICKET_PREFIX: &str = "tenants/current/gameSessionCreationTickets/";
pub const ALIAS_PREFIX: &str = "tenants/current/GameSessionShortAliases/";

/// The last path component of field `number` (an id inside a resource name).
pub fn resource_id(buf: &[u8], number: u32) -> Option<String> {
    let name = wire::str_field(buf, number).ok()??;
    Some(crate::users::tail(&name).to_owned()).filter(|s| !s.is_empty())
}

/// Final `TrackGameSessionCreationTicket` message: state 2 (created) with the matched players
/// and the GameSession (consumer FUN_01395860).
pub fn room_created(tenant: &str, ticket: &str, mode: &str, matched: &[Msg], session: &Msg) -> Vec<u8> {
    let mut m = Msg::new()
        .bytes(1, format!("{tenant}/gameSessionCreationTickets/{ticket}"))
        .bytes(2, format!("{tenant}/matchmakingConfigs/{mode}"))
        .varint(4, 2);
    for p in matched {
        m = m.msg(5, p);
    }
    m.msg(6, session).into_bytes()
}

/// GameSessionShortAlias (main+0x1407230): `{1 alias name, 2 game session, 3 Timestamp}`.
pub fn short_alias(tenant: &str, alias: &str, lobby: &str, now: u64) -> Vec<u8> {
    Msg::new()
        .bytes(1, format!("{tenant}/GameSessionShortAliases/{alias}"))
        .bytes(2, session_name(tenant, lobby))
        .msg(3, &wire::seconds(now + 24 * 3600))
        .into_bytes()
}

/// `JoinGameSession` request: `{1 session name, 2 password, 3 Player*}`.
pub struct JoinRequest {
    pub lobby: String,
    pub password: String,
    pub players: Vec<Vec<u8>>,
}

pub fn parse_join(buf: &[u8]) -> Option<JoinRequest> {
    Some(JoinRequest {
        lobby: resource_id(buf, 1)?,
        password: wire::str_field(buf, 2).ok()?.unwrap_or_default(),
        players: wire::repeated_bytes(buf, 3).ok()?.into_iter().map(<[u8]>::to_vec).collect(),
    })
}

/// `BulkGetDocuments` request names (field 2, repeated).
pub fn bulk_get_names(buf: &[u8]) -> Result<Vec<String>> {
    Ok(wire::repeated_bytes(buf, 2)?.into_iter().map(|b| String::from_utf8_lossy(b).into_owned()).collect())
}

/// One BulkGetDocuments result: `{1: Document{1 name, 2 {Rating: 1500.0}}}` (the game reads
/// "Rating" at 0x49c54c; 1500 is its own default).
pub fn bulk_get_result(name: &str) -> Vec<u8> {
    let fields = Fields::new().with("Rating", Value::Double(1500.0));
    Msg::new().msg(1, &Msg::new().bytes(1, name).msg(2, &fields.encode())).into_bytes()
}

/// `WriteDocumentsResponse` (converter FUN_012a00a0): `{1 repeated WriteResult, 2 Timestamp}`.
/// WriteResult oneof: 1 = plain ack, 2 = transform results `{1 repeated Value}`. The game builds
/// one native write (set + transforms, FUN_01281500) that the SDK splits into an update and a
/// transform Write; results map back by native write index, so the transform values belong in
/// result 0. `writes` = (name, transform count) per request write.
pub fn write_response(writes: &[(String, usize)], value_of: impl Fn(&str) -> i64, now: u64) -> Vec<u8> {
    let mut out = Msg::new();
    if !writes.is_empty() {
        let total: usize = writes.iter().map(|(_, c)| c).sum();
        let name0 = writes.iter().find(|(_, c)| *c > 0).map_or(writes[0].0.as_str(), |(n, _)| n.as_str());
        let mut results = vec![(name0.to_owned(), total)];
        results.extend(writes[1..].iter().map(|(n, _)| (n.clone(), 0)));
        for (name, count) in results {
            let result = if count > 0 {
                let values = (0..count).fold(Msg::new(), |m, _| m.msg(1, &Value::Int(value_of(&name)).encode()));
                Msg::new().msg(2, &values)
            } else {
                Msg::new().bytes(1, b"")
            };
            out = out.msg(1, &result);
        }
    }
    out.msg(2, &wire::seconds(now)).into_bytes()
}

// ---- KeepUserSession (class 8, main+0x12a8064, both directions) -----------------------------

pub const SESSION: &str = "userSessions/current";

/// Op kinds the server sends (stream handler FUN_01271c00).
pub const OP_SUCCEEDED: u64 = 1;
pub const OP_CURRENT: u64 = 2;

/// A client listen: `Op{1: Target{1 "userSessions/current/targets/N", 2 {1 doc} | 3 {1 coll}}}`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Listen {
    pub target_id: u64,
    /// Document name, or collection name + "/".
    pub target: String,
}

/// A parsed client frame: `{1 session, 3 repeated Op, 4 repeated RemoveTarget{1 name}}`.
#[derive(Debug, Default)]
pub struct SessionFrame {
    pub session: String,
    pub listens: Vec<Listen>,
    pub removed: Vec<u64>,
}

fn target_id(name: &str) -> u64 {
    name.rsplit('/').next().and_then(|n| n.parse().ok()).unwrap_or(1)
}

pub fn parse_session_frame(buf: &[u8]) -> Result<SessionFrame> {
    let mut out = SessionFrame { session: wire::str_field(buf, 1)?.unwrap_or_else(|| SESSION.into()), ..Default::default() };
    for op in wire::repeated_bytes(buf, 3)? {
        let Some(target) = wire::bytes_field(op, 1)? else { continue };
        let name = wire::str_field(target, 1)?.unwrap_or_default();
        let mut doc = String::new();
        if let Some(d) = wire::bytes_field(target, 2)? {
            doc = wire::str_field(d, 1)?.unwrap_or_default();
        } else if let Some(c) = wire::bytes_field(target, 3)? {
            doc = format!("{}/", wire::str_field(c, 1)?.unwrap_or_default());
        }
        out.listens.push(Listen { target_id: target_id(&name), target: doc });
    }
    for r in wire::repeated_bytes(buf, 4)? {
        out.removed.push(target_id(&wire::str_field(r, 1)?.unwrap_or_default()));
    }
    Ok(out)
}

/// `{1 session, 2 Change{1 target id, 2 kind, 3 Document}}` (Change class 30, main+0x12b7d00).
/// REMOVED (kind 3) carries `Document{1 name}` only (FUN_01271c00 reads Change.3.Document.name).
pub fn change(session: &str, target_id: u64, kind: crate::docs::ChangeKind, doc: &Document) -> Vec<u8> {
    let document = match kind {
        crate::docs::ChangeKind::Removed => Msg::new().bytes(1, &doc.name),
        _ => doc.encode(),
    };
    let change = Msg::new().bytes(1, target_id.to_string()).varint(2, kind as u64).msg(3, &document);
    Msg::new().bytes(1, session).msg(2, &change).into_bytes()
}

/// `{1 session, 3 Op{1 decimal op id, 2 kind}}`: kind 2 = target in sync (runs the listener's
/// second callback), kind 1 completes the client's op future.
pub fn op(session: &str, target_id: u64, kind: u64) -> Vec<u8> {
    Msg::new().bytes(1, session).msg(3, &Msg::new().bytes(1, target_id.to_string()).varint(2, kind)).into_bytes()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn player_for_replaces_only_the_user() {
        let player = Msg::new().bytes(1, "tenants/current/users/current").bytes(2, "attrs").varint(5, 9).into_bytes();
        let out = player_for("tenants/t/users/u", &player);
        assert_eq!(out, Msg::new().bytes(1, "tenants/t/users/u").bytes(2, "attrs").varint(5, 9).into_bytes());
    }

    #[test]
    fn session_frames_parse_documents_collections_and_removals() {
        let listen = |id: u32, f: u32, n: &str| {
            Msg::new().msg(1, &Msg::new().bytes(1, format!("userSessions/current/targets/{id}")).msg(f, &Msg::new().bytes(1, n)))
        };
        let frame = Msg::new()
            .bytes(1, SESSION)
            .msg(3, &listen(1, 2, "docs/__us/abc"))
            .msg(3, &listen(2, 3, "docs/__us"))
            .msg(4, &Msg::new().bytes(1, "userSessions/current/targets/7"));
        let f = parse_session_frame(&frame.0).unwrap();
        assert_eq!(f.listens, vec![
            Listen { target_id: 1, target: "docs/__us/abc".into() },
            Listen { target_id: 2, target: "docs/__us/".into() },
        ]);
        assert_eq!(f.removed, vec![7]);
    }

    #[test]
    fn op_ack_matches_the_reference() {
        // session_message("userSessions/current", ops=op_ack(3, OP_CURRENT))
        let mut want = Msg::new().bytes(1, SESSION).into_bytes();
        want.extend(Msg::new().msg(3, &Msg::new().bytes(1, "3").varint(2, 2)).into_bytes());
        assert_eq!(op(SESSION, 3, OP_CURRENT), want);
    }

    #[test]
    fn write_response_puts_transforms_in_result_zero() {
        let w = vec![("docs/__pgn/All/__pus/u".to_string(), 0), ("docs/__pgn/All/__pus/u".to_string(), 1)];
        let out = write_response(&w, |_| 5, 9);
        let results = wire::repeated_bytes(&out, 1).unwrap();
        assert_eq!(results.len(), 2);
        assert!(wire::bytes_field(results[0], 2).unwrap().is_some());
        assert_eq!(results[1], Msg::new().bytes(1, b"").0.as_slice());
    }

    #[test]
    fn ticket_names_parse() {
        let id = uuid::Uuid::new_v4();
        let m = Msg::new().bytes(1, format!("{TICKET_PREFIX}{id}")).into_bytes();
        assert_eq!(parse_ticket_name(&m), Some(id));
        assert_eq!(parse_ticket_name(&Msg::new().bytes(1, format!("x/{id}")).0), None);
    }
}
