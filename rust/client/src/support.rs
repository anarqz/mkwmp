//! The support bundle: the logs and facts a bug report needs, collected in
//! `<launcher dir>/logs/bug-report-<stamp>/` and zipped beside it.

use std::fs;
use std::path::{Path, PathBuf};

use sha2::{Digest, Sha256};

use crate::log::{redact, sessions};

/// Redact the secrets of the probe's `mkwmp.toml`.
pub fn redact_toml(text: &str) -> String {
    text.lines()
        .map(|l| {
            let key = l.split('=').next().unwrap_or("").trim();
            if matches!(key, "launcher_token" | "ticket" | "pin") && l.contains('=') {
                format!("{key} = \"<redacted>\"\n")
            } else {
                format!("{}\n", redact(l))
            }
        })
        .collect()
}

fn short_sha(p: &Path) -> String {
    match fs::read(p) {
        Ok(b) => Sha256::digest(&b).iter().take(4).map(|x| format!("{x:02x}")).collect(),
        Err(_) => "?".into(),
    }
}

fn dll_line(game: &Path, name: &str) -> String {
    let p = game.join(name);
    if p.exists() {
        format!("{name}: present, sha256 {}", short_sha(&p))
    } else {
        format!("{name}: missing")
    }
}

fn system_txt(game: &Path) -> String {
    // Against the zip's signed list when there is one (no network here).
    let expected = crate::probe::bundled().map(|b| b.expected);
    let report = crate::probe::check(game, expected.as_ref());
    format!(
        "launcher: {}\nos: {} {}\ngame folder: {}\nmkw-recomp data folder: {}\nmkw-recomp: {:?}\n{}\n{}\nwinmm_host.dll: {}\npatch check: {}\n  {}\ntime: {}\n",
        env!("CARGO_PKG_VERSION"),
        std::env::consts::OS,
        std::env::consts::ARCH,
        game.display(),
        crate::recomp::data_dir(game).display(),
        crate::recomp::identify(game),
        dll_line(game, crate::probe::PROBE),
        dll_line(game, crate::probe::VOICE),
        if game.join(crate::probe::HOST).exists() { "present" } else { "missing" },
        report.summary(env!("CARGO_PKG_VERSION")),
        report.files.join("\n  "),
        crate::log::timestamp(),
    )
}

/// Build the bundle; returns (folder, zip). `dir` is the launcher folder.
pub fn save_bundle(dir: &Path, game: &Path) -> Result<(PathBuf, PathBuf), String> {
    let logs = dir.join("logs");
    let out = logs.join(format!("bug-report-{}", crate::log::file_stamp()));
    fs::create_dir_all(&out).map_err(|e| format!("{}: {e}", out.display()))?;
    // The two newest launcher sessions (this one, then the previous).
    for p in sessions(&logs).into_iter().take(2) {
        let _ = fs::copy(&p, out.join(p.file_name().unwrap_or_default()));
    }
    let _ = fs::copy(game.join("mkwmp.log"), out.join("mkwmp.log"));
    // mkw-recomp's own log lives in its data folder (0.1.12 can move it).
    let data = crate::recomp::data_dir(game);
    let _ = fs::copy(data.join("logs").join("mkw.log"), out.join("mkw.log"));
    let _ = fs::copy(data.join("logs").join("mkw.1.log"), out.join("mkw.1.log"));
    if let Ok(t) = fs::read_to_string(game.join("mkwmp.toml")) {
        let _ = fs::write(out.join("mkwmp.toml"), redact_toml(&t));
    }
    fs::write(out.join("system.txt"), system_txt(game)).map_err(|e| e.to_string())?;
    let zip = out.with_extension("zip");
    zip_folder(&out, &zip).map_err(|e| format!("{}: {e}", zip.display()))?;
    Ok((out, zip))
}

/// GUI / CLI entry: build the bundle and open its folder. Returns a message.
pub fn save_and_open(dir: &Path, game: &Path) -> Result<String, String> {
    let (folder, zip) = save_bundle(dir, game)?;
    crate::log::note(format!("bug report saved: {}", zip.display()));
    let _ = open::that(&folder);
    Ok(format!("Saved {}", zip.display()))
}

