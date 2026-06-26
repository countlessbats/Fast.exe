using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fast;

class SpeedSlot
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("controller")]
    public string? Controller { get; set; }

    [JsonPropertyName("speed")]
    public double Speed { get; set; } = 1.0;

    [JsonPropertyName("hold")]
    public bool Hold { get; set; } = false;

    public bool HasBinding => !string.IsNullOrEmpty(Key) || !string.IsNullOrEmpty(Controller);
    public bool HasSpeed => Speed > 0 && Math.Abs(Speed - 1.0) > 0.001;

    public string BindingDisplay
    {
        get
        {
            if (!string.IsNullOrEmpty(Key) && !string.IsNullOrEmpty(Controller))
                return $"{Key} / {Controller}";
            if (!string.IsNullOrEmpty(Key)) return Key;
            if (!string.IsNullOrEmpty(Controller)) return Controller;
            return "";
        }
    }
}

class AppSettings
{
    [JsonPropertyName("slots")]
    public List<SpeedSlot> Slots { get; set; } = new();

    [JsonPropertyName("processes")]
    public List<string> Processes { get; set; } = new();

    [JsonPropertyName("hookDllPath")]
    public string HookDllPath { get; set; } = "FastHook.dll";

    private static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fast");

    private static string DefaultPath =>
        Path.Combine(ConfigDirectory, "settings.json");

    private static string LegacyPath =>
        Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;

        EnsureConfigDirectory();
        TryMigrateLegacySettings(path);

        if (!File.Exists(path))
        {
            var settings = CreateDefault();
            settings.Save(path);
            return settings;
        }

        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? CreateDefault();
            // Ensure exactly 10 slots
            while (settings.Slots.Count < 10)
                settings.Slots.Add(new SpeedSlot());
            if (settings.Slots.Count > 10)
                settings.Slots.RemoveRange(10, settings.Slots.Count - 10);
            return settings;
        }
        catch
        {
            return CreateDefault();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        EnsureConfigDirectory();
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(this, options);
        File.WriteAllText(path, json);
    }

    public void AddProcess(string processName)
    {
        string name = NormalizeProcessName(processName);
        if (string.IsNullOrEmpty(name)) return;

        // Normalize: remove .exe suffix for comparison, store with .exe
        string normalized = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name : name + ".exe";

        if (!Processes.Any(p => p.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            Processes.Add(normalized);
            Save();
        }
    }

    public void RemoveProcess(string processName)
    {
        string normalized = NormalizeProcessName(processName);
        if (string.IsNullOrEmpty(normalized)) return;

        if (!normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            normalized += ".exe";

        Processes.RemoveAll(p => p.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    private static string NormalizeProcessName(string processName)
    {
        string name = processName.Trim();
        if (string.IsNullOrEmpty(name)) return "";

        try
        {
            string fileName = Path.GetFileName(name);
            if (!string.IsNullOrWhiteSpace(fileName))
                name = fileName;
        }
        catch { }

        return name;
    }

    private static AppSettings CreateDefault()
    {
        var settings = new AppSettings();
        for (int i = 0; i < 10; i++)
            settings.Slots.Add(new SpeedSlot());
        return settings;
    }

    private static void EnsureConfigDirectory()
    {
        Directory.CreateDirectory(ConfigDirectory);
    }

    private static void TryMigrateLegacySettings(string targetPath)
    {
        if (File.Exists(targetPath))
            return;

        if (!File.Exists(LegacyPath))
            return;

        if (string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(LegacyPath), StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            File.Copy(LegacyPath, targetPath);
        }
        catch
        {
            // Fall back to defaults if migration fails.
        }
    }
}
