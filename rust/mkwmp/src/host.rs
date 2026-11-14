//! `mkwmp host`: a whole server in one command, for a PC or a VM.
//!
//! The first run creates everything in a data folder: the ticket signing
//! key, the lobby's tokens, its TLS certificate (self-signed, for the host
//! names the game asks for) and the SQLite registry. Every run writes
//! `server.toml` from `host.toml` (the address, name and seat count, which
//! flags and `MKWMP_*` variables update), then runs the seat server and the
//! lobby together in this process.

use std::io::{IsTerminal, Write as _};
use std::net::Ipv4Addr;
use std::path::{Path, PathBuf};

type Res<T> = Result<T, Box<dyn std::error::Error>>;

pub const USAGE: &str = "\
usage: mkwmp host [--public-ip IPV4] [--name NAME] [--capacity N] [--turn-port PORT]
                  [--seat-bind ADDR:PORT] [--data DIR] [--init-only]

  --public-ip   the IPv4 address players reach this machine at (asked on the
                first run when omitted); env MKWMP_PUBLIC_IP
  --name        server name shown in the launcher (MKWMP_NAME, default mkwmp)
  --capacity    players seated at once; the rest queue (MKWMP_CAPACITY, 50)
  --turn-port   UDP port of the TURN relay (MKWMP_TURN_PORT, 3478)
  --seat-bind   where the seat API listens (MKWMP_SEAT_BIND, 0.0.0.0:7000)
  --data        data folder (MKWMP_DATA; default: mkwmp-data next to this program)
  --init-only   create the data folder and config, then exit
Settings are remembered in <data>/host.toml.";

/// The host settings, kept in `<data>/host.toml`.
#[derive(Debug, Clone, PartialEq)]
struct Host {
    public_ip: Option<Ipv4Addr>,
    name: String,
    capacity: u32,
    turn_port: u16,
    seat_bind: String,
}

impl Default for Host {
    fn default() -> Self {
        Self { public_ip: None, name: "mkwmp".into(), capacity: 50, turn_port: 3478, seat_bind: "0.0.0.0:7000".into() }
    }
}

impl Host {
    fn load(path: &Path) -> Res<Self> {
        let mut h = Host::default();
        let Ok(text) = std::fs::read_to_string(path) else { return Ok(h) };
        for line in text.lines() {
            let Some((k, v)) = line.split_once('=') else { continue };
            let v = v.trim().trim_matches('"');
            match k.trim() {
                "public_ip" => h.public_ip = Some(v.parse().map_err(|_| format!("{}: bad public_ip", path.display()))?),
                "name" => h.name = v.into(),
                "capacity" => h.capacity = v.parse().map_err(|_| format!("{}: bad capacity", path.display()))?,
                "turn_port" => h.turn_port = v.parse().map_err(|_| format!("{}: bad turn_port", path.display()))?,
                "seat_bind" => h.seat_bind = v.into(),
                _ => {}
            }
        }
        Ok(h)
    }

    fn save(&self, path: &Path) -> Res<()> {
        let ip = self.public_ip.map(|i| i.to_string()).unwrap_or_default();
        let text = format!(
            "# mkwmp host settings (`mkwmp host --help`). server.toml is written from these on every start.\n\
             public_ip = \"{ip}\"\nname = \"{}\"\ncapacity = {}\nturn_port = {}\nseat_bind = \"{}\"\n",
            self.name, self.capacity, self.turn_port, self.seat_bind
        );
        std::fs::write(path, text)?;
        Ok(())
    }

    fn validate(&self) -> Res<()> {
        if self.name.is_empty()
            || self.name.len() > 32
            || !self.name.chars().all(|c| c.is_ascii_alphanumeric() || " _.-".contains(c))
        {
            return Err("the server name may use up to 32 letters, digits, spaces, _ . -".into());
        }
        if self.capacity == 0 {
            return Err("capacity must be at least 1".into());
        }
        if self.turn_port == 0 {
            return Err("turn-port must be a UDP port number".into());
        }
        self.seat_bind.parse::<std::net::SocketAddr>().map_err(|_| "seat-bind must look like 0.0.0.0:7000")?;
        Ok(())
    }
}

struct Opts {
    host: Host,
    data: PathBuf,
    init_only: bool,
}

