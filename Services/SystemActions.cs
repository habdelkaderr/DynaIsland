using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace DynaIsland.Services;

/// <summary>Real Windows actions used by the menu bar and Control Center.</summary>
public static class SystemActions
{
    public static void OpenSettings(string page = "") => ShellApps.Start($"ms-settings:{page}");

    /// <summary>Windows' notification centre / calendar flyout (Win+N).</summary>
    public static void OpenNotificationCenter() => Native.SendKeys(0x5B /* LWin */, 0x4E /* N */);

    /// <summary>Windows' input switcher (Win+Space).</summary>
    public static void SwitchKeyboardLayout() => Native.SendKeys(0x5B, 0x20);

    public static void ShowDesktop() => Native.SendKeys(0x5B, 0x44 /* D */);

    public static void TaskManager() => ShellApps.Start("taskmgr.exe");

    public static void Lock() => LockWorkStation();

    public static void Sleep() => SetSuspendState(false, false, false);

    public static void Restart()
    {
        if (Confirm("Restart your PC now?\nUnsaved work in open apps may be lost.", "Restart"))
            ShellApps.Start("shutdown.exe", "/r /t 0");
    }

    public static void ShutDown()
    {
        if (Confirm("Shut down your PC now?\nUnsaved work in open apps may be lost.", "Shut Down"))
            ShellApps.Start("shutdown.exe", "/s /t 0");
    }

    public static void SignOut()
    {
        if (Confirm("Sign out of Windows now?\nUnsaved work in open apps may be lost.", "Sign Out"))
            ShellApps.Start("shutdown.exe", "/l");
    }

    /// <summary>Folder for a Go-menu entry (well-known name or a path).</summary>
    public static string? GoTarget(string item) => item switch
    {
        "Home" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "Documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Downloads" => Native.KnownFolder(Native.FOLDERID_Downloads),
        "Pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "Music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        "Videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "This PC" => "shell:MyComputerFolder",
        "Recycle Bin" => "shell:RecycleBinFolder",
        _ => Directory.Exists(item) ? item : null,
    };

    public static void Go(string item)
    {
        if (GoTarget(item) is { } target) ShellApps.Start("explorer.exe", $"\"{target}\"");
    }

    private static bool Confirm(string text, string title) =>
        MessageBox.Show(text, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll")]
    private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);
}
