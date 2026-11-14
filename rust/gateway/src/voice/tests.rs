use super::*;
use std::sync::Mutex;

/// Tickets are "t:<uid>"; rooms are set per test.
#[derive(Default)]
struct Fake {
    rooms: Mutex<HashMap<String, (u128, Vec<String>)>>,
}

impl Fake {
    fn put(&self, room: u128, members: &[&str]) {
        let m: Vec<String> = members.iter().map(|s| s.to_string()).collect();
        let mut r = self.rooms.lock().unwrap();
        r.retain(|_, (id, _)| *id != room);
        for u in members {
            r.insert(u.to_string(), (room, m.clone()));
        }
    }
    fn remove(&self, uid: &str) {
        self.rooms.lock().unwrap().remove(uid);
    }
}

impl Rooms for Fake {
    fn user(&self, ticket: &str) -> Option<String> {
        ticket.strip_prefix("mkw1.t:").map(str::to_string)
    }
    fn room(&self, uid: &str) -> Option<(u128, Vec<String>)> {
        self.rooms.lock().unwrap().get(uid).cloned()
    }
    fn account(&self, uid: &str) -> u64 {
        uid.bytes().fold(7u64, |a, b| a * 31 + b as u64)
    }
}

fn ep(n: u16) -> SocketAddr {
    SocketAddr::from(([203, 0, 113, 1], 40000 + n))
}

fn sub(uid: &str, op: u8, flags: u8, mutes: Vec<u64>) -> Vec<u8> {
    write_control(&Control { op, self_flags: flags, mutes, ticket: format!("mkw1.t:{uid}") }, &[9; 12])
}

fn voice(seq: u16, flags: u8) -> Vec<u8> {
    let mut txid = [0u8; 12];
    txid[0] = VERSION;
    txid[1] = flags;
    txid[2..4].copy_from_slice(&seq.to_be_bytes());
    txid[4..8].copy_from_slice(&(seq as u32 * 3).to_be_bytes());
    write(KIND_VOICE, &txid, ATTR_VOICE_UP, &[0xAB; 40])
}

fn voice_to(out: &Datagrams) -> Vec<(SocketAddr, u8)> {
    let mut v: Vec<_> = out
        .iter()
        .filter_map(|(to, d)| parse(d, KIND_VOICE, ATTR_VOICE_DOWN).map(|(t, _)| (*to, t[8])))
        .collect();
    v.sort();
    v
}

fn states_to(out: &Datagrams, who: SocketAddr) -> Vec<(bool, u8, Vec<SeatState>)> {
    out.iter().filter(|(to, _)| *to == who).filter_map(|(_, d)| parse_state(d)).collect()
}

/// a, b, c in room 1 (ep 1, 2, 3), d alone in room 2 (ep 4), all subscribed.
fn setup() -> (Arc<Fake>, Relay<Fake>) {
    let f = Arc::new(Fake::default());
    f.put(1, &["a", "b", "c"]);
    f.put(2, &["d"]);
    let mut r = Relay::new(f.clone());
    for (i, u) in ["a", "b", "c", "d"].iter().enumerate() {
        r.handle(0.0, ep(i as u16 + 1), &sub(u, OP_SUBSCRIBE, 0, vec![]));
    }
    (f, r)
}