fn crc32(data: &[u8]) -> u32 {
    let mut c = !0u32;
    for &b in data {
        c ^= u32::from(b);
        for _ in 0..8 {
            c = if c & 1 != 0 { (c >> 1) ^ 0xEDB8_8320 } else { c >> 1 };
        }
    }
    !c
}

/// A flat zip (stored, no compression) of the files in `folder`.
fn zip_folder(folder: &Path, zip: &Path) -> std::io::Result<()> {
    let mut files: Vec<(String, Vec<u8>)> = vec![];
    for e in fs::read_dir(folder)?.flatten() {
        if e.path().is_file() {
            files.push((e.file_name().to_string_lossy().into_owned(), fs::read(e.path())?));
        }
    }
    files.sort();
    let mut buf: Vec<u8> = vec![];
    let mut central: Vec<u8> = vec![];
    let le16 = |v: &mut Vec<u8>, x: u16| v.extend_from_slice(&x.to_le_bytes());
    let le32 = |v: &mut Vec<u8>, x: u32| v.extend_from_slice(&x.to_le_bytes());
    for (name, data) in &files {
        let (crc, off) = (crc32(data), buf.len() as u32);
        for (v, local) in [(&mut buf, true), (&mut central, false)] {
            le32(v, if local { 0x0403_4b50 } else { 0x0201_4b50 });
            if !local {
                le16(v, 20); // version made by
            }
            le16(v, 20); // version needed
            le16(v, 0x0800); // UTF-8 names
            le16(v, 0); // stored
            le16(v, 0);
            le16(v, 0x21); // time 00:00, date 1980-01-01
            le32(v, crc);
            le32(v, data.len() as u32);
            le32(v, data.len() as u32);
            le16(v, name.len() as u16);
            le16(v, 0); // extra
            if !local {
                le16(v, 0); // comment
                le16(v, 0); // disk
                le16(v, 0); // internal attrs
                le32(v, 0); // external attrs
                le32(v, off);
            }
            v.extend_from_slice(name.as_bytes());
            if local {
                v.extend_from_slice(data);
            }
        }
    }
    let (cd_off, cd_len) = (buf.len() as u32, central.len() as u32);
    buf.extend_from_slice(&central);
    le32(&mut buf, 0x0605_4b50);
    le16(&mut buf, 0);
    le16(&mut buf, 0);
    le16(&mut buf, files.len() as u16);
    le16(&mut buf, files.len() as u16);
    le32(&mut buf, cd_len);
    le32(&mut buf, cd_off);
    le16(&mut buf, 0);
    fs::write(zip, buf)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn toml_secrets_are_masked() {
        let t = "server = \"x:1\"\nlauncher_token = \"abc\"\nticket = \"mkw1.a.b\"\nname = \"Ana\"\n";
        let r = redact_toml(t);
        assert!(r.contains("launcher_token = \"<redacted>\"") && r.contains("ticket = \"<redacted>\""));
        assert!(!r.contains("abc") && !r.contains("mkw1.a") && r.contains("name = \"Ana\""));
    }

    #[test]
    fn crc_known_value() {
        assert_eq!(crc32(b"123456789"), 0xCBF4_3926);
    }

    #[test]
    fn bundle_has_the_files() {
        let d = std::env::temp_dir().join(format!("mkwmp-sup-{}", std::process::id()));
        let _ = fs::remove_dir_all(&d);
        let game = d.join("game");
        fs::create_dir_all(game.join("logs")).unwrap();
        fs::create_dir_all(d.join("logs")).unwrap();
        fs::write(d.join("logs").join("launcher-20260101-000000.log"), "hi").unwrap();
        fs::write(game.join("mkwmp.toml"), "ticket = \"mkw1.a.b\"\n").unwrap();
        fs::write(game.join("mkwmp.log"), "probe").unwrap();
        let (folder, zip) = save_bundle(&d, &game).unwrap();
        assert!(folder.join("system.txt").exists() && folder.join("mkwmp.log").exists());
        assert!(!fs::read_to_string(folder.join("mkwmp.toml")).unwrap().contains("mkw1.a"));
        let z = fs::read(zip).unwrap();
        assert_eq!(&z[..4], b"PK\x03\x04");
        let _ = fs::remove_dir_all(&d);
    }
}
