using System.Globalization;

namespace Ets2u.Core.Telemetry;

public enum DrivingEventKind { Acceleration, Braking, Cornering, Speeding }

public enum AlertLevel { Yellow, Red }

/// <summary>Where a vehicle's alert thresholds come from.</summary>
public enum AlertSource
{
    /// <summary>The built-in thresholds for that vehicle.</summary>
    Default,
    /// <summary>The coach passengers' g-force limits: yellow = normal limit, red = harsh limit.</summary>
    CoachMod,
    /// <summary>The player's own numbers.</summary>
    Custom,
}

/// <summary>How much looser than its source a vehicle's thresholds are (not used for custom ones).</summary>
public enum AlertRelax { None, Plus10, Plus20 }

/// <summary>
/// Where the g-force meter turns yellow and red, in g (the plugin's reading, smoothed over ~0.3 s).
/// Defaults, set from driving with the live meter: coaches 0.35 / 0.7 g in every direction, trucks 0.4 / 0.8 g.
/// </summary>
public sealed class DrivingThresholds
{
    public double AccelYellow { get; set; } = 0.35;
    public double AccelRed { get; set; } = 0.70;
    public double BrakeYellow { get; set; } = 0.35;
    public double BrakeRed { get; set; } = 0.70;
    public double CornerYellow { get; set; } = 0.35;
    public double CornerRed { get; set; } = 0.70;

    /// <summary>Buses and coaches.</summary>
    public static DrivingThresholds Coach => Uniform(0.35, 0.70);

    /// <summary>Trucks: a little more, there are no passengers.</summary>
    public static DrivingThresholds Truck => Uniform(0.40, 0.80);

    static DrivingThresholds Uniform(double yellow, double red) => new()
    {
        AccelYellow = yellow, AccelRed = red, BrakeYellow = yellow, BrakeRed = red, CornerYellow = yellow, CornerRed = red,
    };

    /// <summary>
    /// A built-in default, past or present: 0.40 / 0.60 and 0.45 / 0.65 (v0.7), 0.61 / 1.12 and
    /// 0.35 / 0.7 with 0.61 / 1.12 cornering (v0.8 previews), and today's. Thresholds equal to one of these
    /// were never chosen by the player.
    /// </summary>
    public static bool IsBuiltIn(DrivingThresholds t)
    {
        var rows = new[] { (t.AccelYellow, t.AccelRed), (t.BrakeYellow, t.BrakeRed), (t.CornerYellow, t.CornerRed) };
        var uniform = new[] { (0.40, 0.60), (0.45, 0.65), (0.61, 1.12), (0.35, 0.70), (0.40, 0.80) }.Any(d => rows.All(r => r == d));
        return uniform || (rows[0] == (0.35, 0.70) && rows[1] == (0.35, 0.70) && rows[2] == (0.61, 1.12));
    }

    /// <summary>Every threshold times <paramref name="factor"/>.</summary>
    public DrivingThresholds Scaled(double factor)
    {
        static double R(double g) => Math.Round(g + 1e-9, 2, MidpointRounding.AwayFromZero); // 0.385 is 0.39, not 0.38
        return new DrivingThresholds
        {
            AccelYellow = R(AccelYellow * factor), AccelRed = R(AccelRed * factor),
            BrakeYellow = R(BrakeYellow * factor), BrakeRed = R(BrakeRed * factor),
            CornerYellow = R(CornerYellow * factor), CornerRed = R(CornerRed * factor),
        }.Normalised();
    }

    /// <summary>What a vehicle's alerts use.</summary>
    public static DrivingThresholds Resolve(AlertSource source, AlertRelax relax, DrivingThresholds builtIn,
                                            DrivingThresholds custom, Features.CoachGLimits coachLimits) => source switch
    {
        AlertSource.CoachMod => FromCoachLimits(coachLimits, Factor(relax)),
        AlertSource.Custom => custom.Normalised(),
        _ => builtIn.Scaled(Factor(relax)),
    };

    /// <summary>
    /// From the coach passengers' g-force limits: yellow where the small penalty starts, red where the big
    /// one does. Accelerating and braking share the limit along the coach; cornering takes the one across it.
    /// <paramref name="factor"/> loosens them (trucks carry no passengers, so a little more can be fine).
    /// </summary>
    public static DrivingThresholds FromCoachLimits(Features.CoachGLimits l, double factor = 1)
    {
        static double R(double g) => Math.Round(g + 1e-9, 2, MidpointRounding.AwayFromZero); // 0.385 is 0.39, not 0.38
        var brakeRed = Features.CoachGLimits.HarshOrDefault(l.Braking, l.HarshBraking);
        var cornerRed = Features.CoachGLimits.HarshOrDefault(l.Cornering, l.HarshCornering);
        return new DrivingThresholds
        {
            AccelYellow = R(l.Braking * factor), AccelRed = R(brakeRed * factor),
            BrakeYellow = R(l.Braking * factor), BrakeRed = R(brakeRed * factor),
            CornerYellow = R(l.Cornering * factor), CornerRed = R(cornerRed * factor),
        }.Normalised();
    }

