/*
 * KaM TSK Win10/11 Fix 1.33 - by Daniel Airich
 * winmm.dll proxy: emulates the MCI "cdaudio" device with audio files (Music\TrackNN.wav / .mp3).
 * Everything that is not "cdaudio" is forwarded to the real system winmm.dll.
 * Own code, no third-party code. Build: tcc -shared (32-bit).
 *
 * Game usage (reverse engineered): OPEN "cdaudio", SET time format TMSF, STATUS number of tracks / track length,
 * PLAY from track N to N+1 with NOTIFY (window gets MM_MCINOTIFY when done), STOP, CLOSE.
 */
#include <windows.h>
#include <stdarg.h>
#include <stdlib.h>
#include <stdio.h>
#include <string.h>

#define MCI_OPEN        0x0803
#define MCI_CLOSE       0x0804
#define MCI_PLAY        0x0806
#define MCI_STOP        0x0808
#define MCI_PAUSE       0x0809
#define MCI_SET         0x080D
#define MCI_STATUS      0x0814
#define MCI_RESUME      0x0855
#define MCI_NOTIFY      0x0001
#define MCI_FROM        0x0004
#define MCI_TRACK       0x0010
#define MCI_STATUS_ITEM 0x0100
#define MCI_OPEN_TYPE   0x2000
#define MCI_OPEN_TYPE_ID 0x1000
#define MCI_STATUS_LENGTH 1
#define MCI_STATUS_POSITION 2
#define MCI_STATUS_NUMBER_OF_TRACKS 3
#define MCI_STATUS_MODE 4
#define MCI_STATUS_MEDIA_PRESENT 5
#define MCI_STATUS_TIME_FORMAT 6
#define MCI_STATUS_READY 7
#define MCI_STATUS_CURRENT_TRACK 8
#define MCI_MODE_STOP   525
#define MCI_MODE_PLAY   526
#define MCI_NOTIFY_SUCCESSFUL 1
#define MCI_NOTIFY_SUPERSEDED 2
#define MCI_NOTIFY_ABORTED    4
#define MM_MCINOTIFY    0x3B9
#define MCIERR_OUTOFRANGE 282
#define FAKE_ID         0x0CDA
#define MAXT            100

typedef struct { DWORD_PTR dwCallback; UINT wDeviceID; LPCSTR lpstrDeviceType; LPCSTR lpstrElementName; LPCSTR lpstrAlias; } OPENPARMS;
typedef struct { DWORD_PTR dwCallback; DWORD_PTR dwReturn; DWORD dwItem; DWORD dwTrack; } STATUSPARMS;
typedef struct { DWORD_PTR dwCallback; DWORD dwFrom; DWORD dwTo; } PLAYPARMS;

typedef DWORD (WINAPI *F_cmd)(UINT, UINT, DWORD_PTR, DWORD_PTR);
typedef BOOL  (WINAPI *F_err)(DWORD, LPSTR, UINT);
typedef DWORD (WINAPI *F_str)(LPCSTR, LPSTR, UINT, HWND);
typedef UINT  (WINAPI *F_devid)(LPCSTR);
typedef UINT  (WINAPI *F_mnum)(void);
typedef UINT  (WINAPI *F_mclose)(void *);
typedef UINT  (WINAPI *F_mopen)(void *, UINT, DWORD_PTR, DWORD_PTR, DWORD);
typedef UINT  (WINAPI *F_m3)(void *, void *, DWORD);

static F_cmd r_cmd; static F_err r_err; static F_str r_str; static F_devid r_devid;
static F_mnum r_mnum; static F_mclose r_mclose; static F_mopen r_mopen;
static F_m3 r_mlineinfo, r_mlinectl, r_mgetdet, r_msetdet;

static CRITICAL_SECTION cs, csReq;
static volatile LONG inited = 0;
static char dirMusic[MAX_PATH];
static char trackFile[MAXT][MAX_PATH];
static int  trackIsMp3[MAXT];
static DWORD trackLenMs[MAXT];
static int  maxTrack = 0, haveMusic = 0, fakeOpen = 0;
static int  playing = 0, curTrack = 0, pendingNotify = 0;
static HWND notifyWnd = NULL;
static char curAlias[40];
static LONG aliasCounter = 0;

static int  debugLog = 0;
static char logPath[MAX_PATH];

