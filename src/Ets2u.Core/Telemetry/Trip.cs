using System.Globalization;

namespace Ets2u.Core.Telemetry;

/// <summary>One safety event of a trip, copied so the UI never reads what the reader thread is changing.</summary>
public sealed record TripEvent(
    DrivingEventKind Kind, AlertLevel Level, DateTime Started, DateTime? Ended,
    string Category, string Variant, string Detail, double Peak, double StartKmh, double EndKmh, bool Coach)
{
    /// <summary>How long it lasted, or so far.</summary>
    public TimeSpan Duration(DateTime now) => (Ended ?? now) - Started;
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
    /// Null until 10 game minutes have been driven (one event in a few seconds says nothing).
    /// </summary>
    public double? Score => DrivingTime < TimeSpan.FromMinutes(10) ? null
        : (Count(AlertLevel.Yellow) + 2.0 * Count(AlertLevel.Red)) / DrivingTime.TotalHours * 10;

    /// <summary>"1 h 44 min driving (6 min 27 s real)".</summary>
    public string DrivingText => $"{FormatDuration(DrivingTime)} driving" +
                                 (RealDrivingTime is { } real && real != DrivingTime ? $" ({FormatDuration(real)} real)" : "");

    /// <summary>"97.0 km (6.0 km driven)".</summary>
    public string DistanceText => string.Create(CultureInfo.InvariantCulture, $"{DistanceKm:0.0} km") +
                                  (RealDistanceKm is { } real && Math.Abs(real - DistanceKm) > 0.05
                                      ? string.Create(CultureInfo.InvariantCulture, $" ({real:0.0} km driven)") : "");

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
            Score is { } s ? string.Create(c, $"Safety score {s:0} ({Band(s)?.ToString() ?? "Green"})") : "Safety score: not enough driving yet",
            "",
            "Safety events",
        };
        lines.AddRange(Events.Count == 0 ? ["None"] : Events.Select(e =>
            string.Create(c, $"{e.Started.ToLocalTime():HH:mm:ss}  {e.Level,-6}  {e.Category} - {e.Variant} ({e.Detail})")));
        return string.Join(Environment.NewLine, lines);
    }

    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{t.Minutes} min {t.Seconds:00} s";
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

    /// <summary>Starts a new trip, dropping the last one.</summary>
    public void Start(DateTime now)
    {
        lock (_lock)
        {
            _events.Clear();
            _started = now;
            _stopped = null;
            _lastFrame = null;
            _driving = _realDriving = TimeSpan.Zero;
            _distanceKm = _realDistanceKm = _topKmh = 0;
            _coach = _truck = false;
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
                if (!_events.Any(x => ReferenceEquals(x.Event, e))) _events.Add((e, coach));
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

    /// <summary>The trip so far (or as it was stopped); null before the first Start.</summary>
    public TripSummary? Snapshot()
    {
        lock (_lock)
        {
            if (!_any) return null;
            var events = _events
                .Select(x => new TripEvent(x.Event.Kind, x.Event.Level, x.Event.Started, x.Event.Ended, x.Event.Category,
                    x.Event.Variant, x.Event.Detail, x.Event.Peak, x.Event.StartKmh, x.Event.EndKmh, x.Coach))
                .OrderBy(e => e.Started)
                .ToList();
            return new TripSummary(_started, _stopped, _driving, _distanceKm, _topKmh, _coach, _truck, events, _realDriving, _realDistanceKm);
        }
    }
}
