#!/usr/bin/env python3
"""mkwmp local NPLN gRPC capture server.

A minimal HTTP/2 + gRPC server, with TLS or plaintext h2c. The captured NPLN path uses embedded
OpenSSL through the probe's loopback sockets; use --tls-cert and --tls-key for that path.
We log each call (/nn.npln.<svc>/<Method>) and its length-prefixed
protobuf body (decoded generically, since we don't have the .proto schemas), then return a minimal
gRPC response. IssuePrearrangedUserToken returns a reconstructed local token;
other methods currently receive an empty message with grpc-status 0.

    python mkwmp_server.py [--host 127.0.0.1] [--port 8900] [--log capture.log]

Deps (install into the project venv): pip install h2
"""
import argparse
import asyncio
import base64
import datetime
import json
import struct
import ssl
import sys
import time
from uuid import UUID

from h2.config import H2Configuration
from h2.connection import H2Connection
from h2.events import DataReceived, RequestReceived, StreamEnded, StreamReset, WindowUpdated
from local_matchmaking import LocalMatchmaking

LOG_FILE = None
CREATE_TICKET = "/nn.npln.matchmaking.v1.Matchmaker/CreateMatchmakingTicket"
TRACK_TICKET = "/nn.npln.matchmaking.v1.Matchmaker/TrackMatchmakingTicket"
PRESENCE_SUBSCRIBE = "/nn.npln.friends.v1.PresenceService/SubscribePresences"
PRESENCE_KEEPALIVE = "/nn.npln.friends.v1.PresenceService/KeepAlive"
SUBSCRIPTIONS = {
    PRESENCE_SUBSCRIBE,
    "/nn.npln.friends.v1.Friends/SubscribeFriendUsers",
    "/nn.npln.maintenance.v1.MaintenanceScheduleService/SubscribeMaintenanceSchedules",
}


def log(msg):
    line = f"[{datetime.datetime.now():%H:%M:%S.%f}] {msg}"
    print(line, flush=True)
    if LOG_FILE:
        LOG_FILE.write(line + "\n")
        LOG_FILE.flush()


# ---- generic protobuf wire decoder (no schema) -----------------------------
def _varint(b, i):
    r = s = 0
    while True:
        x = b[i]; i += 1
        r |= (x & 0x7F) << s
        if not x & 0x80:
            return r, i
        s += 7


def decode_protobuf(data, indent=1):
    """Best-effort decode of protobuf wire format into a readable tree."""
    out = []
    pad = "  " * indent
    i, n = 0, len(data)
    try:
        while i < n:
            tag, i = _varint(data, i)
            field, wt = tag >> 3, tag & 7
            if field == 0:
                return [f"{pad}<not protobuf>"]
            if wt == 0:
                v, i = _varint(data, i)
                out.append(f"{pad}{field}: varint {v}")
            elif wt == 1:
                v = data[i:i + 8]; i += 8
                out.append(f"{pad}{field}: 64bit {v.hex()}")
            elif wt == 2:
                ln, i = _varint(data, i)
                raw = data[i:i + ln]; i += ln
                # prefer a clean printable string; else a cleanly-parsing nested message; else bytes
                as_str = None
                try:
                    s = raw.decode("utf-8")
                    if s and all(c in "\t\n\r" or 0x20 <= ord(c) < 0x7f for c in s):
                        as_str = s
                except UnicodeDecodeError:
                    pass
                nested = decode_protobuf(raw, indent + 1) if raw else None
                nested_ok = nested is not None and not any("<" in x for x in nested)
                if as_str is not None and not (nested_ok and len(raw) > 2):
                    out.append(f'{pad}{field}: string "{as_str}"')
                elif nested_ok:
                    out.append(f"{pad}{field}: message ({ln}B) {{")
                    out.extend(nested)
                    out.append(f"{pad}}}")
                elif as_str is not None:
                    out.append(f'{pad}{field}: string "{as_str}"')
                else:
                    out.append(f"{pad}{field}: bytes({ln}) {raw[:64].hex()}{'...' if ln > 64 else ''}")
            elif wt == 5:
                v = data[i:i + 4]; i += 4
                out.append(f"{pad}{field}: 32bit {v.hex()}")
            else:
                return [f"{pad}<unknown wiretype {wt}>"]
    except (IndexError, Exception):
        out.append(f"{pad}<decode error, raw={data.hex()[:128]}>")
    return out


