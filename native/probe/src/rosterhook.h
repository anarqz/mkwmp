// Previous races, the game side (roster.h has the logic): the lobby's members, read from the game's member tree after
// the online race controller's own update, sent to the launcher as POST /local/v1/roster (LAUNCHER_API.md).
//
//   - "change" when the set of {ussid, uid, name} differs from the last one sent, at most once a second (the latest
//     wins); "match" once per match, with the referee key, when the game sends its referee request; "left" when the
//     player leaves online. Nothing while offline.
//   - The game's thread only reads and copies; a worker thread talks to the launcher.
//   - Game 1.8.0 only (gameaddrs.h ro_*; every address and offset is guarded), both backends. MKWMP_NO_ROSTER=1 turns
//     it off. Nothing is written to the game's memory and nothing is drawn.
//   - Neither a uid nor a name is logged: counts only.
#ifndef MKWMP_ROSTERHOOK_H
#define MKWMP_ROSTERHOOK_H

#include "gameaddrs.h"

// Hook the controller's update and the two referee-request senders of the build (1 installed). Call once the game's
// code is loaded, while hooks can still go in.
int roster_install(const struct game_addrs *a);
// The player goes online (again): the lobby that comes is a new one.
void roster_online(void);
// The player left online: the launcher is told (if it was told of a lobby), and nothing more is sent until the member
// tree has emptied or the player goes online again.
void roster_left(void);

#endif
