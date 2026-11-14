#include "voice.h"

#include "config.h"
#include "friends.h"
#include "launcher.h"
#include "log.h"
#include "seat.h"
#include "../../voice/src/mkwmp_voice.h"

#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// ---- parsing (pure) ---------------------------------------------------------------------------

static void defaults(VoiceSettings *s) {
    memset(s, 0, sizeof *s);
    s->enabled = 1;
    s->input_gain = s->output_gain = 100;
}

// The value after `"key":` in a flat JSON object, or NULL.
static const char *field(const char *json, const char *key) {
    char pat[40];
    snprintf(pat, sizeof pat, "\"%s\"", key);
    for (const char *p = strstr(json, pat); p; p = strstr(p + 1, pat)) {
        const char *q = p + strlen(pat);
        while (*q == ' ' || *q == '\t' || *q == '\r' || *q == '\n') q++;
        if (*q != ':') continue;
        q++;
        while (*q == ' ' || *q == '\t' || *q == '\r' || *q == '\n') q++;
        return q;
    }
    return NULL;
}

// A JSON string at p into dst (\" \\ \/ and \uXXXX below 0x80 unescaped; others kept raw). Returns the char after it.
static const char *string_at(const char *p, char *dst, size_t n) {
    if (n) dst[0] = 0;
    if (!p || *p != '"') return p;
    size_t k = 0;
    for (p++; *p && *p != '"'; p++) {
        char c = *p;
        if (c == '\\' && p[1]) {
            p++;
            if (*p == 'u' && p[1] && p[2] && p[3] && p[4]) {
                char hex[5] = {p[1], p[2], p[3], p[4], 0};
                long v = strtol(hex, NULL, 16);
                c = v > 0 && v < 0x80 ? (char)v : '?';
                p += 4;
            } else {
                c = *p == 'n' ? '\n' : *p == 't' ? '\t' : *p;
            }
        }
        if (k + 1 < n) dst[k++] = c;
    }
    if (n) dst[k] = 0;
    return *p == '"' ? p + 1 : p;
}

static int int_field(const char *json, const char *key, int def, int lo, int hi) {
    const char *p = field(json, key);
    if (!p || (*p != '-' && (*p < '0' || *p > '9'))) return def;
    int v = atoi(p);
    return v < lo || v > hi ? def : v;
}

int voice_parse_settings(int http_status, const char *body, VoiceSettings *out) {
    defaults(out);
    if (http_status != 200 || !body) return 0;
    const char *p = field(body, "enabled");
    if (p && !strncmp(p, "false", 5)) out->enabled = 0;
    string_at(field(body, "input"), out->input, sizeof out->input);
    string_at(field(body, "output"), out->output, sizeof out->output);
    out->input_gain = int_field(body, "inputGain", 100, 1, 100);
    out->output_gain = int_field(body, "outputGain", 100, 1, 100);
    p = field(body, "mutes");
    if (p && *p == '[') {
        const char *end = strchr(p, ']');
        for (const char *a = strstr(p, "\"account\""); a && (!end || a < end) && out->mute_count < VOICE_MAX_MUTES;
             a = strstr(a + 1, "\"account\"")) {
            const char *v = field(a, "account");
            char num[32];
            if (v && *v == '"') string_at(v, num, sizeof num);
            else if (v) { snprintf(num, sizeof num, "%.20s", v); }
            else continue;
            uint64_t id = strtoull(num, NULL, 10);
            if (id) out->mutes[out->mute_count++] = id;
        }
    }
    return 1;
}

// ---- runtime ------------------------------------------------------------------------------------

static struct {
    HMODULE dll;
    void (*init)(mkwmp_voice_log_fn);
    void (*connect)(const char *, int, const char *);
    void (*disconnect)(void);
    void (*settings)(int, const char *, const char *, int, int);
    void (*set_mutes)(const uint64_t *, int);
    void (*set_account)(uint64_t);
    int (*devices_json)(char *, int);
    int (*peers)(mkwmp_voice_peer *, int);
    int (*status)(void);
} g_dll;

static SRWLOCK g_lock = SRWLOCK_INIT;
static int g_online;
static char g_host[128];
static VoiceSettings g_settings;   // the launcher's last answer (mutes = the launcher's list)
static uint64_t g_mutes[VOICE_MAX_MUTES];
static int g_mute_count;
// in-game mute changes not yet told to the launcher
static struct { uint64_t account; int muted; char name[FRIEND_NAME_SIZE]; } g_pending[16];
static int g_pending_count;
static HANDLE g_thread;

static void dll_log(const char *line) { mkwmp_log("%s", line); }

