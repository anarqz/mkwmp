// The Camera settings: the tab of mkw-recomp's in-game settings and a section of the mod's options page. The values
// are the mod's settings, all off unless "1" (as voice): camera, camera_view, camera_mirror; camera_device is a capture
// device's name ("" the first one, "test" the test pattern). camera.c reads them twice a second, so a change applies
// without a restart. The device list is the capture library's (camlib.c), loaded when these widgets are first drawn,
// in the game and on the start screen alike.
#include "cameratab.h"

#include "camera.h"
#include "camlib.h"
#include "plat.h"
#include "seatapi.h"

#include <stdio.h>
#include <string.h>

enum { C_ON, C_DEVICE, C_FIRST, C_TEST, C_MIRROR, C_VIEW, C_WHAT, C_NOT_SENT, C_NEED_CONSENT, C_OLD_RUNTIME, C_NO_LIBRARY,
       C_FAILED, C_RUNNING, C_STARTING, C_GAME, C_COUNT };
static const char *const EN[C_COUNT] = {
    "Show my camera above my kart",
    "Camera",
    "First camera found",
    "Test pattern (colour bars)",
    "Mirror my picture",
    "Show the other players' cameras",
    "With the camera on, the players racing with you who chose to see cameras see your picture above your kart. "
    "Nothing is recorded. A player you mute no longer shows, and no longer sees you.",
    "This version does not send pictures to other players yet: only you see your camera.",
    "Accept the notice on the MKWMP page first (18 or older).",
    "The camera needs a newer mkw-recomp (plugin API 3).",
    "The camera library (" PLAT_CAMERA_LIB ") is not in the mod's folder: only the test pattern works.",
    "This camera could not be opened: another program may be using it.",
    "The camera is running.",
    "Opening the camera...",
    "The camera works in Mario Kart World 1.8.0 only, in online lobbies and races.",
};
static const char *const PT[C_COUNT] = {
    "Mostrar minha câmera em cima do meu kart",
    "Câmera",
    "Primeira câmera encontrada",
    "Padrão de teste (barras coloridas)",
    "Espelhar minha imagem",
    "Mostrar as câmeras dos outros jogadores",
    "Com a câmera ligada, os jogadores correndo com você que escolheram ver câmeras veem sua imagem em cima do seu kart. "
    "Nada é gravado. Um jogador que você silenciar deixa de aparecer e deixa de ver você.",
    "Esta versão ainda não envia imagens para outros jogadores: só você vê sua câmera.",
    "Aceite primeiro o aviso na página MKWMP (18 anos ou mais).",
    "A câmera precisa de um mkw-recomp mais novo (API de plugins 3).",
    "A biblioteca da câmera (" PLAT_CAMERA_LIB ") não está na pasta do mod: só o padrão de teste funciona.",
    "Não foi possível abrir esta câmera: outro programa pode estar usando.",
    "A câmera está funcionando.",
    "Abrindo a câmera...",
    "A câmera funciona só no Mario Kart World 1.8.0, em lobbies e corridas online.",
};

enum { DEVICES_MAX = 24 };
static struct {
    int n;
    char ids[DEVICES_MAX][256];
    const char *values[DEVICES_MAX + 2], *labels[DEVICES_MAX + 2];
} D;

// {"devices":[{"id","name"}]} between "the first one" and the test pattern.
static void parse(const char *json, const char *const *T) {
    D.n = 0;
    const char *b = NULL, *e = NULL;
    if (json && seatapi_json_span(json, "devices", &b, &e)) {
        for (const char *p = b; p < e && D.n < DEVICES_MAX;) {
            const char *o = strchr(p, '{');
            if (!o || o >= e) break;
            const char *c = strchr(o, '}');
            if (!c || c > e) break;
            char obj[600];
            size_t len = (size_t)(c - o + 1);
            if (len < sizeof obj) {
                memcpy(obj, o, len);
                obj[len] = 0;
                if (seatapi_json_str(obj, "id", D.ids[D.n], sizeof D.ids[0]) && D.ids[D.n][0] && strcmp(D.ids[D.n], CAMERA_DEVICE_TEST))
                    D.n++;
            }
            p = c + 1;
        }
    }
    D.values[0] = "", D.labels[0] = T[C_FIRST];
    for (int i = 0; i < D.n; i++) D.values[i + 1] = D.labels[i + 1] = D.ids[i];   // the id is the device's name
    D.values[D.n + 1] = CAMERA_DEVICE_TEST, D.labels[D.n + 1] = T[C_TEST];
}

static int is_on(const MkwPage *p, const char *key) {
    char v[8] = "";
    return p->setting_get(key, v, sizeof v) && !strcmp(v, "1");
}

void cameratab_widgets(const MkwPage *p) {
    const char *const *T = p->language && (!strcmp(p->language, "pt-BR") || !strcmp(p->language, "pt")) ? PT : EN;
    if (p->version < CAMERA_API_VERSION) {
        p->note(MKW_NOTE_DIM, T[C_OLD_RUNTIME]);
        return;
    }
    camlib_set_dir(p->mod_dir);
    camlib_show_devices();
    static char json[8192];
    parse(camlib_devices_json(json, sizeof json) ? json : NULL, T);

    int consent = is_on(p, "consent18");
    if (!consent) p->note(MKW_NOTE_WARN, T[C_NEED_CONSENT]);
    p->disabled_begin(!consent);
    p->checkbox("camera", T[C_ON], 0);   // off by default
    int on = is_on(p, "camera");
    p->disabled_begin(!on);
    p->choice("camera_device", T[C_DEVICE], D.values, D.labels, D.n + 2);
    p->checkbox("camera_mirror", T[C_MIRROR], 0);
    p->disabled_end();
    if (on && consent) {
        char device[256] = "";
        p->setting_get("camera_device", device, sizeof device);
        if (strcmp(device, CAMERA_DEVICE_TEST)) {
            int state = camlib_state();
            if (state == CAMLIB_MISSING) p->note(MKW_NOTE_WARN, T[C_NO_LIBRARY]);
            else if (state == CAMLIB_FAILED) p->note(MKW_NOTE_ERROR, T[C_FAILED]);
            else if (state == CAMLIB_RUNNING) p->note(MKW_NOTE_OK, T[C_RUNNING]);
            else if (state == CAMLIB_STARTING) p->busy(T[C_STARTING]);
        }
    }
    p->checkbox("camera_view", T[C_VIEW], 0);   // off by default too
    p->disabled_end();
    p->note(MKW_NOTE_DIM, T[C_WHAT]);
    p->note(MKW_NOTE_WARN, T[C_NOT_SENT]);
    p->note(MKW_NOTE_DIM, T[C_GAME]);
}

void cameratab_draw(const MkwPage *p) {
    if (p->size < sizeof(MkwPage)) return;
    cameratab_widgets(p);
}
