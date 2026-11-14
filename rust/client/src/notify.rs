//! System notifications for friends: "Ana is online", "Ana is playing",
//! "Bia wants to be your friend". Driven by the friends long-poll
//! ([`crate::core::Core::friends_loop`]): successive lists are compared, the
//! first list after start is only remembered (friends already online then
//! don't notify), and each (friend, kind) notifies at most once per
//! [`DEBOUNCE`].

use std::collections::{HashMap, HashSet};
use std::sync::Arc;
use std::time::{Duration, Instant};

use crate::api::Friends;
use crate::config::Background;
use crate::core::Core;

pub const DEBOUNCE: Duration = Duration::from_secs(5 * 60);
/// Windows: the toast sender identity (registered per user, see `register`).
#[cfg(windows)]
pub const APP_ID: &str = "anarqz.mkwmp.launcher";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Kind {
    Online,
    Playing,
    Request,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Event {
    pub kind: Kind,
    pub name: String,
}

impl Event {
    pub fn text(&self) -> (String, String) {
        match self.kind {
            Kind::Online => (format!("{} is online", self.name), "Your friend just opened MKWMP.".into()),
            Kind::Playing => (format!("{} is playing", self.name), "Your friend is in Mario Kart World online.".into()),
            Kind::Request => (
                format!("{} wants to be your friend", self.name),
                "Open MKWMP to accept or decline.".into(),
            ),
        }
    }
}

/// Remembers the last friends list and what was already notified.
#[derive(Default)]
pub struct Watch {
    /// name -> state ("playing" / "online" / "offline"); None before the first list.
    states: Option<HashMap<String, String>>,
    incoming: HashSet<String>,
    sent: HashMap<(String, Kind), Instant>,
}

impl Watch {
    /// The events between the previous list and `list`, filtered by the
    /// user's settings and the debounce.
    pub fn diff(&mut self, list: &Friends, prefs: &Background, now: Instant) -> Vec<Event> {
        let states: HashMap<String, String> = list.friends.iter().map(|f| (f.name.clone(), f.state.clone())).collect();
        let incoming: HashSet<String> = list.incoming.iter().map(|r| r.name.clone()).collect();
        let Some(prev) = self.states.replace(states.clone()) else {
            self.incoming = incoming;
            return Vec::new(); // the first list: only remember it
        };
        let mut out = Vec::new();
        for (name, state) in &states {
            let before = prev.get(name).map_or("offline", String::as_str);
            let kind = match (before, state.as_str()) {
                (b, "playing") if b != "playing" => Kind::Playing,
                ("offline", "online") => Kind::Online,
                _ => continue,
            };
            out.push(Event { kind, name: name.clone() });
        }
        for name in incoming.difference(&self.incoming) {
            out.push(Event { kind: Kind::Request, name: name.clone() });
        }
        self.incoming = incoming;
        out.retain(|e| match e.kind {
            Kind::Online => prefs.notify_online,
            Kind::Playing => prefs.notify_playing,
            Kind::Request => prefs.notify_requests,
        });
        out.retain(|e| {
            let key = (e.name.to_ascii_lowercase(), e.kind);
            let fresh = self.sent.get(&key).is_none_or(|t| now.duration_since(*t) >= DEBOUNCE);
            if fresh {
                self.sent.insert(key, now);
            }
            fresh
        });
        out.sort_by(|a, b| a.name.cmp(&b.name));
        out
    }
}

/// Show one system notification (never blocks the caller).
pub fn show(summary: String, body: String) {
    std::thread::spawn(move || {
        let mut n = notify_rust::Notification::new();
        n.appname("MKWMP").summary(&summary).body(&body);
        #[cfg(windows)]
        n.app_id(APP_ID);
        #[cfg(all(unix, not(target_os = "macos")))]
        n.icon("mkwmp-launcher");
        if let Err(e) = n.show() {
            crate::log::note(format!("notification failed: {e}"));
        }
    });
}

/// Watches the friends list while the launcher runs and notifies.
pub async fn watch_loop(core: Arc<Core>) {
    let mut watch = Watch::default();
    let mut last: Option<u64> = None;
    loop {
        tokio::time::sleep(Duration::from_secs(1)).await;
        let ui = core.ui();
        let Some(list) = ui.friends.list else {
            watch = Watch::default(); // signed out / another profile: start over
            last = None;
            continue;
        };
        if last == Some(list.cursor) {
            continue;
        }
        last = Some(list.cursor);
        for e in watch.diff(&list, &ui.cfg.background, Instant::now()) {
            let (s, b) = e.text();
            show(s, b);
        }
    }
}

/// Windows shows a toast only for a known sender: register ours per user
/// (HKCU\Software\Classes\AppUserModelId\<id>, with our name and icon).
#[cfg(windows)]
pub fn register() {
    use winreg::enums::HKEY_CURRENT_USER;
    let dir = std::env::var_os("LOCALAPPDATA").map(std::path::PathBuf::from).unwrap_or_else(std::env::temp_dir).join("mkwmp");
    let icon = dir.join("mkwmp.png");
    let _ = std::fs::create_dir_all(&dir).and_then(|_| std::fs::write(&icon, crate::icon::png(128)));
    let key = format!(r"Software\Classes\AppUserModelId\{APP_ID}");
    let res = winreg::RegKey::predef(HKEY_CURRENT_USER).create_subkey(&key).and_then(|(k, _)| {
        k.set_value("DisplayName", &"MKWMP")?;
        k.set_value("IconUri", &icon.display().to_string())
    });
    if let Err(e) = res {
        crate::log::note(format!("registering notifications failed: {e}"));
    }
}

#[cfg(not(windows))]
pub fn register() {}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::api::{Friend, Request};

    fn list(friends: &[(&str, &str)], incoming: &[&str], cursor: u64) -> Friends {
        Friends {
            friends: friends.iter().map(|(n, s)| Friend { name: (*n).into(), state: (*s).into() }).collect(),
            incoming: incoming.iter().map(|n| Request { name: (*n).into(), since: 0 }).collect(),
            outgoing: Vec::new(),
            cursor,
        }
    }

    #[test]
    fn first_list_is_quiet_then_changes_notify() {
        let p = Background::default();
        let mut w = Watch::default();
        let t = Instant::now();
        assert!(w.diff(&list(&[("Ana", "online"), ("Bia", "offline")], &["Cid"], 1), &p, t).is_empty());
        let got = w.diff(&list(&[("Ana", "playing"), ("Bia", "online")], &["Cid", "Dan"], 2), &p, t);
        assert_eq!(got, vec![
            Event { kind: Kind::Playing, name: "Ana".into() },
            Event { kind: Kind::Online, name: "Bia".into() },
            Event { kind: Kind::Request, name: "Dan".into() },
        ]);
        // going offline and back within the debounce: quiet
        w.diff(&list(&[("Ana", "playing"), ("Bia", "offline")], &[], 3), &p, t);
        assert!(w.diff(&list(&[("Ana", "playing"), ("Bia", "online")], &[], 4), &p, t + Duration::from_secs(60)).is_empty());
        w.diff(&list(&[("Ana", "playing"), ("Bia", "offline")], &[], 5), &p, t);
        assert_eq!(w.diff(&list(&[("Ana", "playing"), ("Bia", "online")], &[], 6), &p, t + DEBOUNCE).len(), 1);
    }

    #[test]
    fn settings_switch_kinds_off() {
        let p = Background { notify_online: false, ..Background::default() };
        let mut w = Watch::default();
        let t = Instant::now();
        w.diff(&list(&[("Ana", "offline")], &[], 1), &p, t);
        assert!(w.diff(&list(&[("Ana", "online")], &[], 2), &p, t).is_empty());
    }
}
