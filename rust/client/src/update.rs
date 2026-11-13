//! Self-update from GitHub Releases, and keeping the game patch (the probe,
//! `winmm.dll`) in step with this launcher's release.
//!
//! Every release carries `SHA256SUMS` and `SHA256SUMS.sig`:
//!
//!   # mkwmp 0.0.1-rc.1
//!   <sha256 hex>  mkwmp-launcher-x86_64-windows.exe
//!   ...
//!
//! The signature is Ed25519 (base64url, no padding) over the exact bytes of
//! SHA256SUMS, by the release key whose public half is [`RELEASE_KEYS`]. A
//! download is used only if the signature verifies, the version inside the
//! signed file is the release's tag, and the file's SHA-256 matches. Any
//! failure leaves what is installed alone; updating never blocks play.

use std::collections::HashMap;
use std::path::{Path, PathBuf};

use base64::engine::general_purpose::URL_SAFE_NO_PAD as B64;
use base64::Engine;
use ed25519_dalek::{Signature, Verifier, VerifyingKey};
use serde::Deserialize;
use sha2::{Digest, Sha256};

pub const VERSION: &str = env!("CARGO_PKG_VERSION");
pub const REPO: &str = "anarqz/mkwmp";
/// Public release keys (`kid:base64url`). The private half never leaves the
/// release machine.
pub const RELEASE_KEYS: &[&str] = &["release1:Zuvb-AuF9DcXEzXRSu49EFrb2ilCQnJl1e4ApQJI9Dw"];
pub const PROBE_ASSET: &str = "mkwmp-probe-winmm.dll";

pub fn launcher_asset() -> Option<&'static str> {
    match (std::env::consts::OS, std::env::consts::ARCH) {
        ("windows", "x86_64") => Some("mkwmp-launcher-x86_64-windows.exe"),
        ("linux", "x86_64") => Some("mkwmp-launcher-x86_64-linux"),
        _ => None,
    }
}

#[derive(Debug, Clone, Deserialize)]
pub struct Release {
    pub tag_name: String,
    #[serde(default)]
    pub draft: bool,
    pub assets: Vec<Asset>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct Asset {
    pub name: String,
    pub browser_download_url: String,
}

impl Release {
    pub fn version(&self) -> Option<semver::Version> {
        semver::Version::parse(self.tag_name.strip_prefix('v')?).ok()
    }

    fn url(&self, name: &str) -> Result<&str, String> {
        self.assets
            .iter()
            .find(|a| a.name == name)
            .map(|a| a.browser_download_url.as_str())
            .ok_or_else(|| format!("release {} has no {name}", self.tag_name))
    }
}

/// Verified `SHA256SUMS`: asset name → sha256 hex.
#[derive(Debug, PartialEq, Eq)]
pub struct Sums(pub HashMap<String, String>);

/// Check SHA256SUMS against its signature and the expected version.
pub fn verify_sums(sums: &[u8], sig_b64: &str, keys: &[&str], version: &semver::Version) -> Result<Sums, String> {
    let sig = B64.decode(sig_b64.trim()).map_err(|_| "malformed signature")?;
    let sig = Signature::from_slice(&sig).map_err(|_| "malformed signature")?;
    let trusted = keys.iter().any(|entry| {
        let Some((_, k)) = entry.split_once(':') else { return false };
        let Ok(bytes) = B64.decode(k) else { return false };
        let Ok(bytes) = <[u8; 32]>::try_from(bytes.as_slice()) else { return false };
        VerifyingKey::from_bytes(&bytes).is_ok_and(|key| key.verify(sums, &sig).is_ok())
    });
    if !trusted {
        return Err("SHA256SUMS signature does not verify".into());
    }
    let text = std::str::from_utf8(sums).map_err(|_| "SHA256SUMS is not text")?;
    let mut lines = text.lines();
    let header = lines.next().unwrap_or("");
    if header.trim() != format!("# mkwmp {version}") {
        return Err(format!("SHA256SUMS is for {header:?}, not {version}"));
    }
    let mut map = HashMap::new();
    for line in lines.filter(|l| !l.trim().is_empty()) {
        let (hash, name) = line.split_once("  ").ok_or("malformed SHA256SUMS line")?;
        if hash.len() != 64 || !hash.bytes().all(|b| b.is_ascii_hexdigit()) {
            return Err("malformed SHA256SUMS hash".into());
        }
        map.insert(name.trim().to_string(), hash.to_ascii_lowercase());
    }
    Ok(Sums(map))
}

pub fn sha256_hex(bytes: &[u8]) -> String {
    Sha256::digest(bytes).iter().map(|b| format!("{b:02x}")).collect()
}

/// The newest non-draft release strictly newer than `current`.
pub fn newest<'a>(releases: &'a [Release], current: &semver::Version) -> Option<&'a Release> {
    releases
        .iter()
        .filter(|r| !r.draft)
        .filter_map(|r| r.version().map(|v| (v, r)))
        .filter(|(v, _)| v > current)
        .max_by(|a, b| a.0.cmp(&b.0))
        .map(|(_, r)| r)
}

