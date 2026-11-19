/* mkwmp_camera.h on Windows: DirectShow. Devices are the video input category's filters, so the list has the real
 * webcams (through the kernel streaming proxy) and software sources such as the OBS Virtual Camera, which Media
 * Foundation does not list. Capture is source -> Sample Grabber -> Null Renderer, the grabber asked for RGB32 first
 * (DirectShow then inserts its own decoders for MJPG and the YUV formats), then RGB24, YUY2 and NV12, which
 * camera_convert.h reads itself.
 *
 * One capture at a time, on a thread of the library's own that owns the COM objects; the grabber calls back on
 * DirectShow's streaming thread, where the picture is cropped, scaled and published under a lock. */
#define COBJMACROS
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <objbase.h>
#include <oleauto.h>
#include <dshow.h>
#include <qedit.h>

#include "camera_convert.h"
#include "mkwmp_camera.h"

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* The GUIDs, spelled out here so the library links against no GUID library. */
#define K(name, a, b, c, d0, d1, d2, d3, d4, d5, d6, d7) static const GUID name = {a, b, c, {d0, d1, d2, d3, d4, d5, d6, d7}}
K(K_IUnknown, 0x00000000, 0x0000, 0x0000, 0xc0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46);
K(K_SystemDeviceEnum, 0x62be5d10, 0x60eb, 0x11d0, 0xbd, 0x3b, 0x00, 0xa0, 0xc9, 0x11, 0xce, 0x86);
K(K_VideoInputDeviceCategory, 0x860bb310, 0x5d01, 0x11d0, 0xbd, 0x3b, 0x00, 0xa0, 0xc9, 0x11, 0xce, 0x86);
K(K_ICreateDevEnum, 0x29840822, 0x5b84, 0x11d0, 0xbd, 0x3b, 0x00, 0xa0, 0xc9, 0x11, 0xce, 0x86);
K(K_IPropertyBag, 0x55272a00, 0x42cb, 0x11ce, 0x81, 0x35, 0x00, 0xaa, 0x00, 0x4b, 0xb8, 0x51);
K(K_IBaseFilter, 0x56a86895, 0x0ad4, 0x11ce, 0xb0, 0x3a, 0x00, 0x20, 0xaf, 0x0b, 0xa7, 0x70);
K(K_FilterGraph, 0xe436ebb3, 0x524f, 0x11ce, 0x9f, 0x53, 0x00, 0x20, 0xaf, 0x0b, 0xa7, 0x70);
K(K_IGraphBuilder, 0x56a868a9, 0x0ad4, 0x11ce, 0xb0, 0x3a, 0x00, 0x20, 0xaf, 0x0b, 0xa7, 0x70);
K(K_CaptureGraphBuilder2, 0xbf87b6e1, 0x8c27, 0x11d0, 0xb3, 0xf0, 0x00, 0xaa, 0x00, 0x37, 0x61, 0xc5);
K(K_ICaptureGraphBuilder2, 0x93e5a4e0, 0x2d50, 0x11d2, 0xab, 0xfa, 0x00, 0xa0, 0xc9, 0xc6, 0xe3, 0x8d);
K(K_IMediaControl, 0x56a868b1, 0x0ad4, 0x11ce, 0xb0, 0x3a, 0x00, 0x20, 0xaf, 0x0b, 0xa7, 0x70);
K(K_IAMStreamConfig, 0xc6e13340, 0x30ac, 0x11d0, 0xa1, 0x8c, 0x00, 0xa0, 0xc9, 0x11, 0x89, 0x56);
K(K_SampleGrabber, 0xc1f400a0, 0x3f08, 0x11d3, 0x9f, 0x0b, 0x00, 0x60, 0x08, 0x03, 0x9e, 0x37);
K(K_ISampleGrabber, 0x6b652fff, 0x11fe, 0x4fce, 0x92, 0xad, 0x02, 0x66, 0xb5, 0xd7, 0xc7, 0x8f);
K(K_ISampleGrabberCB, 0x0579154a, 0x2b53, 0x4994, 0xb0, 0xd0, 0xe7, 0x73, 0x14, 0x8e, 0xff, 0x85);
K(K_NullRenderer, 0xc1f400a4, 0x3f08, 0x11d3, 0x9f, 0x0b, 0x00, 0x60, 0x08, 0x03, 0x9e, 0x37);
K(K_MEDIATYPE_Video, 0x73646976, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);
K(K_RGB32, 0xe436eb7e, 0x524f, 0x11ce, 0x9f, 0x53, 0x00, 0x20, 0xaf, 0x0b, 0xa7, 0x70);
K(K_RGB24, 0xe436eb7d, 0x524f, 0x11ce, 0x9f, 0x53, 0x00, 0x20, 0xaf, 0x0b, 0xa7, 0x70);
K(K_YUY2, 0x32595559, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);
K(K_NV12, 0x3231564e, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);
K(K_FORMAT_VideoInfo, 0x05589f80, 0xc356, 0x11ce, 0xbf, 0x01, 0x00, 0xaa, 0x00, 0x55, 0x59, 0x5a);
K(K_PIN_CATEGORY_CAPTURE, 0xfb6c4281, 0x0353, 0x11d1, 0x90, 0x5f, 0x00, 0x00, 0xc0, 0xcc, 0x16, 0xba);
#undef K

