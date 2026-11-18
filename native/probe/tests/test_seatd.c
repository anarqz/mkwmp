// seatd.c (the launcher's local API answered in the game) against tests/fake_seat.py: the seat with its ticket and
// lobby, the heartbeat, a session lost and joined again, friends, voice settings and mutes, refusals (wrong PIN,
// unknown name) as seat.c reads them, and the lobby CA saved in home. Built by build_plugin.py --test with the core
// sources it needs.   Usage: test_seatd <server base URL> <empty directory>
#undef NDEBUG
#include "../src/config.h"
#include "../src/plat.h"
#include "../src/seat.h"
#include "../src/seatapi.h"
#include "../src/seatd.h"

#include <assert.h>
#include <stdio.h>
#include <string.h>

static char buf[16384];

static int req(const char *method, const char *path, const char *json, const char **body) {
    return seatd_request(method, path, json, buf, sizeof buf, body);
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
    seat_parse(req("POST", "/local/v1/seat", NULL, &body), body, &r);   // the heartbeat: the same session
    assert(r.state == SEAT_SEATED && !strcmp(r.ticket, ticket));

    // the server forgot the session (restart, timeout): joined again
    seatd_stop();
    seat_parse(req("POST", "/local/v1/seat", NULL, &body), body, &r);
    assert(r.state == SEAT_SEATED && strcmp(r.ticket, ticket));
    printf("heartbeat keeps the session; a lost one is joined again\n");

    assert(req("GET", "/local/v1/friends", NULL, &body) == 200 && strstr(body, "\"friends\":["));
    assert(req("GET", "/local/v1/voice", NULL, &body) == 200 && strstr(body, "\"enabled\":true"));
    assert(req("POST", "/local/v1/voice/mute", "{\"account\":\"42\",\"name\":\"Luz\",\"muted\":true}", &body) == 204);
    assert(req("GET", "/local/v1/voice", NULL, &body) == 200 && strstr(body, "\"account\":\"42\""));
    assert(req("POST", "/local/v1/voice/mute", "{\"account\":\"42\",\"name\":\"Luz\",\"muted\":false}", &body) == 204);
    assert(req("GET", "/local/v1/voice", NULL, &body) == 200 && !strstr(body, "\"account\":\"42\""));
    assert(req("GET", "/local/v1/room", NULL, &body) == 404);
    printf("friends, voice settings and mutes, no room\n");

    snprintf(c.name, sizeof c.name, "Nobody");
    seatd_stop();
    seatd_start(&c);
    seat_parse(req("POST", "/local/v1/seat", NULL, &body), body, &r);
    assert(r.state == SEAT_REFUSED && strstr(r.message, "not registered"));
    seatd_stop();
    printf("test_seatd: OK\n");
    return 0;
}
