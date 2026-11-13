//! The mkwmp server binary. One VM can run both parts:
//!
//!   mkwmp server [config/server.toml]   seats, queue and signed tickets (HTTP)
//!   mkwmp lobby  [config/server.toml]   the game-facing lobby (matches)
//!   mkwmp keygen [kid]                  print a new ticket signing key

const USAGE: &str = "usage: mkwmp <server|lobby> [config.toml] | mkwmp keygen [kid]";

#[tokio::main]
async fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let cmd = args.first().map(String::as_str);
    let arg = args.get(1).map(String::as_str);
    let result: Result<(), Box<dyn std::error::Error>> = match cmd {
        Some("keygen") => {
            seat::keygen(arg.unwrap_or("k1"));
            Ok(())
        }
        Some("server") | Some("lobby") => match config::ServerConfig::load(arg.unwrap_or("config/server.toml")) {
            Ok(cfg) if cmd == Some("server") => seat::serve(&cfg).await,
            Ok(cfg) => lobby::serve(&cfg).await,
            Err(e) => Err(e.into()),
        },
        _ => {
            eprintln!("{USAGE}");
            std::process::exit(2);
        }
    };
    if let Err(e) = result {
        eprintln!("mkwmp: {e}");
        std::process::exit(1);
    }
}
