"""Poll a client's game-session handles, member trees and per-slot match
context; print a line whenever anything changes (referee/doc-handle chain).

    python -I tools/re/watch_match_members.py <seconds> <pid>

Chain (docs/NETCODE.md "Referee chain"): mgr = *(**0x70032fd120 + 0x80);
slot = *(mgr+0x12c0) + cur*0x148; slot+0 handle count, slot+8 16-byte
shared_ptr handles (filled by the session-op pollers via FUN_0223b804),
slot+0x138 member object: two member trees at +0x130/+0x150 (root,
begin, end, u32 size at +0x18, capacity 200), active index +0x19c. FUN_0049a5a0 (per frame) copies members
into ctx+0x28 and points ctx+0x8da0/+0x8da8 at the member whose `ussid`
equals handle[0]/handle[1] +0xa0. FUN_01e37e98 (@@RefereeRequest) needs
ctx+0x8da0 and handle[ctx+0x8f60].
"""
import os
import struct
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from watch_session_slots import reader  # noqa: E402


def snapshot(rd):
    q = lambda a: struct.unpack("<Q", rd(a, 8))[0]
    u = lambda a: struct.unpack("<I", rd(a, 4))[0]
    out = {}
    try:
        mgr = q(q(q(0x70032FD120)) + 0x80)
        count, cur, slots = u(mgr + 0x12B8), u(mgr + 0x12C8), q(mgr + 0x12C0)
        out["cur"] = cur
        for i in range(min(count, 2)):
            s = slots + i * 0x148
            n, arr = u(s), q(s + 8)
            hs = []
            for h in range(min(n, 4)):
                p = q(arr + h * 16)
                hs.append(u(p + 0xA0) if p else None)
            obj = q(s + 0x138)
            trees = (u(obj + 0x19C), u(obj + 0x148), u(obj + 0x168)) if obj else None
            out[f"s{i}"] = (hs, trees)
    except OSError:
        out["slots"] = None
    try:
        base = q(0x70033E7EE0)
        for i in range(2):
            ctx = q(base + 0xA0 + i * 8)
            if ctx:
                out[f"ctx{i}"] = (q(ctx + 0x8DA0) != 0, q(ctx + 0x8DA8) != 0, q(ctx + 0x28) != 0, u(ctx + 0x8F60))
    except OSError:
        out["ctx"] = None
    return out


def main():
    seconds, pid = float(sys.argv[1]), int(sys.argv[2])
    rd, last = reader(pid), None
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        snap = snapshot(rd)
        if snap != last:
            print(time.strftime("%H:%M:%S"), snap, flush=True)
            last = snap
        time.sleep(0.25)


if __name__ == "__main__":
    main()
