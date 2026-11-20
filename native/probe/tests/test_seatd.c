// seatd.c (the launcher's local API answered in the game) against tests/fake_seat.py: the seat with its ticket and
// lobby, the heartbeat, a session lost and joined again, friends, voice settings and mutes, refusals (wrong PIN,
// a purged profile restored) as seat.c reads them, and the lobby CA saved in home. Built by build_plugin.py --test with the core
// sources it needs.   Usage: test_seatd <server base URL> <empty directory>
#undef NDEBUG
#include "../src/config.h"
#include "../src/plat.h"
#include "../src/seat.h"
#include "../src/seatapi.h"
#include "../src/seatd.h"

#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static char buf[16384];
static char g_store[8][2][1100];   // the mod's settings: keys and values
static int s_get(const char *key, char *out, size_t cap) {
    for (int i = 0; i < 8; i++)
        if (!strcmp(g_store[i][0], key)) { snprintf(out, cap, "%s", g_store[i][1]); return 1; }
    return 0;
}
static int s_set(const char *key, const char *value) {
    for (int i = 0; i < 8; i++)
        if (!strcmp(g_store[i][0], key) || (!g_store[i][0][0] && value)) {
            if (!value) { g_store[i][0][0] = 0; return 1; }
            snprintf(g_store[i][0], sizeof g_store[i][0], "%s", key);
            snprintf(g_store[i][1], sizeof g_store[i][1], "%s", value);
            return 1;
        }
    return !value;
}

static int req(const char *method, const char *path, const char *json, const char **body) {
    return seatd_request(method, path, json, buf, sizeof buf, body);
}

// The room seat.c hands on (probe.c: to the crash record's breadcrumbs).
static struct { int calls; char lobby[40]; uint32_t ussid, ussids[8]; int n; uint64_t at; } g_room;
static void room_sink(const char *lobby, uint32_t ussid, const uint32_t *ussids, int n, uint64_t at) {
    g_room.calls++;
    snprintf(g_room.lobby, sizeof g_room.lobby, "%s", lobby ? lobby : "");
    g_room.ussid = ussid, g_room.n = n < 8 ? n : 8, g_room.at = at;
    for (int i = 0; i < g_room.n; i++) g_room.ussids[i] = ussids[i];
}