static void LOGF(const char *fmt, ...)
{
    FILE *f; va_list ap;
    if (!debugLog) return;
    f = fopen(logPath, "a");
    if (!f) return;
    va_start(ap, fmt);
    vfprintf(f, fmt, ap);
    va_end(ap);
    fputc('\n', f);
    fclose(f);
}



static DWORD WavLengthMs(const char *path)
{
    FILE *f = fopen(path, "rb");
    unsigned char h[12], c[8];
    unsigned long byteRate = 0, dataLen = 0;
    if (!f) return 0;
    if (fread(h, 1, 12, f) != 12 || memcmp(h, "RIFF", 4) || memcmp(h + 8, "WAVE", 4)) { fclose(f); return 0; }
    while (fread(c, 1, 8, f) == 8) {
        unsigned long sz = c[4] | (c[5] << 8) | (c[6] << 16) | ((unsigned long)c[7] << 24);
        if (!memcmp(c, "fmt ", 4)) {
            unsigned char fm[16];
            if (sz >= 16 && fread(fm, 1, 16, f) == 16) { byteRate = fm[8] | (fm[9] << 8) | (fm[10] << 16) | ((unsigned long)fm[11] << 24); fseek(f, (long)(sz - 16 + (sz & 1)), SEEK_CUR); }
            else break;
        } else if (!memcmp(c, "data", 4)) { dataLen = sz; break; }
        else fseek(f, (long)(sz + (sz & 1)), SEEK_CUR);
    }
    fclose(f);
    if (!byteRate) return 0;
    return (DWORD)(((unsigned __int64)dataLen * 1000) / byteRate);
}

static void InitOnce(void)
{
    char sysdir[MAX_PATH], real[MAX_PATH], self[MAX_PATH], *sl;
    HMODULE hm = NULL, hr;
    int n;
    if (inited) return;
    EnterCriticalSection(&cs);
    if (inited) { LeaveCriticalSection(&cs); return; }

    GetSystemDirectoryA(sysdir, MAX_PATH);
    _snprintf(real, MAX_PATH, "%s\\winmm.dll", sysdir);
    hr = LoadLibraryA(real);
    if (hr) {
        r_cmd = (F_cmd)GetProcAddress(hr, "mciSendCommandA");
        r_err = (F_err)GetProcAddress(hr, "mciGetErrorStringA");
        r_str = (F_str)GetProcAddress(hr, "mciSendStringA");
        r_devid = (F_devid)GetProcAddress(hr, "mciGetDeviceIDA");
        r_mnum = (F_mnum)GetProcAddress(hr, "mixerGetNumDevs");
        r_mclose = (F_mclose)GetProcAddress(hr, "mixerClose");
        r_mopen = (F_mopen)GetProcAddress(hr, "mixerOpen");
        r_mlineinfo = (F_m3)GetProcAddress(hr, "mixerGetLineInfoA");
        r_mlinectl = (F_m3)GetProcAddress(hr, "mixerGetLineControlsA");
        r_mgetdet = (F_m3)GetProcAddress(hr, "mixerGetControlDetailsA");
        r_msetdet = (F_m3)GetProcAddress(hr, "mixerSetControlDetails");
    }

    GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCSTR)&InitOnce, &hm);
    GetModuleFileNameA(hm, self, MAX_PATH);
    sl = strrchr(self, '\\'); if (sl) *sl = 0;
    _snprintf(dirMusic, MAX_PATH, "%s\\Music", self);
    _snprintf(logPath, MAX_PATH, "%s\\winmm_cd.log", dirMusic);
    {
        char flag[MAX_PATH];
        _snprintf(flag, MAX_PATH, "%s\\debug.flag", dirMusic);
        debugLog = (GetFileAttributesA(flag) != INVALID_FILE_ATTRIBUTES);
    }
    for (n = 2; n < MAXT; n++) {
        char p[MAX_PATH];
        _snprintf(p, MAX_PATH, "%s\\Track%02d.wav", dirMusic, n);
        if (GetFileAttributesA(p) != INVALID_FILE_ATTRIBUTES) { strcpy(trackFile[n], p); trackIsMp3[n] = 0; }
        else {
            _snprintf(p, MAX_PATH, "%s\\Track%02d.mp3", dirMusic, n);
            if (GetFileAttributesA(p) != INVALID_FILE_ATTRIBUTES) { strcpy(trackFile[n], p); trackIsMp3[n] = 1; }
            else continue;
        }
        if (n > maxTrack) maxTrack = n;
        haveMusic++;
        if (!trackIsMp3[n]) trackLenMs[n] = WavLengthMs(trackFile[n]);
    }
    LOGF("init: real winmm=%p cmd=%p tracks=%d max=%d dir=%s", (void *)hr, (void *)r_cmd, haveMusic, maxTrack, dirMusic);
    inited = 1;
    LeaveCriticalSection(&cs);
}

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID r)
{
    if (reason == DLL_PROCESS_ATTACH) { InitializeCriticalSection(&cs); InitializeCriticalSection(&csReq); DisableThreadLibraryCalls(h); }
    return TRUE;
}

