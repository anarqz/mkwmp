//! The launcher's engine, shared by the window and `--headless`: settings,
//! the server's public status, the PLAY session (seat + game), and updates.
//! The window only reads [`Core::ui`] and calls [`Core::play`] / [`Core::stop`]
//! / [`Core::set_config`].

use std::path::PathBuf;
use std::process::Child;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use rand::RngCore;
use tokio::sync::{Notify, RwLock};

use crate::api::{Api, ApiError, Friends};
use crate::config::{ClientConfig, SeatMode};
use crate::log::note;
use crate::seat::{Phase, Seat};
use crate::{launch, probe, update};

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ServerView {
    Checking,
    Online { name: String, seats_total: usize, seated: usize, queued: usize },
    Offline(String),
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SessionView {
    Idle,
    Starting,
    Running { phase: Phase, game_running: bool, steam: bool },
    Failed(String),
}

/// The REGISTER button's last outcome.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RegisterView {
    Idle,
    Working,
    /// The name is registered already: if it's yours, enter its PIN.
    Taken,
    Failed(String),
}

/// The FRIENDS card: the list (None until the first answer), a one-line
/// note (the last action's outcome or a problem), and whether an action is
/// on its way.
#[derive(Debug, Clone, Default)]
pub struct FriendsUi {
    pub list: Option<Friends>,
    pub note: Option<(String, bool)>, // (text, is_error)
    pub working: bool,
}

#[derive(Debug, Clone)]
pub struct Ui {
    pub cfg: ClientConfig,
    pub register: RegisterView,
    /// GitHub stars of the project (None until fetched).
    pub stars: Option<u64>,
    pub server: ServerView,
    pub session: SessionView,
    pub friends: FriendsUi,
    pub update: String,
}

/// Who the presence token belongs to: a change signs in again.
type Ident = (String, String, String); // (server, name key, pin)

pub struct Core {
    pub config_path: String,
    token: String,
    seat: RwLock<Option<Arc<Seat>>>,
    ui: Mutex<Ui>,
    stop: Notify,
    /// The launcher's presence on the server (friends API), per identity.
    presence: Mutex<Option<(Ident, String)>>,
    /// Wakes the friends loop (an action, or the name/PIN changed).
    friends_wake: Notify,
}

impl Core {
    pub fn new(config_path: String, cfg: ClientConfig) -> Arc<Self> {
        let mut token = [0u8; 32];
        rand::rngs::OsRng.fill_bytes(&mut token);
        Arc::new(Self {
            config_path,
            token: token.iter().map(|b| format!("{b:02x}")).collect(),
            seat: RwLock::new(None),
            ui: Mutex::new(Ui {
                cfg,
                register: RegisterView::Idle,
                stars: None,
                server: ServerView::Checking,
                session: SessionView::Idle,
                friends: FriendsUi::default(),
                update: String::new(),
            }),
            stop: Notify::new(),
            presence: Mutex::new(None),
            friends_wake: Notify::new(),
        })
    }

    pub fn ui(&self) -> Ui {
        self.ui.lock().unwrap().clone()
    }

    pub fn token(&self) -> &str {
        &self.token
    }

    /// The live seat, for the local API (`None` until PLAY).
    pub async fn seat(&self) -> Option<Arc<Seat>> {
        self.seat.read().await.clone()
    }

    fn set<F: FnOnce(&mut Ui)>(&self, f: F) {
        f(&mut self.ui.lock().unwrap());
    }

    /// Save the window's edits. Friends edits reach a running seat through the
    /// file (the heartbeat re-reads it).
    pub fn set_config(&self, cfg: ClientConfig) -> Result<(), String> {
        cfg.save(&self.config_path)?;
        self.set(|u| u.cfg = cfg);
        self.friends_wake.notify_one();
        Ok(())
    }

    fn api(&self) -> Api {
        let server = self.ui().cfg.server;
        let base = if server.contains("://") { server } else { format!("http://{server}") };
        Api::new(base.trim_end_matches('/'))
    }

    /// REGISTER: ask the server for the configured name. Success saves the
    /// PIN to client.toml; a taken name asks for its PIN instead.
    pub fn register(self: &Arc<Self>) {
        self.set(|u| u.register = RegisterView::Working);
        let core = self.clone();
        tokio::spawn(async move {
            let name = core.ui().cfg.name;
            let outcome = match core.api().register(&name).await {
                Ok(pin) => {
                    let mut cfg = core.ui().cfg;
                    cfg.pin = pin;
                    match core.set_config(cfg) {
                        Ok(()) => {
                            note(format!("registered {name}"));
                            RegisterView::Idle
                        }
                        Err(e) => RegisterView::Failed(e),
                    }
                }
                Err(ApiError::Taken) => RegisterView::Taken,
                Err(e) => RegisterView::Failed(e.to_string()),
            };
            core.set(|u| u.register = outcome);
        });
    }

