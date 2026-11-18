// plat.h on Linux (and other POSIX systems): for the native .so of mkw-recomp's plugin API.
#define _GNU_SOURCE
#include "plat.h"


#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/file.h>
#include <sys/mman.h>
#include <unistd.h>

typedef struct { void (*fn)(void *); void *arg; } Start;

static void *thread_main(void *p) {
    Start s = *(Start *)p;
    free(p);
    s.fn(s.arg);
    return NULL;
}

int plat_thread_start(void (*fn)(void *arg), void *arg) {
    Start *s = malloc(sizeof *s);
    if (!s) return 0;
    s->fn = fn, s->arg = arg;
    pthread_t t;
    if (pthread_create(&t, NULL, thread_main, s)) { free(s); return 0; }
    pthread_detach(t);
    return 1;
}

struct PlatEvent {
    pthread_mutex_t mu;
    pthread_cond_t cv;   // on CLOCK_MONOTONIC
    int manual, set;
};

PlatEvent *plat_event_new(int manual_reset) {
    PlatEvent *e = calloc(1, sizeof *e);
    if (!e) return NULL;
    pthread_condattr_t a;
    pthread_condattr_init(&a);
    pthread_condattr_setclock(&a, CLOCK_MONOTONIC);
    pthread_mutex_init(&e->mu, NULL);
    pthread_cond_init(&e->cv, &a);
    pthread_condattr_destroy(&a);
    e->manual = manual_reset;
    return e;
}

void plat_event_set(PlatEvent *e) {
    pthread_mutex_lock(&e->mu);
    e->set = 1;
    if (e->manual) pthread_cond_broadcast(&e->cv);
    else pthread_cond_signal(&e->cv);
    pthread_mutex_unlock(&e->mu);
}

void plat_event_reset(PlatEvent *e) {
    pthread_mutex_lock(&e->mu);
    e->set = 0;
    pthread_mutex_unlock(&e->mu);
}

int plat_event_wait(PlatEvent *e, uint32_t ms) {
    struct timespec until;
    clock_gettime(CLOCK_MONOTONIC, &until);
    until.tv_sec += ms / 1000u;
    until.tv_nsec += (long)(ms % 1000u) * 1000000L;
    if (until.tv_nsec >= 1000000000L) until.tv_sec++, until.tv_nsec -= 1000000000L;
    pthread_mutex_lock(&e->mu);
    int r = 0;
    while (!e->set && r != ETIMEDOUT)
        r = ms == PLAT_FOREVER ? pthread_cond_wait(&e->cv, &e->mu) : pthread_cond_timedwait(&e->cv, &e->mu, &until);
    int got = e->set;
    if (got && !e->manual) e->set = 0;   // auto-reset: this wait consumed it
    pthread_mutex_unlock(&e->mu);
    return got;
}

unsigned long plat_thread_id(void) { return (unsigned long)gettid(); }
unsigned long plat_last_error(void) { return (unsigned long)errno; }

// ---- files
int plat_home_path(char *out, size_t cap, const char *name) {
    char path[PATH_MAX];
    ssize_t n = readlink("/proc/self/exe", path, sizeof path - 1);
    if (n <= 0) return 0;
    path[n] = 0;
    char *slash = strrchr(path, '/');
    if (slash) slash[1] = 0;
    return snprintf(out, cap, "%s%s", path, name) < (int)cap;
}

long plat_read_file(const char *path, void *buf, size_t cap) {
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) return -1;
    size_t got = 0;
    while (got < cap) {
        ssize_t r = read(fd, (char *)buf + got, cap - got);
        if (r <= 0) break;
        got += (size_t)r;
    }
    close(fd);
    return (long)got;
}