/* ---- playback via the real MCI (waveaudio / mpegvideo) ----
 * MCI devices belong to the thread that opened them, so ALL real MCI calls run on one worker thread.
 * The exported functions only post requests to it. */
enum { OP_PLAY = 1, OP_STOP = 2, OP_PAUSE = 3, OP_RESUME = 4, OP_LEN = 5 };
typedef struct { int op; int track; HWND wnd; int notify; DWORD code; DWORD result; HANDLE done; } REQ;
static CRITICAL_SECTION csReq;
static HANDLE hReqEvent = NULL;
static REQ *volatile pendingReq = NULL;

static void PostNotify(DWORD code)
{
    if (pendingNotify && notifyWnd) {
        LOGF("notify code=%lu", code);
        PostMessageA(notifyWnd, MM_MCINOTIFY, (WPARAM)code, (LPARAM)FAKE_ID);
    }
    pendingNotify = 0;
}

static void StopTrackW(DWORD notifyCode)   /* worker thread only */
{
    char buf[128];
    EnterCriticalSection(&cs);
    if (playing) {
        _snprintf(buf, sizeof(buf), "stop %s", curAlias);  if (r_str) r_str(buf, NULL, 0, NULL);
        _snprintf(buf, sizeof(buf), "close %s", curAlias); if (r_str) r_str(buf, NULL, 0, NULL);
        playing = 0;
        LOGF("stopped track %d", curTrack);
    }
    if (notifyCode) PostNotify(notifyCode); else pendingNotify = 0;
    LeaveCriticalSection(&cs);
}

static DWORD StartTrackW(int t, HWND cb, int wantNotify)   /* worker thread only */
{
    char buf[MAX_PATH + 96], alias[40];
    DWORD e;
    if (t < 2 || t >= MAXT || !trackFile[t][0]) return MCIERR_OUTOFRANGE;
    StopTrackW(pendingNotify ? MCI_NOTIFY_SUPERSEDED : 0);
    _snprintf(alias, sizeof(alias), "kamcd%ld", (long)InterlockedIncrement(&aliasCounter));
    _snprintf(buf, sizeof(buf), "open \"%s\" type %s alias %s", trackFile[t], trackIsMp3[t] ? "mpegvideo" : "waveaudio", alias);
    e = r_str ? r_str(buf, NULL, 0, NULL) : 1;
    if (e) { LOGF("open failed %lu for %s", e, trackFile[t]); return e; }
    _snprintf(buf, sizeof(buf), "play %s", alias);
    e = r_str(buf, NULL, 0, NULL);
    if (e) { _snprintf(buf, sizeof(buf), "close %s", alias); r_str(buf, NULL, 0, NULL); LOGF("play failed %lu", e); return e; }
    EnterCriticalSection(&cs);
    strcpy(curAlias, alias); playing = 1; curTrack = t; notifyWnd = cb; pendingNotify = wantNotify ? 1 : 0;
    LeaveCriticalSection(&cs);
    LOGF("play track %d notify=%d wnd=%p alias=%s", t, wantNotify, (void *)cb, alias);
    return 0;
}

static DWORD LenMp3W(int t)   /* worker thread only */
{
    char buf[MAX_PATH + 96], ret[64];
    DWORD ms = 0;
    _snprintf(buf, sizeof(buf), "open \"%s\" type mpegvideo alias kamlen", trackFile[t]);
    if (r_str && r_str(buf, NULL, 0, NULL) == 0) {
        r_str("set kamlen time format milliseconds", NULL, 0, NULL);
        if (r_str("status kamlen length", ret, sizeof(ret), NULL) == 0) ms = (DWORD)atol(ret);
        r_str("close kamlen", NULL, 0, NULL);
    }
    return ms;
}

