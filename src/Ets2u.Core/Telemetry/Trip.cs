using System.Globalization;

namespace Ets2u.Core.Telemetry;

/// <summary>One safety event of a trip, copied so the UI never reads what the reader thread is changing.</summary>
/// <param name="GameStarted">The game's clock when it started (see <see cref="GameClock"/>); null when not known (saved before 0.12.5).</param>
/// <param name="Scale">The game's time scale when it started; null when not known.</param>
public sealed record TripEvent(
    DrivingEventKind Kind, AlertLevel Level, DateTime Started, DateTime? Ended,
    string Category, string Variant, string Detail, double Peak, double StartKmh, double EndKmh, bool Coach,
    uint? GameStarted = null, double? Scale = null)
{
    /// <summary>How long it lasted, or so far (real time).</summary>
    public TimeSpan Duration(DateTime now) => (Ended ?? now) - Started;

    /// <summary>"Wed 14:32 in game · 20:15:07 real", or just the real time when the game's clock isn't known.</summary>
    public string TimeText(string realFormat = "HH:mm:ss")
    {
        var real = Started.ToLocalTime().ToString(realFormat, CultureInfo.InvariantCulture);
        return GameStarted is { } game ? $"{GameClock.Format(game)} in game · {real} real" : real;
    }

    /// <summary>"1 min 16 s in game · 4.0 s real" (or "... so far" while it lasts); just the real one without a scale.</summary>
    public string DurationText(DateTime now)
    {
        var lasted = Duration(now);
        var real = string.Create(CultureInfo.InvariantCulture, $"{lasted.TotalSeconds:0.0} s");
        var text = Scale is { } scale && Math.Abs(scale - 1) > 0.01
            ? $"{TripSummary.FormatDuration(lasted * scale)} in game · {real} real"
            : real;
        return Ended is null ? text + " so far" : text;
    }
}

/// <summary>
/// A captured trip as it stands: totals and every safety event, oldest first. Driving time and distance
/// are in the game's terms (what its clock and its job screens show): ETS2's map is smaller than the world
/// it stands for, and its clock faster, by the game's time scale (about 19 on roads, less in towns). The
/// real ones, as you sat at the wheel, are kept beside them.
/// </summary>
/// <param name="RealDrivingTime">Null in a summary saved before 0.12.1, which only had the real figures.</param>
public sealed record TripSummary(
    DateTime Started, DateTime? Stopped, TimeSpan DrivingTime, double DistanceKm, double TopKmh,
    bool InCoach, bool InTruck, IReadOnlyList<TripEvent> Events,
    TimeSpan? RealDrivingTime = null, double? RealDistanceKm = null)
{
    public bool Capturing => Stopped is null;

    public int Count(DrivingEventKind kind, AlertLevel level) => Events.Count(e => e.Kind == kind && e.Level == level);
    public int Count(AlertLevel level) => Events.Count(e => e.Level == level);

    /// <summary>
    /// GreenRoad's way of scoring: events per 10 hours of driving, a red one counting as two yellows. The
    /// hours are the game's: a route packs a real drive's corners and junctions into a few real minutes,
    /// so real minutes would make every trip look many times worse than the same drive on a real road.
    /// A trip shorter than <see cref="ScoreFloor"/> counts as that long, the rest as clean driving: one
    /// event in a few minutes would otherwise scale up to hundreds. Longer trips score on their own hours.
    /// </summary>
    public double Score =>
        (Count(AlertLevel.Yellow) + 2.0 * Count(AlertLevel.Red)) / Math.Max(DrivingTime.TotalHours, ScoreFloor.TotalHours) * 10;

    /// <summary>The least driving (game time) a score is worked out over.</summary>
    public static readonly TimeSpan ScoreFloor = TimeSpan.FromHours(1);

    /// <summary>The summary without one event (a saved job's, taken off by hand).</summary>
    public TripSummary Without(DateTime started, DrivingEventKind kind) =>
        this with { Events = Events.Where(e => e.Started != started || e.Kind != kind).ToList() };

    /// <summary>"1 h 44 min driving (6 min 27 s real)".</summary>
    public string DrivingText => $"{FormatDuration(DrivingTime)} driving" +
                                 (RealDrivingTime is { } real && real != DrivingTime ? $" ({FormatDuration(real)} real)" : "");

    /// <summary>"97.0 km", in the game's terms (the real distance is kept, not shown).</summary>
    public string DistanceText => string.Create(CultureInfo.InvariantCulture, $"{DistanceKm:0.0} km");

    /// <summary>Green under 20, yellow under 40, red from 40 (GreenRoad's bands).</summary>
    public static AlertLevel? Band(double score) => score < 20 ? null : score < 40 ? AlertLevel.Yellow : AlertLevel.Red;

    /// <summary>A plain-text copy of the summary, for pasting anywhere.</summary>
    public string ToText()
    {
        var c = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            string.Create(c, $"Trip summary, {Started.ToLocalTime():yyyy-MM-dd HH:mm} to {(Stopped ?? DateTime.UtcNow).ToLocalTime():HH:mm}"),
            string.Create(c, $"{DrivingText} · {DistanceText} · top speed {TopKmh:0} km/h"),
            string.Create(c, $"Safety score {Score:0} ({Band(Score)?.ToString() ?? "Green"})"),
            "",
            "Safety events",
        };
        lines.AddRange(Events.Count == 0 ? ["None"] : Events.Select(e =>
            string.Create(c, $"{e.TimeText()}  {e.Level,-6}  {e.Category} - {e.Variant} ({e.Detail})")));
        return string.Join(Environment.NewLine, lines);
    }

    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{t.Minutes} min {t.Seconds:00} s";
}

