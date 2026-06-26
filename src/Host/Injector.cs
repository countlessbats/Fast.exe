using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Fast;

static class Injector
{
    public static bool Inject(int pid, string dllPath)
    {
        bool targetIs32Bit = IsTarget32Bit(pid);
        string selectedDll = ResolveHookDll(dllPath, targetIs32Bit);

        if (targetIs32Bit && Environment.Is64BitProcess)
        {
            string helper = Path.Combine(AppContext.BaseDirectory, "FastInjector32.exe");
            if (!File.Exists(helper))
                throw new FileNotFoundException($"32-bit injector helper not found: {helper}");

            Log.Write($"Inject request routed to x86 helper: pid={pid}, dll={selectedDll}");
            return RunHelper(helper, pid, selectedDll);
        }

        if (!targetIs32Bit && !Environment.Is64BitProcess)
            throw new Exception("A 32-bit Fast process cannot inject the 64-bit hook. Start the main 64-bit Fast.exe instead.");

        return InjectDirect(pid, selectedDll);
    }

    public static bool InjectDirect(int pid, string dllPath)
    {
        string fullPath = Path.GetFullPath(dllPath);
        Log.Write($"Inject request: pid={pid}, dll={fullPath}");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Hook DLL not found: {fullPath}");

        byte[] pathBytes = Encoding.ASCII.GetBytes(fullPath + '\0');

        IntPtr hProcess = Native.OpenProcess(Native.PROCESS_ALL_ACCESS, false, pid);
        if (hProcess == IntPtr.Zero)
            throw new Exception($"OpenProcess failed for PID {pid}: {Marshal.GetLastWin32Error()}");

        try
        {
            // Allocate memory in target for DLL path
            IntPtr remoteMem = Native.VirtualAllocEx(hProcess, IntPtr.Zero,
                (UIntPtr)pathBytes.Length, Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);
            if (remoteMem == IntPtr.Zero)
                throw new Exception($"VirtualAllocEx failed: {Marshal.GetLastWin32Error()}");

            // Write DLL path
            if (!Native.WriteProcessMemory(hProcess, remoteMem, pathBytes, (UIntPtr)pathBytes.Length, out _))
            {
                Native.VirtualFreeEx(hProcess, remoteMem, UIntPtr.Zero, Native.MEM_RELEASE);
                throw new Exception($"WriteProcessMemory failed: {Marshal.GetLastWin32Error()}");
            }

            // Get LoadLibraryA address
            IntPtr hKernel = Native.GetModuleHandleA("kernel32.dll");
            IntPtr loadLibAddr = Native.GetProcAddress(hKernel, "LoadLibraryA");
            if (loadLibAddr == IntPtr.Zero)
                throw new Exception("Could not find LoadLibraryA");

            // Create remote thread
            IntPtr hThread = Native.CreateRemoteThread(hProcess, IntPtr.Zero, UIntPtr.Zero,
                loadLibAddr, remoteMem, 0, out _);
            if (hThread == IntPtr.Zero)
            {
                Native.VirtualFreeEx(hProcess, remoteMem, UIntPtr.Zero, Native.MEM_RELEASE);
                throw new Exception($"CreateRemoteThread failed: {Marshal.GetLastWin32Error()}");
            }

            Native.WaitForSingleObject(hThread, 5000);
            if (!Native.GetExitCodeThread(hThread, out uint exitCode))
            {
                Native.CloseHandle(hThread);
                Native.VirtualFreeEx(hProcess, remoteMem, UIntPtr.Zero, Native.MEM_RELEASE);
                throw new Exception($"GetExitCodeThread failed: {Marshal.GetLastWin32Error()}");
            }

            if (exitCode == 0)
            {
                Log.Write($"Inject failed in target: pid={pid}, LoadLibraryA returned 0");
                Native.CloseHandle(hThread);
                Native.VirtualFreeEx(hProcess, remoteMem, UIntPtr.Zero, Native.MEM_RELEASE);
                throw new Exception("Remote LoadLibraryA returned NULL. This usually means a DLL/target bitness mismatch.");
            }

            Log.Write($"Inject success: pid={pid}, module=0x{exitCode:X}");
            Native.CloseHandle(hThread);
            Native.VirtualFreeEx(hProcess, remoteMem, UIntPtr.Zero, Native.MEM_RELEASE);
            return true;
        }
        finally
        {
            Native.CloseHandle(hProcess);
        }
    }

    private static string ResolveHookDll(string configuredDllPath, bool targetIs32Bit)
    {
        string configuredFullPath = Path.GetFullPath(configuredDllPath);
        if (!targetIs32Bit || !Environment.Is64BitProcess)
            return configuredFullPath;

        string directory = Path.GetDirectoryName(configuredFullPath) ?? AppContext.BaseDirectory;
        string x86Hook = Path.Combine(directory, "FastHook32.dll");
        return File.Exists(x86Hook) ? x86Hook : configuredFullPath;
    }

    private static bool IsTarget32Bit(int pid)
    {
        if (!Environment.Is64BitOperatingSystem)
            return true;

        IntPtr hProcess = Native.OpenProcess(Native.PROCESS_ALL_ACCESS, false, pid);
        if (hProcess == IntPtr.Zero)
            throw new Exception($"OpenProcess failed while checking target bitness for PID {pid}: {Marshal.GetLastWin32Error()}");

        try
        {
            if (!Native.IsWow64Process(hProcess, out bool wow64))
                throw new Exception($"IsWow64Process failed for PID {pid}: {Marshal.GetLastWin32Error()}");

            return wow64;
        }
        finally
        {
            Native.CloseHandle(hProcess);
        }
    }

    private static bool RunHelper(string helperPath, int pid, string dllPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--inject-direct");
        startInfo.ArgumentList.Add(pid.ToString());
        startInfo.ArgumentList.Add(Path.GetFullPath(dllPath));

        using var helper = Process.Start(startInfo)
            ?? throw new Exception("Failed to start 32-bit injector helper.");

        if (!helper.WaitForExit(10000))
        {
            try { helper.Kill(); } catch { }
            throw new Exception("32-bit injector helper timed out.");
        }

        if (helper.ExitCode != 0)
            throw new Exception($"32-bit injector helper failed with exit code {helper.ExitCode}.");

        Log.Write($"x86 helper injection success: pid={pid}");
        return true;
    }
}
