// Voice in the game's online pause menu (player list labels, cursor, mute toggle). See voice_ui.c.
#ifndef MKWMP_VOICE_UI_H
#define MKWMP_VOICE_UI_H

// Hook the online pause menu (once; after the guest image is loaded). MKWMP_VOICE_UI=0 disables it.
void voice_ui_install(void);

#endif
