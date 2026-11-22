using Ets2u.Core.Telemetry;

namespace Ets2u.Core.Tests;

public class TripEventListTests
{
    static readonly DateTime T0 = new(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);

    static TripEvent Ev(int second, DrivingEventKind kind, AlertLevel level, double lasted) =>
        new(kind, level, T0.AddSeconds(second), T0.AddSeconds(second + lasted), "", "", "", 0, 0, 0, true);

    static readonly TripEvent[] Events =
    [
        Ev(30, DrivingEventKind.Speeding, AlertLevel.Red, 8),
        Ev(10, DrivingEventKind.Cornering, AlertLevel.Yellow, 2),
        Ev(20, DrivingEventKind.Braking, AlertLevel.Red, 1),
        Ev(40, DrivingEventKind.Braking, AlertLevel.Yellow, 3),
    ];

    static int[] Seconds(IEnumerable<TripEvent> events) => events.Select(e => (int)(e.Started - T0).TotalSeconds).ToArray();

    [Fact]
    public void SortsByTimeTypeLevelAndDurationEitherWay()
    {
        Assert.Equal([10, 20, 30, 40], Seconds(TripEventOrder.Sort(Events, TripSort.Time, false, T0)));
        Assert.Equal([40, 30, 20, 10], Seconds(TripEventOrder.Sort(Events, TripSort.Time, true, T0)));
        // Braking (both, oldest first), cornering, speeding.
        Assert.Equal([20, 40, 10, 30], Seconds(TripEventOrder.Sort(Events, TripSort.Type, false, T0)));
        Assert.Equal([30, 10, 20, 40], Seconds(TripEventOrder.Sort(Events, TripSort.Type, true, T0)));
        Assert.Equal([20, 30, 10, 40], Seconds(TripEventOrder.Sort(Events, TripSort.Level, true, T0)));
        Assert.Equal([20, 10, 40, 30], Seconds(TripEventOrder.Sort(Events, TripSort.Duration, false, T0)));
    }

    [Fact]
    public void ASavedJobsEventCanBeTakenOffAndTheScoreFollows()
    {
        var trip = new TripSummary(T0, T0.AddHours(1), TimeSpan.FromHours(1), 50, 80, true, false, Events);
        Assert.Equal(60, trip.Score); // 2 yellow + 2 red x 2 = 6, per hour x 10
        var fewer = trip.Without(T0.AddSeconds(30), DrivingEventKind.Speeding);
        Assert.Equal(3, fewer.Events.Count);
        Assert.Equal(40, fewer.Score);
        Assert.Equal(4, trip.Events.Count);
    }

    [Fact]
    public void ALiveEventTakenOffStaysOffEvenWhenItTurnsRed()
    {
        var detector = new DrivingEventDetector { Speeding = false };
        var trip = new TripRecorder();
        var now = T0;
        trip.Start(now);
        void Step(float brakeG)
        {
            now += TimeSpan.FromMilliseconds(100);
            trip.Step(now, 20, true, detector.Update(now, -brakeG, 0, 20, 0), 19);
        }
        Step(0);
        Step(0.4f); // yellow braking starts
        var started = trip.Snapshot()!.Events.Single().Started;
        Assert.True(trip.Remove(started, DrivingEventKind.Braking));
        Assert.False(trip.Remove(started, DrivingEventKind.Braking));
        Step(0.8f); // the same event turns red
        Step(0);
        Assert.Empty(trip.Snapshot()!.Events);
        Step(0.4f); // a new one counts again
        Assert.Single(trip.Snapshot()!.Events);
    }

    [Fact]
    public void SpeedingIsNotDetectedWithItsSwitchOff()
    {
        var detector = new DrivingEventDetector { Speeding = false };
        // 90 km/h in a 50.
        Assert.Empty(detector.Update(T0, 0, 0, 25, 50 / 3.6f));
        detector.Speeding = true;
        Assert.Single(detector.Update(T0.AddSeconds(1), 0, 0, 25, 50 / 3.6f));
    }

    [Fact]
    public void AJobIsSummedUpInAFewWords()
    {
        var job = JobText.Parse(
            "bus.stages.count=1\nbus.stages.current=0\nincome=3081\nplanned_distance.km=94\nbus_job.market=quick_job\n" +
            "source_stop.type[0]=city\nsource_stop.id[0]=kobenhavn\nsource_stop.name[0]=Copenhagen\n" +
            "source_stop.departure_time.planned[0]=57375\nsource_stop.entering_passengers.planned[0]=44\n");
        Assert.Equal("Pay 3081 · 94 km planned · 44 passengers", JobText.Facts(job));
        Assert.Equal("", JobText.Facts(new Dictionary<string, string> { ["source_stop.id[0]"] = "x" }));
    }
}
