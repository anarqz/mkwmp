// NPLN interception overrides.
//
// Local capture bridge. The observed NPLN gRPC path uses embedded OpenSSL via
// SendMsg/RecvMsg; it trusts the CA the probe returns (lobby_ca.h: the server's own, else capture_cert.h).
// nn::ssl stubs below cover other SDK paths, not the captured gRPC transport.
//
// Gates and completed local async prerequisites start NPLN.
// Winsock carries its TLS records to resources/python-lobby/mkwmp_server.py, which terminates
// TLS, decodes the HTTP/2 + protobuf requests and supplies experimental responses.
//
// Env: MKWMP_PORT (default 8900), MKWMP_HOST (default 127.0.0.1).
#include "rtapi.h"
#include "breadcrumbs.h"
#include "log.h"
#include "config.h"
#include "lobby_ca.h"
#include "seat.h"
#include "friends.h"
#include "gameaddrs.h"
#include "guesthook.h"
#include "probe.h"
#include "uuid5.h"
#include "voice.h"

#include "plat_net.h"
#include <stdlib.h>
#include <string.h>
#include <limits.h>
#include <stdarg.h>
#include <stdio.h>

// ---- logging ---------------------------------------------------------------
// One sink for everything: our own mkwmp.log (the file players attach to
// reports) plus a mirror into the game log via rt_log as a backstop. Format
// once, emit twice. Safe to call from any override/thread.
static void mlog(const char *fmt, ...) {
    char buf[4096];
    va_list ap;
    va_start(ap, fmt);
    int n = vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    mkwmp_log("%s", buf);
    crumb_text(buf);   // every line here is an event of the session: the crash record keeps the last ones
    if (g_rt.log) g_rt.log("mkwmp: %s", buf);
}
static void rlog(const char *s) { mlog("%s", s); }
// MKWMP_TRACE_EDGES=1: the probe's debugging detail (every call, hex dumps, every packet). Off for players.
static int g_trace_edges;
static volatile int32_t g_mesh_role;   // how this player got into the Pia mesh: 1 made it, 2 joined it (0: in none)
static void dump_args(const char *name, Cpu *c, int n) {
    if (!g_rt.log || !g_trace_edges) return;
    uint64_t a[6] = {0};
    for (int i = 0; i < n && i < 6; i++) a[i] = arg_x(c, i);
    mlog("CALL %s x0=%#llx x1=%#llx x2=%#llx x3=%#llx x4=%#llx", name,
                (unsigned long long)a[0], (unsigned long long)a[1], (unsigned long long)a[2],
                (unsigned long long)a[3], (unsigned long long)a[4]);
}
static void dump_cstr(const char *label, const void *p) {
    if (g_rt.log) mlog("%s = %s", label, p ? (const char *)p : "(null)");
}
static void rhex(const char *tag, const void *buf, size_t len) {
    if (!g_rt.log || !g_trace_edges) return;
    static const char H[] = "0123456789abcdef";
    const unsigned char *b = buf;
    char hb[1537];
    size_t cap = len > 512 ? 512 : len, k = 0;
    for (size_t i = 0; i < cap; i++) { hb[k++] = H[b[i] >> 4]; hb[k++] = H[b[i] & 0xf]; hb[k++] = ' '; }
    hb[k] = 0;
    mlog("%s len=%llu %s%s", tag, (unsigned long long)len, hb, cap < len ? "..." : "");
}

// Linux errno numbers the game reads
enum { G_EINTR = 4, G_EAGAIN = 11, G_EINVAL = 22, G_EPROTONOSUPPORT = 93, G_EINPROGRESS = 115 };
static _Thread_local int g_errno;

// LAN mode (server host not loopback): see net_init.
static int g_lan;
static struct in_addr g_local_ip;
static char g_lobby_host[128];   // authoritative lobby from the seated reply (overrides mkwmp.toml)
static int g_lobby_port;
static void eager_seat(void);
static void net_init(void);

// Guest symbols never move once the modules are loaded, but ldr_symbol is a linear strcmp scan over every symbol:
// overrides on hot paths (1.8.0's SDK calls pthread_cond_timedwait and clock_gettime constantly) must not pay it per
// call. A miss (0) is retried.
static uint64_t sym_once(volatile int64_t *slot, const char *name) {
    uint64_t v = (uint64_t)*slot;
    if (!v && g_rt.guest_symbol) plat_xchg(slot, (int64_t)(v = g_rt.guest_symbol(name)));
    return v;
}
#define SYM(name) ({ static volatile int64_t slot_; sym_once(&slot_, name); })

// The SDK's own clock_gettime, called directly (the import override below is not in its way). 1 = a usable timestamp.
static int sdk_clock_gettime(Cpu *c, int id, int64_t *out) {
    if (!g_rt.guest_symbol || !g_rt.call) return 0;
    uint64_t original = SYM("clock_gettime");
    return original && !g_rt.call(c, original, 2, (uint64_t)(unsigned)id, (uint64_t)(uintptr_t)out) &&
           out[1] >= 0 && out[1] < 1000000000;
}

// Game 1.8.0 (SDK 21.4.0): clock_gettime reads each clock through a source pointer (sdk+0xe40018 realtime, +0xe40020
// monotonic). Both start at the tick source (sdk+0xaf300: nn::os::GetSystemTick -> ConvertToTimeSpan), and
// nntimeStartup re-points them at nn::time::GetStandardUserSystemClockCurrentTimespec /
// GetStandardSteadyClockCurrentTimespec. Those read the time service's shared memory, which mkw-recomp does not map
// (its nn::time::Initialize override only returns success), so they answer 0x74 and clock_gettime fails with EAGAIN.
// pthread_cond_timedwait / pthread_cond_clockwait take "now" from that same clock_gettime by a direct call and return
// its errno at once: no timed wait in the SDK can block. The public way back, clock_setclockbase_np, is locked by the
// SDK's first init_array entry, so the monotonic slot gets the tick source back by hand (gameaddrs.h: the row is
// picked by main's build id, and the tick source must be the expected code). CLOCK_REALTIME has no source that works
// here (the tick source would make it time since boot), so it stays with the host clock in ov_clock_gettime.
//
// Called when the SDK's monotonic clock fails (never on 1.0.0, whose clock_gettime computes it inline; its row has no
// slot anyway). 1 = this call changed the source: ask the SDK again.
static int sdk_monotonic_repair_row(const struct game_addrs *ga) {
    if (!ga || !ga->sdk_mono_source_slot ||
        !game_guard_words("SDK tick clock source", ga->sdk_tick_source, ga->sdk_tick_source_words, 8))
        return 0;
    volatile int64_t *slot = (volatile int64_t *)(uintptr_t)ga->sdk_mono_source_slot;
    uint64_t was = (uint64_t)plat_xchg(slot, (int64_t)ga->sdk_tick_source);
    if (was == ga->sdk_tick_source) return 0;   // already the tick source: whatever fails, it is not the source
    static volatile int32_t said;
    if (!plat_xchg(&said, 1))
        mlog("SDK monotonic clock failed (source %#llx): tick source restored, timed waits can block",
             (unsigned long long)was);
    return 1;
}
static int sdk_monotonic_repair(void) {
    static volatile int32_t state;   // 0 undecided (main not mapped yet), 1 the build has a verified repair, 2 it has not
    if (state == 2) return 0;
    int build = game_build();
    if (build == GAME_UNKNOWN) return 0;
    const struct game_addrs *ga = game_addrs_for(build);
    if (state == 1) return sdk_monotonic_repair_row(ga);
    int ok = ga && ga->sdk_mono_source_slot &&
             game_guard_words("SDK tick clock source", ga->sdk_tick_source, ga->sdk_tick_source_words, 8);
    if (!plat_xchg(&state, ok ? 1 : 2) && !ok)
        mlog("SDK monotonic clock failed on game build %s: no verified repair, SDK timed waits cannot block",
             game_build_name(build));
    return ok && sdk_monotonic_repair_row(ga);
}

// The SDK's default monotonic clock, computed here: the epoch its timed waits use once repaired.
static int tick_monotonic(Cpu *c, int64_t *out) {
    if (!g_rt.guest_symbol || !g_rt.call) return 0;
    uint64_t tick = SYM("_ZN2nn2os13GetSystemTickEv"), span = SYM("_ZN2nn2os17ConvertToTimeSpanENS0_4TickE");
    if (!tick || !span) return 0;
    int64_t ns = (int64_t)g_rt.call(c, span, 1, g_rt.call(c, tick, 0));
    if (ns < 0) return 0;
    out[0] = ns / 1000000000;
    out[1] = ns % 1000000000;
    return 1;
}

// SDK pthread_cond_timedwait reads the SDK clock via a direct call, bypassing
// this import override. Keep successful SDK timestamps in the same epoch.
// gRPC reads the output even on SDK clock failure, so supply a fallback then:
// the host's wall clock for CLOCK_REALTIME, the SDK tick clock for CLOCK_MONOTONIC.
static void ov_clock_gettime(Cpu *c) {
    int id = (int)arg_x(c, 0);
    int64_t *out = arg_p(c, 1);
    if (out && sdk_clock_gettime(c, id, out)) {
        ret_x(c, 0);
        return;
    }
    if (!out || (id != 0 && id != 1)) {
        g_errno = G_EINVAL; ret_x(c, (uint64_t)-1); return;
    }
    if (id == 0) {
        plat_realtime(out);
    } else if (!(sdk_monotonic_repair() && sdk_clock_gettime(c, 1, out)) && !tick_monotonic(c, out)) {
        plat_monotonic(out);
    }
    ret_x(c, 0);
}

// The return address of gRPC's pthread_cond_timedwait call in the running game build (gameaddrs.h), 0 when there is
// none: an unknown build, or the instruction before it is not the expected BL (then every timed wait keeps the SDK's
// own clock). Decided once main is mapped: until then (GAME_UNKNOWN) nothing is cached and the next call asks again.
static uint64_t grpc_timedwait_gate(void) {
    static volatile int32_t state;        // 0 undecided, 1 decided
    static volatile int64_t gate;
    if (state) return (uint64_t)gate;
    int build = game_build();
    if (build == GAME_UNKNOWN) return 0;
    const struct game_addrs *ga = game_addrs_for(build);
    uint64_t lr = ga && game_guard_words("gRPC timed wait call", ga->grpc_timedwait_lr - 4, &ga->grpc_timedwait_bl, 1)
                      ? ga->grpc_timedwait_lr : 0;
    plat_xchg(&gate, (int64_t)lr);
    if (!plat_xchg(&state, 1) && !lr)
        mkwmp_log("game build %s: gRPC timed waits keep the SDK clock (no verified call site)", game_build_name(build));
    return lr;
}

// The deadline on another clock: as far after `to_now` as `deadline` is after `from_now` (not before it).
static void deadline_on_clock(const int64_t *deadline, const int64_t *from_now, const int64_t *to_now, int64_t *out) {
    __int128 remaining = ((__int128)deadline[0] - from_now[0]) * 1000000000 + deadline[1] - from_now[1];
    if (remaining < 0) remaining = 0;
    if (remaining > INT64_MAX) remaining = INT64_MAX;
    int64_t ns = to_now[1] + (int64_t)(remaining % 1000000000);
    __int128 sec = (__int128)to_now[0] + (int64_t)(remaining / 1000000000) + ns / 1000000000;
    out[0] = sec > INT64_MAX ? INT64_MAX : (int64_t)sec;
    out[1] = ns % 1000000000;
}

static void ov_cond_timedwait(Cpu *c) {
    uint64_t cond = arg_x(c, 0), mutex = arg_x(c, 1);
    const int64_t *deadline = arg_p(c, 2);
    uint64_t original = SYM("pthread_cond_timedwait");
    uint64_t clockwait = SYM("pthread_cond_clockwait");
    // Only the verified gRPC call site converts its deadline to clock ID 0.
    uint64_t gate = grpc_timedwait_gate();
    if (!gate || c->x[30] != gate || !clockwait || !deadline ||
        deadline[1] < 0 || deadline[1] >= 1000000000) {
        ret_x(c, original ? g_rt.call(c, original, 3, cond, mutex,
                                        (uint64_t)(uintptr_t)deadline) : G_EINVAL);
        return;
    }
    // gRPC built the deadline from this override's CLOCK_REALTIME (gpr_cv_wait converts every deadline to
    // GPR_CLOCK_REALTIME; gpr_now reads clock_gettime(0)): take the time left on that clock and wait it out on the
    // SDK's monotonic clock, the one pthread_cond_clockwait itself reads.
    int64_t now[2], mono[2];
    c->x[0] = 0;
    c->x[1] = (uintptr_t)now;
    ov_clock_gettime(c);
    if (c->x[0] || (!sdk_clock_gettime(c, 1, mono) && !(sdk_monotonic_repair() && sdk_clock_gettime(c, 1, mono)))) {
        ret_x(c, G_EAGAIN);
        return;
    }
    int64_t absolute[2];
    deadline_on_clock(deadline, now, mono, absolute);
    static volatile int32_t announced;
    if (plat_cas(&announced, 1, 0) == 0) rlog("gRPC timed waits use SDK monotonic clock");
    ret_x(c, g_rt.call(c, clockwait, 4, cond, mutex, (uint64_t)1,
                          (uint64_t)(uintptr_t)absolute));
}

// ===========================================================================
// Gates
// ===========================================================================
static void ov_ok(Cpu *c) { ret_x(c, 0); }
static void ov_friend_list_available(Cpu *c) {
    // The Online Play gate at main+0x2228120 requires a ready list.
    uint8_t *available = arg_p(c, 0);
    if (available) *available = 1;
    rlog("friends list available");
    ret_x(c, 0);
}
// Result GetFriendList(int *outCount, Friend *out, const Uid &uid, int offset, int count,
// const FriendFilter &filter): the launcher's roster, so the lobby's NPLN friends have a
// system friend to attach to. The game reads it once after each sign-in, 10 at a time, until
// a page comes back short (main+0x22283e8); its filter is all zero (no presence filter).
static void ov_friend_list(Cpu *c) {
    int32_t *out_count = arg_p(c, 0);
    int offset = (int)arg_x(c, 3), count = (int)arg_x(c, 4);
    int n = friends_list(arg_p(c, 2), offset, count, arg_p(c, 1));
    if (out_count) *out_count = n;
    static volatile int32_t last = -1;
    if (offset == 0 && plat_xchg(&last, n) != n) mlog("friend list: %d friends from offset 0", n);
    ret_x(c, 0);
}

