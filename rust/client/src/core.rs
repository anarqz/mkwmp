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

use crate::api::{Api, ApiError, Deleted, Friends};
use crate::config::{ClientConfig, SeatMode, Voice};
use crate::log::note;
use crate::seat::{Answer, Phase, Seat};
use crate::{launch, probe, update};

/// PLAY could not reach the server (the details go to the log).
pub const UNREACHABLE: &str = "Server unreachable: retry";

/// What PLAY shows for an API error.
fn reason(e: &ApiError) -> String {
    match e {
        ApiError::Transient(m) => {
            note(m.clone());
            UNREACHABLE.into()
        }
        e => e.to_string(),
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ServerView {
    Checking,
    /// Not asked: the player hasn't confirmed they're 18+ yet.
    NotAsked,
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
    /// The profile was deleted (or the server never had it): the launcher
    /// forgot it, and the GAMERTAG field waits for a new one.
    Deleted,
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

/// SETTINGS → REPORT VOICE ABUSE: this profile's report waiting for review
/// (None until asked), the last action's outcome, an action on its way.
#[derive(Debug, Clone, Default)]
pub struct ReportUi {
    pub mine: Option<Option<crate::api::Report>>,
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
    pub report: ReportUi,
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
    /// One config write at a time (the window and the local API both save).
    cfg_write: std::sync::Mutex<()>,
    /// The hashes this launcher's game patch must have, once known.
    expected: std::sync::Mutex<Option<probe::Expected>>,
    /// The game's local API (`127.0.0.1:<local_port>`) is listening in this process. While
    /// it isn't (another launcher holds the port), PLAY is refused: the game would reach
    /// the other launcher.
    pub local_api: std::sync::atomic::AtomicBool,
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
                report: ReportUi::default(),
                update: String::new(),
            }),
            stop: Notify::new(),
            presence: Mutex::new(None),
            friends_wake: Notify::new(),
            cfg_write: std::sync::Mutex::new(()),
            expected: std::sync::Mutex::new(None),
            local_api: std::sync::atomic::AtomicBool::new(false),
        })
    }

    pub fn ui(&self) -> Ui {
        self.ui.lock().unwrap().clone()
    }

    /// Learn the expected hashes and, when the patch is on, install it.
    /// `force`: install even with `patch` off (INSTALL). Never fatal to the
    /// caller beyond the returned error.
    pub async fn sync_patch(&self, force: bool) -> Result<(), String> {
        let cfg = self.ui().cfg;
        if !cfg.game.join("mkw.exe").exists() {
            return Ok(());
        }
        let b = patch_files(&cfg).await;
        if let Ok(b) = &b {
            *self.expected.lock().unwrap_or_else(|p| p.into_inner()) = Some(b.expected.clone());
        }
        let r = if let Some(dir) = crate::recomp::active_mod(&cfg.game).filter(|_| (cfg.patch || force) && !probe::dev_probe()) {
            // The mod wins: no launcher game patch beside mkw.exe.
            note(format!("game patch: MKWMP runs as a mod ({})", dir.display()));
            probe::remove_for_mod(&cfg.game, probe::game_running()).map(|done| {
                for d in done {
                    note(format!("game patch: {d} (the mod carries it)"));
                }
            })
        } else if probe::dev_probe() && !force {
            note(format!("game patch: {}=1, keeping the game folder's winmm.dll", probe::DEV_PROBE_ENV));
            Ok(())
        } else if cfg.patch || force {
            b.and_then(|b| install_bundle(&cfg, &b))
        } else {
            Ok(())
        };
        // The verdict, for bug reports.
        let report = self.patch_report();
        let rc = crate::recomp::identify(&cfg.game);
        note(format!("game: {}", rc.problem().unwrap_or_else(|| rc.explain())));
        note(crate::recomp::game_line(&cfg.game));
        note(format!("game patch: {} [{}]", report.summary(update::VERSION), report.files.join("; ")));
        r
    }

    /// What the game folder holds, checked by content against this
    /// launcher's signed hashes.
    pub fn patch_report(&self) -> probe::PatchReport {
        let game = self.ui().cfg.game;
        if let Some(dir) = crate::recomp::active_mod(&game).filter(|_| !probe::dev_probe()) {
            let mut files = vec![format!("mods/{}: enabled ({})", crate::recomp::MOD, dir.display())];
            if game.join(".mkwmp-probe").exists() || game.join(probe::VOICE).exists() {
                files.push("the launcher's game patch is still beside mkw.exe: close the game, it goes on the next PLAY".into());
            }
            return probe::PatchReport { state: probe::PatchState::Mod, files };
        }
        let expected = self.expected.lock().unwrap_or_else(|p| p.into_inner()).clone();
        probe::check(&game, expected.as_ref())
    }

    /// SETTINGS → GAME PATCH → INSTALL: turn the patch on, save, install.
    pub async fn patch_install(&self) -> Result<(), String> {
        let mut cfg = self.ui().cfg;
        cfg.patch = true;
        self.set_config(cfg)?;
        self.sync_patch(true).await?;
        let r = self.patch_report();
        if r.ok() {
            Ok(())
        } else {
            Err(format!("{}: {}", r.summary(update::VERSION), r.files.join("; ")))
        }
    }

    /// SETTINGS → GAME PATCH → UNINSTALL: turn the patch off (so nothing puts
    /// it back), save, and remove every mkwmp file from the game folder.
    pub async fn patch_uninstall(&self) -> Result<Vec<String>, String> {
        if self.seat().await.is_some() {
            return Err("Press STOP first".into());
        }
        let mut cfg = self.ui().cfg;
        cfg.patch = false;
        self.set_config(cfg.clone())?;
        let done = probe::uninstall(&cfg.game, probe::game_running())?;
        for line in &done {
            note(format!("uninstall: {line}"));
        }
        Ok(done)
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
    /// file (the heartbeat re-reads it). The voice lists the game owns (its
    /// devices, its mutes) are kept as they are now: change them with
    /// [`Core::edit_voice`].
    pub fn set_config(&self, mut cfg: ClientConfig) -> Result<(), String> {
        let _g = self.cfg_write.lock().unwrap_or_else(|p| p.into_inner());
        let now = self.ui().cfg.voice;
        cfg.voice.mutes = now.mutes;
        cfg.voice.inputs = now.inputs;
        cfg.voice.outputs = now.outputs;
        cfg.save(&self.config_path)?;
        self.set(|u| u.cfg = cfg);
        self.friends_wake.notify_one();
        Ok(())
    }

    /// Change the voice settings in place (the local API, an unmute in the
    /// window), saved at once when something changed.
    pub fn edit_voice<F: FnOnce(&mut Voice)>(&self, f: F) -> Result<(), String> {
        let _g = self.cfg_write.lock().unwrap_or_else(|p| p.into_inner());
        let mut cfg = self.ui().cfg;
        let before = cfg.voice.clone();
        f(&mut cfg.voice);
        if cfg.voice == before {
            return Ok(());
        }
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
        if !self.ui().cfg.adult_consent {
            self.set(|u| u.register = RegisterView::Failed(crate::api::NO_CONSENT.into()));
            return;
        }
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

    /// Delete the configured profile from the server. Deleted, or not on
    /// this server at all: the launcher forgets it ([`Core::forget_profile`]).
    /// A wrong PIN or server trouble keeps everything (the X stays, to retry).
    pub fn delete_profile(self: &Arc<Self>) {
        if self.busy() {
            self.set(|u| u.register = RegisterView::Failed("Stop playing before deleting the profile".into()));
            return;
        }
        self.set(|u| u.register = RegisterView::Working);
        let core = self.clone();
        tokio::spawn(async move {
            let cfg = core.ui().cfg;
            let outcome = match core.api().delete_profile(&cfg.name, &cfg.pin).await {
                Ok(how) => match core.forget_profile() {
                    Ok(()) => {
                        note(match how {
                            Deleted::Removed => format!("deleted the profile {}", cfg.name),
                            Deleted::Absent => format!("the server has no profile {}; forgot it", cfg.name),
                        });
                        RegisterView::Deleted
                    }
                    Err(e) => RegisterView::Failed(e),
                },
                Err(e) => RegisterView::Failed(e.to_string()),
            };
            core.set(|u| u.register = outcome);
        });
    }

    /// Forget the profile: name, PIN, its voice mutes, its friends and
    /// presence, and the ticket in the game's mkwmp.toml.
    fn forget_profile(&self) -> Result<(), String> {
        {
            let _g = self.cfg_write.lock().unwrap_or_else(|p| p.into_inner());
            let mut cfg = self.ui().cfg;
            cfg.name.clear();
            cfg.pin.clear();
            cfg.voice.mutes.clear();
            cfg.save(&self.config_path)?;
            if let Err(e) = launch::forget_ticket(&cfg.game) {
                note(format!("clearing the ticket in mkwmp.toml: {e}"));
            }
            self.set(|u| {
                u.cfg = cfg;
                u.friends = FriendsUi::default();
                u.report = ReportUi::default();
            });
        }
        *self.presence.lock().unwrap() = None;
        self.friends_wake.notify_one();
        Ok(())
    }

    /// The 18+ consent changed: sign in again (or stop being signed in).
    pub fn consent_changed(&self) {
        let t = self.presence.lock().unwrap().take();
        if let Some((_, token)) = t {
            let api = self.api();
            tokio::spawn(async move { api.presence_end(&token).await });
        }
        self.set(|u| {
            u.friends = FriendsUi::default();
            u.report = ReportUi::default();
            if matches!(&u.register, RegisterView::Failed(m) if m == crate::api::NO_CONSENT) {
                u.register = RegisterView::Idle;
            }
        });
        self.friends_wake.notify_one();
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

    /// Why PLAY can't start because of the local API; None when it is up.
    pub fn local_api_problem(&self) -> Option<String> {
        (!self.local_api.load(std::sync::atomic::Ordering::Relaxed))
            .then(|| format!("Another MKWMP launcher is running (port {} is in use): close it", self.ui().cfg.local_port))
    }

    async fn session(self: &Arc<Self>) -> Result<(), String> {
        let cfg = self.ui().cfg;
        if let Some(p) = cfg.problem() {
            return Err(p);
        }
        if let Some(p) = self.local_api_problem() {
            return Err(p);
        }
        // Before any patch write or network call: the patch's hooks only fit some game versions.
        note(crate::recomp::game_line(&cfg.game));
        if let Some(p) = crate::recomp::game_problem(&cfg.game) {
            return Err(p);
        }
        let seat = Arc::new(Seat::new(&cfg));
        let (lobby, lobby_ca, voice_port) = seat.api().lobby().await.map_err(|e| reason(&e))?;
        // Always the current server's: a stale file from another server
        // would make the game reject this one's lobby.
        launch::write_lobby_ca(&cfg.game, lobby_ca.as_deref()).map_err(|e| format!("writing {}: {e}", launch::LOBBY_CA))?;
        if let Err(e) = self.sync_patch(false).await {
            note(format!("game patch: {e}"));
        }
        let rc = crate::recomp::identify(&cfg.game);
        if !rc.ok() {
            return Err(format!("{} ({})", rc.problem().unwrap_or_default(), rc.explain()));
        }
        let patch = self.patch_report();
        if !patch.ok() {
            return Err(format!("{} ({})", patch.summary(update::VERSION), patch.files.join("; ")));
        }
        let addr = format!("127.0.0.1:{}", cfg.local_port);
        let write = |ticket: Option<&str>, lobby: &str| {
            let toml = launch::probe_toml(
                &cfg,
                &launch::ProbeConfig { lobby, launcher: &addr, launcher_token: &self.token, ticket, voice_port },
            );
            launch::write_probe_toml(&cfg.game, &toml).map_err(|e| format!("writing mkwmp.toml: {e}"))
        };
        write(None, &lobby)?; // never a stale ticket
        // Consent, and a profile the server deleted (a month unused) comes back
        // here, before the seat server is asked for a seat.
        self.presence_token().await.map_err(|e| reason(&e))?;
        *self.seat.write().await = Some(seat.clone());
        match cfg.seat {
            SeatMode::Start => {
                note(format!("taking a seat on {}", cfg.server));
                // The game starts only once seated.
                match self.take_seat(&seat, &cfg).await? {
                    Some((ticket, seated_lobby)) => {
                        write(Some(&ticket), &seated_lobby)?;
                    }
                    None => return Ok(()), // STOP while queued
                }
            }
            SeatMode::Online => {
                // No seat until the game goes online, but the profile must be good.
                self.presence_token().await.map_err(|e| reason(&e))?;
                note("you'll get a seat when you go online in the game");
            }
        }

        let mut game: Option<Child> = None;
        if cfg.launch {
            note("starting the game");
            game = Some(launch::spawn_game(&cfg.game, &cfg.launch_command).map_err(|e| format!("starting mkw.exe: {e}"))?);
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

    /// seat = "start": join and wait (queued) until seated. The seated
    /// ticket and lobby; None if STOP was pressed while waiting. A refusal or
    /// an unreachable server is an error (the game is not started).
    async fn take_seat(&self, seat: &Seat, cfg: &ClientConfig) -> Result<Option<(String, String)>, String> {
        match seat.want().await {
            Answer::Seated { ticket, lobby, .. } => return Ok(Some((ticket, lobby))),
            Answer::Refused(m) => return Err(m),
            Answer::Unavailable(m) => {
                note(format!("no seat: {m}"));
                if !matches!(seat.snapshot().await.phase, Phase::Busy(_)) {
                    return Err(UNREACHABLE.into());
                }
                // Busy (gamertag in use, queue full): wait here, retrying.
            }
            Answer::Queued { .. } => {}
        }
        loop {
            let changed = seat.tick().await;
            let snap = seat.snapshot().await;
            match (&snap.phase, changed) {
                (Phase::Refused(m), _) => return Err(m.clone()),
                (_, Some(t)) => return Ok(Some(t)),
                (Phase::Seated, None) => {
                    if let Answer::Seated { ticket, lobby, .. } = seat.want().await {
                        return Ok(Some((ticket, lobby)));
                    }
                }
                _ => {}
            }
            self.set(|u| u.session = SessionView::Running { phase: snap.phase, game_running: false, steam: !cfg.launch });
            tokio::select! {
                _ = tokio::time::sleep(Duration::from_secs(1)) => {}
                _ = self.stop.notified() => {
                    note("stopped; seat released");
                    seat.release().await;
                    return Ok(None);
                }
            }
        }
    }

    fn ident(cfg: &ClientConfig) -> Option<Ident> {
        (crate::config::valid_name(&cfg.name) && cfg.pin.len() == 6)
            .then(|| (cfg.server.clone(), cfg.name.to_ascii_lowercase(), cfg.pin.clone()))
    }

    /// The presence token for the current name + PIN, signing in if needed.
    /// A profile the server deleted (a month unused) is registered again with
    /// the saved PIN first. Nothing reaches the server before the player's
    /// consent (18+, privacy policy).
    async fn presence_token(&self) -> Result<String, ApiError> {
        let cfg = self.ui().cfg;
        if !cfg.adult_consent {
            return Err(ApiError::Refused(crate::api::NO_CONSENT.into()));
        }
        let id = Self::ident(&cfg).ok_or_else(|| ApiError::Refused("register your gamertag first".into()))?;
        if let Some((who, t)) = self.presence.lock().unwrap().clone() {
            if who == id {
                return Ok(t);
            }
        }
        let api = self.api();
        let mut id = id;
        let t = match api.presence(&id.1, &id.2).await {
            Err(ApiError::Refused(m)) if m == crate::api::NOT_REGISTERED => {
                match api.restore(&cfg.name, &id.2).await {
                    Ok(pin) if pin == id.2 => {
                        note(format!("{} had expired on the server; registered it again with your PIN", cfg.name))
                    }
                    Ok(pin) => {
                        // An older server picked its own PIN: keep that one, or nobody has it.
                        let mut c = self.ui().cfg;
                        c.pin = pin.clone();
                        self.set_config(c).map_err(ApiError::Refused)?;
                        note(format!("{} had expired on the server; registered it again (new PIN)", cfg.name));
                        id.2 = pin;
                    }
                    Err(ApiError::Taken) => {
                        return Err(ApiError::Refused(format!(
                            "{} expired on the server and someone else registered it: pick another gamertag",
                            cfg.name
                        )))
                    }
                    Err(e) => return Err(e),
                }
                api.presence(&id.1, &id.2).await?
            }
            other => other?,
        };
        *self.presence.lock().unwrap() = Some((id, t.clone()));
        Ok(t)
    }

    /// Ask the server for this profile's open report (SETTINGS opened).
    pub fn refresh_report(self: &Arc<Self>) {
        self.report_action(|api, t| async move { api.report(&t).await.map(|r| (Some(r), None)) });
    }

    /// Report `name`'s voice chat.
    pub fn file_report(self: &Arc<Self>, name: String, reason: &'static str, details: String) {
        self.report_action(move |api, t| async move {
            let r = api.report_file(&t, &name, reason, &details).await?;
            let msg = format!("Report about {} sent. The server's host will review it.", r.reported);
            Ok((Some(Some(r)), Some(msg)))
        });
    }

    /// Take back the report waiting for review.
    pub fn withdraw_report(self: &Arc<Self>) {
        self.report_action(|api, t| async move {
            api.report_withdraw(&t).await?;
            Ok((Some(None), Some("Report withdrawn.".to_string())))
        });
    }

    /// One report call; `run` returns the new open report (if it knows) and a note.
    fn report_action<F, Fut>(self: &Arc<Self>, run: F)
    where
        F: FnOnce(Api, String) -> Fut + Send + 'static,
        Fut: std::future::Future<Output = Result<(Option<Option<crate::api::Report>>, Option<String>), ApiError>> + Send,
    {
        self.set(|u| u.report.working = true);
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
            core.set(|u| {
                u.report.working = false;
                match outcome {
                    Ok((mine, msg)) => {
                        if let Some(m) = mine {
                            u.report.mine = Some(m);
                        }
                        u.report.note = msg.map(|m| (m, false));
                    }
                    Err(ApiError::Transient(_)) => u.report.note = Some(("can't reach the server right now".into(), true)),
                    Err(e) => u.report.note = Some((e.to_string(), true)),
                }
            });
        });
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
            if !self.ui().cfg.adult_consent {
                self.set(|u| u.server = ServerView::NotAsked);
                tokio::time::sleep(Duration::from_secs(1)).await;
                continue;
            }
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
            if let Err(e) = self.sync_patch(false).await {
                note(format!("game patch: {e}"));
            }
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
        if let Err(e) = self.sync_patch(false).await {
            note(format!("game patch: {e}"));
        }
        self.set(|ui| ui.update = "up to date".into());
        None
    }
}

/// The game patch files for this launcher's version and the hashes they must
/// have: from the release zip's `game/` folder when its signed list is for
/// this version, else (with updates on) downloaded from this launcher's own
/// release and checked against its signed SHA256SUMS.
async fn patch_files(cfg: &ClientConfig) -> Result<probe::Bundle, String> {
    if let Some(b) = probe::bundled() {
        return Ok(b);
    }
    if !cfg.update {
        return Err("no game/ folder next to the launcher, and updates are off".into());
    }
    let u = update::Updater::new();
    let (r, sums) = update::own_release(&u).await?;
    let expected = probe::Expected {
        version: update::VERSION.to_string(),
        probe: sums.0.get(update::PROBE_ASSET).cloned().ok_or("the release lists no game patch")?,
        voice: sums.0.get(update::VOICE_ASSET).cloned(),
    };
    let probe = u.asset(&r, &sums, update::PROBE_ASSET).await.map_err(|e| format!("game patch download failed: {e}"))?;
    let voice = match expected.voice {
        Some(_) => Some(u.asset(&r, &sums, update::VOICE_ASSET).await.map_err(|e| format!("voice chat download failed: {e}"))?),
        None => None,
    };
    Ok(probe::Bundle { expected, probe, voice })
}

/// Put `b` in the game folder. Notes what it did.
fn install_bundle(cfg: &ClientConfig, b: &probe::Bundle) -> Result<(), String> {
    let tag = format!("v{}", b.expected.version);
    let running = probe::game_running();
    match probe::install_probe(&cfg.game, &b.probe, running) {
        Ok(probe::ProbeAction::UpToDate) => {}
        Ok(probe::ProbeAction::Installed { backup }) => {
            note(format!("installed the game patch {tag}"));
            if let Some(bak) = backup {
                note(format!("your previous winmm.dll is saved as {}", bak.display()));
            }
        }
        Ok(probe::ProbeAction::GameRunning) => return Err("close the game first: a game patch update is ready".into()),
        Err(e) => return Err(format!("installing the game patch failed: {e}")),
    }
    if let Some(bytes) = &b.voice {
        match probe::install_file(&cfg.game, probe::VOICE, bytes, running) {
            Ok(probe::ProbeAction::Installed { .. }) => note(format!("installed voice chat {tag}")),
            Ok(probe::ProbeAction::GameRunning) => return Err("close the game first: a voice chat update is ready".into()),
            Ok(probe::ProbeAction::UpToDate) => {}
            Err(e) => return Err(format!("installing voice chat failed: {e}")),
        }
    }
    if let Some(msg) = probe::ensure_host(&cfg.game) {
        note(format!("winmm_host.dll: {msg}"));
    }
    Ok(())
}

#[cfg(test)]
mod tests;
