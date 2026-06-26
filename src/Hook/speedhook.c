/*
 * Fast speedhack - faithful port of Cheat Engine's speedhackmain.pas
 *
 * CE algorithm:
 *   result = trunc((currentRealTime - initialRealTime) * speedmultiplier) + initialFakeOffset
 *
 * On speed change, re-anchor:
 *   initialFakeOffset = currentFakeTime  (what the game currently sees)
 *   initialRealTime   = currentRealTime  (actual wall clock)
 *   speedmultiplier   = newSpeed
 *
 * Hook targets (CE priority order):
 *   GTC:   kernelbase!GetTickCount -> kernel32!GetTickCount
 *   GTC64: kernelbase!GetTickCount64 -> kernel32!GetTickCount64
 *   QPC:   ntdll!RtlQueryPerformanceCounter -> kernel32!QueryPerformanceCounter
 *   TGT:   winmm!timeGetTime (redirects to GTC hook logic, same as CE)
 *
 * Only ONE hook per timing path. Never hook both QPC and NtQPC.
 */

#include "speedhook.h"
#include "shared.h"
#include "minhook/include/MinHook.h"
#include <timeapi.h>
#include <math.h>
#include <stdio.h>
#include <stdarg.h>

static volatile FastShared *g_shared = NULL;

typedef struct {
    const char *moduleName;
    DWORD branchRva;
    DWORD rateIntRva;
    DWORD rateDoubleRva;
    DWORD intervalRva;
} FF8PatchSpec;

static const FF8PatchSpec g_ff8Specs[] = {
    { "FFVIII_EFIGS.dll", 0x15635EB, 0x1781D58, 0x1781D70, 0x1781D78 },
    { "FFVIII_JP.dll",    0x1599CCB, 0x1793878, 0x1793890, 0x1793898 },
};

static HANDLE g_ff8Thread = NULL;
static volatile LONG g_ff8Stop = 0;

static void fast_log(const char *fmt, ...)
{
    char tempPath[MAX_PATH];
    char logPath[MAX_PATH];
    FILE *fp;
    SYSTEMTIME st;
    va_list args;

    if (!GetTempPathA(MAX_PATH, tempPath))
        return;

    snprintf(logPath, sizeof(logPath), "%sFastHook-%lu.log", tempPath, GetCurrentProcessId());

    fp = fopen(logPath, "a");
    if (!fp)
        return;

    GetLocalTime(&st);
    fprintf(fp, "[%04u-%02u-%02u %02u:%02u:%02u.%03u] ",
        st.wYear, st.wMonth, st.wDay,
        st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);

    va_start(args, fmt);
    vfprintf(fp, fmt, args);
    va_end(args);

    fputc('\n', fp);
    fclose(fp);
}

static int write_memory(void *address, const void *buffer, SIZE_T size)
{
    DWORD oldProtect;
    if (!VirtualProtect(address, size, PAGE_EXECUTE_READWRITE, &oldProtect))
        return 0;

    memcpy(address, buffer, size);
    FlushInstructionCache(GetCurrentProcess(), address, size);

    DWORD ignored;
    VirtualProtect(address, size, oldProtect, &ignored);
    return 1;
}

