// The in-game Voice chat tab (voicetab.c), drawn by mkw-recomp's settings menu through MkwPluginApi.settings_tab.
#ifndef MKWMP_VOICETAB_H
#define MKWMP_VOICETAB_H

#include "mkw_plugin.h"

void voicetab_draw(const MkwPage *p);

// Who runs voice chat this game: the plugin (the tab is live), nobody yet (not signed up: the tab greyed, pointing at
// the MKWMP tab) or the MKWMP launcher (greyed: it has its own voice settings). Not signed up until told otherwise.
enum { VOICETAB_NOT_SIGNED_UP, VOICETAB_LIVE, VOICETAB_LAUNCHER };
void voicetab_set_state(int state);

#endif