def decode_grpc_messages(buf):
    """Split a gRPC-framed body (1-byte flag + 4-byte len + msg)* into messages."""
    msgs, i = [], 0
    while i + 5 <= len(buf):
        flag = buf[i]
        (ln,) = struct.unpack_from(">I", buf, i + 1)
        i += 5
        msg = buf[i:i + ln]
        i += ln
        msgs.append((flag, msg))
    return msgs


def grpc_frame(msg: bytes) -> bytes:
    return struct.pack(">BI", 0, len(msg)) + msg


def encode_varint(value):
    out = bytearray()
    while value > 127:
        out.append((value & 127) | 128)
        value >>= 7
    out.append(value)
    return bytes(out)


def protobuf_bytes(field, value):
    if isinstance(value, str):
        value = value.encode("utf-8")
    return encode_varint((field << 3) | 2) + encode_varint(len(value)) + value


def bytes_field(message, wanted):
    """Read one length-delimited field without descending into nested data."""
    i = 0
    while i < len(message):
        tag, i = _varint(message, i)
        field, wire = tag >> 3, tag & 7
        if wire == 2:
            length, i = _varint(message, i)
            end = i + length
            if end > len(message):
                raise ValueError("truncated protobuf field")
            if field == wanted:
                return message[i:end]
            i = end
        elif wire == 0:
            _, i = _varint(message, i)
        elif wire in (1, 5):
            i += 8 if wire == 1 else 4
        else:
            raise ValueError("unsupported protobuf wire type")
    raise ValueError(f"missing field {wanted}")


def iter_fields(message):
    """Yield (field, wire type, raw encoded field bytes) for a flat message."""
    i = 0
    while i < len(message):
        start = i
        tag, i = _varint(message, i)
        wire = tag & 7
        if wire == 0:
            _, i = _varint(message, i)
        elif wire == 2:
            length, i = _varint(message, i)
            i += length
        elif wire in (1, 5):
            i += 8 if wire == 1 else 4
        else:
            raise ValueError("unsupported protobuf wire type")
        if i > len(message):
            raise ValueError("truncated protobuf field")
        yield tag >> 3, wire, message[start:i]


def timestamp(seconds):
    # google.protobuf.Timestamp/Duration {1: int64 seconds, 2: int32 nanos};
    # serializer main+0x11ddd08.
    return b"\x08" + encode_varint(int(seconds))


def player_for(user, player):
    """The client's own Player (ticket field 4) with its real user resource.

    Player serializer main+0x140d68c: 1 user, 2 attributes, 3 map, 4 string.
    The client sends tenants/current/users/current; peers need the resolved name.
    """
    rest = b"".join(raw for field, _, raw in iter_fields(player) if field != 1)
    return protobuf_bytes(1, user) + rest


def create_ticket_response(queue, user, request_body):
    messages = decode_grpc_messages(request_body)
    if len(messages) != 1 or messages[0][0]:
        raise ValueError("expected one uncompressed ticket request")
    request = messages[0][1]
    ticket_message = bytes_field(request, 2)
    config = bytes_field(ticket_message, 2).decode()
    prefix = "tenants/current/matchmakingConfigs/"
    if not config.startswith(prefix) or not config[len(prefix):]:
        raise ValueError("invalid matchmaking config")
    try:
        player = bytes_field(ticket_message, 4)
    except ValueError:
        player = b""
    ticket = queue.create(user, config[len(prefix):], player)
    tenant = user.split("/users/", 1)[0]
    name = f"{tenant}/matchmakingTickets/{ticket.id}"
    return protobuf_bytes(1, name)