static DWORD WINAPI FF8TurboThread(LPVOID unused)
{
    const FF8PatchSpec *spec = NULL;
    HMODULE module = NULL;
    BYTE *branchPtr = NULL;
    DWORD *rateIntPtr = NULL;
    double *rateDoublePtr = NULL;
    double *intervalPtr = NULL;
    double baseRateDouble = 0.0;
    DWORD baseRateInt = 0;
    double baseInterval = 0.0;
    int intervalReady = 0;
    BYTE originalBranch[2] = { 0x76, 0x52 };

    (void)unused;

    for (;;) {
        if (InterlockedCompareExchange(&g_ff8Stop, 0, 0) != 0)
            return 0;

        if (!spec) {
            for (int i = 0; i < (int)(sizeof(g_ff8Specs) / sizeof(g_ff8Specs[0])); i++) {
                module = GetModuleHandleA(g_ff8Specs[i].moduleName);
                if (module) {
                    spec = &g_ff8Specs[i];
                    branchPtr = (BYTE*)module + spec->branchRva;
                    rateIntPtr = (DWORD*)((BYTE*)module + spec->rateIntRva);
                    rateDoublePtr = (double*)((BYTE*)module + spec->rateDoubleRva);
                    intervalPtr = (double*)((BYTE*)module + spec->intervalRva);
                    fast_log("FF8 module found: %s base=%p branch=%p rateInt=%p rateDouble=%p interval=%p",
                        spec->moduleName, module, branchPtr, rateIntPtr, rateDoublePtr, intervalPtr);
                    break;
                }
            }

            if (!spec) {
                Sleep(1000);
                continue;
            }
        }

        if (!intervalReady) {
            if (!IsBadReadPtr(intervalPtr, sizeof(*intervalPtr)) &&
                !IsBadReadPtr(rateDoublePtr, sizeof(*rateDoublePtr)) &&
                !IsBadReadPtr(rateIntPtr, sizeof(*rateIntPtr))) {
                double current = *intervalPtr;
                double currentRate = *rateDoublePtr;
                if (current > 0.0)
                    baseInterval = current;
                else
                    baseInterval = 1.0 / 30.0;
                if (currentRate > 0.0)
                    baseRateDouble = currentRate;
                else
                    baseRateDouble = 30.0;
                baseRateInt = *rateIntPtr ? *rateIntPtr : (DWORD)(baseRateDouble + 0.5);
                intervalReady = 1;
                fast_log("FF8 timing initialized: rateInt=%lu rateDouble=%.9f interval=%.9f",
                    (unsigned long)baseRateInt, baseRateDouble, baseInterval);
            } else {
                Sleep(250);
                continue;
            }
        }

        double speed = 1.0;
        if (g_shared && g_shared->enabled && g_shared->speed > 0.0)
            speed = g_shared->speed;

        if (speed < 1.0)
            speed = 1.0;
        if (speed > 1000.0)
            speed = 1000.0;

        if (branchPtr[0] != originalBranch[0] || branchPtr[1] != originalBranch[1]) {
            fast_log("Restoring FF8 wait branch from %02X %02X to %02X %02X",
                branchPtr[0], branchPtr[1], originalBranch[0], originalBranch[1]);
            write_memory(branchPtr, originalBranch, sizeof(originalBranch));
        }

        double desiredInterval = baseInterval / speed;
        double desiredRateDouble = baseRateDouble * speed;
        DWORD desiredRateInt = (DWORD)(desiredRateDouble + 0.5);
        if (desiredInterval < 0.000001)
            desiredInterval = 0.000001;
        if (desiredRateDouble < 1.0)
            desiredRateDouble = 1.0;
        if (desiredRateInt < 1)
            desiredRateInt = 1;

        if (fabs(*rateDoublePtr - desiredRateDouble) > 0.0000005) {
            fast_log("Applying FF8 rate: speed=%.3f oldDouble=%.9f newDouble=%.9f oldInt=%lu newInt=%lu",
                speed, *rateDoublePtr, desiredRateDouble, (unsigned long)*rateIntPtr, (unsigned long)desiredRateInt);
            write_memory(rateDoublePtr, &desiredRateDouble, sizeof(desiredRateDouble));
            write_memory(rateIntPtr, &desiredRateInt, sizeof(desiredRateInt));
        } else if (*rateIntPtr != desiredRateInt) {
            fast_log("Correcting FF8 integer rate: oldInt=%lu newInt=%lu",
                (unsigned long)*rateIntPtr, (unsigned long)desiredRateInt);
            write_memory(rateIntPtr, &desiredRateInt, sizeof(desiredRateInt));
        }

        if (fabs(*intervalPtr - desiredInterval) > 0.0000005) {
            fast_log("Applying FF8 turbo: speed=%.3f old=%.9f new=%.9f", speed, *intervalPtr, desiredInterval);
            write_memory(intervalPtr, &desiredInterval, sizeof(desiredInterval));
        }

        Sleep(10);
    }
}

/* ---- CE-style spinlock (matches speedhackmain.pas TSimpleLock) ---- */

typedef struct {
    volatile LONG count;
    volatile DWORD owner;
} SpinLock;

static void spin_lock(SpinLock *l) {
    DWORD tid = GetCurrentThreadId();
    if (l->owner != tid) {
        while (InterlockedExchange(&l->count, 1) != 0)
            Sleep(0);
        l->owner = tid;
    } else {
        InterlockedIncrement(&l->count);
    }
}

static void spin_unlock(SpinLock *l) {
    if (l->count == 1)
        l->owner = 0;
    InterlockedDecrement(&l->count);
}

static SpinLock GTCLock = {0, 0};
static SpinLock QPCLock = {0, 0};

/* ---- Original function pointers (filled by MinHook) ---- */

typedef DWORD     (WINAPI *fn_GetTickCount)(void);
typedef ULONGLONG (WINAPI *fn_GetTickCount64)(void);
typedef BOOL      (WINAPI *fn_QueryPerformanceCounter)(LARGE_INTEGER*);
typedef DWORD     (WINAPI *fn_timeGetTime)(void);

