// The Voice chat tab of mkw-recomp's in-game settings (MkwPluginApi.settings_tab, plugin API 2): voice on or off, the
// microphone and the speakers the voice engine sees, their volumes, and the players muted (kept from earlier
// versions, which had a mute menu in the game; the mod adds nothing to the game's own menus any more), without leaving the game.
// The values are the mod's settings (voice, voice_input, voice_output, voice_in_gain, voice_out_gain); seatd.c hands
// them to voice.c at its next poll, so a change applies within a second or two. Only without the MKWMP launcher (it
// has its own voice settings then).
#include "mkw_plugin.h"

#include "seatapi.h"
#include "seatd.h"
#include "voicetab.h"

#include <stdio.h>
#include <string.h>

enum { V_HEAD, V_ON, V_INPUT, V_OUTPUT, V_DEFAULT, V_NO_DEVICES, V_IN_GAIN, V_OUT_GAIN, V_MUTED, V_NONE_MUTED,
       V_UNMUTE, V_PLAYER, V_REPORT, V_REPORT_NEXT, V_NOT_SIGNED_UP, V_LAUNCHER, V_COUNT };
static const char *const EN[V_COUNT] = {
    "Voice chat", "Voice chat in online lobbies and races", "Microphone", "Speakers or headphones", "System default",
    "The devices show up once voice chat has started (go online once).", "Microphone volume", "Voice volume",
    "Muted players", "Nobody is muted.", "Unmute", "player %llu",
    "Report", "%s is in the report form: finish it in the MKWMP tab (Report voice abuse).",
    ("Voice chat needs an MKWMP account: sign up in the MKWMP tab (or Mods > MKWMP > Options in the start screen), "
     "then restart the game."),
    "The MKWMP launcher runs voice chat this time: change it in the launcher's settings."};
static const char *const PT[V_COUNT] = {
    "Chat de voz", "Chat de voz nos lobbies e corridas online", "Microfone", "Alto-falantes ou fones", "Padrão do sistema",
    "Os dispositivos aparecem depois que o chat de voz começa (entre online uma vez).", "Volume do microfone",
    "Volume das vozes", "Jogadores silenciados", "Ninguém está silenciado.", "Reativar", "jogador %llu",
    "Denunciar", "%s está no formulário de denúncia: termine na aba MKWMP (Denunciar abuso no chat de voz).",
    ("O chat de voz precisa de uma conta MKWMP: cadastre-se na aba MKWMP (ou em Mods > MKWMP > Opções na tela "
     "inicial) e reinicie o jogo."),
    "Desta vez o launcher do MKWMP cuida do chat de voz: mude nas configurações dele."};

enum { DEVICES_MAX = 24 };
typedef struct {
    int n;
    char ids[DEVICES_MAX][256], names[DEVICES_MAX][128];
    const char *values[DEVICES_MAX + 1], *labels[DEVICES_MAX + 1];
} Devices;

static volatile int g_state = VOICETAB_NOT_SIGNED_UP;   // set at the plugin's start, read by the menu's thread

void voicetab_set_state(int state) {
    g_state = state;
}

// One list ("inputs" or "outputs") of the engine's {"inputs":[{"id","name"}],"outputs":[...]}, after "System default".
static void parse(const char *json, const char *key, const char *default_label, Devices *d) {
    d->n = 0;
    d->values[0] = "";
    d->labels[0] = default_label;
    const char *b, *e;
    if (!seatapi_json_span(json, key, &b, &e)) return;
    for (const char *p = b; p < e && d->n < DEVICES_MAX;) {
        const char *o = strchr(p, '{');
        if (!o || o >= e) break;
        const char *c = strchr(o, '}');
        if (!c || c > e) break;
        char obj[512];
        size_t len = (size_t)(c - o + 1);
        if (len < sizeof obj) {
            memcpy(obj, o, len);
            obj[len] = 0;
            if (seatapi_json_str(obj, "id", d->ids[d->n], sizeof d->ids[0]) && d->ids[d->n][0]) {
                if (!seatapi_json_str(obj, "name", d->names[d->n], sizeof d->names[0]) || !d->names[d->n][0])
                    snprintf(d->names[d->n], sizeof d->names[0], "%s", d->ids[d->n]);
                d->values[d->n + 1] = d->ids[d->n];
                d->labels[d->n + 1] = d->names[d->n];
                d->n++;
            }
        }
        p = c + 1;
    }
}

