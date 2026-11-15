//! Launcher messages: printed for console mode and logs. The window shows
//! state instead. Every line also goes to `<launcher dir>/logs/launcher-<UTC
//! date>-<time>.log` (the last [`KEEP`] sessions are kept), flushed per line,
//! with secrets (tickets, tokens, PINs) redacted.

use std::fs::{self, File};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

/// Session logs kept in `logs/`.
pub const KEEP: usize = 10;

static FILE: Mutex<Option<File>> = Mutex::new(None);

pub fn note(msg: impl Into<String>) {
    let msg = msg.into();
    println!("mkwmp: {msg}");
    line(&msg);
}

/// Append a timestamped, redacted line to the session log (if open).
pub fn line(msg: &str) {
    let mut g = FILE.lock().unwrap_or_else(|e| e.into_inner());
    if let Some(f) = g.as_mut() {
        let _ = writeln!(f, "{} {}", timestamp(), redact(msg));
        let _ = f.flush();
    }
}

/// The folder of the executable: where client.toml and `logs/` live.
pub fn launcher_dir() -> PathBuf {
    std::env::current_exe()
        .ok()
        .and_then(|e| e.parent().map(Path::to_path_buf))
        .unwrap_or_else(|| PathBuf::from("."))
}

/// Open a new session log in `<dir>/logs`, prune old ones and route panics
/// to it. Failures are silent: logging never stops the launcher.
pub fn init(dir: &Path) {
    let logs = dir.join("logs");
    if fs::create_dir_all(&logs).is_err() {
        return;
    }
    let path = logs.join(format!("launcher-{}.log", file_stamp()));
    let Ok(f) = File::create(&path) else { return };
    *FILE.lock().unwrap_or_else(|e| e.into_inner()) = Some(f);
    prune(&logs, KEEP);
    let prev = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        line(&format!("PANIC: {info}"));
        prev(info);
    }));
}

/// The session header: what a bug report needs to know first.
pub fn header(cfg: &crate::config::ClientConfig, config_path: &str) {
    line(&format!("mkwmp-launcher {}", env!("CARGO_PKG_VERSION")));
    line(&format!("os: {} {}", std::env::consts::OS, std::env::consts::ARCH));
    line(&format!("config: {config_path}"));
    line(&format!("server: {}", cfg.server));
    line(&format!("game folder: {}", cfg.game.display()));
    line(&format!("seat mode: {:?}", cfg.seat));
}

/// `launcher-*.log` files in `logs`, newest first (names sort by time).
pub fn sessions(logs: &Path) -> Vec<PathBuf> {
    let mut v: Vec<PathBuf> = fs::read_dir(logs)
        .into_iter()
        .flatten()
        .flatten()
        .map(|e| e.path())
        .filter(|p| {
            p.file_name()
                .and_then(|n| n.to_str())
                .is_some_and(|n| n.starts_with("launcher-") && n.ends_with(".log"))
        })
        .collect();
    v.sort();
    v.reverse();
    v
}

/// Delete all but the newest `keep` session logs.
pub fn prune(logs: &Path, keep: usize) {
    for old in sessions(logs).into_iter().skip(keep) {
        let _ = fs::remove_file(old);
    }
}

/// Mask what must never reach a log: `mkw1.` tickets, 64-hex tokens and a
/// 6-digit PIN on a line that mentions "pin".
pub fn redact(s: &str) -> String {
    let word = |c: char| c.is_ascii_alphanumeric() || matches!(c, '_' | '.' | '-');
    let mut out = String::with_capacity(s.len());
    let mut run = String::new();
    let flush = |run: &mut String, out: &mut String| {
        if run.starts_with("mkw1.") {
            out.push_str("mkw1.<redacted>");
        } else if run.len() >= 64 && run.chars().all(|c| c.is_ascii_hexdigit()) {
            out.push_str("<redacted>");
        } else {
            out.push_str(run);
        }
        run.clear();
    };
    for c in s.chars() {
        if word(c) {
            run.push(c);
        } else {
            flush(&mut run, &mut out);
            out.push(c);
        }
    }
    flush(&mut run, &mut out);
    if out.to_ascii_lowercase().contains("pin") {
        out = mask_pin(&out);
    }
    out
}

