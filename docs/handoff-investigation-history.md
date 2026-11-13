# mkwmp — Handoff

Updated 2026-11-12 after validating the local Pia NAT replies.

## Latest checkpoint: player selection returns to the menu

Use ForzaInfinity's `docs/contracts/MATCHMAKING_API.md` §§3 and 4a as the
design reference once we capture the first MKWorld ticket. Start with its
separation of lobby selection, seat reservation, confirmed membership,
and cleanup on cancellation or disconnect. Keep those rules behind an
MKWorld adapter; verify ticket messages and Pia peer transport from MKWorld.

`scratch/pia-join.c` identifies `JoinRandomSessionJob` transitions from
`WaitAutoConnect` to mesh startup or disconnect. The probe now traces known
step changes and the Pia result at job offset `0xd8` via `rt_dispatch`.
The DLL builds; the trace still needs verification during player selection.
On PID 17948 the trace installed, but the attempt reported to fail on the
character selection screen produced no join-step records and no error applet
record. The server saw one NAT batch at 18:05:11, confirmed by live counts
`(3,3,1)` and ready flag 1, then presence keepalive and no ticket. This trace
does not distinguish an earlier UI failure from a join path bypassing the
indirect dispatcher. Capture the exact popup text/code before choosing which
path to instrument next.

Screenshot follow-up: the popup reads “Ocorreu um erro de comunicação.”
with no code, over the character picker. The 18:07:04 attempt repeated the
successful NAT exchange and emitted only Presence KeepAlive afterward.
Live PID 17948 still showed NAT ready with `(3,3,1)`. The guest's
`AIGlobalSeqWaitMatchmake` vtable is `main+0x3177b18` and a heap scan found
an instance at `0x22cdadf40` while the popup was visible. The matching
`AIGlobalSeqStartMatchmake` vtable is `main+0x31725f8`. Inspect the game
sequence transitions and their shared match state next; the current Pia
trace did not capture the popup's cause.

Idle-picker test, PID 32064: the popup appears without confirming a
character. NAT was again ready `(3,3,1)` and no ticket reached the server.
The start/wait phase trace through `rt_dispatch` produced no lines; absence
of trace is not evidence the callbacks were never called. `scratch/match-update.c`
has the actual vtable +0xf0 callbacks at `main+0x1a79fa0` and `main+0x1a93138`.
The latter checks the connection object at +0xc and +0x4b4/+0x4b5;
the former records the enqueue outcome via `main+0x212f6c0`. The wait
object was present at `0x22cdac108` after the popup with phase +0x2c zero.
Use the lifted native call sites or sample the phase before the popup;
reading the reset state afterward cannot identify the rejecting branch.

Read-only retry trace `scratch/picker-live.log` (18:22:08–11): connection
phase +0xc reached 3, operation +0x46c reached 10, and flags +0x4b4/+0x4b5
were both set after the NAT batch. The client then entered operation 2,
cleared both flags and cancelled the friends and maintenance streams. No
ticket was sent. `main+0x261f064` stores the operation setup return value
at +0x468 next to the operation ID; the field was 8 after the popup, but
the capture did not sample it during operation 10. Operation 10 selects
the +0x78 vtable callback via `main+0x2620a04` in one dispatch path.
Resolve the callback for the active connection type before assigning a
meaning to 8 or changing a server reply.

Follow-up: `scratch/match-queue.c` confirms +0x468 is an enqueue job ID,
not a failure code. The completion ring has the sequence 1, 3, 10, 2
for several attempts; inspect the game-side decision to tear down rather
than interpreting those job IDs as result codes. `tools/re/watch_match_connection.py`
reads the operation, flags and completion ring without changing the game.

Two processes now boot from `scratch/game` and `scratch/game-p2`. Run the
second with `MKWMP_PLAYER=2` and its own profile; its auth response and
three profile reads used the resource ending `000000000002` in
`scratch/presence-stream.log`. `tools/prepare_player_two.ps1` stages this
layout with separate binaries, logs and profile and shared game assets.
The server now leaves SubscribePresences open (method kind 2) and responds
to KeepAlive frames as they arrive (kind 3). Tests pass; the game still
cancelled the stream itself and reproduced the picker popup on port 8905.
The probe's revised Pia send path preserves the peer's loopback port,
verified with a real two-socket UDP test. It still needs a game test;
both isolated copies now have the revised probe. Player one PID 31736
reproduced the popup with successful NAT under the new probe.

