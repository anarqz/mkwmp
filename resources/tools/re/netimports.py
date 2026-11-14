"""List the network-relevant nn:: imports/exports of each NSO module in exefs."""
import re
import sys

sys.path.insert(0, sys.argv[1])  # mkw-recomp/recomp
from nso import Nso  # noqa: E402

exefs = sys.argv[2]
pat = re.compile(r'(?i)socket|nifm|nsd|ssl|http|account|friends|npln|nex|pia|grpc|curl|ldn|bcat|news|olsc|prepo|'
                 r'notification|online|resolv|getaddrinfo|gethostby|connect|dauth|aauth|nsa|token|ngc|'
                 r'IdToken|NetworkService|crypto|time4|ec|eshop|matchmak|lobby|room|session|websocket|libcurl|ns::')
for mod in ('main', 'subsdk0', 'sdk'):
    n = Nso(f'{exefs}/{mod}')
    imps = [s[0] for s in n.imports()]
    exps = [s[0] for s in n.exports()]
    print(f'===== {mod}: {len(imps)} imports, {len(exps)} exports, needed {n.needed}')
    for name in sorted(imps):
        if pat.search(name):
            print('  IMP', name)
    if mod != 'main':
        hits = [e for e in exps if pat.search(e)]
        print(f'  -- {len(hits)} matching exports (first 400)')
        for e in sorted(hits)[:400]:
            print('  EXP', e)