static void ov_show_error_variant(Cpu *c) {
    // SDK error applets need a console service that the shipped host lacks.
    const uint32_t *variant = arg_p(c, 0);
    if (!variant || !g_rt.log) return;
    if (variant[0] == 1) {
        uint32_t result = variant[1];
        mlog("game error result=%#x (%04u-%04u) caller=%#llx",
                    result, 2000u + (result & 0x1ffu), (result >> 9) & 0x1fffu,
                    (unsigned long long)c->x[30]);
    } else if (variant[0] == 2) {
        mlog("game error code=%04u-%04u caller=%#llx",
                    variant[1], variant[2], (unsigned long long)c->x[30]);
    } else {
        mlog("game error variant=%u caller=%#llx",
                    variant[0], (unsigned long long)c->x[30]);
    }
}
// void nnosGetThreadCoreMask(int *ideal, uint64_t *mask, const ThreadType *t):
// application cores 0-2, ideal core 0. nnosSetThreadCoreMask: keep the default.
static void ov_get_thread_core_mask(Cpu *c) {
    int32_t *ideal = arg_p(c, 0);
    uint64_t *mask = arg_p(c, 1);
    if (ideal) *ideal = 0;
    if (mask) *mask = 0x7;
    ret_x(c, 0);
}
static void ov_set_thread_core_mask(Cpu *c) { ret_x(c, 0); }
static void ov_nifm_is_network_available(Cpu *c) { ret_x(c, 1); }
static GuestFn stock_handler(const char *name);
// Going online may wait this long for the launcher's friend roster: the game reads its friend
// list once, about 2.5 s after the sign-in, and a roster that arrives later is not shown.
enum { FRIENDS_WAIT_MS = 1000 };
// Online again after leaving: take the seat back (the launcher freed it 90 s after the
// keepalives stopped; with a live seat this is just an immediate POST).
static void ov_nifm_submit_wait(Cpu *c) {
    (void)c;
    if (seat_keepalive_paused()) {
        SeatReply r;
        if (seat_acquire(&r, 10 * 60 * 1000) == SEAT_SEATED) {
            seat_keepalive_resume();
            friends_online(FRIENDS_WAIT_MS);
            voice_online(g_lobby_host);
        }
    }
}
// The player left online: stop the seat keepalive so the launcher frees the seat.
static void ov_nifm_cancel(Cpu *c) {
    static GuestFn stock;
    if (!stock) stock = stock_handler("_ZN2nn4nifm20CancelNetworkRequestEv");
    if (stock) stock(c);
    if (seat_keepalive_pause()) mlog("left online: seat keepalive paused (the launcher frees the seat in 90 s)");
    crumb_state("online", "0");
    crumb_state("mesh", "%s", "");
    plat_xchg(&g_mesh_role, 0);
    crumb_peers_reset();
    crumb_room(NULL, 0, NULL, 0);
    voice_offline();
}
// In-game player name: the launcher's `name` (mkw-recomp hardcodes "Local").
static void ov_nickname(Cpu *c) {
    char *out = arg_p(c, 0);
    const char *name = mkwmp_config()->name;
    if (out) {
        memset(out, 0, 33);   // nn::account::Nickname: char[33]
        snprintf(out, 33, "%s", name[0] ? name : "Local");
    }
    ret_x(c, 0);
}
static void ov_nifm_ip_config(Cpu *c) {
    // nn::nifm returns network-order in_addr values. Loopback mode advertises
    // 127.0.0.1 (the Pia transport binds there); LAN mode this machine's address.
    // Both overloads land here: (in_addr *) x5, and 1.8.0's (nn::socket::InAddr *) x5 that Pia 7.3 calls when its
    // transport starts (main+0x1661d90, through ov_nifm_ip_config_inaddr); InAddr is the same four network-order
    // bytes (the caller byte-swaps the first one into its ping state).
    net_init();
    uint8_t fields[5][4] = {
        {127, 0, 0, 1}, {255, 0, 0, 0}, {0}, {0}, {0}
    };
    if (g_lan) {
        memcpy(fields[0], &g_local_ip, 4);
        static const uint8_t lan_mask[4] = {255, 255, 255, 0};
        memcpy(fields[1], lan_mask, 4);
    }
    for (int i = 0; i < 5; ++i) {
        void *out = arg_p(c, i);
        if (out) memcpy(out, fields[i], sizeof fields[i]);
    }
    ret_x(c, 0);
}
#define FAKE_NSA_ID 0x1122334455667788ull
static const char kDummyToken[] =
    "eyJhbGciOiJub25lIn0.eyJzdWIiOiJta3dtcC1sb2NhbCIsImlzcyI6Im1rd21wIn0.";
static const char kSecondDummyToken[] =
    "eyJhbGciOiJub25lIn0.eyJzdWIiOiJta3dtcC1sb2NhbC0yIiwiaXNzIjoibWt3bXAifQ.";
static int g_second_player;
static void ov_nsa_available(Cpu *c) { uint8_t *o = arg_p(c, 0); if (o) *o = 1; ret_x(c, 0); }
// Each player needs its own account id: Pia's mesh join fails with 2318-0540 (result 0x647e,
// main+0x1577c6c) when the joiner's station id equals the host's. A launcher player gets the
// id the lobby derives for its name (the one its friends see); local tests keep FAKE_NSA_ID(+1).
static uint64_t nsa_id(void) {
    const char *name = mkwmp_config()->name;
    return name[0] ? friends_account_id(name) : FAKE_NSA_ID + g_second_player;
}
static void ov_nsa_get_id(Cpu *c) {
    uint64_t *o = arg_p(c, 0);
    if (o) *o = nsa_id();
    static volatile int32_t announced;
    if (plat_cas(&announced, 1, 0) == 0) {
        mlog("account id %#llx", (unsigned long long)nsa_id());
        // the crash record's "uid": the user id the lobby derives from the name and prints in its room lines
        const char *name = mkwmp_config()->name;
        if (name[0]) {
            char key[11 + 64] = "mkwmp-user/";
            size_t n = 11;
            for (; *name && n < sizeof key; name++) key[n++] = *name >= 'A' && *name <= 'Z' ? (char)(*name + 32) : *name;
            uint8_t u[16];
            uuid5(kUuidNamespaceUrl, key, n, u);
            crumb_state("uid", "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x", u[0], u[1], u[2], u[3],
                        u[4], u[5], u[6], u[7], u[8], u[9], u[10], u[11], u[12], u[13], u[14], u[15]);
            crumb_state("name", "%s", mkwmp_config()->name);
        }
        crumb_state("account", "%#llx", (unsigned long long)nsa_id());
    }
    ret_x(c, 0);
}
static void ov_nsa_load_token(Cpu *c) {
    uint64_t *out_len = arg_p(c, 0);
    char *buf = arg_p(c, 1);
    uint64_t cap = arg_x(c, 2);
    // The seat ticket (mkw1.<payload>.<sig>) is the account token the lobby verifies in
    // IssuePrearrangedUserToken; local tests without a launcher keep the dummy tokens.
    static char ticket[520];
    eager_seat();
    const char *token = g_second_player ? kSecondDummyToken : kDummyToken;
    if (seat_ticket(ticket, sizeof ticket) ||
        (mkwmp_config()->ticket[0] && snprintf(ticket, sizeof ticket, "%s", mkwmp_config()->ticket) > 0))
        token = ticket;
    size_t len = strlen(token);
    if (buf && cap >= len) { memcpy(buf, token, len); if (out_len) *out_len = len; }
    else if (out_len) *out_len = 0;
    ret_x(c, 0);
}
static void ov_nsa_async_event(Cpu *c) {
    uint64_t event = arg_x(c, 1);
    uint64_t create = SYM("_ZN2nn2os17CreateSystemEventEPNS0_15SystemEventTypeENS0_14EventClearModeEb");
    uint64_t signal = SYM("_ZN2nn2os17SignalSystemEventEPNS0_15SystemEventTypeE");
    if (!event || !create || !signal) { ret_x(c, (uint64_t)-1); return; }
    uint64_t result = g_rt.call(c, create, 3, event, (uint64_t)0, (uint64_t)1);
    if (!result) {
        g_rt.call(c, signal, 1, event);
        rlog("account token async event -> completed");
    }
    ret_x(c, result);
}
static void ov_clock_init(Cpu *c) {
    // The SDK's task reserves 0x90 bytes plus an initialized flag. Use its
    // leading storage for a genuine SystemEvent, leaving that flag clear.
    uint64_t task = arg_x(c, 0);
    uint64_t create = SYM("_ZN2nn2os17CreateSystemEventEPNS0_15SystemEventTypeENS0_14EventClearModeEb");
    uint64_t signal = SYM("_ZN2nn2os17SignalSystemEventEPNS0_15SystemEventTypeE");
    if (!task || !create || !signal) { ret_x(c, (uint64_t)-1); return; }
    uint64_t result = g_rt.call(c, create, 3, task, (uint64_t)0, (uint64_t)1);
    if (!result) g_rt.call(c, signal, 1, task);
    rlog("network clock task -> local completed event");
    ret_x(c, result);
}
static void ov_clock_event(Cpu *c) { /* event is at offset zero; x0 already points to it */ (void)c; }
static void ov_clock_destroy(Cpu *c) {
    uint64_t task = arg_x(c, 0);
    uint64_t destroy = SYM("_ZN2nn2os18DestroySystemEventEPNS0_15SystemEventTypeE");
    if (destroy && task && *(uint8_t *)(uintptr_t)(task + 0x28)) g_rt.call(c, destroy, 1, task);
}
static void ov_nsd_env_id(Cpu *c) {
    char *out = arg_p(c, 0);
    if (out) { memset(out, 0, 8); memcpy(out, "lp1", 3); }
    ret_x(c, 0);
}
static void ov_fs_mount_rom(Cpu *c) { dump_cstr("fs::MountRom(name)", arg_p(c, 0)); ret_x(c, 0); }
static void ov_fs_unmount(Cpu *c) { dump_cstr("fs::Unmount", arg_p(c, 0)); ret_x(c, 0); }

// We virtualize exactly one file: _npln:/npln_config.json (NPLN requires it; the recomp's romfs
// doesn't have it). All other fs calls delegate to the recomp's real handler so content loading is
// unaffected. Our file handle is a distinctive magic value the fs handlers key on.
#define NPLN_CFG_PATH "_npln:/npln_config.json"
#define NPLN_FH 0x6d6b776d706e0001ull   // "mkwmp\0\0\1"
// Empty config is sufficient for the first auth request. The old startup abort
// was gRPC's unavailable poll/eventfd backend, not a missing JSON property.
static const char kNplnConfig[] = "{}";

static int is_cfg(const char *p) { return p && strcmp(p, NPLN_CFG_PATH) == 0; }
static int fs_interesting(const char *p) {
    return p && (p[0] == '_' || strstr(p, "npln") || strstr(p, "config") || strstr(p, "Npln"));
}
static void ov_fs_entry_type(Cpu *c) {
    const char *path = arg_p(c, 1);
    if (fs_interesting(path)) mlog("fs GetEntryType %s", path);
    if (is_cfg(path)) {
        int32_t *out = arg_p(c, 0);
        if (out) *out = 1;   // DirectoryEntryType::File
        ret_x(c, 0);
        return;
    }
    if (g_rt.fs_entry_type) g_rt.fs_entry_type(c); else ret_x(c, 0);
}
static void ov_fs_open_file(Cpu *c) {
    const char *path = arg_p(c, 1);
    if (fs_interesting(path)) mlog("fs OpenFile %s mode=%lld", path, (long long)arg_x(c, 2));
    if (is_cfg(path)) {
        uint64_t *out = arg_p(c, 0);   // FileHandle { void* } -> our magic
        if (out) *out = NPLN_FH;
        mlog("serving %s (%llu bytes)", NPLN_CFG_PATH, (unsigned long long)(sizeof(kNplnConfig) - 1));
        ret_x(c, 0);
        return;
    }
    if (g_rt.fs_open_file) g_rt.fs_open_file(c); else ret_x(c, 0);
}
// GetFileSize(long* out, FileHandle h): h is the u64 in x1.
static void ov_fs_file_size(Cpu *c) {
    if (arg_x(c, 1) == NPLN_FH) {
        int64_t *out = arg_p(c, 0);
        if (out) *out = (int64_t)(sizeof(kNplnConfig) - 1);
        ret_x(c, 0);
        return;
    }
    if (g_rt.fs_file_size) g_rt.fs_file_size(c); else ret_x(c, 0);
}
// ReadFile(size_t* outRead, FileHandle h, long offset, void* buf, size_t size)
static void ov_fs_read_file(Cpu *c) {
    if (arg_x(c, 1) == NPLN_FH) {
        uint64_t *outread = arg_p(c, 0);
        int64_t off = (int64_t)arg_x(c, 2);
        char *buf = arg_p(c, 3);
        uint64_t size = arg_x(c, 4);
        int64_t total = (int64_t)(sizeof(kNplnConfig) - 1);
        int64_t n = (off >= total) ? 0 : (total - off < (int64_t)size ? total - off : (int64_t)size);
        if (buf && n > 0) memcpy(buf, kNplnConfig + off, (size_t)n);
        if (outread) *outread = (uint64_t)n;
        ret_x(c, 0);
        return;
    }
    if (g_rt.fs_read_file) g_rt.fs_read_file(c); else ret_x(c, 0);
}
// CloseFile(FileHandle h): h in x0.
static void ov_fs_close_file(Cpu *c) {
    if (arg_x(c, 0) == NPLN_FH) { ret_x(c, 0); return; }
    if (g_rt.fs_close_file) g_rt.fs_close_file(c); else ret_x(c, 0);
}

// ===========================================================================
// Real-socket bridge to the local gRPC server
// ===========================================================================
#define NFD 128
static PlatSock g_sock[NFD];
static int g_used[NFD];
typedef struct { int level, option, value, used; } SocketOption;
static SocketOption g_options[NFD][16];
static PlatSock g_wake_peer[NFD];
static uint64_t g_wake_count[NFD];
static int g_wake_semaphore[NFD];
static PlatLock g_wake_lock = PLAT_LOCK_INIT;
static int g_ready;
static struct sockaddr_in g_server;

