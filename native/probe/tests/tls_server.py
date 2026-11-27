#!/usr/bin/env python3
"""A loopback HTTPS server for tests/test_nnssl.c: the two things a ghost attachment server does.

    python -I tls_server.py <port> <server.pem> <server.key>

GET  any path: 200 with 300000 bytes (byte i is i % 251), Content-Length set.
PUT  any path: reads the body; "Content-Range: bytes */N" with no body answers 308 and a Range header (a resumable
     upload's status request), anything else 200 with the body "{}".
HTTP/1.1 with keep-alive, ALPN "http/1.1", TLS 1.2 and later. Certificates: tests/make_test_ca.ps1.
Only the standard library.
"""
import http.server
import ssl
import sys

GHOST_BYTES = 300000


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def do_GET(self):
        body = bytes(i % 251 for i in range(GHOST_BYTES))
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_PUT(self):
        length = int(self.headers.get("Content-Length", "0"))
        got = 0
        while got < length:
            chunk = self.rfile.read(min(65536, length - got))
            if not chunk:
                return
            got += len(chunk)
        if length == 0 and self.headers.get("Content-Range", "").startswith("bytes */"):
            self.send_response(308)
            self.send_header("Range", "bytes=0-99")
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        self.send_response(200)
        self.send_header("x-goog-stored-content-length", str(got))
        self.send_header("Content-Length", "2")
        self.end_headers()
        self.wfile.write(b"{}")

    def log_message(self, fmt, *args):
        sys.stderr.write("tls_server: " + fmt % args + "\n")


def main():
    port, cert, key = int(sys.argv[1]), sys.argv[2], sys.argv[3]
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.minimum_version = ssl.TLSVersion.TLSv1_2
    ctx.load_cert_chain(cert, key)
    ctx.set_alpn_protocols(["http/1.1"])
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler)
    server.socket = ctx.wrap_socket(server.socket, server_side=True)
    sys.stderr.write(f"tls_server: listening on 127.0.0.1:{port}\n")
    server.serve_forever()


if __name__ == "__main__":
    main()
