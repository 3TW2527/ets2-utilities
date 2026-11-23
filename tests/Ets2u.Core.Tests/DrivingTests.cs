using Ets2u.Core.Telemetry;

namespace Ets2u.Core.Tests;

public class DrivingEventTests
{
    // The tests' own thresholds, so they don't move with the defaults.
    static DrivingEventDetector New() => new()
    {
        Thresholds = new DrivingThresholds
        {
            AccelYellow = 0.22, AccelRed = 0.28, BrakeYellow = 0.25, BrakeRed = 0.32, CornerYellow = 0.25, CornerRed = 0.30,
        },
    };

    static readonly DateTime T0 = new(2026, 1, 1);
    const float Kmh = 1 / 3.6f;

    static IReadOnlyList<DrivingEvent> Step(DrivingEventDetector d, int ms, float fwd = 0, float right = 0, float kmh = 60, float limit = 0) =>
        d.Update(T0.AddMilliseconds(ms), fwd, right, kmh * Kmh, limit * Kmh);

    [Fact]
    public void BrakingGoesYellowThenRedAndEnds()
    {
        var d = New();
        Assert.Empty(Step(d, 0, fwd: -0.10f));
        var started = Step(d, 100, fwd: -0.30f);
        var e = Assert.Single(started);
        Assert.Equal(DrivingEventKind.Braking, e.Kind);
        Assert.Equal(AlertLevel.Yellow, e.Level);

        var red = Step(d, 200, fwd: -0.45f);
        Assert.Same(e, Assert.Single(red));
        Assert.Equal(AlertLevel.Red, e.Level);
        Assert.Empty(Step(d, 300, fwd: -0.50f));   // still the same event
        Assert.Equal(0.50, e.Peak, 3);

        Assert.Empty(Step(d, 400, fwd: -0.15f));   // under 75% of yellow: over
        Assert.NotNull(e.Ended);
        Assert.Empty(d.Active);
    }

    [Fact]
    public void HysteresisKeepsOneEvent()
    {
        var d = New();
        Assert.Single(Step(d, 0, fwd: 0.23f));
        Assert.Empty(Step(d, 100, fwd: 0.19f));  // dips just under yellow
        Assert.Empty(Step(d, 200, fwd: 0.23f));  // not a second event
        Assert.Single(d.Active);
    }

    [Fact]
    public void CorneringCountsEitherSide()
    {
        var d = New();
        Assert.Equal(DrivingEventKind.Cornering, Assert.Single(Step(d, 0, right: -0.35f)).Kind);
        Assert.Equal(AlertLevel.Red, d.Active.Single().Level);
    }

    [Fact]
    public void ReversingFlipsBraking()
    {
        var d = New();
        // going backwards at 10 km/h, the truck accelerating forwards is braking
        var e = Assert.Single(d.Update(T0, 0.3f, 0, -10 * Kmh, 0));
        Assert.Equal(DrivingEventKind.Braking, e.Kind);
    }

    [Fact]
    public void StandingStillIgnoresForces()
    {
        var d = New();
        Assert.Empty(Step(d, 0, fwd: 0.5f, kmh: 2));
    }

    [Theory]
    [InlineData(50, 56, false)]
    [InlineData(50, 57, true)]
    [InlineData(80, 89, false)]
    [InlineData(80, 90, true)]
    [InlineData(30, 35, true)]
    public void SpeedingAtLimitPlusTenPercentPlusTwo(float limit, float kmh, bool speeding)
    {
        var d = New();
        var news = Step(d, 0, kmh: kmh, limit: limit);
        Assert.Equal(speeding, news.Any(e => e.Kind == DrivingEventKind.Speeding));
        if (speeding) Assert.Equal(AlertLevel.Red, news.Single().Level);
    }

