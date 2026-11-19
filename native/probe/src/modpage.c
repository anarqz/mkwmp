// MKWMP's options page in mkw-recomp's start screen (mkw_plugin.h's mkw_mod_page, plugin API 2): what the MKWMP
// launcher's window asks today, so the mod needs no other program. Runs in the start screen's process, not the game's:
// nothing of g_rt exists here, only the page, the mod's settings and the seat server (seatapi.c).
//
// Settings (the player's, <player>/mod-settings/mkwmp.ini; the game reads them back in backend_plugin.c):
//   consent18   "1" once the player accepted the notice (18+, voice with strangers, the name shown to others)
//   server      the seat server's URL; empty: SEATAPI_DEFAULT_SERVER
//   pin         the PIN of the name below; pin_name the profile name it was registered or entered for (a renamed
//               profile no longer matches, and the page asks again)
//   voice       "1" turns voice chat on (default off)
//   report_name, report_reason, report_details   the voice-abuse report being written (the Voice chat tab's Report
//               next to a muted player fills report_name)
// Transient fields: pin_entry, friend_add.
//
// Network calls run on one worker thread of the page's own; the page draws its state each frame.
#include "mkw_plugin.h"

#include "plat.h"
#include "seatapi.h"

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// ---- texts, English and Brazilian Portuguese (the plugin translates its own: page->language)
enum {
    T_HEAD, T_INTRO, T_CONSENT, T_CONSENT_BOX, T_CONSENT_FIRST, T_ACCOUNT, T_SERVER, T_PROFILE_BAD, T_SIGNED,
    T_PIN_SHOW, T_FORGET, T_NOT_YET, T_REGISTER, T_HAVE_PIN, T_PIN, T_PIN_HINT, T_USE_PIN, T_VOICE_HEAD, T_VOICE,
    T_FRIENDS, T_NO_FRIENDS, T_ADD, T_ADD_HINT, T_SEND, T_ACCEPT, T_DECLINE, T_CANCEL, T_REMOVE, T_WANTS, T_ASKED,
    T_PLAYING, T_ONLINE, T_OFFLINE, T_BUSY_REG, T_BUSY_PIN, T_BUSY_FRIENDS, T_REG_OK, T_TAKEN, T_RESERVED,
    T_RATE, T_OFFLINE_SRV, T_PIN_WRONG, T_PIN_OK, T_NOT_FOUND, T_FRIEND_SENT, T_FRIEND_DONE, T_FRIEND_UNKNOWN,
    T_ENABLE, T_SERVER_HEAD, T_SEATS, T_SEATS_QUEUE, T_SERVER_DOWN, T_IN_GAME,
    T_PRIVACY_HEAD, T_PRIVACY, T_DELETE, T_DELETE_ASK, T_DELETE_YES, T_DELETE_IN_GAME, T_BUSY_DELETE, T_DELETED,
    T_REPORT_HEAD, T_REPORT_NAME, T_REPORT_NAME_HINT, T_REPORT_REASON, T_REPORT_DETAILS, T_REPORT_DETAILS_HINT,
    T_REPORT_WARN, T_REPORT_SEND, T_REPORT_OPEN, T_REPORT_WITHDRAW, T_BUSY_REPORT, T_REPORT_SENT, T_REPORT_DONE,
    T_REPORT_UNKNOWN, T_REPORT_SELF, T_REPORT_ACTIVE, T_REPORT_CHECKING,
    T_R_HARASSMENT, T_R_HATE, T_R_SEXUAL, T_R_THREATS, T_R_DISRUPTION, T_R_OTHER, T_COUNT
};
static const char *const EN[T_COUNT] = {
    "Online play (MKWMP)",
    "MKWMP is a fan-run online service for mkw-recomp: races and battles with other players, friends and voice chat. "
    "It is not affiliated with Nintendo.",
    "Online, other players see your name and can hear you in voice chat; anyone can join a public race. You must be "
    "18 or older to use it.",
    "I am 18 or older and I accept this",
    "Accept the notice above to set up online play.",
    "Account",
    "Server",
    "The profile name \"%s\" cannot be used online: rename it on the Profiles page (3 to 16 letters, digits, - or _).",
    "Signed up as %s. Turn MKWMP on in the Mods list, then choose Online Play in the game.",
    "Your PIN is %s. Write it down: it is the only way to play as %s on another PC or with the MKWMP launcher.",
    "Use another PIN",
    "%s is not signed up yet.",
    "Sign up as %s",
    "Already signed up as this name (on another PC, or with the MKWMP launcher)? Enter its PIN:",
    "PIN",
    "6 digits",
    "Use this PIN",
    "Voice chat",
    "Voice chat in online lobbies and races",
    "Friends",
    "No friends yet: send a request by name below.",
    "Add a friend",
    "their name",
    "Send request",
    "Accept",
    "Decline",
    "Cancel",
    "Remove",
    "wants to be your friend",
    "request sent",
    "playing",
    "online",
    "offline",
    "Signing up...",
    "Checking the PIN...",
    "Updating friends...",
    "Signed up.",
    "This name is already signed up. If it is yours, enter its PIN below; if not, rename your profile.",
    "This name is reserved.",
    "Too many attempts: try again in %d s.",
    "The server did not answer (%s).",
    "Wrong PIN.",
    "PIN accepted.",
    "This name is not signed up on this server.",
    "Request sent to %s.",
    "Done.",
    "There is no player named %s.",
    "Turn MKWMP on in the Mods list to play online.",
    "Server",
    "%d of %d seats taken",
    "%d of %d seats taken, %d waiting in line",
    "The server does not answer right now.",
    "Changes to the account and the server apply at the next start; voice chat has its own tab.",
    "Privacy",
    "Privacy policy: https://anarqz.github.io/mkwmp/#privacy",
    "Delete my account",
    "Delete \"%s\" from this server? Its PIN, your friends and the reports you filed are removed, and anyone can then "
    "sign up as this name.",
    "Delete",
    "Delete the account from Mods > MKWMP > Options in the start screen, with the game closed.",
    "Deleting the account...",
    "Account deleted from this server.",
    "Report voice abuse",
    "Who",
    "their name",
    "What happened",
    "Details",
    "optional: when, in which lobby, what was said",
    "The server's host reviews reports. False reports can get you blocked.",
    "Send report",
    "Your report about %s (%s) is waiting for the host's review. You can have one report waiting at a time.",
    "Withdraw",
    "Sending...",
    "Report sent: the server's host will review it.",
    "Report withdrawn.",
    "There is no player named %s.",
    "You cannot report yourself.",
    "You already have a report waiting.",
    "Checking your reports...",
    "Harassment or bullying",
    "Hate speech or slurs",
    "Sexual content",
    "Threats or doxxing",
    "Loud noise, music or spam",
    "Something else",
};
static const char *const PT[T_COUNT] = {
    "Jogo online (MKWMP)",
    "MKWMP é um serviço online feito por fãs para o mkw-recomp: corridas e batalhas com outros jogadores, amigos e "
    "chat de voz. Não tem ligação com a Nintendo.",
    "Online, outros jogadores veem seu nome e podem ouvir você no chat de voz; qualquer um pode entrar numa corrida "
    "pública. Você precisa ter 18 anos ou mais para usar.",
    "Tenho 18 anos ou mais e aceito",
    "Aceite o aviso acima para configurar o jogo online.",
    "Conta",
    "Servidor",
    "O nome de perfil \"%s\" não pode ser usado online: renomeie na página Perfis (3 a 16 letras, números, - ou _).",
    "Cadastrado como %s. Ligue o MKWMP na lista de Mods e escolha Jogo Online no jogo.",
    "Seu PIN é %s. Anote: é o único jeito de jogar como %s em outro PC ou com o launcher do MKWMP.",
    "Usar outro PIN",
    "%s ainda não está cadastrado.",
    "Cadastrar como %s",
    "Já cadastrou este nome (em outro PC ou no launcher do MKWMP)? Digite o PIN:",
    "PIN",
    "6 dígitos",
    "Usar este PIN",
    "Chat de voz",
    "Chat de voz nos lobbies e corridas online",
    "Amigos",
    "Nenhum amigo ainda: envie um pedido pelo nome abaixo.",
    "Adicionar amigo",
    "o nome",
    "Enviar pedido",
    "Aceitar",
    "Recusar",
    "Cancelar",
    "Remover",
    "quer ser seu amigo",
    "pedido enviado",
    "jogando",
    "online",
    "offline",
    "Cadastrando...",
    "Verificando o PIN...",
    "Atualizando amigos...",
    "Cadastrado.",
    "Este nome já está cadastrado. Se for seu, digite o PIN abaixo; se não, renomeie o perfil.",
    "Este nome é reservado.",
    "Tentativas demais: tente de novo em %d s.",
    "O servidor não respondeu (%s).",
    "PIN errado.",
    "PIN aceito.",
    "Este nome não está cadastrado neste servidor.",
    "Pedido enviado para %s.",
    "Pronto.",
    "Não existe jogador chamado %s.",
    "Ligue o MKWMP na lista de Mods para jogar online.",
    "Servidor",
    "%d de %d vagas ocupadas",
    "%d de %d vagas ocupadas, %d na fila",
    "O servidor não está respondendo agora.",
    "Mudanças na conta e no servidor valem no próximo início; o chat de voz tem sua própria aba.",
    "Privacidade",
    "Política de privacidade: https://anarqz.github.io/mkwmp/#privacy",
    "Apagar minha conta",
    "Apagar \"%s\" deste servidor? O PIN, seus amigos e as denúncias que você fez são removidos, e qualquer um poderá "
    "se cadastrar com este nome.",
    "Apagar",
    "Apague a conta em Mods > MKWMP > Opções na tela inicial, com o jogo fechado.",
    "Apagando a conta...",
    "Conta apagada deste servidor.",
    "Denunciar abuso no chat de voz",
    "Quem",
    "o nome",
    "O que aconteceu",
    "Detalhes",
    "opcional: quando, em qual lobby, o que foi dito",
    "Quem hospeda o servidor analisa as denúncias. Denúncias falsas podem bloquear você.",
    "Enviar denúncia",
    "Sua denúncia sobre %s (%s) aguarda a análise de quem hospeda o servidor. Só uma denúncia por vez.",
    "Retirar",
    "Enviando...",
    "Denúncia enviada: quem hospeda o servidor vai analisar.",
    "Denúncia retirada.",
    "Não existe jogador chamado %s.",
    "Você não pode denunciar a si mesmo.",
    "Você já tem uma denúncia aguardando.",
    "Verificando suas denúncias...",
    "Assédio ou bullying",
    "Discurso de ódio ou ofensas",
    "Conteúdo sexual",
    "Ameaças ou exposição de dados",
    "Barulho, música ou spam",
    "Outra coisa",
};
static const char *const *T = EN;

