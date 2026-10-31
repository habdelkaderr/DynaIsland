using System.IO;
using System.Windows.Threading;

namespace DynaIsland.Services;

/// <summary>
/// Watches the Downloads folder for browser downloads (Chrome/Edge .crdownload, Firefox/Zen .part)
/// and the Screenshots folder for new screenshots.
/// </summary>
public sealed class FolderWatchService : IDisposable
{
    private static readonly string[] PartialExtensions = [".crdownload", ".part", ".partial", ".download", ".opdownload"];
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];

    private readonly Dispatcher _ui;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, DateTime> _announced = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A download started (file name without the partial extension). UI thread.</summary>
    public event EventHandler<string>? DownloadStarted;

    /// <summary>A download finished (full path). UI thread.</summary>
    public event EventHandler<string>? DownloadCompleted;

    /// <summary>A screenshot was saved (full path). UI thread.</summary>
    public event EventHandler<string>? ScreenshotSaved;

    public FolderWatchService(Dispatcher ui)
    {
        _ui = ui;
        if (Native.KnownFolder(Native.FOLDERID_Downloads) is { } downloads) WatchDownloads(downloads);
        if (Native.KnownFolder(Native.FOLDERID_Screenshots) is { } screenshots) WatchScreenshots(screenshots);
    }

    private void WatchDownloads(string folder)
    {
        var w = Create(folder, NotifyFilters.FileName);
        if (w is null) return;
        w.Created += (_, e) => Post(() => OnPartial(e.FullPath));
        w.Renamed += (_, e) => Post(() =>
        {
            if (IsPartial(e.FullPath)) OnPartial(e.FullPath);
            else if (IsPartial(e.OldFullPath)) DownloadCompleted?.Invoke(this, e.FullPath);
        });
    }

    private void WatchScreenshots(string folder)
    {
        var w = Create(folder, NotifyFilters.FileName);
        if (w is null) return;
        w.Created += (_, e) =>
        {
            if (ImageExtensions.Contains(Path.GetExtension(e.FullPath), StringComparer.OrdinalIgnoreCase))
                Post(() => ScreenshotSaved?.Invoke(this, e.FullPath));
        };
    }

    private void OnPartial(string path)
    {
        if (!IsPartial(path)) return;
        string name = Path.GetFileNameWithoutExtension(path);
        if (name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase)) return; // Chrome renames it right away

        if (_announced.TryGetValue(name, out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(30)) return;
        _announced[name] = DateTime.UtcNow;
        DownloadStarted?.Invoke(this, name);
    }

    private static bool IsPartial(string path) =>
        PartialExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? Create(string folder, NotifyFilters filters)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;
            var w = new FileSystemWatcher(folder) { NotifyFilter = filters, IncludeSubdirectories = false };
            w.Error += (_, e) => Log.Error("folder watch", e.GetException());
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
            return w;
        }
        catch (Exception ex)
        {
            Log.Error("folder watch", ex);
            return null;
        }
    }

    private void Post(Action action) => _ui.InvokeAsync(action);

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
    }
}