    [Fact]
    public void SpeedingEndsBelowThresholdAndNotWithoutLimit()
    {
        var d = New();
        var e = Assert.Single(Step(d, 0, kmh: 60, limit: 50));
        Assert.Equal("60 km/h in a 50 km/h zone", e.Detail);
        Assert.Empty(Step(d, 100, kmh: 56.5f, limit: 50)); // within 1 km/h: still on
        Assert.Null(e.Ended);
        Step(d, 200, kmh: 55, limit: 50);
        Assert.NotNull(e.Ended);
        Assert.Empty(Step(d, 300, kmh: 120, limit: 0));
    }

    [Fact]
    public void SpeedingCanBeOff()
    {
        var d = New(); d.Speeding = false;
        Assert.Empty(Step(d, 0, kmh: 90, limit: 50));
    }

    [Fact]
    public void ThresholdsNormalise()
    {
        var t = new DrivingThresholds { BrakeYellow = 0.4, BrakeRed = 0.3, AccelYellow = 0 }.Normalised();
        Assert.Equal(0.4, t.BrakeRed);
        Assert.Equal(0.05, t.AccelYellow);
    }
}

public class CoachAndTruckTests
{
    [Fact]
    public void Defaults()
    {
        foreach (var (d, yellow, red) in new[] { (DrivingThresholds.Coach, 0.35, 0.70), (DrivingThresholds.Truck, 0.40, 0.80) })
        {
            Assert.Equal((yellow, red), (d.AccelYellow, d.AccelRed));
            Assert.Equal((yellow, red), (d.BrakeYellow, d.BrakeRed));
            Assert.Equal((yellow, red), (d.CornerYellow, d.CornerRed));
        }
    }

    [Fact]
    public void CoachDefaultsAlertSoonerThanTruckOnes()
    {
        var t0 = new DateTime(2026, 1, 1);
        Assert.Equal(AlertLevel.Yellow, Assert.Single(new DrivingEventDetector { Thresholds = DrivingThresholds.Coach }.Update(t0, 0, 0.38f, 15, 0)).Level);
        Assert.Empty(new DrivingEventDetector { Thresholds = DrivingThresholds.Truck }.Update(t0, 0, 0.38f, 15, 0));
        Assert.Equal(AlertLevel.Red, Assert.Single(new DrivingEventDetector { Thresholds = DrivingThresholds.Coach }.Update(t0, 0, 0.75f, 15, 0)).Level);
        Assert.Equal(AlertLevel.Yellow, Assert.Single(new DrivingEventDetector { Thresholds = DrivingThresholds.Truck }.Update(t0, 0, 0.75f, 15, 0)).Level);
        Assert.Equal(AlertLevel.Yellow, Assert.Single(new DrivingEventDetector { Thresholds = DrivingThresholds.Coach }.Update(t0, -0.4f, 0, 15, 0)).Level);
        Assert.Equal(AlertLevel.Red, Assert.Single(new DrivingEventDetector { Thresholds = DrivingThresholds.Coach }.Update(t0, -0.75f, 0, 15, 0)).Level);
        Assert.Empty(new DrivingEventDetector { Thresholds = DrivingThresholds.Coach }.Update(t0, -0.3f, 0, 15, 0)); // an ordinary stop
    }