static void PollW(void)   /* worker thread only: detect natural end of a track */
{
    EnterCriticalSection(&cs);
    if (playing && r_devid && r_cmd) {
        UINT id = r_devid(curAlias);
        STATUSPARMS sp;
        memset(&sp, 0, sizeof(sp));
        sp.dwItem = MCI_STATUS_MODE;
        if (id == 0 || r_cmd(id, MCI_STATUS, MCI_STATUS_ITEM, (DWORD_PTR)&sp) != 0 || sp.dwReturn == MCI_MODE_STOP) {
            char buf[96];
            LOGF("track %d finished", curTrack);
            _snprintf(buf, sizeof(buf), "close %s", curAlias); if (r_str) r_str(buf, NULL, 0, NULL);
            playing = 0;
            PostNotify(MCI_NOTIFY_SUCCESSFUL);
        }
    }
    LeaveCriticalSection(&cs);
}

static DWORD WINAPI Worker(LPVOID p)
{
    for (;;) {
        DWORD w = WaitForSingleObject(hReqEvent, 40);
        if (w == WAIT_OBJECT_0) {
            REQ *r = pendingReq;
            if (r) {
                char b[96];
                switch (r->op) {
                case OP_PLAY:   r->result = StartTrackW(r->track, r->wnd, r->notify); break;
                case OP_STOP:   StopTrackW(r->code); r->result = 0; break;
                case OP_PAUSE:  if (playing && r_str) { _snprintf(b, sizeof(b), "pause %s", curAlias);  r_str(b, NULL, 0, NULL); } r->result = 0; break;
                case OP_RESUME: if (playing && r_str) { _snprintf(b, sizeof(b), "resume %s", curAlias); r_str(b, NULL, 0, NULL); } r->result = 0; break;
                case OP_LEN:    r->result = LenMp3W(r->track); break;
                }
                SetEvent(r->done);
            }
        }
        PollW();
    }
    return 0;
}

static DWORD RunOnWorker(int op, int track, HWND wnd, int notify, DWORD code)
{
    REQ r;
    DWORD res;
    EnterCriticalSection(&csReq);
    if (!hReqEvent) {
        hReqEvent = CreateEventA(NULL, FALSE, FALSE, NULL);
        CreateThread(NULL, 0, Worker, NULL, 0, NULL);
    }
    memset(&r, 0, sizeof(r));
    r.op = op; r.track = track; r.wnd = wnd; r.notify = notify; r.code = code;
    r.done = CreateEventA(NULL, FALSE, FALSE, NULL);
    pendingReq = &r;
    SetEvent(hReqEvent);
    if (WaitForSingleObject(r.done, 8000) != WAIT_OBJECT_0) r.result = 277;
    res = r.result;
    pendingReq = NULL;
    CloseHandle(r.done);
    LeaveCriticalSection(&csReq);
    return res;
}

static DWORD TrackLenMs(int t)
{
    if (t < 2 || t >= MAXT || !trackFile[t][0]) return 0;
    if (!trackLenMs[t] && trackIsMp3[t]) trackLenMs[t] = RunOnWorker(OP_LEN, t, NULL, 0, 0);
    return trackLenMs[t];
}

