using System;
using System.IO;

namespace Fast;

static class Log
{
    private const long MaxLogBytes = 1024 * 1024;
    private static readonly object Sync = new();
    private static readonly string LogPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Fast.log");

    public static string Path => LogPath;

    public static void Write(string message)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
            RotateIfNeeded();
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogPath))
            return;

        var info = new FileInfo(LogPath);
        if (info.Length < MaxLogBytes)
            return;

        string oldPath = LogPath + ".old";
        try
        {
            File.Delete(oldPath);
            File.Move(LogPath, oldPath);
        }
        catch
        {
            File.Delete(LogPath);
        }
    }
}
