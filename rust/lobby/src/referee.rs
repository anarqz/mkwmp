//! The match referee (resources/docs/RE-MAP.md section 1 and "Q4 Referee"; live-verified 2026-11-13).
//!
//! - Every member writes `@@RefereeRequest/<ussid>/Request/<key>` (create) once per match, with
//!   the same `<key>`. The first one opens `@@RefereeResult/<uuid>` {request_id = key,
//!   status 0, result []}; the start waiter (FUN_02127388) needs exactly that within 36 s.
//! - Members report `@@RefereeReport/<uuid>/User/<ussid>` {result: [player index, ...]} (the
//!   cumulative finish/elimination order they saw), only while the result is at status 0.
//! - The result stays open, merging the reported order, until the order is complete AND every
//!   player has reported. Then status 1 + `ratings` {"<ussid>": number}. Finalizing on the first
//!   complete report breaks a slower device still a second from its own end (0x9b928162).
//! - A complete order whose remaining reporters are gone is finalized after [`GRACE_SECS`].
//! - A player who leaves the lobby mid-match is retired: no report is expected from them and
//!   their player index is placed after every finisher.

use std::collections::{BTreeSet, HashMap, HashSet};

use uuid::Uuid;

use crate::value::{Document, Fields, Value};

pub const REQUEST: &str = "docs/@@RefereeRequest/";
pub const RESULT: &str = "docs/@@RefereeResult/";
pub const REPORT: &str = "docs/@@RefereeReport/";
/// Seconds a complete result waits for the remaining players' reports.
pub const GRACE_SECS: f64 = 10.0;
/// Rating shown to every player (no rating changes on community servers).
pub const RATING: f64 = 3000.0;

#[derive(Debug)]
struct Match {
    key: String,
    /// Players in the match (`option.total_player_count`).
    total: usize,
    /// Finish / elimination order (0-based race-player indices).
    order: Vec<i64>,
    /// ussids that reported.
    reporters: HashSet<u32>,
    /// ussids of the match (request `users` / `ussid_map` values, else the lobby's members).
    players: BTreeSet<u32>,
    /// ussid -> race-player index, from the request's `ussid_map`.
    index_of: HashMap<u32, i64>,
    /// ussids that left mid-match.
    retired: BTreeSet<u32>,
    complete_at: Option<f64>,
    final_: bool,
    create_time: u64,
}

impl Match {
    fn retired_indices(&self) -> Vec<i64> {
        let mut out: Vec<i64> = self.retired.iter().filter_map(|u| self.index_of.get(u).copied()).collect();
        out.sort_unstable();
        out.retain(|i| !self.order.contains(i));
        out
    }

    /// Every player is either in the order or retired (a retired player without a known
    /// index still counts).
    fn complete(&self) -> bool {
        let retired_unplaced = self
            .retired
            .iter()
            .filter(|u| self.index_of.get(u).map_or(true, |i| !self.order.contains(i)))
            .count();
        self.order.len() + retired_unplaced >= self.total
    }

    fn reported_by_all(&self) -> bool {
        self.players.iter().filter(|u| !self.retired.contains(u)).all(|u| self.reporters.contains(u))
            && self.reporters.len() + self.retired.len() >= self.total
    }

    fn document(&self, name: &str, now: u64) -> Document {
        let mut order = self.order.clone();
        if self.final_ {
            order.extend(self.retired_indices());
        }
        let mut fields = Fields::new()
            .with("request_id", Value::str(&self.key))
            .with("status", Value::Int(i64::from(self.final_)))
            .with("result", Value::List(order.into_iter().map(Value::Int).collect()));
        if self.final_ {
            let ratings = self.players.iter().fold(Fields::new(), |f, u| f.with(&u.to_string(), Value::Double(RATING)));
            fields.set("ratings", Value::Map(ratings));
        }
        Document { name: name.to_owned(), fields, create_time: self.create_time, update_time: now }
    }
}

/// A result document to store and publish.
#[derive(Debug, Clone, PartialEq)]
pub struct Verdict {
    pub doc: Document,
    pub opened: bool,
    pub final_: bool,
}

/// One lobby's referee.
#[derive(Debug)]
pub struct Referee {
    /// Salt for result ids, so two lobbies never share a result document id.
    salt: String,
    matches: HashMap<String, Match>,
    answered: HashSet<String>,
}

impl Referee {
    pub fn new(salt: impl Into<String>) -> Self {
        Self { salt: salt.into(), matches: HashMap::new(), answered: HashSet::new() }
    }

