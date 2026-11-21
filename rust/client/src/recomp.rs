//! Which mkw-recomp the game folder holds. The game patch hooks one runtime
//! build (addresses, symbols, fiber and host-function layout), so MKWMP runs
//! only with the mkw-recomp releases in SUPPORTED. Each release's mkw.exe (the
//! Windows build, also what runs under Proton) is identified by its SHA-256,
//! taken from the release zips: the official ones (0.1.13+ also carry their
//! version string, older ones none) and MKWMP's fork, 0.1.15-mkwmp
//! (github.com/anarqz/mkw-recomp), which adds the camera to the plugin API.
//!
//! The game patch also hooks one game version (guest addresses): GAMES lists
//! the ones it has tables for, read from mkw-recomp's setup-state.json.

use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::SystemTime;

use crate::update::sha256_hex;

/// The runtimes the game patch supports (KNOWN names their mkw.exe).
pub const SUPPORTED: &[&str] = &["0.1.10", "0.1.12", "0.1.13", "0.1.15", "0.1.15-mkwmp", "0.1.16", "0.1.16-mkwmp", "0.1.16-mkwmp.1"];
/// What the window recommends installing.
pub const RECOMMENDED: &str = "0.1.16-mkwmp.1";
pub const DOWNLOAD: &str = "https://github.com/anarqz/mkw-recomp/releases/tag/v0.1.16-mkwmp.1";