static void net_init(void) {
    if (g_ready) return;
    plat_net_init();
    for (int i = 0; i < NFD; i++) {
        g_sock[i] = PLAT_NO_SOCK;
        g_wake_peer[i] = PLAT_NO_SOCK;
    }
    // Server from mkwmp.toml; env vars override for testing.
    const MkwmpConfig *cfg = mkwmp_config();
    const char *host = getenv("MKWMP_HOST");
    const char *port = getenv("MKWMP_PORT");
    if (!host || !*host) host = g_lobby_host[0] ? g_lobby_host : cfg->host;
    int pnum = port && *port ? atoi(port) : g_lobby_port ? g_lobby_port : cfg->port;
    if (!host || !*host) host = "127.0.0.1";
    g_server.sin_family = AF_INET;
    g_server.sin_port = htons((unsigned short)pnum);
    g_server.sin_addr.s_addr = inet_addr(host);
    if (g_server.sin_addr.s_addr == INADDR_NONE) {
        // Hostname (e.g. mkwlb.anarqz.dev): resolve once, IPv4.
        struct addrinfo hints = {0}, *res = NULL;
        hints.ai_family = AF_INET;
        hints.ai_socktype = SOCK_STREAM;
        if (!getaddrinfo(host, NULL, &hints, &res) && res) {
            g_server.sin_addr = ((struct sockaddr_in *)res->ai_addr)->sin_addr;
            freeaddrinfo(res);
        } else {
            mlog("net: cannot resolve lobby host %s, using 127.0.0.1", host);
            g_server.sin_addr.s_addr = inet_addr("127.0.0.1");
        }
    }
    // LAN mode: a non-loopback server means real peers on other machines. Pia
    // then binds all interfaces, advertises this machine's LAN address, sends
    // NAT probes to the server host and keeps peer addresses as given.
    g_lan = g_server.sin_addr.s_addr != htonl(INADDR_LOOPBACK);
    g_local_ip.s_addr = htonl(INADDR_LOOPBACK);
    if (g_lan) {
        PlatSock probe = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        struct sockaddr_in route = g_server, me; plat_socklen_t ml = sizeof me;
        if (probe != PLAT_NO_SOCK && !connect(probe, (struct sockaddr *)&route, sizeof route) &&
            !getsockname(probe, (struct sockaddr *)&me, &ml))
            g_local_ip = me.sin_addr;
        if (probe != PLAT_NO_SOCK) plat_sock_close(probe);
    }
    g_ready = 1;
    mlog("net bridge ready -> %s:%u (%s, local %s)", host && *host ? host : "127.0.0.1",
                (unsigned)ntohs(g_server.sin_port), g_lan ? "LAN" : "loopback", inet_ntoa(g_local_ip));
}

static int fd_new(PlatSock s) {
    for (int i = 0; i < NFD; i++) if (!g_used[i]) {
        g_used[i] = 1; g_sock[i] = s;
        memset(g_options[i], 0, sizeof g_options[i]);
        return i;
    }
    return -1;
}
static PlatSock fd_get(int fd) { return (fd >= 0 && fd < NFD && g_used[fd]) ? g_sock[fd] : PLAT_NO_SOCK; }

// gRPC probes EventFd before selecting its poll backend. Keep a real counter and
// a coalesced UDP wakeup so the same descriptor participates in poll.
static void ov_eventfd(Cpu *c) {
    net_init();
    PlatSock rd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    PlatSock wr = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    struct sockaddr_in addr = {0};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    plat_socklen_t size = sizeof addr;
    int fd = -1;
    if (rd == PLAT_NO_SOCK || wr == PLAT_NO_SOCK ||
        bind(rd, (struct sockaddr *)&addr, sizeof addr) ||
        getsockname(rd, (struct sockaddr *)&addr, &size) ||
        connect(wr, (struct sockaddr *)&addr, size)) goto fail;
    plat_sock_nonblock(rd);
    plat_sock_nonblock(wr);
    fd = fd_new(rd);
    if (fd < 0) goto fail;
    g_wake_peer[fd] = wr;
    g_wake_count[fd] = arg_x(c, 0);
    g_wake_semaphore[fd] = (arg_x(c, 1) & 1) != 0;
    if (g_wake_count[fd]) send(wr, "w", 1, 0);
    mlog("EventFd -> fd=%d flags=%llu", fd, (unsigned long long)arg_x(c, 1));
    ret_x(c, (uint64_t)fd);
    return;
fail:
    if (rd != PLAT_NO_SOCK) plat_sock_close(rd);
    if (wr != PLAT_NO_SOCK) plat_sock_close(wr);
    g_errno = G_EINVAL;
    ret_x(c, (uint64_t)-1);
}
static void ov_eventfd_read(Cpu *c) {
    int fd = (int)arg_x(c, 0);
    uint64_t *out = arg_p(c, 1);
    if (!out || fd_get(fd) == PLAT_NO_SOCK || g_wake_peer[fd] == PLAT_NO_SOCK) {
        g_errno = G_EINVAL; ret_x(c, (uint64_t)-1); return;
    }
    plat_lock(&g_wake_lock);
    uint64_t count = g_wake_count[fd];
    if (count) {
        *out = g_wake_semaphore[fd] ? 1 : count;
        g_wake_count[fd] -= *out;
        if (!g_wake_count[fd]) { char byte; recv(g_sock[fd], &byte, 1, 0); }
    }
    plat_unlock(&g_wake_lock);
    if (!count) g_errno = G_EAGAIN;
    ret_x(c, count ? 0 : (uint64_t)-1);
}
static void ov_eventfd_write(Cpu *c) {
    int fd = (int)arg_x(c, 0);
    uint64_t value = arg_x(c, 1);
    if (fd_get(fd) == PLAT_NO_SOCK || g_wake_peer[fd] == PLAT_NO_SOCK || value == UINT64_MAX) {
        g_errno = G_EINVAL; ret_x(c, (uint64_t)-1); return;
    }
    plat_lock(&g_wake_lock);
    uint64_t count = g_wake_count[fd];
    int ok = value < UINT64_MAX - count;
    if (ok && value && !count) ok = send(g_wake_peer[fd], "w", 1, 0) == 1;
    if (ok) g_wake_count[fd] += value;
    plat_unlock(&g_wake_lock);
    if (!ok) g_errno = G_EAGAIN;
    ret_x(c, ok ? 0 : (uint64_t)-1);
}

// int nnsocketSocket(int domain, int type, int protocol): always a non-blocking TCP socket to loopback.
static void ov_sock_socket(Cpu *c) {
    net_init();
    dump_args("nnsocketSocket", c, 3);
    PlatSock s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == PLAT_NO_SOCK) { g_errno = G_EINVAL; ret_x(c, (uint64_t)-1); return; }
    plat_sock_nonblock(s);
    int fd = fd_new(s);
    if (fd < 0) { plat_sock_close(s); g_errno = G_EINVAL; ret_x(c, (uint64_t)-1); return; }
    ret_x(c, (uint64_t)fd);
}
// int nnsocketConnect(int fd, const sockaddr*, u32 len): connect to our loopback server.
// Port 443 (NPLN TLS) goes to MKWMP_PORT; any other explicit port, such as the
// plaintext Gamesync endpoint we hand out in session assignments, keeps its port.
static void ov_sock_connect(Cpu *c) {
    int fd = (int)arg_x(c, 0);
    PlatSock s = fd_get(fd);
    if (s == PLAT_NO_SOCK) { g_errno = G_EINVAL; ret_x(c, (uint64_t)-1); return; }
    struct sockaddr_in target = g_server;
    const uint8_t *guest = arg_p(c, 1);
    if (guest && arg_x(c, 2) >= 4 && guest[1] == 2) {
        uint16_t port;
        memcpy(&port, guest + 2, sizeof port);
        if (port && ntohs(port) != 443) target.sin_port = port;
    }
    if (target.sin_port != g_server.sin_port)
        mlog("nnsocketConnect keeps explicit port %u", (unsigned)ntohs(target.sin_port));
    int r = connect(s, (struct sockaddr *)&target, sizeof target);
    if (r == 0) { rlog("nnsocketConnect -> connected (loopback)"); ret_x(c, 0); return; }
    int e = plat_net_error();
    if (e == PLAT_NET_WOULDBLOCK || e == PLAT_NET_INPROGRESS || e == PLAT_NET_ALREADY) {
        g_errno = G_EINPROGRESS; rlog("nnsocketConnect -> EINPROGRESS (loopback)");
    } else if (e == PLAT_NET_ISCONN) { ret_x(c, 0); return; }
    else g_errno = G_EINVAL;
    ret_x(c, (uint64_t)-1);
}
static void ov_sock_close(Cpu *c) {
    int fd = (int)arg_x(c, 0);
    PlatSock s = fd_get(fd);
    if (s != PLAT_NO_SOCK) {
        if (g_wake_peer[fd] != PLAT_NO_SOCK) plat_sock_close(g_wake_peer[fd]);
        g_wake_peer[fd] = PLAT_NO_SOCK;
        g_wake_count[fd] = 0;
        plat_sock_close(s); g_sock[fd] = PLAT_NO_SOCK; g_used[fd] = 0;
    }
    ret_x(c, 0);
}
static void ov_sock_fcntl(Cpu *c) { ret_x(c, 0); }    // sockets already non-blocking
static void ov_sock_setsockopt(Cpu *c) {
    int fd = (int)arg_x(c, 0), level = (int)arg_x(c, 1), option = (int)arg_x(c, 2);
    const int32_t *value = arg_p(c, 3);
    if (fd_get(fd) == PLAT_NO_SOCK || !value || arg_x(c, 4) < 4) {
        g_errno = G_EINVAL; ret_x(c, (uint64_t)-1); return;
    }
    for (int i = 0; i < 16; i++) {
        SocketOption *saved = &g_options[fd][i];
        if (!saved->used || (saved->level == level && saved->option == option)) {
            *saved = (SocketOption){level, option, *value, 1};
            break;
        }
    }
    ret_x(c, 0);
}
// int nnsocketGetSockOpt(fd, level, opt, void* val, u32* len): answer SO_ERROR = 0 (connected).
static void ov_sock_getsockopt(Cpu *c) {
    int fd = (int)arg_x(c, 0), level = (int)arg_x(c, 1), option = (int)arg_x(c, 2);
    void *val = arg_p(c, 3); uint32_t *len = arg_p(c, 4);
    if (fd_get(fd) == PLAT_NO_SOCK || !val || !len || *len < 4) {
        g_errno = G_EINVAL; ret_x(c, (uint64_t)-1); return;
    }
    int32_t result = 0;
    for (int i = 0; i < 16; i++) {
        SocketOption *saved = &g_options[fd][i];
        if (saved->used && saved->level == level && saved->option == option) { result = saved->value; break; }
    }
    if (level == 0xffff && option == 0x1007) {
        plat_socklen_t bytes = sizeof result;
        getsockopt(g_sock[fd], SOL_SOCKET, SO_ERROR, (char *)&result, &bytes);
        if (result) result = G_EINVAL;
    }
    *(int32_t *)val = result; *len = 4;
    ret_x(c, 0);
}
static void ov_sock_shutdown(Cpu *c) {
    PlatSock s = fd_get((int)arg_x(c, 0));
    if (s != PLAT_NO_SOCK) plat_sock_shutdown(s);
    ret_x(c, 0);
}
static void ov_errno_location(Cpu *c) { ret_x(c, (uintptr_t)&g_errno); }

// nnsocketPoll(pollfd* fds, nfds, timeout): poll the real sockets (WSAPoll on Windows). Console pollfd = {s32 fd; s16 events; s16 revents}.
enum { G_POLLIN = 1, G_POLLOUT = 4, G_POLLERR = 8, G_POLLHUP = 0x10, G_POLLNVAL = 0x20 };
static void ov_sock_poll(Cpu *c) {
    uint8_t *g = arg_p(c, 0);
    uint64_t n = arg_x(c, 1);
    int ms = (int)arg_x(c, 2);
    if ((!g && n) || n > NFD) { g_errno = G_EINVAL; ret_x(c, (uint64_t)-1); return; }
    PlatPollFd h[NFD];
    int map[NFD], valid = 0, ready = 0;
    for (uint64_t i = 0; i < n; i++) {
        int32_t fd; int16_t events;
        memcpy(&fd, g + i * 8, 4);
        memcpy(&events, g + i * 8 + 4, 2);
        int16_t zero = 0; memcpy(g + i * 8 + 6, &zero, 2);
        PlatSock s = fd_get(fd);
        if (s == PLAT_NO_SOCK) {
            if (fd >= 0) {
                int16_t invalid = G_POLLNVAL;
                memcpy(g + i * 8 + 6, &invalid, 2);
                ready++;
            }
            continue;
        }
        h[valid].fd = s;
        h[valid].events = (short)(((events & G_POLLIN) ? PLAT_POLLIN : 0) | ((events & G_POLLOUT) ? PLAT_POLLOUT : 0));
        h[valid].revents = 0;
        map[valid] = (int)i;
        valid++;
    }
    int away = 0;
    static int no_core_away = -1;
    if (no_core_away < 0) { const char *v = getenv("MKWMP_NO_CORE_AWAY"); no_core_away = v && !strcmp(v, "1"); }
    int guest = g_rt.guest_thread ? g_rt.guest_thread(c) : c->thread != NULL;
    if (ms && !ready && guest && g_rt.core_away && g_rt.core_back && !no_core_away) away = g_rt.core_away();
    int result = 0;
    if (valid) {
        result = plat_poll(h, valid, ready ? 0 : (ms < 0 ? -1 : ms));
    } else if (!ready && ms) {
        plat_sleep_ms(ms < 0 ? PLAT_FOREVER : (uint32_t)ms);
    }
    int socket_error = result < 0 ? plat_net_error() : 0;
    if (g_rt.core_back && away) g_rt.core_back(&away);
    if (result < 0) {
        g_errno = socket_error == PLAT_NET_INTR ? G_EINTR : G_EINVAL;
        ret_x(c, (uint64_t)-1); return;
    }
    if (valid) {
        if (result > 0) {
            for (int k = 0; k < valid; k++) {
                short re = h[k].revents;
                int16_t gre = (int16_t)(((re & (PLAT_POLLIN | POLLHUP)) ? G_POLLIN : 0) |
                                        ((re & PLAT_POLLOUT) ? G_POLLOUT : 0) |
                                        ((re & POLLERR) ? G_POLLERR : 0) | ((re & POLLHUP) ? G_POLLHUP : 0) |
                                        ((re & POLLNVAL) ? G_POLLNVAL : 0));
                if (gre) { memcpy(g + (size_t)map[k] * 8 + 6, &gre, 2); ready++; }
            }
        }
    }
    ret_x(c, (uint64_t)ready);
}

typedef struct { void *base; uint64_t size; } GIoVec;
typedef struct {
    void *name; uint32_t namelen, pad;
    GIoVec *iov; uint32_t iovlen, pad2;
    void *control; uint32_t controllen; int32_t flags;
} GMsgHdr;
static void socket_msg(Cpu *c, int reading) {
    PlatSock sock = fd_get((int)arg_x(c, 0));
    GMsgHdr *msg = arg_p(c, 1);
    if (sock == PLAT_NO_SOCK || !msg || !msg->iov || msg->iovlen > 1024) {
        g_errno = G_EINVAL; ret_x(c, (uint64_t)-1); return;
    }
    PlatIov buffers[1024];
    for (uint32_t i = 0; i < msg->iovlen; i++) {
        if (msg->iov[i].size > INT_MAX) { g_errno = G_EINVAL; ret_x(c, (uint64_t)-1); return; }
        buffers[i].base = msg->iov[i].base;
        buffers[i].len = (size_t)msg->iov[i].size;
    }
    long result = reading ? plat_sock_recvv(sock, buffers, (int)msg->iovlen)
                          : plat_sock_sendv(sock, buffers, (int)msg->iovlen);
    if (result < 0) {
        g_errno = plat_net_error() == PLAT_NET_WOULDBLOCK ? G_EAGAIN : G_EINVAL;
        ret_x(c, (uint64_t)-1); return;
    }
    size_t transferred = (size_t)result;
    if (reading) msg->flags = 0;
    if (transferred && msg->iovlen) rhex(reading ? "<<< RecvMsg" : ">>> SendMsg", buffers[0].base,
        transferred < buffers[0].len ? transferred : buffers[0].len);
    ret_x(c, transferred);
}
static void ov_sock_sendmsg(Cpu *c) { socket_msg(c, 0); }
static void ov_sock_recvmsg(Cpu *c) { socket_msg(c, 1); }

