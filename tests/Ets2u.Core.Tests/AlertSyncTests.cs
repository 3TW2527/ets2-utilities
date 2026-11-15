using System.Text;
using Ets2u.Core.Features;
using Ets2u.Core.Sii;
using Ets2u.Core.Telemetry;

namespace Ets2u.Core.Tests;

public class AlertSyncTests
{
    [Fact]
    public void EveryPresetHasAHarshLimitInEveryDirection()
    {
        foreach (var (name, l) in CoachGLimits.Presets)
        {
            Assert.True(l.HarshCornering is not null && l.HarshVertical is not null && l.HarshBraking is not null, name);
            l.Validated();
        }
    }

    [Fact]
    public void StockPresetWritesHarshCorneringAndBumps()
    {
        const string stock = "SiiNunit\n{\nbus_economy_data : bus_economy.data\n{\n" +
                             "\tsatisfaction_acceleration_limits[]: (6.0, 6.0, 6.0)\n" +
                             "\tsatisfaction_acceleration_limits[]: (1000.0, 1000.0, 11.0)\n" +
                             "\tfailure_acceleration_limit: (-1.0, -1.0, 22.0)\n}\n}\n";
        var doc = SiiDocument.Parse(CoachComfort.Build(Encoding.UTF8.GetBytes(stock), CoachFactor.None, false, CoachGLimits.Stock));
        var unit = doc.FindUnit("bus_economy_data")!;
        Assert.Equal(["(6.0, 6.0, 6.0)", "(11.0, 11.0, 11.0)"], doc.GetValues(unit, CoachComfort.LimitsKey));
    }

    [Fact]
    public void YellowIsTheNormalLimitAndRedTheHarshOne()
    {
        var l = CoachGLimits.Forgiving; // normal 0.85 across / 0.9 along, harsh 1.4
        var t = DrivingThresholds.FromCoachLimits(l);
        Assert.Equal((0.9, 1.4), (t.AccelYellow, t.AccelRed));
        Assert.Equal((0.9, 1.4), (t.BrakeYellow, t.BrakeRed));
        Assert.Equal((0.85, 1.4), (t.CornerYellow, t.CornerRed));
    }

    [Fact]
    public void MissingHarshLimitUsesTheGamesRatio()
    {
        // The game's own file: no harsh limit across the coach.
        var t = DrivingThresholds.FromCoachLimits(CoachGLimits.Game);
        Assert.Equal(0.61, t.CornerYellow);
        Assert.Equal(1.12, t.CornerRed);
        Assert.Equal(1.12, t.BrakeRed);
    }

    [Theory]
    [InlineData(AlertRelax.None, 0.9, 1.4)]
    [InlineData(AlertRelax.Plus10, 0.99, 1.54)]
    [InlineData(AlertRelax.Plus20, 1.08, 1.68)]
    public void EitherVehicleCanUseTheCoachModRelaxed(AlertRelax relax, double yellow, double red)
    {
        var s = new AppSettings
        {
            CoachLimits = CoachGLimits.Forgiving,
            CoachAlertSource = AlertSource.CoachMod, CoachAlertRelax = relax,
            TruckAlertSource = AlertSource.CoachMod, TruckAlertRelax = relax,
        };
        Assert.Equal((yellow, red), (s.EffectiveCoachThresholds.BrakeYellow, s.EffectiveCoachThresholds.BrakeRed));
        Assert.Equal((yellow, red), (s.EffectiveTruckThresholds.BrakeYellow, s.EffectiveTruckThresholds.BrakeRed));
    }

    [Theory]
    [InlineData(AlertRelax.None, 0.35, 0.70, 0.40, 0.80)]
    [InlineData(AlertRelax.Plus10, 0.39, 0.77, 0.44, 0.88)]
    [InlineData(AlertRelax.Plus20, 0.42, 0.84, 0.48, 0.96)]
    public void DefaultsRelaxPerVehicle(AlertRelax relax, double coachYellow, double coachRed, double truckYellow, double truckRed)
    {
        var s = new AppSettings { CoachAlertRelax = relax, TruckAlertRelax = relax };
        Assert.Equal((coachYellow, coachRed), (s.EffectiveCoachThresholds.CornerYellow, s.EffectiveCoachThresholds.CornerRed));
        Assert.Equal((truckYellow, truckRed), (s.EffectiveTruckThresholds.CornerYellow, s.EffectiveTruckThresholds.CornerRed));
        // Separate settings: relaxing trucks leaves coaches alone.
        var trucksOnly = new AppSettings { TruckAlertRelax = relax };
        Assert.Equal(0.70, trucksOnly.EffectiveCoachThresholds.BrakeRed);
    }

