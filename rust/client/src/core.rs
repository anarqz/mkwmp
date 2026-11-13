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

use crate::api::{Api, ApiError, Friend};
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

#[derive(Debug, Clone)]
pub struct Ui {
    pub cfg: ClientConfig,
    pub register: RegisterView,
    /// GitHub stars of the project (None until fetched).
    pub stars: Option<u64>,
    pub server: ServerView,
    pub session: SessionView,
    pub friends: Vec<Friend>,
    pub update: String,
}

pub struct Core {
    pub config_path: String,
    token: String,
    seat: RwLock<Option<Arc<Seat>>>,
    ui: Mutex<Ui>,
    stop: Notify,
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
                friends: vec![],
                update: String::new(),
            }),
            stop: Notify::new(),
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
                u.friends.clear();
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
        let seat = Arc::new(Seat::new(&cfg, &self.config_path));
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
                u.friends = snap.friends.clone();
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
