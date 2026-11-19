// plat_http.h on Windows: WinHTTP, synchronous, one session per request (a few requests a minute at most).
#include "plat_http.h"

#include <windows.h>
#include <winhttp.h>

#include <stdio.h>
#include <string.h>

static _Thread_local char g_error[160];

const char *plat_http_error(void) { return g_error; }

static void fail(const char *what) {
    snprintf(g_error, sizeof g_error, "%s (error %lu)", what, GetLastError());
}

PlatHttpResult plat_http(const char *method, const char *url, const char *bearer, const char *json, char *out,
                         size_t cap, int timeout_ms) {
    PlatHttpResult r = {0};
    if (cap) out[0] = 0;
    g_error[0] = 0;
    wchar_t wurl[1024], wmethod[16], host[256], path[1024];
    if (!MultiByteToWideChar(CP_UTF8, 0, url, -1, wurl, 1024) || !MultiByteToWideChar(CP_UTF8, 0, method, -1, wmethod, 16)) {
        fail("bad URL");
        return r;
    }
    URL_COMPONENTS u;
    memset(&u, 0, sizeof u);
    u.dwStructSize = sizeof u;
    u.lpszHostName = host;
    u.dwHostNameLength = 256;
    u.lpszUrlPath = path;
    u.dwUrlPathLength = 1024;
    wchar_t extra[512];
    u.lpszExtraInfo = extra;
    u.dwExtraInfoLength = 512;
    if (!WinHttpCrackUrl(wurl, 0, 0, &u)) {
        fail("bad URL");
        return r;
    }
    wcsncat(path, extra, 1023 - wcslen(path));   // the query string
    HINTERNET s = WinHttpOpen(L"mkwmp", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!s) s = WinHttpOpen(L"mkwmp", WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!s) {
        fail("WinHttpOpen");
        return r;
    }
    WinHttpSetTimeouts(s, timeout_ms, timeout_ms, timeout_ms, timeout_ms);
    HINTERNET c = WinHttpConnect(s, host, u.nPort, 0);
    HINTERNET q = c ? WinHttpOpenRequest(c, wmethod, path, NULL, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES,
                                         u.nScheme == INTERNET_SCHEME_HTTPS ? WINHTTP_FLAG_SECURE : 0)
                  : NULL;
    if (!q) {
        fail("WinHttpConnect");
    } else {
        wchar_t headers[512] = L"";
        if (json) wcscat(headers, L"Content-Type: application/json\r\n");
        if (bearer && *bearer) {
            wchar_t b[300];
            MultiByteToWideChar(CP_UTF8, 0, bearer, -1, b, 300);
            b[299] = 0;
            _snwprintf(headers + wcslen(headers), 511 - wcslen(headers), L"Authorization: Bearer %ls\r\n", b);
        }
        DWORD n = json ? (DWORD)strlen(json) : 0;
        if (!WinHttpSendRequest(q, headers[0] ? headers : WINHTTP_NO_ADDITIONAL_HEADERS, headers[0] ? (DWORD)-1L : 0,
                                (void *)json, n, n, 0) ||
            !WinHttpReceiveResponse(q, NULL)) {
            fail("no answer");
        } else {
            DWORD status = 0, size = sizeof status;
            WinHttpQueryHeaders(q, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX,
                                &status, &size, WINHTTP_NO_HEADER_INDEX);
            DWORD retry = 0;
            size = sizeof retry;
            if (WinHttpQueryHeaders(q, WINHTTP_QUERY_RETRY_AFTER | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX,
                                    &retry, &size, WINHTTP_NO_HEADER_INDEX))
                r.retry_after = (int)retry;
            size_t got = 0;
            for (;;) {
                DWORD avail = 0, read = 0;
                if (!WinHttpQueryDataAvailable(q, &avail) || !avail) break;
                char chunk[4096];
                if (avail > sizeof chunk) avail = sizeof chunk;
                if (!WinHttpReadData(q, chunk, avail, &read) || !read) break;
                if (cap && got < cap - 1) {
                    size_t k = read < cap - 1 - got ? read : cap - 1 - got;
                    memcpy(out + got, chunk, k);
                    got += k;
                }
            }
            if (cap) out[got] = 0;
            r.length = got;
            r.status = (int)status;
        }
        WinHttpCloseHandle(q);
    }
    if (c) WinHttpCloseHandle(c);
    WinHttpCloseHandle(s);
    return r;
}
