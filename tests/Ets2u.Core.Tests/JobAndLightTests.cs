using System.Runtime.InteropServices;
using System.Text;
using Ets2u.Core.Extract;
using Ets2u.Core.Features;
using Ets2u.Core.Paths;
using Ets2u.Core.Sii;
using Ets2u.Core.Telemetry;
using Xunit.Abstractions;

namespace Ets2u.Core.Tests;

public class TrafficLightTests
{
    [Fact]
    public void GreenWithoutAlertsAndTheWorstLevelOtherwise()
    {
        Assert.Null(AlertLight.For([]));
        Assert.Equal(AlertLevel.Yellow, AlertLight.For([AlertLevel.Yellow, AlertLevel.Yellow]));
        Assert.Equal(AlertLevel.Red, AlertLight.For([AlertLevel.Yellow, AlertLevel.Red]));
    }
}

public class JobTrackerTests
{
    static readonly DateTime T0 = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>A frame as the plugin writes it: the job and the gameplay events.</summary>
    sealed class Plugin
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

    static JobSummary? Drive(JobTracker jobs, Plugin plugin, ref DateTime now, int seconds, float speedMs, bool coach = true,
                             bool paused = false)
    {
        JobSummary? finished = null;
        for (var i = 0; i < seconds * 10; i++)
        {
            now += TimeSpan.FromMilliseconds(100);
            finished ??= jobs.Step(now, plugin.Frame(), paused ? null : speedMs, coach, []);
        }
        return finished;
    }

    [Fact]
    public void ABusJobFromStartToDelivery()
    {
        var jobs = new JobTracker();
        var plugin = new Plugin();
        // Delivered before the app was watching: not this job's.
        plugin.Event("bus_job.delivered\nrevenue=999\n");
        var now = T0;
        Assert.Null(Drive(jobs, plugin, ref now, 1, 0));
        Assert.Null(jobs.Current());

        plugin.SetJob(2, "source_city=Praha\ndestination_city=Brno\n");
        Assert.Null(Drive(jobs, plugin, ref now, 60, 20));
        var current = jobs.Current()!;
        Assert.Equal(JobOutcome.InProgress, current.Outcome);
        Assert.Equal("Praha → Brno", current.Route);
        Assert.Null(current.Result);

        // The results screen pauses the game; the event and the job's end still come through.
        plugin.Event("bus_job.delivered\nrevenue=1234\nearned.xp=300\n");
        Assert.Null(Drive(jobs, plugin, ref now, 2, 0, paused: true));
        plugin.SetJob(0, "");
        var done = Drive(jobs, plugin, ref now, 1, 0, paused: true)!;
        Assert.Equal(JobOutcome.Delivered, done.Outcome);
        Assert.Equal(JobKind.Bus, done.Kind);
        Assert.Equal("1234", done.Result!["revenue"]);
        Assert.Equal("bus_job.delivered", done.ResultEvent);
        Assert.InRange(done.Trip.DrivingTime.TotalSeconds, 59, 61);
        Assert.InRange(done.Trip.DistanceKm, 1.18, 1.22); // 20 m/s for a minute
        Assert.Contains("Delivered, revenue 1234, 300 XP", done.ToText());
        Assert.Null(jobs.Current());
    }

    [Fact]
    public void AHalfWrittenJobWaitsForTheNextStep()
    {
        var jobs = new JobTracker();
        var plugin = new Plugin();
        var now = T0;
        plugin.SetJob(2, "source_city=A\n");
        plugin.JobSeq++; // odd: being written
        Drive(jobs, plugin, ref now, 1, 10);
        Assert.Null(jobs.Current());
        plugin.JobSeq++;
        Drive(jobs, plugin, ref now, 1, 10);
        Assert.NotNull(jobs.Current());
    }

