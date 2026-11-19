"""A fake seat server (docs/contracts/SEAT_API.md) for the plugin's own seat client: profiles with PINs, sessions with
tickets, presence tokens and friends, in memory, over plain HTTP on 127.0.0.1. Used by build_plugin.py --test.

    python fake_seat.py PORTFILE      prints nothing; writes the port it listens on into PORTFILE, serves until killed

Only what seatapi.c calls, with the contract's answers and error codes. Not the real server (rust/seat).
"""
import json
import random
import re
import sys
import threading
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

NAME = re.compile(r"^[A-Za-z0-9_-]{3,16}$")
LOCK = threading.Lock()
PROFILES = {"Rafa": "222222", "Luz": "333333"}   # name -> PIN; two players already signed up
SESSIONS = {}    # id -> {key, name}
PRESENCE = {}    # token -> name
FRIENDS = set()  # frozenset({a, b}) accepted
PENDING = set()  # (from, to)
REPORTS = {}     # reporter -> Report (the open one)
REASONS = {"harassment", "hate", "sexual", "threats", "disruption", "other"}
CA = "-----BEGIN CERTIFICATE-----\nMIIBfake\n-----END CERTIFICATE-----\n"


def canon(name):
    for n in PROFILES:
        if n.lower() == name.lower():
            return n
    return None


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def send(self, status, body=None, headers=None):
        data = b"" if body is None else json.dumps(body).encode()
        self.send_response(status)
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        if body is not None:
            self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def error(self, status, code, headers=None):
        self.send(status, {"error": {"code": code, "message": code.lower().replace("_", " ")}}, headers)

    def body(self):
        n = int(self.headers.get("Content-Length") or 0)
        try:
            return json.loads(self.rfile.read(n) or b"{}")
        except ValueError:
            return {}

    def bearer(self):
        a = self.headers.get("Authorization") or ""
        return a[7:] if a.startswith("Bearer ") else ""

    def view(self, sid):
        s = SESSIONS[sid]
        return {"bootId": "boot", "sessionId": sid, "name": s["name"], "state": "seated", "seatsTotal": 24,
                "seatsFree": 23, "queued": 0, "nextPollMs": 20000,
                "ticket": {"token": "mkw1.fake." + sid[:8], "expiresAt": 0, "lobby": "lobby.example:443"}}

    def do_GET(self):
        with LOCK:
            p = self.path
            if p == "/seat/v1/status":
                return self.send(200, {"bootId": "boot", "name": "fake", "lobbyEndpoint": "lobby.example:443",
                                       "lobbyCa": CA, "voicePort": 8907, "seatsTotal": 24, "seated": len(SESSIONS),
                                       "queued": 0, "lobbies": {}})
            if p == "/seat/v1/reports":
                me = PRESENCE.get(self.bearer())
                if not me:
                    return self.error(401, "PRESENCE_NOT_FOUND")
                return self.send(200, {"report": REPORTS.get(me)})
            m = re.match(r"^/seat/v1/sessions/([^/]+)$", p)
            if m:
                s = SESSIONS.get(m.group(1))
                if not s or s["key"] != self.bearer():
                    return self.error(404, "SESSION_NOT_FOUND")
                return self.send(200, self.view(m.group(1)))
            if p.startswith("/seat/v1/friends"):
                me = PRESENCE.get(self.bearer())
                if not me:
                    return self.error(401, "PRESENCE_NOT_FOUND")
                friends = sorted(next(iter(f - {me})) for f in FRIENDS if me in f)
                return self.send(200, {"cursor": "1",
                                       "friends": [{"name": f, "state": "offline"} for f in friends],
                                       "incoming": [{"name": a, "since": 0} for a, b in PENDING if b == me],
                                       "outgoing": [{"name": b, "since": 0} for a, b in PENDING if a == me]})
            return self.error(404, "NOT_FOUND")

    def do_POST(self):
        with LOCK:
            p, b = self.path, self.body()
            if p == "/seat/v1/profiles":
                name = b.get("name", "")
                if not NAME.match(name):
                    return self.error(400, "NAME_INVALID")
                if name.lower() == "admin":
                    return self.error(403, "NAME_RESERVED")
                if canon(name):
                    return self.error(409, "NAME_REGISTERED")
                pin = b.get("pin")   # 1.2.0: a purged profile restored with the PIN its player has
                if pin is not None and not re.match(r"^[0-9]{6}$", str(pin)):
                    return self.error(400, "NAME_INVALID")
                pin = pin or "%06d" % random.randrange(10**6)
                PROFILES[name] = pin
                return self.send(201, {"name": name, "pin": pin})
            if p in ("/seat/v1/sessions", "/seat/v1/presence"):
                name = canon(b.get("name", ""))
                if not name:
                    return self.error(404, "PROFILE_NOT_FOUND")
                if PROFILES[name] != b.get("pin"):
                    return self.error(401, "PIN_WRONG")
                if p == "/seat/v1/presence":
                    token = uuid.uuid4().hex
                    PRESENCE[token] = name
                    return self.send(200, {"token": token, "name": name, "ttlMs": 90000})
                sid = uuid.uuid4().hex
                SESSIONS[sid] = {"key": uuid.uuid4().hex, "name": name}
                return self.send(201, dict(self.view(sid), sessionKey=SESSIONS[sid]["key"]))
            me = PRESENCE.get(self.bearer())
            if p == "/seat/v1/reports":
                if not me:
                    return self.error(401, "PRESENCE_NOT_FOUND")
                if me in REPORTS:
                    return self.error(409, "REPORT_ACTIVE")
                other = canon(b.get("name", ""))
                if not other:
                    return self.error(404, "REPORT_UNKNOWN")
                if other == me:
                    return self.error(400, "REPORT_SELF")
                if b.get("reason") not in REASONS or b.get("kind", "voice") not in ("voice", "camera"):
                    return self.error(400, "REPORT_INVALID")
                REPORTS[me] = {"id": len(REPORTS) + 1, "reporter": me, "reported": other, "reportedCreated": 0,
                               "reason": b["reason"], "details": str(b.get("details", ""))[:500], "created": 1,
                               "status": "open", "closed": None, "outcome": None, "kind": b.get("kind", "voice")}
                return self.send(201, {"report": REPORTS[me]})
            if p == "/seat/v1/friends":
                if not me:
                    return self.error(401, "PRESENCE_NOT_FOUND")
                other = canon(b.get("name", ""))
                if not other:
                    return self.error(404, "FRIEND_UNKNOWN")
                if (other, me) in PENDING:
                    PENDING.discard((other, me))
                    FRIENDS.add(frozenset({me, other}))
                    return self.send(200, {"name": other, "state": "accepted"})
                PENDING.add((me, other))
                return self.send(200, {"name": other, "state": "pending"})
            m = re.match(r"^/seat/v1/friends/([^/]+)/accept$", p)
            if m:
                if not me:
                    return self.error(401, "PRESENCE_NOT_FOUND")
                other = canon(m.group(1))
                if (other, me) not in PENDING:
                    return self.error(404, "REQUEST_NOT_FOUND")
                PENDING.discard((other, me))
                FRIENDS.add(frozenset({me, other}))
                return self.send(204)
            return self.error(404, "NOT_FOUND")

    def do_DELETE(self):
        with LOCK:
            p, b = self.path, self.body()
            m = re.match(r"^/seat/v1/profiles/([^/]+)$", p)
            if m:
                name = canon(m.group(1))
                if not name:
                    return self.error(404, "PROFILE_NOT_FOUND")
                if PROFILES[name] != b.get("pin"):
                    return self.error(403, "PIN_WRONG")
                del PROFILES[name]
                REPORTS.pop(name, None)
                for t in [t for t, n in PRESENCE.items() if n == name]:
                    del PRESENCE[t]
                return self.send(204)
            if p == "/seat/v1/reports":
                me = PRESENCE.get(self.bearer())
                if not me:
                    return self.error(401, "PRESENCE_NOT_FOUND")
                if REPORTS.pop(me, None) is None:
                    return self.error(404, "REPORT_NOT_FOUND")
                return self.send(204)
            m = re.match(r"^/seat/v1/sessions/([^/]+)$", p)
            if m:
                SESSIONS.pop(m.group(1), None)
                return self.send(204)
            if p == "/seat/v1/presence":
                PRESENCE.pop(self.bearer(), None)
                return self.send(204)
            m = re.match(r"^/seat/v1/friends/([^/]+)$", p)
            if m:
                me, other = PRESENCE.get(self.bearer()), canon(m.group(1))
                PENDING.discard((me, other))
                PENDING.discard((other, me))
                FRIENDS.discard(frozenset({me, other}))
                return self.send(204)
            return self.error(404, "NOT_FOUND")


def main():
    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    with open(sys.argv[1], "w") as f:
        f.write(str(server.server_address[1]))
    server.serve_forever()


if __name__ == "__main__":
    main()
