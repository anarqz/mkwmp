# Contract status

One row per RPC the game binary knows (inventory: [rpcs.tsv](rpcs.tsv)). Workflow and the meaning of the Status column: [WORKFLOW.md](WORKFLOW.md).

Contract column:

| Value | Meaning |
|---|---|
| done | Messages written from the serializers, and the RPC has been exercised in-game with them |
| shape | Messages written from the serializers; not exercised in-game, or only with an empty reply |
| none | No contract |

Contracts: `contracts/nn/npln/<service>/v1/*.proto`. Shared value types (Value, ArrayValue, MapValue, Status) are in `contracts/nn/npln/gamesync/v1/values.proto`. `rust/contracts/src/lib.rs` does not yet include the new packages `friends`, `leaderboard` and `maintenance`.

| Service | Method | Type | Contract | Status | Notes |
|---|---|---|---|---|---|
| auth.v1.Auth | CreateUser | unary | none | todo |  |
| auth.v1.Auth | DeleteUser | unary | none | todo |  |
| auth.v1.Auth | IssueAnonymousUserToken | unary | none | todo |  |
| auth.v1.Auth | IssuePrearrangedUserToken | unary | done | live |  |
| auth.v1.Auth | IssueToken | unary | none | todo |  |
| auth.v1.Auth | ListUsers | unary | none | todo |  |
| auth.v1.Auth | RefreshAnonymousUserToken | unary | none | todo |  |
| auth.v1.Auth | RefreshToken | unary | none | todo |  |
| auth.v1.Auth | ValidateToken | unary | none | todo |  |
| friends.v1.Friends | ActivateUser | unary | done | live | Empty reply accepted. |
| friends.v1.Friends | ListBlockingUsers | unary | none | todo | No caller and no message classes in the binary. |
| friends.v1.Friends | ListFriendUsers | unary | none | todo | No caller and no message classes in the binary. |
| friends.v1.Friends | SubscribeFriendUsers | server_stream | shape | stub-ok | Reference server sends one empty message; friend list stays empty. Full response shape written, never sent live. |
| friends.v1.PresenceService | KeepAlive | bidi | shape | stub-ok | Only the client `open` frame captured. The client waits for `ack` {1: {Duration, Duration}}; never sent live. |
| friends.v1.PresenceService | SubscribePresences | server_stream | shape | stub-ok | Request captured. Presence push never sent live. |
| gamesync.v1.Gamesync | BeginTransaction | unary | none | todo | No caller and no message classes in the binary. |
| gamesync.v1.Gamesync | CommitTransaction | unary | none | todo | No caller and no message classes in the binary. |
| gamesync.v1.Gamesync | CreateRound | unary | none | todo | No caller and no message classes in the binary. |
| gamesync.v1.Gamesync | GetDocument | unary | done | live | Reply is the Document itself. |
| gamesync.v1.Gamesync | IssueToken | unary | done | live |  |
| gamesync.v1.Gamesync | KeepUserSession | bidi | done | live | Add/remove target, Change ADDED/MODIFIED, Op CURRENT/SUCCEEDED live. Change REMOVED and Op FAILED read in the client reader, not yet sent live. |
| gamesync.v1.Gamesync | ListDocuments | unary | none | todo | No caller and no message classes in the binary. |
| gamesync.v1.Gamesync | QueryCollectionIds | unary | none | todo | No caller and no message classes in the binary. |
| gamesync.v1.Gamesync | ReadDocuments | unary | none | todo | No caller and no message classes in the binary. |
| gamesync.v1.Gamesync | RefreshToken | unary | shape | static | Same messages as IssueToken; not captured. |
| gamesync.v1.Gamesync | RollbackTransaction | unary | none | todo | No caller and no message classes in the binary. |
| gamesync.v1.Gamesync | WriteDocuments | unary | done | live | Update, Transform, Delete and deferments captured; Write.set, Precondition.update_time and DefermentOp.remove never observed. |
| leaderboard.v1.LeaderboardService | BatchGetCharts | unary | none | todo |  |
| leaderboard.v1.LeaderboardService | BrowseLeaderboard | unary | none | todo |  |
| leaderboard.v1.LeaderboardService | CreateHolder | unary | none | todo |  |
| leaderboard.v1.LeaderboardService | DeleteHolder | unary | none | todo |  |
| leaderboard.v1.LeaderboardService | DeleteScore | unary | none | todo |  |
| leaderboard.v1.LeaderboardService | FetchRankScore | unary | none | todo |  |
| leaderboard.v1.LeaderboardService | FindRankScores | unary | none | todo |  |
| leaderboard.v1.LeaderboardService | GetCategory | unary | none | todo |  |
| leaderboard.v1.LeaderboardService | GetChart | unary | none | todo |  |
| leaderboard.v1.LeaderboardService | GetHolder | unary | none | todo |  |
| leaderboard.v1.LeaderboardService | GetScore | unary | none | todo |  |
| leaderboard.v1.LeaderboardService | GetSeason | unary | none | todo |  |
| leaderboard.v1.LeaderboardService | PurgeScoreHolders | unary | none | todo |  |
| leaderboard.v1.LeaderboardService | PurgeUserScores | unary | none | todo |  |
| leaderboard.v1.LeaderboardService | RegisterScore | unary | none | todo |  |
| leaderboard.v1.LeaderboardService | RetrieveQuantileScores | unary | none | todo |  |
| leaderboard.v1.LeaderboardService | SearchHolders | unary | none | todo |  |
| leaderboard.v1.LeaderboardService | UpdateHolder | unary | shape | stub-ok | Request captured (names `display_name`, `fields` from its field mask). Reply class not identified; an empty reply is accepted. |
| maintenance.v1.MaintenanceScheduleService | SubscribeMaintenanceSchedules | server_stream | shape | stub-ok | Request captured. One empty message accepted; schedule fields unnamed. |
| matchmaking.v1.GameSessionService | AllocateIceServerSet | unary | done | live |  |
| matchmaking.v1.GameSessionService | BatchGetGameSessions | unary | shape | static | Friend rooms. |
| matchmaking.v1.GameSessionService | CancelGameSessionCreationTicket | unary | shape | static | Reply class not identified (Empty inferred). |
| matchmaking.v1.GameSessionService | CreateGameSessionCreationTicket | unary | shape | static | Private24 rooms; password in GameSession field 6. |
| matchmaking.v1.GameSessionService | CreateGameSessionShortAlias | unary | shape | static | Room ID. |
| matchmaking.v1.GameSessionService | GetGameSession | unary | shape | static |  |
| matchmaking.v1.GameSessionService | GetGameSessionShortAlias | unary | shape | static | Room ID lookup. |
| matchmaking.v1.GameSessionService | GetUserSession | unary | none | todo | No caller and no message classes in the binary. |
| matchmaking.v1.GameSessionService | IssueMatchmakingIdToken | unary | shape | static |  |
| matchmaking.v1.GameSessionService | IssueUserDelegationToken | unary | shape | static | Packed enum fields taken from resources/docs/RE-MAP.md. |
| matchmaking.v1.GameSessionService | JoinGameSession | unary | shape | static |  |
| matchmaking.v1.GameSessionService | ListLatencyMeasurementServers | unary | shape | stub-ok | Request captured; empty reply accepted. Server entry fields unnamed. |
| matchmaking.v1.GameSessionService | ListUserSessions | unary | none | todo | No caller and no message classes in the binary. |
| matchmaking.v1.GameSessionService | QueryGameSessions | unary | shape | static |  |
| matchmaking.v1.GameSessionService | TrackGameSessionCreationTicket | server_stream | shape | static | States from the client consumer: 2 created, 3 cancelled, 4 timed out, 5 failed. |
| matchmaking.v1.Matchmaker | CancelMatchmakingTicket | unary | shape | static | Request {1 name}; the reference server handles it (`cancel_ticket`). Reply class not identified (Empty inferred). |
| matchmaking.v1.Matchmaker | CreateAcceptance | unary | shape | static | Only sent after ticket state 7. Reply class not identified (Empty inferred). |
| matchmaking.v1.Matchmaker | CreateMatchmakingTicket | unary | done | live |  |
| matchmaking.v1.Matchmaker | TrackMatchmakingTicket | server_stream | done | live | State 3 (matched) live. States 4-8 are from the client consumer; not confirmed in-game here. |
| ugcstore.v1.Ugcstore | BulkGetDocuments | server_stream | done | live |  |
| ugcstore.v1.Ugcstore | BulkIssueAttachmentUri | unary | none | todo |  |
| ugcstore.v1.Ugcstore | CommitDocuments | unary | none | todo |  |
| ugcstore.v1.Ugcstore | CreateDocumentShortAlias | unary | none | todo |  |
| ugcstore.v1.Ugcstore | GetDocument | unary | none | todo |  |
| ugcstore.v1.Ugcstore | GetDocumentShortAlias | unary | none | todo |  |
| ugcstore.v1.Ugcstore | IssueAttachmentUri | unary | none | todo |  |
| ugcstore.v1.Ugcstore | IssueUploadUri | unary | none | todo |  |
| ugcstore.v1.Ugcstore | RunQuery | server_stream | none | todo |  |

## Corrections made to earlier contracts

- Gamesync `Value` field 6 is a Timestamp, not a struct. Nested maps are field 10 (`MapValue`), lists field 9 (`ArrayValue`). `Document.fields` is a `MapValue`.
- Gamesync `KeepUserSession` uses two message types: the request carries add-target (3) and remove-target (4); the response is a oneof of Change (2) and Op (3).
- Change kind 3 is REMOVED. Op kinds are 1 succeeded, 2 current, 4 failed.
- Matchmaking `Member`: field 1 is the user-session name, field 2 the user.
- Matchmaking `Player` / `Member` field 3 is a message holding a map of string to a seconds/nanos time value, not `map<string, string>`.
- `MatchmakingTicket` field 3 is the failure cause; field 8 is a google.rpc.Status.
- Ugcstore `Document.fields` is a `MapValue` message (field 2), not a map placed directly on field 2.
