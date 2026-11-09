using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DynaIsland.Services;

/// <summary>
/// Event-driven window tracking (no polling): Windows tells us when the foreground window changes and
/// when top-level windows appear, disappear, minimize or restore. Window-set changes are debounced.
/// Must be created on the UI thread (hooks are delivered through its message loop).
/// </summary>
public sealed class WinEvents : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint EVENT_OBJECT_CLOAKED = 0x8017;
    private const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    private readonly WinEventProc _proc; // kept alive for the hooks' lifetime
    private readonly List<IntPtr> _hooks = [];
    private readonly DispatcherTimer _debounce;

    /// <summary>The foreground window changed.</summary>
    public event Action<IntPtr>? ForegroundChanged;

    /// <summary>The set or state of top-level windows changed (debounced).</summary>
    public event Action? WindowsChanged;

    public WinEvents(bool start = true)
    {
        _proc = OnEvent;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); WindowsChanged?.Invoke(); };
        Enabled = start;
    }

    /// <summary>Hooks are installed only while something needs them.</summary>
    public bool Enabled
    {
        get => _hooks.Count > 0;
        set
        {
            if (value == Enabled) return;
            if (!value)
            {
                Dispose();
                return;
            }
            Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
            Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
            Hook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE); // destroy, show, hide
            Hook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED); // UWP / virtual desktop switches
        }
    }

    private void Hook(uint min, uint max)
    {
        var hook = SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (hook != IntPtr.Zero) _hooks.Add(hook);
    }

    private void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return; // OBJID_WINDOW only
        if (evt == EVENT_SYSTEM_FOREGROUND)
        {
            ForegroundChanged?.Invoke(hwnd);
            Restart();
            return;
        }
        if (GetAncestor(hwnd, 2 /* GA_ROOT */) != hwnd) return; // top-level windows only
        Restart();
    }

    private void Restart()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public void Dispose()
    {
        _debounce.Stop();
        foreach (var h in _hooks) UnhookWinEvent(h);
        _hooks.Clear();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