#[test]
fn wire_round_trips() {
    let c = Control { op: OP_REPLACE_MUTES, self_flags: 3, mutes: vec![1, u64::MAX >> 1], ticket: "mkw1.x.y".into() };
    let d = write_control(&c, &[0; 12]);
    assert_eq!(d.len() % 4, 0);
    assert_eq!(parse_control(&d), Some(c));
    let seats = [SeatState { seat: 0, account: 5, state: 1 }, SeatState { seat: 23, account: 6, state: 0 }];
    assert_eq!(parse_state(&write_state(true, 3, &seats, &[0; 12])), Some((true, 3, seats.to_vec())));
    // Anything malformed is dropped.
    let mut bad = d.clone();
    bad[4] ^= 1;
    assert!(parse_control(&bad).is_none());
    assert!(parse_control(&d[..d.len() - 4]).is_none());
    assert!(parse_voice_up(&write(KIND_VOICE, &[1; 12], ATTR_VOICE_UP, &[])).is_none());
    assert!(parse_voice_up(&write(KIND_VOICE, &[1; 12], ATTR_VOICE_UP, &[0; 401])).is_none());
    let mut v2 = voice(1, 0);
    v2[8] = 2;
    assert!(parse_voice_up(&v2).is_none(), "ver must be 1");
}

/// The vectors in VOICE_API.md §2.1.
#[test]
fn contract_vectors() {
    let hex = |b: &[u8]| b.iter().map(|x| format!("{x:02x}")).collect::<String>();
    let txid = [1, 0, 0x00, 0x2A, 0, 0, 0, 0x7E, 0xDE, 0xAD, 0xBE, 0xEF];
    let up = write(KIND_VOICE, &txid, ATTR_VOICE_UP, &[0xF8, 0xFF, 0xFE]);
    assert_eq!(hex(&up), "3e9300082112a4420100002a0000007edeadbeefc0f30003f8fffe00");
    let down = write_voice_down(&txid, 5, &[0xF8, 0xFF, 0xFE], [0x11, 0x22, 0x33]);
    assert_eq!(hex(&down), "3e9300082112a4420100002a0000007e05112233c0f40003f8fffe00");
    let c = Control { op: 1, self_flags: 2, mutes: vec![0x0123456789ABCDEF], ticket: "mkw1.a.b".into() };
    let ctl = write_control(&c, &[0; 12]);
    assert_eq!(
        hex(&ctl),
        "3e94001c2112a442000000000000000000000000c0f50016010102010123456789abcdef00086d6b77312e612e620000"
    );
    let st = write_state(true, 3, &[SeatState { seat: 2, account: 0x0123456789ABCDEF, state: 1 }], &[0; 12]);
    assert_eq!(hex(&st), "3e9400142112a442000000000000000000000000c0f6000e01010301020123456789abcdef010000");
}

#[test]
fn fans_out_to_the_other_members_of_the_room_only() {
    let (_, mut r) = setup();
    let out = r.handle(1.0, ep(1), &voice(1, 0));
    // a has seat 0; b and c hear it, a (no echo) and d (other room) do not.
    assert_eq!(voice_to(&out), vec![(ep(2), 0), (ep(3), 0)]);
    // Copied header fields.
    let (t, opus) = parse(&out.iter().find(|(to, _)| *to == ep(2)).unwrap().1, KIND_VOICE, ATTR_VOICE_DOWN).unwrap();
    assert_eq!((&t[..8], opus), (&[1, 0, 0, 1, 0, 0, 0, 3][..], &[0xAB; 40][..]));
    // b talking: seat 1.
    assert_eq!(voice_to(&r.handle(1.0, ep(2), &voice(1, 0))), vec![(ep(1), 1), (ep(3), 1)]);
}

#[test]
fn unbound_and_unticketed_get_nothing() {
    let (_, mut r) = setup();
    assert!(r.handle(1.0, ep(9), &voice(1, 0)).is_empty());
    let bad = write_control(&Control { op: 1, self_flags: 0, mutes: vec![], ticket: "nope".into() }, &[0; 12]);
    assert!(r.handle(1.0, ep(9), &bad).is_empty());
    assert!(r.handle(1.0, ep(9), &voice(2, 0)).is_empty());
    assert!(r.handle(1.0, ep(9), b"hello").is_empty());
}

