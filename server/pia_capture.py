"""Capture and optionally answer Pia NAT probes on loopback."""
import argparse
from datetime import datetime
import socket
import struct


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=10025)
    parser.add_argument("--log", required=True)
    parser.add_argument("--reply", action="store_true", help="answer known NAT probe types locally")
    args = parser.parse_args()
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.bind(("127.0.0.1", args.port))
        with open(args.log, "a", encoding="utf-8", buffering=1) as log:
            print(f"Pia UDP capture listening on 127.0.0.1:{args.port} (reply={args.reply})", file=log)
            while True:
                data, peer = sock.recvfrom(65535)
                print(f"[{datetime.now().isoformat(timespec='milliseconds')}] "
                      f"{peer[0]}:{peer[1]} {len(data)} bytes {data.hex()}", file=log)
                # Guest 0x1624314 reads four big-endian words.
                # 0x16268c4 accepts 101..103; 0x16243f8 extracts port and IPv4.
                if args.reply and len(data) == 16 and not any(data[4:]):
                    kind = int.from_bytes(data[:4], "big")
                    if kind in (101, 102, 103):
                        reply = struct.pack("!II4sI", kind, peer[1], socket.inet_aton(peer[0]), 0)
                        sock.sendto(reply, peer)
                        print(f"  reply {reply.hex()}", file=log)


if __name__ == "__main__":
    main()
