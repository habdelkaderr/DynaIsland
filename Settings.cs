using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace DynaIsland;

/// <summary>User preferences, stored locally in %APPDATA%\DynaIsland\settings.json.</summary>
internal sealed class Settings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynaIsland", "settings.json");

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "DynaIsland";

    public bool ShowMedia { get; set; } = true;
    public bool ShowVolume { get; set; } = true;
    public bool ShowBattery { get; set; } = true;
    public bool ShowCapsLock { get; set; } = true;
    public bool ShowClipboard { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;
    public bool HideWhenIdle { get; set; }
    public bool ShowIsland { get; set; } = true;

    /// <summary>Set once auto-start has been switched on by default, so turning it off sticks.</summary>
    public bool AutoStartConfigured { get; set; }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Error("settings load", ex);
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Error("settings save", ex);
        }
    }

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value && Environment.ProcessPath is { } exe)
                key.SetValue(RunValue, $"\"{exe}\"");
            else
                key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }
}
