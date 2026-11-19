//! The mkwmp server binary. One VM can run both parts:
//!
//!   mkwmp server [config/server.toml]   seats, queue and signed tickets (HTTP)
//!   mkwmp lobby  [config/server.toml]   the game-facing lobby (matches)
//!   mkwmp host [--public-ip IP] ...     both parts in one process, set up
//!                                       on first run (also: no arguments)
//!   mkwmp keygen [kid]                  print a new ticket signing key
//!   mkwmp admin <list|register|reset-pin|release> [name] [--config config.toml]
//!   mkwmp admin reports [open|closed|all] | close-report <id> [outcome...] | delete-report <id>
//!                                       manage registered profiles on the
//!                                       running server (same machine)

mod host;
mod lobby;

const USAGE: &str = "usage: mkwmp host [options] (see mkwmp host --help) | mkwmp <server|lobby> [config.toml] | mkwmp keygen [kid] | mkwmp admin <list|register|reset-pin|release> [name] [--config file] | mkwmp admin <reports [open|closed|all]|close-report <id> [outcome...]|delete-report <id>> [--config file]";

#[tokio::main]
async fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let cmd = args.first().map(String::as_str);
    let arg = args.get(1).map(String::as_str);
    let result: Result<(), Box<dyn std::error::Error>> = match cmd {
        // No arguments (e.g. double-clicked): host a server.
        None => host::run(&[]).await,
        Some("host") => host::run(&args[1..]).await,
        Some("keygen") => {
            seat::keygen(arg.unwrap_or("k1"));
            Ok(())
        }
        Some("admin") => admin(&args[1..]).await,
        Some("server") | Some("lobby") => match config::ServerConfig::load(arg.unwrap_or("config/server.toml")) {
            Ok(cfg) if cmd == Some("server") => seat::serve(&cfg).await,
            Ok(cfg) => {
                logging();
                lobby::serve(&cfg).await
            }
            Err(e) => Err(e.into()),
        },
        _ => {
            eprintln!("{USAGE}");
            std::process::exit(2);
        }
    };
    if let Err(e) = result {
        eprintln!("mkwmp: {e}");
        // Double-clicked (no arguments): keep the window open to read it.
        if cmd.is_none() && std::io::IsTerminal::is_terminal(&std::io::stdin()) {
            eprintln!("press Enter to close");
            let _ = std::io::stdin().read_line(&mut String::new());
        }
        std::process::exit(1);
    }
}

/// Profile admin over the server's loopback admin API.
async fn admin(args: &[String]) -> Result<(), Box<dyn std::error::Error>> {
    let mut cfg_path = "config/server.toml".to_string();
    let mut rest = Vec::new();
    let mut it = args.iter();
    while let Some(a) = it.next() {
        if a == "--config" {
            cfg_path = it.next().ok_or("--config needs a file")?.clone();
        } else {
            rest.push(a.as_str());
        }
    }
    let cfg = config::ServerConfig::load(&cfg_path)?;
    let token_file = cfg.registry.admin_token_file.as_ref().ok_or("set [registry] admin_token_file in the config")?;
    let token = std::fs::read_to_string(token_file)?.trim().to_string();
    // The admin API answers loopback only; a 0.0.0.0 bind is reached on
    // 127.0.0.1 (Windows can't connect to 0.0.0.0).
    let mut addr: std::net::SocketAddr = cfg.seats.bind.parse().map_err(|_| "bad [seats] bind")?;
    if addr.ip().is_unspecified() {
        addr.set_ip(std::net::Ipv4Addr::LOCALHOST.into());
    }
    let base = format!("http://{addr}/seat/v1/admin/profiles");
    let reports = format!("http://{addr}/seat/v1/admin/reports");
    let http = reqwest::Client::new();
    let id = |s: &str| s.parse::<i64>().map_err(|_| format!("not a report id: {s}"));
    let res = match rest.as_slice() {
        ["list"] => http.get(&base).bearer_auth(&token).send().await?,
        ["reports"] => http.get(&reports).bearer_auth(&token).send().await?,
        ["reports", status] => http.get(&reports).query(&[("status", status)]).bearer_auth(&token).send().await?,
        ["close-report", n, outcome @ ..] => {
            let body = serde_json::json!({"action": "close", "id": id(n)?, "outcome": outcome.join(" ")});
            http.post(&reports).bearer_auth(&token).json(&body).send().await?
        }
        ["delete-report", n] => {
            http.post(&reports).bearer_auth(&token).json(&serde_json::json!({"action": "delete", "id": id(n)?})).send().await?
        }
        [action @ ("register" | "reset-pin" | "release"), name] => {
            http.post(&base).bearer_auth(&token).json(&serde_json::json!({"action": action, "name": name})).send().await?
        }
        _ => return Err(USAGE.into()),
    };
    let status = res.status();
    let body: serde_json::Value = res.json().await.unwrap_or_default();
    if !status.is_success() {
        return Err(format!("{status}: {}", body["error"]["message"].as_str().unwrap_or("error")).into());
    }
    if let Some(list) = body["reports"].as_array() {
        if list.is_empty() {
            println!("no reports");
        }
        for r in list {
            let when = |k: &str| r[k].as_u64().map(utc).unwrap_or_default();
            println!(
                "#{} {} [{}] {} reported {} (registered {}) for {}",
                r["id"],
                when("created"),
                r["status"].as_str().unwrap_or(""),
                r["reporter"].as_str().unwrap_or(""),
                r["reported"].as_str().unwrap_or(""),
                when("reportedCreated"),
                r["reason"].as_str().unwrap_or(""),
            );
            if let Some(d) = r["details"].as_str().filter(|d| !d.is_empty()) {
                println!("    \"{}\"", d.replace('\n', "\n    "));
            }
            if let Some(o) = r["outcome"].as_str() {
                println!("    closed {}: {o}", when("closed"));
            }
        }
    } else if let Some(list) = body["profiles"].as_array() {
        for p in list {
            println!(
                "{:<16} last seen {}{}",
                p["name"].as_str().unwrap_or(""),
                p["lastSeen"],
                if p["reserved"].as_bool() == Some(true) { "  (reserved)" } else { "" }
            );
        }
    } else if let Some(pin) = body["pin"].as_str() {
        println!("{}: PIN {pin}  (give it to the player; it is not shown again)", body["name"].as_str().unwrap_or(""));
    } else {
        println!("done");
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    #[test]
    fn utc_dates() {
        assert_eq!(super::utc(0), "1970-01-01 00:00 UTC");
        assert_eq!(super::utc(1_795_478_400 + 3_660), "2026-11-24 01:01 UTC");
    }
}

/// Seconds since 1970 as `YYYY-MM-DD HH:MM UTC`.
fn utc(secs: u64) -> String {
    let (days, rem) = (secs / 86_400, secs % 86_400);
    // Civil from days (Howard Hinnant's algorithm).
    let z = days as i64 + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = yoe + era * 400 + i64::from(m <= 2);
    format!("{y:04}-{m:02}-{d:02} {:02}:{:02} UTC", rem / 3600, rem % 3600 / 60)
}

/// The lobby Core logs through `tracing`; RUST_LOG overrides (e.g. lobby=debug).
fn logging() {
    let filter = tracing_subscriber::EnvFilter::try_from_default_env()
        .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("info"));
    use std::io::IsTerminal;
    tracing_subscriber::fmt()
        .with_env_filter(filter)
        .with_writer(std::io::stderr)
        .with_ansi(std::io::stderr().is_terminal())
        .init();
}