pub struct Updater {
    http: reqwest::Client,
    api_base: String,
    keys: Vec<&'static str>,
}

impl Updater {
    pub fn new() -> Self {
        let http = reqwest::Client::builder()
            .timeout(std::time::Duration::from_secs(60))
            .user_agent(concat!("mkwmp-launcher/", env!("CARGO_PKG_VERSION")))
            .build()
            .expect("http client");
        // MKWMP_UPDATE_API points at a test server; the keys stay the built-in ones.
        let api_base = std::env::var("MKWMP_UPDATE_API").unwrap_or_else(|_| "https://api.github.com".into());
        Self { http, api_base: api_base.trim_end_matches('/').into(), keys: RELEASE_KEYS.to_vec() }
    }

    #[cfg(test)]
    pub fn with(api_base: &str, keys: Vec<&'static str>) -> Self {
        Self { http: reqwest::Client::new(), api_base: api_base.into(), keys }
    }

    async fn get(&self, url: &str) -> Result<Vec<u8>, String> {
        let res = self.http.get(url).send().await.map_err(|e| format!("download {url}: {e}"))?;
        if !res.status().is_success() {
            return Err(format!("download {url}: HTTP {}", res.status()));
        }
        Ok(res.bytes().await.map_err(|e| format!("download {url}: {e}"))?.to_vec())
    }

    pub async fn releases(&self) -> Result<Vec<Release>, String> {
        let url = format!("{}/repos/{REPO}/releases?per_page=20", self.api_base);
        let body = self.get(&url).await?;
        serde_json::from_slice(&body).map_err(|e| format!("release list: {e}"))
    }

    pub async fn sums(&self, r: &Release) -> Result<Sums, String> {
        let version = r.version().ok_or("release tag is not a version")?;
        let sums = self.get(r.url("SHA256SUMS")?).await?;
        let sig = self.get(r.url("SHA256SUMS.sig")?).await?;
        verify_sums(&sums, &String::from_utf8_lossy(&sig), &self.keys, &version)
    }

    /// Download `name` from `r` and check it against the verified sums.
    pub async fn asset(&self, r: &Release, sums: &Sums, name: &str) -> Result<Vec<u8>, String> {
        let want = sums.0.get(name).ok_or_else(|| format!("{name} is not in SHA256SUMS"))?;
        let bytes = self.get(r.url(name)?).await?;
        if &sha256_hex(&bytes) != want {
            return Err(format!("{name}: checksum mismatch"));
        }
        Ok(bytes)
    }
}