int main(int argc, char **argv) {
    if (argc != 3) return 2;
    plat_set_home(argv[2]);
    const char *body;

    // a profile of the fake server: Rafa / 222222
    SeatdConfig c = {0};
    snprintf(c.server, sizeof c.server, "%s", argv[1]);
    snprintf(c.name, sizeof c.name, "Rafa");
    snprintf(c.pin, sizeof c.pin, "999999");
    c.voice = 1;
    c.setting_get = s_get;
    c.setting_set = s_set;
    assert(req("POST", "/local/v1/seat", NULL, &body) == 0);   // not started: no answer, as no launcher
    mkwmp_config_use_direct("Rafa");
    seatd_start(&c);
    SeatReply r;
    seat_parse(req("POST", "/local/v1/seat", NULL, &body), body, &r);
    assert(r.state == SEAT_REFUSED && strstr(r.message, "Wrong PIN"));
    printf("wrong PIN: refused (%s)\n", r.message);

    snprintf(c.pin, sizeof c.pin, "222222");
    seatd_start(&c);
    seat_parse(req("POST", "/local/v1/seat", NULL, &body), body, &r);
    assert(r.state == SEAT_SEATED && !strncmp(r.ticket, "mkw1.fake.", 10) && !strcmp(r.lobby, "lobby.example:443"));
    assert(mkwmp_config()->direct && mkwmp_config()->voice_port == 8907);
    printf("seated: ticket %s, lobby %s, voice port %d\n", r.ticket, r.lobby, mkwmp_config()->voice_port);
    char ca[1024];
    assert(plat_home_path(ca, sizeof ca, "mkwmp-lobby-ca.pem"));
    FILE *f = fopen(ca, "rb");
    assert(f);
    char pem[256] = "";
    pem[fread(pem, 1, sizeof pem - 1, f)] = 0;
    fclose(f);
    assert(strstr(pem, "-----BEGIN CERTIFICATE-----"));
    char ticket[sizeof r.ticket];
    snprintf(ticket, sizeof ticket, "%s", r.ticket);
    // the join's view has no room yet: "room": null, which seat.c reads as in none
    seat_set_room_sink(room_sink);
    assert(strstr(body, "\"room\":null}"));
    seat_parse_room(body);
    assert(g_room.calls == 1 && !g_room.lobby[0]);
    seat_parse(req("POST", "/local/v1/seat", NULL, &body), body, &r);   // the heartbeat: the same session
    assert(r.state == SEAT_SEATED && !strcmp(r.ticket, ticket) && !strcmp(r.lobby, "lobby.example:443"));
    // the heartbeat's view has the room the lobby reported: passed on whole, with its "at"
    assert(strstr(body, "\"room\":{\"lobby\": \"0b4e7a10-1111-4222-8333-444455556666\"") ||
           strstr(body, "\"room\":{\"lobby\":\"0b4e7a10-1111-4222-8333-444455556666\""));
    seat_parse_room(body);
    assert(g_room.calls == 2 && !strcmp(g_room.lobby, "0b4e7a10-1111-4222-8333-444455556666") && g_room.ussid == 12);
    assert(g_room.n == 3 && g_room.ussids[0] == 3 && g_room.ussids[1] == 12 && g_room.ussids[2] == 16);
    assert(g_room.at == 1763647200123ull);
    seat_set_room_sink(NULL);
    printf("the room passes from the seat server to seat.c: lobby, ussid 12 of 3,12,16, at %llu\n",
           (unsigned long long)g_room.at);

    // the game's exit gives the seat back: one seat fewer taken on the server
    SeatApiStatus st;
    assert(seatapi_status(argv[1], &st).status == 200);
    int taken = st.seated;
    free(st.lobby_ca);
    seatd_stop();
    assert(seatapi_status(argv[1], &st).status == 200 && st.seated == taken - 1);
    free(st.lobby_ca);
    printf("the exit gives the seat back (%d -> %d taken)\n", taken, taken - 1);
    seat_parse(req("POST", "/local/v1/seat", NULL, &body), body, &r);
    assert(r.state == SEAT_SEATED && strcmp(r.ticket, ticket));
    printf("heartbeat keeps the session; a lost one is joined again\n");

    assert(req("GET", "/local/v1/friends", NULL, &body) == 200 && strstr(body, "\"friends\":["));
    assert(req("GET", "/local/v1/voice", NULL, &body) == 200 && strstr(body, "\"enabled\":true"));
    assert(req("POST", "/local/v1/voice/mute", "{\"account\":\"42\",\"name\":\"Luz\",\"muted\":true}", &body) == 204);
    assert(req("GET", "/local/v1/voice", NULL, &body) == 200 && strstr(body, "\"account\":\"42\""));
    char kept[64];
    assert(s_get("mutes.1", kept, sizeof kept) && !strcmp(kept, "42:Luz"));   // id and name
    seatd_stop();
    seatd_start(&c);   // the next game: the mute is still there
    assert(req("GET", "/local/v1/voice", NULL, &body) == 200 && strstr(body, "\"account\":\"42\",\"name\":\"Luz\""));
    SeatdMute m[4];
    assert(seatd_mutes(m, 4) == 1 && m[0].account == 42 && !strcmp(m[0].name, "Luz"));   // the Voice chat tab's list
    assert(req("POST", "/local/v1/voice/mute", "{\"account\":\"42\",\"name\":\"Luz\",\"muted\":false}", &body) == 204);
    assert(req("GET", "/local/v1/voice", NULL, &body) == 200 && !strstr(body, "\"account\":\"42\""));
    assert(!s_get("mutes.1", kept, sizeof kept));
    // 64 mutes: all kept across games, over the three settings
    for (int i = 1; i <= 64; i++) {
        char j[96];
        snprintf(j, sizeof j, "{\"account\":\"%d\",\"name\":\"p%d\",\"muted\":true}", 100000 + i, i);
        assert(req("POST", "/local/v1/voice/mute", j, &body) == 204);
    }
    seatd_stop();
    seatd_start(&c);
    assert(req("GET", "/local/v1/voice", NULL, &body) == 200 && strstr(body, "\"account\":\"100001\"") &&
           strstr(body, "\"account\":\"100064\""));
    for (int i = 1; i <= 64; i++) {
        char j[96];
        snprintf(j, sizeof j, "{\"account\":\"%d\",\"name\":\"p%d\",\"muted\":false}", 100000 + i, i);
        assert(req("POST", "/local/v1/voice/mute", j, &body) == 204);
    }
    printf("mutes kept in the mod's settings across games (64)\n");
    assert(req("GET", "/local/v1/room", NULL, &body) == 404);
    // the devices the engine reports, for the Voice chat tab; the tab's settings reach voice.c at its next poll
    assert(req("POST", "/local/v1/voice/devices", "{\"inputs\":[{\"id\":\"mic1\",\"name\":\"Mic\"}],\"outputs\":[]}", &body) == 204);
    char dev[256];
    assert(seatd_devices_json(dev, sizeof dev) && strstr(dev, "mic1"));
    s_set("voice_input", "mic1");
    s_set("voice_in_gain", "40");
    s_set("voice", "0");
    assert(req("GET", "/local/v1/voice", NULL, &body) == 200 && strstr(body, "\"enabled\":false") &&
           strstr(body, "\"input\":\"mic1\"") && strstr(body, "\"inputGain\":40"));
    s_set("voice", NULL);
    s_set("voice_input", NULL);
    s_set("voice_in_gain", NULL);
    printf("voice settings from the mod's settings, live; the device list kept for the tab\n");
    printf("friends, voice settings and mutes, no room\n");

    // a profile the server purged (30 days unused) comes back with the PIN the mod saved
    snprintf(c.name, sizeof c.name, "Nobody");
    seatd_stop();
    seatd_start(&c);
    seat_parse(req("POST", "/local/v1/seat", NULL, &body), body, &r);
    assert(r.state == SEAT_SEATED);
    printf("a purged profile restored with the saved PIN, then seated\n");
    seatd_stop();

    // a name the server refuses to restore (reserved): refused, with the server's reason
    snprintf(c.name, sizeof c.name, "admin");
    seatd_start(&c);
    seat_parse(req("POST", "/local/v1/seat", NULL, &body), body, &r);
    assert(r.state == SEAT_REFUSED);
    printf("a profile that cannot be restored: refused (%s)\n", r.message);
    seatd_stop();
    printf("test_seatd: OK\n");
    return 0;
}
