//! Groups: friends who go into matchmaking together, a leader and its followers (the game's
//! "Companion" flow, research/docs/AUTH-REPLIES-1.8.0.md §3 and RE-MAP's Companion row).
//!
//! The game's flow as far as it is known: a follower asks for a delegation token for its leader
//! (GameSessionService/IssueUserDelegationToken), publishes it in its Gamesync `Companion` document
//! (`companion_state` 3, `leader_user_id`, `delegation_token`), and the leader's matchmaking carries
//! the followers' tokens in its game-supplied string lists (CreateMatchmakingTicket field 3,
//! JoinGameSession field 4, CreateGameSessionCreationTicket field 3). Which of these the game really
//! uses in 1.8.0 isn't traced, so any of them binds a follower to its leader here, and the lobby
//! keeps a bound group together whatever order its members search in.

use std::collections::HashMap;

use uuid::Uuid;

/// A delegation token lives this long (the game asks again before it expires).
pub const DELEGATION_TTL_SECS: u64 = 12 * 3600;
/// A follower bond nobody refreshed for this long is forgotten.
pub const BOND_TTL_SECS: f64 = 4.0 * 3600.0;
/// Tokens kept at once (a client could ask in a loop; the oldest go first).
const MAX_DELEGATIONS: usize = 4096;
pub const TOKEN_PREFIX: &str = "mkwmp-delegation-";

#[derive(Clone, Debug)]
struct Delegation {
    /// Who asked (the follower).
    follower: String,
    /// The other user the request named, if it named one (the leader).
    leader: Option<String>,
    at: f64,
}

#[derive(Clone, Debug, PartialEq)]
pub struct Bond {
    pub leader: String,
    /// What bound it: "delegation", "companion" or "ticket".
    pub via: &'static str,
    pub at: f64,
}

#[derive(Default)]
pub struct Groups {
    delegations: HashMap<String, Delegation>,
    /// follower uid -> its leader.
    bonds: HashMap<String, Bond>,
}

impl Groups {
    /// A new delegation token for `follower` (to follow `leader`, when the request named one).
    /// Naming a leader binds the follower to it at once.
    pub fn issue(&mut self, follower: &str, leader: Option<&str>, now: f64) -> String {
        if self.delegations.len() >= MAX_DELEGATIONS {
            if let Some(oldest) = self.delegations.iter().min_by(|a, b| a.1.at.total_cmp(&b.1.at)).map(|(k, _)| k.clone()) {
                self.delegations.remove(&oldest);
            }
        }
        let token = format!("{TOKEN_PREFIX}{}", Uuid::new_v4());
        let leader = leader.filter(|l| *l != follower).map(str::to_owned);
        if let Some(l) = &leader {
            self.bind(follower, l, "delegation", now);
        }
        self.delegations.insert(token.clone(), Delegation { follower: follower.to_owned(), leader, at: now });
        token
    }

    /// `follower` follows `leader`. Returns true when that is new (for the log).
    pub fn bind(&mut self, follower: &str, leader: &str, via: &'static str, now: f64) -> bool {
        if follower == leader || follower.is_empty() || leader.is_empty() {
            return false;
        }
        // A leader can't follow its own follower: the newer bond wins.
        if self.bonds.get(leader).is_some_and(|b| b.leader == follower) {
            self.bonds.remove(leader);
        }
        let new = self.bonds.get(follower).map_or(true, |b| b.leader != leader);
        self.bonds.insert(follower.to_owned(), Bond { leader: leader.to_owned(), via, at: now });
        new
    }

    /// `uid` stopped following (its Companion says it's no follower any more).
    pub fn unbind(&mut self, uid: &str) -> Option<Bond> {
        self.bonds.remove(uid)
    }