/// Replace the running executable with `bytes`. The old one is kept as
/// `<exe>.old` (a running .exe on Windows can be renamed, not overwritten).
pub fn replace_self(bytes: &[u8]) -> Result<PathBuf, String> {
    let exe = std::env::current_exe().map_err(|e| e.to_string())?;
    let new = sibling(&exe, "new");
    let old = sibling(&exe, "old");
    std::fs::write(&new, bytes).map_err(|e| format!("write {}: {e}", new.display()))?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        std::fs::set_permissions(&new, std::fs::Permissions::from_mode(0o755)).map_err(|e| e.to_string())?;
    }
    let _ = std::fs::remove_file(&old);
    std::fs::rename(&exe, &old).map_err(|e| format!("move {} aside: {e}", exe.display()))?;
    if let Err(e) = std::fs::rename(&new, &exe) {
        let _ = std::fs::rename(&old, &exe);
        return Err(format!("install the new launcher: {e}"));
    }
    Ok(exe)
}

/// Remove the `<exe>.old` left by the last update.
pub fn clean_old() {
    if let Ok(exe) = std::env::current_exe() {
        let _ = std::fs::remove_file(sibling(&exe, "old"));
    }
}

fn sibling(exe: &Path, ext: &str) -> PathBuf {
    let mut name = exe.file_name().unwrap_or_default().to_os_string();
    name.push(format!(".{ext}"));
    exe.with_file_name(name)
}

/// Check for a newer launcher; if one verifies, install it and return its
/// path (the caller restarts into it). `Ok(None)`: up to date.
pub async fn self_update(u: &Updater) -> Result<Option<PathBuf>, String> {
    let Some(asset) = launcher_asset() else { return Ok(None) };
    let current = semver::Version::parse(VERSION).map_err(|e| e.to_string())?;
    let releases = u.releases().await?;
    let Some(r) = newest(&releases, &current) else { return Ok(None) };
    println!("mkwmp: updating the launcher {VERSION} -> {}", r.tag_name);
    let sums = u.sums(r).await?;
    let bytes = u.asset(r, &sums, asset).await?;
    replace_self(&bytes).map(Some)
}

/// This launcher's own release (tag v<VERSION>), verified.
pub async fn own_release(u: &Updater) -> Result<(Release, Sums), String> {
    let tag = format!("v{VERSION}");
    let releases = u.releases().await?;
    let r = releases.into_iter().find(|r| r.tag_name == tag && !r.draft).ok_or(format!("no release {tag}"))?;
    let sums = u.sums(&r).await?;
    Ok((r, sums))
}

#[cfg(test)]
mod tests {
    use super::*;
    use ed25519_dalek::{Signer, SigningKey};

    const TEST_KEY: &str = "t:bnoc3Smwt4_ROvTFWY_v9O8qlxZuPKby5Pv8zYBQW_E"; // seed [5; 32]

    fn signed(text: &str) -> (Vec<u8>, String) {
        let k = SigningKey::from_bytes(&[5; 32]);
        assert_eq!(format!("t:{}", B64.encode(k.verifying_key().as_bytes())), TEST_KEY);
        (text.as_bytes().to_vec(), B64.encode(k.sign(text.as_bytes()).to_bytes()))
    }

    fn v(s: &str) -> semver::Version {
        semver::Version::parse(s).unwrap()
    }

    const SUMS: &str = "# mkwmp 0.0.2\n\
        aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  mkwmp-launcher-x86_64-linux\n";

    #[test]
    fn sums_verify() {
        let (b, s) = signed(SUMS);
        let sums = verify_sums(&b, &s, &[TEST_KEY], &v("0.0.2")).unwrap();
        assert_eq!(sums.0["mkwmp-launcher-x86_64-linux"].len(), 64);
    }

    #[test]
    fn sums_reject_tampering_wrong_key_and_wrong_version() {
        let (b, s) = signed(SUMS);
        let mut t = b.clone();
        t[20] ^= 1;
        assert!(verify_sums(&t, &s, &[TEST_KEY], &v("0.0.2")).is_err());
        assert!(verify_sums(&b, &s, RELEASE_KEYS, &v("0.0.2")).is_err());
        // A validly signed file replayed under another tag.
        assert!(verify_sums(&b, &s, &[TEST_KEY], &v("0.0.3")).is_err());
        assert!(verify_sums(&b, "garbage", &[TEST_KEY], &v("0.0.2")).is_err());
    }