    [Fact]
    public void OldBuiltInDefaultsMoveToTheNewOnesButOwnValuesStay()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ets2u_settings_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"AlertDefaults\": 4, " +
                "\"CoachThresholds\": {\"AccelYellow\": 0.4, \"AccelRed\": 0.6, \"BrakeYellow\": 0.4, \"BrakeRed\": 0.6, \"CornerYellow\": 0.4, \"CornerRed\": 0.6}, " +
                "\"TruckThresholds\": {\"AccelYellow\": 0.5, \"AccelRed\": 0.7, \"BrakeYellow\": 0.45, \"BrakeRed\": 0.65, \"CornerYellow\": 0.45, \"CornerRed\": 0.65}}");
            var s = AppSettings.Load(path);
            Assert.Equal(0.70, s.CoachThresholds.BrakeRed);
            Assert.Equal(0.5, s.TruckThresholds.AccelYellow); // the player changed this one: kept
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CoachesAreVehiclesUnderDefVehicleBus()
    {
        var game = new Ets2u.Core.Extract.InMemoryGameFiles(new Dictionary<string, byte[]>
        {
            ["def/vehicle/bus/man.lions_24/data.sii"] = [1],
            ["def/vehicle/truck/scania.s_2016/data.sii"] = [1],
        });
        var kinds = new VehicleKind(() => game);
        Assert.True(kinds.IsCoach("vehicle.man.lions_24"));
        Assert.False(kinds.IsCoach("vehicle.scania.s_2016"));
        Assert.False(kinds.IsCoach(""));
    }

    [Fact]
    public void OldThresholdsAreReplacedOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ets2u_settings_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"CoachThresholds\": {\"AccelYellow\": 0.15}}");
            var s = AppSettings.Load(path);
            Assert.Equal(0.35, s.CoachThresholds.AccelYellow);
            s.CoachThresholds.AccelYellow = 0.5;   // the player's own choice afterwards stays
            s.Save(path);
            Assert.Equal(0.5, AppSettings.Load(path).CoachThresholds.AccelYellow);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OldSingleSwitchTurnsOnBoth()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ets2u_settings_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"DrivingAlerts\": true}");
            var s = AppSettings.Load(path);
            Assert.True(s.CoachAlerts);
            Assert.True(s.TruckAlerts);
            Assert.Null(s.DrivingAlerts);
            Assert.Equal(0.35, s.CoachThresholds.AccelYellow);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class MouseDriveConfigTests
{
    [Fact]
    public void WritesWhatThePluginReads()
    {
        var text = MouseDriveConfig.Format(new AppSettings
        {
            MouseDrive = true, MouseToggle = MouseToggle.MiddleButton, MouseSensitivity = 1.5, MousePedalStrength = 9,
        });
        Assert.Contains("[mouse_drive]\r\n", text);
        Assert.Contains("enabled=1\r\n", text);
        Assert.Contains("toggle=middle\r\n", text);
        Assert.Contains("sensitivity=1.5\r\n", text);
        Assert.Contains("pedal_strength=4\r\n", text); // clamped like the plugin does
    }

    [Fact]
    public void F8IsTheDefaultKey()
    {
        var text = MouseDriveConfig.Format(new AppSettings());
        Assert.Contains("enabled=0\r\n", text);
        Assert.Contains("toggle=key\r\ntoggle_vk=119\r\n", text);
    }
}

public class MouseDriveControlsTests
{
    // Lines as a 1.62 profile's controls.sii has them.
    const string Stock =
        "SiiNunit\n{\ninput_config : _nameless.2da.7d20.1c58 {\n" +
        " config_lines[37]: \"constant c_mousesteer 0.000000\"\n" +
        " config_lines[40]: \"constant c_relatsteer 1.000000\"\n" +
        " config_lines[250]: \"mix camlr `-mouse.rel_position.x?0 * c_msens - semantical.camlr?0`\"\n" +
        " config_lines[251]: \"mix camud `-mouse.rel_position.y?0 * sel(c_minvert, -c_msens, c_msens) - semantical.camud?0`\"\n" +
        " config_lines[334]: \"mix msteering `-mouse.rel_position.x?0 * c_msens`\"\n" +
        "}\n}\n";

    // What a v0.7 preview left: the game's mouse steering mode with its own mouse steering muted.
    static readonly string Legacy = Stock
        .Replace("constant c_mousesteer 0.000000", "constant c_mousesteer 1.000000")
        .Replace("mix msteering `-mouse.rel_position.x?0 * c_msens`", "mix msteering `(-mouse.rel_position.x?0 * c_msens) * 0`");