    /// Any result not final yet.
    pub fn in_event(&self) -> bool {
        self.matches.values().any(|m| !m.final_)
    }

    /// A member wrote `name` with `fields`. `members` are the lobby's ussids (fallback for the
    /// player list). Returns the result document to publish, if any.
    pub fn on_write(&mut self, name: &str, fields: &Fields, members: &[u32], now: u64, mono: f64) -> Option<Verdict> {
        if let Some(rest) = name.strip_prefix(REQUEST) {
            return self.open(rest, fields, members, now);
        }
        if let Some(rest) = name.strip_prefix(REPORT) {
            return self.report(rest, fields, now, mono);
        }
        None
    }

    fn open(&mut self, rest: &str, fields: &Fields, members: &[u32], now: u64) -> Option<Verdict> {
        let parts: Vec<&str> = rest.split('/').collect();
        let [_, "Request", key] = parts[..] else { return None };
        if !self.answered.insert(key.to_owned()) {
            return None;
        }
        let option = fields.get("option").and_then(Value::entries);
        let total = option
            .and_then(|o| o.get("total_player_count"))
            .and_then(Value::as_int)
            .and_then(|n| usize::try_from(n).ok())
            .filter(|&n| n > 0)
            .unwrap_or(members.len().max(2));
        let mut index_of = HashMap::new();
        let mut players: BTreeSet<u32> = fields.get("users").map(Value::int_list).unwrap_or_default()
            .into_iter().filter_map(|u| u32::try_from(u).ok()).collect();
        if let Some(map) = fields.get("ussid_map").and_then(Value::entries) {
            for (index, ussid) in &map.0 {
                if let (Ok(i), Some(u)) = (index.parse::<i64>(), ussid.as_int().and_then(|u| u32::try_from(u).ok())) {
                    index_of.insert(u, i);
                    players.insert(u);
                }
            }
        }
        if players.is_empty() {
            players.extend(members.iter().copied());
        }
        let id = Uuid::new_v5(&Uuid::NAMESPACE_URL, format!("mkwmp-referee/{}/{key}", self.salt).as_bytes());
        let name = format!("{RESULT}{id}");
        let m = Match {
            key: key.to_owned(),
            total,
            order: Vec::new(),
            reporters: HashSet::new(),
            players,
            index_of,
            retired: BTreeSet::new(),
            complete_at: None,
            final_: false,
            create_time: now,
        };
        let doc = m.document(&name, now);
        self.matches.insert(name, m);
        Some(Verdict { doc, opened: true, final_: false })
    }

    fn report(&mut self, rest: &str, fields: &Fields, now: u64, mono: f64) -> Option<Verdict> {
        let parts: Vec<&str> = rest.split('/').collect();
        let [uuid, "User", ussid] = parts[..] else { return None };
        let name = format!("{RESULT}{uuid}");
        let m = self.matches.get_mut(&name).filter(|m| !m.final_)?;
        for index in fields.get("result").map(Value::int_list).unwrap_or_default() {
            if !m.order.contains(&index) {
                m.order.push(index);
            }
        }
        if let Ok(u) = ussid.parse::<u32>() {
            m.reporters.insert(u);
            m.players.insert(u);
        }
        Self::settle(m, mono);
        Some(Verdict { doc: m.document(&name, now), opened: false, final_: m.final_ })
    }

    fn settle(m: &mut Match, mono: f64) {
        if m.complete() {
            m.complete_at.get_or_insert(mono);
            m.final_ = m.reported_by_all();
        }
    }

    /// A member left the lobby: retire them from every open match.
    pub fn retire(&mut self, ussid: u32, now: u64, mono: f64) -> Vec<Verdict> {
        let mut out = Vec::new();
        for (name, m) in &mut self.matches {
            if m.final_ || !m.players.contains(&ussid) {
                continue;
            }
            m.retired.insert(ussid);
            Self::settle(m, mono);
            out.push(Verdict { doc: m.document(name, now), opened: false, final_: m.final_ });
        }
        out
    }

