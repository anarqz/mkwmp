//! Launcher messages: printed (console mode) and kept for the window.

use std::collections::VecDeque;
use std::sync::Mutex;

static LINES: Mutex<VecDeque<String>> = Mutex::new(VecDeque::new());
const KEEP: usize = 50;

pub fn note(msg: impl Into<String>) {
    let msg = msg.into();
    println!("mkwmp: {msg}");
    let mut l = LINES.lock().unwrap();
    if l.len() == KEEP {
        l.pop_front();
    }
    l.push_back(msg);
}

/// The last `n` messages, oldest first.
pub fn recent(n: usize) -> Vec<String> {
    let l = LINES.lock().unwrap();
    l.iter().skip(l.len().saturating_sub(n)).cloned().collect()
}