The 18:42 capture identifies the phase transitions directly:
operation 3 → phase 3, operation 10 → phase 1, operation 2 → phase 0.
`main+0x1e3f88c` logs a `Companion`/`companion_state` event for op 10;
its return is zero on this run, while the phase callback returns true.
`AIGlobalSeqAnybodyMain` (`main+0x1a30810`) requests phase 3 via
`main+0x1a309d8` and uses `main+0x212f764` for its follow-up gate.
That gate treats both controller flags or global +0x118 bit 4 as a reason
to take its recovery path. Trace the flag writer before patching behavior.
Static reverse engineering is in Ghidra; Python is used for the server,
read-only process captures, and tests.

`server/local_matchmaking.py` models same-mode two-seat reservation,
membership confirmation, and requeue after an unfilled lobby cancels.
It is unit tested and has no NPLN wire adapter yet.

At 18:53:13 the live watcher caught error words `[3,0,0,0,13,5,0]`
before the desired phase switched from 3 to 0. Ghidra traced that record
to Pia's empty-address check: `main+0x150dc04` compares the address with
16 zero bytes, and `main+0x2247bbc` supplies zero when the SDK
`nn::nifm::GetCurrentIpConfigInfo` fails. The recomp's offline override
does fail this call. The new capture DLL returns a 127.0.0.1 address and
loopback netmask. The DLL builds; verify the next run no longer emits
this error and look for the first ticket. See `scratch/pia-validation.c`,
`scratch/nifm-address-getter.c`, and `scratch/disconnect-event-live.log`.

Verified: the loopback IP fix cleared the address gate. At 18:57:10 the
game sent its first `CreateMatchmakingTicket` (164 framed bytes).
Request field 2 contains the ticket, whose field 2 is config `Normal24`.
Player attributes include `batching_key=cv1`, `course_id=0`, `rom_id=cv1`.
The previous empty success reply caused an SDK abort at `main+0x13ea94c`.
Ghidra found the resource-name parser at `main+0x13ea960`. The server now
queues the request by account and mode and returns field 1 as
`tenants/<tenant>/matchmakingTickets/<uuid>`; five server tests pass.
Live acceptance verified at 19:02:37: the game used the returned ID in
`TrackMatchmakingTicket`. The stub closed that stream with an empty success
reply; the character picker returned to the main menu without the old popup.
The next change is to keep the track stream open and send state updates.
Verified at 19:05:18: holding the authenticated TrackMatchmakingTicket
stream open, without an empty frame or success trailers, leaves player one
in free roam at “A PROCURAR JOGADORES”. The next milestone is a match update
that advances both game clients; reservation alone does not notify either.
At 19:11 both game instances submitted Normal24 requests. After preferring
the account issued on the active connection, the server assigned distinct
users and tickets and opened both tracking streams (`scratch/two-tickets.log`).
The server-side queue pairs these tickets; neither game has received a match
update yet. Ghidra's track consumer is at `main+0x138fea0`.
Both clients closed tracking after roughly 150 seconds. The adapter now
releases a ticket on its last tracker reset or disconnect, and an HTTP/2
test verifies that the remaining seat returns to the queue. The running
server still has the earlier build. Ghidra followed track outcome 3 into
`main+0x13dbf00`, which reads a resource name at message object +0x28,
a second string at +0x38, and a repeated collection. The wire tags and
outcome discriminator still need verification before sending an assignment.

Ghidra confirmed the tags in the serializers at `main+0x1408a40` and
`main+0x140b440`: tracking outcome is varint field 5, value 3 enters the
session branch, field 7 holds the session object, and its field 1 holds
the resource name `tenants/<tenant>/gameSessions/<uuid>`. The experimental
`--announce-matches` mode sends the same session name to both live trackers.
Server tests pass; in-game acceptance and peer connection still need a live
test. The running listener uses `scratch/match-assignment.log`.
Port 8905 now logs to `scratch/match-ticket.log`; use Python `-E -s`
for direct server launch so its sibling module is importable.

Corrida reached player selection and, on an earlier run, the free-roam screen
with “Procurando jogadores”. That text indicates the client is searching;
it does not establish that the server received a matchmaking ticket.
On the latest runs the user selected a player and reported an error and a
return to the menu. The local gRPC log still contains no matchmaking ticket.

The UDP bridge and `server/pia_capture.py --reply` exchange Pia NAT probes on
loopback port 10025. The 16-byte packet consists of four big-endian words.
For types 101–103 the responder sends the same type, the sender port, loopback
IPv4, and zero. Type 0 is logged and left unanswered. The parser at
`main+0x1624314` reads these words; `main+0x16268c4` counts accepted replies.
`scratch/pia-replies.log` shows three recent attempts. Live guest memory
showed NAT reply counts `(3,3,1)` and the network object's ready flag set.
The previous `2318-0201` timeout was absent in these attempts.

