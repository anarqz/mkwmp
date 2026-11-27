// Ghost data transfers (time-trial ghosts: the game's document attachments), the parts without a runtime or a socket.
//
// The game moves a ghost's bytes outside its gRPC channel: its own HTTP client asks a signed link for the bytes (GET:
// a download, PUT with Content-Range: an upload in one or more parts) over the console's TLS service, which mkw-recomp
// does not have. nnssl.c answers that service on the host (plat_tls.h); the plaintext that passes through it is HTTP/1.1
// and is followed here, for one log line per step of a transfer.
//
// Nothing of a link is logged but its host: the path names the player's document and the query is the signature
// (a bearer secret), so a line carries their lengths only. Headers are logged when they are plain numbers
// (Content-Length, Content-Range, Range).
#ifndef MKWMP_GHOST_H
#define MKWMP_GHOST_H

#include <stddef.h>
#include <stdint.h>

// ---- certificate names (pure) ------------------------------------------------------------------------------------
// 1 when a certificate's DNS name covers host. Case does not matter, nor a trailing dot of host. "*" is honoured only
// as the whole left-most label and stands for exactly one label ("*.example.net" covers "a.example.net", not
// "example.net" or "a.b.example.net"); a pattern of fewer than three labels takes no wildcard.
int ghost_name_match(const char *pattern, const char *host);

// ---- the HTTP/1.1 exchange on one connection (pure) --------------------------------------------------------------
enum { GHOST_LINE_MAX = 256, GHOST_HEAD_MAX = 4096, GHOST_LINES = 6 };

typedef struct {
    char head[GHOST_HEAD_MAX];   // the message head being collected (request line / status line and headers)
    size_t head_len;
    int state;                   // GD_HEAD ... (ghost.c)
    int lost;                    // a head that did not fit or made no sense: this direction is only counted from here
    uint64_t left;               // body bytes still to come (GD_BODY), or of the chunk (GD_CHUNK)
    uint64_t body;               // body bytes of the message so far
    char num[20];                // a chunk-size line
    size_t num_len;
} GhostDir;

typedef struct {
    char host[128];
    GhostDir tx, rx;             // what the game sends, what it receives
    char method[12];             // of the last request (a HEAD's answer has no body)
    int upload;                  // the last request carried or announced a body (PUT / POST)
    unsigned requests, responses;
    uint64_t sent, received;     // plaintext bytes in each direction
} GhostTrace;

void ghost_trace_start(GhostTrace *t, const char *host);
// The game sent (sent = 1) or received n plaintext bytes. Writes the lines these bytes complete (a request's head, its
// body, an answer's status, its body) into lines and returns how many (0..max; max <= GHOST_LINES is enough).
int ghost_trace_feed(GhostTrace *t, int sent, const void *data, size_t n, char lines[][GHOST_LINE_MAX], int max);
// The connection ends: one summary line (always).
void ghost_trace_end(const GhostTrace *t, char *line, size_t cap);

#endif