#[test]
fn deafen_mutes_and_mic_mute() {
    let (f, mut r) = setup();
    let acc_a = f.account("a");
    // b mutes a, c deafens.
    r.handle(1.0, ep(2), &sub("b", OP_REPLACE_MUTES, 0, vec![acc_a]));
    r.handle(1.0, ep(3), &sub("c", OP_SUBSCRIBE, SELF_DEAFENED, vec![]));
    assert!(voice_to(&r.handle(1.1, ep(1), &voice(1, 0))).is_empty());
    // c still talks to a and b.
    assert_eq!(voice_to(&r.handle(1.2, ep(3), &voice(1, 0))).len(), 2);
    // b unmutes; a mic-mutes itself: dropped.
    r.handle(1.3, ep(2), &sub("b", OP_REPLACE_MUTES, 0, vec![]));
    r.handle(1.3, ep(1), &sub("a", OP_SUBSCRIBE, SELF_MIC_MUTED, vec![]));
    assert!(voice_to(&r.handle(1.4, ep(1), &voice(2, 0))).is_empty());
}

#[test]
fn bindings_expire_and_leave() {
    let (_, mut r) = setup();
    assert_eq!(r.subscribers(), 4);
    // b and c refresh; a and d do not.
    r.handle(10.0, ep(2), &sub("b", OP_SUBSCRIBE, 0, vec![]));
    r.handle(10.0, ep(3), &sub("c", OP_SUBSCRIBE, 0, vec![]));
    r.tick(15.0);
    assert_eq!(r.subscribers(), 2);
    assert!(r.handle(15.1, ep(1), &voice(1, 0)).is_empty());
    // A leave from another endpoint is ignored; from the bound one it drops at once.
    r.handle(15.2, ep(7), &sub("b", OP_LEAVE, 0, vec![]));
    assert_eq!(r.subscribers(), 2);
    r.handle(15.2, ep(2), &sub("b", OP_LEAVE, 0, vec![]));
    assert_eq!(r.subscribers(), 1);
    assert!(voice_to(&r.handle(15.3, ep(3), &voice(1, 0))).is_empty());
}

#[test]
fn a_new_endpoint_replaces_the_old_and_keeps_the_seat() {
    let (_, mut r) = setup();
    r.handle(1.0, ep(12), &sub("b", OP_SUBSCRIBE, 0, vec![]));
    assert_eq!(voice_to(&r.handle(1.1, ep(1), &voice(1, 0))), vec![(ep(3), 0), (ep(12), 0)]);
    assert!(r.handle(1.2, ep(2), &voice(1, 0)).is_empty(), "old endpoint unbound");
    assert_eq!(voice_to(&r.handle(1.5, ep(12), &voice(2, 0))), vec![(ep(1), 1), (ep(3), 1)]);
}

#[test]
fn at_most_four_talkers_per_room() {
    let f = Arc::new(Fake::default());
    let users = ["u0", "u1", "u2", "u3", "u4", "u5"];
    f.put(1, &users);
    let mut r = Relay::new(f.clone());
    for (i, u) in users.iter().enumerate() {
        r.handle(0.0, ep(i as u16), &sub(u, OP_SUBSCRIBE, 0, vec![]));
    }
    for i in 0..4 {
        assert_eq!(voice_to(&r.handle(1.0, ep(i), &voice(1, 0))).len(), 5);
    }
    assert!(r.handle(1.05, ep(4), &voice(1, 0)).is_empty(), "fifth talker dropped");
    assert_eq!(voice_to(&r.handle(1.1, ep(0), &voice(2, 0))).len(), 5, "the four keep talking");
    // u1 ends its spurt; u4 gets in.
    r.handle(1.15, ep(1), &voice(2, FLAG_END_OF_SPURT));
    assert_eq!(voice_to(&r.handle(1.2, ep(4), &voice(2, 0))).len(), 5);
    // After 300 ms of silence everyone is free again.
    r.tick(1.6);
    assert_eq!(voice_to(&r.handle(1.6, ep(5), &voice(1, 0))).len(), 5);
}