    #[test]
    fn newest_only_upgrades() {
        let rel = |tag: &str, draft| Release { tag_name: tag.into(), draft, assets: vec![] };
        let list = vec![rel("v0.0.1-rc.1", false), rel("v0.0.1-rc.2", false), rel("v0.0.2", true), rel("junk", false)];
        assert_eq!(newest(&list, &v("0.0.1-rc.1")).unwrap().tag_name, "v0.0.1-rc.2");
        assert!(newest(&list, &v("0.0.1-rc.2")).is_none());
        assert!(newest(&list, &v("0.0.1")).is_none()); // 0.0.1 > 0.0.1-rc.2: never downgrade
    }

    /// A fake GitHub on localhost: release list, signed sums, one asset.
    async fn fake_github(sums_text: String, sig: String, asset: Vec<u8>) -> String {
        use axum::routing::get;
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let base = format!("http://{}", listener.local_addr().unwrap());
        let list = serde_json::json!([{
            "tag_name": "v0.0.2", "draft": false,
            "assets": [
                {"name": "SHA256SUMS", "browser_download_url": format!("{base}/dl/sums")},
                {"name": "SHA256SUMS.sig", "browser_download_url": format!("{base}/dl/sig")},
                {"name": "mkwmp-launcher-x86_64-linux", "browser_download_url": format!("{base}/dl/bin")},
            ]
        }]);
        let app = axum::Router::new()
            .route("/repos/anarqz/mkwmp/releases", get(move || async move { axum::Json(list) }))
            .route("/dl/sums", get(move || async move { sums_text }))
            .route("/dl/sig", get(move || async move { sig }))
            .route("/dl/bin", get(move || async move { asset }));
        tokio::spawn(async move { axum::serve(listener, app).await.unwrap() });
        base
    }

    #[tokio::test]
    async fn downloads_verify_end_to_end() {
        let bin = b"new launcher".to_vec();
        let good = format!("# mkwmp 0.0.2\n{}  mkwmp-launcher-x86_64-linux\n", sha256_hex(&bin));
        let (b, sig) = signed(&good);
        let base = fake_github(String::from_utf8(b).unwrap(), sig, bin.clone()).await;
        let u = Updater::with(&base, vec![TEST_KEY]);
        let releases = u.releases().await.unwrap();
        let r = newest(&releases, &v("0.0.1-rc.1")).unwrap();
        let sums = u.sums(r).await.unwrap();
        assert_eq!(u.asset(r, &sums, "mkwmp-launcher-x86_64-linux").await.unwrap(), bin);

        // The built-in key does not trust the test signature.
        let u = Updater::with(&base, RELEASE_KEYS.to_vec());
        assert!(u.sums(r).await.is_err());
    }

    #[tokio::test]
    async fn swapped_asset_is_rejected() {
        let good = format!("# mkwmp 0.0.2\n{}  mkwmp-launcher-x86_64-linux\n", sha256_hex(b"the real one"));
        let (b, sig) = signed(&good);
        let base = fake_github(String::from_utf8(b).unwrap(), sig, b"malware".to_vec()).await;
        let u = Updater::with(&base, vec![TEST_KEY]);
        let releases = u.releases().await.unwrap();
        let sums = u.sums(&releases[0]).await.unwrap();
        let err = u.asset(&releases[0], &sums, "mkwmp-launcher-x86_64-linux").await.unwrap_err();
        assert!(err.contains("checksum"), "{err}");
    }

    #[test]
    fn sibling_names() {
        assert_eq!(sibling(Path::new("/x/mkwmp-launcher.exe"), "old"), PathBuf::from("/x/mkwmp-launcher.exe.old"));
    }
}
