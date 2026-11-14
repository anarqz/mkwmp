//! The launcher's seat: one place that joins, heartbeats, rejoins after a
//! server restart, and (in `seat = "online"` mode)
//! lets the seat go once the game stops asking for it.
//!
//! The game reaches it through the local API ([`crate::local`]); `tick` is
//! called once a second by the main loop.

use std::time::{Duration, Instant};

use tokio::sync::Mutex;

use crate::api::{Api, ApiError, View};
use crate::config::{ClientConfig, SeatMode};

/// The game stops asking (left Online Play, quit) → the seat goes after this.
pub const IDLE_RELEASE: Duration = Duration::from_secs(90);

/// The seat as the window shows it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Phase {
    /// seat = "online": no seat until the game goes online.
    WaitingForGame,
    Joining,
    Queued(usize),
    Seated,
    Refused(String),
}

#[derive(Debug, Clone)]
pub struct Snapshot {
    pub phase: Phase,
}

/// What the local API answers the game.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Answer {
    Seated { ticket: String, lobby: String, name: String },
    Queued { position: usize, seats_total: usize },
    Refused(String),
    Unavailable(String),
}

struct Session {
    sid: String,
    key: String,
    boot: String,
}

struct State {
    session: Option<Session>,
    view: Option<View>,
    next_poll: Instant,
    last_want: Option<Instant>,
    refused: Option<String>,
    shown_position: Option<usize>,
}

pub struct Seat {
    api: Api,
    mode: SeatMode,
    name: String,
    pin: String,
    state: Mutex<State>,
}

/// A seated ticket changed (new seat, renewal, rejoin): the probe file must
/// be rewritten.
pub type TicketChanged = Option<(String, String)>; // (ticket, lobby)

impl Seat {
    pub fn new(cfg: &ClientConfig) -> Self {
        Self {
            api: Api::new(&cfg.server),
            mode: cfg.seat,
            name: cfg.name.clone(),
            pin: cfg.pin.clone(),
            state: Mutex::new(State {
                session: None,
                view: None,
                next_poll: Instant::now(),
                last_want: None,
                refused: None,
                shown_position: None,
            }),
        }
    }

    pub fn api(&self) -> &Api {
        &self.api
    }

    /// The game asks for a seat (POST /local/v1/seat). Joins if needed.
    pub async fn want(&self) -> Answer {
        let mut st = self.state.lock().await;
        st.last_want = Some(Instant::now());
        if let Some(msg) = &st.refused {
            return Answer::Refused(msg.clone());
        }
        if st.session.is_none() {
            if let Err(a) = self.join(&mut st).await {
                return a;
            }
        }
        Self::answer(&st)
    }

    fn answer(st: &State) -> Answer {
        match &st.view {
            Some(v) => match &v.ticket {
                Some(t) => Answer::Seated { ticket: t.token.clone(), lobby: t.lobby.clone(), name: v.name.clone() },
                None => Answer::Queued { position: v.position.unwrap_or(0), seats_total: v.seats_total },
            },
            None => Answer::Unavailable("not connected to the server yet".into()),
        }
    }

    async fn join(&self, st: &mut State) -> Result<(), Answer> {
        match self.api.join(&self.name, &self.pin).await {
            Ok(v) => {
                let key = v.session_key.clone().unwrap_or_default();
                st.session = Some(Session { sid: v.session_id.clone(), key, boot: v.boot_id.clone() });
                st.next_poll = Instant::now() + Duration::from_millis(v.next_poll_ms.clamp(1_000, 30_000));
                Self::show(st, &v);
                st.view = Some(v);
                Ok(())
            }
            Err(ApiError::Refused(m)) => {
                crate::log::note(format!("the server refused: {m}"));
                st.refused = Some(m.clone());
                Err(Answer::Refused(m))
            }
            Err(e) => Err(Answer::Unavailable(e.to_string())),
        }
    }

