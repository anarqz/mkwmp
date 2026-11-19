use super::*;
use std::sync::Mutex;

/// Tickets are "t:<uid>"; rooms are set per test.
#[derive(Default)]
struct Fake {
    rooms: Mutex<HashMap<String, (u128, Vec<String>)>>,
    friends: Mutex<HashSet<(String, String)>>,
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
    fn befriend(&self, a: &str, b: &str, yes: bool) {
        let mut f = self.friends.lock().unwrap();
        for k in [(a.to_string(), b.to_string()), (b.to_string(), a.to_string())] {
            if yes {
                f.insert(k);
            } else {
                f.remove(&k);
            }
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
        acct(uid)
    }
    fn friends(&self, a: &str, b: &str) -> bool {
        self.friends.lock().unwrap().contains(&(a.to_string(), b.to_string()))
    }
}

fn acct(uid: &str) -> u64 {
    uid.bytes().fold(7u64, |a, b| a * 31 + b as u64)
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
    // A keepalive answers only the sender.
    let out = r.handle(1.9, ep(1), &sub("a", OP_SUBSCRIBE, 0, vec![]));
    assert_eq!((states_to(&out, ep(1)).len(), states_to(&out, ep(2)).len()), (1, 0));
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

// ---- friend rooms: state v2 and video (VOICE_API.md §6, VIDEO_API.md) ----------------------

const RM: u8 = SELF_ROOM_MODE;
const CAM: u8 = SELF_ROOM_MODE | SELF_CAMERA;

fn video(frame: u16, chunk: u8, chunks: u8) -> Vec<u8> {
    let mut txid = [0u8; 12];
    txid[0] = VERSION;
    txid[1] = VIDEO_FLAG_KEY | if chunk + 1 == chunks { VIDEO_FLAG_LAST } else { 0 };
    txid[2..4].copy_from_slice(&frame.to_be_bytes());
    txid[4] = chunk;
    txid[5] = chunks;
    txid[10] = 1; // MJPEG
    write(KIND_VIDEO, &txid, ATTR_VIDEO_UP, &[0xD8; 1000])
}

fn video_to(out: &Datagrams) -> Vec<SocketAddr> {
    let mut v: Vec<_> = out.iter().filter(|(_, d)| parse(d, KIND_VIDEO, ATTR_VIDEO_DOWN).is_some()).map(|(to, _)| *to).collect();
    v.sort();
    v
}

fn v2_to(out: &Datagrams, who: SocketAddr) -> Option<StateV2> {
    out.iter().filter(|(to, _)| *to == who).filter_map(|(_, d)| parse_state_v2(d)).next_back()
}

fn want(accounts: &[&str]) -> Vec<u8> {
    write_video_subscribe(&accounts.iter().map(|a| acct(a)).collect::<Vec<_>>(), &[9; 12])
}

/// Room 1 holds `users` (ep 1, 2, ...), subscribed with `flags`.
fn room(f: &Arc<Fake>, users: &[(&str, u8)]) -> Relay<Fake> {
    f.put(1, &users.iter().map(|u| u.0).collect::<Vec<_>>());
    let mut r = Relay::new(f.clone());
    for (i, (u, flags)) in users.iter().enumerate() {
        r.handle(0.0, ep(i as u16 + 1), &sub(u, OP_SUBSCRIBE, *flags, vec![]));
    }
    r
}

#[test]
fn room_wire_round_trips_and_vectors() {
    let hex = |b: &[u8]| b.iter().map(|x| format!("{x:02x}")).collect::<String>();
    let v = write_video_subscribe(&[0x0123456789ABCDEF], &[0; 12]);
    assert_eq!(parse_video_subscribe(&v), Some(vec![0x0123456789ABCDEF]));
    assert_eq!(hex(&v), "3e9400102112a442000000000000000000000000c0f5000b0106010123456789abcdef00");
    assert!(parse_video_subscribe(&write_video_subscribe(&[1, 2, 3, 4, 5], &[0; 12])).is_none(), "at most 4 faces");
    assert!(parse_control(&v).is_none(), "op 6 is no seat-ticket op");
    let st = StateV2 {
        in_room: true,
        bundle: 3,
        seats: vec![SeatState { seat: 2, account: 0x0123456789ABCDEF, state: 1 }],
        room_id: 0x1122334455667788,
        fps: 1,
        members: vec![(0x0123456789ABCDEF, MEMBER_BOUND | MEMBER_CAMERA)],
    };
    let sd = write_state_v2(&st, &[0; 12]);
    assert_eq!(parse_state_v2(&sd), Some(st));
    assert!(parse_state(&sd).is_none(), "a v1 client never takes a v2 state");
    assert_eq!(
        hex(&sd),
        "3e9400282112a442000000000000000000000000c0f6002102010301020123456789abcdef0111223344556677880101\
0123456789abcdef03000000"
    );
    let txid = [1, 3, 0x00, 0x07, 0, 1, 0, 0, 0x03, 0xE8, 1, 0x5A];
    let up = write(KIND_VIDEO, &txid, ATTR_VIDEO_UP, &[0xFF, 0xD8, 0xFF]);
    assert_eq!(parse_video_up(&up).map(|(t, c)| (t, c.len())), Some((txid, 3)));
    assert_eq!(hex(&up), "3e9500082112a442010300070001000003e8015ac0f70003ffd8ff00");
    let down = write_video_down(&txid, 4, &[0xFF, 0xD8, 0xFF]);
    assert_eq!(hex(&down), "3e9500082112a442010300070001000003e80104c0f80003ffd8ff00");
    let mut bad = txid;
    bad[4] = 1;
    assert!(parse_video_up(&write(KIND_VIDEO, &bad, ATTR_VIDEO_UP, &[1])).is_none(), "chunk past chunks");
    bad[4] = 0;
    bad[5] = 0;
    assert!(parse_video_up(&write(KIND_VIDEO, &bad, ATTR_VIDEO_UP, &[1])).is_none(), "no chunks");
    assert!(parse_video_up(&write(KIND_VIDEO, &txid, ATTR_VIDEO_UP, &[0; 1101])).is_none());
}

#[test]
fn everyone_in_the_room_hears_everyone_and_v2_lists_mutual_friends() {
    let f = Arc::new(Fake::default());
    f.befriend("a", "b", true);
    let mut r = room(&f, &[("a", CAM), ("b", RM), ("c", RM), ("x", 0)]);
    let heard: Vec<SocketAddr> = voice_to(&r.handle(0.1, ep(1), &voice(1, 0))).into_iter().map(|x| x.0).collect();
    assert_eq!(heard, vec![ep(2), ep(3), ep(4)]);
    let out = r.tick(1.0);
    let sa = v2_to(&out, ep(1)).expect("a room client gets state v2");
    assert_eq!(sa.members, vec![(acct("b"), MEMBER_BOUND)]);
    assert_eq!(sa.seats.len(), 4);
    assert_ne!(sa.room_id, 0);
    assert_eq!(v2_to(&out, ep(2)).unwrap().members, vec![(acct("a"), MEMBER_BOUND | MEMBER_CAMERA)]);
    assert!(v2_to(&out, ep(3)).unwrap().members.is_empty(), "c is nobody's friend");
    // The released client gets v1 only, with every seat.
    assert!(v2_to(&out, ep(4)).is_none());
    assert_eq!(states_to(&out, ep(4)).pop().unwrap().2.len(), 4);
}

#[test]
fn no_room_no_voice_no_pictures() {
    let f = Arc::new(Fake::default());
    f.befriend("a", "b", true);
    let mut r = Relay::new(f.clone());
    // Not in a friend room (the lobby gives matchmade lobbies no room): nothing at all.
    r.handle(0.0, ep(1), &sub("a", OP_SUBSCRIBE, CAM, vec![]));
    let out = r.handle(0.0, ep(2), &sub("b", OP_SUBSCRIBE, RM, vec![]));
    let st = v2_to(&out, ep(2)).unwrap();
    assert!(!st.in_room && st.room_id == 0 && st.members.is_empty());
    r.handle(0.1, ep(2), &want(&["a"]));
    assert!(voice_to(&r.handle(0.2, ep(1), &voice(1, 0))).is_empty());
    assert!(video_to(&r.handle(0.2, ep(1), &video(1, 0, 1))).is_empty());
}

#[test]
fn pictures_go_to_mutual_friends_who_asked_for_them() {
    let f = Arc::new(Fake::default());
    for o in ["b", "c", "d"] {
        f.befriend("a", o, true);
    }
    // e asked too but isn't a's friend; x runs a released client.
    let mut r = room(&f, &[("a", CAM), ("b", RM), ("c", RM), ("d", RM), ("e", RM), ("x", 0)]);
    assert!(video_to(&r.handle(0.1, ep(1), &video(1, 0, 2))).is_empty(), "nobody asked yet");
    for i in 2..=5 {
        r.handle(0.2, ep(i), &want(&["a"]));
    }
    assert_eq!(video_to(&r.handle(0.3, ep(1), &video(2, 0, 2))), vec![ep(2), ep(3), ep(4)]);
    // A mute hides the face too.
    r.handle(0.4, ep(3), &sub("c", OP_REPLACE_MUTES, RM, vec![acct("a")]));
    assert_eq!(video_to(&r.handle(0.5, ep(1), &video(3, 0, 2))), vec![ep(2), ep(4)]);
    // ...and a sender's mute stops their face going to the muted player.
    r.handle(0.51, ep(1), &sub("a", OP_REPLACE_MUTES, CAM, vec![acct("b")]));
    assert_eq!(video_to(&r.handle(0.52, ep(1), &video(3, 1, 2))), vec![ep(4)]);
    r.handle(0.53, ep(1), &sub("a", OP_REPLACE_MUTES, CAM, vec![]));
    // Unfriended: gone once the room is looked up again (within a second).
    f.befriend("a", "d", false);
    r.tick(1.5);
    assert_eq!(video_to(&r.handle(1.6, ep(1), &video(4, 0, 2))), vec![ep(2)]);
    // Camera off: nothing goes out, and the friends' state shows it.
    let out = r.handle(1.7, ep(1), &sub("a", OP_SUBSCRIBE, RM, vec![]));
    assert_eq!(v2_to(&out, ep(2)).unwrap().members, vec![(acct("a"), MEMBER_BOUND)]);
    assert!(video_to(&r.handle(1.8, ep(1), &video(5, 0, 2))).is_empty());
    // Leaving room mode drops the subscriptions; a released client's pictures go nowhere.
    r.handle(1.9, ep(2), &sub("b", OP_SUBSCRIBE, 0, vec![]));
    assert!(r.subs["b"].video_want.is_empty());
    f.befriend("x", "b", true);
    r.handle(2.0, ep(6), &sub("x", OP_SUBSCRIBE, SELF_CAMERA, vec![]));
    assert!(video_to(&r.handle(2.1, ep(6), &video(1, 0, 1))).is_empty());
}

#[test]
fn video_off_rate_limit_and_frame_rate_divisor() {
    let mk = |opts: Options| {
        let f = Arc::new(Fake::default());
        f.befriend("a", "b", true);
        f.put(1, &["a", "b"]);
        let mut r = Relay::with_options(f, opts);
        r.handle(0.0, ep(1), &sub("a", OP_SUBSCRIBE, CAM, vec![]));
        r.handle(0.0, ep(2), &sub("b", OP_SUBSCRIBE, CAM, vec![]));
        r.handle(0.0, ep(2), &want(&["a"]));
        r
    };
    let mut off = mk(Options { video: false, ..Options::default() });
    // The stats lines name rooms, pairs, subscriptions and counts.
    let mut on = mk(Options { stats_secs: 1.0, ..Options::default() });
    on.handle(0.2, ep(1), &video(1, 0, 1));
    let lines = on.stats();
    assert!(lines.iter().any(|l| l.contains("friends [") && l.contains('-')), "{lines:?}");
    assert!(lines.iter().any(|l| l.contains("wants video of")), "{lines:?}");
    assert!(lines.iter().any(|l| l.contains("up 1 dgrams") && l.contains("down 1 dgrams")), "{lines:?}");
    assert!(video_to(&off.handle(0.1, ep(1), &video(1, 0, 1))).is_empty());
    assert_eq!(v2_to(&off.tick(1.0), ep(2)).unwrap().members, vec![(acct("a"), MEMBER_BOUND)], "no camera flag");
    // 64 KiB/s per sender: about 63 datagrams of 1024 bytes in one second.
    let mut r = mk(Options::default());
    let sent: usize = (0..200).map(|i| video_to(&r.handle(0.5, ep(1), &video(i, 0, 1))).len()).sum();
    assert!((60..=64).contains(&sent), "{sent}");
    // A tiny budget: one second over it halves the frame rate, five calm seconds restore it.
    let mut r = mk(Options { video: true, video_budget: 10_000, ..Options::default() });
    for i in 0..20 {
        r.handle(0.5, ep(1), &video(i, 0, 1));
    }
    r.tick(1.0);
    assert_eq!(v2_to(&r.tick(2.0), ep(2)).unwrap().fps, 2);
    let mut fps = 2;
    let mut t = 2.0;
    while t < 9.0 {
        t += 1.0;
        r.handle(t, ep(1), &sub("a", OP_SUBSCRIBE, CAM, vec![]));
        r.handle(t, ep(2), &sub("b", OP_SUBSCRIBE, CAM, vec![]));
        if let Some(s) = v2_to(&r.tick(t), ep(2)) {
            fps = s.fps;
        }
    }
    assert_eq!(fps, 1);
}
