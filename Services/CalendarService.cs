using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DynaIsland.Services;

public sealed record Meeting(string Subject, string Location, DateTime Start, DateTime End, int MinutesAway)
{
    public bool IsNow => MinutesAway <= 0 && End > DateTime.Now;
}

/// <summary>
/// Calendar from classic Outlook (local COM, only while Outlook is already running; it is never
/// launched): the next events for the Home card, plus heads-ups 10 minutes before and at start.
/// The new Outlook has no local API, but its reminders still arrive through notification mirroring.
/// </summary>
public sealed class CalendarService : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly Timer _timer;
    private readonly HashSet<string> _announced = [];
    private int _busy;

    /// <summary>Upcoming events (ongoing first), at most six, within the next 24 hours.</summary>
    public IReadOnlyList<Meeting> Events { get; private set; } = [];

    /// <summary>Whether classic Outlook was running at the last check.</summary>
    public bool OutlookRunning { get; private set; }

    /// <summary>Raised on the UI thread 10 minutes before and when a meeting starts.</summary>
    public event EventHandler<Meeting>? Upcoming;

    /// <summary>Raised on the UI thread when <see cref="Events"/> or <see cref="OutlookRunning"/> change.</summary>
    public event EventHandler? EventsChanged;

    public CalendarService(Dispatcher ui)
    {
        _ui = ui;
        _timer = new Timer(_ => Check(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
    }

    private void Check()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            bool running = Process.GetProcessesByName("OUTLOOK").Length > 0;
            var events = running ? ReadEvents() : [];

            foreach (var meeting in events)
            {
                // One heads-up at <= 10 minutes and one at start.
                string stage = meeting.MinutesAway <= 0 && meeting.MinutesAway > -2 ? "now" : meeting.MinutesAway is > 0 and <= 10 ? "soon" : "";
                if (stage.Length == 0) continue;
                if (!_announced.Add($"{meeting.Subject}|{meeting.Start:O}|{stage}")) continue;
                _ui.InvokeAsync(() => Upcoming?.Invoke(this, meeting));
            }

            _ui.InvokeAsync(() =>
            {
                bool changed = running != OutlookRunning || !events.SequenceEqual(Events);
                OutlookRunning = running;
                Events = events;
                if (changed) EventsChanged?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception ex)
        {
            Log.Error("calendar", ex);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private static List<Meeting> ReadEvents()
    {
        var result = new List<Meeting>();
        if (GetActive("Outlook.Application") is not { } outlook) return result;

        dynamic app = outlook;
        dynamic ns = app.GetNamespace("MAPI");
        dynamic folder = ns.GetDefaultFolder(9); // olFolderCalendar
        dynamic items = folder.Items;
        items.IncludeRecurrences = true;
        items.Sort("[Start]");

        var now = DateTime.Now;
        var culture = CultureInfo.CurrentCulture; // Outlook parses Restrict dates in the user's locale
        string filter = $"[End] >= '{now.ToString("g", culture)}' AND [Start] <= '{now.AddHours(24).ToString("g", culture)}'";
        dynamic found = items.Restrict(filter);

        dynamic? item = found.GetFirst();
        int guard = 0;
        while (item is not null && guard++ < 40 && result.Count < 6)
        {
            if (!(bool)item.AllDayEvent)
            {
                DateTime start = item.Start;
                DateTime end = item.End;
                int minutes = (int)Math.Ceiling((start - now).TotalMinutes);
                result.Add(new Meeting((string)(item.Subject ?? ""), (string)(item.Location ?? ""), start, end, minutes));
            }
            item = found.GetNext();
        }
        return result;
    }

    private static object? GetActive(string progId)
    {
        if (CLSIDFromProgID(progId, out var clsid) != 0) return null;
        return GetActiveObject(ref clsid, IntPtr.Zero, out var obj) == 0 ? obj : null;
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object obj);

    public void Dispose() => _timer.Dispose();
}