// ---- the worker's state, shared with the page (under g_lock)
enum { JOB_NONE, JOB_REGISTER, JOB_PIN, JOB_FRIENDS, JOB_ADD, JOB_ACCEPT, JOB_REMOVE, JOB_STATUS, JOB_DELETE,
       JOB_REPORT_GET, JOB_REPORT_FILE, JOB_REPORT_WITHDRAW };
static const char *const REASONS[] = {SEATAPI_REPORT_REASONS};
enum { NREASONS = sizeof REASONS / sizeof *REASONS };
static PlatLock g_lock = PLAT_LOCK_INIT;
static struct {
    int job;
    char base[256], name[SEATAPI_NAME_MAX + 1], pin[SEATAPI_PIN_LEN + 1], target[SEATAPI_NAME_MAX + 1];
    int busy;                      // a T_BUSY_* while a job runs
    int note_kind;
    char note[300];
    char revealed_pin[SEATAPI_PIN_LEN + 1];   // shown once after signing up, until the page closes
    SeatApiFriends friends;
    int have_friends;
    uint64_t friends_at, drawn_at, status_at;
    int status_ok, seats_total, seated, queued;   // the server's last /status (status_ok -1: no answer)
    char status_base[256];
    char token[160], token_for[SEATAPI_NAME_MAX + SEATAPI_PIN_LEN + 260];
    char reason[16], details[SEATAPI_REPORT_DETAILS_MAX + 1];   // JOB_REPORT_FILE's
    int report_state;          // 0 not known yet, 1 none open, 2 one open (report)
    SeatApiReport report;
    uint64_t report_at;
} W;
static int (*g_set)(const char *key, const char *value);   // the page's setting_set (any thread)
static int g_worker;

