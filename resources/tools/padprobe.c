// padprobe.exe: what controllers a Windows SDL3 program (and XInput) sees here.
// Build: zig cc -target x86_64-windows-gnu -O2 -o padprobe.exe resources/tools/padprobe.c
// Run next to the game's SDL3.dll (e.g. under Proton on the Deck).
#include <windows.h>
#include <stdio.h>
#include <stdint.h>
#include <string.h>

typedef uint32_t SDL_JoystickID;
typedef struct { DWORD dwPacketNumber; WORD buttons; BYTE lt, rt; SHORT lx, ly, rx, ry; } XSTATE;

int main(int argc, char **argv) {
    freopen("padprobe.txt", "a", stdout);
    setvbuf(stdout, NULL, _IONBF, 0);
    printf("start:");
    for (int a = 1; a < argc; a++) {          // KEY=VALUE environment for SDL hints
        char *eq = strchr(argv[a], '=');
        if (eq) { *eq = 0; SetEnvironmentVariableA(argv[a], eq + 1); printf(" %s=%s", argv[a], eq + 1); *eq = '='; }
    }
    printf("\n");
    printf("SDL_GAMECONTROLLER_IGNORE_DEVICES set: %s\n",
           GetEnvironmentVariableA("SDL_GAMECONTROLLER_IGNORE_DEVICES", NULL, 0) ? "yes" : "no");
    HMODULE xi = LoadLibraryA("xinput1_4.dll");
    if (!xi) xi = LoadLibraryA("xinput1_3.dll");
    DWORD (WINAPI *XGetState)(DWORD, XSTATE *) = xi ? (void *)GetProcAddress(xi, "XInputGetState") : NULL;
    for (DWORD i = 0; XGetState && i < 4; i++) {
        XSTATE s; DWORD r = XGetState(i, &s);
        printf("XInput slot %lu: %s\n", (unsigned long)i, r == 0 ? "CONNECTED" : "not connected");
    }
    HMODULE sdl = LoadLibraryA("SDL3.dll");
    if (!sdl) { printf("SDL3.dll not found\n"); return 1; }
    int (*Init)(uint32_t) = (void *)GetProcAddress(sdl, "SDL_Init");
    SDL_JoystickID *(*GetJoysticks)(int *) = (void *)GetProcAddress(sdl, "SDL_GetJoysticks");
    SDL_JoystickID *(*GetGamepads)(int *) = (void *)GetProcAddress(sdl, "SDL_GetGamepads");
    const char *(*JName)(SDL_JoystickID) = (void *)GetProcAddress(sdl, "SDL_GetJoystickNameForID");
    uint16_t (*JVendor)(SDL_JoystickID) = (void *)GetProcAddress(sdl, "SDL_GetJoystickVendorForID");
    uint16_t (*JProduct)(SDL_JoystickID) = (void *)GetProcAddress(sdl, "SDL_GetJoystickProductForID");
    const char *(*GetError)(void) = (void *)GetProcAddress(sdl, "SDL_GetError");
    void (*Pump)(void) = (void *)GetProcAddress(sdl, "SDL_PumpEvents");
    if (!Init(0x200u | 0x2000u)) { printf("SDL_Init failed: %s\n", GetError()); return 1; }  // JOYSTICK | GAMEPAD
    for (int k = 0; k < 20; k++) { Pump(); Sleep(50); }
    int nj = 0, ng = 0;
    SDL_JoystickID *js = GetJoysticks(&nj), *gp = GetGamepads(&ng);
    printf("SDL joysticks: %d, gamepads: %d\n", nj, ng);
    for (int i = 0; i < nj; i++)
        printf("  joystick %u: %s (%04x:%04x)\n", js[i], JName(js[i]), JVendor(js[i]), JProduct(js[i]));
    return 0;
}