The next observed DNS lookup was for `g2122d301.%.p.srv.nintendo.net`.
Its caller belongs to `MonitoringServerAddressResolveJob`; it is not
confirmed as the matchmaking endpoint. `scratch/profile-rating.log` shows
the client cancelling the friends and maintenance streams around the menu
return, but does not establish what triggered the cancellation. Trace the
client's transition after NAT readiness to find the actual rejection.
Keep the user in control of all in-game input.

## Goal and current milestone

Latest verified milestone: the **Online Play mode menu opens** in the
isolated build. After the profile operation succeeded, the menu had been
failing the local SDK friends availability gate at main+0x2228120.
Capture mode now reports an available empty friends list (the recomp's
GetFriendList already returns zero entries). The menu displayed Corrida,
Eliminatória, Batalha and Amigos. The user controls all menu input.
`scratch/profile-rating.log` records the TLS session on port 8905.
`BulkGetDocuments` sends a server stream of three documents with each
requested name and double Rating=1500; live guest state confirmed all
three present and the completion code was zero. The menu displays 3000
for each mode; the display conversion has not been established.
`SubscribePresences` is newly observed, with a current-user resource and
a mask for name/state/unsubscribed/attributes; the empty reply did not
block entry. Next test: select Corrida and capture matchmaking.

Latest manual checkpoint: user controls the game UI. The probe's SDK-clock
delegation alone did not fix the 1–3 FPS stalls. The targeted
`pthread_cond_timedwait` override for the gRPC call at main+0x104a8b8
converted deadlines to the SDK monotonic clock and restored ~60 FPS with
all six boot RPCs still captured. See `scratch/manual-sched-game.log` for
the slow trace and `scratch/manual-session.log` for the active TLS capture
on port 8905. The user selected Online Play and saw a communication error.
The capture shows three successful but empty `BulkGetDocuments` replies
for the normal, battle, and survival profiles, with no matchmaking ticket.
The response schema and exact rejection branch remain to be established.
Ghidra outputs: `scratch/bulk-get.c` (RPC registration at 0x14a1684 and
profile-name helper at 0x1e4542c), `scratch/bulk-consumer.c`,
`scratch/bulk-request.c`.

Intercept Mario Kart World's online calls and receive/decode them on a local server.
**The first real request has now been captured over verified TLS + HTTP/2.**
See `docs/first-auth-capture.log` and `docs/FINDINGS.md` section 6.

The request is `/nn.npln.auth.v1.Auth/IssuePrearrangedUserToken`, authority
`t-e96531c6-lp1.lp1.t.npln.srv.nintendo.net:443`, with a 96-byte protobuf:

- field 1: `tenants/t-e96531c6-lp1`
- field 3: nested message, field 1 = the probe's dummy account JWT

The auth response is now reconstructed and accepted in-game. See
`docs/auth-inferred.proto` and `docs/auth-response-capture.log`. The exact RPC response
has a User message in field 1 and a Token message in **field 2**. The minimal accepted
response omits User and supplies Token.user (1), access_token (2), refresh_token (3),
and expires_in (4, google.protobuf.Duration). Field names are inferred; wire tags
and object offsets were verified against generated parsers and serializers.

After accepting it, the game emits three further calls:
- GameSessionService/ListLatencyMeasurementServers
- LeaderboardService/UpdateHolder
- Friends/ActivateUser

## Latest checkpoint — paused at user's request, 2026-11-12 16:33

The previous main+0x104b26c abort is now cleared. Disassembly shows this is
**gRPC clock validation**, not an allocator. Function 0x104b230 checks the clock
type and that nanoseconds are below 1,000,000,000; its default source at
0x104b1b0 calls guest clock_gettime without checking failure. Stack symbol
labels mentioning memalign were misleading. An opt-in host clock_gettime
provides normalized Windows realtime (ID 0) and monotonic (ID 1) timespecs.
It replaced the unused nnsslConnectionPending override, retaining the 64 limit.

The latest game run (16:32:17–16:33:13) stayed alive for about 56 seconds with
**no FATAL/assertion**. It completed the six observed boot RPC paths: auth,
latency discovery, holder update, user activation, maintenance subscription,
and friend subscription. The latter two now receive an initial empty protobuf
snapshot and remain open rather than ending/retrying. Their full semantic
schemas are not yet reconstructed, so do not claim all online features work.