enum { DEVICES_MAX = 32, NAME_SIZE = 256 };

static struct {
    SRWLOCK lock;
    mkwmp_camera_log_fn log;
    /* what the caller wants (gen counts its changes) */
    char device[NAME_SIZE];
    int width, height, fps, mirror, want;
    uint32_t gen;
    /* the newest frame, width x height RGBA */
    uint8_t *frame;
    int frame_w, frame_h;
    uint32_t serial;
    int status;
    HANDLE wake, thread;
} G = {.lock = SRWLOCK_INIT};

/* What the streaming thread reads: set before the graph runs, constant while it does. */
static struct {
    int fmt, w, h, stride, bottom_up;
    int out_w, out_h, interval_ms;
    uint8_t *scratch;
    ULONGLONG last_ms;
} S;

static void say(const char *fmt, ...) {
    if (!G.log) return;
    char line[512];
    int n = snprintf(line, sizeof line, "camera: ");
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(line + n, sizeof line - (size_t)n, fmt, ap);
    va_end(ap);
    G.log(line);
}

/* ---- devices ---------------------------------------------------------------------------------- */

typedef struct {
    char id[NAME_SIZE];
    IMoniker *moniker;
} Device;

static void release_devices(Device *d, int n) {
    for (int i = 0; i < n; i++)
        if (d[i].moniker) IMoniker_Release(d[i].moniker);
}

/* The video input devices, in the system's order. COM must be initialised on the calling thread. */
static int list_devices(Device *out, int max) {
    ICreateDevEnum *dev_enum = NULL;
    IEnumMoniker *monikers = NULL;
    if (FAILED(CoCreateInstance(&K_SystemDeviceEnum, NULL, CLSCTX_INPROC_SERVER, &K_ICreateDevEnum, (void **)&dev_enum)))
        return 0;
    HRESULT hr = ICreateDevEnum_CreateClassEnumerator(dev_enum, &K_VideoInputDeviceCategory, &monikers, 0);
    ICreateDevEnum_Release(dev_enum);
    if (hr != S_OK || !monikers) return 0;   /* S_FALSE: the category is empty */
    int n = 0;
    IMoniker *m = NULL;
    while (n < max && IEnumMoniker_Next(monikers, 1, &m, NULL) == S_OK) {
        IPropertyBag *bag = NULL;
        if (SUCCEEDED(IMoniker_BindToStorage(m, NULL, NULL, &K_IPropertyBag, (void **)&bag))) {
            VARIANT v;
            VariantInit(&v);
            char name[NAME_SIZE - 8] = "";
            if (SUCCEEDED(IPropertyBag_Read(bag, L"FriendlyName", &v, NULL)) && v.vt == VT_BSTR && v.bstrVal)
                WideCharToMultiByte(CP_UTF8, 0, v.bstrVal, -1, name, sizeof name, NULL, NULL);
            name[sizeof name - 1] = 0;
            VariantClear(&v);
            IPropertyBag_Release(bag);
            if (name[0]) {
                /* a second device of the same name: "name (2)" */
                snprintf(out[n].id, sizeof out[n].id, "%s", name);
                for (int k = 2, again = 1; again; k++) {
                    again = 0;
                    for (int i = 0; i < n; i++)
                        if (!strcmp(out[i].id, out[n].id)) again = 1;
                    if (again) snprintf(out[n].id, sizeof out[n].id, "%s (%d)", name, k);
                }
                out[n++].moniker = m;
                m = NULL;
            }
        }
        if (m) IMoniker_Release(m);
        m = NULL;
    }
    IEnumMoniker_Release(monikers);
    return n;
}

