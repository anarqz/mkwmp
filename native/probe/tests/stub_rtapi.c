// Link stub for the Linux core build (build_linux.py): the runtime table with no backend behind it. A plugin build
// replaces this with the backend that fills g_rt from mkw-recomp's plugin API (docs/PLUGIN-API-READINESS.md).
#include "../src/rtapi.h"

RtApi g_rt;