    [Fact]
    public void CustomNumbersAreUsedAsTyped()
    {
        var own = new DrivingThresholds { AccelYellow = 0.3, AccelRed = 0.5, BrakeYellow = 0.33, BrakeRed = 0.66, CornerYellow = 0.2, CornerRed = 0.4 };
        var s = new AppSettings { CoachAlertSource = AlertSource.Custom, CoachThresholds = own, CoachAlertRelax = AlertRelax.Plus20 };
        Assert.Equal(0.66, s.EffectiveCoachThresholds.BrakeRed); // relax doesn't apply to custom numbers
        Assert.Equal(0.4, s.EffectiveCoachThresholds.CornerRed);
    }

    [Fact]
    public void NoUpperCeiling()
    {
        var s = new AppSettings { CoachLimits = CoachGLimits.Relaxed, TruckAlertSource = AlertSource.CoachMod, TruckAlertRelax = AlertRelax.Plus20 };
        Assert.Equal(2.16, s.EffectiveTruckThresholds.CornerRed); // 1.8 x 1.2
        Assert.Equal(3.5, new DrivingThresholds { CornerYellow = 3, CornerRed = 3.5 }.Normalised().CornerRed);
        (CoachGLimits.Relaxed with { HarshCornering = 8 }).Validated();
    }

    [Fact]
    public void DefaultsByDefaultAndCoachModIsAnOption()
    {
        var s = new AppSettings();
        Assert.Equal(AlertSource.Default, s.CoachAlertSource);
        Assert.Equal(AlertSource.Default, s.TruckAlertSource);
        Assert.Equal(0.70, s.EffectiveCoachThresholds.CornerRed);
        Assert.Equal(0.80, s.EffectiveTruckThresholds.CornerRed);
        s.CoachAlertSource = AlertSource.CoachMod;
        Assert.Equal(1.12, s.EffectiveCoachThresholds.BrakeRed); // the coach mod's Stock harsh limit
    }

    [Fact]
    public void PreviewSettingsBecomeSources()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, "{\"AlertDefaults\": 5, \"CoachAlertSync\": true, \"TruckAlertSync\": \"Plus10\", " +
                                "\"CoachThresholds\": {\"AccelYellow\": 0.5}}");
        var s = AppSettings.Load(path);
        Assert.Equal(AlertSource.CoachMod, s.CoachAlertSource);
        Assert.Equal((AlertSource.CoachMod, AlertRelax.Plus10), (s.TruckAlertSource, s.TruckAlertRelax));
        Assert.Equal(0.5, s.CoachThresholds.AccelYellow); // kept for if they pick custom
        s.Save(path);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("AlertSync", text);
        Assert.DoesNotContain("Effective", text);
        Assert.Equal(AlertSource.CoachMod, AppSettings.Load(path).CoachAlertSource);
    }

    [Fact]
    public void TypedThresholdsBecomeCustomAndBuiltInOnesFollowTheDefault()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, "{\"AlertDefaults\": 5, " +
            "\"CoachThresholds\": {\"AccelYellow\": 0.35, \"AccelRed\": 0.7, \"BrakeYellow\": 0.35, \"BrakeRed\": 0.7, \"CornerYellow\": 0.61, \"CornerRed\": 1.12}, " +
            "\"TruckThresholds\": {\"AccelYellow\": 0.5, \"AccelRed\": 0.7, \"BrakeYellow\": 0.45, \"BrakeRed\": 0.65, \"CornerYellow\": 0.45, \"CornerRed\": 0.65}}");
        var s = AppSettings.Load(path);
        Assert.Equal(AlertSource.Default, s.CoachAlertSource);
        Assert.Equal(0.70, s.EffectiveCoachThresholds.CornerRed);
        Assert.Equal(AlertSource.Custom, s.TruckAlertSource);
        Assert.Equal(0.5, s.EffectiveTruckThresholds.AccelYellow);
    }
}