static int json_put(char *buf, int cap, int at, const char *s, int quoted) {
    if (quoted && at < cap) buf[at] = '"';
    at += quoted;
    for (; *s; s++) {
        unsigned char c = (unsigned char)*s;
        if (quoted && (c == '"' || c == '\\')) {
            if (at < cap) buf[at] = '\\';
            at++;
        }
        if (at < cap) buf[at] = quoted && c < 0x20 ? ' ' : (char)c;
        at++;
    }
    if (quoted && at < cap) buf[at] = '"';
    return at + quoted;
}

MKWMP_CAMERA_API int mkwmp_camera_devices_json(char *buf, int cap) {
    if (!buf || cap < 1) return -1;
    HRESULT com = CoInitializeEx(NULL, COINIT_MULTITHREADED);   /* RPC_E_CHANGED_MODE: the thread has COM already */
    static Device d[DEVICES_MAX];
    static SRWLOCK one = SRWLOCK_INIT;
    AcquireSRWLockExclusive(&one);
    int n = list_devices(d, DEVICES_MAX);
    int at = json_put(buf, cap, 0, "{\"devices\":[", 0);
    for (int i = 0; i < n; i++) {
        at = json_put(buf, cap, at, i ? ",{\"id\":" : "{\"id\":", 0);
        at = json_put(buf, cap, at, d[i].id, 1);
        at = json_put(buf, cap, at, ",\"name\":", 0);
        at = json_put(buf, cap, at, d[i].id, 1);
        at = json_put(buf, cap, at, "}", 0);
    }
    at = json_put(buf, cap, at, "]}", 0);
    release_devices(d, n);
    memset(d, 0, sizeof d);
    ReleaseSRWLockExclusive(&one);
    if (SUCCEEDED(com)) CoUninitialize();
    if (at >= cap) return -1;
    buf[at] = 0;
    return at;
}

/* ---- the grabber's callback (DirectShow's streaming thread) ------------------------------------ */

