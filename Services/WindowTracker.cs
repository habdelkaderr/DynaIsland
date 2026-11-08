namespace DynaIsland.Services;

public sealed record AppWindow(IntPtr Handle, string Title, string? Exe, string? Aumid)
{
    /// <summary>Groups windows of the same app: AppUserModelID when known, otherwise the exe path.</summary>
    public string Key => (Aumid ?? Exe ?? Handle.ToString()).ToLowerInvariant();
}

/// <summary>Top-level app windows (the ones Alt+Tab shows), front to back.</summary>
public static class WindowTracker
{
    private static readonly int OwnPid = Environment.ProcessId;

    public static List<AppWindow> Snapshot()
    {
        var handles = new List<IntPtr>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (Native.IsAppWindow(hwnd)) handles.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        var result = new List<AppWindow>(handles.Count);
        var identities = new Dictionary<uint, (string? Path, string? Aumid)>();
        foreach (var hwnd in handles)
        {
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == OwnPid) continue;
            if (!identities.TryGetValue(pid, out var identity)) identities[pid] = identity = Native.ProcessIdentity(pid);

            // UWP apps run inside ApplicationFrameHost; Chromium/Electron apps may declare their own ID per window.
            string? aumid = ShellApps.WindowAumid(hwnd) ?? identity.Aumid;
            result.Add(new AppWindow(hwnd, Native.WindowTitle(hwnd), identity.Path, aumid));
        }
        return result;
    }

    /// <summary>Whether a window belongs to the app identified by an AUMID and/or exe path.</summary>
    public static bool Matches(AppWindow window, string? aumid, string? exe)
    {
        if (aumid is not null && string.Equals(window.Aumid, aumid, StringComparison.OrdinalIgnoreCase)) return true;
        return !string.IsNullOrEmpty(exe) && string.Equals(window.Exe, exe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Brings a window to the front, restoring it if minimized.</summary>
    public static void Activate(IntPtr hwnd)
    {
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        Native.SetForegroundWindow(hwnd);
    }
}
