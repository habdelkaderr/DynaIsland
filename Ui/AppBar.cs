using System.Runtime.InteropServices;

namespace DynaIsland.Ui;

/// <summary>
/// Reserves a strip along a screen edge (like the Windows taskbar) so maximized windows stop beside it.
/// Windows notifies the owner window when the layout changes or a full-screen app opens.
/// </summary>
internal sealed class AppBar
{
    public const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;
    private const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_GETSTATE = 4, ABM_GETTASKBARPOS = 5, ABM_SETSTATE = 10;
    public const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;

    private readonly IntPtr _hwnd;
    private bool _registered;

    /// <summary>Message id Windows uses to notify this app bar (compare in the owner's WndProc).</summary>
    public uint CallbackMessage { get; }

    public AppBar(IntPtr hwnd, string name)
    {
        _hwnd = hwnd;
        CallbackMessage = RegisterWindowMessage(name);
    }

    /// <summary>Reserves <paramref name="thickness"/> px along an edge of a monitor; returns the granted rectangle.</summary>
    public Native.RECT Reserve(uint edge, Native.RECT monitor, int thickness)
    {
        var data = Data();
        if (!_registered)
        {
            data.uCallbackMessage = CallbackMessage;
            SHAppBarMessage(ABM_NEW, ref data);
            _registered = true;
        }
        data.uEdge = edge;
        data.rc = monitor;
        switch (edge)
        {
            case ABE_TOP: data.rc.Bottom = monitor.Top + thickness; break;
            case ABE_BOTTOM: data.rc.Top = monitor.Bottom - thickness; break;
            case ABE_LEFT: data.rc.Right = monitor.Left + thickness; break;
            case ABE_RIGHT: data.rc.Left = monitor.Right - thickness; break;
        }
        SHAppBarMessage(ABM_QUERYPOS, ref data); // moves it beside other bars (e.g. the Windows taskbar)
        switch (edge)
        {
            case ABE_TOP: data.rc.Bottom = data.rc.Top + thickness; break;
            case ABE_BOTTOM: data.rc.Top = data.rc.Bottom - thickness; break;
            case ABE_LEFT: data.rc.Right = data.rc.Left + thickness; break;
            case ABE_RIGHT: data.rc.Left = data.rc.Right - thickness; break;
        }
        SHAppBarMessage(ABM_SETPOS, ref data);
        return data.rc;
    }

    public void Remove()
    {
        if (!_registered) return;
        var data = Data();
        SHAppBarMessage(ABM_REMOVE, ref data);
        _registered = false;
    }

    private APPBARDATA Data() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };

    // ---- Windows taskbar ------------------------------------------------------------------------

    public static bool TaskbarAutoHide
    {
        get
        {
            var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
            return ((int)SHAppBarMessage(ABM_GETSTATE, ref data) & 1) != 0;
        }
        set
        {
            var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), lParam = new IntPtr(value ? 1 /* ABS_AUTOHIDE */ : 2 /* ABS_ALWAYSONTOP */) };
            SHAppBarMessage(ABM_SETSTATE, ref data);
        }
    }

    /// <summary>The Windows taskbar's rectangle (primary monitor) and edge.</summary>
    public static (Native.RECT Rect, uint Edge) TaskbarPosition()
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        SHAppBarMessage(ABM_GETTASKBARPOS, ref data);
        return (data.rc, data.uEdge);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Native.RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
}
