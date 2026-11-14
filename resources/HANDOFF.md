# MKWorld loopback multiplayer handoff

Updated 2026-11-12. Workspace: C:\Users\alcmo\Projects\mkwmp.

## Start here (updated 2026-11-12 22:00)

**Two isolated clients sign in, queue, and accept a shared session assignment. Both now get past matchmaking and the session join check and try to reach the Gamesync (P2P setup) server on localhost:8906.** They have not joined each other yet. Read [resources/docs/NETCODE.md](docs/NETCODE.md) first: it has the decoded flow, schemas and experiment log.

What was learned this session:

- 2321-xxxx errors are **gRPC status codes** mapped by `main+0x292f9bc` (3072 = FAILED_PRECONDITION, 4992 = UNAVAILABLE), often raised locally by the SDK.
- 2321-3072 was the join check: each MatchedPlayer (track field 6) needs a `userSessions` resource (field 2). 2321-4992 was Gamesync: session **fields 8/9 are the Gamesync host/port**; empty → `:0`.
- Probe tracing (`MKWMP_TRACE_EDGES=1`) logs NPLN code-path edges and SDK strings; it found the above in one run.

**Next action:** run the two-client test once with the current server (already running, log `scratch/run-b2.log`). Look for the 8906 sniff line (`Gamesync connection first bytes … -> TLS/h2c`), `Gamesync/IssueToken`, and `KeepUserSession` frames. The 21:46 run showed a TLS ClientHello on 8906; the certificate now also covers `gamesync.npln.nintendo.net` (verified locally), so the handshake should succeed. Then build the KeepUserSession reply that delivers the client's `__us/<user-id>` document (keys `gsid`, `addr`, `p`, `mcn`, `maxu`, `rs`; see NETCODE §4).

Commits this session: 99b3f96 scanners; 42ff927 typed assignment + NETCODE.md; 458006e tracing + Gamesync endpoint; bb215a2 TLS/h2c sniffing; 6d30f0c IssueToken + KeepUserSession handling.

The user controls every in-game click. Do not use computer control for the game. Shell launch of the isolated copies is authorized when requested; ask the user to close the games before replacing their DLLs. Use Ghidra for static reverse engineering; Python is for the server, tests and read-only live memory sampling. Commit each verified milestone. Use ForzaInfinity's docs/contracts/MATCHMAKING_API.md §§3 and 4a as the design reference for reservation, confirmed membership and cancellation, with MKWorld-specific wire and peer transport.

## State at handoff

- Both games are closed. Player copies: scratch/game and scratch/game-p2, both with winmm.dll SHA-256 starting EB0A93E8 (tracing, explicit-port connect, certificate with gamesync.npln.nintendo.net SAN; old credentials backed up in scratch/certs-backup-20261112). Original game assets are shared through junctions; profiles, binaries and logs are separate. Do not install into the original under Downloads.
- Server (one process): TLS + HTTP/2 on 127.0.0.1:8905 and Gamesync on 127.0.0.1:8906 (TLS or h2c, sniffed), assignments enabled, log scratch/run-b2.log. Pia NAT responder: UDP 127.0.0.1:10025, PID 15372, log scratch/pia-replies.log. An old unrelated capture server from 16:36 may still hold port 8904. Recheck PIDs before operating on any process.
- Launch clients with `MKWMP_TRACE_EDGES=1` in addition to the variables below to get edge/string traces (boot runs ~0.35–0.6× speed while tracing).
- Git main checkpoints: a977fc5 ticket capture and creation; 7cfad1f open tracking/free roam; f742660 distinct live tickets; 0a85271 release seats when tracking ends; 5009b2a experimental shared assignment. Roster, top-level player list and user-session changes were developed after 5009b2a. Preserve their checkpoint as experimental until a game verifies it.
- Older notes, including superseded hypotheses and runtime bring-up, are archived in [investigation history](docs/handoff-investigation-history.md). See also [early findings](docs/FINDINGS.md) and [inferred auth schema](docs/auth-inferred.proto).

## Reproduce the current experiment

If the listeners are already active, reuse them. To start them from the workspace in separate shells:

    .venv/Scripts/python.exe -E -s resources/python-lobby/mkwmp_server.py --port 8905 --tls-cert scratch/certs/capture-cert.pem --tls-key scratch/certs/capture-key.pem --log scratch/run-b1.log --announce-matches

