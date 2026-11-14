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
}

#[derive(Clone)]
struct Fake {
    seat: Seat,
    delete: (u16, &'static str),
    polls: Arc<AtomicUsize>,
    joins: Arc<AtomicUsize>,
    deletes: Arc<Mutex<Vec<String>>>,
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

async fn presence(State(f): State<Fake>) -> Response {
    match f.seat {
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
        .with_state(f);
    let l = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = l.local_addr().unwrap();
    tokio::spawn(async move { axum::serve(l, app).await });
    format!("http://{addr}")
}

fn fake(seat: Seat, delete: (u16, &'static str)) -> Fake {
    Fake { seat, delete, polls: Arc::default(), joins: Arc::default(), deletes: Arc::default() }
}

static N: AtomicUsize = AtomicUsize::new(0);

/// A fresh folder with a game (mkw.exe, an old mkwmp.toml with a ticket) and
/// a launcher whose "game" is a script that leaves `started` behind.
fn setup(server: &str) -> (Arc<Core>, PathBuf) {
    let dir = std::env::temp_dir().join(format!("mkwmp_core_{}_{}", std::process::id(), N.fetch_add(1, Ordering::SeqCst)));
    let _ = std::fs::remove_dir_all(&dir);
    let game = dir.join("game");
    std::fs::create_dir_all(&game).unwrap();
    std::fs::write(game.join("mkw.exe"), "").unwrap();
    std::fs::write(game.join("mkwmp.toml"), "lobby = \"old:8905\"\nticket = \"mkw1.old\"\nvoice_port = 8907\n").unwrap();
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
    };
    let path = dir.join("client.toml");
    cfg.save(path.to_str().unwrap()).unwrap();
    (Core::new(path.to_str().unwrap().into(), cfg), game)
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
