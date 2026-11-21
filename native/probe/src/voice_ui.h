// Voice in the game's online pause menu (player list labels, cursor, mute toggle). See voice_ui.c.
#ifndef MKWMP_VOICE_UI_H
#define MKWMP_VOICE_UI_H

#include <stddef.h>
#include <stdint.h>

struct game_addrs;

// Hook the online pause menu (once; after the guest image is loaded). MKWMP_VOICE_UI=0 disables it.
void voice_ui_install(void);

// The player list of an online pause menu page, read the way the game reads it (exposed for the tests): page +
// row->page_count is the count, page + row->page_entries a POINTER to the array of entry pointers. Returns the count
// (0..row->rows), or -1 when the page holds no usable list (bad count, unreadable array). For each i < count:
// entries[i] is the entry, or 0 when it, or its name, cannot be read; names[i] / lens[i] its name (at most 32 chars).
// The arrays hold GAME_ROWS_MAX (gameaddrs.h) items.
int voice_ui_page_entries(uint64_t page, const struct game_addrs *row, uint64_t *entries, const uint16_t **names,
                          size_t *lens);

#endif