static HRESULT STDMETHODCALLTYPE cb_query(ISampleGrabberCB *self, REFIID iid, void **out) {
    if (!out) return E_POINTER;
    if (IsEqualGUID(iid, &K_IUnknown) || IsEqualGUID(iid, &K_ISampleGrabberCB)) {
        *out = self;
        return S_OK;
    }
    *out = NULL;
    return E_NOINTERFACE;
}
static ULONG STDMETHODCALLTYPE cb_addref(ISampleGrabberCB *self) { (void)self; return 2; }   /* a static object */
static ULONG STDMETHODCALLTYPE cb_release(ISampleGrabberCB *self) { (void)self; return 1; }
static HRESULT STDMETHODCALLTYPE cb_sample(ISampleGrabberCB *self, double time, IMediaSample *sample) {
    (void)self, (void)time, (void)sample;
    return S_OK;
}
static HRESULT STDMETHODCALLTYPE cb_buffer(ISampleGrabberCB *self, double time, BYTE *data, LONG len) {
    (void)self, (void)time;
    if (!data || len <= 0 || !S.scratch) return S_OK;
    ULONGLONG now = GetTickCount64();
    if (S.last_ms && now - S.last_ms + 5 < (ULONGLONG)S.interval_ms) return S_OK;   /* faster than asked: dropped */
    AcquireSRWLockShared(&G.lock);
    int mirror = G.mirror;
    ReleaseSRWLockShared(&G.lock);
    if (!cam_convert(data, (size_t)len, S.fmt, S.w, S.h, S.stride, S.bottom_up, S.scratch, S.out_w, S.out_h, mirror))
        return S_OK;
    S.last_ms = now;
    AcquireSRWLockExclusive(&G.lock);
    if (G.frame && G.frame_w == S.out_w && G.frame_h == S.out_h) {
        memcpy(G.frame, S.scratch, (size_t)S.out_w * (size_t)S.out_h * 4);
        if (!++G.serial) G.serial = 1;
        if (G.status == MKWMP_CAMERA_STARTING) G.status = MKWMP_CAMERA_RUNNING;
    }
    ReleaseSRWLockExclusive(&G.lock);
    return S_OK;
}
static ISampleGrabberCBVtbl kCallbackVtbl = {cb_query, cb_addref, cb_release, cb_sample, cb_buffer};
static ISampleGrabberCB g_callback = {&kCallbackVtbl};

/* ---- the graph (the capture thread) ------------------------------------------------------------ */

typedef struct {
    IGraphBuilder *graph;
    ICaptureGraphBuilder2 *builder;
    IBaseFilter *source, *grabber_filter, *sink;
    ISampleGrabber *grabber;
    IMediaControl *control;
} Graph;

#define DROP(type, p) do { if (p) { type##_Release(p); (p) = NULL; } } while (0)

static void graph_close(Graph *g) {
    if (g->control) IMediaControl_Stop(g->control);
    if (g->grabber) ISampleGrabber_SetCallback(g->grabber, NULL, 1);
    DROP(IMediaControl, g->control);
    DROP(ISampleGrabber, g->grabber);
    DROP(IBaseFilter, g->sink);
    DROP(IBaseFilter, g->grabber_filter);
    DROP(IBaseFilter, g->source);
    DROP(ICaptureGraphBuilder2, g->builder);
    DROP(IGraphBuilder, g->graph);
}

static void free_media_type(AM_MEDIA_TYPE *mt, int whole) {
    if (!mt) return;
    if (mt->pbFormat) CoTaskMemFree(mt->pbFormat);
    if (mt->pUnk) IUnknown_Release(mt->pUnk);
    mt->pbFormat = NULL, mt->pUnk = NULL, mt->cbFormat = 0;
    if (whole) CoTaskMemFree(mt);
}

/* Ask the device for its smallest format that still covers the picture wanted (a 1080p stream scaled down to
 * 384x216 costs USB bandwidth and CPU for nothing). Not every source has the interface: the OBS Virtual Camera's
 * single format is then used as it is. */