// int nnsocketGetAddrInfo(node, service, hints, addrinfo** res): return a loopback result so gRPC
// proceeds to create a socket + connect (which we redirect anyway).
typedef struct {
    int32_t ai_flags, ai_family, ai_socktype, ai_protocol;
    uint32_t ai_addrlen; uint32_t _pad;
    char *ai_canonname; void *ai_addr; void *ai_next;
} GAddrInfo;
_Static_assert(offsetof(GAddrInfo, ai_addr) == 0x20, "console addrinfo ABI");
static void ov_sock_freeaddrinfo(Cpu *c) {
    GAddrInfo *ai = arg_p(c, 0);
    while (ai) {
        GAddrInfo *next = ai->ai_next;
        free(ai->ai_addr); free(ai->ai_canonname); free(ai);
        ai = next;
    }
}
// Results of the typed nn::socket::GetAddrInfo(..., AddrInfo **) are not heap blocks but entries of this ring, so
// nothing has to free them: nn::socket::FreeAddrInfo(AddrInfo *) is left to the runtime's stock handler, which does
// nothing (kStockFreeAddrInfo; its override slot went to the second GetCurrentIpConfigInfo overload, see kRegs).
// The game frees a result right after copying the address out; an entry is reused 64 lookups later.
enum { TYPED_AI_RING = 64 };
static struct { GAddrInfo ai; uint8_t sa[16]; } g_typed_ai[TYPED_AI_RING];
static volatile int32_t g_typed_ai_next;

static void sock_getaddrinfo(Cpu *c, const char *label, int cpp_layout) {
    net_init();
    dump_cstr(label, arg_p(c, 0));
    void **res = arg_p(c, 3);
    if (!res) { ret_x(c, (uint64_t)-1); return; }
    *res = NULL;
    GAddrInfo *ai;
    uint8_t *sa;   // console sockaddr_in: len,family,port,addr,zero[8]
    if (cpp_layout) {
        unsigned slot = (unsigned)(plat_inc(&g_typed_ai_next) - 1) % TYPED_AI_RING;
        memset(&g_typed_ai[slot], 0, sizeof g_typed_ai[slot]);
        ai = &g_typed_ai[slot].ai;
        sa = g_typed_ai[slot].sa;
    } else {
        ai = calloc(1, sizeof *ai);
        sa = calloc(1, 16);
        if (!ai || !sa) { free(ai); free(sa); ret_x(c, (uint64_t)-1); return; }
    }
    sa[0] = 16; sa[1] = 2;                      // len=16, AF_INET=2
    memcpy(sa + 2, &g_server.sin_port, 2);      // port (network order)
    // Keep an explicit numeric service other than 443 (see ov_sock_connect).
    const char *service = arg_p(c, 1);
    if (service && *service) {
        char *end;
        unsigned long port = strtoul(service, &end, 10);
        if (!*end && port && port < 65536 && port != 443) {
            uint16_t be = htons((uint16_t)port);
            memcpy(sa + 2, &be, 2);
        }
    }
    memcpy(sa + 4, &g_server.sin_addr, 4);      // 127.0.0.1
    ai->ai_family = 2; ai->ai_socktype = 1; ai->ai_protocol = 6; ai->ai_addrlen = 16;
    if (cpp_layout) ai->ai_canonname = (char *)sa; // nn::socket::AddrInfo address at +0x18
    else ai->ai_addr = sa;
    *res = ai;
    ret_x(c, 0);
}
static void ov_sock_getaddrinfo(Cpu *c) { sock_getaddrinfo(c, "nnsocketGetAddrInfo node", 0); }
static void ov_sock_getaddrinfo_cpp(Cpu *c) { sock_getaddrinfo(c, "nn::socket::GetAddrInfo typed node", 1); }

// Pia's separate datagram path. Keep it loopback-only; gRPC still uses TCP.
//
// Socket(AF_INET, SOCK_RAW = 3, IPPROTO_ICMP = 1) is Pia 7.3's ping socket (game 1.8.0 only; 1.0.0's Pia opens only
// UDP). It is refused with EPROTONOSUPPORT: no socket, nothing sent. Read in the 1.8.0 main (capstone, 2026-11-17):
//   - main+0x1655b20 (socket open, reached from +0x1656db0 `mov w1,#2; mov w2,#3; mov w3,#1`): Socket() < 0 ->
//     GetLastError -> +0x1655dd0 maps the errno (a jump table over 1..110, Linux numbering) to a pia::Result; 93 and
//     22 share the plain "invalid argument" case (result 0x12407, no extra call). No path aborts.
//   - main+0x1660f10 (ping socket open) turns that into result 0x12408. Its only caller, the transport start
//     +0x16fdd10, passes the same result slot straight to +0x1661d90 (the GetCurrentIpConfigInfo wrapper), which
//     overwrites it, and then returns success unconditionally: the failed open is not checked.
//   - A ping is only requested by +0x16fe1c8 -> +0x1660dd0, which returns result 0x12408 while the socket's open
//     count (+0xc) is 0 and never starts the PingJob; the caller only skips setting a flag on a non-zero result.
//     So SendTo and nn::socket::Recv (+0x1656ff0, only used by the ICMP receive +0x1661c10) are never reached, and
//     the close (+0x1660d30) does nothing for a socket that is not open.
// If a live run shows Pia needs the ping after all, answer the echo on our relay instead (GAME-1.8.0-PLAN.md 1.2).
static void ov_pia_socket(Cpu *c) {
    net_init();
    dump_args("Pia Socket", c, 3);
    if (arg_x(c, 0) == 2 && ((unsigned)arg_x(c, 1) & 0xf) == 3 && arg_x(c, 2) == 1) {
        static volatile int32_t said;
        if (plat_cas(&said, 1, 0) == 0)
            mlog("Pia ping socket (raw ICMP) refused: EPROTONOSUPPORT, pings stay off");
        g_errno = G_EPROTONOSUPPORT; ret_x(c, UINT64_MAX); return;
    }
    if (arg_x(c, 0) != 2 || ((unsigned)arg_x(c, 1) & 0xf) != 2) {
        g_errno = G_EINVAL; ret_x(c, UINT64_MAX); return;
    }
    PlatSock s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (s == PLAT_NO_SOCK || plat_sock_nonblock(s)) {
        if (s != PLAT_NO_SOCK) plat_sock_close(s);
        g_errno = G_EINVAL; ret_x(c, UINT64_MAX); return;
    }
    int fd = fd_new(s);
    if (fd < 0) { plat_sock_close(s); g_errno = G_EINVAL; }
    ret_x(c, (uint64_t)(int64_t)fd);
}

static void ov_pia_bind(Cpu *c) {
    PlatSock s = fd_get((int)arg_x(c, 0));
    const uint8_t *guest = arg_p(c, 1);
    struct sockaddr_in sa = {0};
    sa.sin_family = AF_INET;
    sa.sin_addr.s_addr = htonl(g_lan ? INADDR_ANY : INADDR_LOOPBACK);
    // Honour the port Pia asked for: Pia advertises it to peers as its private
    // address (it never asks the socket afterwards). Fall back to ephemeral.
    unsigned requested = 0;
    if (guest && arg_x(c, 2) >= 4 && guest[1] == 2) {
        memcpy(&sa.sin_port, guest + 2, sizeof sa.sin_port);
        requested = ntohs(sa.sin_port);
    }
    if (s == PLAT_NO_SOCK) { g_errno = G_EINVAL; ret_x(c, UINT64_MAX); return; }
    if (bind(s, (struct sockaddr *)&sa, sizeof sa)) {
        sa.sin_port = 0;
        if (bind(s, (struct sockaddr *)&sa, sizeof sa)) { g_errno = G_EINVAL; ret_x(c, UINT64_MAX); return; }
    }
    struct sockaddr_in got; plat_socklen_t gl = sizeof got;
    unsigned actual = getsockname(s, (struct sockaddr *)&got, &gl) ? 0 : ntohs(got.sin_port);
    mlog("Pia UDP bound to %s: requested port %u, got %u", g_lan ? "all interfaces" : "loopback", requested, actual);
    ret_x(c, 0);
}

static void ov_pia_setsockopt(Cpu *c) {
    int opt = (int)arg_x(c, 2);
    int winopt = opt == 4 ? SO_REUSEADDR : opt == 0x20 ? SO_BROADCAST : 0;
    PlatSock s = fd_get((int)arg_x(c, 0));
    const void *value = arg_p(c, 3);
    dump_args("Pia SetSockOpt", c, 5);
    if (arg_x(c, 1) == 0xffff && winopt && s != PLAT_NO_SOCK && value && arg_x(c, 4) == 4) {
        if (setsockopt(s, SOL_SOCKET, winopt, value, 4)) {
            g_errno = G_EINVAL; ret_x(c, UINT64_MAX); return;
        }
    }
    ov_sock_setsockopt(c);
}

// Pia packets: only important events are logged — the first packet to / from each address (who talks
// to whom), and one summary per minute. Logging every packet (and dumping it) on the game threads cost
// the Steam Deck much of its frame rate online (2026-11-15). MKWMP_TRACE_EDGES=1 logs every packet.
static int pia_log_packet(int send, unsigned bytes, uint32_t ip, uint16_t port) {
    static volatile int64_t count[2], total[2];
    static volatile int64_t window_start;
    static uint64_t seen[64];
    static volatile int32_t nseen;
    plat_inc(&count[send]);
    plat_add(&total[send], bytes);
    crumb_peer(ip, port);   // who is in the mesh now, for the crash record
    if (g_trace_edges) return 1;
    int fresh = 0;
    uint64_t key = (uint64_t)send << 48 | (uint64_t)ip << 16 | port;
    int32_t k = nseen, i = 0;
    while (i < k && i < 64 && seen[i] != key) i++;
    if (i == k && k < 64 && plat_cas(&nseen, k + 1, k) == k) { seen[k] = key; fresh = 1; }
    int64_t now = (int64_t)plat_ms(), start = window_start;
    if (now - start >= 60000 && plat_cas(&window_start, now, start) == start) {
        int64_t sc = plat_xchg(&count[1], 0), sb = plat_xchg(&total[1], 0);
        int64_t rc = plat_xchg(&count[0], 0), rb = plat_xchg(&total[0], 0);
        if (start) mlog("Pia UDP last %llds: sent %lld (%lld B), received %lld (%lld B)", (long long)((now - start) / 1000),
                        (long long)sc, (long long)sb, (long long)rc, (long long)rb);
    }
    return fresh;
}
static void ov_pia_sendto(Cpu *c) {
    PlatSock s = fd_get((int)arg_x(c, 0));
    const char *buf = arg_p(c, 1);
    uint64_t len = arg_x(c, 2);
    if (s == PLAT_NO_SOCK || !buf || len > 65507) {
        g_errno = G_EINVAL; ret_x(c, UINT64_MAX); return;
    }
    const uint8_t *guest = arg_p(c, 4);
    struct sockaddr_in sa = {0};
    sa.sin_family = AF_INET;
    sa.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    // NAT probes reach the local responder. A peer endpoint learned from a
    // local session must keep its own port instead of going to that responder.
    // Every peer is local in this setup: any other IPv4 peer address (e.g. one
    // advertised through Gamesync signaling) is redirected to loopback, same port.
    int is_probe = len == 16 && !buf[0] && !buf[1] && !buf[2] && (buf[3] == 0 || (buf[3] >= 101 && buf[3] <= 103));
    if (g_lan) {
        // Real peers: send where Pia says; NAT probes go to the server host.
        if (is_probe) {
            sa.sin_addr = g_server.sin_addr;
            sa.sin_port = htons(10025);
        } else if (guest && arg_x(c, 5) >= 16 && guest[1] == 2 && (guest[2] || guest[3])) {
            memcpy(&sa.sin_port, guest + 2, sizeof sa.sin_port);
            memcpy(&sa.sin_addr, guest + 4, 4);
        }
    } else if (guest && arg_x(c, 5) >= 16 && guest[1] == 2 && !is_probe && (guest[2] || guest[3])) {
        memcpy(&sa.sin_port, guest + 2, sizeof sa.sin_port);
        if (!(guest[4] == 127 && guest[5] == 0 && guest[6] == 0 && guest[7] == 1)) {
            static volatile int32_t redirected;
            if (plat_inc(&redirected) <= 20)
                mlog("Pia UDP peer %u.%u.%u.%u:%u redirected to loopback", guest[4], guest[5], guest[6], guest[7],
                     ntohs(sa.sin_port));
        }
    } else if (guest && arg_x(c, 5) >= 16 && guest[1] == 2 && guest[4] == 127 &&
        guest[5] == 0 && guest[6] == 0 && guest[7] == 1) {
        memcpy(&sa.sin_port, guest + 2, sizeof sa.sin_port);
    } else if (len == 16 && !buf[0] && !buf[1] && !buf[2] &&
               (buf[3] == 0 || (buf[3] >= 101 && buf[3] <= 103))) {
        sa.sin_port = htons(10025);
    }
    if (!sa.sin_port) {
        static volatile int32_t rejected;
        if (plat_inc(&rejected) <= 20)
            mlog("Pia UDP send rejected: %u bytes, addr family %u port %u ip %u.%u.%u.%u", (unsigned)len,
                 guest && arg_x(c, 5) >= 16 ? guest[1] : 0xff,
                 guest && arg_x(c, 5) >= 16 ? (unsigned)((guest[2] << 8) | guest[3]) : 0,
                 guest && arg_x(c, 5) >= 16 ? guest[4] : 0, guest && arg_x(c, 5) >= 16 ? guest[5] : 0,
                 guest && arg_x(c, 5) >= 16 ? guest[6] : 0, guest && arg_x(c, 5) >= 16 ? guest[7] : 0);
        g_errno = G_EINVAL; ret_x(c, UINT64_MAX); return;
    }
    if (len != 16 && pia_log_packet(1, (unsigned)len, sa.sin_addr.s_addr, sa.sin_port)) {
        struct sockaddr_in local; plat_socklen_t ll = sizeof local;
        unsigned lport = getsockname(s, (struct sockaddr *)&local, &ll) ? 0 : ntohs(local.sin_port);
        int has_guest = guest && arg_x(c, 5) >= 16;
        mlog("Pia UDP send %u -> %s:%u (%u bytes; guest fam %u %u.%u.%u.%u:%u)", lport, inet_ntoa(sa.sin_addr),
             ntohs(sa.sin_port), (unsigned)len, has_guest ? guest[1] : 0xff, has_guest ? guest[4] : 0,
             has_guest ? guest[5] : 0, has_guest ? guest[6] : 0, has_guest ? guest[7] : 0,
             has_guest ? (unsigned)((guest[2] << 8) | guest[3]) : 0);
    }
    if (g_trace_edges) rhex(">>> Pia UDP", buf, (size_t)len);
    int sent =sendto(s, buf, (int)len, 0, (struct sockaddr *)&sa, sizeof sa);
    if (sent < 0) g_errno = plat_net_error() == PLAT_NET_WOULDBLOCK ? G_EAGAIN : G_EINVAL;
    ret_x(c, (uint64_t)(int64_t)sent);
}

