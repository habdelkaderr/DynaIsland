using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace DynaIsland;

public sealed record IslandShortcut(string Id, bool Ask);

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

    /// <summary>"Island" (floating pill) or "Notch" (attached to the top edge).</summary>
    public string Shape { get; set; } = "Island";

    /// <summary>Volume and brightness as pop-ups sliding in from the screen edges instead of in the island.</summary>
    public bool SidePopups { get; set; } = true;

    /// <summary>Press Enter in the AI app after handing over the prompt (only when its message box has focus).</summary>
    public bool AiAutoSend { get; set; } = true;

    /// <summary>
    /// App shortcuts on the island (Start-list IDs, in order). Ask = open the island's prompt box and hand
    /// the text to the app; otherwise just open the app. Null until first seeded with the installed AI apps.
    /// </summary>
    public List<IslandShortcut>? IslandShortcuts { get; set; }

    // ---- Desktop modules (each can be switched on/off live) -----------------------------------
    // ShowIsland (above) is the island's own switch.
    public bool DockEnabled { get; set; } = true;
    public bool MenuBarEnabled { get; set; } = true;
    public bool LaunchpadEnabled { get; set; } = true;
    public bool ControlCenterEnabled { get; set; } = true;

    // ---- Dock ---------------------------------------------------------------------------------
    /// <summary>Bottom, Left or Right.</summary>
    public string DockPosition { get; set; } = "Bottom";
    public double DockIconSize { get; set; } = 48;
    /// <summary>0 = no magnification, 1 = icons grow to about 1.8x.</summary>
    public double DockMagnification { get; set; } = 0.6;
    public double DockSpacing { get; set; } = 6;
    public bool DockAutoHide { get; set; }
    /// <summary>Maximized windows stop at the dock instead of going underneath it.</summary>
    public bool DockReserveSpace { get; set; } = true;
    /// <summary>While the dock is on, set the Windows taskbar to auto-hide (restored when the dock is off or on quit).</summary>
    public bool DockReplacesTaskbar { get; set; } = true;
    /// <summary>Start-list IDs of apps kept in the dock. Null until first seeded.</summary>
    public List<string>? DockPins { get; set; }
    /// <summary>Windows taskbar auto-hide state before DynaIsland changed it (survives crashes so it can be restored).</summary>
    public bool? TaskbarAutoHideBefore { get; set; }

    // ---- Menu bar -----------------------------------------------------------------------------
    public bool MenuBarShowMedia { get; set; } = true;
    public bool MenuBarShowKeyboard { get; set; } = true;
    public bool MenuBarShowWifi { get; set; } = true;
    public bool MenuBarShowVolume { get; set; } = true;
    public bool MenuBarShowBattery { get; set; } = true;
    public bool MenuBarShowNotifications { get; set; } = true;
    public bool MenuBarShowDate { get; set; } = true;
    public bool MenuBarShowSeconds { get; set; }
    /// <summary>Folders in the menu bar's Go menu (known folder names or paths).</summary>
    public List<string> MenuBarGoItems { get; set; } = ["Home", "Desktop", "Documents", "Downloads", "Pictures", "This PC", "Recycle Bin"];

    // ---- Launchpad ----------------------------------------------------------------------------
    /// <summary>Custom order (Start-list IDs) after drag-and-drop; apps not listed follow alphabetically.</summary>
    public List<string> LaunchpadOrder { get; set; } = [];
    public Dictionary<string, int> LaunchCounts { get; set; } = [];

    // ---- Appearance & behaviour ---------------------------------------------------------------
    /// <summary>System, Dark or Light (desktop surfaces; the island itself stays dark like Apple's).</summary>
    public string UiTheme { get; set; } = "System";
    /// <summary>Glass tint opacity, 0.25 (very clear) to 0.95 (nearly solid).</summary>
    public double GlassOpacity { get; set; } = 0.6;
    public bool GlassBlur { get; set; } = true;
    /// <summary>0.5 (slow) to 2 (fast).</summary>
    public double AnimationSpeed { get; set; } = 1.0;

    // ---- Keyboard shortcuts (empty = off) -----------------------------------------------------
    public string HotkeyLaunchpad { get; set; } = "Ctrl+Alt+L";
    public string HotkeyControlCenter { get; set; } = "Ctrl+Alt+C";
    public string HotkeyToggleAll { get; set; } = "Ctrl+Alt+D";
    public string HotkeySettings { get; set; } = "";

    /// <summary>Set once auto-start has been switched on by default, so turning it off sticks.</summary>
    public bool AutoStartConfigured { get; set; }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
                // Ctrl+Alt+Space (the first default) is commonly taken by other apps.
                if (settings.HotkeyLaunchpad == "Ctrl+Alt+Space") settings.HotkeyLaunchpad = "Ctrl+Alt+L";
                return settings;
            }
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
