//! Seat manager: N seats, a FIFO queue when full. Each client holds a
//! `Membership` for its whole session; dropping it (client disconnect) frees
//! the seat or leaves the queue, then promotes the next in line.

use std::collections::{HashMap, VecDeque};
use std::sync::{Arc, Mutex};
use tokio::sync::mpsc;

use contracts::mkwmp::seat::v1::{QueueUpdate, State};

pub struct SeatManager {
    inner: Mutex<Inner>,
}

struct Inner {
    capacity: usize,
    next_id: u64,
    next_ticket: u64,
    // Granted seats. Keeping the sender here holds the client's stream open
    // (the "seat held" signal); it is dropped only when the client leaves.
    holders: HashMap<u64, mpsc::UnboundedSender<QueueUpdate>>,
    waiters: VecDeque<Waiter>,
}

struct Waiter {
    id: u64,
    tx: mpsc::UnboundedSender<QueueUpdate>,
}

/// Lives for the client's whole session. On drop, releases the seat or leaves
/// the queue and promotes the next waiter.
pub struct Membership {
    manager: Arc<SeatManager>,
    id: u64,
}

impl Drop for Membership {
    fn drop(&mut self) {
        self.manager.leave(self.id);
    }
}

impl SeatManager {
    pub fn new(capacity: usize) -> Arc<Self> {
        Arc::new(Self {
            inner: Mutex::new(Inner {
                capacity,
                next_id: 0,
                next_ticket: 0,
                holders: HashMap::new(),
                waiters: VecDeque::new(),
            }),
        })
    }

    /// Join: returns the update stream and a membership to hold for the session.
    /// The first update is GRANTED (seat free) or QUEUED (place in line).
    pub fn join(self: &Arc<Self>, _username: &str) -> (mpsc::UnboundedReceiver<QueueUpdate>, Membership) {
        let (tx, rx) = mpsc::unbounded_channel();
        let mut inner = self.inner.lock().unwrap();
        let id = inner.next_id;
        inner.next_id += 1;
        inner.waiters.push_back(Waiter { id, tx });
        self.pump(&mut inner);
        (rx, Membership { manager: Arc::clone(self), id })
    }

    fn leave(self: &Arc<Self>, id: u64) {
        let mut inner = self.inner.lock().unwrap();
        if inner.holders.remove(&id).is_none() {
            inner.waiters.retain(|w| w.id != id);
        }
        self.pump(&mut inner);
    }

    // Promote the front of the queue while seats are free, then renumber the
    // rest. Closed senders (client gone) are dropped.
    fn pump(self: &Arc<Self>, inner: &mut Inner) {
        inner.waiters.retain(|w| !w.tx.is_closed());
        while inner.holders.len() < inner.capacity {
            let Some(w) = inner.waiters.pop_front() else { break };
            inner.next_ticket += 1;
            let ticket = format!("seat-{:016x}", inner.next_ticket);
            let free = inner.capacity - inner.holders.len() - 1;
            let _ = w.tx.send(QueueUpdate {
                state: State::Granted as i32,
                position: 0,
                ticket,
                seats_total: inner.capacity as u32,
                seats_free: free as u32,
            });
            inner.holders.insert(w.id, w.tx); // keep the sender -> stream open
        }
        let total = inner.capacity as u32;
        let free = (inner.capacity - inner.holders.len()) as u32;
        for (i, w) in inner.waiters.iter().enumerate() {
            let _ = w.tx.send(QueueUpdate {
                state: State::Queued as i32,
                position: (i + 1) as u32,
                ticket: String::new(),
                seats_total: total,
                seats_free: free,
            });
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn recv(rx: &mut mpsc::UnboundedReceiver<QueueUpdate>) -> QueueUpdate {
        rx.try_recv().expect("an update")
    }

    #[test]
    fn free_seat_grants_immediately() {
        let m = SeatManager::new(1);
        let (mut rx, _hold) = m.join("ana");
        let u = recv(&mut rx);
        assert_eq!(u.state, State::Granted as i32);
        assert!(!u.ticket.is_empty());
        assert_eq!(u.seats_free, 0);
    }

    #[test]
    fn full_server_queues_then_promotes_on_release() {
        let m = SeatManager::new(1);
        let (mut a, hold_a) = m.join("ana");
        assert_eq!(recv(&mut a).state, State::Granted as i32);
        let (mut b, _hold_b) = m.join("bia");
        let q = recv(&mut b);
        assert_eq!(q.state, State::Queued as i32);
        assert_eq!(q.position, 1);
        drop(hold_a); // ana leaves
        let g = recv(&mut b);
        assert_eq!(g.state, State::Granted as i32);
        assert!(!g.ticket.is_empty());
    }

    #[test]
    fn leaving_the_queue_renumbers_those_behind() {
        let m = SeatManager::new(1);
        let (mut _a, _hold_a) = m.join("ana");       // granted
        let (mut b, hold_b) = m.join("bia");          // pos 1
        let (mut c, _hold_c) = m.join("cid");         // pos 2
        assert_eq!(recv(&mut b).position, 1);
        assert_eq!(recv(&mut c).position, 2);
        drop(hold_b);                                  // bia leaves the queue
        assert_eq!(recv(&mut c).position, 1);
    }
}
