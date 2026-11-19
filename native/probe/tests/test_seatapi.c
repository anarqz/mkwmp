// seatapi.c's pure parts for the reports' `kind` (docs/contracts/SEAT_API.md 2b): what a status says the server
// takes, and the body of POST /seat/v1/reports with and without the field. No network.
//
//   $zig = "C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"; $s = "native\probe\src"
//   & $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o scratch\test_seatapi.exe `
//       native\probe\tests\test_seatapi.c $s\seatapi.c $s\plat_http_win.c $s\plat_win.c $s\log.c -lwinhttp
//   scratch\test_seatapi.exe
#include "../src/rtapi.h"
#include "../src/seatapi.h"

#include <stdio.h>
#include <string.h>

RtApi g_rt;

static int failures;
#define CHECK(c) do { if (!(c)) { printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #c); failures++; } } while (0)

int main(void) {
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

    printf("test_seatapi: %s\n", failures ? "FAIL" : "PASS");
    return failures != 0;
}
