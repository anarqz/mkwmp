"""Minimal TURN relay (RFC 5766 subset) for Pia's nn::pia::turn::TurnProtocol.

Pia's mesh attach (AttachMeshJob, FUN_01630b64) waits until its TURN client
reports ready, so the ICE server set (GameSessionService/AllocateIceServerSet)
must point at a TURN server that grants allocations. Everything is loopback:
allocations get a real UDP relay socket on 127.0.0.1.

Handles: Binding, Allocate (long-term credentials: 401 challenge, then
MESSAGE-INTEGRITY), Refresh, CreatePermission, ChannelBind, Send indication,
ChannelData; relays peer traffic back as ChannelData or Data indications.
"""

import asyncio
import hashlib
import hmac
import os
import socket
import struct
import zlib

MAGIC = 0x2112A442
REALM = b"mkwmp"
USERNAME = "mkwmp"
PASSWORD = "mkwmp"

BINDING, ALLOCATE, REFRESH, SEND, DATA, CREATE_PERMISSION, CHANNEL_BIND = 1, 3, 4, 6, 7, 8, 9
REQUEST, INDICATION, SUCCESS, ERROR = 0x000, 0x010, 0x100, 0x110

A_USERNAME, A_MI, A_ERROR, A_CHANNEL, A_LIFETIME = 0x0006, 0x0008, 0x0009, 0x000C, 0x000D
A_PEER, A_DATA, A_REALM, A_NONCE, A_RELAYED = 0x0012, 0x0013, 0x0014, 0x0015, 0x0016
A_TRANSPORT, A_MAPPED, A_SOFTWARE, A_FINGERPRINT = 0x0019, 0x0020, 0x8022, 0x8028


def msg_type(method, cls):
    return ((method & 0xF80) << 2) | ((method & 0x70) << 1) | (method & 0xF) | \
           ((cls & 0x100) << 0) | ((cls & 0x010) << 0)


def split_type(t):
    method = (t & 0xF) | ((t >> 1) & 0x70) | ((t >> 2) & 0xF80)
    cls = t & 0x110
    return method, cls


def parse(data):
    if len(data) < 20:
        return None
    t, length, magic = struct.unpack_from("!HHI", data)
    if magic != MAGIC or t & 0xC000 or 20 + length > len(data):
        return None
    attrs, pos = [], 20
    while pos + 4 <= 20 + length:
        at, al = struct.unpack_from("!HH", data, pos)
        attrs.append((at, data[pos + 4:pos + 4 + al], pos))
        pos += 4 + al + (-al % 4)
    return t, data[8:20], attrs


def xor_addr(host, port, tid):
    ip = struct.unpack("!I", socket.inet_aton(host))[0]
    return struct.pack("!BBHI", 0, 1, port ^ (MAGIC >> 16), ip ^ MAGIC)


def unxor_addr(value):
    _, family, xport, xip = struct.unpack_from("!BBHI", value)
    return socket.inet_ntoa(struct.pack("!I", xip ^ MAGIC)), xport ^ (MAGIC >> 16)


def attr(at, value):
    return struct.pack("!HH", at, len(value)) + value + b"\0" * (-len(value) % 4)


def build(t, tid, attrs, key=None):
    body = b"".join(attr(a, v) for a, v in attrs)
    if key is not None:
        head = struct.pack("!HHI", t, len(body) + 24, MAGIC) + tid
        mac = hmac.new(key, head + body, hashlib.sha1).digest()
        body += attr(A_MI, mac)
    head = struct.pack("!HHI", t, len(body) + 8, MAGIC) + tid
    crc = (zlib.crc32(head + body) ^ 0x5354554E) & 0xFFFFFFFF
    return head + body + attr(A_FINGERPRINT, struct.pack("!I", crc))


def long_term_key(username=USERNAME, realm=REALM, password=PASSWORD):
    return hashlib.md5(f"{username}:{realm.decode()}:{password}".encode()).digest()


