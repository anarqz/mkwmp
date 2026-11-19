#include "chatroom.h"

#include "log.h"
#include "plat.h"

#include <stdio.h>
#include <string.h>

// ---- pure -------------------------------------------------------------------------------------------------------------

static int among(const uint64_t *list, int n, uint64_t account) {
    for (int i = 0; i < n; i++)
        if (list[i] == account) return 1;
    return 0;
}

static int listed(const ChatRoom *r, uint64_t account) {
    for (int i = 0; i < r->n; i++)
        if (r->member[i].account == account) return 1;
    return 0;
}

void chatroom_pick(const ChatRoomInput *in, ChatRoom *out) {
    memset(out, 0, sizeof *out);
    for (int pass = 0; pass < 2; pass++) {   // the friends the relay has in the lobby, then (loopback) the mirrored ones
        if (pass && !in->loopback) break;
        for (int i = 0; i < in->nfriends && out->n < CHATROOM_FRIENDS; i++) {
            uint64_t a = in->friends[i];
            if (!a || a == in->self || listed(out, a)) continue;
            if (!pass && !among(in->peers, in->npeers, a)) continue;
            out->member[out->n].account = a;
            out->member[out->n++].camera = in->loopback != 0;
            out->in_lobby += !pass;
        }
    }
}

void chatroom_ask(const ChatRoomInput *in, const char *key, ChatRoomRequest *out) {
    memset(out, 0, sizeof *out);
    snprintf(out->key, sizeof out->key, "%s", key ? key : "");
    for (int pass = 0; pass < 2; pass++)
        for (int i = 0; i < in->nfriends && out->n < CHATROOM_FRIENDS; i++) {
            uint64_t a = in->friends[i];
            if (!a || a == in->self || among(out->friends, out->n, a)) continue;
            if (!pass && !among(in->peers, in->npeers, a)) continue;
            out->friends[out->n++] = a;
        }
}

// ---- the room of this process -----------------------------------------------------------------------------------------

enum { LIST_MAX = 64 };

static PlatLock g_lock = PLAT_LOCK_INIT;
static ChatRoomHost H;
static int g_installed, g_loopback, g_voice_filter;
static ChatRoom g_room, g_grant;
static ChatRoomRequest g_request;
static char g_key[CHATROOM_KEY_SIZE];

void chatroom_install(const ChatRoomHost *host) {
    char v[8] = "";
    int loopback = plat_getenv("MKWMP_CAMERA_LOOPBACK", v, sizeof v) && !strcmp(v, "1");
    int filter = !(plat_getenv("MKWMP_VOICE_ROOM", v, sizeof v) && !strcmp(v, "0"));
    plat_lock(&g_lock);
    int first = !g_installed;
    H = *host;
    g_loopback = loopback;
    g_voice_filter = filter;
    g_installed = 1;
    plat_unlock(&g_lock);
    if (first)
        mkwmp_log("chat room: at most %d friends in the same game session%s; voice %s", CHATROOM_FRIENDS,
                  loopback ? " (loopback: every friend counts)" : "",
                  filter ? "is shared with the room only" : "stays lobby-wide (MKWMP_VOICE_ROOM=0)");
}

int chatroom_installed(void) {
    plat_lock_shared(&g_lock);
    int on = g_installed;
    plat_unlock_shared(&g_lock);
    return on;
}

int chatroom_refresh(void) {
    plat_lock_shared(&g_lock);
    ChatRoomHost h = H;
    int installed = g_installed, loopback = g_loopback;
    plat_unlock_shared(&g_lock);
    if (!installed) return 0;
    uint64_t friends[LIST_MAX], peers[LIST_MAX];
    ChatRoomInput in = {.self = h.self_account ? h.self_account() : 0, .loopback = loopback, .friends = friends, .peers = peers};
    in.nfriends = h.friends ? h.friends(friends, LIST_MAX) : 0;
    in.npeers = h.peers ? h.peers(peers, LIST_MAX) : 0;
    ChatRoom picked;
    chatroom_pick(&in, &picked);
    plat_lock(&g_lock);
    chatroom_ask(&in, g_key, &g_request);
    ChatRoom now = g_grant.granted ? g_grant : picked;
    int changed = memcmp(&now, &g_room, sizeof now) != 0;
    g_room = now;
    plat_unlock(&g_lock);
    if (changed) {
        char list[160] = "";
        int k = 0;
        for (int i = 0; i < now.n && k < (int)sizeof list - 30; i++)
            k += snprintf(list + k, sizeof list - (size_t)k, " %#llx", (unsigned long long)now.member[i].account);
        if (now.granted && !now.id)
            mkwmp_log("chat room: the server has put the player in no room (it waits for a friend in the same game session who "
                      "asks for this player too): nobody");
        else if (now.granted)
            mkwmp_log("chat room: %d members, the server's room %#llx:%s", now.n, (unsigned long long)now.id, now.n ? list : " nobody");
        else
            mkwmp_log("chat room: %d members (%d friends in the lobby's voice room%s; %d friends, %d players in the voice room):%s",
                      now.n, now.in_lobby, loopback ? ", the rest by loopback" : "", in.nfriends, in.npeers, now.n ? list : " nobody");
    }
    return changed;
}

void chatroom_set_key(const char *key) {
    plat_lock(&g_lock);
    snprintf(g_key, sizeof g_key, "%s", key ? key : "");
    snprintf(g_request.key, sizeof g_request.key, "%s", g_key);
    plat_unlock(&g_lock);
}

int chatroom_get(ChatRoom *out, char key[CHATROOM_KEY_SIZE]) {
    plat_lock_shared(&g_lock);
    int on = g_installed;
    *out = g_room;
    if (key) memcpy(key, g_key, CHATROOM_KEY_SIZE);
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

int chatroom_voice_filter(void) {
    plat_lock_shared(&g_lock);
    int on = g_installed && g_voice_filter;
    plat_unlock_shared(&g_lock);
    return on;
}

int chatroom_request(ChatRoomRequest *out) {
    plat_lock_shared(&g_lock);
    *out = g_request;
    int ask = g_installed && g_key[0] != 0;
    plat_unlock_shared(&g_lock);
    return ask;
}

void chatroom_grant(uint64_t id, const ChatMember *members, int n) {
    ChatRoom r;
    memset(&r, 0, sizeof r);
    if (members && n >= 0) {
        r.granted = 1;
        r.id = id;
        for (int i = 0; i < n && r.n < CHATROOM_FRIENDS; i++)
            if (members[i].account && !listed(&r, members[i].account)) {
                r.member[r.n].account = members[i].account;
                r.member[r.n++].camera = members[i].camera != 0;
            }
    }
    plat_lock(&g_lock);
    g_grant = r;
    plat_unlock(&g_lock);
}