static void ov_pia_recvfrom(Cpu *c) {
    PlatSock s = fd_get((int)arg_x(c, 0));
    char *buf = arg_p(c, 1);
    uint64_t cap = arg_x(c, 2);
    uint8_t *out = arg_p(c, 4);
    uint32_t *out_len = arg_p(c, 5);
    if (s == PLAT_NO_SOCK || !buf || cap > INT_MAX || (out && (!out_len || *out_len < 16))) {
        g_errno = G_EINVAL; ret_x(c, UINT64_MAX); return;
    }
    struct sockaddr_in sa;
    plat_socklen_t addrlen = sizeof sa;
    int got = recvfrom(s, buf, (int)cap, 0, (struct sockaddr *)&sa, &addrlen);
    if (got < 0) {
        int err = plat_net_error();
        g_errno = err == PLAT_NET_WOULDBLOCK || err == PLAT_NET_CONNRESET ? G_EAGAIN : G_EINVAL;
    } else if (out) {
        if (got != 16 && pia_log_packet(0, (unsigned)got, sa.sin_addr.s_addr, sa.sin_port)) {
            struct sockaddr_in local; plat_socklen_t ll = sizeof local;
            unsigned lport = getsockname(s, (struct sockaddr *)&local, &ll) ? 0 : ntohs(local.sin_port);
            mlog("Pia UDP recv %u <- %s:%u (%d bytes)", lport, inet_ntoa(sa.sin_addr), ntohs(sa.sin_port), got);
        }
        memset(out, 0, 16);
        out[0] = 16; out[1] = 2;
        memcpy(out + 2, &sa.sin_port, 2);
        memcpy(out + 4, &sa.sin_addr, 4);
        *out_len = 16;
    }
    ret_x(c, (uint64_t)(int64_t)got);
}

// ssize_t nn::socket::Recv(int fd, void *buf, size_t n, MsgFlag flags): RecvFrom without the address (game 1.8.0:
// Pia 7.3's ICMP receive, main+0x1656ff0). Not in kRegs: with the ping socket refused (ov_pia_socket) the game never
// calls it, and the runtime's 64 override slots are all taken (see kRegs). Register it if a ping socket is ever
// opened here; tests/test_bridge.c exercises it meanwhile.
__attribute__((unused)) static void ov_pia_recv(Cpu *c) {
    PlatSock s = fd_get((int)arg_x(c, 0));
    char *buf = arg_p(c, 1);
    uint64_t cap = arg_x(c, 2);
    if (s == PLAT_NO_SOCK || !buf || cap > INT_MAX) {
        g_errno = G_EINVAL; ret_x(c, UINT64_MAX); return;
    }
    int got = recv(s, buf, (int)cap, 0);   // the probe's sockets are non-blocking, so MSG_DONTWAIT is implied
    if (got < 0) {
        int err = plat_net_error();
        g_errno = err == PLAT_NET_WOULDBLOCK || err == PLAT_NET_CONNRESET ? G_EAGAIN : G_EINVAL;
    }
    ret_x(c, (uint64_t)(int64_t)got);
}

static void ov_pia_errno(Cpu *c) { ret_x(c, (unsigned)g_errno); }

// ===========================================================================
// SDK SSL setup and certificate hooks; actual TLS is embedded OpenSSL.
// ===========================================================================
static GuestFn original_dispatch;

// Everything profile_dispatch matches or reads through is per game build: A is the running build's row of
// gameaddrs.h, copied by install_profile_dispatch_trace once its guards passed (a field whose guard failed is
// zeroed). Until then, and for an unknown build, A is all zero and every check below is off: a zero address never
// matches (at()).
static struct game_addrs A;
static inline int at(uint64_t value, uint64_t addr) { return addr && value == addr; }

// JoinRandomSessionJob step functions (WaitAutoConnect, ..., StartMeshStartup, DisconnectNetwork, CreateMesh,
// JoinMesh).
static int is_pia_join_step(uint64_t target) {
    for (int i = 0; i < GAME_PIA_STEPS; i++)
        if (at(target, A.pia_join_step[i])) return 1;
    return 0;
}

// Opt-in (MKWMP_TRACE_EDGES=1) coverage of indirect calls made from NPLN
// matchmaking/game-session/Gamesync code. Each distinct caller->target pair is
// logged once with a millisecond tick, so a run shows which code executed
// between a match assignment and the resulting error.
// Traced code ranges (guest addresses): A.trace_ranges, i.e. NPLN, Pia, the game's matchmaking
// sequence (AIGlobalSeq*), the online-session manager and the race controller.

// Watchpoints: every indirect call to these targets is logged (first 20,
// then every 200th), with a state dump where useful. See research/docs/NETCODE.md.
// The targets are A.watch[] in this order, then A.errsvc_post for W_COMM_ERROR (error service slot 6: post
// communication error, reason 37, kind 12). The names are the 1.0.0 functions' (logs stay comparable).
enum { W_START_P2P, W_P2P_INIT_SEQ, W_US_JOB, W_GAME_CB, W_PLUGIN_A, W_PLUGIN_B, W_COMM_ERROR, W_COUNT };
_Static_assert(W_COMM_ERROR == GAME_WATCH, "game_addrs.watch holds every watchpoint but comm_error");
static uint64_t watch_addr(int w) { return w == W_COMM_ERROR ? A.errsvc_post : A.watch[w]; }
static const char *const kWatchName[W_COUNT] = {"start_p2p", "p2p_init_seq", "us_job_run", "game_cb_223d768",
                                                "plugin_16279e4", "plugin_1629020", "comm_error"};
// Optional field dump at x0 + off (W_US_JOB: A.us_job_dump): count qwords.
static const uint8_t kWatchDumpCount[W_COUNT] = {0, 0, 12, 0, 0, 0, 0};
static volatile int32_t g_watch_hits[W_COUNT];

static int guest_readable(const void *p, size_t n) { return plat_mem_readable(p) >= n; }

static void trace_watch(const Cpu *c) {
    for (int w = 0; w < W_COUNT; w++) {
        if (!at(c->pc, watch_addr(w))) continue;
        int32_t n = plat_inc(&g_watch_hits[w]);
        if (n > 20 && n % 200) return;
        const uint8_t *mgr = (const uint8_t *)(uintptr_t)c->x[0];
        if (w == W_START_P2P && guest_readable(mgr + A.mgr_count, 0x18)) {
            // FUN_01a9637c(mgr, -1): slots at +mgr_slots (mgr_slot_size bytes each), count
            // +mgr_count, current index +mgr_cur; slot handle count at slot+0.
            uint64_t slots; uint32_t count, cur, handles = 0xffffffff;
            memcpy(&slots, mgr + A.mgr_slots, 8); memcpy(&count, mgr + A.mgr_count, 4); memcpy(&cur, mgr + A.mgr_cur, 4);
            const uint8_t *slot =
                (const uint8_t *)(uintptr_t)(slots + (uint64_t)(cur < count ? cur : 0) * A.mgr_slot_size);
            if (slots && guest_readable(slot, 4)) memcpy(&handles, slot, 4);
            g_rt.log("mkwmp: watch #%ld %s caller=main+%#llx mgr=%p slots=%#llx count=%u cur=%u handles=%u",
                        (long)n, kWatchName[w], (unsigned long long)(c->x[30] - GUEST_MAIN(0)),
                        (const void *)mgr, (unsigned long long)slots, count, cur, handles);
            mkwmp_log("watch #%ld %s caller=main+%#llx count=%u cur=%u handles=%u", (long)n, kWatchName[w],
                      (unsigned long long)(c->x[30] - GUEST_MAIN(0)), count, cur, handles);
        } else if (w == W_COMM_ERROR) {
            // x1 = error info {u32, u32, u64, u32}; also the caller's caller via x29.
            const uint8_t *info = (const uint8_t *)(uintptr_t)c->x[1];
            const uint8_t *fp = (const uint8_t *)(uintptr_t)c->x[29];
            uint64_t lr[6] = {0}, w[3] = {0};
            for (int k = 0; k < 6 && guest_readable(fp, 16); k++) {
                memcpy(&lr[k], fp + 8, 8);
                uint64_t next; memcpy(&next, fp, 8);
                fp = (const uint8_t *)(uintptr_t)next;
            }
            if (guest_readable(info, 24)) memcpy(w, info, 24);
            mkwmp_log("watch #%ld comm_error caller=main+%#llx frames=%#llx %#llx %#llx %#llx %#llx %#llx info=%016llx %016llx %016llx",
                      (long)n, (unsigned long long)(c->x[30] - GUEST_MAIN(0)),
                      (unsigned long long)(lr[0] - GUEST_MAIN(0)), (unsigned long long)(lr[1] - GUEST_MAIN(0)),
                      (unsigned long long)(lr[2] - GUEST_MAIN(0)), (unsigned long long)(lr[3] - GUEST_MAIN(0)),
                      (unsigned long long)(lr[4] - GUEST_MAIN(0)), (unsigned long long)(lr[5] - GUEST_MAIN(0)),
                      (unsigned long long)w[0], (unsigned long long)w[1], (unsigned long long)w[2]);
            g_rt.log("mkwmp: comm_error caller=main+%#llx frames=%#llx %#llx %#llx %#llx",
                        (unsigned long long)(c->x[30] - GUEST_MAIN(0)), (unsigned long long)(lr[0] - GUEST_MAIN(0)),
                        (unsigned long long)(lr[1] - GUEST_MAIN(0)), (unsigned long long)(lr[2] - GUEST_MAIN(0)),
                        (unsigned long long)(lr[3] - GUEST_MAIN(0)));
        } else {
            mkwmp_log("watch #%ld %s caller=main+%#llx x0=%#llx x1=%#llx x2=%#llx x3=%#llx", (long)n,
                      kWatchName[w], (unsigned long long)(c->x[30] - GUEST_MAIN(0)),
                      (unsigned long long)c->x[0], (unsigned long long)c->x[1],
                      (unsigned long long)c->x[2], (unsigned long long)c->x[3]);
            unsigned dump_off = w == W_US_JOB ? A.us_job_dump : 0;
            if (kWatchDumpCount[w] && guest_readable(mgr + dump_off, kWatchDumpCount[w] * 8u)) {
                char line[512]; int len = 0;
                for (int k = 0; k < kWatchDumpCount[w] && len < (int)sizeof line - 24; k++) {
                    uint64_t v; memcpy(&v, mgr + dump_off + k * 8, 8);
                    len += snprintf(line + len, sizeof line - len, " +%#x=%#llx",
                                    dump_off + (unsigned)k * 8, (unsigned long long)v);
                }
                mkwmp_log("watch %s fields:%s", kWatchName[w], line);
            }
        }
        return;
    }
}
static int in_trace_range(uint64_t a) {
    for (int i = 0; i < GAME_TRACE_RANGES; i++)
        if (A.trace_ranges[i][1] && a >= A.trace_ranges[i][0] && a < A.trace_ranges[i][1]) return 1;
    return 0;
}
#define EDGE_SLOTS 16384
static int g_trace_edges;
static volatile int32_t g_mesh_role;   // how this player got into the Pia mesh: 1 made it, 2 joined it (0: in none)
static int g_skip_referee_timeout;
static volatile int64_t g_edges[EDGE_SLOTS];

static void trace_edge(uint64_t caller, uint64_t target, const Cpu *c) {
    if (!in_trace_range(caller)) return;
    uint64_t key = ((caller - GUEST_MAIN(0)) << 32) ^ (target - GUEST_MAIN(0));
    if (!key) return;
    uint64_t h = key * 0x9E3779B97F4A7C15ull;
    for (unsigned probe = 0; probe < 64; probe++) {
        volatile int64_t *slot = &g_edges[(h + probe) & (EDGE_SLOTS - 1)];
        int64_t seen = plat_cas(slot, (int64_t)key, 0);
        if (seen == (int64_t)key) return;
        if (seen == 0) {
            mlog("edge t=%llu caller=main+%#llx target=main+%#llx x0=%#llx x1=%#llx x2=%#llx",
                        (unsigned long long)plat_ms(),
                        (unsigned long long)(caller - GUEST_MAIN(0)),
                        (unsigned long long)(target - GUEST_MAIN(0)),
                        (unsigned long long)c->x[0], (unsigned long long)c->x[1],
                        (unsigned long long)c->x[2]);
            return;
        }
    }
}

// The sdk's memmove (1.0.0: 0x70046966a0; the sdk module moves between game builds, so it is looked up by name)
// copies a C string from x1 when a string is constructed (error messages such as
// "Timed out to get my UserSession." and resource names).
// Mirror the ones built by NPLN matchmaking/Gamesync code into mkw.log.
static uint64_t g_sdk_memmove;
static void trace_sdk_message(const Cpu *c) {
    const char *msg = (const char *)(uintptr_t)c->x[1];
    if ((uintptr_t)msg < 0x10000) return;
    size_t avail = plat_mem_readable(msg);
    if (!avail) return;
    char buf[400];
    size_t n = 0;
    for (; n < sizeof buf - 1 && n < avail && msg[n]; n++)
        buf[n] = (msg[n] >= 0x20 && msg[n] < 0x7f) ? msg[n] : '.';
    buf[n] = 0;
    mlog("sdk-msg t=%llu caller=main+%#llx \"%s\"",
                (unsigned long long)plat_ms(),
                (unsigned long long)(c->x[30] - GUEST_MAIN(0)), buf);
}