static DWORD MakeTmsfLen(DWORD ms)
{
    DWORD s = ms / 1000, f = ((ms % 1000) * 75) / 1000;
    return (s / 60) | ((s % 60) << 8) | (f << 16);
}
/* ---- exported API ---- */
__declspec(dllexport) DWORD WINAPI mciSendCommandA(UINT id, UINT msg, DWORD_PTR flags, DWORD_PTR param)
{
    InitOnce();
    if (msg == MCI_OPEN && (flags & MCI_OPEN_TYPE) && !(flags & MCI_OPEN_TYPE_ID) && param) {
        OPENPARMS *op = (OPENPARMS *)param;
        if (haveMusic && op->lpstrDeviceType && lstrcmpiA(op->lpstrDeviceType, "cdaudio") == 0) {
            fakeOpen = 1;
            op->wDeviceID = FAKE_ID;
            LOGF("OPEN cdaudio -> fake device");
            return 0;
        }
    }
    if (id == FAKE_ID && fakeOpen) {
        DWORD r = 0;
        switch (msg) {
        case MCI_CLOSE:
            RunOnWorker(OP_STOP, 0, NULL, 0, MCI_NOTIFY_ABORTED); fakeOpen = 0; LOGF("CLOSE"); break;
        case MCI_SET:
            break;
        case MCI_STOP:
            RunOnWorker(OP_STOP, 0, NULL, 0, MCI_NOTIFY_ABORTED); LOGF("STOP"); break;
        case MCI_PAUSE:
            RunOnWorker(OP_PAUSE, 0, NULL, 0, 0); break;
        case MCI_RESUME:
            RunOnWorker(OP_RESUME, 0, NULL, 0, 0); break;
        case MCI_PLAY: {
            PLAYPARMS *pp = (PLAYPARMS *)param;
            int t = (flags & MCI_FROM) ? (int)(pp->dwFrom & 0xFF) : 2;
            if (t < 2) t = 2;
            r = RunOnWorker(OP_PLAY, t, (flags & MCI_NOTIFY) ? (HWND)pp->dwCallback : NULL, (flags & MCI_NOTIFY) ? 1 : 0, 0);
            break; }
        case MCI_STATUS: {
            STATUSPARMS *sp = (STATUSPARMS *)param;
            sp->dwReturn = 0;
            if (flags & MCI_STATUS_ITEM) {
                switch (sp->dwItem) {
                case MCI_STATUS_NUMBER_OF_TRACKS: sp->dwReturn = (DWORD_PTR)(maxTrack >= 2 ? maxTrack : 2); break;
                case MCI_STATUS_LENGTH:
                    if (flags & MCI_TRACK) {
                        if (sp->dwTrack == 1) sp->dwReturn = MakeTmsfLen(1000);
                        else if (sp->dwTrack >= 2 && sp->dwTrack < MAXT && trackFile[sp->dwTrack][0]) sp->dwReturn = MakeTmsfLen(TrackLenMs((int)sp->dwTrack));
                        else r = MCIERR_OUTOFRANGE;
                    } break;
                case MCI_STATUS_MODE: EnterCriticalSection(&cs); sp->dwReturn = playing ? MCI_MODE_PLAY : MCI_MODE_STOP; LeaveCriticalSection(&cs); break;
                case MCI_STATUS_MEDIA_PRESENT: case MCI_STATUS_READY: sp->dwReturn = 1; break;
                case MCI_STATUS_TIME_FORMAT: sp->dwReturn = 10; break;
                case MCI_STATUS_CURRENT_TRACK: EnterCriticalSection(&cs); sp->dwReturn = playing ? curTrack : 2; LeaveCriticalSection(&cs); break;
                case MCI_STATUS_POSITION: sp->dwReturn = 0; break;
                default: break;
                }
            }
            break; }
        default:
            break;
        }
        return r;
    }
    return r_cmd ? r_cmd(id, msg, flags, param) : 263;
}

__declspec(dllexport) BOOL WINAPI mciGetErrorStringA(DWORD err, LPSTR buf, UINT len)
{
    InitOnce();
    if (r_err) return r_err(err, buf, len);
    if (buf && len) buf[0] = 0;
    return FALSE;
}

__declspec(dllexport) DWORD WINAPI mciSendStringA(LPCSTR cmd, LPSTR ret, UINT len, HWND cb)
{
    InitOnce();
    return r_str ? r_str(cmd, ret, len, cb) : 263;
}

__declspec(dllexport) UINT WINAPI mixerGetNumDevs(void)               { InitOnce(); return r_mnum ? r_mnum() : 0; }
__declspec(dllexport) UINT WINAPI mixerClose(void *h)                 { InitOnce(); return r_mclose ? r_mclose(h) : 6; }
__declspec(dllexport) UINT WINAPI mixerOpen(void *p, UINT i, DWORD_PTR c, DWORD_PTR n, DWORD f) { InitOnce(); return r_mopen ? r_mopen(p, i, c, n, f) : 6; }
__declspec(dllexport) UINT WINAPI mixerGetLineInfoA(void *h, void *p, DWORD f)       { InitOnce(); return r_mlineinfo ? r_mlineinfo(h, p, f) : 6; }
__declspec(dllexport) UINT WINAPI mixerGetLineControlsA(void *h, void *p, DWORD f)   { InitOnce(); return r_mlinectl ? r_mlinectl(h, p, f) : 6; }
__declspec(dllexport) UINT WINAPI mixerGetControlDetailsA(void *h, void *p, DWORD f) { InitOnce(); return r_mgetdet ? r_mgetdet(h, p, f) : 6; }
__declspec(dllexport) UINT WINAPI mixerSetControlDetails(void *h, void *p, DWORD f)  { InitOnce(); return r_msetdet ? r_msetdet(h, p, f) : 6; }







