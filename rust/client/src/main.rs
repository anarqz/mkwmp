//! mkwmp launcher: a small window to play on an mkwmp server.
//!
//!   mkwmp-launcher [client.toml]                 the window
//!   mkwmp-launcher --tray [client.toml]          the window, started in the tray
//!   mkwmp-launcher --headless [--no-launch] [--no-update] [client.toml]
//!   mkwmp-launcher --version
//!
//! Settings live in client.toml (next to the launcher by default); the window
//! edits it. The launcher updates itself and the game patch from GitHub
//! Releases (signed), takes a seat on the server (or queues), serves the
//! game's seat requests on 127.0.0.1, and starts the game.
#![cfg_attr(all(windows, not(debug_assertions)), windows_subsystem = "windows")]

mod api;
mod autostart;
mod config;
mod core;
mod gui;
mod icon;
mod launch;
mod local;
mod log;
mod notify;
mod probe;
mod recomp;
mod seat;
mod support;
mod tray;
mod update;

use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

use config::ClientConfig;
use core::{Core, SessionView};

struct Args {
    path: Option<String>,
    headless: bool,
    no_launch: bool,
    no_update: bool,
    tray: bool,
    save_logs: bool,
}

fn args() -> Args {
    let mut a = Args { path: None, headless: false, no_launch: false, no_update: false, tray: false, save_logs: false };
    for arg in std::env::args().skip(1) {
        match arg.as_str() {
            "--save-logs" => a.save_logs = true,
            "--headless" => a.headless = true,
            "--no-launch" => a.no_launch = true,
            "--no-update" => a.no_update = true,
            "--tray" => a.tray = true,
            "--version" | "-V" => {
                console();
                println!("mkwmp-launcher {}", update::VERSION);
                std::process::exit(0);
            }
            "-h" | "--help" => {
                console();
                println!("usage: mkwmp-launcher [--headless | --tray] [--no-launch] [--no-update] [--save-logs] [--version] [client.toml]");
                std::process::exit(0);
            }
            _ => a.path = Some(arg),
        }
    }
    a
}

/// A release build on Windows has no console of its own; borrow the
/// terminal's when one started us.
fn console() {
    #[cfg(windows)]
    unsafe {
        windows_sys::Win32::System::Console::AttachConsole(windows_sys::Win32::System::Console::ATTACH_PARENT_PROCESS);
    }
}

fn default_config_path() -> String {
    std::env::current_exe()
        .ok()
        .and_then(|e| e.parent().map(|d| d.join("client.toml")))
        .unwrap_or_else(|| PathBuf::from("client.toml"))
        .display()
        .to_string()
}

fn main() {
    let a = args();
    if a.headless {
        console();
    }
    let path = a.path.clone().unwrap_or_else(default_config_path);
    if a.save_logs {
        console();
        let game = ClientConfig::load_or_default(&path).map(|c| c.game).unwrap_or_default();
        match support::save_and_open(&log::launcher_dir(), &game) {
            Ok(m) => println!("mkwmp: {m}"),
            Err(e) => fatal(&e),
        }
        return;
    }
    log::init(&log::launcher_dir());
    let mut cfg = match ClientConfig::load_or_default(&path) {
        Ok(c) => c,
        Err(e) => fatal(&e),
    };
    log::header(&cfg, &path);
    if a.no_launch {
        cfg.launch = false;
    }
    if a.no_update {
        cfg.update = false;
    }
    update::clean_old();

    let rt = tokio::runtime::Builder::new_multi_thread().enable_all().build().expect("runtime");
    let core = Core::new(path, cfg.clone());

    // The local API the game calls for its seat. A launcher that just replaced itself
    // (update) or is closing may still hold the port: retry for a while, then keep
    // retrying in the background with PLAY refused until it is ours.
    let addr = format!("127.0.0.1:{}", cfg.local_port);
    let first = rt.block_on(bind_local(&addr, Duration::from_secs(3)));
    let serve = |listener: tokio::net::TcpListener| {
        core.local_api.store(true, std::sync::atomic::Ordering::Relaxed);
        let app = local::router(core.clone());
        rt.spawn(async move {
            let _ = axum::serve(listener, app).await;
        });
    };
    match first {
        Ok(listener) => serve(listener),
        Err(e) => {
            let msg = format!("cannot open {addr} ({e}); is another launcher running?");
            if a.headless {
                fatal(&msg);
            }
            log::note(format!("{msg} PLAY waits until it is free"));
            let core = core.clone();
            rt.spawn(async move {
                loop {
                    tokio::time::sleep(Duration::from_secs(2)).await;
                    if let Ok(listener) = tokio::net::TcpListener::bind(&addr).await {
                        log::note(format!("{addr} is free now: the game's local API is up"));
                        core.local_api.store(true, std::sync::atomic::Ordering::Relaxed);
                        let app = local::router(core.clone());
                        let _ = axum::serve(listener, app).await;
                        return;
                    }
                }
            });
        }
    }
    rt.spawn(core.clone().status_loop());
    rt.spawn(core.clone().friends_loop());
    if !a.headless {
        rt.spawn(core.clone().stars_loop());
        notify::register();
        rt.spawn(notify::watch_loop(core.clone()));
        // Keep the start-up entry pointing at this launcher (it may have moved).
        if cfg.background.start_with_system {
            if let Err(e) = autostart::apply(true) {
                log::note(e);
            }
        }
    }

    if a.headless {
        headless(&rt, core);
    } else {
        // Killed (systemd stop, session logout, `kill`): free the seat first.
        rt.spawn({
            let core = core.clone();
            async move {
                terminated().await;
                log::note("terminated; releasing the seat");
                wait_idle(&core, Duration::from_secs(5)).await;
                std::process::exit(0);
            }
        });
        // Updates run beside the window; a new launcher replaces this one.
        rt.spawn({
            let core = core.clone();
            async move {
                if let Some(exe) = core.updates().await {
                    tokio::time::sleep(Duration::from_millis(800)).await;
                    let _ = std::process::Command::new(&exe)
                        .args(std::env::args_os().skip(1))
                        .env("MKWMP_UPDATED", "1")
                        .spawn();
                    std::process::exit(0);
                }
            }
        });
        let _guard = rt.enter();
        if let Err(e) = gui::run(core.clone(), a.tray) {
            fatal(&e);
        }
        // Window closed: free the seat before leaving.
        rt.block_on(wait_idle(&core, Duration::from_secs(5)));
    }
}

