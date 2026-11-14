// voice_parse_settings (voice.h): the launcher's GET /local/v1/voice answer.
//   zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o scratch\test_voice.exe native\probe\tests\test_voice.c
//     native\probe\src\voice.c native\probe\src\log.c native\probe\src\config.c native\probe\src\launcher.c
//     native\probe\src\seat.c native\probe\src\friends.c native\probe\src\uuid5.c native\probe\src\notify.c -lws2_32
#include "../src/voice.h"

#include <stdio.h>
#include <string.h>

static int failures;
#define CHECK(c) do { if (!(c)) { printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #c); failures++; } } while (0)

int main(void) {
    VoiceSettings s;
    CHECK(voice_parse_settings(200, "{\"enabled\":false,\"input\":\"{0.0.1.00000000}.{ab\\\"c}\",\"output\":\"\","
                                    "\"inputGain\":40,\"outputGain\":250,\"mutes\":[{\"account\":\"8070450532247928832\",\"name\":\"Bia\"},"
                                    "{\"account\":\"12\",\"name\":\"x\"}]}", &s));
    CHECK(s.enabled == 0);
    CHECK(!strcmp(s.input, "{0.0.1.00000000}.{ab\"c}"));
    CHECK(s.output[0] == 0);
    CHECK(s.input_gain == 40);
    CHECK(s.output_gain == 100);   // out of range: the default
    CHECK(s.mute_count == 2 && s.mutes[0] == 8070450532247928832ull && s.mutes[1] == 12);
    CHECK(voice_parse_settings(200, "{}", &s) && s.enabled == 1 && s.input_gain == 100 && s.mute_count == 0);
    CHECK(!voice_parse_settings(404, "{}", &s) && s.enabled == 1);
    CHECK(voice_parse_settings(200, "{\"enabled\": true, \"mutes\": []}", &s) && s.enabled == 1 && s.mute_count == 0);
    printf("test_voice: %s\n", failures ? "FAIL" : "PASS");
    return failures != 0;
}