/// Replace every standalone run of exactly 6 digits with `<pin>`.
fn mask_pin(s: &str) -> String {
    let mut out = String::new();
    let mut digits = String::new();
    let mut prev_alnum = false;
    let flush = |digits: &mut String, out: &mut String, next_alnum: bool, prev_alnum: bool| {
        if digits.len() == 6 && !prev_alnum && !next_alnum {
            out.push_str("<pin>");
        } else {
            out.push_str(digits);
        }
        digits.clear();
    };
    for c in s.chars() {
        if c.is_ascii_digit() {
            digits.push(c);
            continue;
        }
        if !digits.is_empty() {
            flush(&mut digits, &mut out, c.is_alphanumeric(), prev_alnum);
        }
        prev_alnum = c.is_alphanumeric();
        out.push(c);
    }
    flush(&mut digits, &mut out, false, prev_alnum);
    out
}

fn civil() -> (i64, u32, u32, u32, u32, u32, u32) {
    let d = SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default();
    let (secs, ms) = (d.as_secs() as i64, d.subsec_millis());
    let (days, rem) = (secs.div_euclid(86400), secs.rem_euclid(86400) as u32);
    // Howard Hinnant's civil_from_days.
    let z = days + 719468;
    let era = z.div_euclid(146097);
    let doe = z.rem_euclid(146097);
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let month = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    let year = yoe + era * 400 + i64::from(month <= 2);
    (year, month, day, rem / 3600, rem % 3600 / 60, rem % 60, ms)
}

/// `2026-11-15T09:30:12.345Z`
pub fn timestamp() -> String {
    let (y, mo, d, h, mi, s, ms) = civil();
    format!("{y:04}-{mo:02}-{d:02}T{h:02}:{mi:02}:{s:02}.{ms:03}Z")
}

/// `20261115-093012`, for file and folder names.
pub fn file_stamp() -> String {
    let (y, mo, d, h, mi, s, _) = civil();
    format!("{y:04}{mo:02}{d:02}-{h:02}{mi:02}{s:02}")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn redacts_tickets_tokens_and_pins() {
        let hex = "ab".repeat(32);
        let s = redact(&format!("ticket mkw1.abc_DEF-1.sig== token {hex} end"));
        assert_eq!(s, "ticket mkw1.<redacted>== token <redacted> end");
        assert_eq!(redact("Your PIN is 123456, ok"), "Your PIN is <pin>, ok");
        assert_eq!(redact("pin = \"123456\""), "pin = \"<pin>\"");
        // Not secrets: ports, short hex, numbers on lines without "pin".
        assert_eq!(redact("port 47820 id 123456 abcdef"), "port 47820 id 123456 abcdef");
        assert_eq!(redact("pin 1234567 pinned 12345"), "pin 1234567 pinned 12345");
    }

    #[test]
    fn keeps_the_newest_sessions() {
        let d = std::env::temp_dir().join(format!("mkwmp-logs-{}", std::process::id()));
        let _ = fs::remove_dir_all(&d);
        fs::create_dir_all(&d).unwrap();
        for i in 0..14 {
            fs::write(d.join(format!("launcher-20260101-0000{i:02}.log")), "x").unwrap();
        }
        fs::write(d.join("other.txt"), "x").unwrap();
        prune(&d, KEEP);
        let left = sessions(&d);
        assert_eq!(left.len(), KEEP);
        assert!(left[0].ends_with("launcher-20260101-000013.log"));
        assert!(left[KEEP - 1].ends_with("launcher-20260101-000004.log"));
        assert!(d.join("other.txt").exists());
        let _ = fs::remove_dir_all(&d);
    }

    #[test]
    fn timestamp_shape() {
        let t = timestamp();
        assert_eq!(t.len(), 24);
        assert!(t.ends_with('Z') && &t[10..11] == "T");
    }
}
