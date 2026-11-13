"""Exercise long-lived subscriptions alongside unary RPCs on a real h2 socket."""
import asyncio
import unittest
from unittest.mock import patch

from h2.config import H2Configuration
from h2.connection import H2Connection
from h2.events import DataReceived, StreamEnded, TrailersReceived

import mkwmp_server as capture


def _doc_fields(document):
    """Document.fields (field 2) -> {key: raw Value bytes}."""
    out = {}
    for field, _, raw in capture.iter_fields(capture.bytes_field(document, 2)):
        if field == 1:
            _, start = capture._varint(raw, 0)
            length, start = capture._varint(raw, start)
            entry = raw[start:start + length]
            out[capture.bytes_field(entry, 1).decode()] = capture.bytes_field(entry, 2)
    return out


class ProtocolTest(unittest.IsolatedAsyncioTestCase):
    def test_assignment_sent_to_both_live_trackers(self):
        queue = capture.LocalMatchmaking()
        # Player as captured: 1 placeholder user, 2 attributes, 3 empty map.
        attrs = capture.protobuf_bytes(1, capture.protobuf_bytes(1, "rom_id"))
        captured = capture.protobuf_bytes(1, "tenants/current/users/current") + capture.protobuf_bytes(2, attrs) + b"\x1a\x00"
        first = queue.create("tenants/local/users/one", "Normal24", captured)
        second = queue.create("tenants/local/users/two", "Normal24", captured)
        with patch.object(capture.H2Protocol, "matchmaking", queue), patch.object(
            capture.H2Protocol, "active_trackers", {}
        ), patch.object(capture, "log"):
            peers = [capture.H2Protocol(), capture.H2Protocol()]
            from unittest.mock import Mock
            for peer, ticket in zip(peers, (first, second)):
                peer.conn = Mock()
                peer.conn.local_flow_control_window.return_value = 65535
                peer.transport = Mock()
                peer.tracked_tickets[1] = (ticket.user, ticket.id)
                peer.subscriptions[1] = capture.TRACK_TICKET
                peer.active_trackers[ticket.id] = {(peer, 1)}
            peers[0].publish_match(first)
            session_name = f"tenants/local/gameSessions/{first.lobby}"
            for peer, own in zip(peers, (first, second)):
                frame = peer.conn.send_data.call_args.args[1]
                [(flag, reply)] = capture.decode_grpc_messages(frame)
                self.assertEqual(flag, 0)
                fields = list(capture.iter_fields(reply))
                self.assertEqual([f for f, _, _ in fields], [1, 2, 4, 5, 6, 6, 7])
                self.assertIn(b"\x28\x03", reply)
                self.assertEqual(capture.bytes_field(reply, 1).decode(), f"tenants/local/matchmakingTickets/{own.id}")
                own_player = capture.bytes_field(reply, 4)
                self.assertEqual(capture.bytes_field(own_player, 1).decode(), own.user)
                self.assertEqual(capture.bytes_field(own_player, 2), attrs)
                matched = [raw for f, _, raw in fields if f == 6]
                session = capture.bytes_field(reply, 7)
                self.assertEqual(capture.bytes_field(session, 1).decode(), session_name)
                self.assertEqual(capture.bytes_field(session, 8).decode(), capture.GAMESYNC_HOST)
                self.assertIn(b"\x48" + capture.encode_varint(capture.GAMESYNC_PORT), session)
                for raw, ticket in zip(matched, (first, second)):
                    entry = capture.bytes_field(raw, 6)
                    user_id = ticket.user.rsplit("/", 1)[-1]
                    # MatchedPlayer: Player, userSession resource, session id.
                    self.assertEqual(capture.bytes_field(capture.bytes_field(entry, 1), 1).decode(), ticket.user)
                    self.assertEqual(capture.bytes_field(entry, 2).decode(), f"{session_name}/userSessions/{user_id}")
                    self.assertEqual(capture.bytes_field(entry, 3).decode(), user_id)
                    # Session member field 1 is a plain string, not a message.
                    member = capture.protobuf_bytes(1, ticket.user) + capture.protobuf_bytes(
                        2, f"{session_name}/userSessions/{user_id}")
                    self.assertIn(member, session)
                peer.conn.send_headers.assert_called_once_with(1, [("grpc-status", "0")], end_stream=True)
                self.assertFalse(peer.tracked_tickets)

    def test_gamesync_issue_token_returns_token_message(self):
        subject = "tenants/local/gameSessions/g/userSessions/u"
        request = capture.grpc_frame(capture.protobuf_bytes(1, subject) + capture.protobuf_bytes(2, "x"))
        reply = capture.gamesync_token_response(request, None)
        token = capture.bytes_field(reply, 1)
        self.assertEqual(capture.bytes_field(token, 1).decode(), subject)
        self.assertTrue(capture.bytes_field(token, 2))
        self.assertTrue(capture.bytes_field(token, 3))
        self.assertEqual(capture.bytes_field(token, 4), b"\x08" + capture.encode_varint(3600))
        self.assertEqual(capture.bearer_token("bearer abc"), "abc")
        self.assertEqual(capture.bearer_token("Bearer abc"), "abc")

    def test_keep_user_session_answers_captured_subscription(self):
        # Client frame captured 2026-11-13 (class 8 session message).
        frame = bytes.fromhex(
            "0a147573657253657373696f6e732f63757272656e741a540a520a1e7573657253657373696f6e732f"
            "63757272656e742f746172676574732f3112300a2e646f63732f5f5f75732f30303030303030302d30"
            "3030302d343030302d383030302d303030303030303030303032")
        session, targets = capture.parse_keep_user_session(frame)
        self.assertEqual(session, "userSessions/current")
        doc_name = "docs/__us/00000000-0000-4000-8000-000000000002"
        self.assertEqual(targets, [("userSessions/current/targets/1", doc_name)])
        replies = capture.keep_user_session_replies(session, targets, now=100)
        expected = [(1, doc_name, {"uid", "ussid", "ucsid", "upcsid", "pgn"}),
                    (1, "docs/__gs/f", {"gsid", "addr", "p", "mcn", "maxu"})]
        self.assertEqual(len(replies), len(expected) + 2)
        # Then Op{1 "1", 2 kind 2} (target in sync) and Op{1 "1", 2 kind 1} (op done).
        self.assertEqual(capture.bytes_field(replies[-2], 3), b"\x0a\x011\x10\x02")
        self.assertEqual(capture.bytes_field(replies[-1], 3), b"\x0a\x011\x10\x01")
        for message, (kind, name, keys) in zip(replies, expected):
            self.assertEqual(capture.bytes_field(message, 1).decode(), session)   # field 1 = string
            change = capture.bytes_field(message, 2)
            self.assertEqual(capture.bytes_field(change, 1).decode(), "1")   # numeric target id
            self.assertIn(b"\x10" + capture.encode_varint(kind), change)
            document = capture.bytes_field(change, 3)
            self.assertEqual(capture.bytes_field(document, 1).decode(), name)
            self.assertTrue(keys <= set(_doc_fields(document)))

    def test_user_session_collection_lists_every_member(self):
        # Captured after the mesh join: Target{3: {1 "docs/__us"}} (collection).
        frame = bytes.fromhex(
            "0a147573657253657373696f6e732f63757272656e741a2f0a2d0a1e7573657253657373696f6e732f"
            "63757272656e742f746172676574732f351a0b0a09646f63732f5f5f7573")
        session, targets = capture.parse_keep_user_session(frame)
        self.assertEqual(targets, [("userSessions/current/targets/5", "docs/__us/")])
        ids = ["00000000-0000-4000-8000-000000000001", "00000000-0000-4000-8000-000000000002"]
        replies = capture.keep_user_session_replies(session, targets, now=100, members=ids)
        names = [capture.bytes_field(capture.bytes_field(capture.bytes_field(m, 2), 3), 1).decode()
                 for m in replies[:2]]
        self.assertEqual(names, [f"docs/__us/{i}" for i in ids])
        self.assertEqual(len(replies), 4)   # 2 documents + sync op + done op

    def test_write_documents_returns_upcsid_in_first_result(self):
        pus = "docs/__pgn/All/__pus/00000000-0000-4000-8000-000000000002"
        response = capture.write_documents_response([(pus, 0), (pus, 4)], capture.user_slot, now=100)
        results = [raw for field, _, raw in capture.iter_fields(response) if field == 1]
        self.assertEqual(len(results), 2)
        first = capture.bytes_field(response, 1)
        values = capture.bytes_field(first, 2)
        self.assertEqual(values, b"\x0a\x02\x18\x02" * 4)   # 4 x Value{int 2}

    def test_user_session_descriptor_is_shared_within_a_lobby(self):
        lobby = "c3dacf52-23fe-43bd-90d9-7d63b2ec6d9e"
        a, b = (_doc_fields(capture.user_session_document(f"docs/__us/{u}", u, now=1, lobby=lobby))
                for u in ("0001", "0002"))
        # Shared group; per-player identity.
        self.assertEqual(a["pgn"], b["pgn"])
        self.assertIn(lobby.encode(), a["pgn"])
        self.assertNotEqual(a["uid"], b["uid"])
        # Fixed data is one shared document for the lobby.
        fixed = capture.game_session_fixed_document(lobby, now=1)
        self.assertEqual(capture.bytes_field(fixed, 1).decode(), "docs/__gs/f")
        self.assertIn(lobby.encode(), _doc_fields(fixed)["gsid"])

    async def test_keep_user_session_stays_open_and_logs_frames(self):
        logged = []
        with patch.object(capture, "log", logged.append):
            loop = asyncio.get_running_loop()
            server = await loop.create_server(capture.H2Protocol, "127.0.0.1", 0)
            reader, writer = await asyncio.open_connection("127.0.0.1", server.sockets[0].getsockname()[1])
            conn = H2Connection(H2Configuration(client_side=True, header_encoding="utf-8"))
            conn.initiate_connection()
            path = next(iter(capture.BIDI_STREAMS))
            conn.send_headers(1, [(":method", "POST"), (":scheme", "http"), (":authority", "local.test"),
                                  (":path", path), ("content-type", "application/grpc"), ("te", "trailers")])
            writer.write(conn.data_to_send())
            try:
                events = []
                while not any(getattr(e, "stream_id", None) == 1 and type(e).__name__ == "ResponseReceived"
                              for e in events):
                    data = await asyncio.wait_for(reader.read(65536), 3)
                    events.extend(conn.receive_data(data))
                    writer.write(conn.data_to_send())
                conn.send_data(1, capture.grpc_frame(b"\x08\x05"))
                writer.write(conn.data_to_send())
                for _ in range(50):
                    if any("frame (2 bytes): 0805" in line for line in logged):
                        break
                    await asyncio.sleep(0.02)
                self.assertTrue(any("frame (2 bytes): 0805" in line for line in logged))
                self.assertFalse(any(isinstance(e, StreamEnded) for e in events))
            finally:
                writer.close()
                await writer.wait_closed()
                server.close()
                await server.wait_closed()

    def test_captured_race_request_returns_a_named_ticket(self):
        request = bytes.fromhex(
            "000000009f0a0f74656e616e74732f63757272656e74128b01122b74656e616e74732f"
            "63757272656e742f6d617463686d616b696e67436f6e666967732f4e6f726d616c3234"
            "225c0a1d74656e616e74732f63757272656e742f75736572732f63757272656e741239"
            "0a150a0c6261746368696e675f6b657912053a036376310a0f0a09636f757273655f69"
            "64120218000a0f0a06726f6d5f696412053a036376311a00")
        queue = capture.LocalMatchmaking()
        user = "tenants/t-e96531c6-lp1/users/00000000-0000-4000-8000-000000000001"
        reply = capture.create_ticket_response(queue, user, request)
        ticket, = queue.tickets.values()
        self.assertEqual(ticket.mode, "Normal24")
        self.assertEqual(capture.bytes_field(reply, 1).decode(),
                         f"tenants/t-e96531c6-lp1/matchmakingTickets/{ticket.id}")

    def test_local_player_slots_have_distinct_user_resources_and_claims(self):
        import base64
        import json

        tenant = "tenants/t-e96531c6-lp1"
        first = b"eyJhbGciOiJub25lIn0.eyJzdWIiOiJta3dtcC1sb2NhbCIsImlzcyI6Im1rd21wIn0."
        for slot, assertion in ((1, first), (2, capture.SECOND_LOCAL_TOKEN)):
            request = capture.protobuf_bytes(1, tenant) + capture.protobuf_bytes(
                3, capture.protobuf_bytes(1, assertion))
            reply = capture.auth_response(capture.grpc_frame(request))
            _, start = capture._varint(reply, 1)
            user_len, user_start = capture._varint(reply, start + 1)
            user_end = user_start + user_len
            user = reply[user_start:user_end].decode()
            self.assertEqual(user, f"{tenant}/users/00000000-0000-4000-8000-{slot:012d}")
            token_len, token_start = capture._varint(reply, user_end + 1)
            payload = reply[token_start:token_start + token_len].split(b".")[1]
            claims = json.loads(base64.urlsafe_b64decode(payload + b"=" * (-len(payload) % 4)))
            self.assertEqual(claims["sub"], user)

    async def test_subscription_stays_open_while_unary_rpc_completes(self):
        with patch.object(capture, "log"):
            loop = asyncio.get_running_loop()
            server = await loop.create_server(capture.H2Protocol, "127.0.0.1", 0)
            reader, writer = await asyncio.open_connection("127.0.0.1", server.sockets[0].getsockname()[1])
            conn = H2Connection(H2Configuration(client_side=True, header_encoding="utf-8"))
            conn.initiate_connection()
            def request(sid, path, payload=b""):
                conn.send_headers(sid, [(":method", "POST"), (":scheme", "http"),
                    (":authority", "local.test"), (":path", path),
                    ("content-type", "application/grpc"), ("te", "trailers")])
                conn.send_data(sid, capture.grpc_frame(payload), end_stream=True)
                writer.write(conn.data_to_send())
            async def events_until(predicate):
                events = []
                while not predicate(events):
                    data = await asyncio.wait_for(reader.read(65536), 3)
                    self.assertTrue(data, "server closed the connection")
                    events.extend(conn.receive_data(data))
                    writer.write(conn.data_to_send())
                return events
            try:
                request(1, capture.PRESENCE_SUBSCRIBE)
                first = await events_until(lambda evs: any(isinstance(e, DataReceived) for e in evs))
                self.assertEqual(b"".join(e.data for e in first if isinstance(e, DataReceived)), bytes(5))
                self.assertFalse(any(isinstance(e, (StreamEnded, TrailersReceived)) for e in first))
                request(3, "/local.test.Service/Unary")
                second = await events_until(lambda evs: any(isinstance(e, StreamEnded) and e.stream_id == 3 for e in evs))
                self.assertFalse(any(isinstance(e, StreamEnded) and e.stream_id == 1 for e in second))
                trailers = next(e for e in second if isinstance(e, TrailersReceived))
                self.assertEqual(dict(trailers.headers)["grpc-status"], "0")
                names = [f"tenants/current/documents/{mode}" for mode in ("normal", "battle", "survival")]
                batch = b"".join(capture.protobuf_bytes(2, name) for name in names)
                request(5, capture.BULK_GET, batch)
                reply = await events_until(lambda evs: any(isinstance(e, StreamEnded) and e.stream_id == 5 for e in evs))
                data = b"".join(e.data for e in reply if isinstance(e, DataReceived) and e.stream_id == 5)
                fields = bytes.fromhex("12150a130a06526174696e671209290000000000709740")
                self.assertEqual(capture.decode_grpc_messages(data), [
                    (0, bytes([10, len(name) + 2 + len(fields), 10, len(name)]) + name.encode() + fields)
                    for name in names])
                self.assertEqual(dict(next(e for e in reply if isinstance(e, TrailersReceived)).headers)["grpc-status"], "0")
                conn.send_headers(7, [(":method", "POST"), (":scheme", "http"),
                    (":authority", "local.test"), (":path", capture.PRESENCE_KEEPALIVE),
                    ("content-type", "application/grpc"), ("te", "trailers")])
                ping = capture.grpc_frame(b"\x08\x01")
                conn.send_data(7, ping[:3])
                conn.send_data(7, ping[3:] + ping)
                writer.write(conn.data_to_send())
                keepalive = await events_until(lambda evs: sum(len(e.data) for e in evs
                    if isinstance(e, DataReceived) and e.stream_id == 7) == 10)
                self.assertEqual(b"".join(e.data for e in keepalive if isinstance(e, DataReceived)), bytes(10))
                self.assertFalse(any(isinstance(e, StreamEnded) for e in keepalive))
                conn.end_stream(7)
                writer.write(conn.data_to_send())
                end = await events_until(lambda evs: any(isinstance(e, StreamEnded) and e.stream_id == 7 for e in evs))
                self.assertEqual(dict(next(e for e in end if isinstance(e, TrailersReceived)).headers)["grpc-status"], "0")
                user = "tenants/local/users/test"
                with patch.object(capture.H2Protocol, "issued_tokens", {"test-token": user}), patch.object(
                    capture.H2Protocol, "matchmaking", capture.LocalMatchmaking()
                ):
                    ticket = capture.H2Protocol.matchmaking.create(user, "Normal24")
                    conn.send_headers(9, [(":method", "POST"), (":scheme", "http"),
                        (":authority", "local.test"), (":path", capture.TRACK_TICKET),
                        ("content-type", "application/grpc"), ("authorization", "Bearer test-token")])
                    name = f"tenants/current/matchmakingTickets/{ticket.id}"
                    conn.send_data(9, capture.grpc_frame(capture.protobuf_bytes(1, name)), end_stream=True)
                    request(11, "/local.test.Service/Unary")
                    tracking = await events_until(lambda evs: any(isinstance(e, StreamEnded) and e.stream_id == 11 for e in evs))
                    self.assertTrue(any(getattr(e, "stream_id", None) == 9 for e in tracking))
                    self.assertFalse(any(isinstance(e, (StreamEnded, DataReceived)) and e.stream_id == 9 for e in tracking))
                    other = capture.H2Protocol.matchmaking.create("tenants/local/users/other", "Normal24")
                    self.assertEqual(ticket.lobby, other.lobby)
                    conn.reset_stream(9)
                    request(13, "/local.test.Service/Unary")
                    await events_until(lambda evs: any(isinstance(e, StreamEnded) and e.stream_id == 13 for e in evs))
                    self.assertNotIn(ticket.id, capture.H2Protocol.matchmaking.tickets)
                    self.assertIsNone(other.lobby)
                conn.reset_stream(1)
                writer.write(conn.data_to_send())
                await writer.drain()
            finally:
                writer.close()
                await writer.wait_closed()
                server.close()
                await server.wait_closed()

class TokenSubjectTest(unittest.TestCase):
    def test_a_restarted_server_reads_the_player_from_its_own_token(self):
        body = capture.grpc_frame(capture.protobuf_bytes(1, "tenants/t-x"))
        token_info = capture.bytes_field(capture.auth_response(body), 2)
        user = capture.bytes_field(token_info, 1).decode()
        token = capture.bytes_field(token_info, 2).decode()
        self.assertEqual(capture.token_subject(token), user)
        self.assertIsNone(capture.token_subject("mkw1.abc.def"))
        self.assertIsNone(capture.token_subject("not-a-token"))


if __name__ == "__main__":
    unittest.main()
