# MKW multiplayer — reverse-engineering findings

**Latest status:** section 6 supersedes the earlier transport/config hypotheses.
The first auth request has been captured over real TLS + HTTP/2; see resources/HANDOFF.md.

Target: `C:\Users\alcmo\Downloads\GAMES\Mario Kart World\mkw.exe`
Source of truth for the runtime: https://github.com/adriadam10/mkw-recomp

## 1. The binary is a static recompilation, not a native x86 game

`mkw.exe` is the [mkw-recomp](https://github.com/adriadam10/mkw-recomp) runtime. The Switch 2
ARM64 game (`exefs/main`, `sdk`, `subsdk0` — NSO modules) is lifted to C and compiled in. The
game's `nn::*` SDK calls are serviced by a table of host functions registered at load time:

- `HOST_OVERRIDE("<mangled nn symbol>", c_fn)` — replaces an SDK import in every module's GOT.
- Registration tables live in PE sections `.ovr` / `.obs` / `.fns` (visible in the PE).
- Each host fn is `void fn(Cpu *c)`: reads args from the emulated CPU, writes the return to x0.
- Runtime internals are **COFF symbols** in `mkw.exe` (full symbol table present, not stripped),
  NOT PE exports. The PE export table has only 6 names (`rt_dispatch`, `rt_svc`, …).

Consequence vs. ForzaInfinity (native x86 hooking): we do not hook x86 functions. We get code
into the process the same way (proxy DLL), but then we register **our own host overrides** by
name through the recomp's own mechanism.

### Cpu struct (runtime/cpu/cpu.h)
- `x[32]` at offset 0 (x0..x30; x31 unused), `sp` at 256, `pc` at 264, `thread` at 312.
- Arg access (AAPCS64): `arg_x(c,i) = i<8 ? x[i] : *(u64*)(sp+8*(i-8))`.
- `arg_p(c,i) = (void*)(uintptr_t)arg_x(c,i)` — **guest memory is directly host-addressable**
  (recomp maps the guest address space into the host process). So our C overrides read/write
  guest buffers through the raw pointer values.
- Return: `ret_x(c,v) => x[0]=v`. Caller LR: `x[30]`.

### Runtime symbols we rely on (RVAs in the shipped mkw.exe; add live module base)
| symbol | RVA | use |
|---|---|---|
| `hostfn_add_override(const char* name, GuestFn fn)` | 0x342a0 | register an override; **must run before `ldr_load`** (fatal otherwise) |
| `hostfn_override(const char* name)` | 0x33e90 | lookup |
| `ldr_count()` | 0x145a0 | 0 until modules load |
| `ldr_load()` | 0x14870 | loads+relocates modules (binds overrides) |
| `ldr_symbol(const char*)` | 0x14750 | resolve a guest symbol addr |
| `rt_call(Cpu*, u64 fn, int n, ...)` | 0x34dd0 | call guest code from a host fn |
| `rt_log(const char* fmt, ...)` | 0x178b0 | write to the game log (`logs/mkw.log`) |
| `cfg_get`/`cfg_bool` | 0xde80 / 0xe9a0 | read MKW_* settings |
| `hostfn_seal` | 0x349c0 | builds dispatch table; registration ends here |

RVAs can shift if the game updates; the DLL resolves them by **name** from the COFF table.

## 2. Online stack: NPLN + Pia (nplnd), gRPC/HTTP2/TLS

Pulled from strings/descriptors in `exefs/main`:

- SDK versions: `NintendoSDK_NPLN-1_33_0`, `NintendoSDK_gRPC_For_NPLN`, `OpenSSL_For_NPLN`,
  `PiaNplnd-6_37_3`.
- Transport: gRPC → `nn::ssl::Connection` (TLS, ALPN `h2`) → `nn::socket` UDP/TCP fd.
  `main` imports the **C** ssl API: `nnsslConnectionCreate/SetSocketDescriptor/SetHostName/
  SetNextAlpnProto/DoHandshake(WithCertBuffer)/Write/Read/Peek/Pending/GetNextAlpnProto/Destroy`,
  plus `nnsslContextCreate/ImportClientPki/Destroy`, `nnsslInitialize/Finalize`.
  ⇒ **Intercepting `nnsslConnectionWrite`/`nnsslConnectionRead` gives the post-TLS plaintext
  HTTP/2 (gRPC) stream** — no TLS to break.
- Socket API `main` imports (BSD, FreeBSD-flavored consts): `Socket/Bind/Connect/SendTo/RecvFrom/
  Poll/SetSockOpt/Shutdown/Close/EventFd*/GetAddrInfo` (C `nnsocket*` + C++ `nn::socket::*`).
- Errno is Linux-numbered; game checks `GetLastErrno == 11` (EAGAIN). gRPC connect loop retries
  on errno 4 (EINTR); fcntl sets O_NONBLOCK.

### Endpoints (string table)
- `%s.%s%s.t.npln.srv.nintendo.net:443` (per-service gRPC host template)
- `gamesync.npln.nintendo.net`
- `nncs1-%.n.n.srv.nintendo.net:10025`, `nncs2-%.n.n.srv.nintendo.net:10025` (Pia relay/NEX-like)
- `%s.%%.p.srv.nintendo.net`
- `oauth2.googleapis.com`, `/grpc.gcp.HandshakerService/DoHandshake` (ALTS handshaker — present
  in the gRPC lib; may be unused by the retail path)
- Config resource: `_npln:/npln_config.json`; header `npln-tenant-id`; UA `NPLN/%s (%s; %s)`.

### gRPC services and methods (full method paths found in `main`)
All `nn.npln.*.v1`. Paths are `/pkg.Service/Method`:

- **auth.v1.Auth**: CreateUser, IssueToken, IssueAnonymousUserToken, IssuePrearrangedUserToken,
  RefreshToken, RefreshAnonymousUserToken, ValidateToken, DeleteUser, ListUsers
- **matchmaking.v1.Matchmaker**: CreateMatchmakingTicket, CancelMatchmakingTicket,
  TrackMatchmakingTicket, CreateAcceptance
- **matchmaking.v1.GameSessionService**: CreateGameSessionCreationTicket,
  TrackGameSessionCreationTicket, CancelGameSessionCreationTicket, CreateGameSession(implied),
  JoinGameSession, GetGameSession, BatchGetGameSessions, QueryGameSessions, ListUserSessions,
  GetUserSession, Create/GetGameSessionShortAlias, AllocateIceServerSet,
  ListLatencyMeasurementServers, IssueMatchmakingIdToken, IssueUserDelegationToken
- **friends.v1.Friends**: ListFriendUsers, SubscribeFriendUsers, ActivateUser, ListBlockingUsers
- **friends.v1.PresenceService**: SubscribePresences, KeepAlive
- **gamesync.v1.Gamesync**: IssueToken, RefreshToken, KeepUserSession, Begin/Commit/Rollback
  Transaction, Read/Write/List/GetDocument(s), CreateRound, QueryCollectionIds
- **leaderboard.v1.LeaderboardService**: RegisterScore, FindRankScores, FetchRankScore,
  RetrieveQuantileScores, BrowseLeaderboard, Get/BatchGet Charts, Create/Get/Update/DeleteHolder,
  SearchHolders, PurgeUserScores/ScoreHolders, Get/DeleteScore, GetCategory, GetSeason
- **ugcstore.v1.Ugcstore**: Get/BulkGet Documents, CommitDocuments, Issue(Upload|Attachment)Uri,
  BulkIssueAttachmentUri, RunQuery, Create/GetDocumentShortAlias
- **maintenance.v1.MaintenanceScheduleService**: SubscribeMaintenanceSchedules

Embedded protobuf FileDescriptorProtos recovered: only the well-known types
(`google/protobuf/*`, `google/rpc/status.proto`) are serialized in the binary. The NPLN message
schemas are **not** embedded as descriptors — they must be reconstructed from the wire bytes we
capture (protobuf field numbers/types inferred from the gRPC payloads).

## 3. Why we can't just sniff sockets

mkw-recomp deliberately severs online (`runtime/sdk/ov/ov_net.c`, `ov_socket.c`):
- `nifm` reports no internet; `account` network-service-account unavailable; `nsd::ResolveEx`
  fails; `socket::GetAddrInfo` resolves nothing; sockets are a network-down stub (or, with
  `MKW_LAN=1`, LAN-only with a filter that refuses any non-private address).
- ⇒ The game never emits a single NPLN request today. To capture requests we must re-open the
  upstream gates (nifm/account/nsd/socket) and then service the gRPC stream ourselves.

## 4. Interception design (chosen: gRPC/NPLN semantic layer, proxy winmm.dll)

Foothold: `mkw.exe` imports `timeBeginPeriod` from `WINMM.dll`; `SDL3.dll` (same folder) imports
`timeBeginPeriod`/`timeEndPeriod`. A proxy `winmm.dll` dropped in the game folder gets `DllMain`
at process init (before `mainCRTStartup` ⇒ before `ldr_load`). It forwards the two real exports
to `C:\Windows\System32\winmm.dll` and registers our host overrides.

Overrides, in two groups:
1. **Gates** (make the flow proceed): `nifm` available + fake IP; `account` NSA available + fake
   id/token; `nsd` resolve ok; `socket` fully virtualized (fake always-connected fds) so gRPC's
   TCP layer is satisfied without real sockets; `GetAddrInfo` → a dummy addr.
2. **Transport capture**: `nnsslConnectionWrite` logs the outbound plaintext (HTTP/2 → gRPC
   request frames); `nnsslConnectionRead`/`Peek`/`Pending` feed inbound bytes from our local
   server (initially: return "pending" so we capture requests before crafting responses).

The local server (phase 2) is a minimal HTTP/2 + gRPC endpoint that decodes `:path` =
`/nn.npln.*/Method` and the length-prefixed protobuf body, logs it, and returns a canned/crafted
response so the game advances to the next call.

### Milestones
- **M1 (foothold):** proxy winmm.dll loads, resolves COFF symbols, registers observers that log
  via `rt_log` + our own file — proves code-in-process + symbol resolve + override timing + ABI.
- **M2 (capture):** gates open + socket virtualized + ssl Write logged ⇒ dump every request.
- **M3 (server):** local HTTP/2+gRPC server returns responses; iterate per method.

## 5. Progress log / current state (2026-11-12)

**DONE & working:**
- Proxy `winmm.dll` (`native/probe/`, built with `build.py` → zig) loads, forwards all 180 winmm
  exports to `winmm_host.dll`, resolves the recomp runtime + static fs handlers from COFF, and
  registers host overrides that verifiably fire. **Opt-in via env `MKWMP_ENABLE=1`** (normal launch
  = stock/offline).
- Overrides open the online gates (`nifm`, `account` NSA + fake token, `nsd`) so the NPLN login job
  starts at boot.
- `nn::ssl` stubbed (bypass TLS) + `nnsocket*` wrapped over real non-blocking winsock with Connect
  redirected to `127.0.0.1:$MKWMP_PORT`; `ssl::Connection::Write/Read` bridged to the socket.
- Local gRPC capture server `resources/python-lobby/mkwmp_server.py` (h2c via `h2`, schema-free protobuf decoder,
  returns empty grpc-status 0). Verified standalone.
- fs: 1-arg `MountRom`/`Unmount` stubbed; `OpenFile/GetEntryType/ReadFile/GetFileSize/CloseFile`
  log-and-delegate to the recomp's real handlers (resolved from COFF: `ov_open_file` etc.), and
  **virtualize `_npln:/npln_config.json`** from in-DLL content (`kNplnConfig` in npln.c).

**Walls cleared (each was a recomp service gap NPLN hit):** `ssl::Initialize` HIPC → stub; 1-arg
`fs::MountRom` + `fs::Unmount` fatal → stub; `_npln:/npln_config.json` missing → virtualize.

**CURRENT BLOCKER:** with config served as `{}` (parses fine — the loader at `main+0xf139c0`
memsets a 4096B buffer with spaces, reads the file, parses JSON at `main+0xf14790` into a global at
`[0x3389000+0xbf8]`), a later *consumer* still `abort()`s at `main+0x1013654`: a factory
(`blr x29`, result stored to `[x23+0xd08]` at `main+0x1013568`) returns NULL because a required
config field is absent. ⇒ Need NPLN's **config schema**: find the keys consumers read from the
parsed config (start at the aborting function `main+0x1013xxx` and the JSON parser `main+0xf14790`
/ the global config object `0x3389000+0xbf8`), synthesize a valid `npln_config.json`. After that,
expect possible further layers (credential/cert validation) before the first gRPC Write reaches the
server.

**Resume checklist:**
1. RE the config schema: xref the parsed-config global `0x3389000+0xbf8`; find key-string lookups.
2. Put a valid config in `kNplnConfig` (npln.c); rebuild+install; run with
   `MKWMP_ENABLE=1 MKWMP_PORT=8900 GRPC_DNS_RESOLVER=native` and the server running.
3. Watch `logs/mkw.log` (`mkwmp:` lines + FATAL) and the server log for the first gRPC call.
4. Note the 64-override cap (`MAX_ADDED`); we're at ~63. Consolidate if adding more.

## 6. First in-game capture (2026-11-12, 16:14 local time)

**Verified twice:** a real boot request reaches our server using TLS 1.2 and ALPN h2.
The exact capture, including raw gRPC body hex, is saved in `resources/docs/first-auth-capture.log`.

- Path: `/nn.npln.auth.v1.Auth/IssuePrearrangedUserToken`
- Authority: `t-e96531c6-lp1.lp1.t.npln.srv.nintendo.net:443`
- Body: 101 bytes (5-byte gRPC envelope plus 96-byte protobuf).
- Field 1 is `tenants/t-e96531c6-lp1`.
- Field 3 is a nested message; its field 1 is the dummy network-service-account JWT.
- We return a zero-length protobuf with grpc-status 0. Transport succeeds, but this
  is not a valid auth response: the game later asserts in a user resource-name parser.

### Corrected transport and config findings

The abort previously attributed to config at main+0x1013654 belongs to the gRPC polling-engine
selector starting at 0x10130e0. The config `{}` is enough for the first request. The selector
reads `grpc_poll_strategy` (structure at 0x33207c8), default `all`, then tries factories in a
registry at 0x33207d8..0x3320870. The populated `poll` factory is at 0x10139f0 and requires the
EventFd probe to succeed. Our counter + loopback UDP wakeup makes it work on Windows.

The observed transport is guest OpenSSL -> nnsocketSendMsg/RecvMsg -> nonblocking Winsock ->
loopback TLS server. nnsslConnectionWrite/Read are NOT used for this request. A TLS ClientHello
was directly captured after fixing scatter/gather socket calls. The server now accepts
`--tls-cert` and `--tls-key`; it still supports h2c when neither is passed.

A self-signed local certificate is returned through BuiltInManager. Its info record has the
DER length at +8 and pointer at +0x10. The output of GetBuiltInCertificates is a full pointer,
not a 32-bit size. The public DER is embedded in capture_cert.h; the matching private key and
PEM live only in ignored scratch/certs. Regenerate with resources/tools/make_capture_cert.ps1 and rebuild.

Other blockers cleared: console addrinfo address pointer at +0x20; matching FreeAddrInfo;
TCP_NODELAY readback; __errno_location rather than nnsocketGetLastErrno for gRPC's errors;
completed account async event and completed network-clock task; base-context fiber switching.

### Native runtime fiber issues

The shipped PE is older than the recovered source checkout in important details. COFF-based
x86 disassembly showed kern_fiber_switch rejects a NULL target unconditionally, although
GetCurrentFiber returns NULL on the base context. Its lower fiber_switch_to already supports
NULL, so our override routes that case there.

Its fiber_main also preserves the address of the original thread's TLS across rt_call. Guest
fibers can migrate during that call. The old final switch consequently uses a departed thread,
causing crashes after async task completion. Our InitializeFiber override delegates creation,
then replaces the host fiber entry with an entry that fetches the current thread again through
kern_cpu after rt_call. This cleared the repeated crash and enabled DNS/TLS/auth traffic.
This uses native layout offsets from this PE: Fiber state/running 0x38/0x3c, RtThread depth +4
and leaving +0x1e0. Revalidate those offsets for another mkw.exe build.

### New response blocker

The post-response assert is at main+0xf180dc, in the resource-name constructor at 0xf18090.
It calls 0xf180e0, which parses `tenants/<tenant>/users/<user>` and returns false for an empty
or malformed name. The exact protobuf response field providing that name is not recovered yet.
Useful direct callers of 0xf18090: 0xe96b1c, 0xe9bd6c, 0xea886c, 0xead78c, 0xeb26cc,
0xeb3a74, 0x1206c48, 0x1207be8. Saved decompilations are scratch/auth-response.c and auth-parser.c.

### Validation and isolation

All new in-game testing used scratch/game, with its own profile and read-only asset junctions
pointing at the original game. The original installed DLL was not replaced in this continuation.
The probe remains opt-in, now requiring the exact value MKWMP_ENABLE=1. Capture mode suppresses
Windows crash dialogs. Native integration tests pass with -O2 -Wall -Wextra -Werror, including
EventFd, addrinfo ABI, socket-option readback, scatter/gather I/O and certificate output. The
TLS h2 client validates certificate/hostname, h2 ALPN, response framing, trailers and stream end.

## 7. Auth response recovered and accepted (2026-11-12, 16:24)

Following the generated response parser and consumers recovered enough of the response to
pass login. `resources/docs/auth-inferred.proto` records the wire schema and `resources/docs/auth-response-capture.log`
contains the successful exchange and three follow-up requests.

The actual RPC consumer is main+0xeb25c8, name constructor call 0xeb26cc. Its response clone
at 0xec3a10 reads a User at object+0x10 and a Token at object+0x18. The response parser at
0xec3cc0 and serializer at 0xec3fa4 map these to wire fields 1 and 2, respectively. Related
RPC responses analyzed first put Token in field 1; that layout is wrong for this RPC.

Token parser/serializer 0xec7100/0xec74c4:

| Field | Wire type | Object offset | Consumer meaning |
| --- | --- | --- | --- |
| 1 | string | +0x10 | user resource, parsed as tenants/<tenant>/users/<user> |
| 2 | string | +0x18 | access token (semantic name inferred) |
| 3 | string | +0x20 | refresh token (semantic name inferred) |
| 4 | message | +0x28 | google.protobuf.Duration; seconds/nanos converted by 0x11dfed0 |

The server returns Token in response field 2, a local UUID user resource, a synthetic unsigned
JWT, a local refresh token and a 3600-second Duration. It omits the outer User field, unused on
the observed success path. This passed the client consumer and triggered:

- `/nn.npln.matchmaking.v1.GameSessionService/ListLatencyMeasurementServers`
- `/nn.npln.leaderboard.v1.LeaderboardService/UpdateHolder`
- `/nn.npln.friends.v1.Friends/ActivateUser`

Those still receive empty success messages. A subsequent assert runs through memalign and
__assert_fail, with main+0x104b26c in its stack; its underlying cause remains unknown.
Temporary assertion tracing used to locate the exact caller was removed from the final probe.

## 8. Clock abort cleared; boot subscriptions remain active (2026-11-12, 16:33 checkpoint)

main+0x104b26c belongs to gRPC clock validation, not an allocator. At 0x104b230,
clock type must be less than 3 and nanoseconds less than one billion. The default
callback at 0x104b1b0 invokes clock_gettime without testing its return. The SDK
realtime implementation can fail, leaving this output unsuitable for gPR.
Our host clock_gettime supplies normalized realtime/monotonic timespecs. This
cleared the observed abort; native tests cover clock range, monotonicity and
invalid IDs. The exact guest clock failure reason has not been instrumented.

Maintenance/friend subscription responses now send an empty initial protobuf
and keep their HTTP/2 streams open. A real-socket protocol test validates that
unary RPCs can complete alongside the open subscription. The latest in-game run
survived about 56 seconds with no FATAL/assertion, all six boot paths observed,
and no subscription retry after keeping those streams open. Evidence is saved
in resources/docs/online-init-capture.log and scratch/online-stable-game.log.

Visual/menu verification is still pending: the computer-use helper could not
connect its native pipe. No UI input was performed, and no actual matchmaking
request has been captured yet. User paused work at this point.
