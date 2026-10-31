namespace DynaIsland.Services;

public enum TimerKind
{
    Timer,
    Focus,
    Stopwatch,
}

/// <summary>A single countdown timer, focus session or stopwatch.</summary>
public sealed class TimerService
{
    private DateTime _startedUtc;
    private TimeSpan _accumulated;
    private bool _running;

    public TimerKind Kind { get; private set; }
    public TimeSpan Duration { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsPaused => IsActive && !_running;

    public TimeSpan Elapsed => _accumulated + (_running ? DateTime.UtcNow - _startedUtc : TimeSpan.Zero);

    public TimeSpan Remaining
    {
        get
        {
            var left = Duration - Elapsed;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>What the island shows: time left for timers, time elapsed for the stopwatch.</summary>
    public TimeSpan Display => Kind == TimerKind.Stopwatch ? Elapsed : Remaining;

    /// <summary>0..1 ring progress. The stopwatch ring sweeps once a minute.</summary>
    public double Progress => Kind == TimerKind.Stopwatch
        ? Elapsed.TotalSeconds % 60 / 60
        : Duration > TimeSpan.Zero ? Math.Clamp(Elapsed / Duration, 0, 1) : 0;

    public string Label => Kind switch
    {
        TimerKind.Focus => "Focus",
        TimerKind.Stopwatch => "Stopwatch",
        _ => "Timer",
    };

    public void Start(TimerKind kind, TimeSpan duration = default)
    {
        Kind = kind;
        Duration = duration;
        _accumulated = TimeSpan.Zero;
        _startedUtc = DateTime.UtcNow;
        _running = true;
        IsActive = true;
    }

    public void Pause()
    {
        if (!_running) return;
        _accumulated = Elapsed;
        _running = false;
    }

    public void Resume()
    {
        if (!IsActive || _running) return;
        _startedUtc = DateTime.UtcNow;
        _running = true;
    }

    public void AddMinute()
    {
        if (IsActive && Kind != TimerKind.Stopwatch) Duration += TimeSpan.FromMinutes(1);
    }

    public void Stop()
    {
        IsActive = false;
        _running = false;
    }

    /// <summary>Stops and returns true once a countdown reaches zero.</summary>
    public bool CheckFinished()
    {
        if (!IsActive || !_running || Kind == TimerKind.Stopwatch || Remaining > TimeSpan.Zero) return false;
        Stop();
        return true;
    }

    public static string Format(TimeSpan t, bool roundUp)
    {
        if (roundUp) t = TimeSpan.FromSeconds(Math.Ceiling(t.TotalSeconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
