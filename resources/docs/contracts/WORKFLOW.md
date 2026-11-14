# Contract translation workflow

Goal: every RPC the game uses gets a written contract, a server handler and a test. Track progress in [STATUS.md](STATUS.md); the raw inventory is [rpcs.tsv](rpcs.tsv).

## Status values

| Status | Meaning |
|---|---|
| todo | Not observed, not analysed |
| static | Schema recovered from code, not yet seen live |
| stub-ok | Game accepts an empty/minimal reply; schema not needed yet |
| next | On the critical path to a race |
| partial | Live, reply accepted, some fields still unknown |
| live | Request and reply verified in-game |

## Steps (one RPC at a time)

1. **Pick** — the first `next`, else the first RPC the client calls that fails. Only RPCs on the race path matter; leaderboard and UGC upload stay `todo`.
2. **Capture** — run both clients with `MKWMP_TRACE_EDGES=1`. Record the request bytes from the server log and the failure string from `sdk-msg` lines.
3. **Recover** — find the reply serializer and the consumer branch that rejects it (NETCODE.md has the method). Note field numbers, types and which ones the client checks.
4. **Write the contract** — add the messages to `contracts/<service>.proto` (proto3, field numbers from the binary, names inferred and marked `// inferred`).
5. **Implement** — handler in the prototype server (`server/`), one function per RPC, encoding from the contract.
6. **Test** — unit test with the captured request; then one live run.
7. **Record** — update STATUS.md, add one line to the NETCODE.md experiment log, commit.

## Rules

- One RPC per commit. Message: what was verified and how.
- Never mark `live` without an in-game run.
- Field numbers come from the binary, never guessed. Unknown fields stay out of the reply.
- The `.proto` files are the source of truth for the Rust services (`prost`/`tonic`).