Evidence: docs/online-init-capture.log; scratch/online-stable-game.log.
Tests: native test_bridge.c passes including normalized/repeated clocks and
invalid clock IDs. New server/test_protocol.py passes a real-socket HTTP/2 test
that verifies a subscription stays open while another unary call completes.
Run: .venv/Scripts/python.exe -I -m unittest discover -s server -p test_protocol.py -v

The isolated game now also has a spirv junction to the original prepared shader
assets and private copies of pipeline-cache.bin/pipeline-keys.bin. The original
installation's DLL/profile remain untouched.

**Still outstanding for this milestone:** visual confirmation of the game/menu
and capturing an actual matchmaking attempt. The computer-use skill was read;
@oai/sky initialized successfully in node_repl, but sky.list_windows failed with
"Computer Use native pipe is unavailable ... (os error 2)". User offered to open
the ChatGPT/Codex app; we said it might restore the helper. No successful UI
snapshot or UI input has occurred. Retry the documented helper when resuming;
do not infer UI success merely from the live process and successful boot RPCs.

Test game and server are being stopped for handoff (server launcher PID 21220,
base-Python child 9024; game PID 24324). Do not kill unrelated Blender/Python
processes. Latest server used port 8903 and scratch/online-stable.log. User
explicitly asked to stop the conversation and resume later; do not launch more
tests until they resume.

## Critical corrections to the previous handoff

1. `{}` is sufficient as `npln_config.json` for the first request. The abort at
   `main+0x1013654` was NOT caused by missing config. Its function starts at
   `0x10130e0` and selects gRPC's polling backend (`grpc_poll_strategy`). The only
   useful backend, `poll`, needs EventFd, which the Windows recomp lacks.
2. The observed gRPC transport uses **embedded OpenSSL**, not `nnsslConnectionWrite`.
   Real TLS records flow through `nnsocketSendMsg` / `nnsocketRecvMsg`.
   The server must offer TLS with the certificate embedded in `capture_cert.h`.
3. The decompiler helper searched for the wrong PAC opcode. `pacibsp` is
   `0xD503237F`; fixed in `tools/ghidra/DecompAddrs.java`. Targeted decompilation
   now works in seconds; no full analysis was needed.
4. The shipped runtime differs from the recovered source checkout. Its native
   `kern_fiber_switch` rejects NULL and `fiber_main` caches a TLS address across
   migration. Both were encountered and handled through opt-in host overrides.

## Implementation now present

`native/probe/src/npln.c` registers exactly 64 overrides (compile-time checked):

- Network/account gates and virtual `_npln:/npln_config.json` with delegated normal fs.
- EventFd backed by a counter and coalesced loopback UDP wakeup, integrated with WSAPoll.
- Base-context fiber switch and migration-aware fiber entry, using COFF-resolved runtime
  delegates. These depend on the shipped runtime's native layouts: Fiber state/running
  at 0x38/0x3c, RtThread host_depth at 4 and leaving at 0x1e0. Revalidate on upgrades.
- Account AsyncContext completed SystemEvent/GetResult and a completed local network-clock task.
- Correct console addrinfo layout (address pointer at 0x20), matching FreeAddrInfo.
- Socket-option readback: TCP_NODELAY must read back the value previously set or gRPC rejects it.
- gRPC's `__errno_location` reads our errno, including EINPROGRESS/EAGAIN.
- Real scatter/gather SendMsg/RecvMsg over nonblocking Winsock; all Connect calls redirect locally.
- BuiltInManager returns a correctly shaped certificate record and a generated local certificate.
- Capture engages only for `MKWMP_ENABLE=1`; crash popups are suppressed in capture mode.

Some unused overrides were removed to stay under the runtime's 64-entry limit. In particular,
raw nnsocketSend/Recv and GetLastErrno are no longer intercepted; the observed gRPC path uses
SendMsg/RecvMsg and __errno_location. The legacy nn::ssl stubs remain for other SDK paths;
their Write/Read ABI is not validated by this capture.

## Workspace and tools

Project root: `C:\Users\alcmo\Projects\mkwmp` (not currently a git repository).
Target original: `C:\Users\alcmo\Downloads\GAMES\Mario Kart World`.
All changes in this continuation were installed into **scratch/game**, an isolated copy,
with exefs/romfs junctions to the original and a separate profile. The original installation
still has the earlier probe. Test only the workspace copy until further notice.

