using System.Globalization;

namespace Ets2u.Core.Features;

/// <summary>
/// When coach passengers start to complain about g-forces, per direction, in g.
/// <para>
/// The game keeps these in def/bus_economy_data.sii in m/s², as (x, y, z) triples in the vehicle's
/// frame: x across the coach (cornering), y up (bumps, crests), z along it (accelerating and braking).
/// Two tiers: <c>satisfaction_acceleration_limits[]</c> line 1 is the normal limit, line 2 the harsh one
/// (bigger penalty), and <c>failure_acceleration_limit</c> ends the job. A direction is switched off
/// with a huge limit (1000) for the tiers and -1 for the failure limit, as the stock file does.
/// Stock: normal 6 m/s² (0.61 g) everywhere, harsh only along the coach at 11 m/s² (1.12 g),
/// failure along the coach at 22 m/s² (2.24 g).
/// </para>
/// </summary>
public sealed record CoachGLimits
{
    public const double G = 9.80665;

    /// <summary>The game's "never" for the two tiers, in m/s².</summary>
    public const double OffLimit = 1000;

    public double Cornering { get; init; }
    public double Vertical { get; init; }
    public double Braking { get; init; }

    /// <summary>Harsh tier; null = that direction has no harsh tier.</summary>
    public double? HarshCornering { get; init; }
    public double? HarshVertical { get; init; }
    public double? HarshBraking { get; init; }

    /// <summary>Along the coach; null = the job never fails from g-force.</summary>
    public double? Failure { get; init; }

    /// <summary>The ratio of the game's harsh limit to its normal one (11 / 6 m/s², braking).</summary>
    public const double GameHarshRatio = 11.0 / 6.0;

    /// <summary>Exactly what the game's file holds: a harsh limit for braking only.</summary>
    public static CoachGLimits Game => new()
    {
        Cornering = 6.0 / G, Vertical = 6.0 / G, Braking = 6.0 / G,
        HarshBraking = 11.0 / G,
        Failure = 22.0 / G,
    };

    /// <summary>
    /// The game's limits, with its braking harsh limit (1.12 g) in every direction: the game itself has
    /// none for cornering or bumps, so a fast corner only ever costs the small penalty.
    /// </summary>
    public static CoachGLimits Stock => Game with { HarshCornering = 11.0 / G, HarshVertical = 11.0 / G };

    /// <summary>Noticeably calmer passengers: a firm stop or a brisk roundabout no longer counts.</summary>
    public static CoachGLimits Forgiving => new()
    {
        Cornering = 0.85, Vertical = 0.85, Braking = 0.9,
        HarshCornering = 1.4, HarshVertical = 1.4, HarshBraking = 1.4,
        Failure = 22.0 / G,
    };

    /// <summary>Only rough driving counts: a swerve or an emergency stop, not ordinary driving.</summary>
    public static CoachGLimits Relaxed => new()
    {
        Cornering = 1.3, Vertical = 1.3, Braking = 1.3,
        HarshCornering = 1.8, HarshVertical = 1.8, HarshBraking = 1.8,
        Failure = 22.0 / G,
    };

    public static IReadOnlyList<(string Name, CoachGLimits Limits)> Presets =>
        [("Stock", Stock), ("Forgiving", Forgiving), ("Relaxed", Relaxed)];

    /// <summary>A harsh limit, or the game's harsh-to-normal ratio where a direction has none.</summary>
    public static double HarshOrDefault(double normal, double? harsh) => harsh ?? normal * GameHarshRatio;

    /// <summary>Same as the game's file once rounded to the 0.1 m/s² the file is written with.</summary>
    public bool IsStock => Rounded() == Game.Rounded();

    /// <summary>Rounded to what the file holds (0.1 m/s²), so a value shown as 0.61 g writes back as 6.0.</summary>
    public CoachGLimits Rounded()
    {
        static double R(double g) => Math.Round(g * G, 1);
        static double? Rn(double? g) => g is { } v ? R(v) : null;
        // Compared as m/s², so the record's equality is exact.
        return new()
        {
            Cornering = R(Cornering), Vertical = R(Vertical), Braking = R(Braking),
            HarshCornering = Rn(HarshCornering), HarshVertical = Rn(HarshVertical), HarshBraking = Rn(HarshBraking),
            Failure = Rn(Failure),
        };
    }

    public string NormalTriple => Triple(Cornering, Vertical, Braking);

    public string HarshTriple => Triple(HarshCornering ?? OffLimit / G, HarshVertical ?? OffLimit / G, HarshBraking ?? OffLimit / G);

    public string FailureTriple => "(-1.0, -1.0, " + (Failure is { } f ? Ms(f) : "-1.0") + ")";

    static string Triple(double x, double y, double z) => $"({Ms(x)}, {Ms(y)}, {Ms(z)})";

    static string Ms(double g)
    {
        var ms = Math.Round(g * G, 1);
        return ms >= OffLimit ? "1000.0" : ms.ToString("0.0", CultureInfo.InvariantCulture);
    }

    /// <summary>Checks the values make sense; each harsh limit must be above its normal one.</summary>
    public CoachGLimits Validated()
    {
        static void Check(double v, string name)
        {
            if (v < 0.1) throw new FormatException($"{name}: enter a g-force of at least 0.1.");
        }
        Check(Cornering, "Cornering");
        Check(Vertical, "Bumps");
        Check(Braking, "Accelerating and braking");
        foreach (var (harsh, normal, name) in new[]
                 {
                     (HarshCornering, Cornering, "cornering"), (HarshVertical, Vertical, "bumps"),
                     (HarshBraking, Braking, "accelerating and braking"),
                 })
        {
            if (harsh is not { } h) continue;
            Check(h, "Harsh " + name);
            if (h <= normal) throw new FormatException($"Harsh {name} must be above its normal limit ({normal:0.00} g).");
        }
        if (Failure is { } f)
        {
            Check(f, "Job failure");
            if (f <= Braking) throw new FormatException("The job-failure limit must be above the accelerating and braking limit.");
        }
        return this;
    }
}
