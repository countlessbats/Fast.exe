#ifndef FAST_SHARED_H
#define FAST_SHARED_H

#pragma pack(push, 1)
typedef struct {
    double speed;       // multiplier: 1.0 = normal, 2.0 = double, 0.5 = half
    int    enabled;     // 1 = speed hack active, 0 = passthrough
    int    detach;      // 1 = unhook and unload
} FastShared;
#pragma pack(pop)

#define FAST_SHM_PREFIX "Fast_Speed_"

#endif
