// Playing on Nextendo Network instead of an MKWMP server (docs/contracts/NEXTENDO_API.md, sections 4 and 5).
//
// mkwmp.toml says `backend = "nextendo"` (config.h). The launcher holds the player's Nextendo account; the probe asks
// it for the session (GET /local/v1/nextendo/session: the game token "nx2...", the account, the two hosts, the CA
// file) when Online Play is opened and again each time the game loads its account token. npln.c branches on the
// backend in the overrides it already has; what differs is here:
//   - the account token: a JWT that carries the nx2 in its "nnex" claim (the only claim Nextendo trusts);
//   - the account id, from the Nextendo pid;
//   - name resolution: the SDK's NAT-check names go to natHost, every other name of the console maker's domains to
//     serverHost, anything else (a gamesync host the game session names) to the machine's DNS, IP literals as they are.
// No seat, ticket, voice, camera or MKWMP friends in this mode. The nx2 token is never logged: its length and expiry
// only. Nor are the account's name and pid: mkwmp.log and the crash record go to MKWMP servers with a report.
#ifndef MKWMP_NEXTENDO_H
#define MKWMP_NEXTENDO_H

#include <stddef.h>
#include <stdint.h>

// ---- the launcher's answer (pure) -------------------------------------------------------------------------------
typedef enum {
    NX_READY = 0,      // 200 ready: use it
    NX_REFUSED,        // 409: stay offline (the launcher has shown the reason)
    NX_UNAVAILABLE,    // 503, no launcher, or an answer that cannot be used: stay offline
    NX_UNAUTHORIZED,   // 401 even after mkwmp.toml was read again (launcher.c does that once)
} NxState;

enum { NX_TOKEN_MAX = 2048 };

typedef struct {
    NxState state;
    char token[NX_TOKEN_MAX];   // nexToken
    uint64_t expires_at;        // ms since 1970 (0: not said)
    uint64_t pid;
    uint64_t account_id;        // accountId: the account's id on the game's servers (0: the launcher did not say)
    char username[64];
    char friend_code[32];
    char server_host[128], nat_host[128];
    char ca_file[96];           // may be empty: the config's nextendo_ca then
    char reason[48], message[160];   // refused / unavailable
} NxSession;

// HTTP status + JSON body of GET /local/v1/nextendo/session. A 200 is NX_READY only when it is whole: state "ready",
// a token that fits and is plain printable text, a pid, a username and two host names; else NX_UNAVAILABLE.
// "accountId" is optional: a string of 1 to 16 hex digits, not zero; anything else is as if it were not there.
void nextendo_session_parse(int http_status, const char *body, NxSession *out);
const char *nextendo_state_name(NxState s);

// ---- name resolution rules (pure) -------------------------------------------------------------------------------
typedef enum {
    NX_HOST_OTHER = 0,   // real DNS
    NX_HOST_SERVER,      // *.nintendo.net, *.nintendo.com, *.nintendo.co.jp (and the bare domains): serverHost
    NX_HOST_NAT,         // nncs2-*.n.n.srv.nintendo.net: natHost
    NX_HOST_LITERAL,     // a dotted IPv4 address: itself (ip gets it)
} NxHostClass;
// Case and a trailing dot do not matter. ip (4 bytes, network order; may be NULL) is written for NX_HOST_LITERAL.
NxHostClass nextendo_classify(const char *name, uint8_t ip[4]);

// ---- a host's address, kept 10 minutes; the last good one outlives a failed lookup (pure but for `resolve`) -------
enum { NX_DNS_TTL_MS = 10 * 60 * 1000, NX_DNS_RETRY_MS = 30 * 1000, NX_DNS_FIRST_RETRY_MS = 5 * 1000 };
typedef int (*NxResolveFn)(const char *host, uint8_t ip[4]);   // 1 = resolved
typedef struct {
    char host[128];
    uint8_t ip[4];
    int have, tried;
    uint64_t good_ms, tried_ms;
} NxHostCache;
// 1 and ip when an address is known for host (fresh, or the last good one). A different host forgets the old address.
// A failed lookup is not repeated for 30 s (5 s while there is no address at all).
int nextendo_cache_get(NxHostCache *c, const char *host, uint64_t now_ms, NxResolveFn resolve, uint8_t ip[4]);

// ---- the account (pure) -----------------------------------------------------------------------------------------
// GetNetworkServiceAccountId: one id per pid, never 0, the same every session (Pia's station id comes from it).
// The stand-in while the launcher's session names no accountId (Nextendo derives the real one on its side).
uint64_t nextendo_account_id(uint64_t pid);
// The id of a session's account: its accountId when the launcher said one (the id Nextendo's servers know the
// player by, the one friends match), else nextendo_account_id(pid); 0 without a pid.
uint64_t nextendo_session_account(const NxSession *s);

// LoadNetworkServiceAccountIdTokenCache: b64url(header).b64url(claims).b64url(64 bytes), no padding. Claims as
// Nextendo's reference table: iss, aud, typ, sub, jti, di, bs:did, sn, hm, tv "1.8.0", iat, exp (3 h), nnex.
typedef struct {
    const char *nex_token;   // plain printable text without quotes or backslashes (nextendo_session_parse checked)
    uint64_t pid;            // sub and di are derived from it: the same for every token of the account
    int64_t iat;             // seconds since 1970
    uint8_t jti[16];         // random; written as a version 4 UUID
    uint8_t sig[64];         // any bytes: nobody verifies them
} NxJwt;
// Returns the token's length (out is NUL-terminated), 0 when it does not fit in cap.
size_t nextendo_jwt(const NxJwt *in, char *out, size_t cap);

// ---- the running session ------------------------------------------------------------------------------------------
// Ask the launcher. NX_READY: this is the session from now on. NX_REFUSED / NX_UNAUTHORIZED: the one in hand is
// dropped. NX_UNAVAILABLE: the one in hand stays while its token has not expired.
NxState nextendo_fetch(void);
int nextendo_have(void);                             // a session is in hand
uint64_t nextendo_pid(void);                         // 0 without one
uint64_t nextendo_account(void);                     // nextendo_session_account of the one in hand; 0 without one
int nextendo_username(char *out, size_t cap);        // 1 with one
int nextendo_hosts(char *server, char *nat, char *ca_file, size_t cap);   // each may be NULL; 1 with a session
// The account token for the session in hand (a new jti each call). Length, 0 without a session or when it does not fit.
size_t nextendo_token(char *out, size_t cap);
// The session's serverHost (NX_HOST_SERVER) or natHost (NX_HOST_NAT) through the cache. 1 and ip when known.
int nextendo_host(NxHostClass which, uint8_t ip[4]);
// The rules above for one name the game asks for; may block on DNS. 1 and ip (network order) when it has an address.
int nextendo_resolve(const char *name, uint8_t ip[4]);

#endif
