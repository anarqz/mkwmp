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
    fn parses_steam_library_folders() {
        let vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"/home/deck/.local/share/Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"/run/media/deck/SN01T\"\n\t\t\"label\"\t\t\"\"\n\t}\n}\n";
        assert_eq!(library_paths(vdf), vec![PathBuf::from("/home/deck/.local/share/Steam"), PathBuf::from("/run/media/deck/SN01T")]);
    }
}