(The same process also serves Gamesync on 8906; change with `--gamesync-port`.)
    .venv/Scripts/python.exe -I resources/python-lobby/pia_capture.py --reply --log scratch/pia-replies.log

Use a new log filename if preserving an earlier run. The TLS certificate must match native/probe/src/capture_cert.h. Do not start another listener on the same port. Run without --announce-matches to reproduce the verified two-player waiting state without sending experimental assignments. A server restart clears in-memory tickets and token mappings. Fresh game launches after a restart avoid stale connections or missing local account mappings.

Launch player one from the workspace with the following PowerShell. For player two change scratch/game to scratch/game-p2 and MKWMP_PLAYER to 2. Do not open duplicates if a copy is already running.

    $game = (Resolve-Path 'scratch/game').Path
    $env:MKWMP_ENABLE = '1'
    $env:MKWMP_PLAYER = '1'
    $env:MKWMP_PORT = '8905'
    $env:GRPC_DNS_RESOLVER = 'native'
    $env:MKW_PROFILE_DIR = Join-Path $game 'profile'
    Start-Process -FilePath (Join-Path $game 'mkw.exe') -ArgumentList '--play' -WorkingDirectory $game -PassThru

Tell the user to choose Online Play → Corrida in each window. Have the first player confirm a character and enter free roam, then have the second click Corrida. **The second ticket is created before character confirmation.** After the second click, check the server log for two distinct user IDs, two ticket IDs, one assignment and any new RPC. Check both scratch/game*/logs/mkw.log files for controller events, game errors, SDK aborts and non-NAT peer UDP. Keep the two runs and their log timestamps separate. A later player-one Vulkan queue submit error (-4) is recorded in the game log; it does not explain the earlier reproducible assignment rejection in both clients.

## What is verified

| Stage | Evidence and implementation |
|---|---|
| Runtime and transport | The injected winmm.dll uses runtime COFF symbols and opt-in host overrides, within a 64-entry limit. The title uses embedded OpenSSL over Winsock SendMsg/RecvMsg, not SDK SSL writes. TLS + h2 is verified. Early gRPC polling needed EventFd support; fiber delegation, clock normalization and targeted pthread_cond_timedwait clock conversion at main+0x104a8b8 removed the 1–3 FPS stall. |
| Login | IssuePrearrangedUserToken accepts a local response with outer field 2 containing user resource, access token, refresh token and Duration (fields 1–4). MKWMP_PLAYER=2 selects a different assertion and SDK account ID. The server prefers the user issued on the connection over a cached bearer. Distinct local users ending 1 and 2 were verified with live tickets. |
| Online menu | BulkGetDocuments streams three named profile docs with Rating=1500. The client displayed 3000; the conversion is unknown. An available empty SDK friends list cleared the menu gate. Friends, presence and maintenance subscriptions remain open; presence KeepAlive is handled per frame. Closing presence early was fixed but was not the cause of the old picker error. Latency discovery still receives an empty success; its impact on later phases is unknown. |
| Pia NAT | UDP request types 0, 101, 102, 103 are four big-endian words (16 bytes). Reply types 101–103 carry type, sender port, 127.0.0.1 and zero. Type 0 is logged only. Ghidra parser main+0x1624314, counters main+0x16268c4; live counts (3,3,1). The peer bridge preserves explicit loopback ports and passed a real two-socket test. In-game peer exchange remains unverified. |
| Old picker popup | Error record [3,0,0,0,13,5,0] preceded recovery. main+0x150dc04 rejects a zero local address; main+0x2247bbc returns zero when GetCurrentIpConfigInfo fails. The DLL now supplies 127.0.0.1 and a loopback netmask. This cleared the gate and produced the first real ticket. |
| Create and track | Create request outer field 2 contains a ticket with field 2 config tenants/current/matchmakingConfigs/Normal24. Attributes: batching_key=cv1, course_id=0, rom_id=cv1. Empty success aborted at main+0x13ea94c; named response field 1 tenants/<tenant>/matchmakingTickets/<uuid> was accepted. Track request field 1 is the same ticket under tenants/current; field 2 is tenants/current/users/current. Keeping Track open without a body allows free roam and searching; immediate empty success returns to menu. Both clients eventually cancel if no assignment arrives. |
| Assignment | With --announce-matches, both live trackers get the same gameSessions UUID. Ghidra serializer main+0x1408a40: outcome field 5, value 3 enters session branch; field 7 carries session. Session serializer main+0x140b440: field 1 resource, field 12 repeated members. main+0x1391884 calls main+0x13dbf00 with the session object and a separate repeated collection from tracking field 6. |