fn parse(args: &[String]) -> Res<Opts> {
    let env = |k: &str| std::env::var(k).ok().filter(|v| !v.trim().is_empty());
    let mut flags: Vec<(String, String)> = Vec::new();
    for k in ["PUBLIC_IP", "NAME", "CAPACITY", "TURN_PORT", "SEAT_BIND"] {
        if let Some(v) = env(&format!("MKWMP_{k}")) {
            flags.push((k.to_lowercase().replace('_', "-"), v));
        }
    }
    let mut data = env("MKWMP_DATA").map(PathBuf::from);
    let mut init_only = false;
    let mut it = args.iter();
    while let Some(a) = it.next() {
        let mut value = || it.next().cloned().ok_or_else(|| format!("{a} needs a value\n\n{USAGE}"));
        match a.as_str() {
            "--public-ip" | "--name" | "--capacity" | "--turn-port" | "--seat-bind" => {
                flags.push((a[2..].to_string(), value()?))
            }
            "--data" => data = Some(PathBuf::from(value()?)),
            "--init-only" => init_only = true,
            "-h" | "--help" => {
                println!("{USAGE}");
                std::process::exit(0);
            }
            _ => return Err(format!("unknown option {a}\n\n{USAGE}").into()),
        }
    }
    let data = match data {
        Some(d) => d,
        None => std::env::current_exe()?.parent().ok_or("no program folder")?.join("mkwmp-data"),
    };
    std::fs::create_dir_all(data.join("keys"))?;
    let mut host = Host::load(&data.join("host.toml"))?;
    for (k, v) in flags {
        match k.as_str() {
            "public-ip" => host.public_ip = Some(v.trim().parse().map_err(|_| format!("--public-ip: {v:?} is not an IPv4 address"))?),
            "name" => host.name = v.trim().to_string(),
            "capacity" => host.capacity = v.trim().parse().map_err(|_| format!("--capacity: {v:?} is not a number"))?,
            "turn-port" => host.turn_port = v.trim().parse().map_err(|_| format!("--turn-port: {v:?} is not a port"))?,
            "seat-bind" => host.seat_bind = v.trim().to_string(),
            _ => unreachable!(),
        }
    }
    Ok(Opts { host, data, init_only })
}

/// This machine's LAN address: the source address of the default route.
/// (A UDP connect sends nothing.)
fn lan_ip() -> Option<Ipv4Addr> {
    let s = std::net::UdpSocket::bind("0.0.0.0:0").ok()?;
    s.connect("192.0.2.1:9").ok()?;
    match s.local_addr().ok()?.ip() {
        std::net::IpAddr::V4(v4) if !v4.is_unspecified() && !v4.is_loopback() => Some(v4),
        _ => None,
    }
}

fn ask_public_ip() -> Res<Ipv4Addr> {
    let lan = lan_ip();
    if !std::io::stdin().is_terminal() {
        return Err("set the address players reach this machine at: --public-ip <IPv4> (or MKWMP_PUBLIC_IP)".into());
    }
    println!("Which IPv4 address do players reach this machine at?");
    println!("  - friends over the internet: your public IP (search \"what is my IP\"),");
    println!("    and forward the ports below on your router to this machine;");
    match lan {
        Some(ip) => println!("  - a LAN party: this machine's LAN address, {ip} (press Enter)."),
        None => println!("  - a LAN party: this machine's LAN address."),
    }
    loop {
        print!("address{}: ", lan.map(|i| format!(" [{i}]")).unwrap_or_default());
        std::io::stdout().flush()?;
        let mut line = String::new();
        if std::io::stdin().read_line(&mut line)? == 0 {
            return Err("no address given".into());
        }
        let line = line.trim();
        match (line.is_empty(), lan) {
            (true, Some(ip)) => return Ok(ip),
            _ => match line.parse() {
                Ok(ip) => return Ok(ip),
                Err(_) => println!("{line:?} is not an IPv4 address (like 203.0.113.7)"),
            },
        }
    }
}

fn write_secret(path: &Path, bytes: &[u8]) -> Res<()> {
    let mut o = std::fs::OpenOptions::new();
    o.write(true).create(true).truncate(true);
    #[cfg(unix)]
    std::os::unix::fs::OpenOptionsExt::mode(&mut o, 0o600);
    o.open(path)?.write_all(bytes)?;
    Ok(())
}