// Friend-room race start (research/docs/SYNCSTATE-RE.md 7): the host's SyncState (FUN_01a82d84) waits for
// every joiner's ready state; log on change what PrivateRoomMain / ReadyVoteSetter / SyncState see, and the
// host's start event 0x9d424b78, on both machines. MKWMP_TRACE_ROOM=0 turns it off.
static void trace_room_start(uint64_t target, const uint8_t *node, const uint8_t *arg) {
    if (!A.game_global) return;
    const uint8_t *g = *(const uint8_t **)(uintptr_t)A.game_global;
    if (!g || !guest_readable(g + A.g_flags, 8) || !guest_readable(node, A.room_node_size)) return;
    uint32_t f110, f114, st[4] = {0}, snap[12] = {0};
    memcpy(&f110, g + A.g_flags, 4);
    memcpy(&f114, g + A.g_flags2, 4);
    const uint8_t *h = guest_readable(g + A.g_handle, 8) ? *(const uint8_t **)(g + A.g_handle) : NULL;
    const uint8_t *s = h && guest_readable(h, 16) ? *(const uint8_t **)(h + 8) : NULL;
    if (s && guest_readable(s + A.sess_states, 16)) memcpy(st, s + A.sess_states, 16);
    const uint8_t *r = guest_readable(g + A.g_room, 8) ? *(const uint8_t **)(g + A.g_room) : NULL;
    uint32_t play = 0, vote = 0;
    uint8_t has = 0;
    if (r && guest_readable(r, 0x50)) {
        memcpy(&play, r + A.room_play, 4); memcpy(&vote, r + A.room_vote_bits, 4); has = r[A.room_has];
    }
    static uint32_t last[3][12];
    int k = target == A.room_sync ? 0 : target == A.room_main ? 1 : target == A.room_vote ? 2 : 3;
    if (k == 3) {   // PrivateRoomMain event handler: only the host's start event
        uint32_t id = 0;
        if (!guest_readable(arg, 8)) return;
        memcpy(&id, arg + 4, 4);
        if (id != A.room_start_event) return;
        mlog("room start event: flags %#x/%#x states %u,%u,%u,%u", f110, f114, st[0], st[1], st[2], st[3]);
        return;
    }
    snap[0] = f110; snap[1] = f114; memcpy(snap + 2, st, 16); snap[6] = play; snap[7] = vote; snap[8] = has;
    if (k == 0) { memcpy(&snap[9], node + A.sync_a, 4); memcpy(&snap[10], node + A.sync_b, 4); }
    if (k == 1) {
        snap[9] = node[A.main_a]; snap[10] = node[A.main_b];
        snap[11] = node[A.main_c] | (uint32_t)node[A.main_d] << 8;
    }
    if (k == 2) {
        const uint8_t *p = *(const uint8_t **)(node + A.vote_ptr);
        snap[9] = p && guest_readable(p, 1) ? *p : 2;
    }
    if (!memcmp(last[k], snap, sizeof snap)) return;
    memcpy(last[k], snap, sizeof snap);
    mlog("%s: flags %#x/%#x states %u,%u,%u,%u playing %#x vote %#x has %u a=%#x b=%#x c=%#x",
         k == 0 ? "SyncState" : k == 1 ? "PrivateRoomMain" : "ReadyVoteSetter",
         f110, f114, st[0], st[1], st[2], st[3], play, vote, has, snap[9], snap[10], snap[11]);
}

// End-of-battle idle kick 0x9b928162 (research/docs/RACEEND-RE.md): FUN_0035ce38 kicks a player whose
// move counter (ctrl+0x1e8, frames with a position change >= 0.1) is under 20 after 3000 frames, at the
// moment its battle ends. MKWMP_TRACE_KART=1 logs the controller state once per second and on change;
// MKWMP_NO_IDLE_KICK=1 keeps the counter at 20 (diagnostic guard).
static int g_trace_kart = -1, g_no_idle_kick;
static void trace_kart_watchdog(uint8_t *ctrl) {
    if (!guest_readable(ctrl, A.kart_size)) return;
    const uint8_t *info = *(const uint8_t **)(ctrl + A.kart_info);
    int32_t player = -1;
    if (info && guest_readable(info, 0x40)) memcpy(&player, info + A.kart_player, 4);
    if (player < 0 || player >= 24) return;
    uint32_t state, frames, cc, strikes;
    int32_t moves;
    float pos[2];
    memcpy(&state, ctrl + A.kart_state, 4); memcpy(&frames, ctrl + A.kart_frames, 4);
    memcpy(&cc, ctrl + A.kart_error, 4);
    memcpy(&strikes, ctrl + A.kart_strikes, 4); memcpy(&moves, ctrl + A.kart_moves, 4);
    memcpy(pos, ctrl + A.kart_pos, 8);
    if (g_no_idle_kick && A.kart_idle_kick && state == 5 && frames > 2900 && moves < 20) {
        moves = 20;
        memcpy(ctrl + A.kart_moves, &moves, 4);
        static volatile int32_t said;
        if (plat_inc(&said) <= 5) mlog("kart watchdog: move count raised (player %d)", player);
    }
    if (!g_trace_kart) return;
    static uint32_t last_state[24];
    static uint64_t last_ms[24];
    uint64_t now = plat_ms();
    if (state == last_state[player] && now - last_ms[player] < 1000) return;
    last_state[player] = state;
    last_ms[player] = now;
    mlog("kart watchdog: player %d state %u frames %u moves %d fe=%u cc=%#x strikes %u pos %.1f,%.1f",
         player, state, frames, moves, ctrl[A.kart_fe], cc, strikes, (double)pos[0], (double)pos[1]);
}

static void profile_dispatch(Cpu *c) {
    if (g_trace_kart < 0) {
        const char *e = getenv("MKWMP_TRACE_KART"), *k = getenv("MKWMP_NO_IDLE_KICK");
        g_trace_kart = e && e[0] == '1';
        g_no_idle_kick = k && k[0] == '1';
    }
    if ((g_trace_kart || g_no_idle_kick) && (at(c->pc, A.kart_entry[0]) || at(c->pc, A.kart_entry[1])))
        trace_kart_watchdog((uint8_t *)(uintptr_t)c->x[0]);
    static int room_trace = -1;
    if (room_trace < 0) {
        const char *e = getenv("MKWMP_TRACE_ROOM");
        room_trace = e && e[0] == '1';   // diagnostics only (SYNCSTATE-RE.md); off for players
    }
    if (room_trace && (at(c->pc, A.room_sync) || at(c->pc, A.room_main) || at(c->pc, A.room_vote) ||
                       at(c->pc, A.room_event)))
        trace_room_start(c->pc, (const uint8_t *)(uintptr_t)c->x[0], (const uint8_t *)(uintptr_t)c->x[1]);
    if (g_trace_edges && g_rt.log) {
        trace_watch(c);
        if (at(c->pc, g_sdk_memmove) && in_trace_range(c->x[30]))
            trace_sdk_message(c);
        trace_edge(c->x[30], c->pc, c);
    }
    // The lifted BLR uses rt_dispatch. This call retrieves the completed
    // BulkGetDocuments result into the guest's x8 return buffer.
    uint32_t *result = at(c->x[30], A.bulkget_lr) && at(c->pc, A.bulkget_fn)
        ? (void *)(uintptr_t)c->x[8] : NULL;
    uint64_t target = c->pc;
    uint64_t caller = c->x[30];
    // (Every read through a matched register below is checked with guest_readable first: a row whose address is
    // only "high" confidence must not fault the game.)
    if (at(target, A.ctl_event_sink) && c->x[1] && g_rt.log &&
        guest_readable((const void *)(uintptr_t)c->x[1], 7 * sizeof(uint32_t))) {
        uint32_t event[7];
        memcpy(event, (const void *)(uintptr_t)c->x[1], sizeof event);
        if (event[0])
            mlog("controller event kind=%u detail=%#x/%#x/%#x/%#x/%#x/%#x caller=%#llx",
                        event[0], event[1], event[2], event[3], event[4], event[5], event[6],
                        (unsigned long long)caller);
    }
    // These BLRs select the next controller phase and prepare a new operation.
    uint32_t *next_phase = at(caller, A.ctl_next_lr) ? (void *)(uintptr_t)c->x[1] : NULL;
    uint64_t operation = next_phase ? c->x[2] : c->x[1];
    int trace_operation = (next_phase || at(caller, A.ctl_prepare_lr)) &&
                          (operation == 2 || operation == 3 || operation == 10);
    uint32_t desired_mode = 0, desired_phase = 0, game_flags = 0;
    if (trace_operation) {
        const uint8_t *controller = (void *)(uintptr_t)c->x[0];
        if (guest_readable(controller + A.ctl_mode, 4) && guest_readable(controller + A.ctl_phase, 4)) {
            memcpy(&desired_mode, controller + A.ctl_mode, sizeof desired_mode);
            memcpy(&desired_phase, controller + A.ctl_phase, sizeof desired_phase);
        }
        const uint8_t *global = A.game_global ? *(const uint8_t **)(uintptr_t)A.game_global : NULL;
        if (global && guest_readable(global + A.g_flags3, 4)) memcpy(&game_flags, global + A.g_flags3, sizeof game_flags);
    }
    // The game's sequence callbacks are upstream of the Pia join job.
    const uint8_t *seq = (at(target, A.seq_wait) || at(target, A.seq_start))
        ? (void *)(uintptr_t)c->x[0] : NULL;
    uint32_t seq_before[2] = {0};
    if (seq && !guest_readable(seq + A.seq_phase, sizeof seq_before)) seq = NULL;
    if (seq) memcpy(seq_before, seq + A.seq_phase, sizeof seq_before);
    // JoinRandomSessionJob stores its current step and error inline. Only
    // inspect known step functions for this build and log state transitions.
    const uint8_t *job = is_pia_join_step(target) ? (void *)(uintptr_t)c->x[0] : NULL;
    uint64_t before = 0;
    if (job && !guest_readable(job, A.join_error + 4u)) job = NULL;
    if (job) memcpy(&before, job + A.join_step, sizeof before);
    // The job's last two steps say how this player got into the mesh: CreateMesh (nobody was there: this player made
    // it, and hosts it until the lobby's order says otherwise) or JoinMesh. For the crash record.
    if (job) {
        int32_t now = at(target, A.pia_join_step[GAME_PIA_STEPS - 2]) ? 1 : at(target, A.pia_join_step[GAME_PIA_STEPS - 1]) ? 2 : 0;
        if (now && plat_xchg(&g_mesh_role, now) != now) {
            crumb_state("mesh", "%s", now == 1 ? "created" : "joined");
            crumb(now == 1 ? "pia: CreateMesh (this player made the mesh)" : "pia: JoinMesh (this player joined a mesh)");
        }
    }
    // Experiment (MKWMP_SKIP_REFEREE_TIMEOUT=1): drop the race-start referee
    // timeout error (FUN_021271c8 -> FUN_01e46958 posts id 0x606314c9 through
    // error service slot 6) so the race can continue without a server referee.
    // MKWMP_SKIP_REFEREE_TIMEOUT=2 drops every communication error (any id).
    // Every communication error the game posts (an important event): its id and the guest return
    // addresses that led there, so the producer is known (e.g. 0x9b928162: FUN_0035ce38 or FUN_020d43dc,
    // RACEEND-RE.md). Once per id and 10 s; the error-service entry is reached via a vtable call.
    int error_post = at(target, A.errsvc_post);
    if (error_post && c->x[1] && guest_readable((const void *)(uintptr_t)c->x[1], 4)) {
        uint32_t id = *(const uint32_t *)(uintptr_t)c->x[1];
        static uint32_t last_id;
        static uint64_t last_ms;
        uint64_t now = plat_ms();
        if (id != last_id || now - last_ms > 10000) {
            last_id = id;
            last_ms = now;
            uint64_t lr[6] = {0};
            const uint8_t *fp = (const uint8_t *)(uintptr_t)c->x[29];
            for (int k = 0; k < 6 && guest_readable(fp, 16); k++) {
                memcpy(&lr[k], fp + 8, 8);
                uint64_t next;
                memcpy(&next, fp, 8);
                fp = (const uint8_t *)(uintptr_t)next;
            }
            mlog("communication error %#x posted: caller main+%#llx frames main+%#llx main+%#llx main+%#llx main+%#llx main+%#llx",
                 id, (unsigned long long)(caller - GUEST_MAIN(0)), (unsigned long long)(lr[0] - GUEST_MAIN(0)),
                 (unsigned long long)(lr[1] - GUEST_MAIN(0)), (unsigned long long)(lr[2] - GUEST_MAIN(0)),
                 (unsigned long long)(lr[3] - GUEST_MAIN(0)), (unsigned long long)(lr[4] - GUEST_MAIN(0)));
        }
    }
    // MKWMP_SKIP_ERROR=<hex id>: drop that one communication error, opt-in. E.g. 0x9b928162, the end-of-battle
    // move-count kick (kart watchdog FUN_020d4ab0 -> FUN_02624bf0, RACEEND-RE.md) the Steam Deck got under Proton at
    // the end of every battle round while driving; dropping it, a 3-round friend battle ran to the end. It used to be
    // the default under Wine/Proton; the Proton support was removed (native Linux comes with mkw-recomp's plugin API),
    // so a native Deck run must show whether the kick still happens there.
    static uint32_t skip_id = 1;
    if (skip_id == 1) {
        const char *e = getenv("MKWMP_SKIP_ERROR");
        skip_id = e && *e ? (uint32_t)strtoul(e, NULL, 16) : 0;
        if (skip_id) mlog("communication error %#x will be dropped (MKWMP_SKIP_ERROR)", skip_id);
    }
    if (error_post && skip_id && c->x[1] && guest_readable((const void *)(uintptr_t)c->x[1], 4) &&
        *(const uint32_t *)(uintptr_t)c->x[1] == skip_id) {
        static volatile int32_t skipped_one;
        if (plat_inc(&skipped_one) <= 20)
            mlog("communication error %#x dropped (MKWMP_SKIP_ERROR)", skip_id);
        return;
    }
    if (error_post && g_skip_referee_timeout && c->x[1] &&
        guest_readable((const void *)(uintptr_t)c->x[1], 4) &&
        (g_skip_referee_timeout == 2 || *(const uint32_t *)(uintptr_t)c->x[1] == A.err_referee_timeout)) {
        static volatile int32_t skipped;
        if (plat_inc(&skipped) <= 50)
            mkwmp_log("communication error %#x suppressed (caller main+%#llx)",
                      *(const uint32_t *)(uintptr_t)c->x[1], (unsigned long long)(caller - GUEST_MAIN(0)));
        return;
    }
    original_dispatch(c);
    if (trace_operation && g_rt.log)
        mlog("Match controller %s op=%llu target=%#llx ret=%#llx next_phase=%u desired=%u/%u flags=%#x",
                    next_phase ? "complete" : "prepare", (unsigned long long)operation,
                    (unsigned long long)target, (unsigned long long)c->x[0],
                    next_phase && guest_readable(next_phase, 4) ? *next_phase : 0, desired_mode, desired_phase,
                    game_flags);
    if (seq && g_rt.log) {
        uint32_t after[2];
        memcpy(after, seq + A.seq_phase, sizeof after);
        if (memcmp(seq_before, after, sizeof after))
            mlog("Matchmake %s phase=%u/%u -> %u/%u",
                        target == A.seq_wait ? "wait" : "start",
                        seq_before[0], seq_before[1], after[0], after[1]);
    }
    if (result && g_rt.log && guest_readable(result, 4))
        mlog("BulkGetDocuments completion=%u (target=%#llx)",
                    *result, (unsigned long long)target);
    if (job && g_rt.log) {
        uint64_t after, label;
        uint32_t error;
        memcpy(&after, job + A.join_step, sizeof after);
        if (before == after) return;
        memcpy(&label, job + A.join_label, sizeof label);
        memcpy(&error, job + A.join_error, sizeof error);
        mlog("Pia JoinRandomSession step=%#llx next=%#llx label=%#llx error=%#x",
                    (unsigned long long)target, (unsigned long long)after,
                    (unsigned long long)label, error);
    }
}

