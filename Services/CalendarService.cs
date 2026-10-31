using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DynaIsland.Services;

public sealed record Meeting(string Subject, string Location, DateTime Start, int MinutesAway);

/// <summary>
/// Upcoming-meeting heads-up from classic Outlook's calendar (local COM, only while Outlook is already
/// running; it is never launched). The new Outlook has no local API, but its reminders still arrive
/// through notification mirroring.
/// </summary>
public sealed class CalendarService : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly Timer _timer;
    private readonly HashSet<string> _announced = [];
    private int _busy;

    /// <summary>Raised on the UI thread 10 minutes before and when a meeting starts.</summary>
    public event EventHandler<Meeting>? Upcoming;

    public CalendarService(Dispatcher ui)
    {
        _ui = ui;
        _timer = new Timer(_ => Check(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
    }

    private void Check()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (Process.GetProcessesByName("OUTLOOK").Length == 0) return;
            foreach (var meeting in ReadUpcoming())
            {
                // One heads-up at <= 10 minutes and one at start.
                string stage = meeting.MinutesAway <= 0 ? "now" : meeting.MinutesAway <= 10 ? "soon" : "";
                if (stage.Length == 0) continue;
                if (!_announced.Add($"{meeting.Subject}|{meeting.Start:O}|{stage}")) continue;
                _ui.InvokeAsync(() => Upcoming?.Invoke(this, meeting));
            }
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

    private static List<Meeting> ReadUpcoming()
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
        string filter = $"[Start] >= '{now.AddMinutes(-2).ToString("g", culture)}' AND [Start] <= '{now.AddMinutes(11).ToString("g", culture)}'";
        dynamic found = items.Restrict(filter);

        dynamic? item = found.GetFirst();
        int guard = 0;
        while (item is not null && guard++ < 25)
        {
            if (!(bool)item.AllDayEvent)
            {
                DateTime start = item.Start;
                int minutes = (int)Math.Ceiling((start - now).TotalMinutes);
                result.Add(new Meeting((string)(item.Subject ?? ""), (string)(item.Location ?? ""), start, minutes));
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