    [Fact]
    public void ATruckJobInATruckIsNotKept()
    {
        var jobs = new JobTracker();
        var plugin = new Plugin();
        var now = T0;
        plugin.SetJob(1, "cargo=wood\nsource_city=A\ndestination_city=B\n");
        Drive(jobs, plugin, ref now, 10, 20, coach: false);
        plugin.SetJob(1, "cargo=wood\nsource_city=A\ndestination_city=B\nis_cargo_loaded=true\n");
        Drive(jobs, plugin, ref now, 10, 20, coach: false);
        Assert.Equal("true", jobs.Current()!.Job["is_cargo_loaded"]); // the same job, updated
        plugin.SetJob(0, "");
        Assert.Null(Drive(jobs, plugin, ref now, 1, 0, coach: false));
    }

    [Fact]
    public void AJobInACoachThroughTheTruckJobConfigIsKept()
    {
        var jobs = new JobTracker();
        var plugin = new Plugin();
        var now = T0;
        plugin.SetJob(1, "source_city=A\n");
        Drive(jobs, plugin, ref now, 10, 15);
        plugin.Event("job.cancelled\ncancel.penalty=500\n");
        plugin.SetJob(1, "source_city=C\n"); // the next job right away
        var done = Drive(jobs, plugin, ref now, 1, 15)!;
        Assert.Equal(JobOutcome.Cancelled, done.Outcome);
        Assert.Equal("A", done.Route);
        Assert.Equal("C", jobs.Current()!.Route);
    }

    [Fact]
    public void AJobNeverDrivenIsDropped()
    {
        var jobs = new JobTracker();
        var plugin = new Plugin();
        var now = T0;
        plugin.SetJob(2, "source_city=A\n");
        Drive(jobs, plugin, ref now, 5, 0);
        plugin.SetJob(0, "");
        Assert.Null(Drive(jobs, plugin, ref now, 1, 0));
    }

    [Fact]
    public void SavedJobsReadBack()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ets2u_jobs_{Guid.NewGuid():N}.json");
        try
        {
            var events = new List<TripEvent>
            {
                new(DrivingEventKind.Braking, AlertLevel.Red, T0, T0.AddSeconds(2), "Braking", "Braking to a stop", "0.81 g", 0.81, 40, 0, true),
            };
            var trip = new TripSummary(T0, T0.AddHours(1), TimeSpan.FromMinutes(50), 42.5, 88, true, false, events);
            var job = new JobSummary(T0, T0.AddHours(1), JobKind.Bus, JobOutcome.Delivered,
                new Dictionary<string, string> { ["source_city"] = "Praha" }, new Dictionary<string, string> { ["revenue"] = "10" },
                "bus_job.delivered", trip);
            JobLog.Save([job], path);
            var back = Assert.Single(JobLog.Load(path));
            Assert.Equal(JobOutcome.Delivered, back.Outcome);
            Assert.Equal("Praha", back.Route);
            Assert.Equal(TimeSpan.FromMinutes(50), back.Trip.DrivingTime);
            Assert.Equal(AlertLevel.Red, Assert.Single(back.Trip.Events).Level);
            Assert.Equal(job.Trip.Score, back.Trip.Score);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheFrameMatchesThePluginsLayout() =>
        // 12 four-byte fields, truck_id[64], job_seq, job_kind, job_info[1024], gameplay_count, gameplay[4][512]
        Assert.Equal(48 + 64 + 8 + 1024 + 4 + 2048, Marshal.SizeOf<TelemetryFrame>());
}

public class GMeterTests(ITestOutputHelper output)
{
    const string Center =
        "SiiNunit\n{\nui::group : _nameless._.gmeter {\n fitting: false\n my_children: 2\n}\n\n" +
        "ui::text : _nameless._.gmeter.range {\n text: \"<img src=/material/ui/adviser/g_force.mat>\"\n id: 1010\n}\n\n" +
        "ui::text : _nameless._.other {\n text: \"<img src=/material/ui/adviser/other.mat>\"\n}\n}\n";

    const string Templates =
        "SiiNunit\n{\nui::text_template : txt.hud.bus_job.satisfaction\n{\n\ttext: \"<img src=/material/ui/adviser/g_force_pointer.mat color=%0>\"\n}\n\n" +
        "ui::text_template : txt.hud.bus_gmeter.pointer\n{\n\tname: \"HUD bus G-meter pointer\"\n" +
        "\ttext: \"<img src=/material/ui/adviser/g_force_pointer.mat color=%0>\"\n}\n\n}\n";