static void pick_format(Graph *g, int want_w, int want_h) {
    IAMStreamConfig *config = NULL;
    if (FAILED(ICaptureGraphBuilder2_FindInterface(g->builder, &K_PIN_CATEGORY_CAPTURE, &K_MEDIATYPE_Video, g->source,
                                                   &K_IAMStreamConfig, (void **)&config)) || !config)
        return;
    int count = 0, size = 0;
    VIDEO_STREAM_CONFIG_CAPS caps;
    AM_MEDIA_TYPE *best = NULL;
    long best_area = 0;
    int best_covers = 0, best_w = 0, best_h = 0;
    if (SUCCEEDED(IAMStreamConfig_GetNumberOfCapabilities(config, &count, &size)) && size == (int)sizeof caps) {
        for (int i = 0; i < count; i++) {
            AM_MEDIA_TYPE *mt = NULL;
            if (FAILED(IAMStreamConfig_GetStreamCaps(config, i, &mt, (BYTE *)&caps)) || !mt) continue;
            int keep = 0;
            if (IsEqualGUID(&mt->formattype, &K_FORMAT_VideoInfo) && mt->cbFormat >= sizeof(VIDEOINFOHEADER) && mt->pbFormat) {
                const BITMAPINFOHEADER *bih = &((const VIDEOINFOHEADER *)mt->pbFormat)->bmiHeader;
                int w = bih->biWidth, h = bih->biHeight < 0 ? -bih->biHeight : bih->biHeight;
                long area = (long)w * h;
                int covers = w >= want_w && h >= want_h;
                /* a covering format beats one that does not; then the smallest covering one, or the largest other */
                if (w > 0 && h > 0 && (!best || covers > best_covers ||
                                       (covers == best_covers && (covers ? area < best_area : area > best_area))))
                    keep = 1, best_area = area, best_covers = covers, best_w = w, best_h = h;
            }
            if (keep) {
                free_media_type(best, 1);
                best = mt;
            } else {
                free_media_type(mt, 1);
            }
        }
    }
    if (best) {
        HRESULT hr = IAMStreamConfig_SetFormat(config, best);
        say("device format %dx%d %s", best_w, best_h, SUCCEEDED(hr) ? "chosen" : "refused (its default is used)");
        free_media_type(best, 1);
    }
    IAMStreamConfig_Release(config);
}

/* 1: the graph runs and S describes its pictures. */
static int graph_open(Graph *g, IMoniker *moniker, int fmt, int want_w, int want_h, int fps) {
    static const GUID *const kSubtype[] = {&K_RGB32, &K_RGB24, &K_YUY2, &K_NV12};
    memset(g, 0, sizeof *g);
    if (FAILED(IMoniker_BindToObject(moniker, NULL, NULL, &K_IBaseFilter, (void **)&g->source))) return 0;
    if (FAILED(CoCreateInstance(&K_FilterGraph, NULL, CLSCTX_INPROC_SERVER, &K_IGraphBuilder, (void **)&g->graph)) ||
        FAILED(CoCreateInstance(&K_CaptureGraphBuilder2, NULL, CLSCTX_INPROC_SERVER, &K_ICaptureGraphBuilder2, (void **)&g->builder)) ||
        FAILED(CoCreateInstance(&K_SampleGrabber, NULL, CLSCTX_INPROC_SERVER, &K_IBaseFilter, (void **)&g->grabber_filter)) ||
        FAILED(CoCreateInstance(&K_NullRenderer, NULL, CLSCTX_INPROC_SERVER, &K_IBaseFilter, (void **)&g->sink)) ||
        FAILED(IBaseFilter_QueryInterface(g->grabber_filter, &K_ISampleGrabber, (void **)&g->grabber)) ||
        FAILED(IGraphBuilder_QueryInterface(g->graph, &K_IMediaControl, (void **)&g->control)))
        return 0;
    AM_MEDIA_TYPE want;
    memset(&want, 0, sizeof want);
    want.majortype = K_MEDIATYPE_Video;
    want.subtype = *kSubtype[fmt];
    want.formattype = K_FORMAT_VideoInfo;
    if (FAILED(ICaptureGraphBuilder2_SetFiltergraph(g->builder, g->graph)) ||
        FAILED(IGraphBuilder_AddFilter(g->graph, g->source, L"camera")) ||
        FAILED(ISampleGrabber_SetMediaType(g->grabber, &want)) ||
        FAILED(IGraphBuilder_AddFilter(g->graph, g->grabber_filter, L"grabber")) ||
        FAILED(IGraphBuilder_AddFilter(g->graph, g->sink, L"sink")))
        return 0;
    pick_format(g, want_w, want_h);
    HRESULT hr = ICaptureGraphBuilder2_RenderStream(g->builder, &K_PIN_CATEGORY_CAPTURE, &K_MEDIATYPE_Video,
                                                    (IUnknown *)g->source, g->grabber_filter, g->sink);
    if (FAILED(hr))   /* a source without pin categories */
        hr = ICaptureGraphBuilder2_RenderStream(g->builder, NULL, &K_MEDIATYPE_Video, (IUnknown *)g->source,
                                                g->grabber_filter, g->sink);
    if (FAILED(hr)) return 0;
    AM_MEDIA_TYPE got;
    memset(&got, 0, sizeof got);
    if (FAILED(ISampleGrabber_GetConnectedMediaType(g->grabber, &got))) return 0;
    int ok = IsEqualGUID(&got.formattype, &K_FORMAT_VideoInfo) && got.cbFormat >= sizeof(VIDEOINFOHEADER) && got.pbFormat &&
             IsEqualGUID(&got.subtype, kSubtype[fmt]);
    if (ok) {
        const BITMAPINFOHEADER *bih = &((const VIDEOINFOHEADER *)got.pbFormat)->bmiHeader;
        S.fmt = fmt;
        S.w = bih->biWidth;
        S.h = bih->biHeight < 0 ? -bih->biHeight : bih->biHeight;
        S.bottom_up = (fmt == CAMFMT_BGRA || fmt == CAMFMT_BGR24) && bih->biHeight > 0;   /* a DIB's last row first */
        S.stride = cam_stride(fmt, S.w);
        ok = S.w >= 2 && S.h >= 2;
    }
    free_media_type(&got, 0);
    if (!ok) return 0;
    S.out_w = want_w, S.out_h = want_h;
    S.interval_ms = 1000 / (fps < 1 ? 1 : fps > 60 ? 60 : fps);
    S.last_ms = 0;
    free(S.scratch);
    S.scratch = malloc((size_t)want_w * (size_t)want_h * 4);
    if (!S.scratch) return 0;
    ISampleGrabber_SetBufferSamples(g->grabber, FALSE);
    ISampleGrabber_SetOneShot(g->grabber, FALSE);
    if (FAILED(ISampleGrabber_SetCallback(g->grabber, &g_callback, 1))) return 0;   /* 1: BufferCB */
    return SUCCEEDED(IMediaControl_Run(g->control));
}

