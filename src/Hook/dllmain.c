/*
 * FastHook DLL entry point.
 *
 * CE behavior: hooks are installed once and NEVER removed. Unhooking while
 * game threads are mid-call inside a trampoline is the #1 cause of crashes.
 * CE's own destructor says: "do not undo the speedhack script (not all games
 * handle a counter that goes back)".
 *
 * On "detach" signal from host, we just flip speed to 1.0 passthrough.
 * The hooks stay resident. The DLL stays loaded. No crash.
 */

#include <windows.h>
#include <stdio.h>
#include "shared.h"
#include "speedhook.h"

static HANDLE g_hMapFile = NULL;
static FastShared *g_pShared = NULL;
static HANDLE g_initThread = NULL;

static int is_ff8_process(void)
{
    char path[MAX_PATH];
    char *name;

    if (!GetModuleFileNameA(NULL, path, sizeof(path)))
        return 0;

    name = strrchr(path, '\\');
    name = name ? (name + 1) : path;

    return _stricmp(name, "FFVIII.exe") == 0;
}

static DWORD WINAPI InitThread(LPVOID param)
{
    int ff8 = (int)(INT_PTR)param;

    if (!ff8) {
        InstallHooks();
    }

    StartGameSpecificHooks();
    return 0;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hModule);

        char shmName[64];
        sprintf(shmName, FAST_SHM_PREFIX "%lu", GetCurrentProcessId());

        g_hMapFile = OpenFileMappingA(FILE_MAP_READ, FALSE, shmName);
        if (!g_hMapFile) return FALSE;

        g_pShared = (FastShared*)MapViewOfFile(g_hMapFile, FILE_MAP_READ, 0, 0, sizeof(FastShared));
        if (!g_pShared) {
            CloseHandle(g_hMapFile);
            return FALSE;
        }

        SetSharedData((void*)g_pShared);
        g_initThread = CreateThread(NULL, 0, InitThread, (LPVOID)(INT_PTR)is_ff8_process(), 0, NULL);
        if (g_initThread) CloseHandle(g_initThread);

        /* No watch thread. No unhooking. No FreeLibrary.
         * Hooks check g_shared->enabled on every call - when host sets it
         * to 0 or closes the shared memory, hooks just passthrough.
         * DLL stays loaded for the lifetime of the process. This is safe. */
    }
    return TRUE;
}
