# Game 1.8.0 vs 1.0.0: what changes for the online stack

Static comparison of the two executables (no game or server was run). Scope: the probe's SDK overrides
(`native/probe/src/npln.c`), the Rust lobby (`rust/`), the Python lobby (`resources/python-lobby/`), the launcher.
Not in scope: the probe's hard-coded guest addresses (mapped separately).

Evidence tags: **V** = read in the binary (address given), **I** = inferred, **?** = not settled statically.
Addresses are image offsets in `main` (1.0.0 / 1.8.0) unless a module is named.

Tools: `tools/re/game180/proto/` (section 8). Inputs were copied to a scratch folder first; the ROM folder was only
read.

## 0. Summary

| | 1.0.0 | 1.8.0 |
|---|---|---|
| main build id | `9e244438c3d8ef1c` | `d00e4f399e411d06` |
| modules | rtld, main, subsdk0 = `nnEcli`, sdk | rtld, main, **subsdk0 = `movie`** (new, `NintendoSDK_movie-21_4_0`), **subsdk1 = `nnEcli`** (the old subsdk0, +2 debugger imports), sdk |
| NintendoSDK (sdk NSO) | `nnSdk-Generic_19_3_3` | `nnSdk-Generic_21_4_0` |
| NPLN | `NintendoSDK_NPLN-1_33_0` | `NintendoSDK_NPLN-1_47_0` |
| Pia | `Pia-6_37_3` (all 10 Pia libs) | `Pia-7_3_0` |
| gRPC core / libcurl / zlib strings | `1.38.0` / `7.64.1` / `1.2.11.f`, `1.3.1.f` | same |
| program id (main.npdm ACI0) | `0400c3f00006e000` | same |
| tenant | `t-e96531c6` (+ `t-%s-%s`) | same |
| NPLN host formats | `%s.%s%s.t.npln.srv.nintendo.net:443`, `gamesync.npln.nintendo.net` | same |

**Can 1.0.0 and 1.8.0 players share a lobby? No.** Pia's packet header version byte went from 0x0F to 0x10 and every
receive path drops a packet whose version differs (section 3.1), so the two can never form a mesh. The lobby must
keep them apart (section 5).

## 1. BREAKING (will fail, or very likely fail, without a code change)

### B1. Pia 7.3 calls SDK functions the probe does not override  **V**
`tools/re/game180/proto/plt.py` on both builds:

| Import (1.8.0 main) | Caller in 1.8.0 | In 1.0.0 | Probe today |
|---|---|---|---|
| `_ZN2nn4nifm22GetCurrentIpConfigInfoEPNS_6socket6InAddrES3_S3_S3_S3_` (new overload) | Pia `0x1661d90`, reached from the transport start `0x16f6580` -> `0x16fdd10` | not imported | only the `in_addr` overload is registered (`npln.c:1600`); mkw-recomp overrides only that one too (`runtime/sdk/ov/ov_net.c:53`) |
| `_ZN2nn6socket4RecvEiPvmNS0_7MsgFlagE` (new) | Pia socket wrapper `0x1656ff0`, used by `0x1661c10` (ICMP receive) | not imported | no override; Pia's fds are the probe's own table (`fd_new`), so the SDK's `Recv` gets a foreign fd |
| `nn::socket::Socket(Family, Type, Protocol)` with **type 3 (RAW), protocol 1 (ICMP)** | `0x1656db0` (`mov w1,#2; mov w2,#3; mov w3,#1; b 0x1655b20`), from `0x1660f10` <- `0x16fdd10` | Pia only opened UDP | `ov_pia_socket` returns `EINVAL` for anything but AF_INET/DGRAM (`npln.c:821-823`) |

This is Pia 7.3's new `PingJob` (`PingJob::StepSendIcmpEchoRequestMessageIpv4` `0x1661100`,
`...StepReceiveIcmpEchoRequestMessageIpv4` `0x16612e0`). The block at `0x16fdd98..0x16fddc4` runs only when a
virtual check (`vtable+0x50` of the object from `0x16f9f50`) returns true; which network type sets it is **?**.

Required: register the `InAddr` overload on `ov_nifm_ip_config` (same five 4-byte outputs) and add a
`nn::socket::Recv` override next to `ov_pia_recvfrom` (`npln.c:963`). For the ICMP socket decide between a real
ICMP socket and a clean failure; whether Pia treats a failed ping socket as fatal is **?** (live test T2).

### B2. Lobbies must be split by game version  **V** (Pia), **I** (how to tell them apart)
`rust/lobby/src/lobbies.rs:398` places a ticket on `ticket.mode` only, so a 1.0.0 and a 1.8.0 player asking for
`Normal24` land in one lobby and then fail to mesh (B-level failure for both). Friend rooms have the same problem
(join by Room ID / friend card). See section 5 for the discriminators.

### B3. New matchmaking configs `Clutch24` and `ClutchSurvival24`  **V**
Config-name function 1.0.0 `0x1e3f61c` -> 1.8.0 `0x20ff6e0` + `0x20ff768`:

| | 1.0.0 | 1.8.0 |
|---|---|---|
| names | `SearchAll`, `World96`, `Attraction6`, `Streamer`, `Survival24`, `Battle24`, `Private24`, `Normal24` | `SearchAll`, `Survival24`, `Battle24`, `Private24`, `Normal24`, **`Clutch24`**, **`ClutchSurvival24`** (`World96`, `Attraction6`, `Streamer` are gone from the binary) |
| mode enum (object `+0x7fc` / `+0x834`) | 1 -> `World96`, 2 -> `Attraction6`, 4 -> `Streamer`, 5 battle, 6 survival | 1 and 2 now use `Normal24`/`Private24`; 5 battle, 6 survival, **7 = clutch** (`ClutchSurvival24` when the clutch object's `+0xc8 == 1`) |

The Rust lobby is generic over the config name (`msgs.rs:214`, `lobbies.rs:28`), so a `Clutch24` ticket creates a
lobby, but nothing in it knows the mode: capacity table (`[lobby.policy]`), the referee's mode handling
(`referee.rs`, modes `normal` / `survival` / `battle` only) and the new `@@drppr_ga_scopes/clutch/...` documents
(section 2.4). Treat Clutch as unsupported until captured: refuse those tickets or keep the menu entry unreachable.

### B4. `@@RefereeResult` reader wants more than the lobby sends  **V** (reader), **?** (effect)
1.0.0 reader `0x498404` reads `result`, `status`, `ratings`, `request_id`. 1.8.0 reader (inlined in `0x6f6f80`,
block `0x6f9670..0x6f9a1c`) reads:

| Key | Read at | Guard | Stored |
|---|---|---|---|
| `result` (array, up to 24 ints) | `0x6f9694` | none | `slot+0xa4..` |
| `status` (int) | `0x6f9700` | none | `slot+0xa0` |
| `ratings` (map) | `0x6f9854` | only when `status == 1`, and present | as 1.0.0 |
| **`coefficients`** (map, same shape as `ratings`) | `0x6f98f8` | only when `status == 1`, and present | per user float at `+0x8f0` region |
| **`user_session_count`** (int) | `0x6f972c` | **unguarded** when `status != 0` | `slot+0x1d0` (0 when absent) |
| **`mp`** (float) | `0x6f9740` / `0x6f9890` | when present | `slot+0x1d4` |
| **`inet_param`** (map: `depop_is_enable`, `depop_member_count`, `depop_race_count`, `epilogue_is_enable`, `epilogue_disclose_distance_ratio`) | `0x6f9754..0x6f9840` | when present | `slot+0x1d8..0x1e8` |
| `request_id` | `0x6f9964` | none | as 1.0.0 |

`referee.rs` writes `request_id`, `status`, `result`, `ratings` only. Everything new is optional by the code path
except `user_session_count`, which a final result (`status` 1) leaves at 0. What the game does with a 0 there is
**?**; send `user_session_count` = number of users of the request in the final result and test (T4).

## 2. NEEDS LIVE TEST (changed in the binary; consequence not provable statically)

### 2.1 NPLN library 1.33 -> 1.47
- Service list: all 74 paths of 1.0.0 are still there, all `v1` (no `v2`); 1.8.0 has 80. **New**: service
  `nn.npln.appconfig.v1.Appconfig` (`FindApplicationConfig`, `PullModifiedApplicationConfig`, `IssueAttachmentUris`),
  `LeaderboardService/BrowseIndexesLeaderboard`, `LeaderboardService/EstimateScoreRank`,
  `Ugcstore/BulkGetDocumentShortAliases` (`rpcs.py`; stub constructors 1.8.0: auth `0xf8a380`, friends `0x1338d90`,
  presence `0x134bcd0`, gamesync `0x13bac90`, leaderboard `0x141cbc0`, maintenance `0x1481940`, game session
  `0x1539e20`, matchmaker `0x1575910`, ugcstore `0x15cff10`, appconfig `0x12e6d40`).
- **The game itself creates an Appconfig client** (game code `0x20f43f8` and `0x2104eb8` -> `0x12d2c90` ->
  `0x12d9320` -> stub `0x12e6cf0`). When it calls it, and whether a gRPC error blocks the online flow, is **?**.
  The Rust lobby has no handler for it (`core.rs:22-45`). Test T1 tells; be ready to answer
  `FindApplicationConfig` / `PullModifiedApplicationConfig` with an empty config.
- The NPLN version string (`1.33.0` at 1.0.0 `0x28adc58`, `1.47.0` at 1.8.0 `0x2c4d289`) is referenced once, by
  `0xeffba0` / `0xfc1140`, next to `"Horizon"` and `"npln-grpc "`: it is most likely part of the gRPC
  `user-agent` (**I**). No server code reads it today.
- Message layouts: **not settled**. `pbshapes.py` (serializer tag heuristic) gives 330 shapes for 1.0.0 and 401
  for 1.8.0 over the NPLN code range, but the difference is dominated by inlining changes, so it neither proves nor
  disproves a field-number change in the messages the lobby parses. The first capture (T1) settles the wire format
  of sign-in, tickets, GameSession, KeepUserSession, WriteDocuments.
- Token / host / tenant / header strings (`npln-tenant-id`, `Authorization: Bearer %s`, host formats) unchanged **V**.

### 2.2 Gamesync documents the game reads and writes  **V** for keys, **?** for effects
| Document | 1.0.0 | 1.8.0 | Notes |
|---|---|---|---|
| `__pus` / `__us` user session (SDK parser `0x128b2b4` -> `0x1386fe0`, writer `0x128b430` -> `0x1387160`) | `uid`, `ussid`, `ucsid`, `upcsid`, `pgn` | + **`pusa`** (a nested value at object `+0x78`, read with the unguarded getter `0x1361440`, written by the client) | client-written `__pus` docs go through the store unchanged; the lobby's own `__us/<id>` docs (`core.rs:1441-1450`) have no `pusa` |
| member state (parser `0x1297914` -> `0x1394cc0`) | `uid`, `ussid`, `ucsid`, `st`, `att`, `ltc`, `tn` | same | |
| `__gs/f` (parser `0x1267698` -> `0x13622c0`) | `gsid`, `addr`, a one-character key (`p`), `mcn`, `maxu`, `rs`, in that order | same six reads, same order | |
| `__pgn/` + `__stu` + `__stg` (`0x128bf1c` -> `0x1387dd0`) | same | same | |
| `PlayerStatic` | `name`, `play_history_key`, `p2p_sid`, `p2p_lid`, `vehicle`, `driver`, `greeting_type`, `greeting_id`, ... | + **`sticker`**, **`su_ussid`** (writer `0x20f6e38`), **`driver_id_for_clutch`** (`0xac6578`) | `core.rs:715` rewrites `name` only |
| `Companion` | `companion_state`, `my_group_id`, `course_sid`, `track_id` | + **`my_follow_ussid`**, **`my_sub_ussid`** (writer `0x20f8050`), reader also `members` | |
| **`Swarm`** (new collection, listed with `PlayerStatic`, `Companion`, `@@RefereeResult` in `0xbf4bd0`; writer `0x20f8880`) | - | new | "SwarmCompanion" strings (`AIGlobalSeqSwarmCompanionMain`, `SwarmCompanionAttachment`) |
| `@@RefereeRequest/<ussid>/Request/<key>` (builder `0x1e37e98` -> `0x20f7250`) | `users`, `ussid_map`, `mode` (`normal`/`survival`/`battle`), `sub_mode`, `rating_enabled`, `option{total_player_count, dropout_rank}` | + **`course_id`**, **`clutch_id`** | extra keys are ignored by `referee.rs` |
| `@@RefereeReport/<uuid>/User/<ussid>` (`0x1e3865c`/`0x1e393dc` -> `0x20f8a50`/`0x20f8d10`) | `result` | `result` always; + **`result_order`** when the caller's flag (w3 bit 0) is set and the list is not empty | `referee.rs` reads `result` only; when the flag is set is **?** |
| Rating doc (`BulkGetDocuments`, reader `0x49bc5c` -> `0x6f4a30`) | `Rating` | one branch (`0x6f4dc8..0x6f4e3c`) reads **`RaceCount`**, **`RatingDisplay`** (ints), `Rating`, **`RatingCoefficient`** (floats), `DriverId`, `StickerId`, all unguarded | `msgs.rs:273-277` sends `{Rating: 1500.0}`; a missing `RatingCoefficient` reads 0.0 |
| `@@KeepAlive`, `DPIS`, `DPID`, `Global`, `Session`, `short_session_id`, `global_time_offset` | present | present | |
| `@@drppr_ga_scopes/%s/@@drppr_ga_users/%s/@@drppr_ga_profiles/Data` | present | + `@@drppr_ga_scopes/clutch/@@drppr_ga_users/%s/@@drppr_ga_profiles/%s` | Clutch profiles |

New spectating vocabulary in 1.8.0 only: `is_spectator`, `spectating_bitmap`, `is_for_to_spectating`,
`AINetStationSpectatorSwitchHandler`, `user_session_count`, `AIGlobalSeqPrivateRoomRematch`,
`AIGlobalSeqChangeSession`, `OnlineMenu_FriendJoinable`, `FriendNumInRace`.

### 2.3 Matchmaking ticket and session attributes  **V**
Builder 1.0.0 `0x1e3fa98` -> 1.8.0 `0x20ffc50`.

| Attribute | 1.0.0 | 1.8.0 |
|---|---|---|
| `rom_id` (player, and session on create/query) | `"cv%d"`, N = u16 at `[online manager + 0x78]` (`0x261f56c`); a capture shows `cv1` | same code (`0x297b7b0`, manager pointer at `0x37c9828`); **the value of N in 1.8.0 is ?** (the field is 0 in the constructor `0x294a580` and filled at run time) |
| `batching_key` | `rom_id` + `_world%u` / `_attr%d` | + `_clutch%s` (clutch id string) and `_prc` |
| `course_id` | int | same |
| **`npln_user_id`** (player) | - | new: string from the per-local-player table (`[mgr+0x12b8] + i*0xd8`) |
| **`clutch_id`** (player) | - | new: clutch id string, empty when not in a clutch |
| `user_name` (session) | same | same |
| player attribute count | 3 | **5** (`mov w2,#5` at `0x20ffff4`) |

The lobby echoes the client's serialized `Player` (`lobbies.rs:102`), so new attributes pass through.

### 2.4 Pia 7.3
- Header magic `0x32AB9864` unchanged; **version byte 0x0F -> 0x10** (section 3.1) **V**.
- NAT probe: same code shape (kinds 101..103 `0x16268c4` -> `0x178dc90`; four big-endian words `0x1624314` ->
  `0x178c150`/`0x178c270`; senders `0x178dfb0`). `NatCheckJob` is now the template `NatCheckJob<TNatChecker>`.
  `rust/gateway/src/nat.rs` should still answer; confirm (T2).
- TURN: STUN magic `0x2112A442` is still built in two functions (`0x163abbc`, `0x163da94` -> `0x17a3540`,
  `0x17a65e0`); `nn::pia::turn::TurnProtocol`, `TurnJob`, `WanRelayClient`, `NplndRelayClient`,
  `IceServerConfigGetter` RTTI unchanged. But the `cricket::StunMessage` / `TurnMessage` / `Stun*Attribute` RTTI of
  1.0.0 is gone and two results are new: `ResultTurnSignalingServerProcessTimeout`,
  `ResultTurnJoinSignalingServerProcessTimeout`. The STUN/TURN client was reworked; whether
  `rust/gateway/src/turn.rs` (RFC 5766 subset) still satisfies it is **?** (T2).
- New jobs: `PingJob` (B1), `NetSearchNetworkBySessionIdJob`, `NetCheckDevidedNetworkJob`,
  `NetHostMigrationJob::CheckExistSameNetwork`, `LdnBackgroundProcessJob::DestroySameNetwork`;
  `LeaveMeshWithHostMigrationJob` is gone; `Update*NetworkSetting` renamed `...NetworkProperty`. Host migration and
  split-mesh detection changed: retest leave / host-leave (T5).
- `nn::ldn::SetProtocol` is new (LDN only; not on the online path).

### 2.5 Other new imports (not on the lobby path, listed so nothing is a surprise)
- `nn::chat::*` (30 functions, GameChat) and camera: mkw-recomp answers them itself (`runtime/sdk/ov/ov_chat.c`);
  the probe needs nothing unless the voice feature wants to hook `nn::chat::IsChatting` later.
- `nnsslConnectionGetServerCertDetail`, `nnsslConnectionSetServerCertBuffer`: both called from one function
  (`0x1631fcc`, libcurl's nnssl backend, `nnssl_connect_common`). The NPLN gRPC channel does not go through nnssl,
  so the probe's certificate path is not affected; only libcurl HTTPS (attachments, not used by the lobby) is.
- `nn::prepo::PlayReport::~PlayReport`, `prepo::Array::Add(Any64BitId)`: play reports, no network redirect needed.

## 3. Pia protocol version (why the versions cannot mix)

### 3.1 Evidence  **V**
| | 1.0.0 | 1.8.0 |
|---|---|---|
| header writer | `0x151ebd8` / `0x151ecc0`: `[+8] = 0x32AB9864`, `[+0xc] = 0x0F` | `0x165e7e0`: `[+8] = 0x32AB9864`, `[+0xc] = 0x10` |
| receive check | `0x151ed74`, `0x151f05c`, `0x151f310`: `cmp magic; (byte[+0xc] & 0x7f) == 0x0F` else drop | `0x165ec10`, `0x165eef0`, `0x1672580`: `(byte[+0xc] & 0x7f) == 0x10` else drop |

### 3.2 What the game tells the lobby about its version
- `rom_id = "cv<N>"` in every matchmaking Player and in friend-room session attributes (section 2.3). `cv1` for
  1.0.0; N for 1.8.0 is **?** until one capture. If N is bumped this is the natural key (real NPLN batches on
  `batching_key`, which starts with it).
- gRPC `user-agent` very likely carries the NPLN version, `1.33.0` vs `1.47.0` (**I**, section 2.1): available on
  every RPC including sign-in, before any ticket exists.
- Player attribute count 3 vs 5 and the presence of `npln_user_id` (section 2.3) are a sure fallback.
- Not different: tenant, hosts, config names (`Normal24`, `Battle24`, `Private24`, `Survival24`), program id.

## 4. SAFE (checked, no change needed)

- **Every name in `kRegs`** (`npln.c:1598-1668`) is still imported by 1.8.0 main and exported by its sdk
  (`probe_names.py`). `InitializeFiber` and `pthread_cond_clockwait` are imported by main in neither build (as
  before).
- `nn::os::CreateSystemEvent` / `SignalSystemEvent` are **no longer imported by main** (now only by `subsdk0`) but
  are still exported by sdk, which is what `g_rt.ldr_symbol` needs (`npln.c:343-358`). Works as long as
  `ldr_symbol` resolves exports, not main's import table; keep an eye on it in T1.
- nnsocket C API set (gRPC/OpenSSL transport), `nn::ssl::*`, `nnsslInitialize`, `nn::account::*`, `nn::friends::*`,
  `nn::nsd::*`, `nn::time::AsyncEnsureNetworkClockAvailabilityTask::*`, `nn::err::ShowError`, `nn::fs::*` used by
  the probe: identical mangled names, no new overloads besides the nifm one (B1).
- TLS: same embedded gRPC 1.38.0 (`NintendoSDK_OpenSSL_For_NPLN-0_0_0` and `gRPC_For_NPLN-0_0_0` tags in both;
  neither build carries an `OpenSSL x.y.z` version string), the `nn::ssl::BuiltInManager::GetBuiltInCertificates` /
  `GetBuiltInCertificateBufSize` / `Context::ImportServerPki` imports the probe overrides are unchanged, and the
  only new pinning-related string (`pPinnedPubKey: %s`) is libcurl's. `lobby_ca.c` / `capture_cert.h` need no
  change by this evidence; T1 confirms the handshake.
- `nn::friends::Friend` layout (sdk 21.4.0): constructor clears 0x200 bytes, `GetAccountId` = `+0x10`,
  `GetNickname` = `+0x18`, `IsValid` = `+0x128` (sdk `0x450fa0..0x4510e0`), same as 19.3.3: `friends.c:11-16`
  stay true.
- `nn::account::GetNickname(Nickname*, Uid const&)`, `GetNetworkServiceAccountId`, `LoadNetworkServiceAccount
  IdTokenCache(size_t*, char*, size_t, UserHandle const&)`: same signatures.
- `nn::swkbd`: same eight imports. The `REQ_*` offsets in `keypad.c:30-36` describe mkw-recomp's request struct,
  not an SDK struct; they depend on the recomp release, not on the game version.
- Tenant `t-e96531c6`, `tenants/current/...` resource names, `matchmakingTickets`, `gameSessions`,
  `gameSessionCreationTickets`, `GameSessionShortAliases`: unchanged.
- `rule` session attribute: same four readers, same default 3 (section 6 item 2).

## 5. Keeping 1.0.0 and 1.8.0 apart in the lobby

Recommended, in this order:
1. **Now, without waiting for a capture**: derive a version key from the ticket's Player attributes in
   `msgs.rs` `parse_ticket_request` (`msgs.rs:75-84`): `rom_id` string, plus `"+npln_user_id"` when that attribute
   exists. Add it to `Ticket` (`lobbies.rs:102`) and to the filter at `lobbies.rs:398`
   (`l.mode == ticket.mode && l.version == ticket.version`); store it on `Lobby::new` (`lobbies.rs:403`).
2. Friend rooms: store the creator's key on the room (`msgs.rs:211-220`, session attributes field 11 carry
   `rom_id`) and refuse `JoinGameSession` / Room-ID lookups from another key with the same answer as "no room with
   this id". Hide other-version friends' rooms in presence (`social.rs:44,57`).
3. After T1 confirms the `user-agent`: pass it from `rust/gateway/src/h2conn.rs` into `Call` and record the version
   at sign-in, so every later RPC of the connection is tagged and the seat API can show it.
4. Launcher: the game version is in `<game data dir>/setup-state.json`, `"generate": "<game id> 1.8.0 <hash>"`
   (also `"port": "0.1.13"`). Send it with the seat request so the roster and the lobby list can show and filter
   by it. `rust/client/src/recomp.rs:14` lists only 0.1.10 and 0.1.12; a 1.8.0 setup made by 0.1.13 is refused
   today.

## 6. Server checklist (Rust lobby owner's list)

| # | Item | Verdict | Where in 1.8.0 |
|---|---|---|---|
| 1 | `msgs.rs:19` default tenant `tenants/t-e96531c6-lp1` | **unchanged**: `t-e96531c6` and the format `t-%s-%s` are the only tenant strings, same as 1.0.0 | strings `0x2bff02e`, `0x2c06f68`; header `npln-tenant-id` set in `0xf6fb30` / `0xf6fbb0` |
| 2 | `core.rs:1452-1466` room attribute `rule` = 3 | **unchanged**: four references, all readers (`0x20f4dc8`, `0x20feec0`, `0x20ff2a0`, and the `__gs/m` reader inside `0x6f6f80` at `0x6f85a0`); no session info -> 3; the game still never writes it (create attrs are `rom_id`, `course_id`, `user_name`). New: the friend-entry getter turns values outside -1..7 into 0 (`0x20f4de4`), and mode 7 = clutch exists. "3 = group / course vote" unchanged is **I** (enum values 1, 2, 5, 6 kept their meaning) | 1.0.0 `0x1e36acc`, `0x1e3f0d0`, `0x1e3f370`, `0x498a80` |
| 3 | `core.rs:1441-1450` user session doc `uid`, `ussid`, `ucsid`, `upcsid`, `pgn`, `st` = 2 | **changed (additive)**: all six still read; SDK parser also reads `pusa`. **unknown**: whether a `__us` doc without `pusa` matters. A capture of a client `__pus` write shows what `pusa` holds; if it is per-user state, copy the member's `pusa` into the synthesized doc | `0x1386fe0` (read), `0x1387160` (write), state parser `0x1394cc0` |
| 4 | `core.rs:1469-1477` `__gs/f` keys `gsid`, `addr`, `p`, `mcn`, `maxu`, `rs` | **unchanged** (same six reads in the same order; the third key is the one-character string at `0x2c078f5`) | `0x13622c0` |
| 5 | `msgs.rs:273-277` bulk_get `{"Rating": 1500.0}` | **changed**: `Rating` still read (three sites in `0x6f4a30`); one branch also reads `RaceCount`, `RatingDisplay`, `RatingCoefficient`, `DriverId`, `StickerId`. **unknown**: which document name that branch serves and the sane value of `RatingCoefficient`; capture the `BulkGetDocuments` names the 1.8.0 client asks for. Until then add `RatingCoefficient: 1.0`, `RatingDisplay: 1500`, `RaceCount: 0` | `0x6f4dc8..0x6f4e3c` |
| 6 | `referee.rs:21-27, 79-84, 131-180` | **changed (additive)**: paths `@@RefereeRequest` / `@@RefereeResult` / `@@RefereeReport`, parts `Request` / `User`, keys `request_id`, `status`, `result`, `ratings`, `option.total_player_count`, `option.dropout_rank`, `users`, `ussid_map`, `mode`, `sub_mode`, `rating_enabled` all still there. New in the request: `course_id`, `clutch_id`. New in the report: `result_order`. New in the result reader: `user_session_count`, `coefficients`, `mp`, `inet_param{...}` (B4). **unknown**: effect of `user_session_count` = 0 and when `result_order` is sent | request `0x20f7250`, report `0x20f8a50` / `0x20f8d10`, result reader `0x6f9670..0x6f9a1c` |
| 7 | `core.rs:22+` RPC paths | **unchanged for all 23 served paths** (each present in both builds with one stub reference); no `v2`. Six new paths, none served: section 2.1; of those only Appconfig is known to be instantiated by game code | `rpcs.py` output |
| 8 | `social.rs:44,57` presence attribute `SessionId` | **unchanged**: string present, six readers in 1.0.0, seven in 1.8.0 (`0x255e340`, `0x255e520`, `0x255ebf0`, `0x255f640`, `0x255fba0`, `0x255fc60`, `0x297c7c0`); `DriverId` likewise (13 -> 15 references) | |
| 9 | Lobby split | section 5. A 1.8.0 client sends, and a 1.0.0 client does not: Player attributes `npln_user_id` and `clutch_id` (**V**), very likely another `user-agent` (**I**), possibly another `rom_id` (**?**). Same in both: tenant, config names, program id | `0x20ffc50` |

## 7. Required changes, in order

Probe (`native/probe/src/npln.c`):
1. `npln.c:1600`: add `{"_ZN2nn4nifm22GetCurrentIpConfigInfoEPNS_6socket6InAddrES3_S3_S3_S3_", ov_nifm_ip_config}`.
2. `npln.c:1645`: add `{"_ZN2nn6socket4RecvEiPvmNS0_7MsgFlagE", ...}` on a `recv` twin of `ov_pia_recvfrom`
   (`npln.c:963`), using the probe's fd table.
3. `npln.c:818-833` `ov_pia_socket`: handle family 2 / type 3 / protocol 1 (ICMP) deliberately (real raw socket,
   or a datagram stub that never receives) instead of the blanket `EINVAL`; pick after T2.
4. Make every registration version-aware only where needed: all other names are common to both builds, so one probe
   binary can serve both (the guest-address tables are the other agent's subject).

Rust lobby:
5. `lobbies.rs:102, 398, 403` + `msgs.rs:75-84`: version key on tickets and lobbies (section 5.1).
6. `msgs.rs:211-220`, `core.rs` room join / alias get, `social.rs:44-57`: version key on friend rooms (5.2).
7. `referee.rs:131-180`: add `user_session_count` to the final result; keep ignoring `course_id`, `clutch_id`,
   `result_order`.
8. `msgs.rs:273-277`: add `RatingCoefficient`, `RatingDisplay`, `RaceCount`.
9. `core.rs:22-45`: handler (empty reply) for `/nn.npln.appconfig.v1.Appconfig/FindApplicationConfig` and
   `/PullModifiedApplicationConfig` if T1 shows the game waits on them.
10. `lobbies.rs:28` / `[lobby.policy]`: decide `Clutch24` / `ClutchSurvival24` (refuse, or capacity entry).
11. `rust/gateway/src/h2conn.rs`: expose `user-agent` to the Core (5.3).
12. `rust/gateway/src/turn.rs`, `nat.rs`: only if T2 fails.

Python lobby (`resources/python-lobby/mkwmp_server.py`, RE testbed): same points 7-9 if it is used for 1.8.0
captures; `pia_capture.py` / `turn_relay.py` are the quickest place to log what Pia 7.3 sends.

Launcher (`rust/client`):
13. `recomp.rs:14-23`: accept the mkw-recomp release that builds 1.8.0 (setup-state says port 0.1.13).
14. Read the game version from `setup-state.json` (`"generate"`), send it with the seat, pick the matching probe
    build / address table.

Live tests, in order:
- **T1** one 1.8.0 client, sign-in to the Online menu with full gRPC logging: `user-agent`, any `Appconfig` call,
  sign-in request shape, `BulkGetDocuments` names, no unresolved-import abort (B1), `ldr_symbol` events work.
- **T2** two 1.8.0 clients into one `Normal24` lobby: `rom_id` value, NAT probes answered, TURN allocation,
  mesh forms, ping socket behaviour.
- **T3** full race: `@@RefereeRequest` (new keys), start within 36 s, reports, result accepted.
- **T4** race end with and without `user_session_count` / `coefficients` in the result; ratings screen; return to
  the lobby; next vote.
- **T5** leave and host-leave mid-lobby and mid-race (host migration was reworked).
- **T6** friend room create / join by Room ID / join by friend card (`rule` = 3), rematch
  (`AIGlobalSeqPrivateRoomRematch`).
- **T7** battle (`Battle24`), then `Survival24`; Clutch menu entry behaviour.
- **T8** one 1.0.0 and one 1.8.0 client on the same server: they must never be placed together, and neither sees
  the other's room.

## 8. Scripts (`tools/re/game180/proto/`)

Run with `.venv\Scripts\python.exe -I <script>`; each adds `.deps` itself and reuses
`resources/tools/re/nsodump.py` (`Nso`).

| Script | Use |
|---|---|
| `dump.py <exefs> <out>` | decompress every NSO: `<mod>.bin`, imports, exports, strings, `info.txt` (build ids, module names, SDK version strings) |
| `probe_names.py <d100> <d180>` | every SDK name in the probe sources against both builds' import/export tables, plus same-name-other-signature candidates |
| `plt.py <exefs>/main <dump>/main.bin <regex>` | callers of imported functions through the PLT |
| `strdiff.py <d100> <d180> <mod> <regex> [--all] [-i]` | string-set diff of a module, filtered |
| `keystrings.py <d100> <d180> [literal ...]` | presence of the protocol literals our servers hard-code |
| `rx.py <image> refs|dis|func|strs|callers|grep|sym ...` | string xrefs (cached index), annotated disassembly |
| `rpcs.py <d100> <d180>` | NPLN method paths, new ones, which the Rust lobby serves |
| `commver.py <image>` | candidate writers of the `cv%d` number (inconclusive: it is not a constant store) |
| `pbshapes.py` | serializer tag shapes of a code range and a multiset diff (noisy; a hint, not proof) |

## 9. Comm error 0x884b839c (T2, 2026-11-18)

Static only (no game run). Addresses are 1.8.0 `main` offsets unless marked. **V** = read in the binary.

### 9.1 Symptom
Two 1.8.0 clients matched into one `Normal24` lobby. About 280 ms after the Gamesync connect, both post
`0x884b839c` (reason 0xc, kind 0x25): caller `0x297d91c`, frames `0x2981090`, `0x6f6e60`, `0x6f6ee4`. The clients
half-close `KeepUserSession` at that moment; no SessionJoin, no Pia.

### 9.2 Raiser **V**
- `0x297d8c0` / `0x2981050` are the generic poster (`0x2981050` = 1.0.0 `FUN_02624ad8`, same instruction bag).
- The id is built at exactly one site, `0x6f6e4c..0x6f6e5c`, in the function `0x6f6d10`. It is absent from 1.0.0
  (no mov/movk pair), and `0x6f6d10`, `0x20fa450`, `0x20ff7b0`, `0x256d5a0` have no 1.0.0 counterpart: **the
  check is new in 1.8.0**.
- `0x6f6d10` is called every frame by the net-system update `0x6f6e70` (at `0x6f6ee0`) on the 0x18-byte object at
  `[net system + 0xc8]` (created in `0xbf4720` at `0xbf48b4`, vtable `0x35afaf0`, getter `0x2108560`). Fields:
  `+8` local player index, `+0xd` armed, `+0xe` "session joined", `+0xf` "error when no seat", `+0x10` "skip the
  seat check".

```
0x6f6d10(obj):
  ctx = netsys[0xa0 + idx*8]                      // per-local-player document context
  if ctx[0xc4c0] != 1 or slot handle[0] == null or obj[0xd] != 1: return
  if obj[0x10] == 0 and has_seat(obj) /*0x20fa450*/:
      if 0x297af60(match controller, ""): obj[0xd] = obj[0xe] = 0      // normal path
      return
  if ctx[0] (PlayerStatic collection)[0x128] /*0x20f5be0: listen in sync*/:
      obj[0xe] = 1
      if obj[0xf] == 1: post 0x884b839c; obj[0xd..0x10] = 0
```

- Who arms it: `0x20fa7e0(obj, &mode, w2, idx)` starts matchmaking (`0x297c7a0`, controller op 2) and sets
  `+0xd = 1`, `+0x10 = 0`, `+0xf = !w2`. `w2 = 0` only from `AIGlobalSeqAfterLoadingMapOuter` (`0x1c93140`, param
  `NetSeqType`): that is the matchmade path of the event. The other callers (`0x637544`, `0x63766c`, `w2 = 1`) and
  `0x20fa6e0` / `0x20fa760` (join a known session) leave `+0xf = 0`: with no seat they only get `+0xe = 1` and their
  own state machine (`0x6326fc`) handles it, so the comm error is specific to the matchmade entry.

### 9.3 The seat check `0x20fa450` **V**
```
has_seat(obj):
  need ctx[0xc4c0]==1 and handle[0] != null            (0x20f5b40)
       PlayerStatic collection in sync                   (0x20f5be0, ctx[0]+0x128)
       member collection in sync                         (0x20f5c00, [mgr slot+0x138]+0x128)
  n    = number of nodes of the member tree ctx+0x30 (keys = ussid), minus 1 with two local players
  X    = 0x20ff7b0 -> 0x256d5a0(mgr, -1) = u32 [session handle[0] + 0x80]
  cap  = 24, or 26 when controller mode != 3 and its target field is non-zero
  thr  = min(X, cap)
  if X < 1: return false                                 (0x20fa5ec)
  if n < thr: return true
  return own key != 0 and own key <= (thr-th smallest key)
```
"In sync" is the byte the collection's listen callback sets (`0x258ce20` registers `0x258f040`: `*(coll+0x1a0) = 1`,
copied to `+0x128` each frame by `0x258cb30`); the lobby's `OP_CURRENT` satisfies it.

**X is the max player count of the GameSession in the matchmaking reply:**
- session handle = SDK UserSession. Its factory `0x1364830` copies 16 bytes `[P+0x48]` to `[session+0x78]`
  (`0x1364b90..0x1364ba0`); `P` is built by `0x14a46a0` from the converted GameSession (`0x14e2bf0`:
  `ldp x4, x5, [gs+0x48]`).
- Converter `0x14f93c0` (1.0.0 `0x13dbf00`): `ldpsw x10, x9, [msg+0x50]` ... `stp x9, x10, [gs+0x48]`, so
  `gs+0x48` = msg `+0x54`, `gs+0x50` = msg `+0x50`. Serializer `0x152c460` writes msg `+0x50` with tag 0x10 and
  `+0x54` with tag 0x18.
- So `session+0x80` = **GameSession field 2** and `session+0x78` = **GameSession field 3**. Field 2 / field 3 are
  max / current players (live-verified on the friend card, `msgs.rs:176`). 1.0.0 already read field 3 through the
  handle (`FUN_01e46888`, `handle+0x58` minus `__gs/r.dpc`; 1.8.0 `0x21085d0`, `handle+0x78`); the read of field 2
  is new.

### 9.4 Root cause
`msgs::assignment` (`rust/lobby/src/msgs.rs:139`, the `TrackMatchmakingTicket` reply) builds the GameSession with
fields 1, 8, 9, 10, 12 only. Field 2 is absent, X = 0, `has_seat` is false, and the first frame after the
PlayerStatic listen is in sync (which is when the client has just written PlayerStatic / Companion, hence the
apparent link with the Companion echo) posts `0x884b839c`. Both clients fail regardless of ussid, as observed
(with X >= 1 at least the lower ussid would pass).

Not involved (the raiser reads none of them): Companion fields (`my_follow_ussid`, `my_sub_ussid`,
`companion_state`, `members`, ...), the self-echo of client writes, `pusa` in `__us` (SDK-side: parsed into the
member struct `+0x78` by `0x1386fe0`, and written by the `__pus` builder `0x137cee0` from the join parameters),
`__gs/f.maxu`, Swarm / DPID / Global documents. Friend rooms are not affected by this bug: `msgs::game_session`
already sends fields 2 and 3.

### 9.5 Fix
Server, `rust/lobby/src/msgs.rs` `assignment()` (and its caller `core.rs:890`, which has the lobby): add to the
GameSession (reply field 7)
- **field 2 (varint) = lobby capacity** (`lobby.maxu(policy)`, 24 for `Normal24`; must be >= the number of members
  or the highest ussids are refused), and
- **field 3 (varint) = current member count** (the client subtracts `__gs/r.dpc` from it; 1.0.0 ran with 0, so it is
  for consistency with `game_session()`).
Same for `match_assignment` in `resources/python-lobby/mkwmp_server.py:241` if it is used with 1.8.0. No probe
change is needed. `MKWMP_SKIP_ERROR=884b839c` is not a workaround: it drops the dialog but the "has seat" branch
(`0x297af60`, controller op 3) still never runs.

Not proven statically: that nothing else blocks after the seat check passes (next step is `0x297af60`: controller
state 2, `+0x4d4 == 0`, `+0x48c == 0`, then op 3), and the exact meaning of the 26 cap.