    /// Finalize complete results whose grace has passed.
    pub fn tick(&mut self, now: u64, mono: f64) -> Vec<Verdict> {
        let mut out = Vec::new();
        for (name, m) in &mut self.matches {
            if !m.final_ && m.complete_at.is_some_and(|t| mono - t >= GRACE_SECS) {
                m.final_ = true;
                out.push(Verdict { doc: m.document(name, now), opened: false, final_: true });
            }
        }
        out
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn request(total: i64, map: &[(i64, i64)]) -> Fields {
        Fields::new()
            .with("users", Value::List(map.iter().map(|(_, u)| Value::Int(*u)).collect()))
            .with("ussid_map", Value::Map(map.iter().fold(Fields::new(), |f, (i, u)| f.with(&i.to_string(), Value::Int(*u)))))
            .with("mode", Value::str("battle"))
            .with("option", Value::Map(Fields::new().with("total_player_count", Value::Int(total)).with("dropout_rank", Value::Int(-1))))
    }

    fn report(order: &[i64]) -> Fields {
        Fields::new().with("result", Value::List(order.iter().map(|i| Value::Int(*i)).collect()))
    }

    fn status(v: &Verdict) -> i64 {
        v.doc.fields.get("status").and_then(Value::as_int).unwrap()
    }

    fn open(r: &mut Referee) -> String {
        let v = r.on_write("docs/@@RefereeRequest/1/Request/k1", &request(2, &[(0, 1), (1, 2)]), &[1, 2], 0, 0.0).unwrap();
        assert!(v.opened);
        assert_eq!(status(&v), 0);
        assert_eq!(v.doc.fields.get("result"), Some(&Value::List(vec![])));
        // the second member's identical request is not answered again
        assert!(r.on_write("docs/@@RefereeRequest/2/Request/k1", &request(2, &[(0, 1), (1, 2)]), &[1, 2], 0, 0.0).is_none());
        v.doc.name.rsplit('/').next().unwrap().to_owned()
    }

    #[test]
    fn final_only_after_every_player_reported() {
        let mut r = Referee::new("lobby-a");
        let id = open(&mut r);
        // the faster device lists everyone first: still open
        let v = r.on_write(&format!("{REPORT}{id}/User/1"), &report(&[1, 0]), &[], 1, 1.0).unwrap();
        assert_eq!(status(&v), 0);
        assert_eq!(v.doc.fields.get("result"), Some(&Value::List(vec![Value::Int(1), Value::Int(0)])));
        let v = r.on_write(&format!("{REPORT}{id}/User/2"), &report(&[1]), &[], 2, 2.0).unwrap();
        assert_eq!(status(&v), 1);
        let ratings = v.doc.fields.get("ratings").and_then(Value::entries).unwrap();
        assert_eq!(ratings.get("1"), Some(&Value::Double(RATING)));
        assert_eq!(ratings.get("2"), Some(&Value::Double(RATING)));
        // final results take no more reports
        assert!(r.on_write(&format!("{REPORT}{id}/User/2"), &report(&[1, 0]), &[], 3, 3.0).is_none());
    }

    #[test]
    fn grace_finalizes_when_a_reporter_is_gone() {
        let mut r = Referee::new("lobby-a");
        let id = open(&mut r);
        r.on_write(&format!("{REPORT}{id}/User/1"), &report(&[1, 0]), &[], 1, 100.0).unwrap();
        assert!(r.tick(2, 105.0).is_empty());
        let v = r.tick(3, 110.5);
        assert_eq!(v.len(), 1);
        assert_eq!(status(&v[0]), 1);
        assert!(!r.in_event());
    }

    #[test]
    fn a_leaver_is_retired_and_placed_last() {
        let mut r = Referee::new("lobby-a");
        let id = open(&mut r);
        // player index 1 (ussid 2) leaves mid-race; ussid 1 finished first
        let v = r.retire(2, 1, 1.0);
        assert_eq!(status(&v[0]), 0);
        let v = r.on_write(&format!("{REPORT}{id}/User/1"), &report(&[0]), &[], 2, 2.0).unwrap();
        assert_eq!(status(&v), 1);
        assert_eq!(v.doc.fields.get("result"), Some(&Value::List(vec![Value::Int(0), Value::Int(1)])));
    }

    #[test]
    fn result_ids_differ_per_lobby() {
        let (mut a, mut b) = (Referee::new("a"), Referee::new("b"));
        let f = request(2, &[(0, 1), (1, 2)]);
        let na = a.on_write("docs/@@RefereeRequest/1/Request/k", &f, &[], 0, 0.0).unwrap().doc.name;
        let nb = b.on_write("docs/@@RefereeRequest/1/Request/k", &f, &[], 0, 0.0).unwrap().doc.name;
        assert_ne!(na, nb);
    }
}
