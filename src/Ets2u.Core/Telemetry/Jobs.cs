using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ets2u.Core.Telemetry;

public enum JobKind { None, Truck, Bus }

public enum JobOutcome { InProgress, Delivered, Cancelled, Ended, Failed }

/// <summary>
/// One job driven with the app open: what the game said about it when it started and ended, and the
/// trip summary of the driving in between.
/// </summary>
/// <param name="Job">The job's attributes as the game sends them (names as in the SCS SDK, e.g. source_city).</param>
/// <param name="Result">The attributes of the delivered or cancelled event, if the game sent one.</param>
/// <param name="ResultEvent">That event's id, e.g. "job.delivered".</param>
public sealed record JobSummary(
    DateTime Started, DateTime? Ended, JobKind Kind, JobOutcome Outcome,
    IReadOnlyDictionary<string, string> Job, IReadOnlyDictionary<string, string>? Result, string? ResultEvent,
    TripSummary Trip)
{
    /// <summary>"Praha → Brno" from the job's cities (or stops, companies), or "" when the game named none.</summary>
    [JsonIgnore]
    public string Route => JobText.Route(Job);

    public string ToText()
    {
        var c = CultureInfo.InvariantCulture;
        var head = string.Create(c, $"Coach job, {Started.ToLocalTime():yyyy-MM-dd HH:mm}");
        if (Route.Length > 0) head += " · " + Route;
        var lines = new List<string> { head, "Result: " + JobText.Describe(Outcome, Result) };
        lines.AddRange(Trip.ToText().Split(Environment.NewLine).Skip(1));
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>Reading the plugin's "name=value" lines.</summary>
public static class JobText
{
    public static Dictionary<string, string> Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var at = line.IndexOf('=');
            if (at > 0) values[line[..at]] = line[(at + 1)..].TrimEnd('\r');
        }
        return values;
    }

    /// <summary>A gameplay event: its id and attributes.</summary>
    public static (string Id, Dictionary<string, string> Attributes) ParseEvent(string text)
    {
        var newline = text.IndexOf('\n');
        return newline < 0 ? (text.Trim(), new()) : (text[..newline].Trim(), Parse(text[(newline + 1)..]));
    }

    static readonly string[][] Ends =
    [
        ["source_city", "destination_city"], ["source.name", "target.name"], ["source_company", "destination_company"],
    ];

    /// <summary>
    /// "Copenhagen → Trelleborg". A truck job names its cities; a bus job (game 1.60) lists its stages as
    /// arrays, source_stop.name[i] and destination_stop.name[i], so the route runs from the first stage's
    /// start to the last stage's end.
    /// </summary>
    public static string Route(IReadOnlyDictionary<string, string> job)
    {
        var from = Stop(job, "source_stop.name", first: true);
        var to = Stop(job, "destination_stop.name", first: false);
        if (from is not null || to is not null) return from is null ? to! : to is null ? from : $"{from} → {to}";
        foreach (var pair in Ends)
            if (job.GetValueOrDefault(pair[0]) is { Length: > 0 } start)
                return job.GetValueOrDefault(pair[1]) is { Length: > 0 } end ? $"{start} → {end}" : start;
        // Attributes the app doesn't know yet: a source and a destination name, if there are any.
        string? Named(Func<string, bool> key) =>
            job.Where(kv => kv.Key.Contains("name", StringComparison.Ordinal) && key(kv.Key) && kv.Value.Length > 0)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value).FirstOrDefault();
        var source = Named(k => k.Contains("source", StringComparison.Ordinal));
        var target = Named(k => k.Contains("destination", StringComparison.Ordinal) || k.Contains("target", StringComparison.Ordinal));
        return source is null ? target ?? "" : target is null ? source : $"{source} → {target}";
    }

    /// <summary>The name in key[i] with the lowest (or highest) i, or in key itself.</summary>
    static string? Stop(IReadOnlyDictionary<string, string> job, string key, bool first)
    {
        if (job.GetValueOrDefault(key) is { Length: > 0 } plain) return plain;
        var stops = job.Where(kv => kv.Key.StartsWith(key + "[", StringComparison.Ordinal) && kv.Value.Length > 0)
            .Select(kv => (Index: int.TryParse(kv.Key[(key.Length + 1)..].TrimEnd(']'), out var i) ? i : 0, kv.Value))
            .OrderBy(s => s.Index).ToList();
        return stops.Count == 0 ? null : first ? stops[0].Value : stops[^1].Value;
    }

    /// <summary>
    /// The job in a few words for the summary page: "Pay 3081 · 94 km planned · 44 passengers". Only what
    /// a player cares about; the game's other attributes (stop ids, planned times) are left out.
    /// </summary>
    public static string Facts(IReadOnlyDictionary<string, string> job)
    {
        var c = CultureInfo.InvariantCulture;
        var parts = new List<string>();
        if (job.GetValueOrDefault("income") is { Length: > 0 } income) parts.Add("Pay " + income);
        if (Number(job.GetValueOrDefault("planned_distance.km")) is { } km) parts.Add(string.Create(c, $"{km:0} km planned"));
        if (Number(job.GetValueOrDefault("bus.stages.count")) is { } stages && stages > 1) parts.Add(string.Create(c, $"{stages:0} stages"));
        var passengers = job.Where(kv => kv.Key.StartsWith("source_stop.entering_passengers.planned", StringComparison.Ordinal))
            .Sum(kv => Number(kv.Value) ?? 0);
        if (passengers > 0) parts.Add(string.Create(c, $"{passengers:0} passengers"));
        if (job.GetValueOrDefault("cargo") is { Length: > 0 } cargo) parts.Add(cargo);
        return string.Join(" · ", parts);
    }

    static double? Number(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    public static string Describe(JobOutcome outcome, IReadOnlyDictionary<string, string>? result)
    {
        var text = outcome switch
        {
            JobOutcome.Delivered => "Delivered",
            JobOutcome.Cancelled => "Cancelled",
            JobOutcome.Failed => "Failed",
            JobOutcome.InProgress => "In progress",
            _ => "Ended",
        };
        if (result?.GetValueOrDefault("revenue") is { } revenue) text += $", revenue {revenue}";
        if (result?.GetValueOrDefault("earned.xp") is { } xp) text += $", {xp} XP";
        // 0..1 in the bus job's completed event.
        if (result?.GetValueOrDefault("passenger.satisfaction") is { } sat
            && double.TryParse(sat, NumberStyles.Float, CultureInfo.InvariantCulture, out var satisfaction))
            text += string.Create(CultureInfo.InvariantCulture, $", passengers {satisfaction * 100:0}% satisfied");
        if (result?.GetValueOrDefault("cancel.penalty") is { } penalty) text += $", penalty {penalty}";
        return text;
    }

    /// <summary>
    /// How a gameplay event ends a job: truck jobs are "job.delivered" / "job.cancelled", a bus job ends
    /// with "bus_job.completed" (game 1.60). Other endings are guessed from the word at the end.
    /// </summary>
    public static JobOutcome? OutcomeOf(string eventId)
    {
        var dot = eventId.LastIndexOf('.');
        if (dot < 0 || !eventId[..dot].EndsWith("job", StringComparison.Ordinal)) return null; // fines, tolls, ferries
        return eventId[(dot + 1)..] switch
        {
            "delivered" or "completed" or "finished" => JobOutcome.Delivered,
            "cancelled" or "canceled" or "abandoned" => JobOutcome.Cancelled,
            "failed" => JobOutcome.Failed,
            _ => null,
        };
    }
}