    /// Called about once a second. Returns a new ticket to write, if any.
    pub async fn tick(&self) -> TicketChanged {
        let mut st = self.state.lock().await;
        let before = st.view.as_ref().and_then(|v| v.ticket.as_ref()).map(|t| t.token.clone());

        let wanted = match self.mode {
            SeatMode::Start => st.refused.is_none(),
            SeatMode::Online => st.last_want.is_some_and(|t| t.elapsed() < IDLE_RELEASE),
        };
        if !wanted {
            if let Some(s) = st.session.take() {
                crate::log::note("the game stopped asking; releasing the seat");
                self.api.leave(&s.sid, &s.key).await;
                st.view = None;
            }
            return None;
        }
        if st.session.is_none() {
            if Instant::now() >= st.next_poll && self.join(&mut st).await.is_err() {
                st.next_poll = Instant::now() + Duration::from_secs(5);
            }
        } else if Instant::now() >= st.next_poll {
            self.heartbeat(&mut st).await;
        }

        let after = st.view.as_ref().and_then(|v| v.ticket.as_ref()).map(|t| (t.token.clone(), t.lobby.clone()));
        match after {
            Some((tok, lobby)) if Some(&tok) != before.as_ref() => Some((tok, lobby)),
            _ => None,
        }
    }

    async fn heartbeat(&self, st: &mut State) {
        let s = st.session.as_ref().expect("session");
        let (sid, key, boot) = (s.sid.clone(), s.key.clone(), s.boot.clone());
        let result = self.api.poll(&sid, &key).await;
        match result {
            Ok(v) if v.boot_id != boot => {
                crate::log::note("the server restarted; rejoining");
                st.session = None;
                st.view = None;
                st.next_poll = Instant::now();
            }
            Ok(v) => {
                st.next_poll = Instant::now() + Duration::from_millis(v.next_poll_ms.clamp(1_000, 30_000));
                Self::show(st, &v);
                st.view = Some(v);
            }
            Err(ApiError::Gone) => {
                crate::log::note("session expired; rejoining");
                st.session = None;
                st.view = None;
                st.next_poll = Instant::now();
            }
            Err(e) => {
                crate::log::note(format!("{e}; retrying"));
                st.next_poll = Instant::now() + Duration::from_secs(5);
            }
        }
    }

    fn show(st: &mut State, v: &View) {
        if v.state == "queued" && st.shown_position != v.position {
            let pos = v.position.unwrap_or(0);
            crate::log::note(format!(
                "server full ({}/{} seats); you are {} in queue",
                v.seats_total - v.seats_free,
                v.seats_total,
                ordinal(pos)
            ));
        }
        let was_seated = st.view.as_ref().is_some_and(|old| old.state == "seated");
        if v.state == "seated" && !was_seated {
            crate::log::note("seated");
        }
        st.shown_position = if v.state == "queued" { v.position } else { None };
    }

    /// What the window shows: the seat's state.
    pub async fn snapshot(&self) -> Snapshot {
        let st = self.state.lock().await;
        let phase = if let Some(m) = &st.refused {
            Phase::Refused(m.clone())
        } else {
            match &st.view {
                Some(v) if v.ticket.is_some() => Phase::Seated,
                Some(v) => Phase::Queued(v.position.unwrap_or(0)),
                None if st.session.is_none() && self.mode == SeatMode::Online => Phase::WaitingForGame,
                None => Phase::Joining,
            }
        };
        Snapshot { phase }
    }

    /// Free the seat (game exited, Ctrl-C).
    pub async fn release(&self) {
        let mut st = self.state.lock().await;
        if let Some(s) = st.session.take() {
            self.api.leave(&s.sid, &s.key).await;
        }
    }
}

fn ordinal(n: usize) -> String {
    let suffix = match (n % 10, n % 100) {
        (_, 11..=13) => "th",
        (1, _) => "st",
        (2, _) => "nd",
        (3, _) => "rd",
        _ => "th",
    };
    format!("{n}{suffix}")
}