/// mkw.exe SHA-256 per mkw-recomp release (mkw-recomp-<v>-windows-x64.zip).
const KNOWN: &[(&str, &str)] = &[
    ("0.1.10", "4fe39fc71d04ed46caf07476e9cdc688f7438f472983c424a2d27e62febcbdcb"),
    ("0.1.11", "8d65b3e3beae4411ea0bf195da8841dc2f23414bc85eedfa593c9f39f54d2a43"),
    ("0.1.12", "fbd215a5f232e9fc786bb4a7e5823d0d7a64ad04e09f50ed37b1cb2d5d66d1b1"),
    ("0.1.13", "1c47ffa2502a1ee92c2d01da0791802c18bf6c3365040c2b4fa1c91dacbc8695"),
    ("0.1.15", "511148040eac4f71cc3f003ace2d100fef784f1756339d88ca939896016d86e9"),
    ("0.1.15-mkwmp", "c6d732115683fff57ab1cd7e68414934c4fa132b2a5afc48247a8826797d58b9"),
    ("0.1.16", "e1829a08a7e24ab0f54425fd5e6a400a6fe5c54798757a80a104268e6c18e029"),
    ("0.1.16-mkwmp", "73997d10f8cca878e9654b174c194833053a529ec04ff3b03be587c1e592eb8f"),
    // The fork's build for CPUs without AVX2 (…-windows-x64-noavx2.zip): the same release.
    ("0.1.16-mkwmp", "0bceb91d6156af779f5fad48963908fc39e4bf0d477f826e0d7106ec4605a34f"),
    // .1: the setup runs under Wine/Proton; no SDL x11 request under Proton. Same game id.
    ("0.1.16-mkwmp.1", "d04dc49a14618dce8385cdb1c3e61ba1bd62a2e5938ddf7dd216c926517cc72d"),
    ("0.1.16-mkwmp.1", "59af1fcc9c3a233e2d86c8437224f90a958e9ecb473118d71d92090379fbe3c0"),
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
/// Tests' stand-in for a supported mkw.exe that loads mods.
#[cfg(test)]
pub const TEST_MOD_EXE: &[u8] = b"mkwmp test mkw.exe with mods";

pub fn identify_bytes(exe: &[u8]) -> Recomp {
    #[cfg(test)]
    if exe == TEST_EXE {
        return Recomp::Supported(SUPPORTED[0]);
    }
    #[cfg(test)]
    if exe == TEST_MOD_EXE {
        return Recomp::Supported(MOD_RUNTIMES[0]);
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

/// MKWMP's mod folder name, under mkw-recomp's `mods/`.
pub const MOD: &str = "mkwmp";
/// The supported releases that load mods (MKW_MODS, plugin API 3).
pub const MOD_RUNTIMES: &[&str] = &["0.1.15", "0.1.15-mkwmp", "0.1.16", "0.1.16-mkwmp", "0.1.16-mkwmp.1"];

/// The MKWMP mod runs in this game: a runtime that loads mods, and the mod
/// enabled there ([`mod_dir`]). A leftover `mods = mkwmp` on an older
/// runtime doesn't count: nothing would load it.
pub fn active_mod(game: &Path) -> Option<PathBuf> {
    match identify(game) {
        Recomp::Supported(v) if MOD_RUNTIMES.contains(&v) => mod_dir(game),
        _ => None,
    }
}

/// The MKWMP mod's folder when mkw-recomp (0.1.15+) loads it, by the same rule
/// as the probe: `mkwmp` is in MKW_MODS (the environment wins, else `mods`
/// under `[launcher]` in the installation's mkw.ini, the data dir's when
/// game_dir is set) and `<data dir>/mods/mkwmp/plugin/mkwmp.dll` (or the
/// Linux .so) exists. The mod then carries the game patch, so the launcher
/// keeps no winmm.dll of its own beside mkw.exe (the mod wins).
pub fn mod_dir(game: &Path) -> Option<PathBuf> {
    let data = data_dir(game);
    let mods = match std::env::var("MKW_MODS") {
        Ok(v) => v,
        Err(_) => mods_in(&std::fs::read_to_string(data.join("mkw.ini")).ok()?)?,
    };
    if !mods_list_has(&mods, MOD) {
        return None;
    }
    let dir = data.join("mods").join(MOD);
    ["mkwmp.dll", "libmkwmp.so", "mkwmp.so"].iter().any(|f| dir.join("plugin").join(f).is_file()).then_some(dir)
}

/// The `mods` value under `[launcher]`.
fn mods_in(ini: &str) -> Option<String> {
    let mut launcher = false;
    for line in ini.lines() {
        let line = line.trim();
        if line.starts_with('[') {
            launcher = line.trim_start_matches('[').trim_end_matches(']').trim().eq_ignore_ascii_case("launcher");
            continue;
        }
        let Some((k, v)) = line.split_once('=') else { continue };
        if launcher && k.trim() == "mods" {
            return Some(v.trim().to_owned());
        }
    }
    None
}

/// mkw-recomp's list: names between commas, leading spaces skipped. Compared
/// in any case, as the probe does (Windows opens the folder in any case).
/// Quotes around the whole value are accepted.
fn mods_list_has(list: &str, name: &str) -> bool {
    let list = list.trim();
    let list = list.strip_prefix('"').and_then(|l| l.strip_suffix('"')).unwrap_or(list);
    list.split(',').any(|m| m.trim().eq_ignore_ascii_case(name))
}

/// Game versions the game patch has guest-address tables for. On any other
/// version its hooks would write into unrelated game memory. Players only meet
/// others on the same version (the lobby keeps them apart).
pub const GAMES: &[&str] = &["1.0.0", "1.8.0"];

fn allowed(version: &str) -> bool {
    GAMES.contains(&version)
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

/// The game build id each runtime compiles the game for (`<data dir>/mkwgame.id`,
/// also the first word of setup-state.json's `generate`). A runtime refuses a
/// game set up by another one until its start screen has set it up again.
const GAME_IDS: &[(&str, &str)] = &[
    ("0.1.12", "c0dd6cf12c07c23a"),
    ("0.1.13", "c0dd6cf12c07c23a"),
    ("0.1.15", "c9bbcb75a9ac9a49"),
    ("0.1.15-mkwmp", "c0dd6cf12c07c23a"),
    ("0.1.16", "c9bbcb75a9ac9a49"),
    ("0.1.16-mkwmp", "c9bbcb75a9ac9a49"),
    ("0.1.16-mkwmp.1", "c9bbcb75a9ac9a49"),
];

/// The id the game in this folder was set up for: mkwgame.id, else the
/// first word of `generate`. Cached by the file's path, size and time.
fn game_id(game: &Path) -> Option<String> {
    type Cached = (PathBuf, u64, Option<SystemTime>, Option<String>);
    static ID_CACHE: Mutex<Option<Cached>> = Mutex::new(None);
    let data = data_dir(game);
    let file = [data.join("mkwgame.id"), data.join("setup-state.json")].into_iter().find(|f| f.is_file())?;
    let meta = std::fs::metadata(&file).ok()?;
    let key = (file, meta.len(), meta.modified().ok());
    let mut c = ID_CACHE.lock().unwrap_or_else(|p| p.into_inner());
    if let Some((p, len, at, v)) = c.as_ref() {
        if (p, *len, *at) == (&key.0, key.1, key.2) {
            return v.clone();
        }
    }
    let text = std::fs::read_to_string(&key.0).ok();
    let v = text.and_then(|t| if key.0.ends_with("mkwgame.id") { id_word(&t) } else { game_id_in(&t) });
    *c = Some((key.0, key.1, key.2, v.clone()));
    v
}

fn id_word(t: &str) -> Option<String> {
    // The id starts the file (or the word): 16 hex digits.
    let w = t.trim_start().get(..16)?;
    w.bytes().all(|b| b.is_ascii_hexdigit()).then(|| w.to_ascii_lowercase())
}

fn game_id_in(json: &str) -> Option<String> {
    let v: serde_json::Value = serde_json::from_str(json).ok()?;
    let generate = v.pointer("/done/generate").or_else(|| v.get("generate")).and_then(|g| g.as_str())?;
    id_word(generate)
}

/// The game was set up by another runtime: PLAY would stop at once.
fn rebuild_problem(runtime: &Recomp, id: Option<&str>) -> Option<String> {
    let Recomp::Supported(v) = runtime else { return None };
    let (_, want) = GAME_IDS.iter().find(|(r, _)| r == v)?;
    (id? != *want).then(|| "mkw-recomp was updated: open mkw-recomp once and let it finish its setup, then press PLAY again".into())
}

/// Why PLAY is refused for this game version, or because the game must be
/// set up again for this runtime; None when fine or unknown.
pub fn game_problem(game: &Path) -> Option<String> {
    game_problem_for(game_version(game).as_deref()).or_else(|| rebuild_problem(&identify(game), game_id(game).as_deref()))
}

fn game_problem_for(version: Option<&str>) -> Option<String> {
    let v = version?;
    (!allowed(v)).then(|| format!("Game {v}: MKWMP supports game {} (a newer MKWMP may support {v})", GAMES.join(" or ")))
}

/// One line for logs and bug reports.
pub fn game_line(game: &Path) -> String {
    match game_version(game) {
        Some(v) if allowed(&v) => format!("game version: {v}"),
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
        assert!(Recomp::Other("0.1.11").problem().unwrap().contains("0.1.11: MKWMP needs 0.1.10 or 0.1.12 or 0.1.13 or 0.1.15 or 0.1.15-mkwmp or 0.1.16 or 0.1.16-mkwmp or 0.1.16-mkwmp.1"));
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
    fn the_mod_is_found_only_when_enabled() {
        assert!(mods_list_has("mkwmp", "mkwmp"));
        assert!(mods_list_has("other,mkwmp", "mkwmp") && mods_list_has("other, mkwmp", "mkwmp"));
        assert!(mods_list_has("\"other,mkwmp\"", "mkwmp"));
        assert!(mods_list_has("MKWMP", "mkwmp"), "any case, like the probe");
        assert!(!mods_list_has("mkwmp2,other", "mkwmp") && !mods_list_has("", "mkwmp"));
        assert_eq!(mods_in("[launcher]\r\nmods = other,mkwmp\r\n").as_deref(), Some("other,mkwmp"));
        assert_eq!(mods_in("[video]\nmods = mkwmp\n"), None, "only under [launcher]");

        if std::env::var_os("MKW_MODS").is_some() {
            return; // the environment would win over the files below
        }
        let d = std::env::temp_dir().join(format!("mkwmp_recomp_mod_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(d.join("mods/mkwmp/plugin")).unwrap();
        assert_eq!(mod_dir(&d), None);
        std::fs::write(d.join("mkw.ini"), "[launcher]\nmods = mkwmp\n").unwrap();
        assert_eq!(mod_dir(&d), None, "enabled, but no plugin/mkwmp.dll");
        std::fs::write(d.join("mods/mkwmp/plugin/mkwmp.dll"), b"mod").unwrap();
        assert_eq!(mod_dir(&d), Some(d.join("mods/mkwmp")));
        std::fs::write(d.join("mkw.ini"), "[launcher]\nmods = other\n").unwrap();
        assert_eq!(mod_dir(&d), None, "installed but not enabled");
        // 0.1.12+ "choose game folder": mods/ and the mods key live in the data dir.
        std::fs::create_dir_all(d.join("data/mods/mkwmp/plugin")).unwrap();
        std::fs::write(d.join("data/mods/mkwmp/plugin/mkwmp.dll"), b"mod").unwrap();
        std::fs::write(d.join("mkw.ini"), "[launcher]\ngame_dir = \"data\"\nmods = mkwmp\n").unwrap();
        assert_eq!(mod_dir(&d), None, "the data dir's mkw.ini decides");
        std::fs::write(d.join("data/mkw.ini"), "[launcher]\nmods = mkwmp\n").unwrap();
        assert_eq!(mod_dir(&d), Some(d.join("data/mods/mkwmp")));
    }

    #[test]
    fn a_game_set_up_by_another_runtime_needs_a_rebuild() {
        let s15 = Recomp::Supported("0.1.15");
        assert!(rebuild_problem(&s15, Some("c0dd6cf12c07c23a")).unwrap().contains("open mkw-recomp once"));
        assert_eq!(rebuild_problem(&s15, Some("c9bbcb75a9ac9a49")), None);
        assert_eq!(rebuild_problem(&Recomp::Supported("0.1.13"), Some("c0dd6cf12c07c23a")), None);
        assert!(rebuild_problem(&Recomp::Supported("0.1.13"), Some("c9bbcb75a9ac9a49")).is_some());
        assert_eq!(rebuild_problem(&s15, None), None, "unknown is allowed");
        let fork = Recomp::Supported("0.1.15-mkwmp");
        assert_eq!(rebuild_problem(&fork, Some("c0dd6cf12c07c23a")), None, "the fork keeps 0.1.13's id");
        assert!(rebuild_problem(&fork, Some("c9bbcb75a9ac9a49")).is_some(), "coming from official 0.1.15");
        assert_eq!(rebuild_problem(&Recomp::Supported("0.1.10"), Some("anything")), None, "no id known for 0.1.10");
        assert_eq!(rebuild_problem(&Recomp::Unknown, Some("c0dd6cf12c07c23a")), None);
        assert_eq!(id_word("C9BBCB75A9AC9A49\n").as_deref(), Some("c9bbcb75a9ac9a49"));
        assert_eq!(id_word("not an id at all"), None);
        assert_eq!(id_word("c9bbcb75a9ac9a49 1.8.0 windows").as_deref(), Some("c9bbcb75a9ac9a49"));
        assert_eq!(game_id_in(r#"{"done":{"generate":"c9bbcb75a9ac9a49 1.8.0 ab"}}"#).as_deref(), Some("c9bbcb75a9ac9a49"));

        let d = std::env::temp_dir().join(format!("mkwmp_recomp_id_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(&d).unwrap();
        assert_eq!(game_id(&d), None);
        std::fs::write(d.join("setup-state.json"), r#"{"done":{"generate":"c0dd6cf12c07c23a 1.8.0 ab"}}"#).unwrap();
        assert_eq!(game_id(&d).as_deref(), Some("c0dd6cf12c07c23a"));
        std::fs::write(d.join("mkwgame.id"), "c9bbcb75a9ac9a49\n").unwrap();
        assert_eq!(game_id(&d).as_deref(), Some("c9bbcb75a9ac9a49"), "mkwgame.id first");
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
        assert_eq!(game_problem_for(Some("1.8.0")), None);
        assert_eq!(game_problem_for(None), None, "unknown is allowed");
        let p = game_problem_for(Some("1.9.0")).unwrap();
        assert!(p.contains("MKWMP supports game 1.0.0 or 1.8.0"), "{p}");

        let d = std::env::temp_dir().join(format!("mkwmp_recomp_game_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(d.join("data")).unwrap();
        assert_eq!(game_problem(&d), None);
        assert!(game_line(&d).contains("unknown"));
        std::fs::write(d.join("mkw.ini"), "game_dir = \"data\"\n").unwrap();
        std::fs::write(d.join("data/setup-state.json"), j).unwrap();
        assert_eq!(game_version(&d).as_deref(), Some("1.8.0"), "read from the data dir");
        assert_eq!(game_problem(&d), None);
        assert_eq!(game_line(&d), "game version: 1.8.0");
        std::fs::write(d.join("data/setup-state.json"), r#"{"done": {"generate": "c0dd6cf12c07c23a 1.9.0 ab"}}"#).unwrap();
        assert!(game_problem(&d).is_some() && game_line(&d).contains("not supported"));
    }
}
