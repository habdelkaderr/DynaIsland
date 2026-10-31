using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace DynaIsland;

/// <summary>User preferences, stored locally in %APPDATA%\DynaIsland\settings.json.</summary>
public sealed class Settings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynaIsland", "settings.json");

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "DynaIsland";

    public bool ShowMedia { get; set; } = true;
    public bool ShowVolume { get; set; } = true;
    public bool ShowBrightness { get; set; } = true;
    public bool ShowBattery { get; set; } = true;
    public bool ShowCapsLock { get; set; } = true;
    public bool ShowClipboard { get; set; } = true;
    public bool ShowScreenshots { get; set; } = true;
    public bool ShowDownloads { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public bool ShowCalendar { get; set; } = true;
    public bool ShowBluetooth { get; set; } = true;
    public bool ShowNetwork { get; set; } = true;
    public bool ShowKeyboardLayout { get; set; } = true;
    public bool ShowDoNotDisturb { get; set; } = true;
    public bool ShowPrivacy { get; set; } = true;

    public bool HideInFullscreen { get; set; } = true;
    public bool HideWhenIdle { get; set; }
    public bool ShowIsland { get; set; } = true;
    public bool ExpandOnHover { get; set; } = true;

    /// <summary>Black, Graphite or Translucent.</summary>
    public string Theme { get; set; } = "Black";

    /// <summary>Small, Default or Large.</summary>
    public string Size { get; set; } = "Default";

    /// <summary>"Primary", "Follow" (screen of the active window) or a display device name like \\.\DISPLAY2.</summary>
    public string Screen { get; set; } = "Primary";

    /// <summary>Software rendering: less memory, slightly less smooth. Applied at start-up.</summary>
    public bool LowMemoryMode { get; set; }

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
