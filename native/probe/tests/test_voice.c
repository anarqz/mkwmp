// voice_parse_settings (voice.h): the launcher's GET /local/v1/voice answer; voice_room_mutes: who the engine mutes while
// voice is shared with the chat room only.
// (voice.c installs the menu hooks, so voice_ui.c and what it needs come along; coff.c defines g_rt.)
//
//   $zig = "C:\Users\alcmo\tools\zig-x86_64-windows-0.15.2\zig.exe"; $s = "native\probe\src"
//   & $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -o scratch\test_voice.exe `
//       native\probe\tests\test_voice.c $s\voice.c $s\chatroom.c $s\voice_ui.c $s\guesthook.c $s\gameaddrs.c $s\coff.c $s\log.c `
//       $s\config.c $s\launcher.c $s\seat.c $s\friends.c $s\uuid5.c $s\notify.c $s\plat_win.c $s\plat_net_win.c -lws2_32
//   scratch\test_voice.exe
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
    // a lobby of five others, two of them in the chat room: the other three are muted, then the player's own mutes (one
    // of them a room member: still muted; one already listed: once)
    const uint64_t lobby[] = {10, 11, 12, 13, 14}, room[] = {11, 13, 99}, mine[] = {13, 12, 77};
    uint64_t out[32];
    int heard = -1;
    CHECK(voice_room_mutes(mine, 3, lobby, 5, room, 3, out, 32, &heard) == 5 && heard == 2);
    CHECK(out[0] == 10 && out[1] == 12 && out[2] == 14 && out[3] == 13 && out[4] == 77);
    CHECK(voice_room_mutes(NULL, 0, lobby, 5, NULL, 0, out, 32, &heard) == 5 && heard == 0);   // no room: nobody is heard
    CHECK(voice_room_mutes(mine, 3, lobby, 5, room, 3, out, 4, NULL) == 4 && out[3] == 13);  // the wire's limit: the lobby first
    CHECK(voice_room_mutes(mine, 3, NULL, 0, room, 3, out, 32, &heard) == 3 && heard == 0);    // not in a voice room
    printf("test_voice: %s\n", failures ? "FAIL" : "PASS");
    return failures != 0;
}