/// <summary>
/// Splits the driving into jobs: a job starts when the game's job configuration appears and ends when it
/// clears, or when another starts after a delivered or cancelled event. Each job gets its own trip
/// summary. Only coach jobs are kept: the game's bus jobs, and any job driven only in a coach. Fed by the
/// telemetry reader thread; <see cref="Current"/> is read by the UI. Thread-safe.
/// </summary>
public sealed class JobTracker
{
    readonly object _lock = new();
    TripRecorder? _trip;
    JobKind _kind;
    Dictionary<string, string> _job = new();
    DateTime _started;
    (JobOutcome Outcome, string Id, Dictionary<string, string> Attributes)? _result;
    uint? _gameplaySeen;

    /// <summary>The job being driven, or null.</summary>
    public JobSummary? Current()
    {
        lock (_lock)
        {
            return _trip?.Snapshot() is { } trip
                ? new JobSummary(_started, null, _kind, JobOutcome.InProgress, _job, _result?.Attributes, _result?.Id, trip)
                : null;
        }
    }

    /// <summary>Takes an event off the job being driven.</summary>
    public bool RemoveEvent(DateTime started, DrivingEventKind kind)
    {
        lock (_lock) return _trip?.Remove(started, kind) == true;
    }

    public bool Active
    {
        get { lock (_lock) return _trip is not null; }
    }