static void set_note(int kind, const char *fmt, ...) __attribute__((format(printf, 2, 3)));
static void set_note(int kind, const char *fmt, ...) {   // g_lock held
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(W.note, sizeof W.note, fmt, ap);
    va_end(ap);
    W.note_kind = kind;
}

// A failed call as a note; g_lock held.
static void note_error(const SeatApiResult *r) {
    if (r->status == 429) set_note(MKW_NOTE_WARN, T[T_RATE], r->retry_after ? r->retry_after : 60);
    else if (!r->status) set_note(MKW_NOTE_ERROR, T[T_OFFLINE_SRV], r->message);
    else if (!strcmp(r->code, "PIN_WRONG")) set_note(MKW_NOTE_ERROR, "%s", T[T_PIN_WRONG]);
    else if (!strcmp(r->code, "PROFILE_NOT_FOUND")) set_note(MKW_NOTE_ERROR, "%s", T[T_NOT_FOUND]);
    else if (!strcmp(r->code, "FRIEND_UNKNOWN")) set_note(MKW_NOTE_ERROR, T[T_FRIEND_UNKNOWN], W.target);
    else if (!strcmp(r->code, "REPORT_UNKNOWN")) set_note(MKW_NOTE_ERROR, T[T_REPORT_UNKNOWN], W.target);
    else if (!strcmp(r->code, "REPORT_SELF")) set_note(MKW_NOTE_ERROR, "%s", T[T_REPORT_SELF]);
    else if (!strcmp(r->code, "REPORT_ACTIVE")) set_note(MKW_NOTE_WARN, "%s", T[T_REPORT_ACTIVE]);
    else set_note(MKW_NOTE_ERROR, "%s", r->message);
}

