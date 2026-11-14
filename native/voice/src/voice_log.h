// mkwmp voice: the engine's one dependency on its host (in ForzaInfinity, the overlay's overlay.h): a log line.
// mkwmp_voice.cpp routes it to the probe's mkwmp.log through the host callback.
#pragma once

#include <windows.h>

namespace fi {
void Log(const char *format, ...);
} // namespace fi