    [Fact]
    public void EmptiesOnlyTheMetersPicturesAndNeedle()
    {
        var game = new InMemoryGameFiles(new Dictionary<string, byte[]>
        {
            ["ui/adviser_center.sii"] = Encoding.UTF8.GetBytes(Center),
            ["ui/template/text.mod_bus.sii"] = Encoding.UTF8.GetBytes(Templates),
        });
        var files = CoachComfort.HideGMeter(game);
        Assert.Equal(["ui/adviser_center.sii", "ui/template/text.mod_bus.sii"], files.Keys.Order());
        var center = SiiDocument.Parse(files["ui/adviser_center.sii"]);
        Assert.Equal("\"\"", center.GetValue(center.Units.First(u => u.Name == "_nameless._.gmeter.range"), "text"));
        Assert.Contains("other.mat", center.GetValue(center.Units.First(u => u.Name == "_nameless._.other"), "text"));
        var templates = SiiDocument.Parse(files["ui/template/text.mod_bus.sii"]);
        Assert.Equal("\"\"", templates.GetValue(templates.Units.First(u => u.Name == "txt.hud.bus_gmeter.pointer"), "text"));
        // The satisfaction icon uses the same picture and stays.
        Assert.Contains("g_force_pointer", templates.GetValue(templates.Units.First(u => u.Name == "txt.hud.bus_job.satisfaction"), "text"));
    }

    [Fact]
    public void FailsWhenTheGameHasNoMeter() =>
        Assert.Throws<FormatException>(() => CoachComfort.HideGMeter(new InMemoryGameFiles(new Dictionary<string, byte[]>())));

    [Fact]
    public void TheCoachModShipsTheHudFilesOnlyWhenAsked()
    {
        var stock = Encoding.UTF8.GetBytes("SiiNunit\n{\nbus_economy_data : bus_economy.data\n{\n\tsatisfaction_late_penalty: (0.05, 0.13)\n}\n}\n");
        var game = new InMemoryGameFiles(new Dictionary<string, byte[]>
        {
            [CoachComfort.GamePath] = stock,
            ["ui/adviser_center.sii"] = Encoding.UTF8.GetBytes(Center),
            ["ui/template/text.mod_bus.sii"] = Encoding.UTF8.GetBytes(Templates),
        });
        static IReadOnlyList<string> Entries(byte[] zip)
        {
            using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
            return archive.Entries.Select(e => e.FullName).ToList();
        }
        var off = GeneratedMods.Build(game, new AppSettings { CoachHideGMeter = false, ModTag = "abcdef" }, m => m == CoachComfort.Mod);
        Assert.DoesNotContain("ui/adviser_center.sii", Entries(off["ets2u_coach_abcdef.scs"]));
        var on = GeneratedMods.Build(game, new AppSettings { CoachHideGMeter = true, ModTag = "abcdef" }, _ => false);
        Assert.Contains("ui/adviser_center.sii", Entries(on["ets2u_coach_abcdef.scs"]));
        Assert.Contains("ui/template/text.mod_bus.sii", Entries(on["ets2u_coach_abcdef.scs"]));
    }

    /// <summary>The installed game's files (run with ETS2U_LOCAL=1); reads only.</summary>
    [Fact]
    public void HidesTheInstalledGamesMeter()
    {
        if (Environment.GetEnvironmentVariable("ETS2U_LOCAL") != "1") return;
        var paths = GamePaths.Detect();
        using var game = new GameArchives(paths.InstallDir!);
        var files = CoachComfort.HideGMeter(game);
        foreach (var (path, bytes) in files)
        {
            var before = Encoding.UTF8.GetString(game.Read(path)).Replace("\r\n", "\n").Split('\n');
            var after = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Split('\n');
            Assert.Equal(before.Length, after.Length);
            var changed = before.Zip(after).Where(p => p.First != p.Second).ToList();
            output.WriteLine($"{path}: {changed.Count} line(s) changed");
            foreach (var (b, a) in changed) output.WriteLine($"  {b.Trim()}  ->  {a.Trim()}");
            Assert.InRange(changed.Count, 1, 2);
        }
        Assert.Equal(3, files.Count);
    }
}