static fn_GetTickCount             Real_GTC   = NULL;
static fn_GetTickCount64           Real_GTC64 = NULL;
static fn_QueryPerformanceCounter  Real_QPC   = NULL;
static fn_timeGetTime              Real_TGT   = NULL;

/* ---- CE-style state variables (matches speedhackmain.pas globals) ---- */

/* GetTickCount (32-bit) */
static DWORD    gtc_initialoffset = 0;   /* fake time at last anchor */
static DWORD    gtc_initialtime   = 0;   /* real time at last anchor */
static double   gtc_lastspeed     = 1.0;
static int      gtc_inited        = 0;

/* GetTickCount64 */
static LONGLONG gtc64_initialoffset = 0;
static LONGLONG gtc64_initialtime   = 0;
static double   gtc64_lastspeed     = 1.0;
static int      gtc64_inited        = 0;

/* QueryPerformanceCounter */
static LONGLONG qpc_initialoffset = 0;
static LONGLONG qpc_initialtime   = 0;
static double   qpc_lastspeed     = 1.0;
static int      qpc_inited        = 0;

/* ---- Hooked functions (CE algorithm, 1:1 port) ---- */

/*
 * speedhackversion_GetTickCount - used for both GetTickCount AND timeGetTime
 * Exactly matches CE: result = trunc((currentTime - initialtime) * speed) + initialoffset
 */
static DWORD WINAPI Hooked_GTC(void)
{
    DWORD currentTime;
    DWORD result;

    if (!g_shared) return Real_GTC();

    spin_lock(&GTCLock);

    currentTime = Real_GTC();
    double speed = g_shared->speed;
    if (speed <= 0.0) speed = 1.0;

    if (!gtc_inited) {
        gtc_initialoffset = currentTime;
        gtc_initialtime   = currentTime;
        gtc_lastspeed     = speed;
        gtc_inited        = 1;
        spin_unlock(&GTCLock);
        return currentTime;
    }

    /* Re-anchor on speed change (CE's InitializeSpeedhack logic) */
    if (speed != gtc_lastspeed) {
        /* Current fake time = what the game sees right now at old speed */
        DWORD fakeCurrent = (DWORD)((LONGLONG)((currentTime - gtc_initialtime) * gtc_lastspeed) + gtc_initialoffset);
        gtc_initialoffset = fakeCurrent;
        gtc_initialtime   = currentTime;
        gtc_lastspeed     = speed;
    }

    /* CE formula: trunc((currentTime - initialtime) * speedmultiplier) + initialoffset */
    result = (DWORD)((LONGLONG)((currentTime - gtc_initialtime) * speed) + gtc_initialoffset);

    spin_unlock(&GTCLock);
    return result;
}

static ULONGLONG WINAPI Hooked_GTC64(void)
{
    ULONGLONG currentTime;
    ULONGLONG result;

    if (!g_shared) return Real_GTC64();

    spin_lock(&GTCLock);

    currentTime = Real_GTC64();
    double speed = g_shared->speed;
    if (speed <= 0.0) speed = 1.0;

    if (!gtc64_inited) {
        gtc64_initialoffset = (LONGLONG)currentTime;
        gtc64_initialtime   = (LONGLONG)currentTime;
        gtc64_lastspeed     = speed;
        gtc64_inited        = 1;
        spin_unlock(&GTCLock);
        return currentTime;
    }

    if (speed != gtc64_lastspeed) {
        LONGLONG fakeCurrent = (LONGLONG)(((LONGLONG)currentTime - gtc64_initialtime) * gtc64_lastspeed) + gtc64_initialoffset;
        gtc64_initialoffset = fakeCurrent;
        gtc64_initialtime   = (LONGLONG)currentTime;
        gtc64_lastspeed     = speed;
    }

    result = (ULONGLONG)((LONGLONG)(((LONGLONG)currentTime - gtc64_initialtime) * speed) + gtc64_initialoffset);

    spin_unlock(&GTCLock);
    return result;
}

/*
 * CE behavior: timeGetTime is literally "jmp speedhackversion_GetTickCount".
 * It calls Real_GetTickCount (NOT Real_timeGetTime) to share the exact same
 * time source and state. This prevents state corruption when both GTC and
 * TGT are called by different threads sharing gtc_initialtime.
 */
static DWORD WINAPI Hooked_TGT(void)
{
    return Hooked_GTC();
}

