using System.Text;
using Ets2u.Core.Telemetry;

namespace Ets2u.Core.Tests;

/// <summary>A job carried across the game and the app being closed.</summary>
public class JobResumeTests
{
    static readonly DateTime T0 = new(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);

    const string BusJob =
        "bus.stages.count=1\nincome=3081\nplanned_distance.km=94\nbus_job.market=quick_job\n" +
        "source_stop.id[0]=kobenhavn\nsource_stop.name[0]=Copenhagen\n" +
        "destination_stop.id[0]=trelleborg\ndestination_stop.name[0]=Trelleborg\n";

    /// <summary>The plugin's memory as a fresh game writes it: blank until the job is sent.</summary>
    sealed class Game
    {
        public uint JobSeq, JobKind, Count;
        public string Job = "";
        readonly string[] _events = new string[4];

        public void SetJob(uint kind, string info)
        {
            JobSeq += 2;
            JobKind = kind;
            Job = info;
        }

        public void Event(string text) => _events[Count++ % 4] = text;

        public TelemetryFrame Frame()
        {
            var f = new TelemetryFrame
            {
                Magic = TelemetryFrame.ExpectedMagic, Version = TelemetryFrame.ExpectedVersion,
                JobSeq = JobSeq, JobKind = JobKind, GameplayCount = Count,
            };
            Span<byte> job = f.JobInfoRaw;
            Encoding.UTF8.GetBytes(Job).CopyTo(job);
            Span<byte> events = f.GameplayRaw;
            for (var i = 0; i < 4; i++)
                if (_events[i] is { } e) Encoding.UTF8.GetBytes(e).CopyTo(events[(i * TelemetryFrame.GameplaySlotSize)..]);
            return f;
        }
    }

    /// <summary>Drives (or sits paused, or with the game closed) in 100 ms steps; returns a job that finished.</summary>
    static JobSummary? Run(JobTracker jobs, Game? game, ref DateTime now, double seconds, float? speedMs, DrivingEvent[]? news = null)
    {
        JobSummary? finished = null;
        for (var i = 0; i < seconds * 10; i++)
        {
            now += TimeSpan.FromMilliseconds(100);
            finished ??= jobs.Step(now, game?.Frame(), game is null ? null : speedMs, true, i == 0 ? news ?? [] : [], 19);
        }
        return finished;
    }

    static DrivingEvent Braking(DateTime now)
    {
        var detector = new DrivingEventDetector { Speeding = false };
        detector.Update(now, 0, 0, 20, 0);
        return detector.Update(now.AddMilliseconds(100), -0.5f, 0, 20, 0).Single();
    }

    [Fact]
    public void RestartingTheGameMidJobKeepsOneSummary()
    {
        var jobs = new JobTracker();
        var game = new Game();
        var now = T0;
        Run(jobs, game, ref now, 1, 0);
        game.SetJob(2, BusJob);
        Assert.Null(Run(jobs, game, ref now, 60, 20, [Braking(now)]));

        // Quit the game, then start it again: its plugin's memory is blank, on the main menu.
        Assert.Null(Run(jobs, null, ref now, 30, null));
        game = new Game();
        Assert.Null(Run(jobs, game, ref now, 20, null));
        Assert.True(jobs.Waiting);
        // The profile loads with the same job, its arrival times filled in a little more.
        game.SetJob(2, BusJob + "source_stop.departure_time.actual[0]=57400\n");
        Assert.Null(Run(jobs, game, ref now, 60, 20));
        Assert.False(jobs.Waiting);

        game.Event("bus_job.completed\nrevenue=3081\ndistance.km=97\n");
        game.SetJob(0, "");
        var done = Run(jobs, game, ref now, 1, 0)!;
        Assert.Equal(JobOutcome.Delivered, done.Outcome);
        Assert.Single(done.Trip.Events);
        Assert.InRange(done.Trip.RealDrivingTime!.Value.TotalSeconds, 119, 121); // both parts
        Assert.False(done.AppClosed);
    }

