"""Find serialized FileDescriptorProto blobs in a raw image and write them as .proto + a FileDescriptorSet.

usage: python -I protodesc.py <image.bin> <outdir>
A serialized FileDescriptorProto starts with field 1 (name): 0x0a <varint len> "<path>.proto".
"""
import os
import re
import sys

from google.protobuf import descriptor_pb2

img = open(sys.argv[1], 'rb').read()
out = sys.argv[2]
os.makedirs(out, exist_ok=True)


def varint(b, i):
    r = s = 0
    while True:
        x = b[i]
        r |= (x & 0x7f) << s
        i += 1
        if not x & 0x80:
            return r, i
        s += 7


def parse_len(b, start):
    """Walk protobuf fields from start until an invalid tag; return the end offset."""
    i, n = start, len(b)
    last_field = 0
    while i < n:
        try:
            tag, j = varint(b, i)
        except IndexError:
            break
        field, wt = tag >> 3, tag & 7
        # FileDescriptorProto fields are 1..14 (+ edition 14 / 50?); stop at anything outside or at a zero byte
        if field == 0 or field > 14 or wt not in (0, 2):
            break
        if wt == 0:
            _, j = varint(b, j)
        else:
            ln, j = varint(b, j)
            if j + ln > n:
                break
            j += ln
        i = j
        last_field = field
    return i


found = {}
for m in re.finditer(rb'\x0a([\x05-\x7f])([A-Za-z0-9_./-]+\.proto)', img):
    ln = m.group(1)[0]
    name = m.group(2)
    if len(name) != ln:
        continue
    start = m.start()
    end = parse_len(img, start)
    blob = img[start:end]
    fd = descriptor_pb2.FileDescriptorProto()
    # trim from the end until it parses (the walker may overshoot into adjacent data)
    ok = False
    for cut in range(len(blob), max(len(blob) - 64, 0), -1):
        try:
            fd = descriptor_pb2.FileDescriptorProto.FromString(blob[:cut])
            if fd.name == name.decode() and (fd.message_type or fd.service or fd.enum_type or fd.dependency):
                ok = True
                blob = blob[:cut]
                break
        except Exception:
            continue
    if ok and (fd.name not in found or len(blob) > len(found[fd.name][1])):
        found[fd.name] = (start, blob, fd)

fds = descriptor_pb2.FileDescriptorSet()
for name, (start, blob, fd) in sorted(found.items()):
    print(f'{start:#010x} {len(blob):6d}  {name}  msgs={len(fd.message_type)} svcs={len(fd.service)} '
          f'enums={len(fd.enum_type)} deps={list(fd.dependency)}')
    fds.file.append(fd)
open(os.path.join(out, 'descriptors.pb'), 'wb').write(fds.SerializeToString())
print(f'{len(found)} files -> {out}/descriptors.pb')