// A presence token for name + PIN (signed in again when they change); g_lock released during the call.
static int presence(const char *base, const char *name, const char *pin) {
    char key[sizeof W.token_for];
    snprintf(key, sizeof key, "%s|%s|%s", base, name, pin);
    plat_lock(&g_lock);
    int have = W.token[0] && !strcmp(W.token_for, key);
    plat_unlock(&g_lock);
    if (have) return 1;
    char token[160];
    SeatApiResult r = seatapi_presence(base, name, pin, token, sizeof token);
    plat_lock(&g_lock);
    if (token[0]) {
        snprintf(W.token, sizeof W.token, "%s", token);
        snprintf(W.token_for, sizeof W.token_for, "%s", key);
    } else {
        note_error(&r);
    }
    plat_unlock(&g_lock);
    return token[0] != 0;
}

static void refresh_friends(const char *base, const char *name, const char *pin) {
    for (int attempt = 0; attempt < 2; attempt++) {
        if (!presence(base, name, pin)) return;
        static SeatApiFriends f;
        char token[160];
        plat_lock(&g_lock);
        snprintf(token, sizeof token, "%s", W.token);
        plat_unlock(&g_lock);
        SeatApiResult r = seatapi_friends(base, token, &f);
        plat_lock(&g_lock);
        if (r.status == 401) {
            W.token[0] = 0;   // expired: sign in again, once
            plat_unlock(&g_lock);
            continue;
        }
        if (r.status == 200) {
            W.friends = f;
            W.have_friends = 1;
        } else {
            note_error(&r);
        }
        W.friends_at = plat_ms();
        plat_unlock(&g_lock);
        return;
    }
}

// The token for name + PIN, signed in if needed, into token; 0 when signing in failed (its note set).
static int token_for(const char *base, const char *name, const char *pin, char *token, size_t cap) {
    if (!presence(base, name, pin)) return 0;
    plat_lock(&g_lock);
    snprintf(token, cap, "%s", W.token);
    plat_unlock(&g_lock);
    return 1;
}

// The open report again (GET /seat/v1/reports); g_lock not held.
static void refresh_report(const char *base, const char *name, const char *pin) {
    for (int attempt = 0; attempt < 2; attempt++) {
        char token[160];
        if (!token_for(base, name, pin, token, sizeof token)) return;
        SeatApiReport rep;
        int has = 0;
        SeatApiResult r = seatapi_report_get(base, token, &rep, &has);
        plat_lock(&g_lock);
        if (r.status == 401) {
            W.token[0] = 0;
            plat_unlock(&g_lock);
            continue;
        }
        if (r.status == 200) {
            W.report_state = has ? 2 : 1;
            W.report = rep;
        }
        W.report_at = plat_ms();
        plat_unlock(&g_lock);
        return;
    }
}