class Allocation:
    def __init__(self, server, client):
        self.server, self.client = server, client
        self.channels = {}      # channel -> peer
        self.peer_channel = {}  # peer -> channel
        self.sock = None
        self.relay = None

    async def open(self):
        loop = asyncio.get_running_loop()
        alloc = self

        class Relay(asyncio.DatagramProtocol):
            def datagram_received(self, data, peer):
                alloc.from_peer(data, peer)

        self.sock, _ = await loop.create_datagram_endpoint(Relay, local_addr=("127.0.0.1", 0))
        self.relay = self.sock.get_extra_info("sockname")

    def from_peer(self, data, peer):
        channel = self.peer_channel.get(peer)
        if channel is not None:
            frame = struct.pack("!HH", channel, len(data)) + data + b"\0" * (-len(data) % 4)
        else:
            frame = build(msg_type(DATA, INDICATION), os.urandom(12),
                          [(A_PEER, xor_addr(*peer, None)), (A_DATA, data)])
        self.server.transport.sendto(frame, self.client)

    def to_peer(self, data, peer):
        if self.sock is not None:
            self.sock.sendto(data, peer)


class TurnServer(asyncio.DatagramProtocol):
    def __init__(self, log=print):
        self.log = log
        self.transport = None
        self.allocations = {}
        self.nonce = os.urandom(8).hex().encode()

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data, client):
        if len(data) >= 4 and 0x4000 <= struct.unpack_from("!H", data)[0] <= 0x7FFF:
            channel, length = struct.unpack_from("!HH", data)
            alloc = self.allocations.get(client)
            if alloc and channel in alloc.channels:
                alloc.to_peer(data[4:4 + length], alloc.channels[channel])
            return
        msg = parse(data)
        if msg is None:
            return
        t, tid, attrs = msg
        method, cls = split_type(t)
        found = {a: v for a, v, _ in attrs}
        if cls == INDICATION and method == SEND:
            alloc = self.allocations.get(client)
            if alloc and A_PEER in found and A_DATA in found:
                alloc.to_peer(found[A_DATA], unxor_addr(found[A_PEER]))
            return
        if cls != REQUEST:
            return
        if method == BINDING:
            self.reply(client, BINDING, tid, [(A_MAPPED, xor_addr(*client, tid))], None)
            return
        if A_MI not in found:
            # Long-term credentials: challenge with realm + nonce.
            self.transport.sendto(build(msg_type(method, ERROR), tid, [
                (A_ERROR, struct.pack("!HBB", 0, 4, 1) + b"Unauthorized"),
                (A_REALM, REALM), (A_NONCE, self.nonce)]), client)
            return
        username = found.get(A_USERNAME, USERNAME.encode()).decode(errors="replace")
        key = long_term_key(username, found.get(A_REALM, REALM))
        if method == ALLOCATE:
            asyncio.ensure_future(self.allocate(client, tid, key))
        elif method == REFRESH:
            lifetime = struct.unpack("!I", found[A_LIFETIME])[0] if A_LIFETIME in found else 600
            if lifetime == 0:
                alloc = self.allocations.pop(client, None)
                if alloc and alloc.sock:
                    alloc.sock.close()
            self.reply(client, REFRESH, tid, [(A_LIFETIME, struct.pack("!I", lifetime))], key)
        elif method == CREATE_PERMISSION:
            self.reply(client, CREATE_PERMISSION, tid, [], key)
        elif method == CHANNEL_BIND:
            alloc = self.allocations.get(client)
            if alloc and A_CHANNEL in found and A_PEER in found:
                channel = struct.unpack_from("!H", found[A_CHANNEL])[0]
                peer = unxor_addr(found[A_PEER])
                alloc.channels[channel] = peer
                alloc.peer_channel[peer] = channel
                self.log(f"TURN {client[1]}: channel {channel:#x} -> {peer}")
            self.reply(client, CHANNEL_BIND, tid, [], key)

    async def allocate(self, client, tid, key):
        alloc = self.allocations.get(client)
        if alloc is None:
            alloc = Allocation(self, client)
            await alloc.open()
            self.allocations[client] = alloc
            self.log(f"TURN allocation for {client} relayed at {alloc.relay}")
        self.reply(client, ALLOCATE, tid, [
            (A_RELAYED, xor_addr(*alloc.relay, tid)),
            (A_MAPPED, xor_addr(*client, tid)),
            (A_LIFETIME, struct.pack("!I", 600)),
            (A_SOFTWARE, b"mkwmp-turn")], key)

    def reply(self, client, method, tid, attrs, key):
        self.transport.sendto(build(msg_type(method, SUCCESS), tid, attrs, key), client)


async def start(host="127.0.0.1", port=3478, log=print):
    loop = asyncio.get_running_loop()
    transport, server = await loop.create_datagram_endpoint(lambda: TurnServer(log), local_addr=(host, port))
    log(f"TURN relay listening on {host}:{port} (udp, user {USERNAME!r})")
    return server
