//! A TURN client against the relay over loopback: challenge, allocate,
//! channel bind, both directions of relayed traffic, release, full range.

use std::net::{IpAddr, Ipv4Addr, SocketAddr, SocketAddrV4};
use std::time::Duration;

use gateway::turn::*;
use tokio::net::UdpSocket;

const LO: IpAddr = IpAddr::V4(Ipv4Addr::LOCALHOST);

async fn relay(ports: Option<(u16, u16)>) -> (std::sync::Arc<Relay>, SocketAddr) {
    let opts = Options {
        relay_bind: LO,
        public_host: Ipv4Addr::LOCALHOST,
        relay_ports: ports,
        username: "mkwmp".into(),
        password: "mkwmp".into(),
    };
    let r = Relay::bind(SocketAddr::new(LO, 0), opts).await.unwrap();
    let addr = r.local_addr().unwrap();
    tokio::spawn(r.clone().run());
    (r, addr)
}

async fn recv(s: &UdpSocket) -> Vec<u8> {
    let mut b = vec![0u8; 2048];
    let (n, _) = tokio::time::timeout(Duration::from_secs(2), s.recv_from(&mut b)).await.expect("timed out").unwrap();
    b.truncate(n);
    b
}

fn key() -> Vec<u8> {
    long_term_key("mkwmp", REALM, "mkwmp")
}

async fn request(c: &UdpSocket, server: SocketAddr, method: u16, attrs: &[(u16, &[u8])], signed: bool) -> Vec<u8> {
    let mut all: Vec<(u16, &[u8])> = vec![(A_USERNAME, b"mkwmp"), (A_REALM, REALM)];
    all.extend_from_slice(attrs);
    let k = key();
    let m = build(msg_type(method, REQUEST), &[3u8; 12], &all, signed.then_some(&k[..]));
    c.send_to(&m, server).await.unwrap();
    recv(c).await
}

async fn allocate(c: &UdpSocket, server: SocketAddr) -> Result<SocketAddrV4, u16> {
    let r = request(c, server, ALLOCATE, &[], true).await;
    let m = parse(&r).unwrap();
    if m.class == ERROR {
        let e = m.get(A_ERROR).unwrap();
        return Err(u16::from(e[2]) * 100 + u16::from(e[3]));
    }
    Ok(unxor_addr(m.get(A_RELAYED).unwrap()).unwrap())
}

#[tokio::test]
async fn relays_both_ways() {
    let (r, server) = relay(None).await;
    let client = UdpSocket::bind((LO, 0)).await.unwrap();
    let peer = UdpSocket::bind((LO, 0)).await.unwrap();
    let SocketAddr::V4(peer_addr) = peer.local_addr().unwrap() else { panic!() };

    // Unsigned: 401 with realm and nonce.
    let r401 = request(&client, server, ALLOCATE, &[], false).await;
    let m = parse(&r401).unwrap();
    assert_eq!((m.method, m.class), (ALLOCATE, ERROR));
    assert_eq!(&m.get(A_ERROR).unwrap()[2..4], &[4, 1]);
    assert!(m.get(A_NONCE).is_some());

    let relayed = allocate(&client, server).await.unwrap();
    assert_eq!(*relayed.ip(), Ipv4Addr::LOCALHOST);
    assert_eq!(r.allocation_count(), 1);
    // A second Allocate reuses it.
    assert_eq!(allocate(&client, server).await.unwrap(), relayed);

    // Send indication to an unbound peer; its answer comes back as a Data indication.
    let ind = build(msg_type(SEND, INDICATION), &[4u8; 12], &[(A_PEER, &xor_addr(peer_addr)), (A_DATA, b"hi")], None);
    client.send_to(&ind, server).await.unwrap();
    let mut b = [0u8; 64];
    let (n, from) = tokio::time::timeout(Duration::from_secs(2), peer.recv_from(&mut b)).await.unwrap().unwrap();
    assert_eq!((&b[..n], from), (&b"hi"[..], SocketAddr::V4(relayed)));
    peer.send_to(b"yo", from).await.unwrap();
    let d = recv(&client).await;
    let m = parse(&d).unwrap();
    assert_eq!((m.method, m.class), (DATA, INDICATION));
    assert_eq!(m.get(A_DATA), Some(&b"yo"[..]));
    assert_eq!(unxor_addr(m.get(A_PEER).unwrap()), Some(peer_addr));

    // ChannelBind, then ChannelData both ways.
    let ch = 0x4001u16;
    let ok = request(&client, server, CHANNEL_BIND, &[(A_CHANNEL, &[0x40, 0x01, 0, 0]), (A_PEER, &xor_addr(peer_addr))], true).await;
    assert_eq!(parse(&ok).unwrap().class, SUCCESS);
    let mut cd = ch.to_be_bytes().to_vec();
    cd.extend(3u16.to_be_bytes());
    cd.extend(b"abc\0");
    client.send_to(&cd, server).await.unwrap();
    let (n, _) = tokio::time::timeout(Duration::from_secs(2), peer.recv_from(&mut b)).await.unwrap().unwrap();
    assert_eq!(&b[..n], b"abc");
    peer.send_to(b"12345", SocketAddr::V4(relayed)).await.unwrap();
    let d = recv(&client).await;
    assert_eq!(&d[..4], &[0x40, 0x01, 0, 5]);
    assert_eq!(&d[4..9], b"12345");
    assert_eq!(d.len() % 4, 0);

    // Refresh with lifetime 0 releases it.
    let ok = request(&client, server, REFRESH, &[(A_LIFETIME, &0u32.to_be_bytes())], true).await;
    assert_eq!(parse(&ok).unwrap().class, SUCCESS);
    assert_eq!(r.allocation_count(), 0);
}

#[tokio::test]
async fn binding_reports_the_mapped_address() {
    let (_r, server) = relay(None).await;
    let client = UdpSocket::bind((LO, 0)).await.unwrap();
    let SocketAddr::V4(me) = client.local_addr().unwrap() else { panic!() };
    let m = build(msg_type(BINDING, REQUEST), &[1u8; 12], &[], None);
    client.send_to(&m, server).await.unwrap();
    let r = recv(&client).await;
    let p = parse(&r).unwrap();
    assert_eq!((p.method, p.class), (BINDING, SUCCESS));
    assert_eq!(unxor_addr(p.get(A_MAPPED).unwrap()), Some(me));
}

#[tokio::test]
async fn full_port_range_refuses_with_508() {
    let probe = std::net::UdpSocket::bind((LO, 0)).unwrap();
    let port = probe.local_addr().unwrap().port();
    drop(probe);
    let (_r, server) = relay(Some((port, port))).await;
    let a = UdpSocket::bind((LO, 0)).await.unwrap();
    let b = UdpSocket::bind((LO, 0)).await.unwrap();
    assert_eq!(allocate(&a, server).await.unwrap().port(), port);
    assert_eq!(allocate(&b, server).await, Err(508));
}

#[tokio::test]
async fn allocations_per_ip_are_capped() {
    let (_r, server) = relay(None).await;
    let mut socks = vec![];
    for _ in 0..gateway::limits::TURN_ALLOCATIONS_PER_IP {
        let c = UdpSocket::bind((LO, 0)).await.unwrap();
        allocate(&c, server).await.unwrap();
        socks.push(c);
    }
    let one_more = UdpSocket::bind((LO, 0)).await.unwrap();
    assert_eq!(allocate(&one_more, server).await, Err(486));
}
