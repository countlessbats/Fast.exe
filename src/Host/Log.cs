using System;
using System.IO;

namespace Fast;

static class Log
{
    private static readonly object Sync = new();
    private static readonly string LogPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Fast.log");

    public static string Path => LogPath;

    public static void Write(string message)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
    }
}