    public static double Factor(AlertRelax relax) => relax switch
    {
        AlertRelax.Plus10 => 1.1,
        AlertRelax.Plus20 => 1.2,
        _ => 1,
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
            y = Math.Max(y, 0.05);
            return (y, Math.Max(r, y));
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
    /// <summary>Speed in km/h when it started, and the latest while it lasts (the last one once it's over).</summary>
    public double StartKmh { get; internal set; }
    public double EndKmh { get; internal set; }
    /// <summary>The most forward and most backward g along the vehicle while it lasted (for cornering: accelerating out or braking into the turn).</summary>
    public double MaxForwardG { get; internal set; }
    public double MaxBackwardG { get; internal set; }

    /// <summary>Forward or backward g past this, during a harsh corner, names how the corner was taken.</summary>
    public const double CornerAlongG = 0.10;

    public string Title => Kind switch
    {
        DrivingEventKind.Acceleration => "Harsh acceleration",
        DrivingEventKind.Braking => "Harsh braking",
        DrivingEventKind.Cornering => "Harsh cornering",
        _ => "Speeding",
    };

    /// <summary>The category, as a trip summary lists it.</summary>
    public string Category => Kind switch
    {
        DrivingEventKind.Acceleration => "Acceleration",
        DrivingEventKind.Braking => "Braking",
        DrivingEventKind.Cornering => "Cornering",
        _ => "Speeding",
    };

    /// <summary>What kind of event within its category, GreenRoad style ("Braking to a stop", "Accelerating while exiting a turn").</summary>
    public string Variant => Kind switch
    {
        DrivingEventKind.Acceleration => StartKmh < 15 ? "Accelerating from a stop" : "Accelerating",
        DrivingEventKind.Braking => EndKmh < 10 ? "Braking to a stop" : "Braking",
        DrivingEventKind.Cornering when MaxForwardG >= CornerAlongG && MaxForwardG >= MaxBackwardG => "Accelerating while exiting a turn",
        DrivingEventKind.Cornering when MaxBackwardG >= CornerAlongG => "Braking while entering a turn",
        DrivingEventKind.Cornering => "Taking a turn too fast",
        _ => string.Create(CultureInfo.InvariantCulture, $"Over the {Math.Round(LimitKmh):0} km/h limit"),
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

        Check(DrivingEventKind.Acceleration, moving ? Math.Max(along, 0) : 0, along, kmh, now, news);
        Check(DrivingEventKind.Braking, moving ? Math.Max(-along, 0) : 0, along, kmh, now, news);
        Check(DrivingEventKind.Cornering, moving ? Math.Abs(rightG) : 0, along, kmh, now, news);

        var limitKmh = limitMs * 3.6;
        var threshold = Speeding ? SpeedingThreshold(limitKmh) : null;
        _active.TryGetValue(DrivingEventKind.Speeding, out var speeding);
        if (speeding is null)
        {
            // Compared as the dashboard shows it: 57 on the gauge in a 50 counts.
            if (threshold is { } th && Math.Round(kmh) >= Math.Ceiling(Math.Round(th, 3)))
            {
                speeding = new DrivingEvent
                {
                    Kind = DrivingEventKind.Speeding, Started = now, Level = AlertLevel.Red, Peak = kmh, LimitKmh = limitKmh,
                    StartKmh = kmh, EndKmh = kmh,
                };
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
            speeding.EndKmh = kmh;
        }
        return news;
    }

    void Check(DrivingEventKind kind, double g, double along, double kmh, DateTime now, List<DrivingEvent> news)
    {
        var (yellow, red) = Thresholds.For(kind);
        if (!_active.TryGetValue(kind, out var e))
        {
            if (g < yellow) return;
            e = new DrivingEvent
            {
                Kind = kind, Started = now, Peak = g, Level = g >= red ? AlertLevel.Red : AlertLevel.Yellow,
                StartKmh = kmh, EndKmh = kmh, MaxForwardG = Math.Max(along, 0), MaxBackwardG = Math.Max(-along, 0),
            };
            _active[kind] = e;
            news.Add(e);
            return;
        }
        e.EndKmh = kmh;
        if (g < yellow * EndRatio)
        {
            End(kind, now);
            return;
        }
        e.Peak = Math.Max(e.Peak, g);
        e.MaxForwardG = Math.Max(e.MaxForwardG, along);
        e.MaxBackwardG = Math.Max(e.MaxBackwardG, -along);
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

/// <summary>What the traffic light shows: one lamp (null is green), flashing while it stands for an event.</summary>
public readonly record struct LightState(AlertLevel? Level, bool Flashing);

/// <summary>The traffic light's state for the alerts on screen.</summary>
public static class AlertLight
{
    /// <summary>
    /// An event on screen (going on, or in the seconds after it a card would stay) flashes its lamp, the
    /// worst one when there are several; otherwise the light shows the recent driving's band, steady.
    /// </summary>
    public static LightState Show(IReadOnlyCollection<AlertLevel> alerts, AlertLevel? steady) =>
        alerts.Count > 0 ? new(For(alerts), true) : new(steady, false);

    /// <summary>The worst level among the alerts, or null (green) when there are none.</summary>
    public static AlertLevel? For(IEnumerable<AlertLevel> alerts)
    {
        AlertLevel? light = null;
        foreach (var level in alerts)
        {
            if (level == AlertLevel.Red) return AlertLevel.Red;
            light = AlertLevel.Yellow;
        }
        return light;
    }
}