void voicetab_draw(const MkwPage *p) {
    if (p->size < sizeof(MkwPage)) return;
    const char *const *T = p->language && (!strcmp(p->language, "pt-BR") || !strcmp(p->language, "pt")) ? PT : EN;
    static char json[8192];
    static Devices in, out;
    if (p->opened || !in.n) {   // the list as the engine last reported it (it reports again every 30 s)
        if (seatd_devices_json(json, sizeof json)) {
            parse(json, "inputs", T[V_DEFAULT], &in);
            parse(json, "outputs", T[V_DEFAULT], &out);
        } else {
            in.n = out.n = 0;
            in.values[0] = out.values[0] = "";
            in.labels[0] = out.labels[0] = T[V_DEFAULT];
        }
    }
    in.labels[0] = out.labels[0] = T[V_DEFAULT];

    int state = g_state;
    if (state != VOICETAB_LIVE) p->note(MKW_NOTE_WARN, T[state == VOICETAB_LAUNCHER ? V_LAUNCHER : V_NOT_SIGNED_UP]);
    p->disabled_begin(state != VOICETAB_LIVE);
    p->columns_begin(2);
    p->checkbox("voice", T[V_ON], 0);   // off by default
    char v[8];
    int on = p->setting_get("voice", v, sizeof v) && !strcmp(v, "1");
    p->disabled_begin(!on);
    p->choice("voice_input", T[V_INPUT], in.values, in.labels, in.n + 1);
    p->slider("voice_in_gain", T[V_IN_GAIN], 1, 100, 100);
    p->choice("voice_output", T[V_OUTPUT], out.values, out.labels, out.n + 1);
    p->slider("voice_out_gain", T[V_OUT_GAIN], 1, 100, 100);
    if (!in.n && !out.n) p->note(MKW_NOTE_DIM, T[V_NO_DEVICES]);
    p->disabled_end();

    p->column_next();
    p->heading(T[V_MUTED]);
    SeatdMute mutes[SEATD_MUTES_MAX];
    int n = seatd_mutes(mutes, SEATD_MUTES_MAX);
    unsigned long long unmute = 0;
    static char reported[64];   // the last one sent to the report form (the menu's thread only)
    p->list_begin("muted", 8);
    p->list_empty(T[V_NONE_MUTED]);
    const char *const buttons[] = {T[V_UNMUTE], T[V_REPORT]};
    for (int i = 0; i < n; i++) {
        char name[64];
        if (mutes[i].name[0]) snprintf(name, sizeof name, "%s", mutes[i].name);
        else snprintf(name, sizeof name, T[V_PLAYER], mutes[i].account);
        // Report only where the game told the gamertag (the form needs it); it fills the MKWMP tab's form
        int b = p->list_row(name, NULL, buttons, seatapi_name_ok(mutes[i].name) ? 2 : 1);
        if (b == 1) unmute = mutes[i].account;
        if (b == 2 && p->setting_set("report_name", mutes[i].name)) snprintf(reported, sizeof reported, "%s", mutes[i].name);
    }
    p->list_end();
    if (unmute) seatd_unmute(unmute);
    if (reported[0]) {
        char line[200];
        snprintf(line, sizeof line, T[V_REPORT_NEXT], reported);
        p->note(MKW_NOTE_INFO, line);
    }
    p->columns_end();
    p->disabled_end();
}