static int load_dll(void) {
    if (g_dll.dll) return 1;
    wchar_t path[MAX_PATH];
    DWORD n = GetModuleFileNameW(NULL, path, MAX_PATH);
    if (!n || n >= MAX_PATH) return 0;
    for (DWORD i = n; i > 0; i--) {
        if (path[i - 1] == L'\\' || path[i - 1] == L'/') { path[i] = 0; break; }
    }
    wcsncat(path, L"mkwmp-voice.dll", MAX_PATH - wcslen(path) - 1);
    HMODULE m = LoadLibraryW(path);
    if (!m) { mkwmp_log("voice: mkwmp-voice.dll not found (error %lu): voice chat off", GetLastError()); return 0; }
    mkwmp_voice_abi_fn abi = (mkwmp_voice_abi_fn)(void *)GetProcAddress(m, "mkwmp_voice_abi");
    if (!abi || abi() != MKWMP_VOICE_ABI) {
        mkwmp_log("voice: mkwmp-voice.dll ABI %d, want %d: voice chat off", abi ? abi() : -1, MKWMP_VOICE_ABI);
        FreeLibrary(m);
        return 0;
    }
#define SYM(f, name) *(FARPROC *)&g_dll.f = GetProcAddress(m, name)
    SYM(init, "mkwmp_voice_init");
    SYM(connect, "mkwmp_voice_connect");
    SYM(disconnect, "mkwmp_voice_disconnect");
    SYM(settings, "mkwmp_voice_settings");
    SYM(set_mutes, "mkwmp_voice_set_mutes");
    SYM(set_account, "mkwmp_voice_set_account");
    SYM(devices_json, "mkwmp_voice_devices_json");
    SYM(peers, "mkwmp_voice_peers");
    SYM(status, "mkwmp_voice_status");
#undef SYM
    if (!g_dll.init || !g_dll.connect || !g_dll.disconnect || !g_dll.settings || !g_dll.set_mutes || !g_dll.set_account ||
        !g_dll.devices_json || !g_dll.peers || !g_dll.status) {
        mkwmp_log("voice: mkwmp-voice.dll lacks an export: voice chat off");
        FreeLibrary(m);
        memset(&g_dll, 0, sizeof g_dll);
        return 0;
    }
    g_dll.dll = m;
    g_dll.init(dll_log);
    mkwmp_log("voice: engine loaded");
    return 1;
}

static int find_mute(uint64_t account) {
    for (int i = 0; i < g_mute_count; i++)
        if (g_mutes[i] == account) return i;
    return -1;
}

// Under g_lock.
static void push_mutes(void) {
    if (g_dll.set_mutes) g_dll.set_mutes(g_mutes, g_mute_count);
}

static void post_pending(void) {
    for (;;) {
        uint64_t account;
        int muted;
        char name[FRIEND_NAME_SIZE];
        AcquireSRWLockExclusive(&g_lock);
        if (!g_pending_count) { ReleaseSRWLockExclusive(&g_lock); return; }
        account = g_pending[0].account;
        muted = g_pending[0].muted;
        memcpy(name, g_pending[0].name, sizeof name);
        memmove(g_pending, g_pending + 1, (size_t)(--g_pending_count) * sizeof g_pending[0]);
        ReleaseSRWLockExclusive(&g_lock);
        char json[256], esc[2 * FRIEND_NAME_SIZE] = {0};
        for (size_t i = 0, k = 0; name[i] && k + 2 < sizeof esc; i++) {
            if (name[i] == '"' || name[i] == '\\') esc[k++] = '\\';
            esc[k++] = (unsigned char)name[i] < 0x20 ? ' ' : name[i];
        }
        snprintf(json, sizeof json, "{\"account\":\"%llu\",\"name\":\"%s\",\"muted\":%s}", (unsigned long long)account, esc,
                 muted ? "true" : "false");
        char buf[512];
        const char *body;
        int st = launcher_request_json("POST", "/local/v1/voice/mute", json, buf, sizeof buf, &body);
        mkwmp_log("voice: %s %llu told to the launcher (HTTP %d)", muted ? "mute" : "unmute", (unsigned long long)account, st);
    }
}

