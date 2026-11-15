//! Keep the game patch in place: `<game>/winmm.dll` is this release's probe,
//! and `<game>/winmm_host.dll` is the real winmm.dll the probe forwards to
//! (the system's on Windows, Proton's under SteamOS; never shipped by us).

use std::path::{Path, PathBuf};

use crate::update::sha256_hex;

pub const PROBE: &str = "winmm.dll";
pub const HOST: &str = "winmm_host.dll";
/// The voice engine the probe loads (native/voice), next to winmm.dll.
pub const VOICE: &str = "mkwmp-voice.dll";

#[derive(Debug, PartialEq, Eq)]
pub enum ProbeAction {
    UpToDate,
    /// Wrote the probe; `backup` is where a foreign winmm.dll was saved.
    Installed { backup: Option<PathBuf> },
    /// The game is running; the file is left alone.
    GameRunning,
}

/// Put `bytes` (already verified) at `<game>/winmm.dll` unless it is there.
/// A winmm.dll we did not write before is kept as winmm.dll.bak.
pub fn install_probe(game: &Path, bytes: &[u8], running: bool) -> std::io::Result<ProbeAction> {
    let path = game.join(PROBE);
    let current = std::fs::read(&path).ok();
    if current.as_deref().map(sha256_hex) == Some(sha256_hex(bytes)) {
        return Ok(ProbeAction::UpToDate);
    }
    if running {
        return Ok(ProbeAction::GameRunning);
    }
    let marker = game.join(".mkwmp-probe");
    let ours = std::fs::read_to_string(&marker).ok();
    let mut backup = None;
    if let Some(cur) = &current {
        if ours.as_deref().map(str::trim) != Some(&sha256_hex(cur)) {
            let bak = game.join("winmm.dll.bak");
            if !bak.exists() {
                std::fs::write(&bak, cur)?;
                backup = Some(bak);
            }
        }
    }
    let tmp = game.join("winmm.dll.mkwmp-new");
    std::fs::write(&tmp, bytes)?;
    std::fs::rename(&tmp, &path)?;
    std::fs::write(&marker, sha256_hex(bytes))?;
    Ok(ProbeAction::Installed { backup })
}

/// Put `bytes` (already verified) at `<game>/<name>` unless it is there: a
/// file only mkwmp ships (the voice engine), so nothing is backed up.
pub fn install_file(game: &Path, name: &str, bytes: &[u8], running: bool) -> std::io::Result<ProbeAction> {
    let path = game.join(name);
    if std::fs::read(&path).ok().as_deref().map(sha256_hex) == Some(sha256_hex(bytes)) {
        return Ok(ProbeAction::UpToDate);
    }
    if running {
        return Ok(ProbeAction::GameRunning);
    }
    let tmp = game.join(format!("{name}.mkwmp-new"));
    std::fs::write(&tmp, bytes)?;
    std::fs::rename(&tmp, &path)?;
    Ok(ProbeAction::Installed { backup: None })
}

/// Files only mkwmp puts in the game folder (removed by [`uninstall`]),
/// besides winmm.dll, which is removed only when it is ours.
const OURS: &[&str] = &[
    HOST,
    VOICE,
    ".mkwmp-probe",
    "mkwmp.toml",
    "mkwmp.toml.tmp",
    "mkwmp-lobby-ca.pem",
    "mkwmp.log",
    "winmm.dll.mkwmp-new",
    "mkwmp-voice.dll.mkwmp-new",
];

