using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DynaIsland.Ui;

/// <summary>
/// "Liquid glass" surfaces: Windows' own acrylic blur behind a window (SetWindowCompositionAttribute,
/// which keeps working while the window is inactive, unlike the system backdrops), plus Windows 11
/// rounded corners. Works on normal (non-layered) windows; where blur is unavailable the window shows
/// its tint as a solid colour instead.
/// </summary>
public static class Glass
{
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_DISABLED = 0;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    private const int ACCENT_ENABLE_GRADIENT = 1;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;

    /// <summary>Call from SourceInitialized (or after EnsureHandle) of a window with AllowsTransparency=false.</summary>
    public static void Apply(Window window, Color tint, bool blur, bool round, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (HwndSource.FromHwnd(hwnd) is { } source) source.CompositionTarget.BackgroundColor = Colors.Transparent;
        window.Background = Brushes.Transparent;

        int darkMode = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
        int corner = round ? 2 /* ROUND */ : 1 /* DONOTROUND */;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        int noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
        SetTint(hwnd, tint, blur);
    }

    /// <summary>Updates the tint (theme / transparency changes).</summary>
    public static void SetTint(IntPtr hwnd, Color tint, bool blur)
    {
        var accent = new AccentPolicy
        {
            AccentState = blur ? ACCENT_ENABLE_ACRYLICBLURBEHIND : ACCENT_ENABLE_GRADIENT,
            AccentFlags = 2, // draw on all borders
            GradientColor = (uint)(tint.A << 24 | tint.B << 16 | tint.G << 8 | tint.R), // ABGR
        };
        if (!blur) accent.GradientColor |= 0xFF000000; // solid when blur is off
        int size = Marshal.SizeOf(accent);
        IntPtr data = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, data, false);
            var attr = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = data, SizeOfData = size };
            SetWindowCompositionAttribute(hwnd, ref attr);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    public static void Disable(IntPtr hwnd)
    {
        var accent = new AccentPolicy { AccentState = ACCENT_DISABLED };
        int size = Marshal.SizeOf(accent);
        IntPtr data = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, data, false);
            var attr = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = data, SizeOfData = size };
            SetWindowCompositionAttribute(hwnd, ref attr);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
