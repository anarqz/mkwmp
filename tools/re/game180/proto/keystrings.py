"""Presence of protocol literals (the ones our servers/probe hard-code) in the main image of two builds.

usage: python -I keystrings.py <dump 1.0.0> <dump 1.8.0> [word ...]
Without words it checks the built-in list (literals found in rust/lobby, the Python lobby and the RE docs).
For each literal: image offsets of NUL-delimited exact occurrences and the raw substring count, per build.
"""
import os
import sys

WORDS = '''Normal24 Battle24 Private24 DriverId SessionId greeting_id total_player_count dropout_rank sub_mode rule mode
p2p_sid rating_enabled Rating ratings request_id mcn maxu pgn ussid_map ucsid upcsid ussid uid gsid addr st rs
t-e96531c6-lp1 e96531c6 __pgn __pus __stu __gs __us @@RefereeRequest @@RefereeReport @@RefereeResult @@Referee
PlayerStatic Companion All User Request vehicle driver solo battle option status result users sid
gamesync.npln.nintendo.net tenants/current userSessions/current deferments targets iceServerSets
matchmakingConfigs matchmakingTickets gameSessions gameSessionCreationTickets GameSessionShortAliases
npln_user_id RatingCoefficient @@drppr_ga_scopes clutch Clutch Survival Knockout Swarm'''.split()


def exact(img, e):
    out, i = [], 0
    pat = b'\0' + e + b'\0'
    while True:
        i = img.find(pat, i)
        if i < 0:
            return out
        out.append(i + 1)
        i += 1


def main():
    d1, d2 = sys.argv[1], sys.argv[2]
    words = sys.argv[3:] or WORDS
    a = open(os.path.join(d1, 'main.bin'), 'rb').read()
    b = open(os.path.join(d2, 'main.bin'), 'rb').read()
    for w in words:
        e = w.encode()
        xa, xb = exact(a, e), exact(b, e)
        ca, cb = a.count(e), b.count(e)
        flag = '   <== DIFFERS' if (bool(xa) != bool(xb) or bool(ca) != bool(cb)) else ''
        fa = ','.join(f'{x:#x}' for x in xa[:3]) or '-'
        fb = ','.join(f'{x:#x}' for x in xb[:3]) or '-'
        print(f'{w:30} 100: exact={len(xa)} sub={ca} [{fa}]  180: exact={len(xb)} sub={cb} [{fb}]{flag}')


if __name__ == '__main__':
    main()