static void worker(void *unused) {
    (void)unused;
    for (;;) {
        plat_sleep_ms(100);
        plat_lock(&g_lock);
        int job = W.job;
        char base[sizeof W.base], name[sizeof W.name], pin[sizeof W.pin], target[sizeof W.target];
        char reason[sizeof W.reason], details[sizeof W.details];
        memcpy(reason, W.reason, sizeof reason);
        memcpy(details, W.details, sizeof details);
        memcpy(base, W.base, sizeof base);
        memcpy(name, W.name, sizeof name);
        memcpy(pin, W.pin, sizeof pin);
        memcpy(target, W.target, sizeof target);
        uint64_t now = plat_ms();
        // the list stays fresh while the page is shown and signed in
        if (!job && pin[0] && now - W.drawn_at < 2000 && now - W.friends_at > 10000) job = JOB_FRIENDS;
        // the seats, every 15 s while shown (or at once for another server)
        if (!job && base[0] && now - W.drawn_at < 2000 && (now - W.status_at > 15000 || strcmp(W.status_base, base)))
            job = JOB_STATUS;
        // the open report, every minute while shown (the server allows 60 a minute)
        if (!job && pin[0] && now - W.drawn_at < 2000 && (!W.report_state || now - W.report_at > 60000)) job = JOB_REPORT_GET;
        W.job = JOB_NONE;
        W.busy = job == JOB_REGISTER                                       ? T_BUSY_REG
                 : job == JOB_PIN                                          ? T_BUSY_PIN
                 : job == JOB_DELETE                                       ? T_BUSY_DELETE
                 : job == JOB_REPORT_FILE || job == JOB_REPORT_WITHDRAW    ? T_BUSY_REPORT
                 : job == JOB_STATUS || job == JOB_REPORT_GET || !job      ? 0
                                                                           : T_BUSY_FRIENDS;
        plat_unlock(&g_lock);
        if (!job) continue;

        if (job == JOB_REGISTER) {
            char got[SEATAPI_PIN_LEN + 1];
            SeatApiResult r = seatapi_register(base, name, got);
            plat_lock(&g_lock);
            if (r.status == 201 && g_set && g_set("pin", got) && g_set("pin_name", name)) {
                snprintf(W.revealed_pin, sizeof W.revealed_pin, "%s", got);
                set_note(MKW_NOTE_OK, "%s", T[T_REG_OK]);
                W.friends_at = 0;
            } else if (r.status == 409) {
                set_note(MKW_NOTE_WARN, "%s", T[T_TAKEN]);
            } else if (r.status == 403) {
                set_note(MKW_NOTE_ERROR, "%s", T[T_RESERVED]);
            } else {
                note_error(&r);
            }
        } else if (job == JOB_PIN) {
            // the PIN typed is checked by signing in to a presence: 401 PIN_WRONG otherwise
            char token[160];
            SeatApiResult r = seatapi_presence(base, name, target, token, sizeof token);
            plat_lock(&g_lock);
            if (token[0] && g_set && g_set("pin", target) && g_set("pin_name", name)) {
                g_set("pin_entry", NULL);
                snprintf(W.token, sizeof W.token, "%s", token);
                snprintf(W.token_for, sizeof W.token_for, "%s|%s|%s", base, name, target);
                set_note(MKW_NOTE_OK, "%s", T[T_PIN_OK]);
                W.friends_at = 0;
            } else {
                note_error(&r);
            }
        } else if (job == JOB_DELETE) {
            SeatApiResult r = seatapi_profile_delete(base, name, pin);
            plat_lock(&g_lock);
            if (r.status == 204 || r.status == 200 || (r.status == 404 && !strcmp(r.code, "PROFILE_NOT_FOUND"))) {
                if (g_set) g_set("pin", NULL), g_set("pin_name", NULL);
                W.token[0] = W.revealed_pin[0] = 0;
                W.have_friends = 0;
                W.report_state = 0;
                set_note(MKW_NOTE_OK, "%s", T[T_DELETED]);
            } else if (r.status == 401 || r.status == 403) {
                set_note(MKW_NOTE_ERROR, "%s", T[T_PIN_WRONG]);
            } else {
                note_error(&r);
            }
        } else if (job == JOB_REPORT_GET) {
            refresh_report(base, name, pin);
            plat_lock(&g_lock);
        } else if (job == JOB_REPORT_FILE || job == JOB_REPORT_WITHDRAW) {
            SeatApiResult r = {0};
            char token[160];
            if (token_for(base, name, pin, token, sizeof token))
                r = job == JOB_REPORT_FILE ? seatapi_report_file(base, token, target, reason, details, NULL)
                                           : seatapi_report_withdraw(base, token);
            plat_lock(&g_lock);
            if (r.status == 201 || r.status == 204) {
                set_note(MKW_NOTE_OK, "%s", T[job == JOB_REPORT_FILE ? T_REPORT_SENT : T_REPORT_DONE]);
                if (job == JOB_REPORT_FILE && g_set) g_set("report_name", NULL), g_set("report_details", NULL);
            } else if (r.status == 401) {
                W.token[0] = 0;
                note_error(&r);
            } else if (r.status) {
                note_error(&r);
            }
            plat_unlock(&g_lock);
            refresh_report(base, name, pin);
            plat_lock(&g_lock);
        } else if (job == JOB_FRIENDS) {
            refresh_friends(base, name, pin);
            plat_lock(&g_lock);
        } else if (job == JOB_STATUS) {
            SeatApiStatus st;
            SeatApiResult r = seatapi_status(base, &st);
            free(st.lobby_ca);
            plat_lock(&g_lock);
            W.status_ok = r.status == 200 ? 1 : -1;
            W.seats_total = st.seats_total, W.seated = st.seated, W.queued = st.queued;
            W.status_at = plat_ms();
            snprintf(W.status_base, sizeof W.status_base, "%s", base);
        } else {   // add, accept, remove
            SeatApiResult r = {0};
            if (presence(base, name, pin)) {
                char token[160];
                plat_lock(&g_lock);
                snprintf(token, sizeof token, "%s", W.token);
                plat_unlock(&g_lock);
                r = job == JOB_ADD      ? seatapi_friend_request(base, token, target)
                    : job == JOB_ACCEPT ? seatapi_friend_accept(base, token, target)
                                        : seatapi_friend_remove(base, token, target);
            }
            plat_lock(&g_lock);
            if (r.status == 200 || r.status == 201 || r.status == 204) {
                if (job == JOB_ADD) {
                    set_note(MKW_NOTE_OK, T[T_FRIEND_SENT], target);
                    if (g_set) g_set("friend_add", NULL);
                } else {
                    set_note(MKW_NOTE_OK, "%s", T[T_FRIEND_DONE]);
                }
            } else if (r.status) {
                note_error(&r);
            }
            plat_unlock(&g_lock);
            refresh_friends(base, name, pin);
            plat_lock(&g_lock);
        }
        W.busy = 0;
        plat_unlock(&g_lock);
    }
}

