using System;
using System.Collections.Generic;
using System.IO;

namespace CrewlessLogistics.Belts;

/// <summary>Append-only file log next to the mod DLL (pattern from SharedShip). Errors rate-limited per key.</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> LastByKey = new();
    private static string? _path;

    public static string ModFolder { get; } =
        Path.GetDirectoryName(typeof(Log).Assembly.Location) ?? AppContext.BaseDirectory;

    public static void Init(string fileName = "CrewlessLogistics.Belts.log")
    {
        _path = Path.Combine(ModFolder, fileName);
        try
        {
            File.WriteAllText(_path, $"[{DateTime.Now:HH:mm:ss}] log start{Environment.NewLine}");
        }
        catch
        {
            _path = null;
        }
    }

    public static void Info(string msg) => Write("INF", msg);

    public static void Error(string key, Exception ex)
    {
        long now = Environment.TickCount64;
        lock (Gate)
        {
            if (LastByKey.TryGetValue(key, out long last) && now - last < 30_000)
            {
                return;
            }
            LastByKey[key] = now;
        }
        Write("ERR", key + ": " + ex);
    }

    private static void Write(string level, string msg)
    {
        if (_path == null)
        {
            return;
        }
        try
        {
            lock (Gate)
            {
                File.AppendAllText(_path, $"[{DateTime.Now:HH:mm:ss}] {level} {msg}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
