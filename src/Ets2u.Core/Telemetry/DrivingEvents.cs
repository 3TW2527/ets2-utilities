using System.Globalization;

namespace Ets2u.Core.Telemetry;

public enum DrivingEventKind { Acceleration, Braking, Cornering, Speeding }

public enum AlertLevel { Yellow, Red }

/// <summary>
/// Where the g-force meter turns yellow and red, in g (the plugin's reading, smoothed over ~0.3 s).
/// Telematics' harsh-event bands (0.15-0.32 g) caught nearly every ordinary start and stop in the
/// game, so the defaults sit higher: 0.4 / 0.6 g for coaches, a little more for trucks.
/// </summary>
public sealed class DrivingThresholds
{
    public double AccelYellow { get; set; } = 0.45;
    public double AccelRed { get; set; } = 0.65;
    public double BrakeYellow { get; set; } = 0.45;
    public double BrakeRed { get; set; } = 0.65;
    public double CornerYellow { get; set; } = 0.45;
    public double CornerRed { get; set; } = 0.65;

    /// <summary>Heavy goods vehicles.</summary>
    public static DrivingThresholds Truck => new();

    /// <summary>Buses and coaches: lower, for the passengers.</summary>
    public static DrivingThresholds Coach => new()
    {
        AccelYellow = 0.40, AccelRed = 0.60,
        BrakeYellow = 0.40, BrakeRed = 0.60,
        CornerYellow = 0.40, CornerRed = 0.60,
    };

    public (double Yellow, double Red) For(DrivingEventKind kind) => kind switch
    {
        DrivingEventKind.Acceleration => (AccelYellow, AccelRed),
        DrivingEventKind.Braking => (BrakeYellow, BrakeRed),
        DrivingEventKind.Cornering => (CornerYellow, CornerRed),
        _ => (0, 0),
    };

    /// <summary>Red can't sit below yellow; nothing below 0.05 g (that's every gear change).</summary>
    public DrivingThresholds Normalised()
    {
        static (double, double) Fix(double y, double r)
        {
            y = Math.Clamp(y, 0.05, 2);
            return (y, Math.Clamp(r, y, 2));
        }
        var (ay, ar) = Fix(AccelYellow, AccelRed);
        var (by, br) = Fix(BrakeYellow, BrakeRed);
        var (cy, cr) = Fix(CornerYellow, CornerRed);
        return new() { AccelYellow = ay, AccelRed = ar, BrakeYellow = by, BrakeRed = br, CornerYellow = cy, CornerRed = cr };
    }
}

/// <summary>One harsh-driving or speeding event, live while it lasts.</summary>
public sealed class DrivingEvent
{
    public required DrivingEventKind Kind { get; init; }
    public AlertLevel Level { get; internal set; }
    /// <summary>The largest g (or km/h for speeding) seen so far.</summary>
    public double Peak { get; internal set; }
    /// <summary>The speed limit in km/h, for speeding.</summary>
    public double LimitKmh { get; internal set; }
    public required DateTime Started { get; init; }
    /// <summary>Null while it lasts.</summary>
    public DateTime? Ended { get; internal set; }

    public string Title => Kind switch
    {
        DrivingEventKind.Acceleration => "Harsh acceleration",
        DrivingEventKind.Braking => "Harsh braking",
        DrivingEventKind.Cornering => "Harsh cornering",
        _ => "Speeding",
    };

    public string Detail => Kind == DrivingEventKind.Speeding
        ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(Peak):0} km/h in a {Math.Round(LimitKmh):0} km/h zone")
        : string.Create(CultureInfo.InvariantCulture, $"{Peak:0.00} g");
}

/// <summary>
/// GreenRoad-style event detection: an event starts when the (smoothed) g-force reaches the
/// yellow threshold, turns red if it goes on to reach the red one, and ends once the force has
/// eased off. Speeding starts at limit + 10% + 2 km/h (57 km/h in a 50) and is red only.
/// </summary>
public sealed class DrivingEventDetector
{
    /// <summary>An event ends once its force is back under this share of the yellow threshold.</summary>
    const double EndRatio = 0.75;
    /// <summary>Below this speed (km/h) a truck creeping or standing doesn't count.</summary>
    const double MinKmh = 5;

    readonly Dictionary<DrivingEventKind, DrivingEvent> _active = new();

    public DrivingThresholds Thresholds { get; set; } = new();
    public bool Speeding { get; set; } = true;

    public IReadOnlyCollection<DrivingEvent> Active => _active.Values;

    /// <summary>Speeding starts at this speed, in km/h; null without a limit.</summary>
    public static double? SpeedingThreshold(double limitKmh) => limitKmh < 1 ? null : limitKmh * 1.1 + 2;

    /// <summary>Takes one frame; returns the events that started or turned red in it.</summary>
    public IReadOnlyList<DrivingEvent> Update(DateTime now, float forwardG, float rightG, float speedMs, float limitMs)
    {
        var news = new List<DrivingEvent>();
        var kmh = Math.Abs(speedMs) * 3.6;
        // Going backwards, "forward" acceleration is braking.
        var along = speedMs < -0.3f ? -forwardG : forwardG;
        var moving = kmh >= MinKmh;

        Check(DrivingEventKind.Acceleration, moving ? Math.Max(along, 0) : 0, now, news);
        Check(DrivingEventKind.Braking, moving ? Math.Max(-along, 0) : 0, now, news);
        Check(DrivingEventKind.Cornering, moving ? Math.Abs(rightG) : 0, now, news);

        var limitKmh = limitMs * 3.6;
        var threshold = Speeding ? SpeedingThreshold(limitKmh) : null;
        _active.TryGetValue(DrivingEventKind.Speeding, out var speeding);
        if (speeding is null)
        {
            // Compared as the dashboard shows it: 57 on the gauge in a 50 counts.
            if (threshold is { } th && Math.Round(kmh) >= Math.Ceiling(Math.Round(th, 3)))
            {
                speeding = new DrivingEvent { Kind = DrivingEventKind.Speeding, Started = now, Level = AlertLevel.Red, Peak = kmh, LimitKmh = limitKmh };
                _active[DrivingEventKind.Speeding] = speeding;
                news.Add(speeding);
            }
        }
        else if (threshold is not { } th2 || kmh < th2 - 1)
        {
            End(DrivingEventKind.Speeding, now);
        }
        else
        {
            speeding.Peak = Math.Max(speeding.Peak, kmh);
            speeding.LimitKmh = limitKmh;
        }
        return news;
    }

    void Check(DrivingEventKind kind, double g, DateTime now, List<DrivingEvent> news)
    {
        var (yellow, red) = Thresholds.For(kind);
        if (!_active.TryGetValue(kind, out var e))
        {
            if (g < yellow) return;
            e = new DrivingEvent { Kind = kind, Started = now, Peak = g, Level = g >= red ? AlertLevel.Red : AlertLevel.Yellow };
            _active[kind] = e;
            news.Add(e);
            return;
        }
        if (g < yellow * EndRatio)
        {
            End(kind, now);
            return;
        }
        e.Peak = Math.Max(e.Peak, g);
        if (e.Level == AlertLevel.Yellow && g >= red)
        {
            e.Level = AlertLevel.Red;
            news.Add(e);
        }
    }

    void End(DrivingEventKind kind, DateTime now)
    {
        if (_active.Remove(kind, out var e)) e.Ended = now;
    }

    /// <summary>The game paused or closed: whatever was going on is over.</summary>
    public void Reset(DateTime now)
    {
        foreach (var e in _active.Values) e.Ended = now;
        _active.Clear();
    }
}