static void start_job(int job, const char *base, const char *name, const char *pin, const char *target) {
    plat_lock(&g_lock);
    if (!W.busy && !W.job) {
        W.job = job;
        snprintf(W.base, sizeof W.base, "%s", base);
        snprintf(W.name, sizeof W.name, "%s", name);
        snprintf(W.pin, sizeof W.pin, "%s", pin ? pin : "");
        snprintf(W.target, sizeof W.target, "%s", target ? target : "");
        W.note[0] = 0;
    }
    plat_unlock(&g_lock);
}

static void start_report(const char *base, const char *name, const char *pin, const char *who, const char *reason,
                         const char *details) {
    plat_lock(&g_lock);
    int idle = !W.busy && !W.job;
    if (idle) {
        snprintf(W.reason, sizeof W.reason, "%s", reason);
        snprintf(W.details, sizeof W.details, "%s", details);
    }
    plat_unlock(&g_lock);
    if (idle) start_job(JOB_REPORT_FILE, base, name, pin, who);
}

static int same_name(const char *a, const char *b) {   // names compare case-insensitively on the server
    for (; *a && *b; a++, b++)
        if ((*a | 0x20) != (*b | 0x20)) return 0;
    return !*a && !*b;
}

static const char *reason_label(const char *code) {
    for (int i = 0; i < NREASONS; i++)
        if (!strcmp(code, REASONS[i])) return T[T_R_HARASSMENT + i];
    return code;
}

static int setting_is(const MkwPage *p, const char *key, const char *value, int unset) {
    char v[16];
    return p->setting_get(key, v, sizeof v) ? !strcmp(v, value) : unset;
}

