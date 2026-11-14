//! One timestamped line per event on stderr (the journal on the VM).

pub fn line(msg: impl AsRef<str>) {
    let now = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap_or_default();
    let (s, ms) = (now.as_secs(), now.subsec_millis());
    let (h, m, sec) = ((s / 3600) % 24, (s / 60) % 60, s % 60);
    eprintln!("{h:02}:{m:02}:{sec:02}.{ms:03} {}", msg.as_ref());
}
