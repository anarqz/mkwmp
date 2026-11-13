"""Exercise long-lived subscriptions alongside unary RPCs on a real h2 socket."""
import asyncio
import unittest
from unittest.mock import patch

from h2.config import H2Configuration
from h2.connection import H2Connection
from h2.events import DataReceived, StreamEnded, TrailersReceived

import mkwmp_server as capture


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


if __name__ == "__main__":
    unittest.main()