def match_assignment(tenant, lobby_id, tickets, recipient, now=None):
    """TrackMatchmakingTicket reply announcing a found session to `recipient`.

    Wire layout from the generated serializers (see docs/NETCODE.md):
      track reply  main+0x1408a40: 1 ticket, 2 config, 4 Player*, 5 state,
                                   6 MatchedPlayer*, 7 GameSession
      MatchedPlayer main+0x140c708: 1 Player, 2 userSession resource,
                                   3 user-session id (read as Gamesync "__us/<id>")
      GameSession  main+0x140b440: 1 name, 10 Timestamp, 12 Member*
      Member       main+0x1405fec: 1 user (string), 2 userSession, 4 Timestamp
    The join check main+0x138dea0 needs every local user in field 6 with a
    parseable userSession (field 2); without it the client reports
    FAILED_PRECONDITION (2321-3072).
    """
    now = time.time() if now is None else now
    name = f"{tenant}/gameSessions/{lobby_id}"
    matched = members = b""
    for ticket in tickets:
        user_id = ticket.user.rsplit("/", 1)[-1]
        user_session = f"{name}/userSessions/{user_id}"
        player = player_for(ticket.user, ticket.player)
        matched += protobuf_bytes(6, protobuf_bytes(1, player) + protobuf_bytes(2, user_session) +
                                  protobuf_bytes(3, user_id))
        members += protobuf_bytes(12, protobuf_bytes(1, ticket.user) + protobuf_bytes(2, user_session) +
                                  protobuf_bytes(4, timestamp(now)))
    session = protobuf_bytes(1, name) + protobuf_bytes(10, timestamp(now + 600)) + members
    return (protobuf_bytes(1, f"{tenant}/matchmakingTickets/{recipient.id}") +
            protobuf_bytes(2, f"{tenant}/matchmakingConfigs/{recipient.mode}") +
            protobuf_bytes(4, player_for(recipient.user, recipient.player)) +
            b"\x28\x03" + matched + protobuf_bytes(7, session))


SECOND_LOCAL_TOKEN = b"eyJhbGciOiJub25lIn0.eyJzdWIiOiJta3dtcC1sb2NhbC0yIiwiaXNzIjoibWt3bXAifQ."


def auth_response(request_body):
    """Local test response; wire layout recovered from main+0xec3cc0/0xec7100."""
    # Read field 1 (tenant) from this specific request's uncompressed protobuf.
    messages = decode_grpc_messages(request_body)
    tenant = "tenants/t-e96531c6-lp1"
    slot = 1
    if messages and not messages[0][0]:
        message = messages[0][1]
        if message and message[0] == 10:
            size, start = _varint(message, 1)
            tenant = message[start:start + size].decode("utf-8")
            if message[start + size:] == protobuf_bytes(3, protobuf_bytes(1, SECOND_LOCAL_TOKEN)):
                slot = 2
    user = tenant + f"/users/00000000-0000-4000-8000-{slot:012d}"
    issued = int(time.time())
    def b64(value):
        return base64.urlsafe_b64encode(json.dumps(value, separators=(",", ":")).encode()).rstrip(b"=").decode()
    token = b64({"alg": "none", "typ": "JWT"}) + "." + b64({
        "sub": user, "iss": "mkwmp-local", "iat": issued, "exp": issued + 3600}) + "."
    duration = b"\x08" + encode_varint(3600)  # google.protobuf.Duration.seconds
    token_message = (protobuf_bytes(1, user) + protobuf_bytes(2, token) +
                     protobuf_bytes(3, f"mkwmp-local-refresh-token-{slot}") + protobuf_bytes(4, duration))
    return protobuf_bytes(2, token_message)


BULK_GET = "/nn.npln.ugcstore.v1.Ugcstore/BulkGetDocuments"


