"""Poll each running test client's game-session slot table and connection
controller; print a line whenever anything changes.

    python -I tools/re/watch_session_slots.py <seconds> <pid> [<pid> ...]

Slot table (see docs/NETCODE.md): mgr = *(**0x70032fd120 + 0x80);
count +0x12b8, current +0x12c8, slots +0x12c0 (0x148 each); slot +0 handle
count, +8 array of 16-byte shared_ptr handles (FUN_01a9637c), +0x140 the
Gamesync document client. Controller: *(**0x70032f29f8 + 0xd8).
"""
import ctypes as C
import struct
import sys
import time

k = C.WinDLL("kernel32", use_last_error=True)
k.OpenProcess.restype = C.c_void_p
k.OpenProcess.argtypes = [C.c_ulong, C.c_int, C.c_ulong]
k.ReadProcessMemory.argtypes = [C.c_void_p, C.c_void_p, C.c_void_p, C.c_size_t, C.POINTER(C.c_size_t)]


def reader(pid):
    h = k.OpenProcess(0x10, False, pid)

    def rd(a, n):
        b = C.create_string_buffer(n)
        d = C.c_size_t()
        if not a or not k.ReadProcessMemory(h, C.c_void_p(a), b, n, C.byref(d)):
            raise OSError(hex(a))
        return b.raw
    return rd


def snapshot(rd):
    q = lambda a: struct.unpack("<Q", rd(a, 8))[0]
    out = {}
    try:
        mgr = q(q(q(0x70032FD120)) + 0x80)
        count, = struct.unpack("<I", rd(mgr + 0x12B8, 4))
        cur, = struct.unpack("<I", rd(mgr + 0x12C8, 4))
        slots = q(mgr + 0x12C0)
        out["slots"] = count
        out["cur"] = cur
        for i in range(min(count, 4)):
            s = slots + i * 0x148
            n, = struct.unpack("<I", rd(s, 4))
            out[f"s{i}"] = (n, q(s + 0x140) != 0)
    except OSError:
        out["slots"] = None
    try:
        # Per-slot online race context (FUN_0045b24c): *(*0x33e7ee0 + 0xa0 + slot*8);
        # +0x8da0/+0x8da8 per-slot document-path handles, +0x28 player map.
        base = q(0x70033E7EE0)
        for i in range(2):
            ctx = q(base + 0xA0 + i * 8)
            if ctx:
                out[f"ctx{i}"] = (q(ctx + 0x8DA0) != 0, q(ctx + 0x8DA8) != 0, q(ctx + 0x28) != 0)
    except OSError:
        out["ctx"] = None
    try:
        ctl = q(q(q(0x70032F29F8)) + 0xD8)
        area = rd(ctl, 0x4B6)
        out["ctl"] = struct.unpack_from("<II", area, 8) + struct.unpack_from("<III", area, 0x468)
    except OSError:
        out["ctl"] = None
    return out


def main():
    seconds = float(sys.argv[1])
    pids = [int(p) for p in sys.argv[2:]]
    readers = {p: reader(p) for p in pids}
    last = {}
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        for p, rd in readers.items():
            snap = snapshot(rd)
            if snap != last.get(p):
                print(time.strftime("%H:%M:%S"), p, snap, flush=True)
                last[p] = snap
        time.sleep(0.5)


if __name__ == "__main__":
    main()