    /// Delete the configured profile from the server; on success the PIN is
    /// cleared so the name (or another) can be registered again.
    pub fn delete_profile(self: &Arc<Self>) {
        self.set(|u| u.register = RegisterView::Working);
        let core = self.clone();
        tokio::spawn(async move {
            let cfg = core.ui().cfg;
            let outcome = match core.api().delete_profile(&cfg.name, &cfg.pin).await {
                Ok(()) => {
                    let mut c = cfg.clone();
                    c.pin.clear();
                    match core.set_config(c) {
                        Ok(()) => {
                            note(format!("deleted the profile {}", cfg.name));
                            RegisterView::Idle
                        }
                        Err(e) => RegisterView::Failed(e),
                    }
                }
                Err(e) => RegisterView::Failed(e.to_string()),
            };
            core.set(|u| u.register = outcome);
        });
    }

    /// Forget the last REGISTER outcome (the name changed).
    pub fn reset_register(&self) {
        self.set(|u| u.register = RegisterView::Idle);
    }

    pub fn busy(&self) -> bool {
        !matches!(self.ui().session, SessionView::Idle | SessionView::Failed(_))
    }

    /// PLAY: take a seat (or queue), write mkwmp.toml, start the game.
    pub fn play(self: &Arc<Self>) {
        if self.busy() {
            return;
        }
        self.set(|u| u.session = SessionView::Starting);
        let core = self.clone();
        tokio::spawn(async move {
            let result = core.session().await;
            *core.seat.write().await = None;
            core.set(|u| {
                u.session = match result {
                    Ok(()) => SessionView::Idle,
                    Err(e) => {
                        note(e.clone());
                        SessionView::Failed(e)
                    }
                }
            });
        });
    }

    /// STOP: free the seat (the game, if running, is left alone).
    pub fn stop(&self) {
        self.stop.notify_one();
    }

    async fn session(self: &Arc<Self>) -> Result<(), String> {
        let cfg = self.ui().cfg;
        if let Some(p) = cfg.problem() {
            return Err(p);
        }
        let seat = Arc::new(Seat::new(&cfg));
        let lobby = seat.api().lobby_endpoint().await.map_err(|e| e.to_string())?;
        if cfg.update {
            sync_probe(&cfg).await;
        }
        if let Some(msg) = probe::ensure_host(&cfg.game) {
            note(format!("winmm_host.dll: {msg}"));
        }
        let addr = format!("127.0.0.1:{}", cfg.local_port);
        let write = |ticket: Option<&str>, lobby: &str| {
            let toml = launch::probe_toml(
                &cfg,
                &launch::ProbeConfig { lobby, launcher: &addr, launcher_token: &self.token, ticket },
            );
            launch::write_probe_toml(&cfg.game, &toml).map_err(|e| format!("writing mkwmp.toml: {e}"))
        };
        write(None, &lobby)?;
        *self.seat.write().await = Some(seat.clone());
        note(match cfg.seat {
            SeatMode::Start => format!("taking a seat on {}", cfg.server),
            SeatMode::Online => "you'll get a seat when you go online in the game".into(),
        });

        let mut game: Option<Child> = None;
        if cfg.launch {
            note("starting the game");
            game = Some(launch::spawn_game(&cfg.game).map_err(|e| format!("starting mkw.exe: {e}"))?);
        } else {
            note("ready: start the game from Steam");
        }

        loop {
            if let Some((ticket, seated_lobby)) = seat.tick().await {
                let t = (cfg.seat == SeatMode::Start).then_some(ticket.as_str());
                write(t, &seated_lobby)?;
            }
            let snap = seat.snapshot().await;
            let game_running = match game.as_mut() {
                Some(child) => match child.try_wait() {
                    Ok(Some(status)) => {
                        note(format!("game exited ({status}); seat released"));
                        seat.release().await;
                        return Ok(());
                    }
                    _ => true,
                },
                None => probe::game_running(),
            };
            self.set(|u| {
                u.session = SessionView::Running { phase: snap.phase.clone(), game_running, steam: !cfg.launch };
            });
            tokio::select! {
                _ = tokio::time::sleep(Duration::from_secs(1)) => {}
                _ = self.stop.notified() => {
                    note("stopped; seat released");
                    seat.release().await;
                    return Ok(());
                }
            }
        }
    }

