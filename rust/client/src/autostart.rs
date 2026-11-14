//! "Start with the system": the launcher starts at login, straight into the
//! tray (`--tray`). Windows: the per-user Run key. Linux: an XDG autostart
//! entry. Other systems: not offered.

/// Whether this system supports it.
pub fn supported() -> bool {
    cfg!(any(windows, target_os = "linux"))
}

/// Make the system's start-up entry match `on` (also refreshes the path after
/// the launcher moved). Errors are reported, never fatal.
pub fn apply(on: bool) -> Result<(), String> {
    let exe = std::env::current_exe().map_err(|e| e.to_string())?;
    imp::apply(on, &exe).map_err(|e| format!("start with the system: {e}"))
}

#[cfg(windows)]
mod imp {
    use winreg::enums::HKEY_CURRENT_USER;
    use winreg::RegKey;

    const RUN: &str = r"Software\Microsoft\Windows\CurrentVersion\Run";
    const NAME: &str = "MKWMP";

    pub fn apply(on: bool, exe: &std::path::Path) -> std::io::Result<()> {
        let (run, _) = RegKey::predef(HKEY_CURRENT_USER).create_subkey(RUN)?;
        if on {
            run.set_value(NAME, &format!("\"{}\" --tray", exe.display()))
        } else {
            match run.delete_value(NAME) {
                Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
                r => r,
            }
        }
    }
}

#[cfg(target_os = "linux")]
mod imp {
    pub fn apply(on: bool, exe: &std::path::Path) -> std::io::Result<()> {
        let dir = std::env::var_os("XDG_CONFIG_HOME")
            .map(std::path::PathBuf::from)
            .or_else(|| std::env::var_os("HOME").map(|h| std::path::PathBuf::from(h).join(".config")))
            .ok_or_else(|| std::io::Error::other("no home folder"))?
            .join("autostart");
        let file = dir.join("mkwmp-launcher.desktop");
        if !on {
            return match std::fs::remove_file(&file) {
                Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
                r => r,
            };
        }
        std::fs::create_dir_all(&dir)?;
        let exec = exe.display().to_string().replace('"', "\\\"");
        std::fs::write(
            file,
            format!(
                "[Desktop Entry]\nType=Application\nName=MKWMP\nComment=MKWMP launcher (tray)\nExec=\"{exec}\" --tray\nTerminal=false\nX-GNOME-Autostart-enabled=true\n"
            ),
        )
    }
}

#[cfg(not(any(windows, target_os = "linux")))]
mod imp {
    pub fn apply(_on: bool, _exe: &std::path::Path) -> std::io::Result<()> {
        Ok(())
    }
}