// A PlatFile is the descriptor + 1 (so NULL stays "none"). The exclusive flock stands in for Windows' share mode.
PlatFile *plat_log_create(const char *path, int *in_use) {
    *in_use = 0;
    int fd = open(path, O_WRONLY | O_CREAT | O_CLOEXEC, 0644);
    if (fd < 0) return NULL;
    if (flock(fd, LOCK_EX | LOCK_NB)) {
        *in_use = errno == EWOULDBLOCK;
        close(fd);
        return NULL;
    }
    if (ftruncate(fd, 0)) {}
    return (PlatFile *)(intptr_t)(fd + 1);
}

void plat_file_write(PlatFile *f, const void *p, size_t n) {
    int fd = (int)(intptr_t)f - 1;
    while (n) {
        ssize_t w = write(fd, p, n);
        if (w <= 0) return;
        p = (const char *)p + w, n -= (size_t)w;
    }
}

// ---- environment
int plat_getenv(const char *name, char *buf, size_t cap) {
    const char *v = getenv(name);
    if (!v) return 0;
    if (buf && cap) snprintf(buf, cap, "%s", v);
    return 1;
}

void plat_setenv(const char *name, const char *value) {
    if (value) setenv(name, value, 1);
    else unsetenv(name);
}

// ---- dynamic libraries
void *plat_dl_open(const char *path) { return dlopen(path, RTLD_NOW | RTLD_LOCAL); }
void *plat_dl_sym(void *lib, const char *name) { return dlsym(lib, name); }
void plat_dl_close(void *lib) { dlclose(lib); }
void *plat_dl_loaded(const char *base) {
    char name[64];
    if (snprintf(name, sizeof name, "lib%s.so.0", base) >= (int)sizeof name) return NULL;
    return dlopen(name, RTLD_NOW | RTLD_NOLOAD);
}

// ---- memory: /proc/self/maps (slow, a file read per call: fine for install-time guards and traces; a plugin
// build should prefer mkw-recomp's guest memory accessors on hot paths)
static int find_mapping(uintptr_t a, uintptr_t *lo, uintptr_t *hi, char perms[5]) {
    FILE *f = fopen("/proc/self/maps", "re");
    if (!f) return 0;
    char line[512];
    int found = 0;
    while (fgets(line, sizeof line, f)) {
        unsigned long l, h;
        char p[5] = {0};
        if (sscanf(line, "%lx-%lx %4s", &l, &h, p) != 3) continue;
        if (a >= l && a < h) { *lo = l, *hi = h, memcpy(perms, p, 5), found = 1; break; }
    }
    fclose(f);
    return found;
}

size_t plat_mem_readable(const void *p) {
    uintptr_t lo, hi;
    char perms[5];
    if ((uintptr_t)p < 0x10000 || !find_mapping((uintptr_t)p, &lo, &hi, perms) || perms[0] != 'r') return 0;
    return (size_t)(hi - (uintptr_t)p);
}

int plat_mem_write_u64(uint64_t *at, uint64_t v) {
    uintptr_t lo, hi;
    char perms[5];
    if (!find_mapping((uintptr_t)at, &lo, &hi, perms)) { errno = EFAULT; return 0; }
    long page = sysconf(_SC_PAGESIZE);
    uintptr_t start = (uintptr_t)at & ~(uintptr_t)(page - 1);
    int prot = (perms[0] == 'r' ? PROT_READ : 0) | (perms[1] == 'w' ? PROT_WRITE : 0) | (perms[2] == 'x' ? PROT_EXEC : 0);
    if (mprotect((void *)start, (size_t)page, prot | PROT_READ | PROT_WRITE)) return 0;
    __atomic_store_n(at, v, __ATOMIC_RELEASE);
    mprotect((void *)start, (size_t)page, prot);
    __builtin___clear_cache((char *)at, (char *)(at + 1));
    return 1;
}

// ---- input: no global keyboard or window focus from a library on Linux (X11 / Wayland belong to the game). The
// voice UI keeps its gamepad shortcuts; keyboard ones need mkw-recomp's input or overlay hooks.
int plat_game_focused(void) { return 0; }
int plat_key_down(int key) { (void)key; return 0; }