    [Fact]
    public void GatesTheCameraAndLeavesSteeringAlone()
    {
        var text = MouseDriveControls.Apply(Stock);
        Assert.Contains("\"mix camlr `(-mouse.rel_position.x?0 * c_msens) * (1 - semantical.camlr?0)`\"", text);
        Assert.Contains("\"mix camud `(-mouse.rel_position.y?0 * sel(c_minvert, -c_msens, c_msens)) * (1 - semantical.camud?0)`\"", text);
        Assert.Contains("\"constant c_mousesteer 0.000000\"", text);
        Assert.Contains("\"constant c_relatsteer 1.000000\"", text);
        Assert.Contains("\"mix msteering `-mouse.rel_position.x?0 * c_msens`\"", text);
        Assert.True(MouseDriveControls.IsApplied(text));
        Assert.False(MouseDriveControls.IsApplied(Stock));
        Assert.Equal(text, MouseDriveControls.Apply(text)); // applying twice changes nothing
    }

    [Fact]
    public void UndoesThePreviewsMouseSteeringMode()
    {
        Assert.False(MouseDriveControls.IsApplied(MouseDriveControls.Apply(Stock).Replace("c_mousesteer 0.000000", "c_mousesteer 1.000000")
            .Replace("mix msteering `-mouse.rel_position.x?0 * c_msens`", "mix msteering `(-mouse.rel_position.x?0 * c_msens) * 0`")));
        var recorded = new ControlsOriginal("0.000000", "-mouse.rel_position.x?0 * c_msens");
        Assert.Equal(MouseDriveControls.Apply(Stock), MouseDriveControls.Apply(Legacy, recorded));
        Assert.Equal(MouseDriveControls.Apply(Stock), MouseDriveControls.Apply(Legacy)); // unrecorded: a fresh profile's values
    }

    [Fact]
    public void RestoresTheProfilesOwnValues()
    {
        var own = MouseDriveControls.Read(Stock)!;
        Assert.Equal("-mouse.rel_position.x?0 * c_msens - semantical.camlr?0", own.CamLr);
        Assert.Equal(Stock, MouseDriveControls.Restore(MouseDriveControls.Apply(Stock), own));
        Assert.Equal(Stock, MouseDriveControls.Restore(MouseDriveControls.Apply(Stock), null));
        Assert.Equal(Stock, MouseDriveControls.Restore(Legacy, null));
    }

    [Fact]
    public void RecordsOwnValuesFromAPreviewChangedProfile()
    {
        // What the app records before applying: the file with any earlier change put back.
        var own = MouseDriveControls.Read(MouseDriveControls.Restore(Legacy, null))!;
        Assert.Equal("0.000000", own.MouseSteering);
        Assert.Equal("-mouse.rel_position.x?0 * c_msens", own.MouseSteeringMix);
    }
}

public class TripTests
{
    static readonly DateTime T0 = new(2026, 1, 1);
    const float Kmh = 1 / 3.6f;

    static DrivingEventDetector Detector() => new()
    {
        Thresholds = new DrivingThresholds
        {
            AccelYellow = 0.2, AccelRed = 0.4, BrakeYellow = 0.2, BrakeRed = 0.4, CornerYellow = 0.2, CornerRed = 0.4,
        },
    };

