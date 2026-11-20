#include "chatroom.h"

#include "log.h"
#include "plat.h"

#include <stdio.h>
#include <string.h>

// ---- pure -------------------------------------------------------------------------------------------------------------

static int listed(const ChatRoom *r, uint64_t account) {
    for (int i = 0; i < r->n; i++)
        if (r->member[i].account == account) return 1;
    return 0;
}

// The friend's camera flag as the relay gives it now; -1 when the account is not among them.
static int camera_of(const ChatMember *friends, int n, uint64_t account) {
    for (int i = 0; i < n; i++)
        if (friends[i].account == account) return friends[i].camera != 0;
    return -1;
}

static void take(ChatRoom *out, uint64_t account, int camera) {
    if (!account || out->n >= CHATROOM_FRIENDS || listed(out, account)) return;
    out->member[out->n].account = account;
    out->member[out->n++].camera = camera != 0;
}

void chatroom_select(const ChatMember *friends, int n, const ChatRoom *before, ChatRoom *out) {
    ChatRoom was;
    memset(&was, 0, sizeof was);
    if (before) was = *before;   // (out may be the same object)
    memset(out, 0, sizeof *out);
    if (!friends || n < 0) n = 0;
    for (int i = 0; i < n; i++) {   // how many there are: each once
        int twice = !friends[i].account;
        for (int k = 0; k < i && !twice; k++) twice = friends[k].account == friends[i].account;
        out->friends += !twice;
    }
    for (int i = 0; i < was.n && i < CHATROOM_FRIENDS; i++)   // 1. on screen, still there, camera on
        if (camera_of(friends, n, was.member[i].account) == 1) take(out, was.member[i].account, 1);
    for (int i = 0; i < n; i++)                               // 2. the other cameras, in seat order
        if (camera_of(friends, n, friends[i].account) == 1) take(out, friends[i].account, 1);   // (a repeat: its first entry counts)
    for (int i = 0; i < was.n && i < CHATROOM_FRIENDS; i++)   // 3. places left: those listed before, then the rest
        if (camera_of(friends, n, was.member[i].account) == 0) take(out, was.member[i].account, 0);
    for (int i = 0; i < n; i++) take(out, friends[i].account, camera_of(friends, n, friends[i].account));
}

// ---- the room of this process -----------------------------------------------------------------------------------------

static PlatLock g_lock = PLAT_LOCK_INIT;
static ChatRoomHost H;
static int g_installed, g_loopback;
static ChatRoom g_room, g_relay;   // as published; as the relay's state last gave it (the faces chosen)

void chatroom_install(const ChatRoomHost *host) {
    char v[8] = "";
    int loopback = plat_getenv("MKWMP_CAMERA_LOOPBACK", v, sizeof v) && !strcmp(v, "1");
    plat_lock(&g_lock);
    int first = !g_installed;
    H = *host;
    g_loopback = loopback;
    g_installed = 1;
    plat_unlock(&g_lock);
    if (first)
        mkwmp_log("chat room: voice and the camera in a private friend room only; the camera between mutual friends, at most %d "
                  "faces%s", CHATROOM_FRIENDS, loopback ? " (loopback: the room is made up of the roster's first friends)" : "");
}

int chatroom_installed(void) {
    plat_lock_shared(&g_lock);
    int on = g_installed;
    plat_unlock_shared(&g_lock);
    return on;
}

void chatroom_set(uint64_t id, const ChatMember *friends, int n) {
    plat_lock(&g_lock);
    ChatRoom r;
    if (!id || !friends) memset(&r, 0, sizeof r);
    else chatroom_select(friends, n, g_relay.id == id ? &g_relay : NULL, &r), r.id = id;
    g_relay = r;
    plat_unlock(&g_lock);
}

int chatroom_refresh(void) {
    plat_lock_shared(&g_lock);
    ChatRoomHost h = H;
    int installed = g_installed, loopback = g_loopback;
    plat_unlock_shared(&g_lock);
    if (!installed) return 0;
    ChatRoom made;
    memset(&made, 0, sizeof made);
    if (loopback) {   // one PC: every friend of the roster counts as a mutual friend in the room, camera on
        uint64_t roster[64], self = h.self_account ? h.self_account() : 0;
        int nroster = h.friends ? h.friends(roster, 64) : 0;
        made.id = CHATROOM_LOOPBACK_ID;
        for (int i = 0; i < nroster; i++) {
            if (!roster[i] || roster[i] == self || listed(&made, roster[i])) continue;
            made.friends++;
            take(&made, roster[i], 1);
        }
    }
    plat_lock(&g_lock);
    ChatRoom now = g_relay.id || !loopback ? g_relay : made;   // (a room given with the loopback on: tests)
    int changed = memcmp(&now, &g_room, sizeof now) != 0;
    g_room = now;
    plat_unlock(&g_lock);
    if (changed) {
        char list[160] = "";
        int k = 0;
        for (int i = 0; i < now.n && k < (int)sizeof list - 40; i++)
            k += snprintf(list + k, sizeof list - (size_t)k, " %#llx%s", (unsigned long long)now.member[i].account,
                          now.member[i].camera ? " (camera)" : "");
        if (!now.id)
            mkwmp_event("chat room: in no friend room (a matchmade race or battle, or not in a room yet): no voice, no camera");
        else
            mkwmp_event("chat room: friend room %#llx%s: %d mutual friend(s) in it, %d face(s) chosen:%s", (unsigned long long)now.id,
                      loopback ? " (loopback)" : "", now.friends, now.n, now.n ? list : " nobody");
    }
    return changed;
}

int chatroom_get(ChatRoom *out) {
    plat_lock_shared(&g_lock);
    int on = g_installed;
    *out = g_room;
    plat_unlock_shared(&g_lock);
    if (!on) memset(out, 0, sizeof *out);
    return on;
}

int chatroom_has(uint64_t account) {
    plat_lock_shared(&g_lock);
    int has = listed(&g_room, account);
    plat_unlock_shared(&g_lock);
    return has;
}