static BOOL WINAPI Hooked_QPC(LARGE_INTEGER *lpCount)
{
    LONGLONG currentTime;
    BOOL ret;

    ret = Real_QPC(lpCount);
    if (!ret || !lpCount || !g_shared)
        return ret;

    spin_lock(&QPCLock);

    currentTime = lpCount->QuadPart;
    double speed = g_shared->speed;
    if (speed <= 0.0) speed = 1.0;

    if (!qpc_inited) {
        qpc_initialoffset = currentTime;
        qpc_initialtime   = currentTime;
        qpc_lastspeed     = speed;
        qpc_inited        = 1;
        spin_unlock(&QPCLock);
        return ret;
    }

    if (speed != qpc_lastspeed) {
        LONGLONG fakeCurrent = (LONGLONG)((currentTime - qpc_initialtime) * qpc_lastspeed) + qpc_initialoffset;
        qpc_initialoffset = fakeCurrent;
        qpc_initialtime   = currentTime;
        qpc_lastspeed     = speed;
    }

    /* CE formula: trunc((currentTime64 - initialtime64) * speedmultiplier) + initialoffset64 */
    lpCount->QuadPart = (LONGLONG)((currentTime - qpc_initialtime) * speed) + qpc_initialoffset;

    spin_unlock(&QPCLock);
    return ret;
}

/* ---- Public API ---- */

void SetSharedData(void *sharedData)
{
    g_shared = (volatile FastShared*)sharedData;
}

int InstallHooks(void)
{
    if (MH_Initialize() != MH_OK)
        return 0;

    int count = 0;
    void *target;

    /* --- GetTickCount: try kernelbase first (CE behavior), then kernel32 --- */
    HMODULE hKernelBase = GetModuleHandleA("kernelbase.dll");
    HMODULE hKernel32   = GetModuleHandleA("kernel32.dll");
    HMODULE hNtdll      = GetModuleHandleA("ntdll.dll");
    HMODULE hWinmm      = GetModuleHandleA("winmm.dll");
    if (!hWinmm) hWinmm = LoadLibraryA("winmm.dll");

    target = NULL;
    if (hKernelBase) target = (void*)GetProcAddress(hKernelBase, "GetTickCount");
    if (!target && hKernel32) target = (void*)GetProcAddress(hKernel32, "GetTickCount");
    if (target) {
        if (MH_CreateHook(target, Hooked_GTC, (void**)&Real_GTC) == MH_OK &&
            MH_EnableHook(target) == MH_OK)
            count++;
    }

    /* --- GetTickCount64: same fallback order --- */
    target = NULL;
    if (hKernelBase) target = (void*)GetProcAddress(hKernelBase, "GetTickCount64");
    if (!target && hKernel32) target = (void*)GetProcAddress(hKernel32, "GetTickCount64");
    if (target) {
        if (MH_CreateHook(target, Hooked_GTC64, (void**)&Real_GTC64) == MH_OK &&
            MH_EnableHook(target) == MH_OK)
            count++;
    }

    /* --- QPC: try ntdll.RtlQueryPerformanceCounter first (CE behavior), then kernel32.QPC --- */
    /* IMPORTANT: only hook ONE of these, never both! */
    target = NULL;
    if (hNtdll) target = (void*)GetProcAddress(hNtdll, "RtlQueryPerformanceCounter");
    if (!target && hKernel32) target = (void*)GetProcAddress(hKernel32, "QueryPerformanceCounter");
    if (target) {
        if (MH_CreateHook(target, Hooked_QPC, (void**)&Real_QPC) == MH_OK &&
            MH_EnableHook(target) == MH_OK)
            count++;
    }

    /* --- timeGetTime --- */
    if (hWinmm) {
        target = (void*)GetProcAddress(hWinmm, "timeGetTime");
        if (target) {
            if (MH_CreateHook(target, Hooked_TGT, (void**)&Real_TGT) == MH_OK &&
                MH_EnableHook(target) == MH_OK)
                count++;
        }
    }

    return count;
}

void RemoveHooks(void)
{
    MH_DisableHook(MH_ALL_HOOKS);
    MH_Uninitialize();
}

void StartGameSpecificHooks(void)
{
    if (g_ff8Thread)
        return;

    InterlockedExchange(&g_ff8Stop, 0);
    g_ff8Thread = CreateThread(NULL, 0, FF8TurboThread, NULL, 0, NULL);
    fast_log("StartGameSpecificHooks thread=%p", g_ff8Thread);
}

void StopGameSpecificHooks(void)
{
    HANDLE thread = g_ff8Thread;
    if (!thread)
        return;

    InterlockedExchange(&g_ff8Stop, 1);
    WaitForSingleObject(thread, 1000);
    CloseHandle(thread);
    g_ff8Thread = NULL;
}
