using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace DynaIsland.Services;

/// <summary>An app from Windows' Start list. <see cref="Id"/> is its shell:AppsFolder identity.</summary>
public sealed record InstalledApp(string Name, string Id, string? Exe, string Category)
{
    public string LaunchPath => @"shell:AppsFolder\" + Id;
    public BitmapSource? Icon(int size = 96) => ShellApps.Icon(LaunchPath, size);
}

/// <summary>
/// Installed applications, discovered from the same list Windows Start uses (no hard-coded paths),
/// grouped into simple categories. Loaded once and refreshed on demand. UI thread.
/// </summary>
public static class AppCatalog
{
    private static List<InstalledApp>? _apps;

    // Start also lists documents, websites and uninstallers; those aren't apps.
    private static readonly Regex NotAnApp = new(
        @"(^|\s)(uninstall|uninstaller|readme|read me|release notes|documentation|help|manual|license|website|changelog)(\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] NotAppExtensions = [".url", ".chm", ".txt", ".pdf", ".htm", ".html", ".rtf", ".ini", ".log", ".md", ".xml"];

    private static readonly (string Category, string[] Words)[] Categories =
    [
        ("AI", ["claude", "chatgpt", "copilot", "gemini", "perplexity", "ollama", "lm studio"]),
        ("Browsers", ["chrome", "edge", "firefox", "zen", "opera", "brave", "vivaldi", "browser", "arc"]),
        ("Development", ["visual studio", "code", "git", "terminal", "powershell", "python", "node", "docker", "antigravity",
                         "opencode", "intellij", "pycharm", "webstorm", "rider", "sublime", "notepad++", "cursor", "wsl", "ubuntu",
                         "sql", "postman", "android studio", "unity", "unreal", "dotnet", "developer"]),
        ("Communication", ["teams", "outlook", "mail", "telegram", "whatsapp", "discord", "slack", "zoom", "skype", "signal", "messenger", "phone link"]),
        ("Productivity", ["word", "excel", "powerpoint", "onenote", "notion", "obsidian", "acrobat", "pdf", "calendar", "to do",
                          "notes", "onedrive", "office", "access", "publisher", "project", "visio", "todo"]),
        ("Media", ["spotify", "vlc", "media player", "photos", "music", "video", "camera", "obs", "audacity", "netflix",
                   "clipchamp", "movies", "itunes", "apple music", "podcast", "sound recorder", "voice"]),
        ("Creativity", ["photoshop", "illustrator", "figma", "paint", "gimp", "blender", "canva", "inkscape", "premiere",
                        "after effects", "lightroom", "davinci", "krita", "snipping", "affinity"]),
        ("Games", ["steam", "epic games", "xbox", "game", "riot", "battle.net", "ea app", "ubisoft", "gog", "minecraft", "launcher"]),
        ("System", ["settings", "control panel", "task manager", "file explorer", "calculator", "clock", "security", "defender",
                    "powertoys", "7-zip", "winrar", "vmware", "virtualbox", "command prompt", "registry", "disk", "device",
                    "remote desktop", "windows tools", "snip", "lenovo", "nvidia", "intel", "realtek", "dolby", "backup",
                    "recovery", "magnifier", "narrator", "on-screen keyboard", "character map", "event viewer", "services"]),
    ];

    public static IReadOnlyList<InstalledApp> All => _apps ??= Load();

    public static void Refresh() => _apps = Load();

    /// <summary>Releases the list when neither the dock nor Launchpad needs it.</summary>
    public static void Unload() => _apps = null;

    public static IEnumerable<string> CategoryNames => Categories.Select(c => c.Category).Append("Other");

    public static InstalledApp? Find(string id) =>
        All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    public static InstalledApp? FindByName(string name) =>
        All.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a window belongs to this app (by AppUserModelID, or by exe path).</summary>
    public static bool Owns(InstalledApp app, AppWindow window) =>
        string.Equals(window.Aumid, app.Id, StringComparison.OrdinalIgnoreCase) ||
        (app.Exe is not null && string.Equals(window.Exe, app.Exe, StringComparison.OrdinalIgnoreCase));

    /// <summary>The installed app a window belongs to, if it's in Start.</summary>
    public static InstalledApp? ForWindow(AppWindow window) => All.FirstOrDefault(a => Owns(a, window));

    /// <summary>Starts an app and counts the launch (for "Frequently used").</summary>
    public static void Launch(InstalledApp app, Settings settings)
    {
        ShellApps.Start("explorer.exe", app.LaunchPath);
        settings.LaunchCounts[app.Id] = settings.LaunchCounts.GetValueOrDefault(app.Id) + 1;
        settings.Save();
    }

    private static List<InstalledApp> Load()
    {
        var result = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, id) in ShellApps.InstalledApps())
        {
            if (string.IsNullOrWhiteSpace(name) || NotAnApp.IsMatch(name) || !seen.Add(id)) continue;
            if (NotAppExtensions.Any(e => id.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;
            if (id.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
            string? exe = ResolveExe(id);
            result.Add(new InstalledApp(name, id, exe, Categorize(name, exe)));
        }
        return result.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Start IDs are an AUMID ("App_hash!App"), a known-folder path ("{GUID}\app.exe") or a full path.</summary>
    private static string? ResolveExe(string id)
    {
        try
        {
            if (id.Contains('!')) return null; // packaged app; matched by AUMID
            if (id.StartsWith('{') && id.IndexOf("}\\", StringComparison.Ordinal) is int end and > 0 &&
                Guid.TryParse(id[1..end], out var folder) && Native.KnownFolder(folder) is { } root)
            {
                string path = Path.Combine(root, id[(end + 2)..]);
                return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null;
            }
            if (Path.IsPathRooted(id)) return id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? id : null;
            string? target = ShellApps.LinkTarget(@"shell:AppsFolder\" + id);
            return target?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true ? target : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Categorize(string name, string? exe)
    {
        string haystack = $"{name} {Path.GetFileNameWithoutExtension(exe ?? "")}".ToLowerInvariant();
        foreach (var (category, words) in Categories)
        {
            foreach (var word in words)
            {
                if (Regex.IsMatch(haystack, $@"(^|[^a-z]){Regex.Escape(word)}([^a-z]|$)")) return category;
            }
        }
        return "Other";
    }
}
