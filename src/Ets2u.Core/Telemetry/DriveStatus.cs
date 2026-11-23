namespace Ets2u.Core.Telemetry;

/// <summary>How many events it takes for the traffic light to stay yellow or red.</summary>
public enum LightStrictness
{
    /// <summary>Yellow from 2 points, red from 4 (GreenRoad's bands over an hour).</summary>
    Realistic,
    /// <summary>Twice as many: yellow from 4 points, red from 8.</summary>
    Normal,
    /// <summary>Four times as many: yellow from 8 points, red from 16.</summary>
    Relaxed,
}

/// <summary>
/// The traffic light's steady colour: the points (yellow event 1, red 2) of the events in the last
/// <see cref="WindowMinutes"/> of driving, in game time, against fixed thresholds set by
/// <see cref="Strictness"/>. The thresholds are the same whatever the window's length, so one event
/// weighs the same in every window; the window only sets how long an event is remembered. It rolls on as
/// you drive: each event drops out once the window has been driven past it, so the light eases back to green.
///
/// The driving time is the trip's: game time spent moving (real time x the game's time scale), so standing
/// still or a paused game doesn't age the window. Fed by the telemetry reader thread only.
/// </summary>
public sealed class DriveStatus
{
    const double MovingKmh = 1;
    static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(1);
    /// <summary>Driving is kept in slices this long (game time), dropped whole as they leave the window.</summary>
    static readonly TimeSpan Slice = TimeSpan.FromMinutes(1);

    sealed class Bucket
    {
        public TimeSpan Game;
        public int Points;
    }

    readonly LinkedList<Bucket> _buckets = new();
    /// <summary>Events still going on, and the points they have earned so far (1 yellow, 2 once red).</summary>
    readonly List<(DrivingEvent Event, int Points)> _open = new();
    DateTime? _lastFrame;
    TimeSpan _driven;
    int _points;

    /// <summary>The window, in game minutes of driving (30, 60 or 120 in the app).</summary>
    public int WindowMinutes { get; set; } = 60;

    /// <summary>Points (yellow 1, red 2) of the events in the window.</summary>
    public int Points => _points;

    /// <summary>Game driving time the window holds so far (up to its length).</summary>
    public TimeSpan Driven => _driven;

    /// <summary>How many points the light takes to stay yellow or red.</summary>
    public LightStrictness Strictness { get; set; } = LightStrictness.Normal;

    /// <summary>Points from which the light stays yellow.</summary>
    public int YellowFrom => YellowPoints(Strictness);

    /// <summary>Points from which the light stays red.</summary>
    public int RedFrom => 2 * YellowFrom;

    public static int YellowPoints(LightStrictness strictness) => strictness switch
    {
        LightStrictness.Realistic => 2,
        LightStrictness.Relaxed => 8,
        _ => 4,
    };

    /// <summary>The steady colour; null is green.</summary>
    public AlertLevel? Level => _points >= RedFrom ? AlertLevel.Red : _points >= YellowFrom ? AlertLevel.Yellow : null;

    /// <summary>One reader step: the speed (null while paused, closed or the alerts are off), the game's time scale and the events that started or turned red.</summary>
    public void Step(DateTime now, float? speedMs, double scale, IEnumerable<DrivingEvent> news)
    {
        if (_buckets.Last is null) _buckets.AddLast(new Bucket());
        var bucket = _buckets.Last!.Value;
        foreach (var e in news)
        {
            var points = e.Level == AlertLevel.Red ? 2 : 1;
            var i = _open.FindIndex(x => ReferenceEquals(x.Event, e));
            var earned = i < 0 ? 0 : _open[i].Points;
            if (points > earned)
            {
                bucket.Points += points - earned;
                _points += points - earned;
            }
            if (i < 0) _open.Add((e, points)); else _open[i] = (e, Math.Max(points, earned));
        }
        _open.RemoveAll(x => x.Event.Ended is not null);

        if (speedMs is not { } ms)
        {
            _lastFrame = null;
            return;
        }
        var kmh = Math.Abs(ms) * 3.6;
        if (_lastFrame is { } last && now - last is var dt && dt > TimeSpan.Zero && dt <= MaxStep && kmh >= MovingKmh)
        {
            var game = dt * scale;
            bucket.Game += game;
            _driven += game;
            if (bucket.Game >= Slice) _buckets.AddLast(new Bucket());
            Trim();
        }
        _lastFrame = now;
    }

    /// <summary>Drops the oldest slices once the rest still fill the window.</summary>
    void Trim()
    {
        var window = TimeSpan.FromMinutes(WindowMinutes);
        while (_buckets.First is { } first && first != _buckets.Last && _driven - first.Value.Game >= window)
        {
            _driven -= first.Value.Game;
            _points -= first.Value.Points;
            _buckets.RemoveFirst();
        }
    }

    public void Clear()
    {
        _buckets.Clear();
        _open.Clear();
        _lastFrame = null;
        _driven = TimeSpan.Zero;
        _points = 0;
    }
}