// The plugin API's way (override_address, only while the game loads): profile_dispatch runs as the override of each
// target it matches, and "the original" is the game's function instead of the runtime's dispatcher. What it matched
// by caller only (the controller's next-phase / prepare BLRs) and MKWMP_TRACE_EDGES' every-call coverage have no
// target to hook, so those traces are COFF-only. The opt-in hooks (kart watchdog, room trace) go in only when their
// switch is on at start: they run every frame.
static void original_by_address(Cpu *c) { g_rt.call_original(c); }

static void install_by_address(const struct game_addrs *a) {
    if (!g_rt.call_original) {
        mlog("version-specific hooks off: the plugin API has no call_original");
        return;
    }
    original_dispatch = original_by_address;
    uint64_t t[32];
    int n = 0;
    const char *e = getenv("MKWMP_TRACE_KART"), *k = getenv("MKWMP_NO_IDLE_KICK"), *r = getenv("MKWMP_TRACE_ROOM");
    t[n++] = a->errsvc_post;
    if ((e && e[0] == '1') || (k && k[0] == '1')) t[n++] = a->kart_entry[0], t[n++] = a->kart_entry[1];
    if (r && r[0] == '1') t[n++] = a->room_sync, t[n++] = a->room_main, t[n++] = a->room_vote, t[n++] = a->room_event;
    t[n++] = a->bulkget_fn, t[n++] = a->ctl_event_sink, t[n++] = a->seq_wait, t[n++] = a->seq_start;
    for (int i = 0; i < GAME_PIA_STEPS; i++) t[n++] = a->pia_join_step[i];
    int ok = 0, tried = 0;
    for (int i = 0; i < n; i++) {
        if (!t[i]) continue;
        int dup = 0;
        for (int j = 0; j < i; j++) dup |= t[j] == t[i];
        if (dup) continue;
        tried++;
        if (g_rt.override_address(t[i], profile_dispatch)) ok++;
        else mlog("version-specific hook at main+%#llx refused by the runtime", (unsigned long long)(t[i] - GUEST_MAIN(0)));
    }
    mlog("BulkGetDocuments and Pia join tracing installed (%d of %d address overrides)", ok, tried);
}

static void install_profile_dispatch_trace(void) {
    static volatile int32_t attempted;
    if ((!g_rt.dispatch_hook && !g_rt.override_address) || !g_rt.game_code_loaded || !g_rt.game_code_loaded() ||
        plat_cas(&attempted, 1, 0))
        return;
    // profile_dispatch matches guest pcs of one game build and reads/writes through their registers (error skips,
    // idle-kick guard, traces): on a build without a table row it would act on unrelated code, so it is not
    // installed at all.
    int build = game_build();
    const struct game_addrs *row = game_addrs_for(build);
    if (!row) {
        mlog("game build %s: version-specific hooks off (BulkGetDocuments/Pia tracing, communication-error skips)",
             game_build_name(build));
        if (build == GAME_UNKNOWN) plat_xchg(&attempted, 0);   // main is not mapped yet: ask again
        return;
    }
    struct game_addrs a = *row;
    // Install-time guards (GAME-1.8.0-ADDRESSES.md §3). The error-service post is the one functional target here:
    // slot 6 of its vtable must hold it and it must start as expected, else nothing matches it (no skip, no log).
    if (!game_guard_slot("error service post", a.errsvc_vtable, 6, a.errsvc_post) ||
        !game_guard_words("error service post", a.errsvc_post, a.errsvc_words, 2)) {
        mlog("game build %s: error-service guard mismatch, communication-error log and skips off",
             game_build_name(build));
        a.errsvc_post = 0;
    }
    const char *kick = getenv("MKWMP_NO_IDLE_KICK");
    if (kick && kick[0] == '1' && !a.kart_idle_kick)
        mlog("game build %s: MKWMP_NO_IDLE_KICK refused (the kart state field is not confirmed for this build)",
             game_build_name(build));
    g_sdk_memmove = g_rt.guest_symbol ? g_rt.guest_symbol("memmove") : 0;
    A = a;   // before the hook goes in: profile_dispatch only ever sees the whole row
    crumb_state("game", "%s", game_build_name(build));
    mlog("game build %s: version-specific hooks on (error service %s, idle-kick guard %s)", game_build_name(build),
         a.errsvc_post ? "verified" : "off", a.kart_idle_kick ? "available" : "unavailable");
    if (!g_rt.dispatch_hook) {
        install_by_address(&a);
        return;
    }
    if (g_rt.dispatch_hook(profile_dispatch, &original_dispatch)) rlog("BulkGetDocuments and Pia join tracing installed");
    else rlog("BulkGetDocuments completion tracing: rt_dispatch import not found");
}

void npln_address_hooks(void) { install_profile_dispatch_trace(); }

static void ov_ssl_initialize(Cpu *c) {
    install_profile_dispatch_trace();
    rlog("ssl::Initialize (stub)");
    ret_x(c, 0);
}
static void ov_ssl_ctx_create(Cpu *c) { uint64_t *s = arg_p(c, 0); if (s) *s = 1; ret_x(c, 0); }
static void ov_ssl_import_pki(Cpu *c) { uint64_t *o = arg_p(c, 1); if (o) *o = 1; ret_x(c, 0); }
typedef struct {
    uint32_t id, status, size, pad;
    const unsigned char *data;
} GCertificateInfo;
// The one trusted CA: the server's own (lobby_ca.h), else the built-in capture certificate.
static void ov_ssl_cert_bufsize(Cpu *c) {
    uint32_t *out = arg_p(c, 0);
    size_t ca_len;
    lobby_ca(&ca_len);
    if (out) *out = (uint32_t)(sizeof(GCertificateInfo) + ca_len);
    ret_x(c, 0);
}
static void ov_ssl_certificates(Cpu *c) {
    GCertificateInfo **out = arg_p(c, 0);
    GCertificateInfo *info = arg_p(c, 1);
    uint32_t *ids = arg_p(c, 3);
    size_t ca_len;
    const unsigned char *ca = lobby_ca(&ca_len);
    if (!out || !info || arg_x(c, 2) < sizeof(*info) + ca_len) {
        ret_x(c, (uint64_t)-1); return;
    }
    memset(info, 0, sizeof(*info));
    info->id = ids ? ids[0] : 0;
    info->size = (uint32_t)ca_len;
    info->data = (unsigned char *)(info + 1);
    memcpy((void *)info->data, ca, ca_len);
    *out = info;
    ret_x(c, 0);
}
// ---- dormant until Online -----------------------------------------------------
// The probe registers its overrides before the game loads (runtime rule), but stays DORMANT:
// every override that shadows one of the runtime's own handlers (mkw.exe's `.ovr` section,
// {name, fn} pairs) calls that stock handler, so the game behaves exactly like offline
// mkw-recomp. The game's network request (nifm SubmitNetworkRequestAndWait, i.e. the player
// opening Online Play) is the trigger: the probe asks mkwmp-launcher for a seat (seat.c) and
// only when seated turns ACTIVE (our overrides from then on). Overrides with no stock handler
// (raw sockets, ssl, ...) are only reached once the game is online.
// MKWMP_EAGER=1: active from start (old behaviour, for debugging).
static volatile int32_t g_active;

// The runtime's own handler of an SDK name (NULL with none, or a backend without stock handlers).
static GuestFn stock_handler(const char *name) { return g_rt.stock ? g_rt.stock(name) : NULL; }

// Online Play was opened: get a seat (if a launcher is configured) and go active.
static void try_activate(void) {
    static PlatLock lock = PLAT_LOCK_INIT;
    plat_lock(&lock);
    if (!g_active) {
        const MkwmpConfig *cfg = mkwmp_config();
        SeatReply r;
        if (!mkwmp_config_has_seat(cfg)) {
            mlog("online requested: no launcher in mkwmp.toml, going active without a seat");
            voice_online(cfg->host);
            plat_xchg(&g_active, 1);
        } else if (seat_acquire(&r, 10 * 60 * 1000) == SEAT_SEATED) {
            char *colon = strrchr(r.lobby, ':');
            if (r.lobby[0] && colon) {
                *colon = 0;
                snprintf(g_lobby_host, sizeof g_lobby_host, "%s", r.lobby);
                g_lobby_port = atoi(colon + 1);
            }
            seat_keepalive_start();
            friends_online(FRIENDS_WAIT_MS);
            mlog("online requested: seated, going active");
            crumb_state("online", "1");
            crumb_state("server", "%s:%d", g_lobby_host, g_lobby_port);
            voice_online(g_lobby_host);
            plat_xchg(&g_active, 1);
        } else {
            mlog("online requested: no seat, staying offline (stock no-network)");
        }
    }
    plat_unlock(&lock);
}

// MKWMP_EAGER=1 is active from boot, so try_activate never runs: the first account-token load (the sign-in) takes
// the seat instead, so the lobby gets a real seat ticket and voice starts. Retried on the next sign-in if refused.
static int g_eager;
static void eager_seat(void) {
    static volatile int32_t seated;
    static PlatLock lock = PLAT_LOCK_INIT;
    if (!g_eager || seated) return;
    plat_lock(&lock);
    if (!seated) {
        const MkwmpConfig *cfg = mkwmp_config();
        SeatReply r;
        if (!mkwmp_config_has_seat(cfg)) {
            voice_online(cfg->host);
            seated = 1;
        } else if (seat_acquire(&r, 60 * 1000) == SEAT_SEATED) {
            char *colon = strrchr(r.lobby, ':');
            if (r.lobby[0] && colon) {
                *colon = 0;
                snprintf(g_lobby_host, sizeof g_lobby_host, "%s", r.lobby);
                g_lobby_port = atoi(colon + 1);
            }
            seat_keepalive_start();
            friends_online(FRIENDS_WAIT_MS);
            mlog("eager sign-in: seated");
            crumb_state("online", "1");
            crumb_state("server", "%s:%d", g_lobby_host, g_lobby_port);
            voice_online(g_lobby_host);
            seated = 1;
        } else {
            mlog("eager sign-in: no seat (the lobby will refuse the sign-in)");
        }
    }
    plat_unlock(&lock);
}

// nn::nifm::GetCurrentIpConfigInfo(nn::socket::InAddr * x5), game 1.8.0 (Pia 7.3's transport start). The runtime has
// no handler under this name, so gate_call cannot keep it dormant: while the probe is dormant it answers as the
// runtime answers the in_addr overload (no network, or the LAN address with MKW_LAN), afterwards as the probe does.
static void ov_nifm_ip_config_inaddr(Cpu *c) {
    if (!g_active) {
        static GuestFn stock;
        if (!stock) stock = stock_handler("_ZN2nn4nifm22GetCurrentIpConfigInfoEP7in_addrS2_S2_S2_S2_");
        if (stock) { stock(c); return; }
    }
    ov_nifm_ip_config(c);
}

#define NREG 64
static GuestFn g_ours[NREG], g_stock[NREG];
static unsigned char g_trigger[NREG];
static const char *g_gate_name[NREG];
// A call from a guest module that must keep the runtime's own handlers whatever the probe's state (gameaddrs.h
// stock_lo/hi): game 1.8.0's subsdk0 "movie" imports nn::socket::Close, GetLastError and EventFd/EventFdRead/
// EventFdWrite for its task queue (subsdk0+0x17680, +0x18da0, with nn::socket::Select, which nobody overrides). Those
// names are gRPC's too. Served by the probe once active, the movie code would get descriptors of the probe's table
// (fd_new) and hand them to the SDK's Select, and its Close(fd) could close a gRPC or Pia socket of the same number.
// With the stock handlers it behaves exactly as in offline mkw-recomp, before and after Online Play is opened.
// (Its nn::fs imports reach the probe's fs overrides, which pass everything but _npln:/npln_config.json through.)
static int stock_only_caller(uint64_t lr) {
    const struct game_addrs *ga = game_addrs();
    return ga && ga->stock_hi && lr >= ga->stock_lo && lr < ga->stock_hi;
}

static void gate_call(int i, Cpu *c) {
    // The first gated call (about 3 s into the boot, long before any hook): say in mkwmp.log which game build runs
    // and whether its table row fits it.
    static volatile int32_t checked;
    if (!checked && game_build() != GAME_UNKNOWN && !plat_xchg(&checked, 1)) game_addrs_selfcheck();
    if (g_stock[i] && stock_only_caller(c->x[30])) { g_stock[i](c); return; }
    if (!g_active && g_stock[i]) {
        static volatile int32_t seen[NREG];
        if (!plat_xchg(&seen[i], 1) || g_trigger[i])
            mlog("dormant call: %s caller=main+%#llx", g_gate_name[i],
                 (unsigned long long)(c->x[30] - GUEST_MAIN(0)));
        // The game makes one automatic sign-in at boot (nifm request ~5 s in, before the title
        // screen) and one per Online Play click, from the same caller (main+0x2247a7c). Decline
        // the boot one (stock "no network": the menu stays offline, no seat taken); every later
        // request is the player opening Online Play.
        static volatile int32_t requests;
        if (g_trigger[i]) {
            // An unknown build keeps the 1.0.0 behaviour (decline the first request).
            const struct game_addrs *ga = game_addrs();
            int boot = !ga || ga->boot_signin;
            if (boot && plat_inc(&requests) == 1) mlog("boot sign-in declined (stays offline until Online Play)");
            else try_activate();
        }
        if (!g_active) { g_stock[i](c); return; }
    }
    g_ours[i](c);
}
#define GATE1(i) static void gate_##i(Cpu *c) { gate_call(i, c); }
#define GATE8(a, b, c_, d, e, f, g, h) GATE1(a) GATE1(b) GATE1(c_) GATE1(d) GATE1(e) GATE1(f) GATE1(g) GATE1(h)
GATE8(0, 1, 2, 3, 4, 5, 6, 7) GATE8(8, 9, 10, 11, 12, 13, 14, 15)
GATE8(16, 17, 18, 19, 20, 21, 22, 23) GATE8(24, 25, 26, 27, 28, 29, 30, 31)
GATE8(32, 33, 34, 35, 36, 37, 38, 39) GATE8(40, 41, 42, 43, 44, 45, 46, 47)
GATE8(48, 49, 50, 51, 52, 53, 54, 55) GATE8(56, 57, 58, 59, 60, 61, 62, 63)
#define GREF8(a, b, c_, d, e, f, g, h) gate_##a, gate_##b, gate_##c_, gate_##d, gate_##e, gate_##f, gate_##g, gate_##h
static const GuestFn kGates[NREG] = {
    GREF8(0, 1, 2, 3, 4, 5, 6, 7), GREF8(8, 9, 10, 11, 12, 13, 14, 15),
    GREF8(16, 17, 18, 19, 20, 21, 22, 23), GREF8(24, 25, 26, 27, 28, 29, 30, 31),
    GREF8(32, 33, 34, 35, 36, 37, 38, 39), GREF8(40, 41, 42, 43, 44, 45, 46, 47),
    GREF8(48, 49, 50, 51, 52, 53, 54, 55), GREF8(56, 57, 58, 59, 60, 61, 62, 63),
};