## Current failure and next investigation

**Superseded 2026-11-12 22:00:** items 1–3 below are resolved (see "Start here" and resources/docs/NETCODE.md §§1–4). The current failure is the Gamesync connection on 8906. Item 4 still applies.

1. A name-only assignment, then a session roster, then a roster plus top-level field 6 all produced **2321-3072 (0x180141)** in both clients. Swapping queue order swaps which client is already in free roam. The second client can fail during the picker. The server shows assignment fan-out but no subsequent session RPC; the game logs show event kind 2, detail 0x1/0x180141/0/0xb/0x6/0 and error caller main+0x25db61c. Do not treat “waiting for other players” as proof of a joined session.
2. The newest candidate adds member field 2 as tenants/<tenant>/gameSessions/<uuid>/userSessions/<user-id>. Ghidra traced the consumer at main+0x13dbda0 into main+0x13ea200 / main+0x13e9de0, which parses gameSessions and userSessions segments. The exact identifier semantics and completeness of the member data remain unverified. **First action: run the fresh two-client experiment above.**
3. If the error persists, trace the consumer after main+0x137e024 (success result construction) to the producer of 0x180141. Verify that the game decodes both field 6 entries and the expected local identity. Inspect member string field 2, session string field 8 (read at object +0x38), numeric fields 2/3/9, and any peer/transport metadata. Determine requirements from the caller before filling fields. Do not blindly treat global recovery flags or operation IDs as the root error.
4. Once a new RPC or UDP packet appears, capture it and recover its reply. Keep the experimental assignment behind its flag. Verify both clients share a session, see each other move, and complete a race before declaring multiplayer working. Commit each verified step; keep unsuccessful experiments labeled.

Known server limitations: membership confirmation is modeled but not connected to an observed game join RPC. Completed tracking streams are detached from cleanup so assigned tickets survive; the same ticket and lobby were reused on retry in an earlier server process. Use a fresh server queue for a clean comparison. The bridge is local-only for Pia; the monitoring DNS name g2122d301.%.p.srv.nintendo.net was traced to MonitoringServerAddressResolveJob and is not a verified matchmaking endpoint.

## Tools and evidence

- Server: resources/python-lobby/mkwmp_server.py, resources/python-lobby/local_matchmaking.py, resources/python-lobby/pia_capture.py. Probe: native/probe/src/npln.c and native/probe/build.py. Player-two staging: resources/tools/prepare_player_two.ps1.
- Tests: .venv/Scripts/python.exe -I -m unittest discover -s server -v (six passed at handoff). Direct server launch uses -E -s so it can import its sibling module. Build with ZIG_GLOBAL_CACHE_DIR and ZIG_LOCAL_CACHE_DIR set to scratch subdirectories; see history for the Zig location and native bridge test. Only copy the rebuilt DLL after the user closes both isolated processes.
- Ghidra: C:/Users/alcmo/Downloads/Ghidra/ghidra_12.1.4_PUBLIC/support/analyzeHeadless.bat; JAVA_HOME is C:/Users/alcmo/Downloads/Ghidra/jdk21. Use scratch/ghidra project mkw, program main.bin, -process main.bin -noanalysis -readOnly -scriptPath tools/ghidra. The raw guest image is based at zero; live guest main base is 0x7000000000. DecompAddrs.java takes output path and addresses; entry:<hex> forces an exact entry for leaf or tail-call functions. InspectWireAddresses.java and FindArm64PageRefs.java handle address and cross-reference lookups.
- Focused Ghidra outputs: scratch/pia-validation.c, nifm-address-getter.c, ticket-parser.c, track-consumer.c, track-flow.txt, track-serialize.c, session-roster.c, assignment-result.c and member-resource.c. Live capture helper: resources/tools/re/watch_match_connection.py.
- Live logs in order: scratch/presence-stream.log (first create and empty-reply abort), match-ticket.log (first Track), track-ticket.log (free roam), two-tickets.log (distinct clients), match-assignment.log (name only), match-roster.log, match-players.log (field 6 still failed), match-user-sessions.log (latest candidate; no ticket yet). Scratch and generated binaries are ignored by Git; keep them on this machine for the next agent.
