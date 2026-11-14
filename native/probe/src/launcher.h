// Transport to mkwmp-launcher's local API (docs/contracts/LAUNCHER_API.md): one HTTP/1.1
// request per call over loopback, `Authorization: Bearer <launcher_token>` from mkwmp.toml.
// seat.c (POST /local/v1/seat) and friends.c (GET /local/v1/friends) are built on it.
#ifndef MKWMP_LAUNCHER_H
#define MKWMP_LAUNCHER_H

#include <stddef.h>

// Send `method path` with no body and read the whole response into buf (always
// NUL-terminated; a response larger than cap is cut short). *body points at the response
// body inside buf. If the launcher refuses the connection or the token (it may have restarted
// with a new one), mkwmp.toml is re-read once and the request repeated.
// Returns the HTTP status, 0 if the launcher could not be reached, or -1 if mkwmp.toml has
// no `launcher` key. Blocks for up to 5 s per socket operation; calls are serialized.
int launcher_request(const char *method, const char *path, char *buf, size_t cap, const char **body);

#endif