// ---- registration table ----------------------------------------------------
typedef struct { const char *name; GuestFn fn; } Reg;
static const Reg kRegs[] = {
    {"_ZN2nn4nifm18IsNetworkAvailableEv", ov_nifm_is_network_available},
    {"_ZN2nn4nifm27SubmitNetworkRequestAndWaitEv", ov_nifm_submit_wait},
    {"_ZN2nn4nifm22GetCurrentIpConfigInfoEP7in_addrS2_S2_S2_S2_", ov_nifm_ip_config},
    // Game 1.8.0 only (Pia 7.3's transport start, main+0x1661d90; no stock handler in the runtime). It took the slot
    // of nn::socket::FreeAddrInfo(AddrInfo *), which the runtime's stock handler now serves (kStockFreeAddrInfo).
    {"_ZN2nn4nifm22GetCurrentIpConfigInfoEPNS_6socket6InAddrES3_S3_S3_S3_", ov_nifm_ip_config_inaddr},
    {"_ZN2nn7account32IsNetworkServiceAccountAvailableEPbRKNS0_10UserHandleE", ov_nsa_available},
    {"_ZN2nn7account36EnsureNetworkServiceAccountAvailableERKNS0_10UserHandleE", ov_ok},
    {"_ZN2nn7account26GetNetworkServiceAccountIdEPNS0_23NetworkServiceAccountIdERKNS0_10UserHandleE", ov_nsa_get_id},
    {"_ZN2nn7account37LoadNetworkServiceAccountIdTokenCacheEPmPcmRKNS0_10UserHandleE", ov_nsa_load_token},
    {"_ZN2nn7account44EnsureNetworkServiceAccountIdTokenCacheAsyncEPNS0_12AsyncContextERKNS0_10UserHandleE", ov_ok},
    {"_ZN2nn7account12AsyncContext14GetSystemEventEPNS_2os11SystemEventE", ov_nsa_async_event},
    {"_ZN2nn7account12AsyncContext9GetResultEv", ov_ok},
    {"_ZN2nn4time39AsyncEnsureNetworkClockAvailabilityTask10InitializeENS_2os14EventClearModeE", ov_clock_init},
    {"_ZN2nn4time39AsyncEnsureNetworkClockAvailabilityTask9StartTaskEv", ov_ok},
    // (AsyncEnsureNetworkClockAvailabilityTask::GetResult is left to the runtime: its stock
    //  handler already returns success, in 1.0.8 and 0.1.10 alike. Its slot went to GetFriendList.)
    {"_ZN2nn4time39AsyncEnsureNetworkClockAvailabilityTask14GetFinishEventEv", ov_clock_event},
    {"_ZN2nn4time39AsyncEnsureNetworkClockAvailabilityTaskD1Ev", ov_clock_destroy},
    {"_ZN2nn3nsd24GetEnvironmentIdentifierEPNS0_21EnvironmentIdentifierE", ov_nsd_env_id},
    {"_ZN2nn2fs8MountRomEPKc", ov_fs_mount_rom},
    {"_ZN2nn2fs7UnmountEPKc", ov_fs_unmount},
    {"_ZN2nn2fs8OpenFileEPNS0_10FileHandleEPKci", ov_fs_open_file},
    {"_ZN2nn2fs12GetEntryTypeEPNS0_18DirectoryEntryTypeEPKc", ov_fs_entry_type},
    {"_ZN2nn2fs8ReadFileEPmNS0_10FileHandleElPvm", ov_fs_read_file},
    {"_ZN2nn2fs11GetFileSizeEPlNS0_10FileHandleE", ov_fs_file_size},
    {"_ZN2nn2fs9CloseFileENS0_10FileHandleE", ov_fs_close_file},
    // ssl C++
    {"_ZN2nn3ssl10InitializeEv", ov_ssl_initialize},
    {"_ZN2nn3ssl7Context6CreateENS1_10SslVersionE", ov_ssl_ctx_create},
    {"_ZN2nn3ssl7Context15ImportServerPkiEPmPKcjNS0_17CertificateFormatE", ov_ssl_import_pki},
    {"_ZN2nn3ssl14BuiltInManager28GetBuiltInCertificateBufSizeEPjPNS0_15CaCertificateIdEj", ov_ssl_cert_bufsize},
    {"_ZN2nn3ssl14BuiltInManager22GetBuiltInCertificatesEPPNS0_26BuiltInCertificateInfoBaseEPhjPNS0_15CaCertificateIdEj", ov_ssl_certificates},
    // Remaining SDK and Pia overrides
    {"nnsslInitialize", ov_ssl_initialize},
    // (The BSD-style nn::socket::GetAddrInfo/FreeAddrInfo(addrinfo*) pair is never called by
    //  this game; their slots went to GetNickname and CancelNetworkRequest: 64-override cap.)
    {"_ZN2nn7account11GetNicknameEPNS0_8NicknameERKNS0_3UidE", ov_nickname},
    {"_ZN2nn4nifm20CancelNetworkRequestEv", ov_nifm_cancel},
    {"_ZN2nn6socket6SocketENS0_6FamilyENS0_4TypeENS0_8ProtocolE", ov_pia_socket},
    {"_ZN2nn6socket10SetSockOptEiNS0_5LevelENS0_6OptionEPKvj", ov_pia_setsockopt},
    {"_ZN2nn6socket11GetAddrInfoEPKcS2_PKNS0_8AddrInfoEPPS3_", ov_sock_getaddrinfo_cpp},
    {"_ZN2nn6socket4PollEP6pollfdmi", ov_sock_poll},
    {"_ZN2nn6socket4BindEiPKNS0_8SockAddrEj", ov_pia_bind},
    // (nn::socket::FreeAddrInfo(AddrInfo *) is left to the runtime: g_typed_ai needs no free. nn::socket::Recv,
    //  new in game 1.8.0, is not registered either: no slot is left and nothing reaches it, see ov_pia_recv.)
    {"_ZN2nn3err9ShowErrorERKNS0_18ErrorResultVariantE", ov_show_error_variant},
    {"_ZN2nn6socket6SendToEiPKvmNS0_7MsgFlagEPKNS0_8SockAddrEj", ov_pia_sendto},
    {"_ZN2nn7friends27CheckFriendListAvailabilityEPbRKNS_7account3UidE", ov_friend_list_available},
    {"_ZN2nn7friends13GetFriendListEPiPNS0_6FriendERKNS_7account3UidEiiRKNS0_12FriendFilterE", ov_friend_list},
    {"pthread_cond_timedwait", ov_cond_timedwait},
    {"_ZN2nn6socket8RecvFromEiPvmNS0_7MsgFlagEPNS0_8SockAddrEPj", ov_pia_recvfrom},
    {"_ZN2nn6socket12GetLastErrorEv", ov_pia_errno},
    {"_ZN2nn6socket5CloseEi", ov_sock_close},
    {"_ZN2nn6socket12GetLastErrnoEv", ov_pia_errno},
    {"clock_gettime", ov_clock_gettime},
    // socket C-API
    {"_ZN2nn6socket7EventFdEmNS0_12EventFdFlagsE", ov_eventfd},
    {"_ZN2nn6socket11EventFdReadEiPm", ov_eventfd_read},
    {"_ZN2nn6socket12EventFdWriteEim", ov_eventfd_write},
    {"nnsocketSocket", ov_sock_socket},
    {"nnsocketConnect", ov_sock_connect},
    {"nnsocketClose", ov_sock_close},
    {"nnsocketFcntl", ov_sock_fcntl},
    {"nnsocketSetSockOpt", ov_sock_setsockopt},
    {"nnsocketGetSockOpt", ov_sock_getsockopt},
    {"nnsocketShutdown", ov_sock_shutdown},
    {"nnsocketSendMsg", ov_sock_sendmsg},
    {"nnsocketRecvMsg", ov_sock_recvmsg},
    {"nnsocketPoll", ov_sock_poll},
    {"nnsocketFreeAddrInfo", ov_sock_freeaddrinfo},
    {"__errno_location", ov_errno_location},
    {"nnsocketGetAddrInfo", ov_sock_getaddrinfo},
};
// Up to mkw-recomp 0.1.13 the runtime keeps overrides in a fixed table of 64: its hostfn_add_override ends the
// process past that ("hostfn_add_override: more than 64"; cmp rax, 0x40 in mkw.exe 1.0.8 and
// 0.1.10 - 0.1.13). Every hostfn_add_override call of npln_register counts: kRegs and the core-mask pair,
// and each kRegs entry needs its own gate. From 0.1.15 the table grows with realloc (no limit; 64 entries are its
// first allocation) and is searched newest first, so a native plugin's override of a name, added after ours, wins.
// The budget stays 64 while 0.1.10 - 0.1.13 are supported: one probe binary serves them all.
// Unchanged in 0.1.15: an override added once the game is loaded is still fatal ("after ldr_load"), hence the
// TOO LATE check in npln_register.
// The table is full: 62 kRegs entries + the core-mask pair. A new override needs the slot of an old one (game
// 1.8.0's GetCurrentIpConfigInfo(InAddr *) took FreeAddrInfo(AddrInfo *)'s). The SwitchToFiber / InitializeFiber
// workaround for runtime 1.0.8 (research/docs/FINDINGS.md) was removed: every supported runtime fixed it.
enum {
    RUNTIME_OVERRIDE_MAX = 64,
    N_REGS = sizeof(kRegs) / sizeof(kRegs[0]),
    N_CORE_MASK_REGS = 2,   // nnosGetThreadCoreMask, nnosSetThreadCoreMask
};
// The typed FreeAddrInfo must have a stock (do-nothing) handler: the SDK's own would free a g_typed_ai entry.
// tools/recomp/check_release.py reads this name and fails a runtime release without it.
static const char kStockFreeAddrInfo[] = "_ZN2nn6socket12FreeAddrInfoEPNS0_8AddrInfoE";
static const char kIpConfigInAddr[] = "_ZN2nn4nifm22GetCurrentIpConfigInfoEPNS_6socket6InAddrES3_S3_S3_S3_";
static void ov_nothing(Cpu *c) { (void)c; }
_Static_assert(N_REGS <= NREG, "one gate per kRegs entry");
_Static_assert(N_REGS + N_CORE_MASK_REGS <= RUNTIME_OVERRIDE_MAX, "runtime override limit");

int npln_register(void) {
    // Opt-in: engage only if mkwmp.toml is present (or MKWMP_ENABLE=1 for tests).
    // A stock launch (no config) must behave like offline: NPLN init aborts if
    // we open the gates without actively capturing.
    const MkwmpConfig *cfg = mkwmp_config();
    const char *enabled = getenv("MKWMP_ENABLE");
    int engage = cfg->present || (enabled && !strcmp(enabled, "1"));
    if (!engage) { log_line("npln: inactive (no mkwmp.toml; set MKWMP_ENABLE=1 to force)"); return 0; }
    const char *player = getenv("MKWMP_PLAYER");
    g_second_player = player ? !strcmp(player, "2") : cfg->slot == 2;
    const char *edges = getenv("MKWMP_TRACE_EDGES");
    g_trace_edges = edges && !strcmp(edges, "1");
    const char *skip = getenv("MKWMP_SKIP_REFEREE_TIMEOUT");
    g_skip_referee_timeout = skip ? atoi(skip) : 0;
    plat_no_error_dialogs();
    if (!g_rt.override || !g_rt.guest_modules) { log_line("npln_register: runtime not resolved"); return -1; }
    if (!g_rt.core_away || !g_rt.core_back) {
        log_line("npln_register: kern_core_away/back missing; capture disabled"); return -1;
    }
    if (g_rt.guest_modules() != 0) { log_line("npln_register: TOO LATE"); return -1; }
    int n = N_REGS;
    const char *eager = getenv("MKWMP_EAGER");
    if (eager && !strcmp(eager, "1")) { g_active = 1; g_eager = 1; log_line("MKWMP_EAGER=1: active from start"); }
    int gated = 0;
    int stock_free = stock_handler(kStockFreeAddrInfo) != NULL;
    for (int i = 0; i < n; i++) {
        if (!stock_free && !strcmp(kRegs[i].name, kIpConfigInAddr)) {
            // A runtime without that stock handler (none of the supported ones): the slot goes back to FreeAddrInfo,
            // as a no-op, and game 1.8.0's Pia keeps the SDK's GetCurrentIpConfigInfo(InAddr *).
            mlog("npln: no stock FreeAddrInfo(AddrInfo *) in this runtime: overriding it instead of %s", kIpConfigInAddr);
            g_rt.override(kStockFreeAddrInfo, ov_nothing);
            continue;
        }
        g_ours[i] = kRegs[i].fn;
        g_gate_name[i] = kRegs[i].name;
        g_stock[i] = stock_handler(kRegs[i].name);
        g_trigger[i] = !strcmp(kRegs[i].name, "_ZN2nn4nifm27SubmitNetworkRequestAndWaitEv");
        // Never gated: fs overrides only log and delegate; nn::account
        // is checked once at boot (IsNetworkServiceAccountAvailable) and cached, so a stock
        // "no network account" there makes the first Online Play click fail (GetNickname gives
        // the launcher name offline too); CancelNetworkRequest calls the stock one itself.
        if (!strncmp(kRegs[i].name, "_ZN2nn2fs", 9) || !strncmp(kRegs[i].name, "_ZN2nn7account", 14) || strstr(kRegs[i].name, "CancelNetworkRequest"))
            g_stock[i] = NULL;
        gated += g_stock[i] != NULL;
        g_rt.override(kRegs[i].name, g_stock[i] ? kGates[i] : kRegs[i].fn);
    }
    mlog("npln: %d overrides gated (dormant until Online)%s", gated, g_active ? ", but MKWMP_EAGER" : "");
    // Runtime 0.1.10 aborts the SDK's GetThreadCoreMask (result 0xe801) on the online start-up path
    // (main+0x14c5bc0).
    g_rt.override("nnosGetThreadCoreMask", ov_get_thread_core_mask);
    g_rt.override("nnosSetThreadCoreMask", ov_set_thread_core_mask);
    log_line("core-mask overrides registered");
    log_kv("npln_register: overrides", (uint64_t)n);
    return n;
}
