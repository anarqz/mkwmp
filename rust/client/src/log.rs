//! Launcher messages: printed for console mode and logs. The window shows
//! state instead.

pub fn note(msg: impl Into<String>) {
    println!("mkwmp: {}", msg.into());
}