/// The hashes this launcher's game patch must have (from the release zip's
/// `game/` folder, or this launcher's own signed SHA256SUMS).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Expected {
    pub version: String,
    pub probe: String,
    pub voice: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PatchState {
    /// Every file is there and is this launcher's version.
    Installed,
    /// Ours and complete, but nothing to compare with (no zip copy, offline).
    Unverified,
    /// No mkwmp file in the folder.
    Missing,
    /// mkwmp's files from another version.
    Outdated,
    /// A file is missing, changed or foreign.
    Damaged,
    /// The patch is fine but the real winmm.dll it forwards to is not there.
    HostMissing,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PatchReport {
    pub state: PatchState,
    /// One line per file, for the hover text and the bug report.
    pub files: Vec<String>,
}

impl PatchReport {
    /// PLAY may start the game.
    pub fn ok(&self) -> bool {
        matches!(self.state, PatchState::Installed | PatchState::Unverified)
    }

    pub fn summary(&self, version: &str) -> String {
        match self.state {
            PatchState::Installed => format!("Patches installed (v{version})"),
            PatchState::Unverified => "Patches installed (not verified: offline)".into(),
            PatchState::Missing => "Not installed: press INSTALL".into(),
            PatchState::Outdated => "Outdated (other version): press INSTALL to update".into(),
            PatchState::Damaged => "Modified or damaged: press INSTALL to repair".into(),
            PatchState::HostMissing => "winmm_host.dll missing: press INSTALL".into(),
        }
    }
}

/// Is `<game>/winmm.dll` the probe we wrote (its hash is in `.mkwmp-probe`)?
fn probe_is_ours(game: &Path) -> bool {
    let (Ok(cur), Ok(mark)) = (std::fs::read(game.join(PROBE)), std::fs::read_to_string(game.join(".mkwmp-probe"))) else {
        return false;
    };
    mark.trim() == sha256_hex(&cur)
}

/// Is this an mkwmp probe, whoever copied it (it logs "mkwmp probe <version>")?
fn looks_like_probe(bytes: &[u8]) -> bool {
    const MARK: &[u8] = b"mkwmp probe ";
    bytes.starts_with(b"MZ") && bytes.windows(MARK.len()).any(|w| w == MARK)
}

fn hash_of(p: &Path) -> Option<String> {
    std::fs::read(p).ok().map(|b| sha256_hex(&b))
}

/// A real winmm.dll: a PE whose export table names timeBeginPeriod and
/// timeEndPeriod (what the probe forwards to).
fn looks_like_winmm(b: &[u8]) -> bool {
    pe_exports(b).is_some_and(|names| {
        ["timeBeginPeriod", "timeEndPeriod"].iter().all(|want| names.iter().any(|n| n == want))
    })
}

/// The exported names of a PE file (None: not a PE, or malformed).
fn pe_exports(b: &[u8]) -> Option<Vec<String>> {
    let u16_at = |o: usize| Some(u16::from_le_bytes(b.get(o..o + 2)?.try_into().ok()?));
    let u32_at = |o: usize| Some(u32::from_le_bytes(b.get(o..o + 4)?.try_into().ok()?));
    if b.get(0..2)? != b"MZ" {
        return None;
    }
    let pe = u32_at(0x3c)? as usize;
    if b.get(pe..pe + 4)? != b"PE\0\0" {
        return None;
    }
    let coff = pe + 4;
    let sections = u16_at(coff + 2)? as usize;
    let opt = coff + 20;
    let opt_size = u16_at(coff + 16)? as usize;
    let dirs = match u16_at(opt)? {
        0x10b => opt + 96,  // PE32
        0x20b => opt + 112, // PE32+
        _ => return None,
    };
    let export_rva = u32_at(dirs)? as usize;
    if export_rva == 0 {
        return Some(vec![]);
    }
    let table = opt + opt_size;
    let to_off = |rva: usize| -> Option<usize> {
        (0..sections.min(96)).find_map(|i| {
            let h = table + i * 40;
            let (vsize, va, raw_size, raw) =
                (u32_at(h + 8)? as usize, u32_at(h + 12)? as usize, u32_at(h + 16)? as usize, u32_at(h + 20)? as usize);
            (rva >= va && rva < va + vsize.max(raw_size)).then(|| raw + (rva - va))
        })
    };
    let exp = to_off(export_rva)?;
    let count = (u32_at(exp + 24)? as usize).min(10_000);
    let names = to_off(u32_at(exp + 32)? as usize)?;
    let mut out = Vec::with_capacity(count);
    for i in 0..count {
        let at = to_off(u32_at(names + i * 4)? as usize)?;
        let end = at + b.get(at..)?.iter().take(256).position(|&c| c == 0)?;
        out.push(String::from_utf8_lossy(&b[at..end]).into_owned());
    }
    Some(out)
}

/// Check the game folder against `expected` (None: compare with nothing,
/// only that our own files are complete).
pub fn check(game: &Path, expected: Option<&Expected>) -> PatchReport {
    let mut files = vec![];
    let probe_bytes = std::fs::read(game.join(PROBE)).ok();
    let probe = probe_bytes.as_deref().map(sha256_hex);
    let voice = hash_of(&game.join(VOICE));
    let host = std::fs::read(game.join(HOST)).ok();
    let ours = probe_is_ours(game) || probe_bytes.as_deref().is_some_and(looks_like_probe);
    if probe.is_none() && voice.is_none() && !OURS.iter().filter(|f| **f != HOST).any(|f| game.join(f).exists()) {
        return PatchReport { state: PatchState::Missing, files: vec!["no mkwmp file in the game folder".into()] };
    }
    let mut outdated = false;
    let mut damaged = false;
    match (&probe, expected) {
        (None, _) => {
            files.push("winmm.dll: missing".into());
            damaged = true;
        }
        (Some(h), Some(e)) if *h == e.probe => files.push(format!("winmm.dll: v{} ✓", e.version)),
        (Some(_), Some(_)) if ours => {
            files.push("winmm.dll: another mkwmp version".into());
            outdated = true;
        }
        (Some(_), None) if ours => files.push("winmm.dll: mkwmp's (not verified)".into()),
        (Some(_), _) => {
            files.push("winmm.dll: not mkwmp's, or changed".into());
            damaged = true;
        }
    }
    match (&voice, expected.map(|e| (&e.voice, &e.version))) {
        (None, Some((None, _))) => {} // a release without voice chat
        (None, _) => {
            files.push("mkwmp-voice.dll: missing".into());
            damaged = true;
        }
        (Some(h), Some((Some(want), v))) if h == want => files.push(format!("mkwmp-voice.dll: v{v} ✓")),
        (Some(_), Some(_)) => {
            files.push("mkwmp-voice.dll: another version, or changed".into());
            outdated = true;
        }
        (Some(_), None) => files.push("mkwmp-voice.dll: present (not verified)".into()),
    }
    let host_ok = match &host {
        None => {
            files.push("winmm_host.dll: missing".into());
            false
        }
        Some(b) if Some(sha256_hex(b)) == probe || !looks_like_winmm(b) => {
            files.push("winmm_host.dll: not the system's winmm.dll".into());
            damaged = true;
            false
        }
        Some(_) => {
            files.push("winmm_host.dll: the system's winmm.dll ✓".into());
            true
        }
    };
    if let Some(e) = expected {
        for other in ["winmm.dll.bak", "winmm.dll.mkwmp-new", HOST] {
            if hash_of(&game.join(other)).as_deref() == Some(&e.probe) {
                files.push(format!("note: this version's probe is also at {other}"));
            }
        }
    }
    let state = if damaged {
        PatchState::Damaged
    } else if outdated {
        PatchState::Outdated
    } else if !host_ok {
        PatchState::HostMissing
    } else if expected.is_some() {
        PatchState::Installed
    } else {
        PatchState::Unverified
    };
    PatchReport { state, files }
}

/// Take the game folder back to how it was before mkwmp: our winmm.dll goes
/// (a winmm.dll.bak we saved is put back), and every file in [`OURS`].
/// Returns what it did, one line per file.
pub fn uninstall(game: &Path, running: bool) -> Result<Vec<String>, String> {
    if running {
        return Err("Close the game first".into());
    }
    if !game.join("mkw.exe").exists() {
        return Err("Set the game folder (the one with mkw.exe)".into());
    }
    let mut done = vec![];
    let rm = |name: &str, done: &mut Vec<String>| -> Result<(), String> {
        let p = game.join(name);
        match std::fs::remove_file(&p) {
            Ok(()) => {
                done.push(format!("removed {name}"));
                Ok(())
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
            Err(e) => Err(format!("remove {}: {e}", p.display())),
        }
    };
    if probe_is_ours(game) {
        rm(PROBE, &mut done)?;
        let bak = game.join("winmm.dll.bak");
        if bak.exists() {
            std::fs::rename(&bak, game.join(PROBE)).map_err(|e| format!("restore {}: {e}", bak.display()))?;
            done.push("put your previous winmm.dll back".into());
        }
    } else if game.join(PROBE).exists() {
        done.push("left winmm.dll alone (not mkwmp's)".into());
    }
    for f in OURS {
        rm(f, &mut done)?;
    }
    Ok(done)
}

/// The game patch shipped next to the launcher in the release zip:
/// `game/winmm.dll`, `game/mkwmp-voice.dll` and `game/SHA256SUMS(.sig)`, a
/// list signed with the release key. Used only when the list verifies for
/// this launcher's version (after a self-update the zip's copy is older) and
/// the files match it.
pub struct Bundle {
    pub expected: Expected,
    pub probe: Vec<u8>,
    pub voice: Option<Vec<u8>>,
}

pub fn bundled() -> Option<Bundle> {
    let exe = std::env::current_exe().ok()?;
    bundled_in(&exe.parent()?.join("game"), crate::update::VERSION, crate::update::RELEASE_KEYS)
}

fn bundled_in(dir: &Path, version: &str, keys: &[&str]) -> Option<Bundle> {
    let sums = std::fs::read(dir.join("SHA256SUMS")).ok()?;
    let sig = std::fs::read_to_string(dir.join("SHA256SUMS.sig")).ok()?;
    let v = semver::Version::parse(version).ok()?;
    let sums = crate::update::verify_sums(&sums, &sig, keys, &v).ok()?;
    let expected = Expected {
        version: version.to_string(),
        probe: sums.0.get(crate::update::PROBE_ASSET)?.clone(),
        voice: sums.0.get(crate::update::VOICE_ASSET).cloned(),
    };
    let probe = std::fs::read(dir.join(PROBE)).ok().filter(|b| sha256_hex(b) == expected.probe)?;
    let voice = match &expected.voice {
        Some(h) => Some(std::fs::read(dir.join(VOICE)).ok().filter(|b| sha256_hex(b) == *h)?),
        None => None,
    };
    Some(Bundle { expected, probe, voice })
}

/// Where the real winmm.dll can be copied from on this machine.
pub fn host_source() -> Option<PathBuf> {
    if cfg!(windows) {
        let root = std::env::var_os("SystemRoot").map(PathBuf::from).unwrap_or_else(|| "C:\\Windows".into());
        let p = root.join("System32").join("winmm.dll");
        return p.exists().then_some(p);
    }
    // SteamOS / Linux: Proton 10.0's own winmm.dll, in any Steam library.
    let home = PathBuf::from(std::env::var_os("HOME")?);
    let rel = Path::new("steamapps/common/Proton 10.0/files/lib/wine/x86_64-windows/winmm.dll");
    let mut libraries = vec![home.join(".steam/steam"), home.join(".local/share/Steam")];
    for vdf in [home.join(".steam/steam/steamapps/libraryfolders.vdf"), home.join(".local/share/Steam/steamapps/libraryfolders.vdf")] {
        if let Ok(text) = std::fs::read_to_string(vdf) {
            libraries.extend(library_paths(&text));
        }
    }
    libraries.into_iter().map(|l| l.join(rel)).find(|p| p.exists())
}

/// `"path"  "/run/media/deck/SD"` lines of Steam's libraryfolders.vdf.
fn library_paths(vdf: &str) -> Vec<PathBuf> {
    vdf.lines()
        .filter_map(|l| {
            let mut parts = l.split('"').filter(|s| !s.trim().is_empty());
            (parts.next()? == "path").then(|| parts.next().map(PathBuf::from)).flatten()
        })
        .collect()
}

/// Make sure winmm_host.dll exists. Returns a message when it had to act or
/// could not.
pub fn ensure_host(game: &Path) -> Option<String> {
    let dest = game.join(HOST);
    if dest.exists() {
        return None;
    }
    match host_source() {
        Some(src) => match std::fs::copy(&src, &dest) {
            Ok(_) => Some(format!("copied {} to {}", src.display(), dest.display())),
            Err(e) => Some(format!("could not copy {} to {}: {e}", src.display(), dest.display())),
        },
        None if cfg!(windows) => Some(format!("copy C:\\Windows\\System32\\winmm.dll to {}", dest.display())),
        None => Some(format!(
            "copy Proton 10.0's files/lib/wine/x86_64-windows/winmm.dll to {} (install Proton 10.0 in Steam first)",
            dest.display()
        )),
    }
}

/// Is mkw.exe running (natively or under Proton)?
pub fn game_running() -> bool {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        // CREATE_NO_WINDOW: from the windowed launcher a console program would flash a terminal window.
        std::process::Command::new("tasklist")
            .args(["/FI", "IMAGENAME eq mkw.exe", "/NH"])
            .creation_flags(0x0800_0000)
            .output()
            .is_ok_and(|o| String::from_utf8_lossy(&o.stdout).to_ascii_lowercase().contains("mkw.exe"))
    }
    #[cfg(not(windows))]
    {
        let Ok(dir) = std::fs::read_dir("/proc") else { return false };
        dir.flatten().any(|e| {
            std::fs::read(e.path().join("cmdline"))
                .is_ok_and(|c| c.split(|&b| b == 0).any(|arg| arg.ends_with(b"mkw.exe")))
        })
    }
}

/// A minimal PE32+ with one section holding an export table naming two functions.
#[cfg(test)]
pub(crate) fn fake_winmm() -> Vec<u8> {
    let mut b = vec![0u8; 0x400];
    b[0..2].copy_from_slice(b"MZ");
    b[0x3c..0x40].copy_from_slice(&0x80u32.to_le_bytes());
    b[0x80..0x84].copy_from_slice(b"PE\0\0");
    let coff = 0x84;
    b[coff + 2..coff + 4].copy_from_slice(&1u16.to_le_bytes());
    b[coff + 16..coff + 18].copy_from_slice(&240u16.to_le_bytes());
    let opt = coff + 20;
    b[opt..opt + 2].copy_from_slice(&0x20bu16.to_le_bytes());
    b[opt + 112..opt + 116].copy_from_slice(&0x1000u32.to_le_bytes()); // export dir RVA
    let sec = opt + 240;
    for (o, v) in [(8, 0x200u32), (12, 0x1000), (16, 0x200), (20, 0x200)] {
        b[sec + o..sec + o + 4].copy_from_slice(&v.to_le_bytes());
    }
    let exp = 0x200; // file offset of RVA 0x1000
    b[exp + 24..exp + 28].copy_from_slice(&2u32.to_le_bytes());
    b[exp + 32..exp + 36].copy_from_slice(&0x1040u32.to_le_bytes()); // name pointers
    b[0x240..0x244].copy_from_slice(&0x1080u32.to_le_bytes());
    b[0x244..0x248].copy_from_slice(&0x1090u32.to_le_bytes());
    b[0x280..0x28f].copy_from_slice(b"timeBeginPeriod");
    b[0x290..0x29d].copy_from_slice(b"timeEndPeriod");
    b
}

#[cfg(test)]
mod tests {
    use super::*;

    fn dir(name: &str) -> PathBuf {
        let d = std::env::temp_dir().join(format!("mkwmp_probe_{name}"));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    #[test]
    fn installs_backs_up_foreign_dll_once_then_updates_quietly() {
        let d = dir("install");
        std::fs::write(d.join(PROBE), b"someone else's dll").unwrap();
        let a = install_probe(&d, b"probe v1", false).unwrap();
        assert_eq!(a, ProbeAction::Installed { backup: Some(d.join("winmm.dll.bak")) });
        assert_eq!(std::fs::read(d.join("winmm.dll.bak")).unwrap(), b"someone else's dll");
        assert_eq!(install_probe(&d, b"probe v1", false).unwrap(), ProbeAction::UpToDate);
        // Our own older probe is replaced without another backup.
        assert_eq!(install_probe(&d, b"probe v2", false).unwrap(), ProbeAction::Installed { backup: None });
        assert_eq!(std::fs::read(d.join(PROBE)).unwrap(), b"probe v2");
        assert_eq!(std::fs::read(d.join("winmm.dll.bak")).unwrap(), b"someone else's dll");
    }

    #[test]
    fn voice_dll_installs_and_updates() {
        let d = dir("voice");
        assert_eq!(install_file(&d, VOICE, b"v1", false).unwrap(), ProbeAction::Installed { backup: None });
        assert_eq!(install_file(&d, VOICE, b"v1", false).unwrap(), ProbeAction::UpToDate);
        assert_eq!(install_file(&d, VOICE, b"v2", true).unwrap(), ProbeAction::GameRunning);
        install_file(&d, VOICE, b"v2", false).unwrap();
        assert_eq!(std::fs::read(d.join(VOICE)).unwrap(), b"v2");
    }

    #[test]
    fn never_touches_a_running_game() {
        let d = dir("running");
        std::fs::write(d.join(PROBE), b"old").unwrap();
        assert_eq!(install_probe(&d, b"new", true).unwrap(), ProbeAction::GameRunning);
        assert_eq!(std::fs::read(d.join(PROBE)).unwrap(), b"old");
    }

    #[test]
    fn uninstall_restores_the_folder() {
        let d = dir("uninstall");
        std::fs::write(d.join("mkw.exe"), b"").unwrap();
        std::fs::write(d.join(PROBE), b"someone else's dll").unwrap();
        assert_eq!(check(&d, None).state, PatchState::Damaged, "a foreign winmm.dll is not our patch");
        install_probe(&d, b"probe", false).unwrap();
        install_file(&d, VOICE, b"voice", false).unwrap();
        for f in ["mkwmp.toml", "mkwmp-lobby-ca.pem", "mkwmp.log"] {
            std::fs::write(d.join(f), b"x").unwrap();
        }
        std::fs::write(d.join(HOST), b"host").unwrap();
        assert!(uninstall(&d, true).is_err());
        uninstall(&d, false).unwrap();
        let mut left: Vec<_> = std::fs::read_dir(&d).unwrap().map(|e| e.unwrap().file_name().into_string().unwrap()).collect();
        left.sort();
        assert_eq!(left, ["mkw.exe", "winmm.dll"]);
        assert_eq!(std::fs::read(d.join(PROBE)).unwrap(), b"someone else's dll");
    }

    #[test]
    fn uninstall_leaves_a_foreign_winmm() {
        let d = dir("uninstall_foreign");
        std::fs::write(d.join("mkw.exe"), b"").unwrap();
        std::fs::write(d.join(PROBE), b"not ours").unwrap();
        std::fs::write(d.join(VOICE), b"v").unwrap();
        uninstall(&d, false).unwrap();
        assert_eq!(std::fs::read(d.join(PROBE)).unwrap(), b"not ours");
        assert!(!d.join(VOICE).exists());
        assert_eq!(check(&d, None).state, PatchState::Damaged);
    }

    #[test]
    fn check_tells_each_state() {
        let d = dir("check");
        std::fs::write(d.join("mkw.exe"), b"").unwrap();
        let want = Expected { version: "1.0.0".into(), probe: sha256_hex(b"p1"), voice: Some(sha256_hex(b"v1")) };
        assert_eq!(check(&d, Some(&want)).state, PatchState::Missing);
        install_probe(&d, b"p1", false).unwrap();
        install_file(&d, VOICE, b"v1", false).unwrap();
        assert_eq!(check(&d, Some(&want)).state, PatchState::HostMissing);
        std::fs::write(d.join(HOST), b"p1").unwrap(); // our probe copied as the host
        assert_eq!(check(&d, Some(&want)).state, PatchState::Damaged);
        std::fs::write(d.join(HOST), fake_winmm()).unwrap();
        let r = check(&d, Some(&want));
        assert_eq!(r.state, PatchState::Installed, "{:?}", r.files);
        assert!(r.ok());
        assert_eq!(check(&d, None).state, PatchState::Unverified);
        // Copied by hand (no .mkwmp-probe), nothing to compare with: ours by content.
        std::fs::remove_file(d.join(".mkwmp-probe")).unwrap();
        assert_eq!(check(&d, None).state, PatchState::Damaged, "not a probe by content");
        std::fs::write(d.join(PROBE), b"MZ...mkwmp probe 1.0.0...").unwrap();
        assert_eq!(check(&d, None).state, PatchState::Unverified);
        install_probe(&d, b"p1", false).unwrap();
        install_probe(&d, b"p0", false).unwrap(); // an older mkwmp probe
        assert_eq!(check(&d, Some(&want)).state, PatchState::Outdated);
        std::fs::write(d.join(PROBE), b"tampered").unwrap();
        assert_eq!(check(&d, Some(&want)).state, PatchState::Damaged);
        assert!(!check(&d, Some(&want)).ok());
    }

    #[test]
    fn bundled_patch_needs_its_signed_list() {
        use base64::Engine;
        use ed25519_dalek::{Signer, SigningKey};
        const KEY: &str = "t:bnoc3Smwt4_ROvTFWY_v9O8qlxZuPKby5Pv8zYBQW_E"; // seed [5; 32]
        let d = dir("bundle");
        let k = SigningKey::from_bytes(&[5; 32]);
        let sign = |t: &str| base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(k.sign(t.as_bytes()).to_bytes());
        assert!(bundled_in(&d, "1.0.0", &[KEY]).is_none());
        std::fs::write(d.join(PROBE), b"p").unwrap();
        std::fs::write(d.join(VOICE), b"v").unwrap();
        let list = format!("# mkwmp 1.0.0\n{}  winmm.dll\n{}  mkwmp-voice.dll\n", sha256_hex(b"p"), sha256_hex(b"v"));
        std::fs::write(d.join("SHA256SUMS"), &list).unwrap();
        std::fs::write(d.join("SHA256SUMS.sig"), sign(&list)).unwrap();
        let b = bundled_in(&d, "1.0.0", &[KEY]).unwrap();
        assert_eq!((b.probe.as_slice(), b.voice.as_deref()), (&b"p"[..], Some(&b"v"[..])));
        assert!(bundled_in(&d, "1.0.1", &[KEY]).is_none(), "a self-updated launcher ignores the older zip copy");
        assert!(bundled_in(&d, "1.0.0", crate::update::RELEASE_KEYS).is_none(), "only the release key");
        std::fs::write(d.join(VOICE), b"changed").unwrap();
        assert!(bundled_in(&d, "1.0.0", &[KEY]).is_none(), "files must match the list");
    }

    #[test]
    fn pe_exports_of_a_real_winmm() {
        let b = fake_winmm();
        assert_eq!(pe_exports(&b).unwrap(), ["timeBeginPeriod", "timeEndPeriod"]);
        assert!(looks_like_winmm(&b));
        assert!(!looks_like_winmm(b"MZ timeBeginPeriod timeEndPeriod"), "names in the bytes are not exports");
    }

    #[test]
    fn parses_steam_library_folders() {
        let vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"/home/deck/.local/share/Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"/run/media/deck/SN01T\"\n\t\t\"label\"\t\t\"\"\n\t}\n}\n";
        assert_eq!(library_paths(vdf), vec![PathBuf::from("/home/deck/.local/share/Steam"), PathBuf::from("/run/media/deck/SN01T")]);
    }
}