public enum TripSort { Time, Type, Level, Duration }

/// <summary>The order the safety events list is shown in.</summary>
public static class TripEventOrder
{
    /// <summary>
    /// By time; by type (acceleration, braking, cornering, speeding); by level (yellow, red); or by how long
    /// each lasted. Ties go by time, oldest first, whichever way the list runs.
    /// </summary>
    public static IReadOnlyList<TripEvent> Sort(IEnumerable<TripEvent> events, TripSort by, bool descending, DateTime now)
    {
        Func<TripEvent, IComparable> key = by switch
        {
            TripSort.Type => e => e.Kind,
            TripSort.Level => e => e.Level == AlertLevel.Red ? 1 : 0,
            TripSort.Duration => e => e.Duration(now),
            _ => e => e.Started,
        };
        var ordered = descending ? events.OrderByDescending(key) : events.OrderBy(key);
        return ordered.ThenBy(e => e.Started).ThenBy(e => e.Kind).ToList();
    }
}

/// <summary>
/// Captures a trip between Start and Stop: driving time (while moving), distance, top speed and the safety
/// events the detector finds. Fed by the telemetry reader thread; read by the UI. Thread-safe.
/// </summary>
public sealed class TripRecorder
{
    /// <summary>Slower than this (km/h) is standing, not driving.</summary>
    const double MovingKmh = 1;
    /// <summary>A longer gap between frames (paused, loading, closed) isn't driven.</summary>
    static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(1);

    readonly object _lock = new();
    readonly List<(DrivingEvent Event, bool Coach)> _events = new();
    /// <summary>Events taken off the list; one still going on would otherwise come back when it turns red.</summary>
    readonly HashSet<DrivingEvent> _removed = new(ReferenceEqualityComparer.Instance);
    /// <summary>Events of a summary this trip carries on from (saved before the app was closed).</summary>
    readonly List<TripEvent> _restored = new();
    DateTime _started;
    DateTime? _stopped;
    DateTime? _lastFrame;
    TimeSpan _driving, _realDriving;
    double _distanceKm, _realDistanceKm, _topKmh;
    bool _coach, _truck, _any;

    public bool Capturing
    {
        get { lock (_lock) return _any && _stopped is null; }
    }

    /// <summary>When the trip started and stopped, or null before the first Start: cheap, for telling one trip from the next.</summary>
    public (DateTime Started, DateTime? Stopped)? Bounds
    {
        get { lock (_lock) return _any ? (_started, _stopped) : null; }
    }

