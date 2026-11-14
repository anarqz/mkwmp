"""Reference verifier for mkwmp seat tickets, format v1 (docs/contracts/SEAT_API.md §3).

    mkw1.<payload>.<sig>

payload: base64url (no padding) of compact JSON claims
         {"v","iss","kid","sid","name","iat","exp"}.
sig:     base64url (no padding) of the Ed25519 signature over b"mkw1.<payload>".

The Rust implementation is rust/ticket. Both must pass resources/tools/ticket/vectors.json:

    python -I resources/tools/ticket/test_verify_ticket.py

Needs the `cryptography` package. Copy this file next to the lobby server,
or import it from here.
"""

import base64
import json

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey

PREFIX = "mkw1"
MAX_LEN = 512


class TicketError(Exception):
    """Any reason a ticket is refused. Key on the `sid` claim, not the raw token."""


def _b64d(s):
    if any(c in s for c in "=+/"):
        raise TicketError("malformed ticket")
    try:
        return base64.urlsafe_b64decode(s + "=" * (-len(s) % 4))
    except ValueError as e:
        raise TicketError("malformed ticket") from e


def parse_public_keys(entries):
    """["kid:base64url-key", ...] (config [ticket] public_keys) -> {kid: key}."""
    keys = {}
    for entry in entries:
        kid, sep, key = entry.partition(":")
        if not sep:
            raise TicketError("public key entry must be kid:key")
        raw = _b64d(key.strip())
        if len(raw) != 32:
            raise TicketError("public key must be 32 bytes")
        keys[kid] = Ed25519PublicKey.from_public_bytes(raw)
    return keys


def verify(ticket, keys, now):
    """Return the claims dict, or raise TicketError. `now` is unix seconds."""
    if len(ticket) > MAX_LEN:
        raise TicketError("ticket too long")
    signing_input, dot, sig_b64 = ticket.rpartition(".")
    prefix, dot2, payload_b64 = signing_input.partition(".")
    if not dot or not dot2:
        raise TicketError("malformed ticket")
    if prefix != PREFIX:
        raise TicketError("unsupported ticket version")
    try:
        claims = json.loads(_b64d(payload_b64))
    except ValueError as e:
        raise TicketError("malformed ticket") from e
    if not isinstance(claims, dict):
        raise TicketError("malformed ticket")
    kid = claims.get("kid")
    key = keys.get(kid) if isinstance(kid, str) else None
    if key is None:
        raise TicketError("ticket signed by an unknown key")
    sig = _b64d(sig_b64)
    if len(sig) != 64:
        raise TicketError("malformed ticket")
    try:
        key.verify(sig, signing_input.encode("ascii"))
    except InvalidSignature as e:
        raise TicketError("bad ticket signature") from e
    if claims.get("v") != 1:
        raise TicketError("unsupported ticket version")
    if not isinstance(claims.get("exp"), int) or claims["exp"] <= now:
        raise TicketError("ticket expired")
    return claims