    /// Our tokens in a game-supplied string list sent by `caller` (a leader's ticket or join):
    /// each binds its follower to the caller. Returns the followers bound.
    pub fn from_strings(&mut self, caller: &str, strings: &[String], now: f64) -> Vec<String> {
        let mut bound = Vec::new();
        for s in strings {
            let Some(d) = self.delegations.get(s.as_str()).cloned() else { continue };
            if d.follower != caller {
                self.bind(&d.follower, caller, "ticket", now);
                bound.push(d.follower);
            }
        }
        bound
    }

    /// The leader `uid` follows, if any.
    pub fn leader_of(&self, uid: &str, now: f64) -> Option<&Bond> {
        self.bonds.get(uid).filter(|b| now - b.at < BOND_TTL_SECS)
    }

    /// Everyone in `uid`'s group but `uid`: its leader (if it follows one) and that leader's other
    /// followers, or its own followers when it leads.
    pub fn others(&self, uid: &str, now: f64) -> Vec<String> {
        let leader = self.leader_of(uid, now).map_or(uid, |b| b.leader.as_str()).to_owned();
        let mut out: Vec<String> = self
            .bonds
            .iter()
            .filter(|(f, b)| b.leader == leader && now - b.at < BOND_TTL_SECS && f.as_str() != uid)
            .map(|(f, _)| f.clone())
            .collect();
        if leader != uid {
            out.push(leader);
        }
        out.sort();
        out
    }

    pub fn sweep(&mut self, now: f64) {
        self.bonds.retain(|_, b| now - b.at < BOND_TTL_SECS);
        self.delegations.retain(|_, d| now - d.at < DELEGATION_TTL_SECS as f64);
    }

    /// The follower a token was issued to (tests and logs).
    pub fn follower_of(&self, token: &str) -> Option<&str> {
        self.delegations.get(token).map(|d| d.follower.as_str())
    }

    /// The leader a token's request named, if any.
    pub fn named_leader(&self, token: &str) -> Option<&str> {
        self.delegations.get(token).and_then(|d| d.leader.as_deref())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn delegation_naming_the_leader_binds_and_groups_go_both_ways() {
        let mut g = Groups::default();
        let t = g.issue("bia", Some("ana"), 0.0);
        assert!(t.starts_with(TOKEN_PREFIX));
        assert_eq!(g.leader_of("bia", 1.0).map(|b| b.leader.as_str()), Some("ana"));
        g.bind("cris", "ana", "companion", 2.0);
        assert_eq!(g.others("ana", 3.0), vec!["bia", "cris"]);
        assert_eq!(g.others("bia", 3.0), vec!["ana", "cris"]);
        assert!(g.others("dan", 3.0).is_empty());
    }

    #[test]
    fn a_leaders_list_with_a_token_binds_its_follower() {
        let mut g = Groups::default();
        let t = g.issue("bia", None, 0.0);
        assert!(g.leader_of("bia", 1.0).is_none(), "no leader named yet");
        assert_eq!(g.from_strings("ana", &[String::from("x"), t.clone()], 2.0), vec!["bia"]);
        assert_eq!(g.leader_of("bia", 3.0).map(|b| (b.leader.as_str(), b.via)), Some(("ana", "ticket")));
        // Its own token in its own list binds nothing.
        assert!(g.from_strings("bia", &[t], 4.0).is_empty());
    }

    #[test]
    fn bonds_expire_unbind_and_never_loop() {
        let mut g = Groups::default();
        g.bind("bia", "ana", "companion", 0.0);
        g.bind("ana", "bia", "companion", 1.0);
        assert!(g.leader_of("bia", 2.0).is_none(), "the newer bond replaced the loop");
        assert_eq!(g.leader_of("ana", 2.0).map(|b| b.leader.as_str()), Some("bia"));
        assert!(g.leader_of("ana", 1.0 + BOND_TTL_SECS).is_none());
        g.bind("cris", "ana", "companion", 0.0);
        assert!(g.unbind("cris").is_some() && g.leader_of("cris", 1.0).is_none());
    }
}
