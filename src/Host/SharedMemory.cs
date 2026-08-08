using System;
using System.Runtime.InteropServices;

namespace Fast;

class SharedMemory : IDisposable
{
    private readonly object _lock = new();
    private IntPtr _hMap;
    private IntPtr _pView;
    public int Pid { get; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FastShared
    {
        public double Speed;
        public int Enabled;
        public int Detach;
    }

    public SharedMemory(int pid)
    {
        Pid = pid;
        string name = $"Fast_Speed_{pid}";
        Log.Write($"SharedMemory create: pid={pid}, name={name}");

        _hMap = Native.CreateFileMappingA(
            (IntPtr)(-1), IntPtr.Zero,
            Native.PAGE_READWRITE,
            0, (uint)Marshal.SizeOf<FastShared>(),
            name);

        if (_hMap == IntPtr.Zero)
            throw new Exception($"CreateFileMapping failed: {Marshal.GetLastWin32Error()}");

        _pView = Native.MapViewOfFile(_hMap, Native.FILE_MAP_WRITE, 0, 0, UIntPtr.Zero);
        if (_pView == IntPtr.Zero)
        {
            Native.CloseHandle(_hMap);
            throw new Exception($"MapViewOfFile failed: {Marshal.GetLastWin32Error()}");
        }

        // Initialize
        var data = new FastShared { Speed = 1.0, Enabled = 0, Detach = 0 };
        Marshal.StructureToPtr(data, _pView, false);
    }

    public void SetSpeed(double speed, bool enabled)
    {
        lock (_lock)
        {
            if (_pView == IntPtr.Zero) return;
            Log.Write($"SharedMemory SetSpeed: pid={Pid}, speed={speed:F2}, enabled={enabled}");
            Marshal.WriteInt64(_pView, 0, BitConverter.DoubleToInt64Bits(speed));
            Marshal.WriteInt32(_pView, 8, enabled ? 1 : 0);
        }
    }

    public void SignalDetach()
    {
        lock (_lock)
        {
            if (_pView == IntPtr.Zero) return;
            Marshal.WriteInt32(_pView, 12, 1);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            Log.Write($"SharedMemory dispose: pid={Pid}");
            if (_pView != IntPtr.Zero) { Native.UnmapViewOfFile(_pView); _pView = IntPtr.Zero; }
            if (_hMap != IntPtr.Zero) { Native.CloseHandle(_hMap); _hMap = IntPtr.Zero; }
        }
    }
}