static void set_status(int status) {
    AcquireSRWLockExclusive(&G.lock);
    G.status = status;
    ReleaseSRWLockExclusive(&G.lock);
}

static int open_device(Graph *g, const char *id, int w, int h, int fps) {
    static const char *const kName[] = {"RGB32", "RGB24", "YUY2", "NV12"};
    static Device d[DEVICES_MAX];
    int n = list_devices(d, DEVICES_MAX), at = -1;
    for (int i = 0; i < n && at < 0; i++)
        if (!id[0] || !strcmp(d[i].id, id)) at = i;
    int ok = 0;
    if (at < 0) {
        say("no device named \"%s\" (%d found)", id, n);
    } else {
        for (int fmt = CAMFMT_BGRA; fmt <= CAMFMT_NV12 && !ok; fmt++) {
            ok = graph_open(g, d[at].moniker, fmt, w, h, fps);
            if (ok) say("\"%s\" opened: %dx%d %s -> %dx%d RGBA, at most %d fps", d[at].id, S.w, S.h, kName[fmt], w, h, fps);
            else graph_close(g);
        }
        if (!ok) say("\"%s\" could not be opened (in use by another program, or no format this library reads)", d[at].id);
    }
    release_devices(d, n);
    memset(d, 0, sizeof d);
    return ok;
}

