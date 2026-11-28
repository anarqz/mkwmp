// test_overlay.c links overlay.c without the in-game overlay (src/ingame/, C++): no in-game page, so every trigger
// is the launcher's window overlay, as on a runtime without plugin API 5.
#include "../src/ingame.h"

int ingame_install(const IngameHost *host) { (void)host; return INGAME_NONE; }
int ingame_tier(void) { return INGAME_NONE; }
int ingame_page_toggle(const char *by) { (void)by; return 0; }
int ingame_page_open(void) { return 0; }
