"""Extract printable strings from a decompressed NSO image that match a regex.

usage: python -I nsostrings.py <nso> <regex> [--dump out.bin]
"""
import re
import sys

sys.path.insert(0, __import__('os').path.dirname(__file__))
from nsodump import Nso  # noqa: E402  (our own parser, scripts dir)

n = Nso(sys.argv[1])
rx = re.compile(sys.argv[2].encode())
if '--dump' in sys.argv:
    open(sys.argv[sys.argv.index('--dump') + 1], 'wb').write(n.image)
seen = set()
for m in re.finditer(rb'[\x20-\x7e]{5,}', n.image):
    s = m.group()
    if rx.search(s) and s not in seen:
        seen.add(s)
        print(f'{m.start():#010x} {s.decode()}')
