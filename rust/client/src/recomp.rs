//! Which mkw-recomp the game folder holds. The game patch hooks one runtime
//! build (addresses, symbols, fiber and host-function layout), so MKWMP runs
//! only with the mkw-recomp releases in SUPPORTED. `mkw.exe` carries no version string; each
//! release's mkw.exe (the Windows build, also what runs under Proton) has its
//! own SHA-256, taken from the official release zips.

use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::SystemTime;

use crate::update::sha256_hex;

/// The runtimes the game patch supports (KNOWN names their mkw.exe).
pub const SUPPORTED: &[&str] = &["0.1.10", "0.1.12"];
/// What the window recommends installing.
pub const RECOMMENDED: &str = "0.1.10";
pub const DOWNLOAD: &str = "https://github.com/adriadam10/mkw-recomp/releases/tag/v0.1.10";

/// mkw.exe SHA-256 per mkw-recomp release (mkw-recomp-<v>-windows-x64.zip).
const KNOWN: &[(&str, &str)] = &[
    ("0.1.10", "4fe39fc71d04ed46caf07476e9cdc688f7438f472983c424a2d27e62febcbdcb"),
    ("0.1.11", "8d65b3e3beae4411ea0bf195da8841dc2f23414bc85eedfa593c9f39f54d2a43"),
    ("0.1.12", "fbd215a5f232e9fc786bb4a7e5823d0d7a64ad04e09f50ed37b1cb2d5d66d1b1"),
    // 0.1.13: the probe's names, .ovr, Cpu prefix, 64-slot table and detour prologues were checked
    // against this mkw.exe (resources/docs/RECOMP-0.1.13.md), but nobody played it, so it is known
    // and not supported: the window can name the version instead of saying "Unknown".
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_supported_releases_pass() {
        assert_eq!(identify_bytes(b"not mkw.exe"), Recomp::Unknown);
        assert!(!Recomp::Unknown.ok() && Recomp::Unknown.problem().unwrap().contains("0.1.10"));
        assert!(Recomp::Other("0.1.11").problem().unwrap().contains("0.1.11: MKWMP needs 0.1.10 or 0.1.12"));
        assert_eq!(identify_bytes(&[]), Recomp::Unknown);
        assert!(Recomp::Supported("0.1.10").ok() && Recomp::Supported("0.1.10").problem().is_none());
        assert!(SUPPORTED.iter().all(|v| KNOWN.iter().any(|(k, _)| k == v)), "every supported release has a known hash");
        assert!(KNOWN.contains(&("0.1.13", "1c47ffa2502a1ee92c2d01da0791802c18bf6c3365040c2b4fa1c91dacbc8695")) && !SUPPORTED.contains(&"0.1.13"), "0.1.13 is known by hash only: no live test yet");
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
}
