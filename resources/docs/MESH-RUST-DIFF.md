# Why the Pia mesh does not form with the Rust lobby (2026-11-14)

Evidence: `scratch/mesh-fail-2020/` (lobby, PC and Deck logs). Decompiles and scan scripts made for this:
`scratch/mesh-tools/`. Addresses are image offsets in `scratch/main.bin`. V = read in code or a log, I = inferred.

## Answers

1. **Port 34343 is a red herring.** It is hardcoded in Pia's monitoring-server resolver (`mov w9,#0x8627` at
   `0x151308c`, next to the `g2122d301.%.p.srv.nintendo.net` lookup). The probe's fake DNS answers with the lobby
   host, so the packet goes to `<lobby ip>:34343`. The same 308/340-byte packets are in the working Python-era logs
   (`scratch/p1-crash-1604.log`, `scratch/game-p2/logs/mkw.4.log`), unanswered. No document or RPC supplies it. V
2. **2318-0540 = Pia result `0x647e`** (table `FUN_0151c96c`, entry at `0x151d7dc`; nn::Result conversion
   `FUN_0151e420`). Posted with reason 11 by the per-frame Pia dispatch (`FUN_02255f78` -> `FUN_0222d4bc`). Two
   producers: a host's join denial (`FUN_0156c5c8`, reason byte 0xf; not our case, the Deck only received the three
   93-byte handshake packets) and `JoinMeshJob` `FUN_015778e0` at `0x1577c6c`: the joiner is not the host, but the
   **host's station id (`session+0x1a8`) equals its own id (`session+0x1e8`)**. V
3. **Who hosts is not `mcn`.** `FUN_0162a360` (nplnd) takes the **lowest `upcsid` among the `__pus` documents it
   has** (own value = the WriteDocuments transform result) as the mesh host; the member that is the minimum runs
   CreateMesh, everyone else JoinMesh (`FUN_01560414`). V

## Root causes, ranked

**H1 (first assignment, 2318-0540 on the Deck): both machines report the same account id.** I, strong.
`ov_nsa_get_id` (`native/probe/src/npln.c`) returned `0x1122334455667788 + g_second_player`, and
`g_second_player` is only set by `MKWMP_PLAYER=2` or `slot = 2`. The Python milestone ran the Deck as dev user
`...0002` with `MKWMP_PLAYER=2` (`run-lan11.log`: `userSessions/00000000-0000-4000-8000-000000000002`). With
launcher tickets both sides are slot 1 (Deck log: `MKWMP_PLAYER` unset; the launcher's `mkwmp.toml` has no `slot`),
so the joiner sees a host with its own id: exactly the `0x647e` condition. Not proven statically that the station
id is the NSA id; the zero-build test below settles it. This is a client problem: the Python lobby would fail the
same way with launcher users. I found no server difference for this case: with the PC (number 1) seated first, the
Rust documents, their order, `__gs/f`, the ICE answer and the write/transform replies match the Python code.

**H2 (second assignment and `host-first-test.log`: both sides rewrite a 773-byte `__stu` request every 1.25 s,
no UDP): the Rust lobby defers `__pus` listens by join order, but `upcsid` is the global player number.** V for
the mechanism and the log pattern. Deck (number 2) seated first: its `__pus` snapshot is empty, so it is the
minimum and hosts; the PC (number 1) gets the Deck in its snapshot, but 1 < 2, so it hosts too. Two hosts, both
initiate, the known deadlock. Python deferred by number (`lower_members_present`), so the lowest number always
listened first. Locations: `rust/lobby/src/core.rs` `earlier_members_present` (`join_seq`), `write_documents`
(`number_of` -> transform result), `user_session_document` (`upcsid`), `lobbies.rs` `add_seat` (`host` = first seat).

**Not causes.** `maxu` (only checked as an upper bound in `FUN_0162a360`). TURN: no allocation is logged in the
working `run-lan11.log` either; `turn.rs` and `nat.rs` match the Python. The `Presence` rate limit: the game sends
~220 KeepAlive frames in about a second after an assignment (in = out + 1, so each ack may trigger the next; the
Python server closed that stream at once and never acked), the stream ends with status 8 and the game reopens it;
on 23:17:09 it ended after the Deck had already failed. Worth fixing separately (drop instead of ending, or do not
ack heartbeats), not a mesh blocker.

Other Rust/Python differences seen, not implicated by these logs: Rust honours write preconditions (`exists`),
executes deletes and deferments, sends REMOVED on leave, and re-encodes values (timestamp nanos dropped).

## Next live test

1. **Zero-build check of H1:** start the Deck with `MKWMP_PLAYER=2` (or `slot = 2` in its `client.toml`); with a
   seat ticket this only changes the account id. Restart the lobby, sign in and search **PC first** (the first
   sign-in after a lobby restart gets number 1; until H2 is patched number 1 must also search first).
   Expect a 141-byte join packet after the 93-byte handshake instead of 2318-0540.
2. **Probe fix (built, not installed, not committed):** `native/probe/build/winmm.dll` (14/11 20:41). Changes in
   `npln.c`: the account id is `friends_account_id(name)` for launcher players (the id the lobby's friends code
   already assumes; logged once as `account id 0x...`), and the Pia lines now read
   `Pia UDP send <lport> -> <ip>:<port> (N bytes; guest fam F a.b.c.d:port)` / `recv <lport> <- <ip>:<port>`.
   Install on **both** machines (`build.py --install <game dir>`), then drop the `MKWMP_PLAYER` workaround.
3. **Lobby patch for H2 (proposed, not applied):**
   ```diff
   --- rust/lobby/src/core.rs  fn earlier_members_present
   -    lobby.seats.iter().filter(|s| s.join_seq < me.join_seq).all(|s| have.contains(&s.uid()))
   +    lobby.seats.iter().filter(|s| s.ussid < me.ussid).all(|s| have.contains(&s.uid()))
   ```
   This restores the Python rule (search order no longer matters for two players). Optionally set `mcn` to the
   lowest-`ussid` seat as Python did. Follow-up for late joiners and bigger lobbies: make `upcsid` a per-lobby
   join counter (transform result and the `__us`/`__pus` field = `seat.join_seq`) and keep join-order deferral;
   with global numbers a late joiner with a lower number than the host would elect itself.
