using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DynaIsland.Ui;

/// <summary>
/// Click-through blurred rectangle placed behind a transparent window's panel (the dock), so the panel
/// gets real blur while its content can still overflow it (icon magnification).
/// </summary>
public sealed class GlassBackdrop : Window
{
    private readonly IntPtr _hwnd;
    private bool _blur = true;

    public GlassBackdrop()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 10;
        Height = 10;
        Title = "DynaIsland glass";
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE,
            new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT));
        Glass.Apply(this, Theme.SurfaceTint, _blur, round: true, Theme.IsDark);
        Theme.Changed += OnThemeChanged;
        Closed += (_, _) => Theme.Changed -= OnThemeChanged;
    }

    public void SetBlur(bool blur)
    {
        _blur = blur;
        Glass.SetTint(_hwnd, Theme.SurfaceTint, _blur);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Glass.SetTint(_hwnd, Theme.SurfaceTint, _blur);

    /// <summary>Moves/resizes (physical pixels) and keeps it directly below <paramref name="above"/> in z-order.</summary>
    public void Place(Int32Rect rect, IntPtr above)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        Native.SetWindowPos(_hwnd, above, rect.X, rect.Y, rect.Width, rect.Height,
            Native.SWP_NOACTIVATE | 0x0040 /* SWP_SHOWWINDOW */);
    }

    public void Conceal() => Native.ShowWindow(_hwnd, 0 /* SW_HIDE */);
}

/// <summary>Invisible 2-pixel strip along a screen edge; the mouse touching it reveals an auto-hidden panel.</summary>
public sealed class EdgeTrigger : Window
{
    private readonly IntPtr _hwnd;

    public event Action? Touched;

    public EdgeTrigger()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // alpha 1: invisible but receives the mouse
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "DynaIsland edge";
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        MouseEnter += (_, _) => Touched?.Invoke();
        DragEnter += (_, _) => Touched?.Invoke();
        AllowDrop = true;
    }

    public void Place(Int32Rect rect)
    {
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height,
            Native.SWP_NOACTIVATE | 0x0040 /* SWP_SHOWWINDOW */);
    }

    public void Conceal() => Native.ShowWindow(_hwnd, 0);
}
