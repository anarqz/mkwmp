import asyncio
import socket
import unittest

import turn_relay


def free_udp_ports(n):
    """n currently free, consecutive-enough UDP ports on loopback (best effort)."""
    socks = [socket.socket(socket.AF_INET, socket.SOCK_DGRAM) for _ in range(n)]
    for s in socks:
        s.bind(("127.0.0.1", 0))
    ports = sorted(s.getsockname()[1] for s in socks)
    for s in socks:
        s.close()
    return ports


class RelayPortsTest(unittest.TestCase):
    def test_parse_port_range(self):
        self.assertEqual(turn_relay.parse_port_range("49152:49154"), [49152, 49153, 49154])
        with self.assertRaises(ValueError):
            turn_relay.parse_port_range("5:1")

    def test_allocations_stay_in_range_and_refuse_when_full(self):
        async def run():
            ports = free_udp_ports(1)
            server = await turn_relay.start("127.0.0.1", 0, log=lambda *_: None, relay_ports=ports)
            first = turn_relay.Allocation(server, ("127.0.0.1", 1))
            await first.open()
            self.assertEqual(first.relay[1], ports[0])
            second = turn_relay.Allocation(server, ("127.0.0.1", 2))
            with self.assertRaises(OSError):
                await second.open()
            first.sock.close()
            server.transport.close()
        asyncio.run(run())


if __name__ == "__main__":
    unittest.main()
