#include "lobby_ca.h"

#include <windows.h>
#include <string.h>

#include "capture_cert.h"
#include "config.h"
#include "log.h"

static int b64(unsigned char ch) {
    if (ch >= 'A' && ch <= 'Z') return ch - 'A';
    if (ch >= 'a' && ch <= 'z') return ch - 'a' + 26;
    if (ch >= '0' && ch <= '9') return ch - '0' + 52;
    if (ch == '+') return 62;
    if (ch == '/') return 63;
    return -1;
}

size_t lobby_ca_decode(const unsigned char *in, size_t len, unsigned char *der, size_t cap) {
    if (len >= 2 && in[0] == 0x30 && (in[1] & 0x80)) {   // already DER
        if (len > cap) return 0;
        memcpy(der, in, len);
        return len;
    }
    static const char begin[] = "-----BEGIN CERTIFICATE-----";
    static const char end[] = "-----END CERTIFICATE-----";
    const unsigned char *p = NULL;
    for (size_t i = 0; i + sizeof begin - 1 <= len; i++) {
        if (!memcmp(in + i, begin, sizeof begin - 1)) { p = in + i + sizeof begin - 1; break; }
    }
    if (!p) return 0;
    size_t out = 0;
    unsigned acc = 0;
    int bits = 0;
    for (; p < in + len; p++) {
        if ((size_t)(in + len - p) >= sizeof end - 1 && !memcmp(p, end, sizeof end - 1)) {
            return out >= 2 && der[0] == 0x30 ? out : 0;
        }
        if (*p == '=') continue;
        int v = b64(*p);
        if (v < 0) continue;   // newlines, spaces
        acc = (acc << 6) | (unsigned)v;
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            if (out >= cap) return 0;
            der[out++] = (unsigned char)(acc >> bits);
        }
    }
    return 0;   // no END line
}

const unsigned char *lobby_ca(size_t *len) {
    static unsigned char der[LOBBY_CA_MAX];
    static size_t der_len;
    static int loaded;
    if (!loaded) {
        loaded = 1;
        wchar_t path[MAX_PATH];
        DWORD n = GetModuleFileNameW(NULL, path, MAX_PATH);
        const char *name = mkwmp_config()->lobby_ca;
        if (n && n < MAX_PATH && name[0] && !strchr(name, '\\') && !strchr(name, '/') && !strstr(name, "..")) {
            for (DWORD i = n; i > 0; i--) {
                if (path[i - 1] == L'\\' || path[i - 1] == L'/') { path[i] = 0; break; }
            }
            size_t at = wcslen(path);
            for (size_t i = 0; name[i] && at + 1 < MAX_PATH; i++) path[at++] = (wchar_t)(unsigned char)name[i];
            path[at] = 0;
            HANDLE fh = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
            if (fh != INVALID_HANDLE_VALUE) {
                static unsigned char text[16384];
                DWORD got = 0;
                ReadFile(fh, text, sizeof text, &got, NULL);
                CloseHandle(fh);
                der_len = lobby_ca_decode(text, got, der, sizeof der);
                log_line(der_len ? "lobby CA: using the server's certificate from the game folder"
                                 : "lobby CA: file present but not a certificate; using the built-in one");
            }
        }
    }
    if (der_len) {
        *len = der_len;
        return der;
    }
    *len = sizeof kCaptureCert;
    return kCaptureCert;
}
