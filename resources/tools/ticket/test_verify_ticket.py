"""Checks verify_ticket.py against the vectors the Rust crate produced.

    python -I resources/tools/ticket/test_verify_ticket.py

Regenerate vectors.json (only if the format changes):

    cargo test --manifest-path rust/Cargo.toml -p ticket print_vectors -- --ignored --nocapture
"""

import json
import pathlib
import sys
import unittest

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import verify_ticket as vt  # noqa: E402

V = json.loads((HERE / "vectors.json").read_text())
KEYS = vt.parse_public_keys(V["publicKeys"])


class Vectors(unittest.TestCase):
    def test_valid(self):
        for case in V["valid"]:
            self.assertEqual(vt.verify(case["ticket"], KEYS, V["now"]), case["claims"])

    def test_invalid(self):
        for case in V["invalid"]:
            with self.subTest(why=case["why"]):
                with self.assertRaises(vt.TicketError):
                    vt.verify(case["ticket"], KEYS, V["now"])

    def test_expired(self):
        t = V["valid"][0]["ticket"]
        with self.assertRaisesRegex(vt.TicketError, "expired"):
            vt.verify(t, KEYS, V["expiredAt"])

    def test_too_long(self):
        with self.assertRaisesRegex(vt.TicketError, "too long"):
            vt.verify("a" * 513, KEYS, V["now"])


if __name__ == "__main__":
    unittest.main()
