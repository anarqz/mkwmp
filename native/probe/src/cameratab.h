// The Camera tab of mkw-recomp's in-game settings (cameratab.c, MkwPluginApi.settings_tab), and the same widgets for
// the mod's options page on the start screen (modpage.c).
#ifndef MKWMP_CAMERATAB_H
#define MKWMP_CAMERATAB_H

#include "mkw_plugin.h"

// The tab: always registered; when camera_install left the camera off it says why (camera_off_reason).
void cameratab_draw(const MkwPage *p);
// The widgets alone, where the caller is in its page: camera on / off, the device, the mirror, the others' cameras
// and what is sent to whom. On a runtime older than plugin API 3 (p->version): one note saying so, and nothing else.
void cameratab_widgets(const MkwPage *p);

#endif
