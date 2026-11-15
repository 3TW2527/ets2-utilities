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
    [InlineData(AlertSync.Exact, 0.9, 1.4)]
    [InlineData(AlertSync.Plus10, 0.99, 1.54)]
    [InlineData(AlertSync.Plus20, 1.08, 1.68)]
    public void TrucksCanUseLooserCoachLimits(AlertSync sync, double yellow, double red)
    {
        var s = new AppSettings { CoachLimits = CoachGLimits.Forgiving, TruckAlertSync = sync };
        Assert.Equal((yellow, red), (s.EffectiveTruckThresholds.BrakeYellow, s.EffectiveTruckThresholds.BrakeRed));
    }

    [Fact]
    public void NoUpperCeiling()
    {
        var s = new AppSettings { CoachLimits = CoachGLimits.Relaxed, TruckAlertSync = AlertSync.Plus20 };
        Assert.Equal(2.16, s.EffectiveTruckThresholds.CornerRed); // 1.8 x 1.2
        Assert.Equal(3.5, new DrivingThresholds { CornerYellow = 3, CornerRed = 3.5 }.Normalised().CornerRed);
        (CoachGLimits.Relaxed with { HarshCornering = 8 }).Validated();
    }

    [Fact]
    public void CoachesSyncByDefaultAndTrucksKeepTheirOwn()
    {
        var s = new AppSettings();
        Assert.True(s.CoachAlertSync);
        Assert.Equal(AlertSync.Off, s.TruckAlertSync);
        Assert.Equal(DrivingThresholds.FromCoachLimits(CoachGLimits.Stock).CornerRed, s.EffectiveCoachThresholds.CornerRed);
        Assert.Equal(1.12, s.EffectiveTruckThresholds.CornerRed); // own thresholds, defaulting to the coach mod's
        s.CoachAlertSync = false;
        Assert.Equal(1.12, s.EffectiveCoachThresholds.CornerRed);
    }

    [Fact]
    public void OlderSettingsTurnCoachSyncOnOnceThenKeepTheChoice()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, "{\"AlertDefaults\": 2, \"CoachAlertSync\": false, \"CoachThresholds\": {\"AccelYellow\": 0.5}}");
        var s = AppSettings.Load(path);
        Assert.True(s.CoachAlertSync);
        Assert.Equal(0.5, s.CoachThresholds.AccelYellow); // own thresholds kept for when sync is off
        s.CoachAlertSync = false;
        s.Save(path);
        Assert.False(AppSettings.Load(path).CoachAlertSync);
        Assert.DoesNotContain("Effective", File.ReadAllText(path));
    }
}