    fn ident(cfg: &ClientConfig) -> Option<Ident> {
        (crate::config::valid_name(&cfg.name) && cfg.pin.len() == 6)
            .then(|| (cfg.server.clone(), cfg.name.to_ascii_lowercase(), cfg.pin.clone()))
    }

    /// The presence token for the current name + PIN, signing in if needed.
    async fn presence_token(&self) -> Result<String, ApiError> {
        let id = Self::ident(&self.ui().cfg).ok_or_else(|| ApiError::Refused("register your gamertag first".into()))?;
        if let Some((who, t)) = self.presence.lock().unwrap().clone() {
            if who == id {
                return Ok(t);
            }
        }
        let t = self.api().presence(&id.1, &id.2).await?;
        *self.presence.lock().unwrap() = Some((id, t.clone()));
        Ok(t)
    }

    /// Keeps the FRIENDS card current while the launcher runs (window,
    /// tray or headless): a long-poll that returns on any change. Holds no
    /// seat.
    pub async fn friends_loop(self: Arc<Self>) {
        let mut cursor: Option<(Ident, u64)> = None;
        loop {
            let id = Self::ident(&self.ui().cfg);
            let Some(id) = id else {
                self.set(|u| u.friends.list = None);
                cursor = None;
                tokio::select! {
                    _ = self.friends_wake.notified() => {}
                    _ = tokio::time::sleep(Duration::from_secs(5)) => {}
                }
                continue;
            };
            if cursor.as_ref().is_some_and(|(who, _)| *who != id) {
                cursor = None;
                self.set(|u| u.friends.list = None);
            }
            let token = match self.presence_token().await {
                Ok(t) => t,
                Err(e) => {
                    self.friends_problem(&e);
                    let wait = match e {
                        ApiError::Limited(s) => s.min(900),
                        ApiError::Refused(_) => 60,
                        _ => 5,
                    };
                    tokio::select! {
                        _ = self.friends_wake.notified() => {}
                        _ = tokio::time::sleep(Duration::from_secs(wait)) => {}
                    }
                    continue;
                }
            };
            let since = cursor.as_ref().map(|(_, c)| *c);
            let api = self.api();
            let res = tokio::select! {
                r = api.friends(&token, since) => Some(r),
                _ = self.friends_wake.notified() => None,
            };
            match res {
                None => cursor = None, // something changed here: fetch now
                Some(Ok(f)) => {
                    cursor = Some((id, f.cursor));
                    self.set(|u| {
                        if matches!(&u.friends.note, Some((_, true))) && !u.friends.working {
                            u.friends.note = None; // a problem that went away
                        }
                        u.friends.list = Some(f);
                    });
                }
                Some(Err(ApiError::SignedOut)) => {
                    *self.presence.lock().unwrap() = None;
                    cursor = None;
                }
                Some(Err(e)) => {
                    self.friends_problem(&e);
                    cursor = None;
                    let wait = if let ApiError::Limited(s) = e { s.min(900) } else { 5 };
                    tokio::time::sleep(Duration::from_secs(wait)).await;
                }
            }
        }
    }

    fn friends_problem(&self, e: &ApiError) {
        let msg = match e {
            ApiError::Transient(_) => "can't reach the server right now".to_string(),
            other => other.to_string(),
        };
        self.set(|u| u.friends.note = Some((msg, true)));
    }

    /// One friends action (add, accept, reject/cancel/remove), then refresh.
    fn friend_action<F, Fut>(self: &Arc<Self>, run: F)
    where
        F: FnOnce(Api, String) -> Fut + Send + 'static,
        Fut: std::future::Future<Output = Result<String, ApiError>> + Send,
    {
        self.set(|u| u.friends.working = true);
        let core = self.clone();
        tokio::spawn(async move {
            let mut outcome = match core.presence_token().await {
                Ok(t) => run(core.api(), t).await,
                Err(e) => Err(e),
            };
            if matches!(outcome, Err(ApiError::SignedOut)) {
                *core.presence.lock().unwrap() = None;
                outcome = Err(ApiError::Transient("signed out; try again".into()));
            }
            let note = match outcome {
                Ok(m) => (m, false),
                Err(ApiError::Transient(_)) => ("can't reach the server right now".into(), true),
                Err(e) => (e.to_string(), true),
            };
            core.set(|u| {
                u.friends.working = false;
                u.friends.note = Some(note);
            });
            core.friends_wake.notify_one();
        });
    }

