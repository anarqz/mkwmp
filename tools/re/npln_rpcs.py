#!/usr/bin/env python3
"""Inventory NPLN gRPC methods: path, stub constructor, slot and call type.

    python -I tools/re/npln_rpcs.py scratch/main.bin > docs/contracts/rpcs.tsv

Each generated stub constructor loads a method path (ADRP+ADD into x1) and
stores {name, type} into the stub. The type is the next `str wzr|wN, [x19,#k]`
after the path (0 unary, 1 client stream, 2 server stream, 3 bidi).
"""
import re
import struct
import sys

from capstone import CS_ARCH_ARM64, CS_MODE_ARM, Cs

TYPES = {0: "unary", 1: "client_stream", 2: "server_stream", 3: "bidi"}


def main():
    data = open(sys.argv[1], "rb").read()
    paths = {m.start(): m.group(1).decode() for m in
             re.finditer(rb"(/nn\.npln\.[a-z]+\.v1\.[A-Za-z]+/[A-Za-z]+)\x00", data)}
    code_end = 0x2820000
    words = struct.unpack_from(f"<{code_end // 4}I", data)
    md = Cs(CS_ARCH_ARM64, CS_MODE_ARM)
    rows = []
    for i, w in enumerate(words):
        if (w & 0x9F000000) != 0x90000000:
            continue
        pc = i * 4
        imm = (((w >> 5) & 0x7FFFF) << 2) | ((w >> 29) & 3)
        if imm & (1 << 20):
            imm -= 1 << 21
        page = (pc & ~0xFFF) + imm * 4096
        op = words[i + 1]
        if (op & 0xFF800000) != 0x91000000 or ((op >> 5) & 31) != (w & 31):
            continue
        target = page + ((op >> 10) & 0xFFF)
        if target not in paths:
            continue
        # Look ahead for the method-type store that follows the name store.
        kind, slot = None, None
        regs = {}
        for ins in md.disasm(data[pc + 8:pc + 8 + 4 * 14], pc + 8):
            if ins.mnemonic == "mov" and ins.op_str.startswith("w") and "#" in ins.op_str:
                r, v = ins.op_str.split(", #")
                regs[r] = int(v, 0)
            if ins.mnemonic in ("str", "stp") and "[x19, #" in ins.op_str:
                off = int(ins.op_str.rsplit("#", 1)[1].rstrip("]"), 0)
                first = ins.op_str.split(",")[0]
                if ins.mnemonic == "stp" and "x1" in ins.op_str.split("[")[0]:
                    slot = off + 8
                elif ins.mnemonic == "str" and first == "x1":
                    slot = off
                elif ins.mnemonic == "str" and first.startswith("w"):
                    kind = 0 if first == "wzr" else regs.get(first)
                    slot = slot if slot is not None else off - 8
                    break
        path = paths[target]
        service, method = path.rsplit("/", 1)
        rows.append((service.lstrip("/"), method, TYPES.get(kind, "?"), f"{pc:#x}",
                     f"{slot:#x}" if slot is not None else "?"))
    seen = set()
    print("service\tmethod\ttype\tstub_ref\tstub_slot")
    for r in sorted(rows):
        if r[:2] in seen:
            continue
        seen.add(r[:2])
        print("\t".join(r))


if __name__ == "__main__":
    main()
