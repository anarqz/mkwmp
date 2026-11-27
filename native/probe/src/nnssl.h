// The console's TLS service for the game's HTTP client, answered on the host: ghost data (time-trial ghosts) is a
// document attachment, and the game moves an attachment's bytes with its own libcurl over nn::ssl (the nnssl C API)
// and the nnsocket C API, not over the gRPC channel (which runs on the game's embedded OpenSSL and needs none of
// this). mkw-recomp has no TLS service, so without these overrides every ghost upload and download ends in the
// game's "could not connect" error.
//
// What is answered here: nnsslConnection* (a connection is a plat_tls.h client on the socket the game names) and the
// two calls that end a context or the library. npln.c answers the socket side (npln_curl_socket_overrides: the
// resolver, errno, plain send / recv, peer and local address). The server is the one the game's link names, at the
// address the probe's name rules give it; its certificate is checked against the session's CA (lobby_ca.h), always,
// whatever verification the game asks for.
//
// Registered by the plugin build only, and only for Nextendo Network: the names add to npln.c's 64 (kRegs), which is
// all a mkw-recomp before 0.1.15 takes; the plugin API exists from 0.1.15 on, where the table grows. MKWMP servers
// issue no attachment links yet (research/docs/GHOSTS-1.8.0.md), so that mode is as it was.
// MKWMP_NO_GHOST=1 (environment or mkwmp.env) leaves them out.
#ifndef MKWMP_NNSSL_H
#define MKWMP_NNSSL_H

// Before the game loads. Returns how many overrides went in (0: off, the reason is logged).
int nnssl_register(void);

// ---- npln.c, for the above ----------------------------------------------------------------------------------------
// The socket-side names of the game's HTTP client that npln.c's table does not hold. Returns how many.
int npln_curl_socket_overrides(void);
// The host socket behind a descriptor the probe gave the game (PLAT_NO_SOCK when it is not one). Declared for the
// files that work with sockets: they include plat_net.h first (Winsock has to come before windows.h, so this header
// does not pull it in for the others).
#ifdef MKWMP_PLAT_NET_H
PlatSock npln_guest_socket(int fd);
#endif

#endif