    /// <summary>
    /// One reader step. <paramref name="raw"/> is the plugin's frame even while paused (null when the game
    /// isn't running); <paramref name="speedMs"/> is null while paused. Returns the coach job that just
    /// finished, if one did.
    /// </summary>
    public JobSummary? Step(DateTime now, TelemetryFrame? raw, float? speedMs, bool coach, IEnumerable<DrivingEvent> news,
                            double scale = 1)
    {
        lock (_lock)
        {
            JobSummary? finished = null;
            if (raw is { } f)
            {
                // Events from before the app was watching belong to no job it saw start.
                _gameplaySeen ??= f.GameplayCount;
                var count = f.GameplayCount;
                for (var n = Math.Max(_gameplaySeen.Value, count >= TelemetryFrame.GameplaySlots ? count - TelemetryFrame.GameplaySlots : 0); n < count; n++)
                {
                    var (id, attributes) = JobText.ParseEvent(f.Gameplay(n));
                    if (_trip is not null && JobText.OutcomeOf(id) is { } outcome) _result = (outcome, id, attributes);
                }
                _gameplaySeen = count;

                // Odd: the plugin is writing the job right now; the next step reads it whole.
                if (f.JobSeq % 2 == 0)
                {
                    var kind = f.JobKind switch { 1 => JobKind.Truck, 2 => JobKind.Bus, _ => JobKind.None };
                    var job = kind == JobKind.None ? new Dictionary<string, string>() : JobText.Parse(f.JobInfo);
                    var changed = kind != _kind || !SameJob(job, _job);
                    if (_trip is not null && (kind == JobKind.None || kind != _kind || (changed && _result is not null)))
                        finished = Finish(now);
                    if (_trip is null && kind != JobKind.None)
                        Begin(now, kind, job);
                    else if (_trip is not null)
                        _job = job; // the same job, updated (e.g. cargo loaded)
                }
            }
            _trip?.Step(now, speedMs, coach, news, scale);
            return finished;
        }
    }

    static bool SameJob(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    void Begin(DateTime now, JobKind kind, Dictionary<string, string> job)
    {
        _trip = new TripRecorder();
        _trip.Start(now);
        _kind = kind;
        _job = job;
        _started = now;
        _result = null;
    }

    JobSummary? Finish(DateTime now)
    {
        var trip = _trip!;
        trip.Stop(now);
        _trip = null;
        var summary = trip.Snapshot()!;
        var job = new JobSummary(_started, now, _kind, _result?.Outcome ?? JobOutcome.Ended, _job, _result?.Attributes, _result?.Id, summary);
        _result = null;
        var isCoach = _kind == JobKind.Bus || (summary.InCoach && !summary.InTruck);
        // A job that was never driven (a profile loaded and left) isn't worth a summary.
        var driven = summary.DrivingTime > TimeSpan.Zero || job.Outcome != JobOutcome.Ended;
        return isCoach && driven ? job : null;
    }
}

/// <summary>The coach jobs' summaries, newest first, in %APPDATA%\ets2u\coach_jobs.json.</summary>
public static class JobLog
{
    public const int Keep = 200;

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FilePath => Path.Combine(AppDirs.Settings, "coach_jobs.json");

    public static List<JobSummary> Load(string? path = null)
    {
        path ??= FilePath;
        try
        {
            var jobs = File.Exists(path) ? JsonSerializer.Deserialize<List<JobSummary>>(File.ReadAllText(path), Json) ?? [] : [];
            return jobs.Select(InGameTime).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            AppDirs.Log("coach_jobs.json: " + ex.Message);
            return [];
        }
    }

    /// <summary>
    /// A job saved before 0.12.1 counted real time and distance only. The game's own distance for the job
    /// (from its end event, or the planned one if it ran to the end) against the real distance driven gives
    /// the time scale it was driven at, so its totals and score can be put in the game's terms too.
    /// </summary>
    public static JobSummary InGameTime(JobSummary job)
    {
        var trip = job.Trip;
        if (trip.RealDrivingTime is not null || trip.DistanceKm < 0.1) return job;
        var gameKm = Km(job.Result?.GetValueOrDefault("distance.km"))
                     ?? (job.Outcome == JobOutcome.Cancelled ? null : Km(job.Job.GetValueOrDefault("planned_distance.km")));
        if (gameKm is not { } km) return job;
        var scale = km / trip.DistanceKm;
        if (scale is < 2 or > 40) return job;
        return job with
        {
            Trip = trip with
            {
                DrivingTime = trip.DrivingTime * scale, DistanceKm = km,
                RealDrivingTime = trip.DrivingTime, RealDistanceKm = trip.DistanceKm,
            },
        };
    }

    static double? Km(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var km) && km > 0 ? km : null;

    public static void Save(IEnumerable<JobSummary> jobs, string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(jobs.Take(Keep).ToList(), Json));
        File.Move(temp, path, overwrite: true);
    }
}
