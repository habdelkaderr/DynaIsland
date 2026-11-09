using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DynaIsland.Ui;

/// <summary>
/// Shared colours for the desktop surfaces (menu bar, dock, Control Center, Launchpad, Settings).
/// Follows Windows' light/dark setting unless forced, and updates live (event-driven, no polling).
/// Brushes are application resources referenced with DynamicResource.
/// </summary>
public static class Theme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDark { get; private set; } = true;

    /// <summary>Glass tint for blurred surfaces (alpha follows the transparency setting).</summary>
    public static Color SurfaceTint { get; private set; }

    public static event EventHandler? Changed;

    private static string _mode = "System";
    private static double _opacity = 0.6;

    public static void Initialize(string mode, double opacity)
    {
        _mode = mode;
        _opacity = opacity;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && _mode == "System")
                Application.Current.Dispatcher.InvokeAsync(Apply);
        };
        Apply();
    }

    public static void Configure(string mode, double opacity)
    {
        _mode = mode;
        _opacity = opacity;
        Apply();
    }

    private static void Apply()
    {
        IsDark = _mode switch
        {
            "Dark" => true,
            "Light" => false,
            _ => !WindowsUsesLightTheme(),
        };

        byte alpha = (byte)Math.Round(255 * Math.Clamp(_opacity, 0.25, 0.95));
        SurfaceTint = IsDark ? Color.FromArgb(alpha, 0x1C, 0x1C, 0x20) : Color.FromArgb(alpha, 0xF2, 0xF2, 0xF5);

        var r = Application.Current.Resources;
        Set(r, "Ui.Text", IsDark ? Color.FromRgb(0xF5, 0xF5, 0xF7) : Color.FromRgb(0x1D, 0x1D, 0x1F));
        Set(r, "Ui.TextSecondary", IsDark ? Color.FromRgb(0xA1, 0xA1, 0xA6) : Color.FromRgb(0x6E, 0x6E, 0x73));
        Set(r, "Ui.TextFaint", IsDark ? Color.FromRgb(0x6E, 0x6E, 0x73) : Color.FromRgb(0x9A, 0x9A, 0xA0));
        Set(r, "Ui.Control", IsDark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        Set(r, "Ui.ControlHover", IsDark ? Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x24, 0x00, 0x00, 0x00));
        Set(r, "Ui.Tile", IsDark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
        Set(r, "Ui.Divider", IsDark ? Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
        Set(r, "Ui.Stroke", IsDark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00));
        Set(r, "Ui.Window", IsDark ? Color.FromRgb(0x20, 0x20, 0x24) : Color.FromRgb(0xF5, 0xF5, 0xF7));
        Set(r, "Ui.Accent", AccentColor());
        Set(r, "Ui.OnAccent", Colors.White);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void Set(ResourceDictionary r, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        r[key] = brush;
    }

    public static bool WindowsUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }

    /// <summary>Switches Windows itself between light and dark (apps and system), like Settings does.</summary>
    public static void SetWindowsLightTheme(bool light)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey))
        {
            key.SetValue("AppsUseLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
        }
        SendMessageTimeout(new IntPtr(0xFFFF) /* HWND_BROADCAST */, 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero,
            "ImmersiveColorSet", 0x0002 /* SMTO_ABORTIFHUNG */, 200, out _);
    }

    /// <summary>The user's Windows accent colour (Settings > Personalization > Colors).</summary>
    private static Color AccentColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                var c = Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
                // Keep it readable as a fill behind white text.
                double luma = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
                if (luma < 200) return c;
            }
        }
        catch
        {
            // fall back to Apple blue
        }
        return Color.FromRgb(0x0A, 0x84, 0xFF);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, string lParam, int flags, int timeout, out IntPtr result);
}