fn random_hex(n: usize) -> String {
    use rand::RngCore;
    let mut b = vec![0u8; n];
    rand::rngs::OsRng.fill_bytes(&mut b);
    b.iter().map(|x| format!("{x:02x}")).collect()
}

/// The host names the game connects to (it still asks for Nintendo's names;
/// the patch redirects the connection). Same profile as the built-in
/// certificate: RSA 2048, self-signed, its own CA.
pub const LOBBY_CERT_NAMES: [&str; 3] = ["*.lp1.t.npln.srv.nintendo.net", "gamesync.npln.nintendo.net", "localhost"];

pub fn lobby_cert(server_name: &str) -> Res<(String, String)> {
    use rsa::pkcs8::EncodePrivateKey;
    let key = rsa::RsaPrivateKey::new(&mut rand::rngs::OsRng, 2048)?;
    let der = key.to_pkcs8_der()?;
    let pkcs8 = rustls_pki_types::PrivatePkcs8KeyDer::from(der.as_bytes().to_vec());
    let kp = rcgen::KeyPair::from_pkcs8_der_and_sign_algo(&pkcs8, &rcgen::PKCS_RSA_SHA256)?;
    let mut p = rcgen::CertificateParams::new(LOBBY_CERT_NAMES.iter().map(|s| s.to_string()).collect::<Vec<_>>())?;
    p.distinguished_name = rcgen::DistinguishedName::new();
    p.distinguished_name.push(rcgen::DnType::CommonName, format!("mkwmp lobby ({server_name})"));
    p.is_ca = rcgen::IsCa::Ca(rcgen::BasicConstraints::Unconstrained);
    let now = time::OffsetDateTime::now_utc();
    p.not_before = now - time::Duration::days(1);
    p.not_after = now + time::Duration::days(3650);
    let cert = p.self_signed(&kp)?;
    Ok((cert.pem(), kp.serialize_pem()))
}

/// Create whatever is missing in the data folder; return the ticket public
/// key entry (`kid:base64url`).
fn ensure_keys(data: &Path, server_name: &str) -> Res<String> {
    let keys = data.join("keys");
    let seed_file = keys.join("seat-ed25519.key");
    if !seed_file.exists() {
        let key = ticket::Issuer::generate();
        write_secret(&seed_file, format!("{}\n", ticket::seed_b64(&key)).as_bytes())?;
        println!("created the ticket signing key");
    }
    let seed = std::fs::read_to_string(&seed_file)?;
    let issuer = ticket::Issuer::from_seed_b64("k1", seed.trim()).map_err(|e| format!("{}: {e}", seed_file.display()))?;
    for (file, len) in [("lobby-tokens.txt", 32), ("admin-token.txt", 32)] {
        let p = keys.join(file);
        if !p.exists() {
            write_secret(&p, format!("{}\n", random_hex(len)).as_bytes())?;
        }
    }
    let secret = keys.join("lobby-token-secret");
    if !secret.exists() {
        let mut b = [0u8; 32];
        rand::RngCore::fill_bytes(&mut rand::rngs::OsRng, &mut b);
        write_secret(&secret, &b)?;
    }
    let (cert, key) = (keys.join("lobby-cert.pem"), keys.join("lobby-key.pem"));
    if !cert.exists() || !key.exists() {
        let (c, k) = lobby_cert(server_name)?;
        write_secret(&key, k.as_bytes())?;
        std::fs::write(&cert, c)?;
        println!("created the lobby certificate");
    }
    Ok(format!("{}:{}", issuer.kid(), issuer.public_key_b64()))
}