#[test]
fn per_endpoint_rate_limit() {
    let (_, mut r) = setup();
    let sent = (0..100).filter(|i| !r.handle(1.0 + *i as f64 * 0.001, ep(1), &voice(*i, 0)).is_empty()).count();
    // A second's burst (60) plus 0.1 s of refill; 64-byte datagrams stay under 4096 B/s.
    assert!((60..=67).contains(&sent), "{sent}");
    let mut sent = 0;
    for i in 0..60 {
        // 424-byte datagrams for 0.6 s: the byte budget bites first ((4096 + 0.6 * 4096) / 424 ~ 15).
        let mut txid = [0u8; 12];
        txid[0] = 1;
        let big = write(KIND_VOICE, &txid, ATTR_VOICE_UP, &[1; 400]);
        if !r.handle(3.0 + i as f64 * 0.01, ep(2), &big).is_empty() {
            sent += 1;
        }
    }
    assert!((14..=16).contains(&sent), "{sent}");
}

#[test]
fn state_down_on_subscribe_change_and_every_second() {
    let f = Arc::new(Fake::default());
    f.put(1, &["a", "b"]);
    let mut r = Relay::new(f.clone());
    let out = r.handle(0.0, ep(1), &sub("a", OP_SUBSCRIBE, 0, vec![]));
    let s = states_to(&out, ep(1));
    assert_eq!(s.len(), 1);
    assert_eq!((s[0].0, s[0].1, s[0].2.len()), (true, BUNDLE, 1));
    // b joins: both learn the two seats.
    let out = r.handle(0.1, ep(2), &sub("b", OP_SUBSCRIBE, 0, vec![]));
    for e in [ep(1), ep(2)] {
        let s = states_to(&out, e);
        let seats: Vec<(u8, u64)> = s[0].2.iter().map(|x| (x.seat, x.account)).collect();
        assert_eq!(seats, vec![(0, f.account("a")), (1, f.account("b"))]);
    }
    // a talks: state with a talking; then silence, state again with nobody talking.
    let out = r.handle(0.2, ep(1), &voice(1, 0));
    assert_eq!(states_to(&out, ep(2))[0].2[0].state, STATE_TALKING);
    let out = r.tick(0.6);
    assert!(states_to(&out, ep(2))[0].2.iter().all(|x| x.state == 0));
    // Nothing changed: the next state comes a second after the last periodic one (0.6).
    assert!(r.tick(0.7).is_empty());
    let n = (8..=16).filter(|t| !r.tick(*t as f64 / 10.0).is_empty()).count();
    assert_eq!(n, 1);
    // A lone subscriber without a room gets room = 0.
    let out = r.handle(2.0, ep(3), &sub("z", OP_SUBSCRIBE, 0, vec![]));
    assert_eq!(states_to(&out, ep(3)), vec![(false, BUNDLE, vec![])]);
}

#[test]
fn leaving_the_lobby_frees_the_seat_within_a_second() {
    let (f, mut r) = setup();
    f.put(1, &["b", "c"]);
    f.remove("a");
    let out = r.tick(1.0);
    let s = states_to(&out, ep(2));
    assert_eq!(s[0].2.iter().map(|x| x.seat).collect::<Vec<_>>(), vec![1, 2]);
    assert_eq!(states_to(&out, ep(1)), vec![(false, BUNDLE, vec![])]);
    assert!(voice_to(&r.handle(1.1, ep(1), &voice(1, 0))).is_empty());
    // e joins the lobby and takes the lowest free seat, 0.
    f.put(1, &["b", "c", "e"]);
    let out = r.handle(1.2, ep(5), &sub("e", OP_SUBSCRIBE, 0, vec![]));
    let seats: Vec<u8> = states_to(&out, ep(5))[0].2.iter().map(|x| x.seat).collect();
    assert_eq!(seats, vec![0, 1, 2]);
}
