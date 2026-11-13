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
import os
import struct
import socket
import ssl
import sys
import time
from uuid import UUID

import turn_relay

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
GAMESYNC_ISSUE_TOKEN = "/nn.npln.gamesync.v1.Gamesync/IssueToken"
GAMESYNC_REFRESH_TOKEN = "/nn.npln.gamesync.v1.Gamesync/RefreshToken"
# Bidirectional streams (RpcMethod type 3 in the stub at main+0x12bae9c): log
# each client frame as it arrives and keep the stream open.
BIDI_STREAMS = {"/nn.npln.gamesync.v1.Gamesync/KeepUserSession"}


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


# A single-label host (e.g. "localhost") gets ".localdomain" appended for the
# TLS SNI, which our cert does not cover. A dotted FQDN is used verbatim and is
# in the capture cert's SANs; connects are redirected to loopback regardless.
GAMESYNC_HOST = "gamesync.npln.nintendo.net"
GAMESYNC_PORT = 8906


def match_assignment(tenant, lobby_id, tickets, recipient, now=None):
    """TrackMatchmakingTicket reply announcing a found session to `recipient`.

    Wire layout from the generated serializers (see docs/NETCODE.md):
      track reply  main+0x1408a40: 1 ticket, 2 config, 4 Player*, 5 state,
                                   6 MatchedPlayer*, 7 GameSession
      MatchedPlayer main+0x140c708: 1 Player, 2 userSession resource,
                                   3 user-session id (read as Gamesync "__us/<id>")
      GameSession  main+0x140b440: 1 name, 8 Gamesync host, 9 Gamesync port,
                                   10 Timestamp, 12 Member*
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
    # Fields 8/9 are the Gamesync endpoint; empty values made the client
    # resolve ":0" and fail Gamesync/IssueToken (2321-4992).
    session = (protobuf_bytes(1, name) + protobuf_bytes(8, GAMESYNC_HOST) +
               b"\x48" + encode_varint(GAMESYNC_PORT) +
               protobuf_bytes(10, timestamp(now + 600)) + members)
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


def bearer_token(header):
    # NPLN sends "Bearer"; the Gamesync interceptor (main+0x1297258) "bearer".
    return header[7:] if header[:7].lower() == "bearer " else header


def gamesync_token_response(request_body, fallback_user):
    """Gamesync IssueToken/RefreshToken reply.

    Response main+0x12a26fc: {1: Token}; Token main+0x12b94c4: 1 string
    (subject), 2 access token, 3 refresh token, 4 Duration (main+0x11b2768),
    the same layout as the Auth service's Token. Request main+0x12a1ee0 is
    {1 string, 2 string}; its field 1 is echoed as the subject when present.
    """
    subject = fallback_user or "tenants/local/users/unknown"
    messages = decode_grpc_messages(request_body)
    if messages and not messages[0][0]:
        try:
            subject = bytes_field(messages[0][1], 1).decode()
        except (ValueError, UnicodeDecodeError):
            pass
    issued = int(time.time())
    token = f"mkwmp-gamesync-{issued}-{abs(hash(subject)) % 10**8}"
    duration = b"\x08" + encode_varint(3600)
    return protobuf_bytes(1, protobuf_bytes(1, subject) + protobuf_bytes(2, token) +
                          protobuf_bytes(3, token + "-refresh") + protobuf_bytes(4, duration))


BULK_GET = "/nn.npln.ugcstore.v1.Ugcstore/BulkGetDocuments"


# Gamesync Value is a oneof (serializer main+0xf19724, discriminator = field):
#   3 = int (varint), 5 = double (fixed64), 7 = string, 6 = struct, 10 = list.
def gs_int(i):
    return b"\x18" + encode_varint(i)                      # Value field 3

def gs_str(s):
    return protobuf_bytes(7, s)                            # Value field 7

def gs_struct(entries):
    """Value{6: Struct}. Struct = {1: repeated Entry{1:key, 2:Value}} — the
    same key/Value encoding proven by ugcstore BulkGetDocuments."""
    body = b"".join(protobuf_bytes(1, protobuf_bytes(1, k) + protobuf_bytes(2, v)) for k, v in entries)
    return protobuf_bytes(6, body)


def gs_timestamp(seconds):
    return b"\x08" + encode_varint(int(seconds))


GAMESYNC_WRITE_DOCUMENTS = "/nn.npln.gamesync.v1.Gamesync/WriteDocuments"
ALLOCATE_ICE_SERVER_SET = "/nn.npln.matchmaking.v1.GameSessionService/AllocateIceServerSet"
TURN_PORT = 3478
PUBLIC_HOST = "127.0.0.1"   # address clients reach this server at (TURN, ICE set, __gs/f addr); --public-host


def ice_server_set_response(now=None):
    """AllocateIceServerSet response (serializer main+0x1410640, converter
    FUN_013cf8e0, consumer nplnd::IceServerConfigGetter FUN_016297a0):
      1 name, 2 {1 host, 2 port, 3 int}, 3 repeated IceServer,
      4 Duration, 5 Timestamp (expiry), 6 Duration
      IceServer (main+0x14121fc) {1 host, 2 port, 3 type (1 = TURN), 4 username, 5 credential}
    Pia's TURN client (TurnProtocol) must reach "ready" before AttachMeshJob
    finishes (FUN_01630b64 polls FUN_01634ef0)."""
    now = int(time.time()) if now is None else now
    server = (protobuf_bytes(1, PUBLIC_HOST) + b"\x10" + encode_varint(TURN_PORT) + b"\x18\x01" +
              protobuf_bytes(4, turn_relay.USERNAME) + protobuf_bytes(5, turn_relay.PASSWORD))
    return (protobuf_bytes(1, "tenants/current/iceServerSets/local") +
            protobuf_bytes(2, protobuf_bytes(1, PUBLIC_HOST) + b"\x10" + encode_varint(TURN_PORT)) +
            protobuf_bytes(3, server) +
            protobuf_bytes(4, b"\x08" + encode_varint(3600)) +
            protobuf_bytes(5, b"\x08" + encode_varint(now + 3600)) +
            protobuf_bytes(6, b"\x08" + encode_varint(3600)))


def parse_write_documents(body):
    """WriteDocumentsRequest: {1 repeated Write, 2 deferments}. Write is a oneof:
    1 update {1 Document, 2 precondition}, 3 transform {1 name, 2 repeated
    FieldTransform{1 field path, 3 increment, 4 maximum, 6/7 counter name}}.
    Returns [(document name, transform count)] per write."""
    writes = []
    for field, _, raw in iter_fields(body):
        if field != 1:
            continue
        _, start = _varint(raw, 0)
        length, start = _varint(raw, start)
        write = raw[start:start + length]
        name, count = "", 0
        for wf, _, wraw in iter_fields(write):
            _, s = _varint(wraw, 0)
            n, s = _varint(wraw, s)
            inner = wraw[s:s + n]
            if wf == 1:
                name = bytes_field(bytes_field(inner, 1), 1).decode(errors="replace")
            elif wf == 3:
                name = bytes_field(inner, 1).decode(errors="replace")
                count = sum(1 for f, _, _ in iter_fields(inner) if f == 2)
        writes.append((name, count))
    return writes


def write_documents_response(writes, value_of, now=None):
    """WriteDocumentsResponse (converter FUN_012a00a0): {1 repeated WriteResult,
    2 commit Timestamp}. WriteResult oneof: 1 = plain write ack, 2 = transform
    results {1 repeated Value}, one per request FieldTransform (keyed by the
    request's field paths, FUN_012a0440). value_of(name) gives the transform
    result (the server-allocated counter, e.g. upcsid)."""
    # The game builds ONE native write (set + transforms, FUN_01281500) that the
    # SDK splits into an update Write and a transform Write; results map back by
    # native write index, so the transform values belong in result 0.
    now = int(time.time()) if now is None else now
    total = sum(count for _, count in writes)
    name0 = next((name for name, count in writes if count), writes[0][0] if writes else "")
    writes = [(name0, total)] + [(name, 0) for name, _ in writes[1:]] if writes else []
    out = b""
    for name, count in writes:
        if count:
            result = protobuf_bytes(2, b"".join(protobuf_bytes(1, gs_int(value_of(name))) for _ in range(count)))
        else:
            result = protobuf_bytes(1, b"")
        out += protobuf_bytes(1, result)
    return out + protobuf_bytes(2, gs_timestamp(now))


def write_updates(body):
    """[(document name, fields body)] for each update Write (Write.1.1 Document)."""
    updates = []
    for field, _, raw in iter_fields(body):
        if field != 1:
            continue
        _, s = _varint(raw, 0)
        n, s = _varint(raw, s)
        try:
            document = bytes_field(bytes_field(raw[s:s + n], 1), 1)
        except ValueError:
            continue
        updates.append((bytes_field(document, 1).decode(errors="replace"), bytes_field(document, 2)))
    return updates


def doc_entries(fields_body):
    """Ordered {key: raw Value} from a fields body (repeated Entry{1 key, 2 Value})."""
    entries = {}
    for field, _, raw in iter_fields(fields_body):
        if field == 1:
            _, s = _varint(raw, 0)
            n, s = _varint(raw, s)
            entry = raw[s:s + n]
            entries[bytes_field(entry, 1).decode(errors="replace")] = bytes_field(entry, 2)
    return entries


def store_write(name, fields_body, transformed=None, now=None):
    """Store a written document, with server transform results applied
    (e.g. {"upcsid": int}). Returns (document bytes, created?)."""
    now = int(time.time()) if now is None else now
    entries = doc_entries(fields_body)
    for key, value in (transformed or {}).items():
        entries[key] = gs_int(value)
    doc = (protobuf_bytes(1, name) + protobuf_bytes(2, gs_fields(entries.items())) +
           protobuf_bytes(3, gs_timestamp(now)) + protobuf_bytes(4, gs_timestamp(now)))
    created = name not in DOC_STORE
    DOC_STORE[name] = doc
    return doc, created


def user_slot(user_id):
    """Local player number from the probe's synthetic user id (...0001, ...0002)."""
    tail = user_id.rsplit("/", 1)[-1].replace("-", "")
    try:
        return int(tail[-4:], 16) or 1
    except ValueError:
        return 1


def gs_fields(entries):
    """Document.fields / struct body (main+0xf1b700): repeated Entry{1:key, 2:Value}."""
    return b"".join(protobuf_bytes(1, protobuf_bytes(1, k) + protobuf_bytes(2, v)) for k, v in entries)


def user_session_document(name, user_id, now=None, lobby=None):
    """Document (class 11, main+0x12ab0e8): {1 name, 2 fields, 3 create_time,
    4 update_time}. fields["f"] is the descriptor struct read by FUN_01267698.
    The client never writes these keys (only readers reference them): the
    Gamesync server authors them. The game copies `mcn` (descriptor+0x50,
    FUN_01267750). Values are shared per lobby so both players land in the
    same session; semantics still inferred."""
    now = int(time.time()) if now is None else now
    session_key = str(lobby) if lobby else f"solo-{user_id}"
    # User-session keys parsed by FUN_0128b2b4: uid (str), ussid/ucsid/upcsid
    # (int32), pgn (str, P2P group name -> client follows __pgn/<pgn>/...).
    # The ids are nonzero and distinct per player; their meaning is inferred.
    local_id = int(user_id[-4:], 16) if all(c in "0123456789abcdef" for c in user_id[-4:]) else 1
    session_keys = [
        ("uid", gs_str(user_id)),
        ("ussid", gs_int(local_id)),
        ("ucsid", gs_int(local_id)),
        ("upcsid", gs_int(local_id)),
        ("pgn", gs_str(session_key)),
    ]
    return (protobuf_bytes(1, name) + protobuf_bytes(2, gs_fields(session_keys)) +
            protobuf_bytes(3, gs_timestamp(now)) + protobuf_bytes(4, gs_timestamp(now)))


GS_FIXED_DOC = "docs/__gs/f"   # name checked by FUN_01267820 against "__gs" + "/" + "f"
LOBBY_HOSTS = {}   # lobby id -> user id of the lowest-slot member (filled at assignment)
LOBBY_SECRETS = {}  # lobby id -> shared random bytes served as __gs/f rs


def game_session_fixed_document(lobby, now=None):
    """Game-session fixed data (parser FUN_01267698): gsid, addr, p (u16), mcn,
    maxu, rs (list, omitted). Shared by every member of the lobby. Never written
    by the client; the server pushes it on the session stream."""
    now = int(time.time()) if now is None else now
    key = str(lobby) if lobby else "solo"
    # mcn: hypothesis "mesh creator" = the member that hosts the Pia mesh (both
    # clients otherwise stay at station index 0xfd). Lowest slot hosts.
    mcn = LOBBY_HOSTS.get(key, "00000000-0000-4000-8000-000000000001")
    fields = gs_fields([
        ("gsid", gs_str(f"gs-{key}")),
        ("addr", gs_str(PUBLIC_HOST)),
        ("p", gs_int(10025)),
        ("mcn", gs_str(mcn)),
        ("maxu", gs_int(2)),
        # rs: bytes (read like "pl", Value field 8). Hypothesis: shared session
        # secret/seed; same random 32 bytes for every member of the lobby.
        ("rs", protobuf_bytes(8, LOBBY_SECRETS.setdefault(key, os.urandom(32)))),
    ])
    return (protobuf_bytes(1, GS_FIXED_DOC) + protobuf_bytes(2, fields) +
            protobuf_bytes(3, gs_timestamp(now)) + protobuf_bytes(4, gs_timestamp(now)))


def change_message(name, kind, document):
    """Change (class 30, main+0x12b7d00): {1 string, 2 varint, 3 Document}."""
    return protobuf_bytes(1, name) + b"\x10" + encode_varint(kind) + (protobuf_bytes(3, document) if document else b"")


def session_message(session, change=b"", ops=b""):
    """KeepUserSession message (class 8, main+0x12a8064), both directions:
    {1 string session, 2 Change, 3 repeated Op{1 bytes, 2 varint, 3 Status}}."""
    out = protobuf_bytes(1, session)
    if change:
        out += protobuf_bytes(2, change)
    return out + ops


CHANGE_ADDED, CHANGE_MODIFIED = 1, 2   # kind 3 is not a document change (FUN_01271c00)
OP_SUCCEEDED, OP_CURRENT, OP_FAILED = 1, 2, 4   # stream handler FUN_01271c00


def op_ack(op_id, kind=OP_SUCCEEDED):
    """Server Op (message field 3): {1 decimal op id, 2 kind}. The client numbers
    its ops per stream (1, 2, ...; also the "targets/N" suffix) and keeps a
    future per id; kind 1 completes it, which the user-session job waits on."""
    return protobuf_bytes(3, protobuf_bytes(1, str(op_id)) + b"\x10" + encode_varint(kind))


def target_op_id(target):
    return int(target.rsplit("/", 1)[-1]) if target.rsplit("/", 1)[-1].isdigit() else 1


def parse_keep_user_session(body):
    """Client frame (class 8): {1 session, 3 repeated Op}. Op.1 is a serialized
    Target {1 target name, 2 {1 document name}}. Returns (session, [(target, doc)])."""
    session = bytes_field(body, 1).decode()
    targets = []
    for field, _, raw in iter_fields(body):
        if field != 3:
            continue
        _, start = _varint(raw, 0)
        length, start = _varint(raw, start)
        op = raw[start:start + length]
        target_bytes = bytes_field(op, 1)
        target = bytes_field(target_bytes, 1).decode()
        doc_name = ""
        for tf, _, traw in iter_fields(target_bytes):
            if tf in (2, 3):   # 2 = single document, 3 = collection (marked by a trailing "/")
                _, s = _varint(traw, 0)
                n, s = _varint(traw, s)
                doc_name = bytes_field(traw[s:s + n], 1).decode() + ("/" if tf == 3 else "")
        targets.append((target, doc_name))
    return session, targets


def removed_targets(body):
    """Client frame field 4: remove target {1 target name}. Returns target ids."""
    ids = []
    for field, _, raw in iter_fields(body):
        if field == 4:
            _, s = _varint(raw, 0)
            n, s = _varint(raw, s)
            ids.append(target_op_id(bytes_field(raw[s:s + n], 1).decode()))
    return ids


# In-memory Gamesync document store (name -> serialized Document), filled by
# WriteDocuments and served to listeners (single documents and collections).
DOC_STORE = {}


def in_collection(name, collection):
    """collection ends with "/"; direct children only."""
    return name.startswith(collection) and "/" not in name[len(collection):]


def lower_members_present(collection, slot):
    """True when every lower slot (1..slot-1) has a document in the collection."""
    have = {user_slot(name) for name in DOC_STORE if in_collection(name, collection)}
    return all(s in have for s in range(1, slot))


GAMESYNC_GET_DOCUMENT = "/nn.npln.gamesync.v1.Gamesync/GetDocument"


def get_document(name, lobby=None, now=None):
    """GetDocument (request {1 name}): the game-session fixed data or a stored
    document. The response is the Document itself (FUN_012a0c00 parses its
    field 1 as the document name; a wrapper aborts the SDK)."""
    if name == GS_FIXED_DOC:
        return game_session_fixed_document(lobby, now)
    return DOC_STORE.get(name, b"")


def stored_documents(doc_name):
    if doc_name.endswith("/"):
        return [doc for name, doc in sorted(DOC_STORE.items()) if in_collection(name, doc_name)]
    return [DOC_STORE[doc_name]] if doc_name in DOC_STORE else []


def keep_user_session_replies(session, targets, now=None, lobby_of=lambda user_id: None, members=()):
    """Per target: ADDED changes for the matching documents, then the op ack.
    docs/__us/<id> carries the descriptor (shared per lobby via lobby_of) plus
    the game-session fixed data; other targets are served from DOC_STORE."""
    # Change.1 is the numeric target id (FUN_01271c00 strtoul's it to route
    # the change to the subscription callback); kind 1/2 carry a document.
    out = []
    for target, doc_name in targets:
        op_id = target_op_id(target)
        tid = str(op_id)
        if doc_name == "docs/__us/":
            # Collection listen on every user session (after the mesh join):
            # one ADDED per lobby member (members = user ids of the lobby).
            for user_id in members:
                out.append(session_message(session, change_message(tid, CHANGE_ADDED, user_session_document(
                    f"docs/__us/{user_id}", user_id, now, lobby_of(user_id)))))
        elif doc_name == "docs/__gs/":
            # Game-session collection (listened after the mesh join): the
            # fixed data __gs/f, the same document the clients GetDocument.
            lobby = lobby_of(members[0]) if members else None
            out.append(session_message(session, change_message(
                tid, CHANGE_ADDED, game_session_fixed_document(lobby, now))))
            for doc in stored_documents(doc_name):
                out.append(session_message(session, change_message(tid, CHANGE_ADDED, doc)))
        elif doc_name.startswith("docs/__us/"):
            user_id = doc_name.rsplit("/", 1)[-1]
            lobby = lobby_of(user_id)
            doc = user_session_document(doc_name, user_id, now, lobby)
            # ADDED user session, ADDED game-session fixed data.
            out.append(session_message(session, change_message(tid, CHANGE_ADDED, doc)))
            out.append(session_message(session, change_message(
                tid, CHANGE_ADDED, game_session_fixed_document(lobby, now))))
        else:
            for doc in stored_documents(doc_name):
                out.append(session_message(session, change_message(tid, CHANGE_ADDED, doc)))
        # Op kind 2 = target in sync: FUN_01271c00 posts task FUN_0127c880, which
        # runs the listener's second callback (e.g. FUN_0128a378, which releases
        # the P2P initial sequence). Then kind 1 completes the client's op.
        out.append(session_message(session, ops=op_ack(op_id, OP_CURRENT)))
        out.append(session_message(session, ops=op_ack(op_id)))
    return out


def keep_user_session_response(doc_name, user_id, now=None):
    """KeepUserSession stream response. Recovered from the serializers:
      Snapshot (main+0x12a6020)       { 1: repeated DocumentChange, 2: Timestamp }
      DocumentChange (main+0x12b0430) { 1: Document, 2: name }
      Document (main+0x12af860)       { 1: Value body, 2: name }
      Value (main+0xf19724)           oneof: 3 int, 5 double, 7 string, 6 struct
    The game (FUN_022364b0) looks up child key "f" in the body struct, so the
    doc body is a struct whose "f" entry is the peer descriptor struct
    (gsid/addr/p/mcn/maxu, + ipc/dpc read by FUN_012679d8).
    """
    now = int(time.time()) if now is None else now
    descriptor = gs_struct([
        ("gsid", gs_str(f"gs-{user_id}")),
        ("addr", gs_str(PUBLIC_HOST)),
        ("p", gs_int(10025)),
        ("mcn", gs_str("")),
        ("maxu", gs_int(2)),
        ("ipc", gs_int(0)),
        ("dpc", gs_int(0)),
    ])
    body = gs_struct([("f", descriptor)])                 # top-level Value struct
    document = protobuf_bytes(1, body) + protobuf_bytes(2, doc_name)
    doc_change = protobuf_bytes(1, document) + protobuf_bytes(2, doc_name)
    read_time = protobuf_bytes(2, b"\x08" + encode_varint(now))
    return protobuf_bytes(1, doc_change) + read_time


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
# --kus-probe: per-slot KeepUserSession replies used to discover the response
# type's wire layout. An empty message parses as any type.
# Each slot gets a sequence, sent KUS_PROBE_GAP seconds apart on the same
# stream. A probe that kills the session (parse or semantic failure) shows up
# as a client error; its timestamp says which one.
# Run p1/p2 (2026-11-13): empty, and every field 1-4 as empty len or varint 0,
# all survived. Protobuf skips wrong-wire-type fields as unknown, so those
# probes cannot see the schema. Parse failures come from malformed bytes,
# broken nested messages, or invalid UTF-8 in proto3 string fields.
# Run p3: P2 survived {2: not-utf8}, died on {1: not-utf8}; P1 survived every
# build-up step whose field-1 bytes were < 0x80 and died on the full document
# (field-1 bytes 0xf3...). Hypothesis: response field 1 is a proto3 string.
KUS_PROBE_GAP = 8.0
_NOT_UTF8 = b"\x08\x80\x01"   # valid message {1: 128}, invalid as UTF-8
_NOT_MSG = "é".encode()  # b"\xc3\xa9": valid UTF-8, malformed as a message


# Run p4: {1: "é"} survived (field 1 = string); {2: "é"} and {3: "é"} died
# (fields 2, 3 = messages). The response is class 8 (main+0x12a8064),
# the same "session message" the client sends. See session_message().
TARGET_1 = "userSessions/current/targets/1"


def _kus_probes(slot, name):
    user_id = name.rsplit("/", 1)[-1]
    doc = user_session_document(name, user_id)
    if slot == "1":   # Change.1 = target name; vary the change kind
        return [session_message("userSessions/current", change_message(TARGET_1, k, doc)) for k in (1, 0, 2)]
    if slot == "2":   # vary Change.1 (document name / empty) and kind 3
        return [session_message("userSessions/current", change_message(name, 1, doc)),
                session_message("userSessions/current", change_message("", 1, doc)),
                session_message("userSessions/current", change_message(TARGET_1, 3, doc))]
    return []


class H2Protocol(asyncio.Protocol):
    kus_probe = False
    matchmaking = LocalMatchmaking()
    issued_tokens = {}
    active_trackers = {}
    announce_matches = False
    watchers = []   # (protocol, stream id, session, target id, document or collection/)
    pending = []    # deferred __pus listens: (protocol, sid, session, target, collection/, slot)

    def __init__(self):
        self.conn = H2Connection(H2Configuration(client_side=False, header_encoding="utf-8"))
        self.transport = None
        self.streams = {}   # stream_id -> {"path":..., "data":bytearray}
        self.subscriptions = {}
        self.tracked_tickets = {}
        self.stream_user = {}   # KeepUserSession stream id -> user id (from docs/__us/<id>)
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
                                              "token": bearer_token(hdrs.get("authorization", "")),
                                              "data": bytearray()}
                if self.streams[ev.stream_id]["path"] in BIDI_STREAMS:
                    self.handle_bidi(ev.stream_id, self.streams[ev.stream_id])
            elif isinstance(ev, DataReceived):
                st = self.streams.get(ev.stream_id)
                if st is not None:
                    st["data"].extend(ev.data)
                    if st["path"] == PRESENCE_KEEPALIVE:
                        self.handle_keepalive(ev.stream_id, st)
                    elif st["path"] in BIDI_STREAMS:
                        self.handle_bidi(ev.stream_id, st)
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

    def handle_bidi(self, sid, st):
        """Log each complete client frame of a bidi stream; keep it open."""
        data = st["data"]
        if sid not in self.subscriptions:
            self.conn.send_headers(sid, [(":status", "200"), ("content-type", "application/grpc")])
            self.subscriptions[sid] = st["path"]
            log(f">>> gRPC STREAM {st['path']} opened (stream {sid}, authority={st['authority']})")
        while len(data) >= 5:
            size = int.from_bytes(data[1:5], "big") + 5
            if len(data) < size:
                return
            frame = bytes(data[:size])
            del data[:size]
            log(f">>> {st['path']} frame ({size - 5} bytes): {frame[5:].hex()}")
            for line in decode_protobuf(frame[5:]):
                log(line)
            if st["path"] in BIDI_STREAMS and self.announce_matches:
                self.answer_keep_user_session(sid, frame[5:])

    @classmethod
    def publish(cls, name, doc, kind):
        """Push a stored document change to every listener whose target covers it."""
        for proto, sid, session, tid, target in list(cls.watchers):
            if not (target == name or (target.endswith("/") and in_collection(name, target))):
                continue
            if sid not in proto.subscriptions or proto.transport is None or proto.transport.is_closing():
                cls.watchers.remove((proto, sid, session, tid, target))
                continue
            proto.conn.send_data(sid, grpc_frame(session_message(session, change_message(str(tid), kind, doc))))
            proto.transport.write(proto.conn.data_to_send())
            log(f"<<< KeepUserSession push {name} -> {proto.user or '?'} target {tid} (kind {kind})")

    def lobby_members(self, sid):
        """User ids in the lobby of this stream's user (lowest slot first)."""
        lobby = self.lobby_of(self.stream_user.get(sid, ""))
        if lobby is None:
            return [self.stream_user[sid]] if self.stream_user.get(sid) else []
        users = {t.user.rsplit("/", 1)[-1] for t in self.matchmaking.tickets.values() if t.lobby == lobby}
        return sorted(users, key=user_slot)

    def lobby_of(self, user_id):
        """Lobby the user was matched into (tickets persist after assignment)."""
        for ticket in self.matchmaking.tickets.values():
            if ticket.user.rsplit("/", 1)[-1] == user_id and ticket.lobby:
                return ticket.lobby
        return None

    def send_probe(self, sid, slot, index, probe):
        if sid not in self.subscriptions or self.transport.is_closing():
            log(f"<<< KeepUserSession PROBE slot {slot} #{index}: skipped (stream gone)")
            return
        self.conn.send_data(sid, grpc_frame(probe))
        self.transport.write(self.conn.data_to_send())
        log(f"<<< KeepUserSession PROBE slot {slot} #{index}: {probe.hex() or '(empty)'}")

    def answer_keep_user_session(self, sid, body):
        """Answer each target the client subscribes to (see keep_user_session_replies)."""
        try:
            session, targets = parse_keep_user_session(body)
        except (ValueError, UnicodeDecodeError):
            log("<<< KeepUserSession: unparseable client frame")
            return
        if self.kus_probe:
            # Wire-type probe sequence for this local slot (last digit of id).
            for _, doc_name in targets:
                slot = doc_name.rsplit("/", 1)[-1][-1:]
                loop = asyncio.get_running_loop()
                for i, probe in enumerate(_kus_probes(slot, doc_name)):
                    loop.call_later(i * KUS_PROBE_GAP, self.send_probe, sid, slot, i, probe)
            return
        for tid in removed_targets(body):
            H2Protocol.watchers = [w for w in H2Protocol.watchers if not (w[0] is self and w[1] == sid and w[3] == tid)]
            log(f"--- KeepUserSession target {tid} removed")
        ready = []
        for target, doc_name in targets:
            log(f"--- KeepUserSession target {target} -> {doc_name or '(no document)'}"
                f" ({len(stored_documents(doc_name))} stored)")
            if doc_name.startswith("docs/__us/") and not doc_name.endswith("/"):
                self.stream_user[sid] = doc_name.rsplit("/", 1)[-1]
            slot = user_slot(self.stream_user.get(sid, ""))
            if doc_name.endswith("/__pus/") and not lower_members_present(doc_name, slot):
                # Answer later (release_pending) so lower slots arrive in the
                # snapshot: the lower slot then initiates, the higher accepts.
                H2Protocol.pending.append((self, sid, session, target, doc_name, slot))
                log(f"--- KeepUserSession target {target_op_id(target)} deferred until slots < {slot} joined")
                continue
            ready.append((target, doc_name))
        self.answer_targets(sid, session, ready)

    def answer_targets(self, sid, session, targets):
        """Send each target's documents + sync/ack ops and start watching it."""
        for target, doc_name in targets:
            if doc_name and not doc_name.startswith("docs/__us/"):
                H2Protocol.watchers.append((self, sid, session, target_op_id(target), doc_name))
        try:
            frames = b"".join(grpc_frame(m) for m in keep_user_session_replies(
                session, targets, lobby_of=self.lobby_of, members=self.lobby_members(sid)))
        except Exception as exc:  # noqa: BLE001 - never kill the session stream
            log(f"<<< KeepUserSession: failed to answer {targets}: {exc!r}")
            return
        if not frames or self.transport is None or self.transport.is_closing():
            return
        self.conn.send_data(sid, frames)
        self.transport.write(self.conn.data_to_send())
        log(f"<<< KeepUserSession: {len(targets)} target(s) answered ({len(frames)} bytes)")

    @classmethod
    def release_pending(cls):
        for entry in list(cls.pending):
            proto, sid, session, target, doc_name, slot = entry
            if lower_members_present(doc_name, slot):
                cls.pending.remove(entry)
                if sid in proto.subscriptions:
                    log(f"--- KeepUserSession target {target_op_id(target)} released (slot {slot})")
                    proto.answer_targets(sid, session, [(target, doc_name)])

    def handle_stream(self, sid):
        st = self.streams.pop(sid, None)
        if st is None:
            return
        if st["path"] in BIDI_STREAMS:
            # Client half-closed: finish the stream cleanly.
            self.handle_bidi(sid, st)
            self.subscriptions.pop(sid, None)
            self.conn.send_headers(sid, [("grpc-status", "0")], end_stream=True)
            log(f"<<< closed {st['path']} after client half-close")
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
        is_auth = st["path"] == "/nn.npln.auth.v1.Auth/IssuePrearrangedUserToken"
        response = auth_response(body) if is_auth else b""
        if st["path"] in (GAMESYNC_ISSUE_TOKEN, GAMESYNC_REFRESH_TOKEN):
            response = gamesync_token_response(body, self.ticket_user(st["token"]))
        if st["path"] == ALLOCATE_ICE_SERVER_SET:
            response = ice_server_set_response()
            log(f"<<< AllocateIceServerSet: TURN {PUBLIC_HOST}:{TURN_PORT}")
        if st["path"] == GAMESYNC_GET_DOCUMENT:
            msgs = decode_grpc_messages(body)
            name = bytes_field(msgs[0][1], 1).decode(errors="replace") if msgs else ""
            user = (self.ticket_user(st["token"]) or "").rsplit("/", 1)[-1]
            doc = get_document(name, self.lobby_of(user))
            response = doc
            log(f"<<< GetDocument {name}: {'found' if doc else 'not found'}")
        if st["path"] == GAMESYNC_WRITE_DOCUMENTS:
            msgs = decode_grpc_messages(body)
            writes = parse_write_documents(msgs[0][1]) if msgs else []
            response = write_documents_response(writes, user_slot)
            transformed = {name: {"upcsid": user_slot(name)} for name, count in writes if count}
            for name, fields_body in (write_updates(msgs[0][1]) if msgs else []):
                doc, created = store_write(name, fields_body, transformed.get(name))
                H2Protocol.publish(name, doc, CHANGE_ADDED if created else 2)
            H2Protocol.release_pending()
            log(f"<<< WriteDocuments: {writes} -> upcsid by slot; stored {len(DOC_STORE)} docs")
        self.conn.send_headers(sid, [(":status", "200"), ("content-type", "application/grpc")])
        if is_auth:
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
        LOBBY_HOSTS[str(lobby.id)] = min((t.user.rsplit("/", 1)[-1] for t in tickets), key=user_slot)
        tenant =ticket.user.split("/users/", 1)[0]
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
    global GAMESYNC_PORT, PUBLIC_HOST
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=8900)
    ap.add_argument("--public-host", default=None,
                    help="LAN address clients use for TURN/ICE/__gs addr (default: --host, or 127.0.0.1 for 0.0.0.0)")
    ap.add_argument("--log", default=None)
    ap.add_argument("--tls-cert", help="PEM certificate for the embedded OpenSSL transport")
    ap.add_argument("--tls-key", help="matching PEM private key")
    ap.add_argument("--announce-matches", action="store_true", help="send experimental local session assignments")
    ap.add_argument("--gamesync-port", type=int, default=GAMESYNC_PORT,
                    help="plaintext h2c listener advertised as the Gamesync endpoint (0 disables)")
    ap.add_argument("--kus-probe", action="store_true",
                    help="send per-slot KeepUserSession wire-type probes (see KUS_PROBES)")
    args = ap.parse_args()
    PUBLIC_HOST = args.public_host or (args.host if args.host != "0.0.0.0" else "127.0.0.1")
    H2Protocol.announce_matches = args.announce_matches
    H2Protocol.kus_probe = args.kus_probe
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
    servers = [server]
    tasks = [s.serve_forever() for s in servers]
    if args.gamesync_port and args.gamesync_port != args.port:
        GAMESYNC_PORT = args.gamesync_port
        tasks.append(serve_sniffing(args.host, args.gamesync_port, tls))
        log(f"Gamesync endpoint listening on {args.host}:{args.gamesync_port} (TLS or h2c, sniffed)")
    await turn_relay.start(args.host, TURN_PORT, log=log, relay_host=PUBLIC_HOST)
    await asyncio.gather(*tasks)


async def serve_sniffing(host, port, tls):
    """Accept both TLS and plaintext h2c on one port by peeking the first byte."""
    loop = asyncio.get_running_loop()
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.bind((host, port))
    listener.listen()
    listener.setblocking(False)

    def peek(conn):
        conn.settimeout(10)
        try:
            return conn.recv(16, socket.MSG_PEEK)
        finally:
            conn.setblocking(False)

    async def handle(conn):
        try:
            first = await loop.run_in_executor(None, peek, conn)
        except OSError as e:
            log(f"Gamesync sniff failed: {e!r}")
            conn.close()
            return
        use_tls = bool(first) and first[0] == 0x16 and tls is not None
        log(f"--- Gamesync connection first bytes {first.hex()} -> {'TLS' if use_tls else 'h2c'} ---")
        await loop.connect_accepted_socket(H2Protocol, conn, ssl=tls if use_tls else None)

    while True:
        conn, _ = await loop.sock_accept(listener)
        asyncio.ensure_future(handle(conn))


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("bye", file=sys.stderr)