    [Fact]
    public void AnotherJobAfterTheRestartEndsTheHeldOne()
    {
        var jobs = new JobTracker();
        var game = new Game();
        var now = T0;
        game.SetJob(2, BusJob);
        Run(jobs, game, ref now, 30, 20);
        game = new Game();
        Run(jobs, game, ref now, 5, null);
        game.SetJob(2, BusJob.Replace("income=3081", "income=1200"));
        var held = Run(jobs, game, ref now, 1, 0)!;
        Assert.Equal(JobOutcome.Ended, held.Outcome);
        Assert.Equal("1200", jobs.Current()!.Job["income"]);
    }

    [Fact]
    public void DrivingWithNoJobAfterTheRestartEndsTheHeldOne()
    {
        var jobs = new JobTracker();
        var game = new Game();
        var now = T0;
        game.SetJob(2, BusJob);
        Run(jobs, game, ref now, 30, 20);
        game = new Game();
        Assert.Null(Run(jobs, game, ref now, 60, null)); // the main menu, however long
        game.SetJob(0, ""); // a profile with no job
        Assert.Null(Run(jobs, game, ref now, 5, 20));
        Assert.Equal(JobOutcome.Ended, Run(jobs, game, ref now, 6, 20)!.Outcome);
        Assert.Null(jobs.Current());
    }

    [Fact]
    public void AJobSavedWhenTheAppClosedCarriesOnAndUsesTheGamesDistance()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ets2u_job_{Guid.NewGuid():N}.json");
        try
        {
            var jobs = new JobTracker();
            var game = new Game();
            var now = T0;
            game.SetJob(2, BusJob);
            Run(jobs, game, ref now, 60, 20, [Braking(now)]); // 1.2 km real, about 23 km in game terms
            JobLog.SaveCurrent(jobs.Current(), path);

            // The app is closed; later it starts again with the game already on the same job.
            var saved = JobLog.LoadCurrent(path)!;
            var again = new JobTracker();
            again.Restore(saved);
            Assert.True(again.Waiting);
            Assert.True(again.Current()!.AppClosed);
            Assert.Single(again.Current()!.Trip.Events);
            Assert.Null(Run(again, game, ref now, 60, 20));
            Assert.False(again.Waiting);
            Assert.InRange(again.Current()!.Trip.RealDrivingTime!.Value.TotalSeconds, 119, 121);

            game.Event("bus_job.completed\nrevenue=3081\ndistance.km=97\n");
            game.SetJob(0, "");
            var done = Run(again, game, ref now, 1, 0)!;
            Assert.True(done.AppClosed);
            Assert.Equal(97, done.Trip.DistanceKm);
            // The driving time grows with it, so the score is over the whole job.
            Assert.True(done.Trip.DrivingTime > TimeSpan.FromSeconds(120 * 19));
            Assert.Contains("The app was closed for part of this job", done.ToText());

            // An event from before the app closed can still be taken off.
            Assert.Single(done.Trip.Events);
            JobLog.SaveCurrent(null, path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ARestoredEventCanBeRemoved()
    {
        var jobs = new JobTracker();
        var game = new Game();
        var now = T0;
        game.SetJob(2, BusJob);
        Run(jobs, game, ref now, 10, 20, [Braking(now)]);
        var again = new JobTracker();
        again.Restore(jobs.Current()!);
        var e = again.Current()!.Trip.Events.Single();
        var revision = again.Revision;
        Assert.True(again.RemoveEvent(e.Started, e.Kind));
        Assert.Empty(again.Current()!.Trip.Events);
        Assert.True(again.Revision > revision);
    }

    [Fact]
    public void SameIdentityIgnoresTimesAndNeedsSomethingToCompare()
    {
        var a = JobText.Parse(BusJob);
        Assert.True(JobText.SameIdentity(a, JobText.Parse(BusJob + "destination_stop.arrival_time.actual[0]=1\n")));
        Assert.False(JobText.SameIdentity(a, JobText.Parse(BusJob.Replace("trelleborg", "malmo"))));
        Assert.False(JobText.SameIdentity(new Dictionary<string, string>(), new Dictionary<string, string>()));
    }
}