    pub fn add_friend(self: &Arc<Self>, name: String) {
        self.friend_action(move |api, t| async move {
            let (name, state) = api.friend_request(&t, &name).await?;
            Ok(if state == "accepted" { format!("You and {name} are friends now.") } else { format!("Request sent to {name}.") })
        });
    }

    pub fn accept_friend(self: &Arc<Self>, name: String) {
        self.friend_action(move |api, t| async move {
            api.friend_accept(&t, &name).await?;
            Ok(format!("You and {name} are friends now."))
        });
    }

    /// Reject a request, cancel yours, or remove a friend.
    pub fn remove_friend(self: &Arc<Self>, name: String, what: &'static str) {
        self.friend_action(move |api, t| async move {
            api.friend_remove(&t, &name).await?;
            Ok(match what {
                "reject" => format!("Request from {name} declined."),
                "cancel" => format!("Request to {name} cancelled."),
                _ => format!("{name} removed from your friends."),
            })
        });
    }

    /// Leaving: friends see this launcher offline at once.
    pub async fn sign_out(&self) {
        let t = self.presence.lock().unwrap().take();
        if let Some((_, token)) = t {
            self.api().presence_end(&token).await;
        }
    }

    /// The server's public status, every 5 s, for whatever server is set.
    pub async fn status_loop(self: Arc<Self>) {
        loop {
            let server = self.ui().cfg.server.clone();
            let base = if server.contains("://") { server } else { format!("http://{server}") };
            let view = match Api::new(base.trim_end_matches('/')).status().await {
                Ok(s) => ServerView::Online { name: s.name, seats_total: s.seats_total, seated: s.seated, queued: s.queued },
                Err(e) => ServerView::Offline(e.to_string()),
            };
            self.set(|u| u.server = view);
            tokio::time::sleep(Duration::from_secs(5)).await;
        }
    }

    /// The project's GitHub star count, every 10 minutes.
    pub async fn stars_loop(self: Arc<Self>) {
        let http = reqwest::Client::builder()
            .user_agent(concat!("mkwmp-launcher/", env!("CARGO_PKG_VERSION")))
            .timeout(Duration::from_secs(10))
            .build()
            .expect("http client");
        loop {
            let url = format!("https://api.github.com/repos/{}", update::REPO);
            if let Ok(res) = http.get(url).send().await {
                if let Ok(v) = res.json::<serde_json::Value>().await {
                    if let Some(n) = v["stargazers_count"].as_u64() {
                        self.set(|u| u.stars = Some(n));
                    }
                }
            }
            tokio::time::sleep(Duration::from_secs(600)).await;
        }
    }

    /// Startup updates. Returns the new launcher to restart into, if any.
    pub async fn updates(self: Arc<Self>) -> Option<PathBuf> {
        let cfg = self.ui().cfg;
        if !cfg.update {
            self.set(|u| u.update = "updates off".into());
            return None;
        }
        self.set(|u| u.update = "checking for updates…".into());
        let u = update::Updater::new();
        if std::env::var_os("MKWMP_UPDATED").is_none() {
            match update::self_update(&u).await {
                Ok(Some(exe)) => {
                    self.set(|ui| ui.update = "updated; restarting…".into());
                    return Some(exe);
                }
                Ok(None) => {}
                Err(e) => note(format!("update check failed: {e}")),
            }
        }
        if cfg.game.join("mkw.exe").exists() {
            sync_probe(&cfg).await;
        }
        self.set(|ui| ui.update = "up to date".into());
        None
    }
}

/// Bring the game patch in line with this launcher's release. Never fatal.
pub async fn sync_probe(cfg: &ClientConfig) {
    let u = update::Updater::new();
    match update::own_release(&u).await {
        Ok((r, sums)) => match u.asset(&r, &sums, update::PROBE_ASSET).await {
            Ok(bytes) => match probe::install_probe(&cfg.game, &bytes, probe::game_running()) {
                Ok(probe::ProbeAction::UpToDate) => {}
                Ok(probe::ProbeAction::Installed { backup }) => {
                    note(format!("installed the game patch {}", r.tag_name));
                    if let Some(b) = backup {
                        note(format!("your previous winmm.dll is saved as {}", b.display()));
                    }
                }
                Ok(probe::ProbeAction::GameRunning) => note("a game patch update is ready; close the game first"),
                Err(e) => note(format!("installing the game patch failed: {e}")),
            },
            Err(e) => note(format!("game patch download failed: {e}")),
        },
        Err(e) => note(format!("game patch check failed: {e}")),
    }
}
