// plat.h on Windows.
#include "plat.h"

#include <stdlib.h>

typedef struct { void (*fn)(void *); void *arg; } Start;

static DWORD WINAPI thread_main(LPVOID p) {
    Start s = *(Start *)p;
    free(p);
    s.fn(s.arg);
    return 0;
}

int plat_thread_start(void (*fn)(void *arg), void *arg) {
    Start *s = malloc(sizeof *s);
    if (!s) return 0;
    s->fn = fn, s->arg = arg;
    HANDLE h = CreateThread(NULL, 0, thread_main, s, 0, NULL);
    if (!h) { free(s); return 0; }
    CloseHandle(h);
    return 1;
}

// A PlatEvent is the Win32 event handle itself.
PlatEvent *plat_event_new(int manual_reset) { return (PlatEvent *)CreateEventW(NULL, manual_reset ? TRUE : FALSE, FALSE, NULL); }
void plat_event_set(PlatEvent *e) { SetEvent((HANDLE)e); }
void plat_event_reset(PlatEvent *e) { ResetEvent((HANDLE)e); }
int plat_event_wait(PlatEvent *e, uint32_t ms) {
    return WaitForSingleObject((HANDLE)e, ms == PLAT_FOREVER ? INFINITE : (DWORD)ms) == WAIT_OBJECT_0;
}