Python: `.venv\Scripts\python.exe`, with lz4/capstone/protobuf/h2 restored from the previous
session's scratch venv. Always use `-I` for tools/re scripts because `dis.py` shadows stdlib dis.
Zig: `C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe`.
Set `ZIG_GLOBAL_CACHE_DIR` to `$PWD\scratch\zig-cache` within the workspace.

Build/install into the isolated game:

```powershell
$env:ZIG_GLOBAL_CACHE_DIR="$PWD\scratch\zig-cache"
.venv\Scripts\python.exe -I native/probe/build.py --install "$PWD\scratch\game"
```

TLS credentials are local in `scratch/certs`. They match the public header in source.
To regenerate (requires rebuilding the probe afterwards):

```powershell
./tools/make_capture_cert.ps1
```

The private key stays under ignored scratch/, never in source. Default SANs cover
`*.lp1.t.npln.srv.nintendo.net` and localhost; pass `-DnsName` for another environment.

Start the TLS server and game (Start-Process background helpers with -WindowStyle Hidden):

```powershell
.venv\Scripts\python.exe -I server/mkwmp_server.py --port 8901 --log scratch/capture.log --tls-cert scratch/certs/capture-cert.pem --tls-key scratch/certs/capture-key.pem
```

Game environment: `MKWMP_ENABLE=1`, `MKWMP_PORT=8901`, `GRPC_DNS_RESOLVER=native`,
`MKW_TRACE=host`, `MKW_PROFILE_DIR=$PWD\scratch\game\profile`.
Run `scratch/game/mkw.exe --play`, working directory scratch/game. Capture is automatic at boot.
The game currently aborts after auth succeeds and further boot requests run; normal play should leave capture off.
Before reinstalling, stop ALL mkw processes belonging to scratch/game; the launcher can re-exec.
A venv Python server has both a launcher and a base-Python child: stop both recorded processes.
No test game/server is intentionally left running at handoff.

## Validation

- DLL builds with zig; native bridge test compiles with -Wall -Wextra -Werror and passes.
- Test source: `native/probe/tests/test_bridge.c`; link with src/log.c and -lws2_32.
  Covers EventFd counter/poll/drain/semaphore/overflow/close/reuse, addrinfo ABI,
  TCP_NODELAY readback, scatter/gather socket I/O, and certificate output. Assertions explicitly stay enabled under -O2.
- `.venv\Scripts\python.exe -I tools/re/h2client.py 8901 --tls-cert scratch/certs/capture-cert.pem`
  verifies certificate/hostname, ALPN h2, response framing, successful trailers, and stream end.
- In-game capture successfully negotiates TLS+h2, sends the auth request and receives empty OK.

## Saved RE assets and next steps

`scratch/main.bin`: decompressed ARM64 image at base 0.
`scratch/ghidra/mkw.gpr`: saved Ghidra project (targeted disassembly, not full auto-analysis).
`scratch/npln-decomp.c`, `poll-decomp.c`, `auth-response.c`, `auth-parser.c`: useful decompilations.
The earlier dependency/source scratch is under:
`C:\Users\alcmo\AppData\Local\Temp\claude\68fe1ae60d56ca0c\b7b2af32-cea0-4849-ad7e-97d055718b4f\scratchpad`.

Ghidra 12.1.4 + jdk21: `C:\Users\alcmo\Downloads\Ghidra`.
For headless runs set APPDATA, LOCALAPPDATA and Java user.home to writable scratch dirs;
otherwise Ghidra tries to write outside the workspace and fails. See scratch/ghidra.log.
Use -process main.bin -noanalysis -scriptPath tools/ghidra -postScript DecompAddrs.java OUT ADDRS.

Calls to the resource-name constructor 0xf18090 were found at:
`0xe96b1c, 0xe9bd6c, 0xea886c, 0xead78c, 0xeb26cc, 0xeb3a74, 0x1206c48, 0x1207be8`.
The actual successful RPC consumer is 0xeb25c8 (name constructor call at 0xeb26cc).
Its response clone is 0xec3a10; response parser/serializer 0xec3cc0/0xec3fa4.
Token parser/serializer: 0xec7100/0xec74c4. Duration-to-nanoseconds: 0x11dfed0.
Related RPC wrappers put Token in field 1, which is incorrect for IssuePrearrangedUserToken.
Saved RE: scratch/prearranged-consumer.c, auth-proto.c, auth-outer.c.
Continue from the latest checkpoint above: UI verification and matchmaking capture.
Full multiplayer behavior remains unfinished.

