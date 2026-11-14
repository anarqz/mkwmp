//! Gateway events through `tracing` (target "gateway"), next to the Core's.

pub fn line(msg: impl AsRef<str>) {
    tracing::info!(target: "gateway", "{}", msg.as_ref());
}
