//! Pia NAT probe responder (port of `resources/python-lobby/pia_capture.py --reply`).
//!
//! A probe is 16 bytes: a big-endian kind (101..=103) and twelve zero bytes.
//! The reply carries the kind, the sender's port and IPv4 address as seen
//! here, and a zero word (guest 0x1624314 reads four big-endian words;
//! 0x16268c4 accepts 101..103; 0x16243f8 extracts port and IPv4).

use std::net::SocketAddr;

use tokio::net::UdpSocket;

pub fn reply(probe: &[u8], peer: SocketAddr) -> Option<[u8; 16]> {
    if probe.len() != 16 || probe[4..].iter().any(|&b| b != 0) {
        return None;
    }
    let kind = u32::from_be_bytes(probe[..4].try_into().unwrap());
    let SocketAddr::V4(v4) = peer else { return None };
    if !(101..=103).contains(&kind) {
        return None;
    }
    let mut out = [0u8; 16];
    out[..4].copy_from_slice(&kind.to_be_bytes());
    out[4..8].copy_from_slice(&u32::from(v4.port()).to_be_bytes());
    out[8..12].copy_from_slice(&v4.ip().octets());
    Some(out)
}

pub async fn run(sock: UdpSocket) -> std::io::Result<()> {
    let mut buf = [0u8; 2048];
    // UDP replies can be aimed at a spoofed address: cap them per source.
    let mut per_ip = crate::limits::PerIp::new(20.0, 40.0);
    loop {
        let (n, peer) = match sock.recv_from(&mut buf).await {
            Ok(x) => x,
            // ICMP port-unreachable surfaces as an error on some systems.
            Err(_) => continue,
        };
        if let Some(r) = reply(&buf[..n], peer).filter(|_| per_ip.allow(peer.ip())) {
            let _ = sock.send_to(&r, peer).await;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn answers_known_probes_like_the_python() {
        let peer: SocketAddr = "203.0.113.7:51234".parse().unwrap();
        let mut probe = [0u8; 16];
        probe[3] = 102;
        // struct.pack("!II4sI", 102, 51234, inet_aton("203.0.113.7"), 0)
        let want = hex("00000066 0000c822 cb007107 00000000");
        assert_eq!(reply(&probe, peer).unwrap().to_vec(), want);
        probe[3] = 104;
        assert!(reply(&probe, peer).is_none());
        probe[3] = 101;
        probe[9] = 1;
        assert!(reply(&probe, peer).is_none());
        assert!(reply(&probe[..15], peer).is_none());
    }

    fn hex(s: &str) -> Vec<u8> {
        let s: String = s.split_whitespace().collect();
        (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
    }
}
