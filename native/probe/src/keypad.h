// On-screen numeric keypad for the game's Room ID keyboard (research/docs/SWKBD-KEYPAD.md).
#ifndef MKWMP_KEYPAD_H
#define MKWMP_KEYPAD_H

// Hook the recomp's text-entry stub and its overlay frame. Call once from DllMain, after coff_resolve_runtime
// (no other thread may be running mkw.exe code yet). Logs what it did; on any mismatch nothing is patched and
// nn::swkbd::ShowKeyboard behaves as before (MKW_SWKBD_ANSWER / default text).
void keypad_install(void);

#endif
