// seatapi.c's pure parts for the reports' `kind` (docs/contracts/SEAT_API.md 2b): what a status says the server
// takes, and the body of POST /seat/v1/reports with and without the field; the room ticket's body, answer and life
// (VOICE_API.md section 6). No network, unless the URL of tests/fake_seat.py is given: then the room ticket is asked
// of it as well (two friends, a stranger, a token that is none).
//
//   $zig = "C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"; $s = "native\probe\src"
//   & $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o scratch\test_seatapi.exe `
//       native\probe\tests\test_seatapi.c $s\seatapi.c $s\plat_http_win.c $s\plat_win.c $s\log.c -lwinhttp
//   scratch\test_seatapi.exe
//   python -I native\probe\tests\fake_seat.py scratch\port     (in another window; then:)
//   scratch\test_seatapi.exe http://127.0.0.1:$(Get-Content scratch\port)
#include "../src/rtapi.h"
#include "../src/seatapi.h"

#include <stdio.h>
#include <string.h>
#include <time.h>

RtApi g_rt;

static int failures;
#define CHECK(c) do { if (!(c)) { printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #c); failures++; } } while (0)

int main(int argc, char **argv) {
    // the status of a server that takes kinds, of one that takes only the voice kind, and of today's servers
    CHECK(seatapi_report_kinds("{\"bootId\":\"b\",\"seatsTotal\":24,\"reportKinds\":[\"voice\",\"camera\"],\"lobbies\":{}}") ==
          (SEATAPI_KIND_VOICE | SEATAPI_KIND_CAMERA));
    CHECK(seatapi_report_kinds("{\"reportKinds\": [ \"voice\" ],\"name\":\"camera\"}") == SEATAPI_KIND_VOICE);
    CHECK(seatapi_report_kinds("{\"bootId\":\"b\",\"name\":\"camera\",\"seatsTotal\":24,\"lobbies\":{\"camera\":1}}") == 0);
    CHECK(seatapi_report_kinds("{\"reportKinds\":[]}") == 0 && seatapi_report_kinds("{\"reportKinds\":\"camera\"}") == 0);
    CHECK(seatapi_report_kinds("") == 0 && seatapi_report_kinds(NULL) == 0);

    // the body: no kind unless one is given, so a server that does not know the field never sees it
    char json[1400];
    CHECK(seatapi_report_body(json, sizeof json, "Rafa", "hate", "said \"x\"\\y\n", NULL));
    CHECK(!strcmp(json, "{\"name\":\"Rafa\",\"reason\":\"hate\",\"details\":\"said \\\"x\\\"\\\\y\"}"));
    CHECK(seatapi_report_body(json, sizeof json, "Rafa", "hate", NULL, "") && !strstr(json, "kind"));
    CHECK(seatapi_report_body(json, sizeof json, "Rafa", "sexual", "", "camera"));
    CHECK(!strcmp(json, "{\"name\":\"Rafa\",\"reason\":\"sexual\",\"details\":\"\",\"kind\":\"camera\"}"));
    CHECK(seatapi_report_body(json, sizeof json, "Rafa", "other", "", "voice") && strstr(json, ",\"kind\":\"voice\"}"));
    // refused before anything is sent: a kind or a reason the server does not have, a name that is none, no room
    CHECK(!seatapi_report_body(json, sizeof json, "Rafa", "hate", "", "video"));
    CHECK(!seatapi_report_body(json, sizeof json, "Rafa", "rude", "", "camera"));
    CHECK(!seatapi_report_body(json, sizeof json, "R\"", "hate", "", "camera") && !seatapi_report_body(json, 20, "Rafa", "hate", "", NULL));
    SeatApiResult r = seatapi_report_file("http://127.0.0.1:9", "token", "Rafa", "hate", "", "video", NULL);
    CHECK(r.status == 400 && !strcmp(r.code, "REPORT_INVALID"));   // no call was made
    // the six reasons are the launcher's, in its order
    static const char *const reasons[] = {SEATAPI_REPORT_REASONS};
    CHECK(sizeof reasons / sizeof *reasons == 6 && !strcmp(reasons[0], "harassment") && !strcmp(reasons[5], "other"));
    for (int i = 0; i < 6; i++) CHECK(seatapi_report_body(json, sizeof json, "Bia_7", reasons[i], "", "camera"));

    // ---- the room ticket (POST /seat/v1/rooms/ticket): the body, the answer, how long it lives
    const char *four[] = {"Ana", "Bia_7", "Caio-x", "Duda"}, *five[] = {"Ana", "Bia_7", "Caio-x", "Duda", "Edu"}, *bad[] = {"Ana", "B\"x"};
    CHECK(seatapi_room_ticket_body(json, sizeof json, four, 1) && !strcmp(json, "{\"friends\":[\"Ana\"]}"));
    CHECK(seatapi_room_ticket_body(json, sizeof json, four, 4) &&
          !strcmp(json, "{\"friends\":[\"Ana\",\"Bia_7\",\"Caio-x\",\"Duda\"]}"));
    CHECK(!seatapi_room_ticket_body(json, sizeof json, five, 5) && !seatapi_room_ticket_body(json, sizeof json, four, 0));
    CHECK(!seatapi_room_ticket_body(json, sizeof json, bad, 2) && !seatapi_room_ticket_body(json, 30, four, 4));
    SeatApiRoomTicket t;
    CHECK(seatapi_room_ticket_parse("{\"ticket\":\"mkwr1.eyJ2IjoxfQ.c2ln\",\"friends\":[\"Ana\",\"Bia_7\"],\"expires\":1795000900}", &t));
    CHECK(!strcmp(t.ticket, "mkwr1.eyJ2IjoxfQ.c2ln") && t.nfriends == 2 && !strcmp(t.friends[0], "Ana") && !strcmp(t.friends[1], "Bia_7") &&
          t.expires == 1795000900);
    // the server grants fewer than asked (not friends), or nobody; a key named "friends" inside another value is not the list
    CHECK(seatapi_room_ticket_parse("{ \"expires\": 900, \"friends\": [ ], \"ticket\": \"mkwr1.a.b\" }", &t) && t.nfriends == 0 && t.expires == 900);
    CHECK(seatapi_room_ticket_parse("{\"ticket\":\"mkwr1.a.b\",\"friends\":[\"A\",\"Rafa\",\"bad name\",\"Caio\",\"Duda\",\"Edu\",\"Fabi\"]}", &t) &&
          t.nfriends == 4 && !strcmp(t.friends[0], "Rafa") && !strcmp(t.friends[3], "Edu") && t.expires == 0);
    CHECK(!seatapi_room_ticket_parse("{\"friends\":[\"Ana\"],\"expires\":5}", &t) && !seatapi_room_ticket_parse("{\"ticket\":\"\"}", &t) &&
          !seatapi_room_ticket_parse("{\"error\":{\"code\":\"PRESENCE_NOT_FOUND\"}}", &t) && !t.ticket[0]);
    char big[700];
    memset(big, 'k', sizeof big);
    snprintf(json, sizeof json, "{\"ticket\":\"%.512s\"}", big);
    CHECK(seatapi_room_ticket_parse(json, &t) && strlen(t.ticket) == 512);
    snprintf(json, sizeof json, "{\"ticket\":\"%.513s\"}", big);
    CHECK(!seatapi_room_ticket_parse(json, &t));   // over the 512 bytes the relay's datagram carries
    // expires: a unix time (seconds or milliseconds), seconds from now, or nothing; never over the 15 minutes
    CHECK(seatapi_room_ticket_life(1795000900, 1795000000) == 900 && seatapi_room_ticket_life(1795000900, 1795000800) == 100);
    CHECK(seatapi_room_ticket_life(1795000900, 1795001000) == 0 && seatapi_room_ticket_life(1795000900000ll, 1795000600) == 300);
    CHECK(seatapi_room_ticket_life(600, 1795000000) == 600 && seatapi_room_ticket_life(0, 1795000000) == 900);
    CHECK(seatapi_room_ticket_life(1795009999, 1795000000) == 900 && seatapi_room_ticket_life(86400, 5) == 900);
    r = seatapi_room_ticket("http://127.0.0.1:9", "token", five, 5, &t);
    CHECK(r.status == 400 && !strcmp(r.code, "ROOM_INVALID"));   // no call was made

    // ---- against the fake seat server: Rafa and Luz become friends; each one's ticket names the other, never a stranger
    if (argc > 1) {
        const char *base = argv[1];
        char rafa[160], luz[160];
        CHECK(seatapi_presence(base, "Rafa", "222222", rafa, sizeof rafa).status == 200 && rafa[0]);
        CHECK(seatapi_presence(base, "Luz", "333333", luz, sizeof luz).status == 200 && luz[0]);
        const char *ask[] = {"Luz", "Nobody_1"}, *back[] = {"rafa"};
        r = seatapi_room_ticket(base, rafa, ask, 2, &t);
        CHECK(r.status == 200 && !strncmp(t.ticket, "mkwr1.", 6) && t.nfriends == 0);   // not friends yet: a ticket naming nobody
        CHECK(seatapi_friend_request(base, rafa, "Luz").status == 200 && seatapi_friend_request(base, luz, "Rafa").status == 200);
        r = seatapi_room_ticket(base, rafa, ask, 2, &t);
        CHECK(r.status == 200 && !strncmp(t.ticket, "mkwr1.", 6) && strlen(t.ticket) <= SEATAPI_ROOM_TICKET_MAX && t.nfriends == 1 &&
              !strcmp(t.friends[0], "Luz"));
        int life = seatapi_room_ticket_life(t.expires, (long long)time(NULL));
        CHECK(life > 880 && life <= 900);
        r = seatapi_room_ticket(base, luz, back, 1, &t);
        CHECK(r.status == 200 && t.nfriends == 1 && !strcmp(t.friends[0], "Rafa"));   // the server's spelling of the name
        r = seatapi_room_ticket(base, "not-a-token", ask, 2, &t);
        CHECK(r.status == 401 && !strcmp(r.code, "PRESENCE_NOT_FOUND") && !t.ticket[0]);
        printf("room tickets against %s: asked, granted to friends only, refused without a presence\n", base);
    }

    printf("test_seatapi: %s\n", failures ? "FAIL" : "PASS");
    return failures != 0;
}
