// Name-based UUIDs (RFC 4122 version 5, SHA-1). The lobby derives a player's user id from the
// launcher name this way (rust/lobby/src/users.rs); the probe has to arrive at the same bytes.
#ifndef MKWMP_UUID5_H
#define MKWMP_UUID5_H

#include <stddef.h>
#include <stdint.h>

// RFC 4122 appendix C namespace for URLs (python: uuid.NAMESPACE_URL).
extern const uint8_t kUuidNamespaceUrl[16];

// SHA-1 of `len` bytes (FIPS 180-4). Used only for name hashing, never for security.
void sha1(const void *data, size_t len, uint8_t out[20]);

// out = uuid5(ns, name): the 16 UUID bytes in network order (python: uuid5(ns, name).bytes).
void uuid5(const uint8_t ns[16], const void *name, size_t len, uint8_t out[16]);

#endif