MKW_PLUGIN_EXPORT void mkw_mod_page(const MkwPage *p) {
    if (p->size < sizeof(MkwPage)) return;
    T = p->language && (!strcmp(p->language, "pt-BR") || !strcmp(p->language, "pt")) ? PT : EN;
    g_set = p->setting_set;
    if (!g_worker) g_worker = plat_thread_start(worker, NULL);
    char line[400];

    // what the page shows this frame
    char server[256] = "", base[256], pin[16] = "", pin_name[64] = "";
    p->setting_get("server", server, sizeof server);
    if (!seatapi_base(server, base, sizeof base)) seatapi_base("", base, sizeof base);
    p->setting_get("pin", pin, sizeof pin);
    p->setting_get("pin_name", pin_name, sizeof pin_name);
    const char *name = p->profile_name ? p->profile_name : "";
    int name_ok = seatapi_name_ok(name);
    int signed_up = name_ok && seatapi_pin_ok(pin) && !strcmp(pin_name, name);
    int consented = setting_is(p, "consent18", "1", 0);
    plat_lock(&g_lock);
    static int confirm_delete;   // the page's thread only
    if (p->opened) {   // another page opening or another player: nothing of the last one shows
        W.note[0] = W.revealed_pin[0] = 0;
        W.have_friends = 0;
        W.friends_at = 0;
        W.report_state = 0;
        confirm_delete = 0;
    }
    W.drawn_at = plat_ms();
    // the worker's refreshes use the page's current server and account; nothing is asked of a server before consent
    snprintf(W.base, sizeof W.base, "%s", consented ? base : "");
    if (signed_up) {
        snprintf(W.name, sizeof W.name, "%s", name);
        snprintf(W.pin, sizeof W.pin, "%s", pin);
    } else {
        W.pin[0] = 0;
    }
    int status_ok = consented ? W.status_ok : 0, seats_total = W.seats_total, seated = W.seated, queued = W.queued;
    int busy = W.busy || W.job;
    int busy_text = W.busy;
    int report_state = W.report_state;
    SeatApiReport report = W.report;
    int note_kind = W.note_kind;
    char note[sizeof W.note], revealed[sizeof W.revealed_pin];
    memcpy(note, W.note, sizeof note);
    memcpy(revealed, W.revealed_pin, sizeof revealed);
    plat_unlock(&g_lock);

    p->columns_begin(2);   // the settings on the left, the server and the friends on the right
    if (p->in_game) p->note(MKW_NOTE_DIM, T[T_IN_GAME]);
    p->text(T[T_INTRO]);
    p->note(MKW_NOTE_WARN, T[T_CONSENT]);
    p->checkbox("consent18", T[T_CONSENT_BOX], 0);
    if (!consented) p->note(MKW_NOTE_DIM, T[T_CONSENT_FIRST]);
    p->disabled_begin(!consented);

    p->heading(T[T_ACCOUNT]);
    p->input("server", T[T_SERVER], SEATAPI_DEFAULT_SERVER, 200, 0);
    if (!name_ok) {
        snprintf(line, sizeof line, T[T_PROFILE_BAD], name);
        p->note(MKW_NOTE_ERROR, line);
    } else if (signed_up) {
        snprintf(line, sizeof line, T[T_SIGNED], name);
        p->note(MKW_NOTE_OK, line);
        if (revealed[0]) {
            snprintf(line, sizeof line, T[T_PIN_SHOW], revealed, name);
            p->note(MKW_NOTE_INFO, line);
        }
        if (p->button(T[T_FORGET])) {
            p->setting_set("pin", NULL);
            p->setting_set("pin_name", NULL);
            plat_lock(&g_lock);
            W.revealed_pin[0] = 0;
            W.have_friends = 0;
            plat_unlock(&g_lock);
        }
    } else {
        snprintf(line, sizeof line, T[T_NOT_YET], name);
        p->text(line);
        p->disabled_begin(busy);
        snprintf(line, sizeof line, T[T_REGISTER], name);
        if (p->button(line)) start_job(JOB_REGISTER, base, name, NULL, NULL);
        p->disabled_end();
        p->text(T[T_HAVE_PIN]);
        p->input("pin_entry", T[T_PIN], T[T_PIN_HINT], SEATAPI_PIN_LEN, MKW_INPUT_DIGITS | MKW_INPUT_PASSWORD);
        char entry[16] = "";
        p->setting_get("pin_entry", entry, sizeof entry);
        p->disabled_begin(busy || !seatapi_pin_ok(entry));
        if (p->button(T[T_USE_PIN])) start_job(JOB_PIN, base, name, NULL, entry);
        p->disabled_end();
    }
    if (busy_text) p->busy(T[busy_text]);
    if (note[0]) p->note(note_kind, note);

    p->heading(T[T_VOICE_HEAD]);
    p->checkbox("voice", T[T_VOICE], 0);   // off by default

    p->heading(T[T_PRIVACY_HEAD]);
    p->note(MKW_NOTE_DIM, T[T_PRIVACY]);
    if (signed_up) {
        if (p->in_game) {
            // the game's own seat client holds this account (and would sign it up again with the saved PIN)
            p->note(MKW_NOTE_DIM, T[T_DELETE_IN_GAME]);
        } else if (!confirm_delete) {
            p->disabled_begin(busy);
            if (p->button(T[T_DELETE])) confirm_delete = 1;
            p->disabled_end();
        } else {
            snprintf(line, sizeof line, T[T_DELETE_ASK], name);
            p->note(MKW_NOTE_ERROR, line);
            p->disabled_begin(busy);
            if (p->button(T[T_DELETE_YES])) {
                confirm_delete = 0;
                start_job(JOB_DELETE, base, name, pin, NULL);
            }
            p->same_line();
            if (p->button(T[T_CANCEL])) confirm_delete = 0;
            p->disabled_end();
        }
    }

    p->column_next();
    p->heading(T[T_SERVER_HEAD]);
    if (status_ok > 0) {
        snprintf(line, sizeof line, queued ? T[T_SEATS_QUEUE] : T[T_SEATS], seated, seats_total, queued);
        p->note(seated >= seats_total && seats_total ? MKW_NOTE_WARN : MKW_NOTE_OK, line);
    } else if (status_ok < 0) {
        p->note(MKW_NOTE_ERROR, T[T_SERVER_DOWN]);
    } else if (consented) {
        p->busy(base);
    }

    if (signed_up) {
        p->heading(T[T_FRIENDS]);
        static SeatApiFriends f;
        plat_lock(&g_lock);
        f = W.friends;
        int have = W.have_friends;
        plat_unlock(&g_lock);
        if (have) {
            int job = JOB_NONE;
            const char *who = NULL;
            p->list_begin("friends", 8);
            p->list_empty(T[T_NO_FRIENDS]);
            const char *const in_buttons[] = {T[T_ACCEPT], T[T_DECLINE]};
            const char *const out_buttons[] = {T[T_CANCEL]};
            const char *const friend_buttons[] = {T[T_REMOVE]};
            for (int i = 0; i < f.nincoming; i++) {
                int b = p->list_row(f.incoming[i].name, T[T_WANTS], in_buttons, 2);
                if (b) job = b == 1 ? JOB_ACCEPT : JOB_REMOVE, who = f.incoming[i].name;
            }
            for (int i = 0; i < f.nfriends; i++) {
                const char *st = f.friends[i].state;
                const char *detail = !strcmp(st, "playing") ? T[T_PLAYING] : !strcmp(st, "online") ? T[T_ONLINE] : T[T_OFFLINE];
                if (p->list_row(f.friends[i].name, detail, friend_buttons, 1)) job = JOB_REMOVE, who = f.friends[i].name;
            }
            for (int i = 0; i < f.noutgoing; i++)
                if (p->list_row(f.outgoing[i].name, T[T_ASKED], out_buttons, 1)) job = JOB_REMOVE, who = f.outgoing[i].name;
            p->list_end();
            if (job && who) start_job(job, base, name, pin, who);
        } else if (!busy_text) {
            p->busy(T[T_BUSY_FRIENDS]);
        }
        p->input("friend_add", T[T_ADD], T[T_ADD_HINT], SEATAPI_NAME_MAX, 0);
        char add[32] = "";
        p->setting_get("friend_add", add, sizeof add);
        p->disabled_begin(busy || !seatapi_name_ok(add));
        if (p->button(T[T_SEND])) start_job(JOB_ADD, base, name, pin, add);
        p->disabled_end();

        p->heading(T[T_REPORT_HEAD]);
        if (report_state == 2) {
            snprintf(line, sizeof line, T[T_REPORT_OPEN], report.reported, reason_label(report.reason));
            p->note(MKW_NOTE_INFO, line);
            p->disabled_begin(busy);
            if (p->button(T[T_REPORT_WITHDRAW])) start_job(JOB_REPORT_WITHDRAW, base, name, pin, NULL);
            p->disabled_end();
        } else if (report_state == 1) {
            p->input("report_name", T[T_REPORT_NAME], T[T_REPORT_NAME_HINT], SEATAPI_NAME_MAX, 0);
            const char *labels[NREASONS];
            for (int i = 0; i < NREASONS; i++) labels[i] = T[T_R_HARASSMENT + i];
            p->choice("report_reason", T[T_REPORT_REASON], REASONS, labels, NREASONS);
            p->input("report_details", T[T_REPORT_DETAILS], T[T_REPORT_DETAILS_HINT], 255, 0);
            p->note(MKW_NOTE_DIM, T[T_REPORT_WARN]);
            char who[32] = "", reason[16] = "", details[300] = "";
            p->setting_get("report_name", who, sizeof who);
            if (!p->setting_get("report_reason", reason, sizeof reason) || !reason[0])
                snprintf(reason, sizeof reason, "%s", REASONS[0]);
            p->setting_get("report_details", details, sizeof details);
            int self = same_name(who, name);
            p->disabled_begin(busy || !seatapi_name_ok(who) || self);
            if (p->button(T[T_REPORT_SEND])) start_report(base, name, pin, who, reason, details);
            p->disabled_end();
        } else {
            p->busy(T[T_REPORT_CHECKING]);
        }

        p->separator();
        if (!p->in_game) p->note(MKW_NOTE_DIM, T[T_ENABLE]);
    }
    p->disabled_end();
    p->columns_end();
}