static DWORD WINAPI voice_thread(LPVOID unused) {
    (void)unused;
    char last_ticket[sizeof ((SeatReply *)0)->ticket] = {0};
    char last_devices[8192] = {0};
    ULONGLONG devices_at = 0;
    int last_enabled = -1;
    static char buf[16384];
    for (;;) {
        AcquireSRWLockShared(&g_lock);
        int online = g_online;
        char host[128];
        memcpy(host, g_host, sizeof host);
        ReleaseSRWLockShared(&g_lock);
        const MkwmpConfig *cfg = mkwmp_config();

        // the launcher's settings (and its mute list)
        const char *body;
        int st = launcher_request("GET", "/local/v1/voice", buf, sizeof buf, &body);
        VoiceSettings s;
        if (!voice_parse_settings(st, body, &s) && st != 404 && st != 0 && st != -1) mkwmp_log("voice: settings HTTP %d", st);
        // an older launcher (404) or none: the defaults (voice on, default devices)
        AcquireSRWLockExclusive(&g_lock);
        if (st == 200) {
            // the launcher's list is the truth, plus in-game changes it has not been told yet
            g_mute_count = 0;
            for (int i = 0; i < s.mute_count; i++) g_mutes[g_mute_count++] = s.mutes[i];
            for (int i = 0; i < g_pending_count; i++) {
                int at = find_mute(g_pending[i].account);
                if (g_pending[i].muted && at < 0 && g_mute_count < VOICE_MAX_MUTES) g_mutes[g_mute_count++] = g_pending[i].account;
                if (!g_pending[i].muted && at >= 0) g_mutes[at] = g_mutes[--g_mute_count];
            }
        }
        g_settings = s;
        push_mutes();
        ReleaseSRWLockExclusive(&g_lock);
        if (s.enabled != last_enabled) {
            mkwmp_log("voice: chat %s by the launcher's settings", s.enabled ? "on" : "off");
            last_enabled = s.enabled;
        }
        g_dll.settings(s.enabled, s.input, s.output, s.input_gain, s.output_gain);
        post_pending();

        // the relay: the seat ticket, the lobby host, the voice port
        char ticket[sizeof last_ticket];
        if (!seat_ticket(ticket, sizeof ticket)) snprintf(ticket, sizeof ticket, "%s", cfg->ticket);
        if (online && ticket[0] && host[0] && cfg->voice_port > 0) {
            if (strcmp(ticket, last_ticket)) {
                g_dll.set_account(friends_account_id(cfg->name));
                g_dll.connect(host, cfg->voice_port, ticket);
                memcpy(last_ticket, ticket, sizeof last_ticket);
            }
        } else if (last_ticket[0]) {
            g_dll.disconnect();
            last_ticket[0] = 0;
        }

        // the devices this process sees, for the launcher's settings pane: at start and every 30 s when they change
        ULONGLONG now = GetTickCount64();
        if (now - devices_at > 30000 || !devices_at) {
            devices_at = now;
            static char devices[8192];
            if (g_dll.devices_json(devices, sizeof devices) > 0 && strcmp(devices, last_devices)) {
                int ps = launcher_request_json("POST", "/local/v1/voice/devices", devices, buf, sizeof buf, &body);
                if (ps == 204 || ps == 200) memcpy(last_devices, devices, sizeof last_devices);
                mkwmp_log("voice: devices sent to the launcher (HTTP %d)", ps);
            }
        }
        Sleep(2000);
    }
    return 0;
}

void voice_online(const char *lobby_host) {
    const MkwmpConfig *cfg = mkwmp_config();
    if (cfg->voice_port <= 0) return;
    AcquireSRWLockExclusive(&g_lock);
    int ok = load_dll();
    if (ok) {
        g_online = 1;
        snprintf(g_host, sizeof g_host, "%s", lobby_host && *lobby_host ? lobby_host : cfg->host);
        if (!g_thread) g_thread = CreateThread(NULL, 0, voice_thread, NULL, 0, NULL);
        mkwmp_log("voice: online (relay %s:%d)", g_host, cfg->voice_port);
    }
    ReleaseSRWLockExclusive(&g_lock);
}

void voice_offline(void) {
    AcquireSRWLockExclusive(&g_lock);
    if (g_online) mkwmp_log("voice: offline");
    g_online = 0;
    ReleaseSRWLockExclusive(&g_lock);
}

int voice_is_muted(uint64_t account) {
    AcquireSRWLockShared(&g_lock);
    int m = find_mute(account) >= 0;
    ReleaseSRWLockShared(&g_lock);
    return m;
}

int voice_is_talking(uint64_t account) {
    if (!g_dll.peers) return 0;
    mkwmp_voice_peer p[32];
    int n = g_dll.peers(p, 32);
    for (int i = 0; i < n; i++)
        if (p[i].account == account) return p[i].talking;
    return 0;
}

int voice_active(void) {
    return g_dll.status && (g_dll.status() & (MKWMP_VOICE_SUBSCRIBED | MKWMP_VOICE_RUNNING)) == (MKWMP_VOICE_SUBSCRIBED | MKWMP_VOICE_RUNNING);
}

void voice_set_muted(uint64_t account, const char *name, int muted) {
    if (!account) return;
    AcquireSRWLockExclusive(&g_lock);
    int at = find_mute(account);
    if (muted && at < 0 && g_mute_count < VOICE_MAX_MUTES) g_mutes[g_mute_count++] = account;
    if (!muted && at >= 0) g_mutes[at] = g_mutes[--g_mute_count];
    if (g_pending_count == (int)(sizeof g_pending / sizeof g_pending[0])) {
        memmove(g_pending, g_pending + 1, sizeof g_pending - sizeof g_pending[0]);
        g_pending_count--;
    }
    g_pending[g_pending_count].account = account;
    g_pending[g_pending_count].muted = muted;
    snprintf(g_pending[g_pending_count].name, sizeof g_pending[0].name, "%s", name ? name : "");
    g_pending_count++;
    push_mutes();
    ReleaseSRWLockExclusive(&g_lock);
    mkwmp_log("voice: %s %llu (%s) from the game's menu", muted ? "muted" : "unmuted", (unsigned long long)account, name ? name : "?");
}
