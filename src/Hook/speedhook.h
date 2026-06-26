#ifndef FAST_HOOK_H
#define FAST_HOOK_H

#include <windows.h>

// Install inline hooks on timing functions via MinHook
int InstallHooks(void);

// Remove all hooks and clean up
void RemoveHooks(void);

// Set the shared memory pointer for speed data
void SetSharedData(void* sharedData);

// FF8-specific turbo worker lifecycle
void StartGameSpecificHooks(void);
void StopGameSpecificHooks(void);

#endif
