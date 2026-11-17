//! Which mkw-recomp the game folder holds. The game patch hooks one runtime
//! build (addresses, symbols, fiber and host-function layout), so MKWMP runs
//! only with the mkw-recomp releases in SUPPORTED. Each release's mkw.exe (the
//! Windows build, also what runs under Proton) is identified by its SHA-256,
//! taken from the official release zips (0.1.13's also carries the string
//! "0.1.13", older ones carry none).
//!
//! The game patch also hooks one game version (guest addresses): GAMES lists
//! the ones it has tables for, read from mkw-recomp's setup-state.json.

use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::SystemTime;

use crate::update::sha256_hex;

/// The runtimes the game patch supports (KNOWN names their mkw.exe).
pub const SUPPORTED: &[&str] = &["0.1.10", "0.1.12", "0.1.13"];
/// What the window recommends installing.
pub const RECOMMENDED: &str = "0.1.13";
pub const DOWNLOAD: &str = "https://github.com/adriadam10/mkw-recomp/releases/tag/v0.1.13";

/// mkw.exe SHA-256 per mkw-recomp release (mkw-recomp-<v>-windows-x64.zip).
const KNOWN: &[(&str, &str)] = &[
    ("0.1.10", "4fe39fc71d04ed46caf07476e9cdc688f7438f472983c424a2d27e62febcbdcb"),
    ("0.1.11", "8d65b3e3beae4411ea0bf195da8841dc2f23414bc85eedfa593c9f39f54d2a43"),
    ("0.1.12", "fbd215a5f232e9fc786bb4a7e5823d0d7a64ad04e09f50ed37b1cb2d5d66d1b1"),
    ("0.1.13", "1c47ffa2502a1ee92c2d01da0791802c18bf6c3365040c2b4fa1c91dacbc8695"),
];

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Recomp {
    /// A supported release.
    Supported(&'static str),
    /// Another known release.
    Other(&'static str),
    /// Not an mkw.exe from any release we know (older, newer, or modified).
    Unknown,
    /// No mkw.exe in the folder.
    Missing,
}

impl Recomp {
    pub fn ok(&self) -> bool {
        matches!(self, Recomp::Supported(_))
    }

    /// One line for the window; None when supported.
    pub fn problem(&self) -> Option<String> {
        match self {
            Recomp::Supported(_) => None,
            Recomp::Other(v) => Some(format!("mkw-recomp {v}: MKWMP needs {}", supported())),
            Recomp::Unknown => Some(format!("Unknown mkw-recomp: MKWMP needs {}", supported())),
            Recomp::Missing => Some("Set the game folder (the one with mkw.exe)".into()),
        }
    }

    /// Why, and what to do (hover text, logs).
    pub fn explain(&self) -> String {
        match self {
            Recomp::Supported(v) => format!("mkw-recomp {v}: supported by this MKWMP."),
            Recomp::Missing => "No mkw.exe in this folder.".into(),
            _ => format!(
                "The MKWMP game patch hooks specific mkw-recomp releases ({}); other versions crash or can't \
                 connect. Install the Windows build of mkw-recomp {RECOMMENDED} ({DOWNLOAD}) with your game \
                 files, and pick that folder.",
                supported()
            ),
        }
    }
}

fn supported() -> String {
    SUPPORTED.join(" or ")
}

/// Tests' stand-in for a supported mkw.exe.
#[cfg(test)]
pub const TEST_EXE: &[u8] = b"mkwmp test mkw.exe";

pub fn identify_bytes(exe: &[u8]) -> Recomp {
    #[cfg(test)]
    if exe == TEST_EXE {
        return Recomp::Supported(SUPPORTED[0]);
    }
    let h = sha256_hex(exe);
    match KNOWN.iter().find(|(_, k)| *k == h) {
        Some((v, _)) if SUPPORTED.contains(v) => Recomp::Supported(v),
        Some((v, _)) => Recomp::Other(v),
        None => Recomp::Unknown,
    }
}

/// The last answer, keyed by mkw.exe's path, size and modification time:
/// hashing 33 MB on every frame would be wasteful.
/// (mkw.exe path, size, modified, answer).
type Cached = (PathBuf, u64, Option<SystemTime>, Recomp);
static CACHE: Mutex<Option<Cached>> = Mutex::new(None);

pub fn identify(game: &Path) -> Recomp {
    let exe = game.join("mkw.exe");
    let Ok(meta) = std::fs::metadata(&exe) else { return Recomp::Missing };
    let key = (exe.clone(), meta.len(), meta.modified().ok());
    let mut c = CACHE.lock().unwrap_or_else(|p| p.into_inner());
    if let Some((p, len, at, r)) = c.as_ref() {
        if (p, *len, *at) == (&key.0, key.1, key.2) {
            return r.clone();
        }
    }
    let r = std::fs::read(&exe).map(|b| identify_bytes(&b)).unwrap_or(Recomp::Missing);
    *c = Some((key.0, key.1, key.2, r.clone()));
    r
}

/// Where mkw-recomp keeps this installation's data (mkwgame.dll, mkw.ini,
/// logs/, profile/): the folder with mkw.exe, unless mkw-recomp 0.1.12's
/// "choose game folder" wrote `game_dir = "<path>"` into the mkw.ini beside
/// mkw.exe (absolute, or relative to that folder). The game patch's own
/// files always stay beside mkw.exe.
pub fn data_dir(game: &Path) -> PathBuf {
    let Ok(ini) = std::fs::read_to_string(game.join("mkw.ini")) else { return game.to_path_buf() };
    for line in ini.lines() {
        let Some((k, v)) = line.split_once('=') else { continue };
        if !k.trim().eq_ignore_ascii_case("game_dir") {
            continue;
        }
        let v = v.trim();
        let v = v.strip_prefix('"').and_then(|x| x.strip_suffix('"')).unwrap_or(v);
        if v.is_empty() {
            break;
        }
        let b = v.as_bytes();
        let absolute = b[0] == b'/' || b[0] == b'\\' || b.get(1) == Some(&b':');
        return if absolute { PathBuf::from(v) } else { game.join(v) };
    }
    game.to_path_buf()
}

/// Game versions the game patch has guest-address tables for. On any other
/// version its hooks would write into unrelated game memory.
pub const GAMES: &[&str] = &["1.0.0"];
/// Live tests only: `MKWMP_ALLOW_GAME_180=1` also lets game 1.8.0 play (the game patch
/// switches its version-specific hooks by build itself).
pub const ALLOW_180_ENV: &str = "MKWMP_ALLOW_GAME_180";

fn allowed(version: &str) -> bool {
    GAMES.contains(&version) || (version == "1.8.0" && std::env::var(ALLOW_180_ENV).is_ok_and(|v| v == "1"))
}

/// The game version mkw-recomp set up, from `<data dir>/setup-state.json`
/// (`"generate": "<RT_GAME_ID> <game version> <hash>"`, under `done`). None
/// when the file or field is missing or unreadable.
/// Cached by the file's path, size and modification time (the window asks every frame).
pub fn game_version(game: &Path) -> Option<String> {
    type Cached = (PathBuf, u64, Option<SystemTime>, Option<String>);
    static GAME_CACHE: Mutex<Option<Cached>> = Mutex::new(None);
    let file = data_dir(game).join("setup-state.json");
    let meta = std::fs::metadata(&file).ok()?;
    let key = (file, meta.len(), meta.modified().ok());
    let mut c = GAME_CACHE.lock().unwrap_or_else(|p| p.into_inner());
    if let Some((p, len, at, v)) = c.as_ref() {
        if (p, *len, *at) == (&key.0, key.1, key.2) {
            return v.clone();
        }
    }
    let v = std::fs::read_to_string(&key.0).ok().and_then(|t| game_version_in(&t));
    *c = Some((key.0, key.1, key.2, v.clone()));
    v
}

/// The second word of `generate`, if it looks like a version (dotted digits);
/// anything else counts as unknown rather than refusing a layout we don't know.
fn game_version_in(json: &str) -> Option<String> {
    let v: serde_json::Value = serde_json::from_str(json).ok()?;
    let generate = v.pointer("/done/generate").or_else(|| v.get("generate")).and_then(|g| g.as_str())?;
    let word = generate.split_whitespace().nth(1)?;
    let dotted = word.split('.').count() >= 2 && word.split('.').all(|p| !p.is_empty() && p.bytes().all(|b| b.is_ascii_digit()));
    dotted.then(|| word.to_owned())
}

/// Why PLAY is refused for this game version; None when supported or unknown.
pub fn game_problem(game: &Path) -> Option<String> {
    game_problem_for(game_version(game).as_deref())
}

fn game_problem_for(version: Option<&str>) -> Option<String> {
    let v = version?;
    (!allowed(v)).then(|| format!("Game {v}: MKWMP supports game {}; {v} support is coming", GAMES.join(" or ")))
}

/// One line for logs and bug reports.
pub fn game_line(game: &Path) -> String {
    match game_version(game) {
        Some(v) if GAMES.contains(&v.as_str()) => format!("game version: {v}"),
        Some(v) if allowed(&v) => format!("game version: {v} (allowed by {ALLOW_180_ENV}=1, live test)"),
        Some(v) => format!("game version: {v} (not supported)"),
        None => "game version: unknown (no setup-state.json generate)".into(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_supported_releases_pass() {
        assert_eq!(identify_bytes(b"not mkw.exe"), Recomp::Unknown);
        assert!(!Recomp::Unknown.ok() && Recomp::Unknown.problem().unwrap().contains("0.1.13"));
        assert!(Recomp::Other("0.1.11").problem().unwrap().contains("0.1.11: MKWMP needs 0.1.10 or 0.1.12 or 0.1.13"));
        assert_eq!(identify_bytes(&[]), Recomp::Unknown);
        assert!(Recomp::Supported("0.1.10").ok() && Recomp::Supported("0.1.10").problem().is_none());
        assert!(SUPPORTED.iter().all(|v| KNOWN.iter().any(|(k, _)| k == v)), "every supported release has a known hash");
    }

    #[test]
    fn identify_reads_the_folder_and_notices_a_new_exe() {
        let d = std::env::temp_dir().join(format!("mkwmp_recomp_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(&d).unwrap();
        assert_eq!(identify(&d), Recomp::Missing);
        std::fs::write(d.join("mkw.exe"), b"some build").unwrap();
        assert_eq!(identify(&d), Recomp::Unknown);
        std::fs::write(d.join("mkw.exe"), b"another build, other size").unwrap();
        assert_eq!(identify(&d), Recomp::Unknown);
    }

    #[test]
    fn data_dir_follows_game_dir_in_mkw_ini() {
        let d = std::env::temp_dir().join(format!("mkwmp_recomp_dir_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(&d).unwrap();
        assert_eq!(data_dir(&d), d);
        std::fs::write(d.join("mkw.ini"), "[launcher]\nupdate_check = 1\n").unwrap();
        assert_eq!(data_dir(&d), d);
        std::fs::write(d.join("mkw.ini"), "[launcher]\ngame_dir = \"/games/mkw data\"\n").unwrap();
        assert_eq!(data_dir(&d), PathBuf::from("/games/mkw data"));
        std::fs::write(d.join("mkw.ini"), "GAME_DIR = \"D:\\\\MKW\"\r\n").unwrap();
        assert_eq!(data_dir(&d), PathBuf::from("D:\\\\MKW"));
        std::fs::write(d.join("mkw.ini"), "game_dir = \"install\"\n").unwrap();
        assert_eq!(data_dir(&d), d.join("install"));
    }

    #[test]
    fn game_version_gate() {
        // Real files (paths shortened): 0.1.12 on game 1.0.0, 0.1.13 on game 1.8.0 (key order differs).
        let v1 = r#"{"done":{"exefs":"C:\\x\\ROM\\base\\exefs","romfs":"copy 1.0.0","compile":"c0dd6cf12c07c23a 1.0.0 windows c5ac38fc744c91bd","shaders":"1.0.0 6a07c53dc829807b","warm":"c0dd6cf12c07c23a 1.0.0","cleanup":"","generate":"c0dd6cf12c07c23a 1.0.0 bdcbce92237e51cf"},"source":"C:\\x\\ROM","romfs_mode":"copy","version":1,"port":"0.1.12"}"#;
        let j = r#"{"done":{"exefs":"C:\\x\\updates\\917504\\exefs","romfs":"link C:\\x\\updates\\917504\\romfs","generate":"c0dd6cf12c07c23a 1.8.0 b05897af8b40fec6","compile":"c0dd6cf12c07c23a 1.8.0 windows 41829b71d97ad65a","shaders":"1.8.0 6b8e0b1447f058ce","warm":"c0dd6cf12c07c23a 1.8.0","cleanup":""},"source":"C:\\x\\updates\\917504","romfs_mode":"link","version":1,"port":"0.1.13"}"#;
        assert_eq!(game_version_in(v1).as_deref(), Some("1.0.0"));
        assert_eq!(game_version_in(j).as_deref(), Some("1.8.0"));
        // A much older recomp put a path there: unknown, not refused (its mkw.exe fails the hash check anyway).
        assert_eq!(game_version_in(r#"{"done":{"generate":"6866c415aadb9a39 C:\\Users\\a\\MKW [ROM]\\base\\exefs"}}"#), None);
        assert_eq!(game_version_in(r#"{"generate": "c0dd6cf12c07c23a 1.0.0 x"}"#).as_deref(), Some("1.0.0"));
        assert_eq!(game_version_in(r#"{"done": {}}"#), None);
        assert_eq!(game_version_in("not json"), None);
        assert_eq!(game_version_in(r#"{"done": {"generate": "c0dd6cf12c07c23a b05897af8b40fec6"}}"#), None, "no version word");
        assert_eq!(game_version_in(r#"{"done": {"generate": "c0dd6cf12c07c23a 1.8.0"}}"#).as_deref(), Some("1.8.0"));
        assert_eq!(game_problem_for(Some("1.0.0")), None);
        assert_eq!(game_problem_for(None), None, "unknown is allowed");
        let p = game_problem_for(Some("1.8.0")).unwrap();
        assert!(p.contains("MKWMP supports game 1.0.0") && p.contains("1.8.0 support is coming"), "{p}");

        let d = std::env::temp_dir().join(format!("mkwmp_recomp_game_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(d.join("data")).unwrap();
        assert_eq!(game_problem(&d), None);
        assert!(game_line(&d).contains("unknown"));
        std::fs::write(d.join("mkw.ini"), "game_dir = \"data\"\n").unwrap();
        std::fs::write(d.join("data/setup-state.json"), j).unwrap();
        assert_eq!(game_version(&d).as_deref(), Some("1.8.0"), "read from the data dir");
        assert!(game_problem(&d).is_some() && game_line(&d).contains("not supported"));
        // The live-test override lets 1.8.0 (only) through. This is the only test that reads it.
        std::env::set_var(ALLOW_180_ENV, "1");
        let (on, other, line) = (game_problem(&d), game_problem_for(Some("1.9.0")), game_line(&d));
        std::env::remove_var(ALLOW_180_ENV);
        assert_eq!(on, None);
        assert!(other.is_some() && line.contains("live test"), "{line}");
    }
}
