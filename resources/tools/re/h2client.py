"""Smoke-test mkwmp_server.py over h2c or verified TLS (--tls-cert PEM)."""
import argparse
import socket
import ssl
import struct
import sys
import time

from h2.connection import H2Connection
from h2.config import H2Configuration

ap = argparse.ArgumentParser()
ap.add_argument("port", nargs="?", type=int, default=8900)
ap.add_argument("--tls-cert")
args = ap.parse_args()
host, port = "127.0.0.1", args.port
# a sample protobuf: field1=varint 7, field2="hello"
msg = bytes([0x08, 0x07, 0x12, 0x05]) + b"hello"
body = struct.pack(">BI", 0, len(msg)) + msg

s = socket.create_connection((host, port))
if args.tls_cert:
    context = ssl.create_default_context(cafile=args.tls_cert)
    context.set_alpn_protocols(["h2"])
    s = context.wrap_socket(s, server_hostname="localhost")
    assert s.selected_alpn_protocol() == "h2", "ALPN h2 was not negotiated"
c = H2Connection(H2Configuration(client_side=True, header_encoding="utf-8"))
c.initiate_connection()
s.sendall(c.data_to_send())
c.send_headers(1, [(":method", "POST"), (":scheme", "https" if args.tls_cert else "http"), (":authority", "auth.npln.test"),
                   (":path", "/nn.npln.auth.v1.Auth/IssueToken"), ("content-type", "application/grpc"),
                   ("te", "trailers")])
c.send_data(1, body, end_stream=True)
s.sendall(c.data_to_send())

s.settimeout(3)
got_status = None
ended = False
response = bytearray()
deadline = time.time() + 3
while time.time() < deadline:
    try:
        data = s.recv(65535)
    except socket.timeout:
        break
    if not data:
        break
    for ev in c.receive_data(data):
        name = type(ev).__name__
        print("client event:", name)
        if name == "TrailersReceived":
            got_status = dict(getattr(ev, "headers", []) or [])
        elif name == "DataReceived":
            response.extend(ev.data)
        elif name == "StreamEnded":
            ended = True
    out = c.data_to_send()
    if out:
        s.sendall(out)
    if ended:
        break
print("trailers:", got_status)
s.close()
if not ended or not got_status or got_status.get("grpc-status") != "0" or response != bytes(5):
    sys.exit("FAIL: missing end-of-stream, successful trailers or empty gRPC response")
print("OK: gRPC response and trailers verified")
