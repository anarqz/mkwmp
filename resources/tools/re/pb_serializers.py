#!/usr/bin/env python3
"""Recover protobuf-lite message layouts from generated _InternalSerialize code.

    python -I tools/re/pb_serializers.py scratch/main.bin 0x12a0000 0x12c0000

Splits [start, end) into functions at `pacibsp`, then reports each function
that writes protobuf tag bytes (`mov wN, #tag` ... `strb wN, [...]`). For each
tag it prints the field number, wire type, and the nearest following `bl`
(the nested message serializer or string/varint helper), which is enough to
reconstruct field numbers and types. Messages with fields >= 16 use two-byte
tags written as `mov wN, #0xXXYY; strh`, which are decoded too.
"""
import struct
import sys

from capstone import CS_ARCH_ARM64, CS_MODE_ARM, Cs

WIRE = {0: "varint", 1: "fixed64", 2: "len", 5: "fixed32"}
# Helpers with fixed meaning in this binary (see docs/NETCODE.md).
KNOWN = {
    0x282b970: "memcpy(string)",
    0x11feb20: "string-size",
    0x11f7ab0: "varint-size",
    0x11f77e0: "ensure-space",
    0x11ddd08: "Timestamp/Duration",
    0xf1b700: "Attributes",
    0x11f7900: "write-unknown",
}


def decode_tag(value, two_byte):
    if not two_byte:
        return value >> 3, value & 7
    lo, hi = value & 0xFF, value >> 8
    if not lo & 0x80:
        return None
    tag = (lo & 0x7F) | (hi << 7)
    return tag >> 3, tag & 7


def main():
    path, start, end = sys.argv[1], int(sys.argv[2], 0), int(sys.argv[3], 0)
    data = open(path, "rb").read()
    words = struct.unpack_from(f"<{(end - start) // 4}I", data, start)
    starts = [start + i * 4 for i, w in enumerate(words) if w == 0xD503237F]
    md = Cs(CS_ARCH_ARM64, CS_MODE_ARM)
    md.skipdata = True
    for n, fn in enumerate(starts):
        stop = starts[n + 1] if n + 1 < len(starts) else end
        ins = list(md.disasm(data[fn:stop], fn))
        fields = []
        for k, i in enumerate(ins):
            if i.mnemonic != "mov" or "#" not in i.op_str or not i.op_str.startswith("w"):
                continue
            reg, imm = i.op_str.split(", #")
            try:
                value = int(imm, 0)
            except ValueError:
                continue
            for j in ins[k + 1:k + 6]:
                if j.mnemonic in ("strb", "strh") and j.op_str.startswith(reg + ","):
                    if j.mnemonic == "strb" and 0 < value < 0x80 and (value & 7) in WIRE and value >> 3:
                        dec = decode_tag(value, False)
                    elif j.mnemonic == "strh" and 0x80 <= value <= 0xFFFF:
                        dec = decode_tag(value, True)
                    else:
                        dec = None
                    if dec and dec[0]:
                        call = next((c for c in ins[k + 1:k + 40] if c.mnemonic == "bl"), None)
                        target = int(call.op_str.lstrip("#"), 16) if call else 0
                        fields.append((dec[0], WIRE.get(dec[1], dec[1]), target))
                    break
        if not fields:
            continue
        seen, uniq = set(), []
        for f in fields:
            if f[0] not in seen:
                seen.add(f[0])
                uniq.append(f)
        desc = ", ".join(f"{f}:{w}" + (f"->{KNOWN.get(t, hex(t))}" if w == "len" and t else "")
                         for f, w, t in sorted(uniq))
        print(f"{fn:08x}  {desc}")


if __name__ == "__main__":
    main()