fn server_toml(h: &Host, ip: Ipv4Addr, public_key: &str) -> String {
    format!(
        r#"## Written by `mkwmp host` on every start, from host.toml: edit that (or
## pass flags), not this file. Paths are relative to this file.
[server]
name = "{name}"
bind = "0.0.0.0:8905"

[seats]
capacity = {capacity}
bind = "{seat_bind}"
lobby_endpoint = "{ip}:8905"
lobby_tokens_file = "keys/lobby-tokens.txt"

[registry]
file = "registry.db"
reserved_names = []
expire_days = 180
admin_token_file = "keys/admin-token.txt"

[ticket]
kid = "k1"
private_key_file = "keys/seat-ed25519.key"
public_keys = ["{public_key}"]

[lobby]
host = "0.0.0.0"
public_host = "{ip}"
turn_port = {turn_port}
relay_ports = "49152:49407"
require_ticket = true
roster_url = "http://{roster}"
roster_token_file = "keys/lobby-tokens.txt"
token_secret_file = "keys/lobby-token-secret"

[tls]
cert = "keys/lobby-cert.pem"
key = "keys/lobby-key.pem"
"#,
        name = h.name,
        capacity = h.capacity,
        seat_bind = h.seat_bind,
        turn_port = h.turn_port,
        // The lobby asks the seat server over loopback.
        roster = format!("127.0.0.1:{}", h.seat_bind.rsplit(':').next().unwrap_or("7000")),
    )
}

/// Prepare the data folder; returns the config path and the settings.
fn prepare(opts: &mut Opts) -> Res<PathBuf> {
    if opts.host.public_ip.is_none() {
        opts.host.public_ip = Some(ask_public_ip()?);
    }
    opts.host.validate()?;
    opts.host.save(&opts.data.join("host.toml"))?;
    let public_key = ensure_keys(&opts.data, &opts.host.name)?;
    let cfg = opts.data.join("server.toml");
    write_secret(&cfg, server_toml(&opts.host, opts.host.public_ip.unwrap(), &public_key).as_bytes())?;
    Ok(cfg)
}

pub async fn run(args: &[String]) -> Res<()> {
    let mut opts = parse(args)?;
    let cfg_path = prepare(&mut opts)?;
    let h = &opts.host;
    let ip = h.public_ip.unwrap();
    let port = h.seat_bind.rsplit(':').next().unwrap_or("7000");
    println!("data folder: {}", opts.data.display());
    println!("players set the launcher's SERVER to: http://{ip}:{port}");
    println!(
        "open (and on a home router, forward to this machine): TCP {port}, 8905, 8906; UDP {}, 10025, 49152-49407",
        h.turn_port
    );
    if opts.init_only {
        return Ok(());
    }
    println!("running; stop with Ctrl-C or by closing this window (players' seats are freed, gamertags are kept)");
    let cfg = config::ServerConfig::load(&cfg_path)?;
    crate::logging();
    tokio::select! {
        r = seat::serve(&cfg) => r,
        r = crate::lobby::serve(&cfg) => r,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn host_settings_round_trip() {
        let dir = std::env::temp_dir().join(format!("mkwmp-host-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let h = Host { public_ip: Some("203.0.113.7".parse().unwrap()), name: "LAN party".into(), capacity: 8, turn_port: 3479, seat_bind: "127.0.0.1:7100".into() };
        h.save(&dir.join("host.toml")).unwrap();
        assert_eq!(Host::load(&dir.join("host.toml")).unwrap(), h);
        let toml = server_toml(&h, "203.0.113.7".parse().unwrap(), "k1:abc");
        assert!(toml.contains("roster_url = \"http://127.0.0.1:7100\""));
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn names_are_checked() {
        let mut h = Host { public_ip: None, ..Host::default() };
        assert!(h.validate().is_ok());
        h.name = "a\"b".into();
        assert!(h.validate().is_err());
    }

    #[test]
    fn writes_a_loadable_config_and_keeps_keys() {
        let dir = std::env::temp_dir().join(format!("mkwmp-host-init-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(dir.join("keys")).unwrap();
        let mut o = Opts { host: Host { public_ip: Some("198.51.100.4".parse().unwrap()), ..Host::default() }, data: dir.clone(), init_only: true };
        let cfg_path = prepare(&mut o).unwrap();
        let cfg = config::ServerConfig::load(&cfg_path).unwrap();
        assert_eq!(cfg.seats.lobby_endpoint, "198.51.100.4:8905");
        assert!(cfg.lobby.require_ticket);
        let cert = std::fs::read(dir.join("keys/lobby-cert.pem")).unwrap();
        prepare(&mut o).unwrap();
        assert_eq!(std::fs::read(dir.join("keys/lobby-cert.pem")).unwrap(), cert, "a restart keeps the certificate");
        std::fs::remove_dir_all(&dir).unwrap();
    }
}
