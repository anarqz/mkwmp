//! PLAY and profile delete against a fake seat server.

use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use axum::extract::{Path as UrlPath, State};
use axum::http::StatusCode;
use axum::response::{IntoResponse, Response};
use axum::routing::{delete, get, post};
use axum::{Json, Router};
use serde_json::{json, Value};

use super::*;
use crate::api::{IN_USE, NOT_REGISTERED, WRONG_PIN};
use crate::config::Voice;

/// How the fake server answers.
#[derive(Clone, Copy)]
enum Seat {
    Seated,
    /// Queued until this many polls, then seated.
    QueuedFor(usize),
    Refused(u16, &'static str),
    Down,
    /// 409 NAME_TAKEN for this many joins, then seated.
    TakenFor(usize),
    /// The server deleted the profile (a month unused): 404 until the
    /// launcher registers it again, then seated.
    Expired,
}

#[derive(Clone)]
struct Fake {
    seat: Seat,
    delete: (u16, &'static str),
    polls: Arc<AtomicUsize>,
    joins: Arc<AtomicUsize>,
    deletes: Arc<Mutex<Vec<String>>>,
    /// Bodies of POST /seat/v1/profiles.
    registers: Arc<Mutex<Vec<Value>>>,
    /// A server older than restore: ignores `pin`, answers this one.
    picks_pin: Option<&'static str>,
}

fn err(status: u16, code: &str) -> Response {
    let s = StatusCode::from_u16(status).unwrap();
    (s, Json(json!({"error": {"code": code, "message": format!("{code} from the fake")}}))).into_response()
}

fn view(state: &str, position: Option<usize>) -> Value {
    let ticket = (state == "seated").then(|| json!({"token": "mkw1.ticket.sig", "expiresAt": 9_999_999_999u64, "lobby": "lobby.test:8905"}));
    json!({
        "bootId": "b1", "sessionId": "s1", "name": "Ana", "sessionKey": "k1",
        "state": state, "position": position, "seatsTotal": 2, "seatsFree": 0,
        "nextPollMs": 1000, "ticket": ticket,
        "room": (state == "seated").then(|| json!({"lobby": "l-1", "ussid": 5, "ussids": [3, 5], "at": 1_795_000_000_000u64})),
    })
}

async fn join(State(f): State<Fake>) -> Response {
    let n = f.joins.fetch_add(1, Ordering::SeqCst) + 1;
    match f.seat {
        Seat::TakenFor(k) if n <= k => err(409, "NAME_TAKEN"),
        Seat::TakenFor(_) => Json(view("seated", None)).into_response(),
        Seat::Seated => Json(view("seated", None)).into_response(),
        Seat::QueuedFor(_) => Json(view("queued", Some(2))).into_response(),
        Seat::Refused(s, c) => err(s, c),
        Seat::Down => err(503, "UNAVAILABLE"),
        Seat::Expired => Json(view("seated", None)).into_response(),
    }
}

async fn poll(State(f): State<Fake>) -> Response {
    let n = f.polls.fetch_add(1, Ordering::SeqCst) + 1;
    match f.seat {
        Seat::QueuedFor(k) if n < k => Json(view("queued", Some(1))).into_response(),
        _ => Json(view("seated", None)).into_response(),
    }
}

async fn delete_profile(State(f): State<Fake>, UrlPath(name): UrlPath<String>) -> Response {
    f.deletes.lock().unwrap().push(name);
    match f.delete {
        (204, _) => StatusCode::NO_CONTENT.into_response(),
        (s, c) => err(s, c),
    }
}

async fn register(State(f): State<Fake>, Json(b): Json<Value>) -> Response {
    f.registers.lock().unwrap().push(b.clone());
    let pin = f.picks_pin.or(b["pin"].as_str()).unwrap_or("654321").to_string();
    (StatusCode::CREATED, Json(json!({"name": b["name"], "pin": pin}))).into_response()
}

async fn presence(State(f): State<Fake>) -> Response {
    match f.seat {
        Seat::Expired if f.registers.lock().unwrap().is_empty() => err(404, "PROFILE_NOT_FOUND"),
        Seat::Refused(s, c) => err(s, c),
        Seat::Down => err(503, "UNAVAILABLE"),
        _ => Json(json!({"token": "p1"})).into_response(),
    }
}

async fn serve(f: Fake) -> String {
    let app = Router::new()
        .route(
            "/seat/v1/status",
            get(|| async {
                Json(json!({"name": "fake", "seatsTotal": 2, "seated": 0, "queued": 0, "lobbyEndpoint": "lobby.test:8905"}))
            }),
        )
        .route("/seat/v1/sessions", post(join))
        .route("/seat/v1/sessions/:sid", get(poll).delete(|| async { StatusCode::NO_CONTENT }))
        .route("/seat/v1/profiles/:name", delete(delete_profile))
        .route("/seat/v1/presence", post(presence))
        .route("/seat/v1/profiles", post(register))
        .with_state(f);
    let l = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = l.local_addr().unwrap();
    tokio::spawn(async move { axum::serve(l, app).await });
    format!("http://{addr}")
}

fn fake(seat: Seat, delete: (u16, &'static str)) -> Fake {
    Fake { seat, delete, polls: Arc::default(), joins: Arc::default(), deletes: Arc::default(), registers: Arc::default(), picks_pin: None }
}

static N: AtomicUsize = AtomicUsize::new(0);

/// A fresh folder with a game (mkw.exe, an old mkwmp.toml with a ticket) and
/// a launcher whose "game" is a script that leaves `started` behind.
fn setup(server: &str) -> (Arc<Core>, PathBuf) {
    let dir = std::env::temp_dir().join(format!("mkwmp_core_{}_{}", std::process::id(), N.fetch_add(1, Ordering::SeqCst)));
    let _ = std::fs::remove_dir_all(&dir);
    let game = dir.join("game");
    std::fs::create_dir_all(&game).unwrap();
    std::fs::write(game.join("mkw.exe"), crate::recomp::TEST_EXE).unwrap();
    std::fs::write(game.join("mkwmp.toml"), "lobby = \"old:8905\"\nticket = \"mkw1.old\"\nvoice_port = 8907\n").unwrap();
    // An installed game patch (no expected hashes in tests: "not verified").
    crate::probe::install_probe(&game, b"probe", false).unwrap();
    crate::probe::install_file(&game, crate::probe::VOICE, b"voice", false).unwrap();
    std::fs::write(game.join(crate::probe::HOST), crate::probe::fake_winmm()).unwrap();
    let cfg = ClientConfig {
        server: server.into(),
        name: "Ana".into(),
        pin: "123456".into(),
        game: game.clone(),
        launch: true,
        seat: SeatMode::Start,
        local_port: 47820,
        update: false,
        slot: 1,
        launch_command: "echo up > started".into(),
        background: Default::default(),
        voice: Voice {
            mutes: vec![crate::config::VoiceMute { account: "42".into(), name: "Bia".into() }],
            ..Default::default()
        },
        patch: true,
        adult_consent: true,
    };
    let path = dir.join("client.toml");
    cfg.save(path.to_str().unwrap()).unwrap();
    let core = Core::new(path.to_str().unwrap().into(), cfg);
    core.local_api.store(true, std::sync::atomic::Ordering::Relaxed); // as if main bound the port
    (core, game)
}

async fn until<F: Fn() -> bool>(what: &str, f: F) {
    for _ in 0..100 {
        if f() {
            return;
        }
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
    panic!("timed out waiting for {what}");
}

fn ticket(game: &Path) -> Option<String> {
    let t = std::fs::read_to_string(game.join("mkwmp.toml")).ok()?;
    t.lines().find(|l| l.starts_with("ticket")).map(str::to_string)
}

async fn settle_register(core: &Arc<Core>) -> RegisterView {
    until("the delete", || core.ui().register != RegisterView::Working).await;
    core.ui().register
}

fn assert_forgotten(core: &Core, game: &Path) {
    let ui = core.ui();
    assert_eq!(ui.register, RegisterView::Deleted);
    assert!(ui.cfg.name.is_empty() && ui.cfg.pin.is_empty() && ui.cfg.voice.mutes.is_empty());
    let saved = ClientConfig::load(&core.config_path).unwrap();
    assert!(saved.name.is_empty() && saved.pin.is_empty() && saved.voice.mutes.is_empty());
    assert_eq!(ticket(game), None, "the old ticket is gone");
    assert!(std::fs::read_to_string(game.join("mkwmp.toml")).unwrap().contains("voice_port = 8907"));
}

fn assert_kept(core: &Core, game: &Path) {
    let saved = ClientConfig::load(&core.config_path).unwrap();
    assert_eq!((saved.name.as_str(), saved.pin.as_str(), saved.voice.mutes.len()), ("Ana", "123456", 1));
    assert_eq!(core.ui().cfg.pin, "123456");
    assert!(ticket(game).is_some());
}

#[tokio::test]
async fn delete_forgets_the_profile() {
    let f = fake(Seat::Seated, (204, ""));
    let (core, game) = setup(&serve(f.clone()).await);
    core.delete_profile();
    settle_register(&core).await;
    assert_forgotten(&core, &game);
    assert_eq!(*f.deletes.lock().unwrap(), ["Ana"]);
}

#[tokio::test]
async fn delete_of_a_profile_the_server_lacks_forgets_it_too() {
    for (status, code) in [(404, "PROFILE_NOT_FOUND"), (404, "")] {
        let (core, game) = setup(&serve(fake(Seat::Seated, (status, code))).await);
        core.delete_profile();
        settle_register(&core).await;
        assert_forgotten(&core, &game);
    }
}

#[tokio::test]
async fn delete_with_a_wrong_pin_keeps_everything() {
    for (status, code) in [(401, "PIN_WRONG"), (403, "FORBIDDEN")] {
        let (core, game) = setup(&serve(fake(Seat::Seated, (status, code))).await);
        core.delete_profile();
        assert_eq!(settle_register(&core).await, RegisterView::Failed(WRONG_PIN.into()));
        assert_kept(&core, &game);
    }
}

#[tokio::test]
async fn delete_without_the_server_keeps_everything() {
    for server in ["http://127.0.0.1:1".to_string(), serve(fake(Seat::Seated, (500, "REGISTRY_ERROR"))).await] {
        let (core, game) = setup(&server);
        core.delete_profile();
        assert!(matches!(settle_register(&core).await, RegisterView::Failed(_)));
        assert_kept(&core, &game);
    }
}

async fn settle_session(core: &Arc<Core>) -> SessionView {
    until("the session to end", || !core.busy()).await;
    core.ui().session
}

#[tokio::test]
async fn play_starts_the_game_once_seated() {
    let (core, game) = setup(&serve(fake(Seat::Seated, (204, ""))).await);
    core.play();
    until("the game", || game.join("started").exists()).await;
    assert_eq!(ticket(&game).as_deref(), Some("ticket = \"mkw1.ticket.sig\""));
    // The "game" exits at once: the seat goes, the session ends.
    assert_eq!(settle_session(&core).await, SessionView::Idle);
}

#[tokio::test]
async fn play_refused_does_not_start_the_game() {
    for (status, code, msg) in [(401, "PIN_WRONG", WRONG_PIN), (404, "PROFILE_NOT_FOUND", NOT_REGISTERED)] {
        let (core, game) = setup(&serve(fake(Seat::Refused(status, code), (204, ""))).await);
        core.play();
        assert_eq!(settle_session(&core).await, SessionView::Failed(msg.into()));
        assert!(!game.join("started").exists());
        assert_eq!(ticket(&game), None, "no stale ticket either");
    }
}

#[tokio::test]
async fn an_expired_profile_is_registered_again_with_the_saved_pin() {
    let f = fake(Seat::Expired, (204, ""));
    let (core, game) = setup(&serve(f.clone()).await);
    core.play();
    until("the game", || game.join("started").exists()).await;
    assert_eq!(*f.registers.lock().unwrap(), vec![json!({"name": "Ana", "pin": "123456"})]);
    assert_eq!(core.ui().cfg.pin, "123456", "the PIN stays");
}

#[tokio::test]
async fn a_server_that_picks_its_own_pin_on_restore_gets_that_pin_saved() {
    let f = Fake { picks_pin: Some("777000"), ..fake(Seat::Expired, (204, "")) };
    let (core, game) = setup(&serve(f.clone()).await);
    core.play();
    until("the game", || game.join("started").exists()).await;
    assert_eq!(core.ui().cfg.pin, "777000");
    let saved = std::fs::read_to_string(&core.config_path).unwrap();
    assert!(saved.contains("777000"), "saved to client.toml");
}

#[tokio::test]
async fn nothing_reaches_the_server_without_consent() {
    let f = fake(Seat::Seated, (204, ""));
    let (core, game) = setup(&serve(f.clone()).await);
    let mut cfg = core.ui().cfg;
    cfg.adult_consent = false;
    core.set_config(cfg).unwrap();
    core.play();
    assert_eq!(settle_session(&core).await, SessionView::Failed(crate::api::NO_CONSENT.into()));
    core.register();
    assert_eq!(core.ui().register, RegisterView::Failed(crate::api::NO_CONSENT.into()));
    assert!(!game.join("started").exists());
    assert_eq!(f.joins.load(Ordering::SeqCst), 0);
    assert!(f.registers.lock().unwrap().is_empty());
}

#[tokio::test]
async fn play_waits_for_the_local_api_port() {
    let (core, game) = setup(&serve(fake(Seat::Seated, (204, ""))).await);
    core.local_api.store(false, std::sync::atomic::Ordering::Relaxed); // another launcher holds it
    core.play();
    match settle_session(&core).await {
        SessionView::Failed(m) => assert!(m.contains("Another MKWMP launcher is running"), "{m}"),
        other => panic!("{other:?}"),
    }
    assert!(!game.join("started").exists());
}

#[tokio::test]
async fn play_without_the_server_does_not_start_the_game() {
    for server in ["http://127.0.0.1:1".to_string(), serve(fake(Seat::Down, (204, ""))).await] {
        let (core, game) = setup(&server);
        core.play();
        assert_eq!(settle_session(&core).await, SessionView::Failed(UNREACHABLE.into()));
        assert!(!game.join("started").exists());
    }
}

#[tokio::test]
async fn play_queued_waits_for_the_seat() {
    let (core, game) = setup(&serve(fake(Seat::QueuedFor(2), (204, ""))).await);
    core.play();
    until("the queue", || {
        matches!(core.ui().session, SessionView::Running { phase: Phase::Queued(_), game_running: false, .. })
    })
    .await;
    assert!(!game.join("started").exists(), "not while queued");
    for _ in 0..80 {
        if game.join("started").exists() {
            break;
        }
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
    assert!(game.join("started").exists(), "started once seated");
    assert!(ticket(&game).is_some());
}

#[tokio::test]
async fn stop_while_queued_never_starts_the_game() {
    let (core, game) = setup(&serve(fake(Seat::QueuedFor(1000), (204, ""))).await);
    core.play();
    until("the queue", || matches!(core.ui().session, SessionView::Running { phase: Phase::Queued(_), .. })).await;
    core.stop();
    assert_eq!(settle_session(&core).await, SessionView::Idle);
    assert!(!game.join("started").exists());
}

#[tokio::test]
async fn play_online_checks_the_profile_first() {
    for (seat, want) in [
        (Seat::Refused(401, "PIN_WRONG"), Some(WRONG_PIN)),
        (Seat::Down, Some(UNREACHABLE)),
        (Seat::Seated, None),
    ] {
        let (core, game) = setup(&serve(fake(seat, (204, ""))).await);
        let mut cfg = core.ui().cfg;
        cfg.seat = SeatMode::Online;
        core.set_config(cfg).unwrap();
        core.play();
        match want {
            Some(msg) => {
                assert_eq!(settle_session(&core).await, SessionView::Failed(msg.into()));
                assert!(!game.join("started").exists());
            }
            None => until("the game", || game.join("started").exists()).await,
        }
        core.stop();
    }
}

#[tokio::test]
async fn a_gamertag_in_use_is_retried_not_refused() {
    let f = fake(Seat::TakenFor(1), (204, ""));
    let (core, game) = setup(&serve(f.clone()).await);
    core.play();
    until("the busy phase", || {
        matches!(&core.ui().session, SessionView::Running { phase: Phase::Busy(m), .. } if m == IN_USE)
    })
    .await;
    assert!(!game.join("started").exists(), "not while the name is in use");
    for _ in 0..140 {
        if game.join("started").exists() {
            break;
        }
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
    assert!(game.join("started").exists(), "seated on the retry");
    assert_eq!(f.joins.load(Ordering::SeqCst), 2);
}

#[tokio::test]
async fn online_mode_does_not_cache_a_gamertag_in_use() {
    let f = fake(Seat::TakenFor(1), (204, ""));
    let (core, _game) = setup(&serve(f.clone()).await);
    let mut cfg = core.ui().cfg;
    cfg.seat = SeatMode::Online;
    let seat = crate::seat::Seat::new(&cfg);
    assert_eq!(seat.want().await, Answer::Unavailable(IN_USE.into()));
    // Asked again at once: no new join (retries are spaced), still busy, not refused.
    assert_eq!(seat.want().await, Answer::Unavailable(IN_USE.into()));
    assert_eq!(f.joins.load(Ordering::SeqCst), 1);
    tokio::time::sleep(crate::seat::BUSY_RETRY).await;
    assert!(matches!(seat.want().await, Answer::Seated { .. }));
}

#[tokio::test]
async fn the_seat_servers_room_reaches_the_game_as_is() {
    let f = fake(Seat::Seated, (204, ""));
    let (core, _game) = setup(&serve(f.clone()).await);
    let mut cfg = core.ui().cfg;
    cfg.seat = SeatMode::Online;
    let seat = crate::seat::Seat::new(&cfg);
    match seat.want().await {
        Answer::Seated { room: Some(r), .. } => {
            assert_eq!(r, json!({"lobby": "l-1", "ussid": 5, "ussids": [3, 5], "at": 1_795_000_000_000u64}));
        }
        other => panic!("{other:?}"),
    }
}

fn enable_mod(game: &Path) {
    std::fs::write(game.join("mkw.ini"), "[launcher]\nmods = mkwmp\n").unwrap();
    std::fs::create_dir_all(game.join("mods/mkwmp/plugin")).unwrap();
    std::fs::write(game.join("mods/mkwmp/plugin/mkwmp.dll"), b"mod").unwrap();
}

#[tokio::test]
async fn the_mod_wins_only_on_a_runtime_that_loads_it() {
    if std::env::var_os("MKW_MODS").is_some() || crate::probe::dev_probe() {
        return; // the environment would decide instead
    }
    // 0.1.10 with a leftover mods/mkwmp: the launcher's patch stays.
    let (core, game) = setup("http://127.0.0.1:9");
    enable_mod(&game);
    let _ = core.sync_patch(false).await;
    assert!(game.join(crate::probe::PROBE).exists());
    assert_ne!(core.patch_report().state, crate::probe::PatchState::Mod);

    // 0.1.15 with the patch off in settings: nothing is removed, but PLAY may go (the mod carries it).
    let (core, game) = setup("http://127.0.0.1:9");
    std::fs::write(game.join("mkw.exe"), crate::recomp::TEST_MOD_EXE).unwrap();
    enable_mod(&game);
    let mut cfg = core.ui().cfg;
    cfg.patch = false;
    core.set_config(cfg).unwrap();
    let _ = core.sync_patch(false).await;
    assert!(game.join(crate::probe::PROBE).exists());
    let r = core.patch_report();
    assert!(r.state == crate::probe::PatchState::Mod && r.ok(), "{r:?}");
    assert!(r.files.iter().any(|f| f.contains("still beside mkw.exe")), "{:?}", r.files);

    // 0.1.15 with the patch on: the launcher's game patch goes, mkwmp.toml stays.
    let mut cfg = core.ui().cfg;
    cfg.patch = true;
    core.set_config(cfg).unwrap();
    core.sync_patch(false).await.unwrap();
    assert!(!game.join(crate::probe::PROBE).exists() && !game.join(crate::probe::VOICE).exists());
    assert!(game.join("mkwmp.toml").exists());
    let r = core.patch_report();
    assert_eq!(r.state, crate::probe::PatchState::Mod);
    assert_eq!(r.summary("1.3.0"), "MKWMP runs as a mod in this game");
    assert_eq!(r.files.len(), 1, "{:?}", r.files);
}
