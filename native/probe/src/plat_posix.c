// plat.h on Linux (and other POSIX systems): for the native .so of mkw-recomp's plugin API.
#include "plat.h"

#include <errno.h>
#include <stdlib.h>

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
