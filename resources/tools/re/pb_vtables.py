#!/usr/bin/env python3
"""List protobuf-lite message vtables whose methods live in a code range.

    python -I tools/re/pb_vtables.py scratch/main.bin 0x12a0000 0x12c0000 0x12a26fc

The image has relocations applied, so vtables are arrays of absolute code
pointers in data. A run of >= 6 pointers into [lo, hi) is reported. Pass a
known serializer address (e.g. a recovered _InternalSerialize) to learn its
slot index; every vtable's function at that slot is then printed, giving the
serializer of each message class in declaration order.
"""
import struct
import sys


def main():
    path, lo, hi = sys.argv[1], int(sys.argv[2], 0), int(sys.argv[3], 0)
    known = int(sys.argv[4], 0) if len(sys.argv) > 4 else None
    data = open(path, "rb").read()
    q = struct.unpack_from(f"<{len(data) // 8}Q", data)
    tables, run = [], []
    for i, v in enumerate(q):
        if lo <= v < hi:
            run.append(i)
            continue
        if len(run) >= 6:
            tables.append((run[0] * 8, [q[j] for j in run]))
        run = []
    slot = None
    if known is not None:
        for _, entries in tables:
            if known in entries:
                slot = entries.index(known)
                break
        print(f"known serializer {known:#x} at slot {slot}")
    for addr, entries in sorted(tables, key=lambda t: min(t[1])):
        pick = entries[slot] if slot is not None and slot < len(entries) else None
        print(f"vtable {addr:#x} n={len(entries)}" + (f" serialize={pick:#x}" if pick else ""))


if __name__ == "__main__":
    main()