/// Bind `addr`, retrying while it is in use for up to `patience`.
async fn bind_local(addr: &str, patience: Duration) -> std::io::Result<tokio::net::TcpListener> {
    let until = tokio::time::Instant::now() + patience;
    loop {
        match tokio::net::TcpListener::bind(addr).await {
            Ok(l) => return Ok(l),
            Err(e) if e.kind() == std::io::ErrorKind::AddrInUse && tokio::time::Instant::now() < until => {
                tokio::time::sleep(Duration::from_millis(250)).await;
            }
            Err(e) => return Err(e),
        }
    }
}

fn headless(rt: &tokio::runtime::Runtime, core: Arc<Core>) {
    rt.block_on(async {
        if let Some(exe) = core.clone().updates().await {
            let status = std::process::Command::new(&exe)
                .args(std::env::args_os().skip(1))
                .env("MKWMP_UPDATED", "1")
                .status();
            std::process::exit(status.map(|s| s.code().unwrap_or(0)).unwrap_or(1));
        }
        // No PIN yet: register the name if it's free (the PIN is printed once).
        let cfg = core.ui().cfg;
        if cfg.pin.is_empty() && config::valid_name(&cfg.name) {
            let api = api::Api::new(cfg.server.trim_end_matches('/'));
            match api.profile(&cfg.name).await {
                Ok((false, false)) => match api.register(&cfg.name).await {
                    Ok(pin) => {
                        let mut c = cfg.clone();
                        c.pin = pin.clone();
                        if let Err(e) = core.set_config(c) {
                            fatal(&e);
                        }
                        println!("mkwmp: registered {}. Your PIN is {pin}: write it down, it is not shown again.", cfg.name);
                    }
                    Err(e) => fatal(&e.to_string()),
                },
                Ok(_) => fatal(&format!("{} is registered: put its 6-digit PIN in client.toml (pin = \"123456\")", cfg.name)),
                Err(e) => fatal(&e.to_string()),
            }
        }
        if let Some(p) = core.ui().cfg.problem() {
            fatal(&p);
        }
        core.play();
        loop {
            tokio::select! {
                _ = tokio::time::sleep(Duration::from_millis(500)) => {}
                _ = terminated() => {
                    core.stop();
                    wait_idle(&core, Duration::from_secs(5)).await;
                    return;
                }
            }
            match core.ui().session {
                SessionView::Idle => return,
                SessionView::Failed(e) => fatal(&e),
                _ => {}
            }
        }
    });
}

/// Ctrl-C, or SIGTERM / SIGHUP on Unix (what `systemctl stop` and a logout send).
async fn terminated() {
    #[cfg(unix)]
    {
        use tokio::signal::unix::{signal, SignalKind};
        let (Ok(mut term), Ok(mut hup)) = (signal(SignalKind::terminate()), signal(SignalKind::hangup())) else {
            let _ = tokio::signal::ctrl_c().await;
            return;
        };
        tokio::select! {
            _ = tokio::signal::ctrl_c() => {}
            _ = term.recv() => {}
            _ = hup.recv() => {}
        }
    }
    #[cfg(not(unix))]
    {
        let _ = tokio::signal::ctrl_c().await;
    }
}

async fn wait_idle(core: &Arc<Core>, max: Duration) {
    core.stop();
    core.sign_out().await;
    let until = tokio::time::Instant::now() + max;
    while core.busy() && tokio::time::Instant::now() < until {
        tokio::time::sleep(Duration::from_millis(100)).await;
    }
}

fn fatal(msg: &str) -> ! {
    eprintln!("mkwmp: {msg}");
    log::line(&format!("fatal: {msg}"));
    std::process::exit(1)
}