    [Fact]
    public void CountsDrivingTimeDistanceAndEventsOnlyWhileCapturing()
    {
        var trip = new TripRecorder();
        Assert.Null(trip.Snapshot());
        var d = Detector();

        // Before Start: nothing is kept.
        trip.Step(T0, 20, false, d.Update(T0, -0.5f, 0, 20, 0));
        trip.Start(T0);
        d.Reset(T0);
        // 2 minutes at 72 km/h (20 m/s), 20 ms frames: 2.4 km, on a road at the game's time scale of 19.
        for (var ms = 0; ms <= 120_000; ms += 20)
        {
            var now = T0.AddMilliseconds(ms);
            var braking = ms is >= 30_000 and < 31_000 ? -0.3f : 0;
            trip.Step(now, 20, true, d.Update(now, braking, 0, 20, 0), 19);
        }
        // A pause: no frames, then a minute later the game is back. The gap isn't driving.
        trip.Step(T0.AddSeconds(150), null, true, [], 19);
        trip.Step(T0.AddSeconds(180), 20, true, [], 19);
        trip.Stop(T0.AddSeconds(181));
        trip.Step(T0.AddSeconds(200), 20, true, d.Update(T0.AddSeconds(200), -0.5f, 0, 20, 0));

        var s = trip.Snapshot()!;
        Assert.False(s.Capturing);
        // In the game's terms 38 minutes and 45.6 km; really 2 minutes and 2.4 km.
        Assert.Equal(120 * 19, s.DrivingTime.TotalSeconds, 0);
        Assert.Equal(2.4 * 19, s.DistanceKm, 2);
        Assert.Equal(120, s.RealDrivingTime!.Value.TotalSeconds, 0);
        Assert.Equal(2.4, s.RealDistanceKm!.Value, 2);
        Assert.Equal("38 min 00 s driving (2 min 00 s real)", s.DrivingText);
        Assert.Equal("45.6 km", s.DistanceText);
        Assert.Equal(72, s.TopKmh, 1);
        Assert.True(s.InCoach);
        Assert.False(s.InTruck);
        var e = Assert.Single(s.Events);
        Assert.Equal((DrivingEventKind.Braking, AlertLevel.Yellow, "Braking"), (e.Kind, e.Level, e.Category));
        // One yellow in 38 game minutes = 15.8 per 10 hours, which is green (in real minutes it would be 300, red).
        Assert.Equal(10 / (38.0 / 60), s.Score!.Value, 3);
        Assert.Null(TripSummary.Band(s.Score.Value));
        Assert.Contains("Braking", s.ToText());
    }

    [Fact]
    public void ScoreWaitsForTenMinutesAndRedCountsDouble()
    {
        var events = new[]
        {
            new TripEvent(DrivingEventKind.Braking, AlertLevel.Red, T0, T0, "Braking", "Braking", "0.50 g", 0.5, 60, 50, false),
            new TripEvent(DrivingEventKind.Cornering, AlertLevel.Yellow, T0, T0, "Cornering", "", "0.30 g", 0.3, 60, 50, false),
        };
        Assert.Null(new TripSummary(T0, null, TimeSpan.FromMinutes(9.9), 1, 60, false, true, events).Score);
        Assert.Equal(3, new TripSummary(T0, null, TimeSpan.FromHours(10), 1, 60, false, true, events).Score!.Value, 6);
        Assert.Null(TripSummary.Band(19.9));
        Assert.Equal(AlertLevel.Yellow, TripSummary.Band(20));
        Assert.Equal(AlertLevel.Red, TripSummary.Band(40));
    }

    [Fact]
    public void NamesHowAnEventHappened()
    {
        var d = Detector();
        // Cornering while accelerating out of the turn.
        var corner = Assert.Single(d.Update(T0, 0.05f, 0.3f, 40 * Kmh, 0));
        d.Update(T0.AddMilliseconds(100), 0.15f, 0.3f, 42 * Kmh, 0);
        Assert.Equal("Accelerating while exiting a turn", corner.Variant);

        // Cornering while braking into the turn.
        var d2 = Detector();
        var c2 = d2.Update(T0, -0.15f, 0.3f, 60 * Kmh, 0).Single(e => e.Kind == DrivingEventKind.Cornering);
        Assert.Equal("Braking while entering a turn", c2.Variant);

        // Braking to a stop.
        var d3 = Detector();
        var b = Assert.Single(d3.Update(T0, -0.3f, 0, 30 * Kmh, 0));
        d3.Update(T0.AddSeconds(1), -0.3f, 0, 8 * Kmh, 0);
        d3.Update(T0.AddSeconds(2), 0, 0, 6 * Kmh, 0);
        Assert.Equal("Braking to a stop", b.Variant);

        // Pulling away hard.
        var d4 = Detector();
        Assert.Equal("Accelerating from a stop", Assert.Single(d4.Update(T0, 0.3f, 0, 8 * Kmh, 0)).Variant);
    }
}