def bulk_get_responses(body):
    """Return one minimal local document per requested profile."""
    names = []
    for flag, msg in decode_grpc_messages(body):
        if flag:
            return []
        pos = 0
        while pos < len(msg):
            tag, pos = _varint(msg, pos)
            if tag & 7 != 2:
                return []
            size, pos = _varint(msg, pos)
            end = pos + size
            if end > len(msg):
                return []
            if tag == 18:
                names.append(msg[pos:end])
            pos = end
    # Result.document = 1 (0x1492980); Document.name = 1, fields = 2
    # (0x14903c0). Map.entries = 1, entry key/value = 1/2
    # (0xf1b460/0xf1d16c). Value.double = 5 (0xf19160).
    # The game reads "Rating" at 0x49c54c; 1500 is its initialized default.
    value = b"\x29" + struct.pack("<d", 1500.0)
    fields = protobuf_bytes(1, protobuf_bytes(1, "Rating") + protobuf_bytes(2, value))
    return [protobuf_bytes(1, protobuf_bytes(1, name) + protobuf_bytes(2, fields)) for name in names]


# ---- HTTP/2 server ----------------------------------------------------------
class H2Protocol(asyncio.Protocol):
    matchmaking = LocalMatchmaking()
    issued_tokens = {}
    active_trackers = {}
    announce_matches = False

    def __init__(self):
        self.conn = H2Connection(H2Configuration(client_side=False, header_encoding="utf-8"))
        self.transport = None
        self.streams = {}   # stream_id -> {"path":..., "data":bytearray}
        self.subscriptions = {}
        self.tracked_tickets = {}
        self.user = None

    def connection_made(self, transport):
        self.transport = transport
        peer = transport.get_extra_info("peername")
        tls = transport.get_extra_info("ssl_object")
        log(f"--- connection from {peer}" + (f" (TLS, ALPN={tls.selected_alpn_protocol()})" if tls else "") + " ---")
        self.conn.initiate_connection()
        transport.write(self.conn.data_to_send())

    def ticket_user(self, token):
        # A fresh local sign-in on this connection takes precedence over any
        # bearer the game retained from its previous profile or login.
        return self.user or self.issued_tokens.get(token)

    def data_received(self, data):
        try:
            events = self.conn.receive_data(data)
        except Exception as e:  # noqa: BLE001
            log(f"h2 receive error: {e!r}")
            self.transport.close()
            return
        for ev in events:
            if isinstance(ev, RequestReceived):
                hdrs = dict(ev.headers)
                self.streams[ev.stream_id] = {"path": hdrs.get(":path", "?"),
                                              "authority": hdrs.get(":authority", "?"),
                                              "token": hdrs.get("authorization", "").removeprefix("Bearer "),
                                              "data": bytearray()}
            elif isinstance(ev, DataReceived):
                st = self.streams.get(ev.stream_id)
                if st is not None:
                    st["data"].extend(ev.data)
                    if st["path"] == PRESENCE_KEEPALIVE:
                        self.handle_keepalive(ev.stream_id, st)
                self.conn.acknowledge_received_data(ev.flow_controlled_length, ev.stream_id)
            elif isinstance(ev, StreamEnded):
                self.handle_stream(ev.stream_id)
            elif isinstance(ev, StreamReset):
                self.streams.pop(ev.stream_id, None)
                path = self.subscriptions.pop(ev.stream_id, None)
                self.release_tracker(ev.stream_id)
                if path:
                    log(f"--- subscription cancelled: {path} (HTTP/2 error {ev.error_code}) ---")
            elif isinstance(ev, WindowUpdated):
                for sid, st in self.streams.items():
                    if st["path"] == PRESENCE_KEEPALIVE and ev.stream_id in (0, sid):
                        self.handle_keepalive(sid, st)
        out = self.conn.data_to_send()
        if out:
            self.transport.write(out)

    def handle_keepalive(self, sid, st):
        data = st["data"]
        while len(data) >= 5:
            size = int.from_bytes(data[1:5], "big") + 5
            if len(data) < size or self.conn.local_flow_control_window(sid) < 5:
                return
            frame = bytes(data[:size])
            del data[:size]
            if sid not in self.subscriptions:
                self.conn.send_headers(sid, [(":status", "200"), ("content-type", "application/grpc")])
                self.subscriptions[sid] = PRESENCE_KEEPALIVE
            log(f">>> gRPC CALL {PRESENCE_KEEPALIVE} (stream {sid}, {size} body bytes)")
            log(f"    raw gRPC body: {frame.hex()}")
            self.conn.send_data(sid, grpc_frame(b""))

    def handle_stream(self, sid):
        st = self.streams.pop(sid, None)
        if st is None:
            return
        if st["path"] == PRESENCE_KEEPALIVE:
            if self.subscriptions.pop(sid, None) is None:
                self.conn.send_headers(sid, [(":status", "200"), ("content-type", "application/grpc")])
            self.conn.send_headers(sid, [("grpc-status", "3" if st["data"] else "0")], end_stream=True)
            return
        body = bytes(st["data"])
        log(f">>> gRPC CALL {st['path']}  (authority={st['authority']}, {len(body)} body bytes)")
        log(f"    raw gRPC body: {body.hex()}")
        for flag, msg in decode_grpc_messages(body):
            log(f"    request message ({len(msg)} bytes, flag={flag}):")
            for line in decode_protobuf(msg):
                log(line)
            if not msg:
                log("      <empty>")
        response = (auth_response(body) if st["path"] == "/nn.npln.auth.v1.Auth/IssuePrearrangedUserToken"
                    else b"")
        self.conn.send_headers(sid, [(":status", "200"), ("content-type", "application/grpc")])
        if response:
            token_info = bytes_field(response, 2)
            self.user = bytes_field(token_info, 1).decode()
            self.issued_tokens[bytes_field(token_info, 2).decode()] = self.user
        if st["path"] == CREATE_TICKET:
            user = self.ticket_user(st["token"])
            if user is None:
                self.conn.send_headers(sid, [("grpc-status", "16")], end_stream=True)
                log("<<< CreateMatchmakingTicket: no local account")
                return
            try:
                response = create_ticket_response(self.matchmaking, user, body)
                log(f"--- queued local matchmaking user {user.rsplit('/', 1)[-1]} ---")
            except (IndexError, ValueError):
                self.conn.send_headers(sid, [("grpc-status", "3")], end_stream=True)
                log("<<< CreateMatchmakingTicket: invalid request")
                return
        if st["path"] == TRACK_TICKET:
            user = self.ticket_user(st["token"])
            try:
                (flag, message), = decode_grpc_messages(body)
                prefix, ticket_id = bytes_field(message, 1).decode().rsplit("/", 1)
                if flag or prefix != "tenants/current/matchmakingTickets":
                    raise ValueError("invalid ticket resource")
                ticket = self.matchmaking.tickets.get(UUID(ticket_id))
            except (IndexError, ValueError):
                self.conn.send_headers(sid, [("grpc-status", "3")], end_stream=True)
                return
            if user is None or ticket is None or ticket.user != user:
                self.conn.send_headers(sid, [("grpc-status", "5")], end_stream=True)
                return
            self.subscriptions[sid] = TRACK_TICKET
            self.tracked_tickets[sid] = (user, ticket.id)
            self.active_trackers.setdefault(ticket.id, set()).add((self, sid))
            log(f"<<< tracking {ticket.id}; waiting for a match update")
            if self.announce_matches:
                self.publish_match(ticket)
            return
        if st["path"] == BULK_GET:
            try:
                results = bulk_get_responses(body)
            except (IndexError, ValueError):
                results = []
            frames = b"".join(map(grpc_frame, results))
            if not frames or len(frames) > self.conn.local_flow_control_window(sid):
                self.conn.send_headers(sid, [("grpc-status", "3")], end_stream=True)
                log("<<< BulkGetDocuments: invalid or oversized profile batch")
                return
            limit = self.conn.max_outbound_frame_size
            for offset in range(0, len(frames), limit):
                self.conn.send_data(sid, frames[offset:offset + limit])
            self.conn.send_headers(sid, [("grpc-status", "0")], end_stream=True)
            log(f"<<< BulkGetDocuments: {len(results)} profile documents with Rating=1500 (grpc-status 0)")
            return
        self.conn.send_data(sid, grpc_frame(response))
        if st["path"] in SUBSCRIPTIONS:
            # A zero-item snapshot is valid protobuf. Keep the server-streaming
            # RPC open for later updates rather than triggering reconnects.
            self.subscriptions[sid] = st["path"]
            self.transport.write(self.conn.data_to_send())
            log(f"<<< opened {st['path']} with an empty snapshot; stream remains active")
            return
        self.conn.send_headers(sid, [("grpc-status", "0"), ("grpc-message", "")], end_stream=True)
        self.transport.write(self.conn.data_to_send())
        log(f"<<< responded to {st['path']} with {len(response)} protobuf bytes (grpc-status 0)")
        if response:
            log(f"    raw response protobuf: {response.hex()}")

    def publish_match(self, ticket):
        if ticket.lobby is None:
            return
        lobby = self.matchmaking.lobbies[ticket.lobby]
        groups = [self.active_trackers.get(seat) for seat in lobby.seats]
        if not all(groups):
            return
        tickets = [self.matchmaking.tickets[seat] for seat in lobby.seats]
        tenant = ticket.user.split("/users/", 1)[0]
        sends = [(peer, sid, grpc_frame(match_assignment(tenant, lobby.id, tickets, seat_ticket)))
                 for seat_ticket, group in zip(tickets, groups) for peer, sid in group]
        if any(peer.conn.local_flow_control_window(sid) < len(frame) for peer, sid, frame in sends):
            return
        for seat in lobby.seats:
            del self.active_trackers[seat]
        for peer, sid, frame in sends:
            del peer.tracked_tickets[sid]
            del peer.subscriptions[sid]
            peer.conn.send_data(sid, frame)
            peer.conn.send_headers(sid, [("grpc-status", "0")], end_stream=True)
            peer.transport.write(peer.conn.data_to_send())
            log(f"    assignment frame stream {sid}: {frame[5:].hex()}")
        log(f"<<< assigned both tickets to local session {lobby.id}")

    def release_tracker(self, sid):
        tracker = self.tracked_tickets.pop(sid, None)
        if tracker is not None:
            user, ticket_id = tracker
            # A transport reconnect can overlap the previous stream. Only
            # release the seat once the last tracker for that ticket closes.
            remaining = self.active_trackers.get(ticket_id)
            if remaining is not None:
                remaining.discard((self, sid))
                if not remaining:
                    del self.active_trackers[ticket_id]
                    self.matchmaking.cancel(user, ticket_id)
                    log(f"--- released local ticket {ticket_id} ---")

    def connection_lost(self, exc):
        if self.subscriptions:
            log(f"--- connection closed with {len(self.subscriptions)} active subscriptions ---")
        self.streams.clear()
        self.subscriptions.clear()
        for sid in list(self.tracked_tickets):
            self.release_tracker(sid)


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=8900)
    ap.add_argument("--log", default=None)
    ap.add_argument("--tls-cert", help="PEM certificate for the embedded OpenSSL transport")
    ap.add_argument("--tls-key", help="matching PEM private key")
    ap.add_argument("--announce-matches", action="store_true", help="send experimental local session assignments")
    args = ap.parse_args()
    H2Protocol.announce_matches = args.announce_matches
    if bool(args.tls_cert) != bool(args.tls_key):
        ap.error("--tls-cert and --tls-key must be supplied together")
    tls = None
    if args.tls_cert:
        tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        tls.minimum_version = ssl.TLSVersion.TLSv1_2
        tls.load_cert_chain(args.tls_cert, args.tls_key)
        tls.set_alpn_protocols(["h2"])

    global LOG_FILE
    if args.log:
        LOG_FILE = open(args.log, "w", encoding="utf-8")

    loop = asyncio.get_running_loop()
    server = await loop.create_server(H2Protocol, args.host, args.port, ssl=tls)
    log(f"mkwmp gRPC capture server listening on {args.host}:{args.port} ({'TLS + h2' if tls else 'h2c, plaintext'})")
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("bye", file=sys.stderr)