static DWORD WINAPI capture_thread(void *unused) {
    (void)unused;
    HRESULT com = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    if (FAILED(com)) say("COM could not be initialised (%#lx)", (unsigned long)com);
    Graph graph;
    memset(&graph, 0, sizeof graph);
    uint32_t applied = 0;
    int open = 0;
    for (;;) {
        WaitForSingleObject(G.wake, 500);
        AcquireSRWLockExclusive(&G.lock);
        uint32_t gen = G.gen;
        int want = G.want, w = G.width, h = G.height, fps = G.fps;
        char device[NAME_SIZE];
        memcpy(device, G.device, sizeof device);
        ReleaseSRWLockExclusive(&G.lock);
        if (gen == applied) continue;
        applied = gen;
        if (open) {
            graph_close(&graph);
            open = 0;
            say("capture stopped");
        }
        if (!want || FAILED(com)) {
            set_status(want ? MKWMP_CAMERA_FAILED : MKWMP_CAMERA_IDLE);
            continue;
        }
        AcquireSRWLockExclusive(&G.lock);
        if (G.frame_w != w || G.frame_h != h) {
            free(G.frame);
            G.frame = calloc((size_t)w * (size_t)h, 4);
            G.frame_w = w, G.frame_h = h;
        }
        int have = G.frame != NULL;
        G.status = MKWMP_CAMERA_STARTING;
        ReleaseSRWLockExclusive(&G.lock);
        open = have && open_device(&graph, device, w, h, fps);
        if (!open) {
            AcquireSRWLockExclusive(&G.lock);
            if (G.gen == gen) G.status = MKWMP_CAMERA_FAILED;
            ReleaseSRWLockExclusive(&G.lock);
        }
    }
    return 0;
}

/* ---- the ABI ------------------------------------------------------------------------------------ */

MKWMP_CAMERA_API int mkwmp_camera_abi(void) { return MKWMP_CAMERA_ABI; }

MKWMP_CAMERA_API void mkwmp_camera_init(mkwmp_camera_log_fn log) {
    AcquireSRWLockExclusive(&G.lock);
    G.log = log;
    ReleaseSRWLockExclusive(&G.lock);
}

static void changed(void) {   /* G.lock held */
    G.gen++;
    if (!G.wake) G.wake = CreateEventW(NULL, FALSE, FALSE, NULL);
    if (!G.thread) G.thread = CreateThread(NULL, 0, capture_thread, NULL, 0, NULL);
    if (G.wake) SetEvent(G.wake);
}

MKWMP_CAMERA_API void mkwmp_camera_start(const char *device_id, int width, int height, int fps, int mirror) {
    if (!device_id) device_id = "";
    if (width < 16 || height < 16 || width > 1920 || height > 1080) return;
    AcquireSRWLockExclusive(&G.lock);
    G.mirror = mirror != 0;
    if (!G.want || strcmp(G.device, device_id) || G.width != width || G.height != height || G.fps != fps) {
        snprintf(G.device, sizeof G.device, "%s", device_id);
        G.width = width, G.height = height, G.fps = fps, G.want = 1;
        G.status = MKWMP_CAMERA_STARTING;
        changed();
    }
    ReleaseSRWLockExclusive(&G.lock);
}

MKWMP_CAMERA_API void mkwmp_camera_set_mirror(int mirror) {
    AcquireSRWLockExclusive(&G.lock);
    G.mirror = mirror != 0;
    ReleaseSRWLockExclusive(&G.lock);
}

MKWMP_CAMERA_API void mkwmp_camera_stop(void) {
    AcquireSRWLockExclusive(&G.lock);
    if (G.want) {
        G.want = 0;
        G.status = MKWMP_CAMERA_IDLE;
        changed();
    }
    ReleaseSRWLockExclusive(&G.lock);
}

MKWMP_CAMERA_API int mkwmp_camera_frame(uint8_t *rgba, int cap, uint32_t *serial) {
    if (!rgba || !serial) return 0;
    int got = 0;
    AcquireSRWLockShared(&G.lock);
    if (G.want && G.frame && G.serial && G.serial != *serial && (int64_t)cap >= (int64_t)G.frame_w * G.frame_h * 4) {
        memcpy(rgba, G.frame, (size_t)G.frame_w * (size_t)G.frame_h * 4);
        *serial = G.serial;
        got = 1;
    }
    ReleaseSRWLockShared(&G.lock);
    return got;
}

MKWMP_CAMERA_API int mkwmp_camera_status(void) {
    AcquireSRWLockShared(&G.lock);
    int s = G.status;
    ReleaseSRWLockShared(&G.lock);
    return s;
}