    /// <summary>Starts a new trip, dropping the last one.</summary>
    public void Start(DateTime now)
    {
        lock (_lock)
        {
            _events.Clear();
            _removed.Clear();
            _restored.Clear();
            _started = now;
            _stopped = null;
            _lastFrame = null;
            _driving = _realDriving = TimeSpan.Zero;
            _distanceKm = _realDistanceKm = _topKmh = 0;
            _coach = _truck = false;
            _any = true;
        }
    }

    /// <summary>Carries on from a summary saved earlier: its totals and events, still capturing.</summary>
    public void Resume(TripSummary saved)
    {
        lock (_lock)
        {
            _events.Clear();
            _removed.Clear();
            _restored.Clear();
            _restored.AddRange(saved.Events);
            _started = saved.Started;
            _stopped = null;
            _lastFrame = null;
            _driving = saved.DrivingTime;
            _realDriving = saved.RealDrivingTime ?? saved.DrivingTime;
            _distanceKm = saved.DistanceKm;
            _realDistanceKm = saved.RealDistanceKm ?? saved.DistanceKm;
            _topKmh = saved.TopKmh;
            _coach = saved.InCoach;
            _truck = saved.InTruck;
            _any = true;
        }
    }

    public void Stop(DateTime now)
    {
        lock (_lock)
        {
            if (_any && _stopped is null) _stopped = now;
        }
    }

    /// <summary>
    /// One reader step: the speed in m/s, or null while the game is paused or closed, and the game's time
    /// scale (game seconds per real second, the plugin's local.scale).
    /// </summary>
    public void Step(DateTime now, float? speedMs, bool coach, IEnumerable<DrivingEvent> news, double scale = 1)
    {
        lock (_lock)
        {
            if (!_any || _stopped is not null) return;
            foreach (var e in news)
                if (!_removed.Contains(e) && !_events.Any(x => ReferenceEquals(x.Event, e))) _events.Add((e, coach));
            if (speedMs is not { } ms)
            {
                _lastFrame = null;
                return;
            }
            var kmh = Math.Abs(ms) * 3.6;
            if (_lastFrame is { } last && now - last is var dt && dt > TimeSpan.Zero && dt <= MaxStep && kmh >= MovingKmh)
            {
                _realDriving += dt;
                _realDistanceKm += kmh * dt.TotalHours;
                _driving += dt * scale;
                _distanceKm += kmh * dt.TotalHours * scale;
            }
            _lastFrame = now;
            if (kmh >= MovingKmh)
            {
                _topKmh = Math.Max(_topKmh, kmh);
                if (coach) _coach = true; else _truck = true;
            }
        }
    }

    /// <summary>Takes an event off the trip (it no longer counts in the score); false when there's no such event.</summary>
    public bool Remove(DateTime started, DrivingEventKind kind)
    {
        lock (_lock)
        {
            var r = _restored.FindIndex(e => e.Started == started && e.Kind == kind);
            if (r >= 0)
            {
                _restored.RemoveAt(r);
                return true;
            }
            var i = _events.FindIndex(x => x.Event.Started == started && x.Event.Kind == kind);
            if (i < 0) return false;
            _removed.Add(_events[i].Event);
            _events.RemoveAt(i);
            return true;
        }
    }

    /// <summary>The trip so far (or as it was stopped); null before the first Start.</summary>
    public TripSummary? Snapshot()
    {
        lock (_lock)
        {
            if (!_any) return null;
            var events = _events
                .Select(x => new TripEvent(x.Event.Kind, x.Event.Level, x.Event.Started, x.Event.Ended, x.Event.Category,
                    x.Event.Variant, x.Event.Detail, x.Event.Peak, x.Event.StartKmh, x.Event.EndKmh, x.Coach,
                    x.Event.GameStarted, x.Event.Scale))
                .Concat(_restored)
                .OrderBy(e => e.Started)
                .ToList();
            return new TripSummary(_started, _stopped, _driving, _distanceKm, _topKmh, _coach, _truck, events, _realDriving, _realDistanceKm);
        }
    }
}
