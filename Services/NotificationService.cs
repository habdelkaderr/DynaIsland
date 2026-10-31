using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using System.Xml.Linq;

namespace DynaIsland.Services;

public sealed record ToastInfo(string AppId, string Title, string Body);

/// <summary>
/// Mirrors new Windows toast notifications by reading the local notification database
/// (%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db) read-only, through Windows'
/// own signed SQLite (winsqlite3.dll). The notification service keeps the file open, so change
/// events are unreliable; a light check (one MAX() query, ~1 ms) runs every 1.5 s as well.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications");
    private static readonly string DbPath = Path.Combine(Folder, "wpndatabase.db");

    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _poll;
    private readonly HashSet<long> _seenIds = [];
    private FileSystemWatcher? _watcher;
    private long _lastOrder = -1;
    private int _busy;

    /// <summary>Raised on the UI thread for each new toast.</summary>
    public event EventHandler<ToastInfo>? Received;

    public NotificationService(Dispatcher ui)
    {
        _ui = ui;
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Normal,
            (_, _) => { _debounce!.Stop(); _ = PollAsync(); }, ui);
        _debounce.Stop();
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) => _ = PollAsync(), ui);
        _poll.Stop();

        if (!File.Exists(DbPath)) return;
        _ = PollAsync(); // establishes the starting point; existing toasts are not replayed
        _poll.Start();

        try
        {
            _watcher = new FileSystemWatcher(Folder, "wpndatabase.db*")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Changed += (_, _) => _ui.InvokeAsync(() => { _debounce.Stop(); _debounce.Start(); });
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Error("notifications watch", ex);
        }
    }

    private async Task PollAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            long since = _lastOrder;
            if (since >= 0 && await Task.Run(MaxOrder) <= since) return; // nothing new: the common, cheap path
            var (rows, maxOrder) = await Task.Run(() => Query(since));
            bool first = _lastOrder < 0;
            if (maxOrder > _lastOrder) _lastOrder = maxOrder;
            if (first) return;

            foreach (var row in rows)
            {
                if (!_seenIds.Add(row.Id)) continue; // updates to an existing toast (progress etc.)
                if (Parse(row.AppId, row.Payload) is { } toast) Received?.Invoke(this, toast);
            }
        }
        catch (Exception ex)
        {
            Log.Error("notifications", ex);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private sealed record Row(long Id, string AppId, string Payload);

    private static long MaxOrder()
    {
        using var db = Sqlite.OpenReadOnly(DbPath);
        using var s = db?.Prepare("SELECT IFNULL(MAX([Order]), 0) FROM Notification");
        return s?.Step() == true ? s.Int64(0) : -1;
    }

    private static (List<Row> Rows, long MaxOrder) Query(long since)
    {
        var rows = new List<Row>();
        long max = since;
        using var db = Sqlite.OpenReadOnly(DbPath);
        if (db is null) return (rows, max);

        if (since < 0)
        {
            using var s = db.Prepare("SELECT IFNULL(MAX([Order]), 0) FROM Notification");
            if (s?.Step() == true) max = s.Int64(0);
            return (rows, max);
        }

        // Only toasts that arrived in the last two minutes, so nothing old is replayed after a pause.
        long recent = DateTime.UtcNow.AddMinutes(-2).ToFileTimeUtc();
        using var st = db.Prepare(
            "SELECT n.[Order], n.Id, h.PrimaryId, n.Payload FROM Notification n " +
            "JOIN NotificationHandler h ON h.RecordId = n.HandlerId " +
            "WHERE n.Type = 'toast' AND n.[Order] > ?1 AND IFNULL(n.ArrivalTime, 0) >= ?2 ORDER BY n.[Order] LIMIT 20");
        if (st is null) return (rows, max);
        st.Bind(1, since);
        st.Bind(2, recent);
        while (st.Step())
        {
            max = Math.Max(max, st.Int64(0));
            rows.Add(new Row(st.Int64(1), st.Text(2), st.Text(3)));
        }

        // Advance past rows we skipped as too old as well.
        using var m = db.Prepare("SELECT IFNULL(MAX([Order]), 0) FROM Notification");
        if (m?.Step() == true) max = Math.Max(max, m.Int64(0));
        return (rows, max);
    }

    private static ToastInfo? Parse(string appId, string payload)
    {
        try
        {
            var doc = XDocument.Parse(payload);
            var binding = doc.Descendants("binding").FirstOrDefault();
            if (binding is null) return null;
            var texts = binding.Elements("text")
                .Where(t => (string?)t.Attribute("placement") != "attribution")
                .Select(t => t.Value.Trim())
                .Where(t => t.Length > 0)
                .ToList();
            if (texts.Count == 0) return null;
            return new ToastInfo(appId, texts[0], string.Join(" · ", texts.Skip(1)));
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _poll.Stop();
        _watcher?.Dispose();
    }

    // ---- Minimal read-only wrapper over winsqlite3.dll (ships signed with Windows) ------------

    private sealed class Sqlite : IDisposable
    {
        private const int SQLITE_OPEN_READONLY = 0x1;
        private const int SQLITE_ROW = 100;
        private IntPtr _db;

        private Sqlite(IntPtr db) => _db = db;

        public static Sqlite? OpenReadOnly(string path)
        {
            if (sqlite3_open_v2(Utf8(path), out var db, SQLITE_OPEN_READONLY, IntPtr.Zero) != 0)
            {
                sqlite3_close(db);
                return null;
            }
            sqlite3_busy_timeout(db, 500);
            return new Sqlite(db);
        }

        public Statement? Prepare(string sql) =>
            sqlite3_prepare_v2(_db, Utf8(sql), -1, out var st, IntPtr.Zero) == 0 ? new Statement(st) : null;

        public void Dispose()
        {
            if (_db != IntPtr.Zero) sqlite3_close(_db);
            _db = IntPtr.Zero;
        }

        public sealed class Statement(IntPtr st) : IDisposable
        {
            public bool Step() => sqlite3_step(st) == SQLITE_ROW;
            public void Bind(int index, long value) => sqlite3_bind_int64(st, index, value);
            public long Int64(int col) => sqlite3_column_int64(st, col);

            public string Text(int col)
            {
                int n = sqlite3_column_bytes(st, col);
                if (n <= 0) return "";
                var bytes = new byte[n];
                Marshal.Copy(sqlite3_column_blob(st, col), bytes, 0, n);
                return Encoding.UTF8.GetString(bytes);
            }

            public void Dispose() => sqlite3_finalize(st);
        }

        private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

        [DllImport("winsqlite3.dll")] private static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr st, IntPtr tail);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_step(IntPtr st);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_bind_int64(IntPtr st, int index, long value);
        [DllImport("winsqlite3.dll")] private static extern long sqlite3_column_int64(IntPtr st, int col);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_column_bytes(IntPtr st, int col);
        [DllImport("winsqlite3.dll")] private static extern IntPtr sqlite3_column_blob(IntPtr st, int col);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_finalize(IntPtr st);
    }
}
