using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DynaIsland;

internal static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_NOACTIVATE = 0x08000000;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const int WM_POWERBROADCAST = 0x0218;
    public const int PBT_POWERSETTINGCHANGE = 0x8013;
    public const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;
    public const int VK_CAPITAL = 0x14;

    /// <summary>Power setting that reports the built-in display's brightness (0-100) whenever it changes.</summary>
    public static readonly Guid GUID_VIDEO_CURRENT_MONITOR_BRIGHTNESS = new("aded5e82-b909-4619-9949-f5d71dac0bcb");

    public static readonly Guid FOLDERID_Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
    public static readonly Guid FOLDERID_Screenshots = new("b7bede81-df94-4682-a7d8-57a52620b86f");

    // SHQueryUserNotificationState results that mean "something is running full screen".
    public const int QUNS_BUSY = 2;
    public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    public const int QUNS_PRESENTATION_MODE = 4;
    public const int QUNS_QUIET_TIME = 6;

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;

    // ---- Windows ----------------------------------------------------------------------------

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [DllImport("shell32.dll")]
    public static extern int SHQueryUserNotificationState(out int state);

    // ---- Monitors ---------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ---- Window enumeration & control (AI handoff) ---------------------------------------------

    public const long WS_EX_TRANSPARENT = 0x00000020;
    public const long WS_EX_APPWINDOW = 0x00040000;
    public const int SW_RESTORE = 9;
    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, char[] text, int max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, char[] name, ref int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr process, ref int length, char[]? id);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    public static string WindowTitle(IntPtr hwnd)
    {
        var buffer = new char[256];
        int n = GetWindowTextW(hwnd, buffer, buffer.Length);
        return n > 0 ? new string(buffer, 0, n) : "";
    }

    /// <summary>True for windows that would appear in Alt+Tab / on the taskbar.</summary>
    public static bool IsAppWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        if (ShellClasses.Contains(WindowClass(hwnd))) return false; // desktop and taskbars (owned by explorer.exe)
        return WindowTitle(hwnd).Length > 0;
    }

    private static readonly HashSet<string> ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, char[] name, int max);

    private static string WindowClass(IntPtr hwnd)
    {
        var buffer = new char[128];
        int n = GetClassNameW(hwnd, buffer, buffer.Length);
        return n > 0 ? new string(buffer, 0, n) : "";
    }

    public static (string? Path, string? Aumid) ProcessIdentity(uint pid)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return (null, null);
        try
        {
            var name = new char[1024];
            int size = name.Length;
            string? path = QueryFullProcessImageNameW(process, 0, name, ref size) ? new string(name, 0, size) : null;

            int length = 0;
            string? aumid = null;
            if (GetApplicationUserModelId(process, ref length, null) == 122 /* ERROR_INSUFFICIENT_BUFFER */ && length > 0)
            {
                var id = new char[length];
                if (GetApplicationUserModelId(process, ref length, id) == 0) aumid = new string(id, 0, length - 1);
            }
            return (path, aumid);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>Presses a key combination (e.g. Ctrl+V) in whatever window has focus.</summary>
    public static void SendKeys(params byte[] keys)
    {
        foreach (var k in keys) keybd_event(k, 0, 0, UIntPtr.Zero);
        for (int i = keys.Length - 1; i >= 0; i--) keybd_event(keys[i], 0, 2, UIntPtr.Zero);
    }

    // ---- Misc -------------------------------------------------------------------------------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, int flags, IntPtr token, out IntPtr path);

    public static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var ptr) != 0) return null;
        try { return Marshal.PtrToStringUni(ptr); }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    [DllImport("advapi32.dll")]
    public static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        int notifyFilter, SafeWaitHandle evt, [MarshalAs(UnmanagedType.Bool)] bool asynchronous);

    public const int REG_NOTIFY_CHANGE_NAME = 0x1;
    public const int REG_NOTIFY_CHANGE_LAST_SET = 0x4;
    public const int REG_NOTIFY_THREAD_AGNOSTIC = 0x10000000;

    [DllImport("ntdll.dll")]
    private static extern int NtQueryWnfStateData(ref ulong stateName, IntPtr typeId, IntPtr explicitScope,
        out uint changeStamp, byte[] buffer, ref uint bufferSize);

    // WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED: 0 = off, otherwise Do Not Disturb / Focus is on.
    private const ulong WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED = 0x0D83063EA3BF1C75;

    /// <summary>Do Not Disturb / Focus state, or null when it can't be read.</summary>
    public static bool? IsDoNotDisturbOn()
    {
        try
        {
            ulong name = WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED;
            var buffer = new byte[16];
            uint size = (uint)buffer.Length;
            if (NtQueryWnfStateData(ref name, IntPtr.Zero, IntPtr.Zero, out _, buffer, ref size) != 0 || size < 4) return null;
            return BitConverter.ToInt32(buffer, 0) != 0;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads a POWERBROADCAST_SETTING { GUID PowerSetting; DWORD DataLength; UCHAR Data[] } with DWORD data.</summary>
    public static bool TryReadPowerSetting(IntPtr lParam, out Guid setting, out int value)
    {
        setting = Marshal.PtrToStructure<Guid>(lParam);
        int length = Marshal.ReadInt32(lParam, 16);
        value = length >= 4 ? Marshal.ReadInt32(lParam, 20) : 0;
        return length >= 4;
    }

    public static bool IsCapsLockOn() => (GetKeyState(VK_CAPITAL) & 1) != 0;

    public static bool IsFullscreenAppRunning()
    {
        if (SHQueryUserNotificationState(out int state) != 0) return false;
        return state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
    }

    /// <summary>Keyboard layout language of the window the user is typing in.</summary>
    public static (IntPtr Window, int LanguageId) ForegroundKeyboardLanguage()
    {
        var hwnd = GetForegroundWindow();
        uint thread = GetWindowThreadProcessId(hwnd, out _);
        return (hwnd, (int)((long)GetKeyboardLayout(thread) & 0xFFFF));
    }
}
