// The lobby's TLS trust anchor.
//
// The game's gRPC runs on its embedded OpenSSL, which verifies the lobby's
// certificate chain and hostname against the CA list the SDK hands it
// (nn::ssl BuiltInManager::GetBuiltInCertificates, answered by the probe).
// Each server publishes its own CA (seat /seat/v1/status "lobbyCa"); the
// launcher writes it to <game dir>\<mkwmp.toml lobby_ca>, default
// "mkwmp-lobby-ca.pem". Without that file the probe trusts the built-in
// capture certificate (capture_cert.h), as rc.1 did.
#ifndef MKWMP_LOBBY_CA_H
#define MKWMP_LOBBY_CA_H

#include <stddef.h>

// Largest CA accepted (DER bytes).
#define LOBBY_CA_MAX 8192

// The first certificate of a PEM text ("-----BEGIN CERTIFICATE-----"), or the
// input itself when it already is DER (starts with a SEQUENCE). Writes at most
// `cap` bytes to `der`; returns its length, 0 when there is none. Pure; tested.
size_t lobby_ca_decode(const unsigned char *in, size_t len, unsigned char *der, size_t cap);

// The CA to trust: the server's (read once from the game folder), else the
// built-in capture certificate. Never NULL.
const unsigned char *lobby_ca(size_t *len);

#endif
