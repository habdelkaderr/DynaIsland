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

    /// <summary>
    /// Switches Windows' Do Not Disturb. There is no public API for it, so this presses the real
    /// "Do not disturb" button in Windows' notification center (UI Automation) and closes it again.
    /// Returns false if the button couldn't be found; the center is then left open for the user.
    /// </summary>
    public static async Task<bool> ToggleDoNotDisturbAsync()
    {
        bool? before = Native.IsDoNotDisturbOn();
        OpenNotificationCenter();
        bool pressed = await Task.Run(() =>
        {
            for (int attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    var center = System.Windows.Automation.AutomationElement.RootElement.FindFirst(System.Windows.Automation.TreeScope.Children,
                        new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.NameProperty, "Notification Center"));
                    var button = center?.FindFirst(System.Windows.Automation.TreeScope.Descendants,
                        new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.NameProperty, "Do not disturb"));
                    if (button is not null)
                    {
                        if (button.TryGetCurrentPattern(System.Windows.Automation.TogglePattern.Pattern, out var toggle))
                            ((System.Windows.Automation.TogglePattern)toggle).Toggle();
                        else if (button.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out var invoke))
                            ((System.Windows.Automation.InvokePattern)invoke).Invoke();
                        else return false;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("dnd", ex);
                }
                Thread.Sleep(100);
            }
            return false;
        });
        if (!pressed) return false;

        await Task.Delay(250);
        Native.SendKeys(0x1B); // Esc closes the notification center
        await Task.Delay(400);
        return Native.IsDoNotDisturbOn() != before;
    }

    private static bool Confirm(string text, string title) =>
        MessageBox.Show(text, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll")]
    private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);
}
