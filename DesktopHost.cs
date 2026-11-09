using DynaIsland.Services;

namespace DynaIsland;

/// <summary>
/// What the desktop surfaces (menu bar, Control Center, Launchpad, dock, Settings) share: the existing
/// services and a few app-level actions. The island owns the services; nothing is duplicated.
/// </summary>
internal sealed class DesktopHost
{
    public required Settings Settings { get; init; }
    public required MediaService Media { get; init; }
    public required NetworkService Network { get; init; }
    public required PowerService Power { get; init; }
    public required AudioVolume Volume { get; init; }
    public required RadioService Radios { get; init; }
    public required WinEvents Events { get; init; }
    public required Func<IntPtr> Monitor { get; init; }

    public required Action OpenLaunchpad { get; init; }
    public required Action ToggleControlCenter { get; init; }
    public required Action OpenSettings { get; init; }
    public required Action<string> OpenSettingsPage { get; init; }
    public required Action ApplyModules { get; init; }
    public required Action Quit { get; init; }

    // Live state pushed by the island's existing watchers.
    public float VolumeLevel { get; private set; }
    public bool Muted { get; private set; }
    public int KeyboardLanguage { get; private set; }
    public bool UnreadNotifications { get; set; }

    public event Action? VolumeChanged;
    public event Action? KeyboardChanged;
    public event Action? NotificationsChanged;

    public void ReportVolume(float level, bool muted)
    {
        if (Math.Abs(level - VolumeLevel) < 0.001 && muted == Muted) return;
        VolumeLevel = level;
        Muted = muted;
        VolumeChanged?.Invoke();
    }

    public void ReportKeyboard(int languageId)
    {
        if (languageId == KeyboardLanguage || languageId == 0) return;
        KeyboardLanguage = languageId;
        KeyboardChanged?.Invoke();
    }

    public void ReportNotification()
    {
        UnreadNotifications = true;
        NotificationsChanged?.Invoke();
    }

    public void ClearNotifications()
    {
        UnreadNotifications = false;
        NotificationsChanged?.Invoke();
    }
}
