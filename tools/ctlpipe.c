// ctlpipe.exe: send commands to a running mkw-recomp through its MKW_CTL named pipe.
//   ctlpipe.exe <pipe name> "<command>" ["<command>" ...]
// e.g. ctlpipe.exe mkwctl "press 1 A 6" "shot Z:\tmp\s.png". Commands: press, shot, screen,
// frame, focus, ... (see mkw-recomp's ctl). Used to drive the Deck's game under Proton, where
// the pipe lives in the Wine prefix: run it with the same prefix as the game.
// Build: zig cc -target x86_64-windows-gnu -O2 -o ctlpipe.exe tools/ctlpipe.c
#include <windows.h>
#include <stdio.h>
#include <string.h>

int main(int argc, char **argv) {
    if (argc < 3) { fprintf(stderr, "usage: ctlpipe <pipe> <command>...\n"); return 2; }
    char path[256];
    snprintf(path, sizeof path, "\\\\.\\pipe\\%s", argv[1]);
    HANDLE h = INVALID_HANDLE_VALUE;
    for (int tries = 0; tries < 50 && h == INVALID_HANDLE_VALUE; tries++) {
        h = CreateFileA(path, GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
        if (h == INVALID_HANDLE_VALUE) Sleep(100);
    }
    if (h == INVALID_HANDLE_VALUE) { fprintf(stderr, "cannot open %s (error %lu)\n", path, GetLastError()); return 1; }
    for (int i = 2; i < argc; i++) {
        char line[1024];
        int n = snprintf(line, sizeof line, "%s\n", argv[i]);
        DWORD w;
        if (!WriteFile(h, line, (DWORD)n, &w, NULL)) { fprintf(stderr, "write failed\n"); return 1; }
        printf("sent: %s", line);
        Sleep(300);
    }
    CloseHandle(h);
    return 0;
}
