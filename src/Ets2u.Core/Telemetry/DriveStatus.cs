namespace Ets2u.Core.Telemetry;

/// <summary>The rolling safety score the traffic light shows between events.</summary>
/// <param name="Score">Events per 10 game hours over the window, as <see cref="TripSummary.Score"/> counts them.</param>
/// <param name="Covered">Real driving time the window holds so far (up to its length).</param>
public readonly record struct DriveScore(double Score, TimeSpan Covered)
{
    /// <summary>Null is green; the same bands as the trip summary's score.</summary>
    public AlertLevel? Level => TripSummary.Band(Score);
}

/// <summary>
/// The traffic light's steady colour: the safety score of the last stretch of driving, rolling forward as
/// you drive. It's the trip summary's GreenRoad score (yellow events + 2 x red, per 10 game hours) taken
/// over only the last <see cref="Window"/> of real driving time, so a run of harsh events turns the light
/// yellow or red and it eases back to green as they fall out of the window behind you.
///
/// The window counts real minutes spent moving: a break at a stop or in a depot doesn't wash a bad
/// stretch away, and a paused or closed game doesn't age it either. Until the window holds
/// <see cref="MinGameHours"/> of game driving the score is taken over that much anyway, so one event
/// in the first minute doesn't light it up red.
/// Fed by the telemetry reader thread only.
/// </summary>
public sealed class DriveStatus
{
    const double MovingKmh = 1;
    static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(1);
    /// <summary>Driving is kept in slices this long (real), dropped whole as they leave the window.</summary>
    static readonly TimeSpan Slice = TimeSpan.FromSeconds(15);
    /// <summary>The score's hours never count as less than this (about 6 real minutes on roads).</summary>
    public const double MinGameHours = 2;

    sealed class Bucket
    {
        public TimeSpan Real, Game;
        public int Points;
    }

    readonly LinkedList<Bucket> _buckets = new();
    /// <summary>Events still going on, and the points they have earned so far (1 yellow, 2 once red).</summary>
    readonly List<(DrivingEvent Event, int Points)> _open = new();
    DateTime? _lastFrame;
    TimeSpan _real, _game;
    int _points;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(30);

    public DriveScore Current => new(_points / Math.Max(_game.TotalHours, MinGameHours) * 10, _real);

    /// <summary>One reader step: the speed (null while paused or closed), the game's time scale and the events that started or turned red.</summary>
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
            bucket.Real += dt;
            bucket.Game += dt * scale;
            _real += dt;
            _game += dt * scale;
            if (bucket.Real >= Slice) _buckets.AddLast(new Bucket());
            Trim();
        }
        _lastFrame = now;
    }

    /// <summary>Drops the oldest slices once the rest still fill the window.</summary>
    void Trim()
    {
        while (_buckets.First is { } first && first != _buckets.Last && _real - first.Value.Real >= Window)
        {
            _real -= first.Value.Real;
            _game -= first.Value.Game;
            _points -= first.Value.Points;
            _buckets.RemoveFirst();
        }
    }

    public void Clear()
    {
        _buckets.Clear();
        _open.Clear();
        _lastFrame = null;
        _real = _game = TimeSpan.Zero;
        _points = 0;
    }
}
