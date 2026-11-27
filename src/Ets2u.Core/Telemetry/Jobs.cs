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
/// <param name="AppClosed">The app was closed for part of the job, so the driving then isn't in the summary.</param>
/// <param name="GameStarted">The local economy's clock (minutes, see <see cref="GameClock"/>) when the app saw the job start; null when not known.</param>
public sealed record JobSummary(
    DateTime Started, DateTime? Ended, JobKind Kind, JobOutcome Outcome,
    IReadOnlyDictionary<string, string> Job, IReadOnlyDictionary<string, string>? Result, string? ResultEvent,
    TripSummary Trip, bool AppClosed = false, uint? GameStarted = null)
{
    /// <summary>A bus job, or a job driven in a coach only; else a truck job. Each kind is kept in its own log.</summary>
    [JsonIgnore]
    public bool IsCoach => Kind == JobKind.Bus || (Trip.InCoach && !Trip.InTruck);

    /// <summary>"Coach job" or "Truck job".</summary>
    [JsonIgnore]
    public string Noun => IsCoach ? "Coach job" : "Truck job";

    public const string AppClosedNote =
        "The app was closed for part of this job, so the driving then isn't counted: its events are missing, " +
        "and the distance and driving time are the game's own where it gave them.";

    /// <summary>"Praha → Brno" from the job's cities (or stops, companies), or "" when the game named none.</summary>
    [JsonIgnore]
    public string Route => JobText.Route(Job);

    public string ToText()
    {
        var c = CultureInfo.InvariantCulture;
        var head = string.Create(c, $"{Noun}, {Started.ToLocalTime():yyyy-MM-dd HH:mm}");
        if (Route.Length > 0) head += " · " + Route;
        var lines = new List<string> { head, "Result: " + JobText.Describe(Outcome, Result) };
        if (AppClosed) lines.Add(AppClosedNote);
        lines.AddRange(Trip.ToText().Split(Environment.NewLine).Skip(1));
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>Where a job being driven stands (see <see cref="JobText.Progress"/>).</summary>
/// <param name="Stage">The stage being driven, from 1.</param>
/// <param name="NextStop">The stop or city it's heading for; null when the game didn't name it.</param>
/// <param name="Due">When it's due there, on the game's clock (minutes); null when not known.</param>
/// <param name="Start">When the job is scheduled to leave its first stop (a coach job's planned departure); null when not known.</param>
/// <param name="End">When it's due at its last stop; null when not known.</param>
public sealed record JobProgress(int Stage, int Stages, string? NextStop, uint? Due, uint? Start = null, uint? End = null)
{
    /// <summary>The next stop is the job's last: the one the game's route advisor is routed to.</summary>
    public bool NextIsLast => Stage >= Stages;

    /// <summary>
    /// When the job gets to its next stop by the route advisor's time (truck.navigation.time, seconds, on the
    /// game's clock), from <paramref name="now"/> (minutes). Only for the last stop: the advisor is routed
    /// there, and the game gives no distance or time for a coach job's stops on the way, so any figure for
    /// those would be a guess. Null then, and when there's no route or no clock.
    /// </summary>
    public uint? Eta(uint? now, float navTimeS, float navDistanceM) =>
        NextIsLast && now is { } clock && float.IsFinite(navTimeS) && navTimeS > 0 && navDistanceM > 0
            ? clock + (uint)Math.Round(navTimeS / 60)
            : null;

    /// <summary>"Due in 1 h 12 min", "Late by 5 min", or null when the due time or the clock isn't known.</summary>
    public string? DueText(uint? now) => Due is not { } due || now is not { } clock ? null
        : due >= clock ? "Due in " + JobText.Minutes(due - clock) : "Late by " + JobText.Minutes(clock - due);
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
        if (from is not null || to is not null)
        {
            var route = from is null ? to! : to is null ? from : $"{from} → {to}";
            var missing = MissingStages(job);
            return missing switch
            {
                0 => route,
                1 => route + " (+1 stop not recorded)",
                _ => string.Create(CultureInfo.InvariantCulture, $"{route} (+{missing} stops not recorded)"),
            };
        }
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

    /// <summary>
    /// Where a job being driven stands: which stage of how many, the stop it's heading for, and when it's due
    /// there (game minutes, as <see cref="GameClock"/>). A coach job's stage is bus.stages.current, counted
    /// from 0; a truck job has one stage, due at its delivery time.
    /// </summary>
    public static JobProgress Progress(IReadOnlyDictionary<string, string> job)
    {
        if (Number(job.GetValueOrDefault("bus.stages.count")) is { } count && count >= 1)
        {
            var stages = (int)count;
            var current = Math.Clamp((int)(Number(job.GetValueOrDefault("bus.stages.current")) ?? 0), 0, stages - 1);
            var next = job.GetValueOrDefault($"destination_stop.name[{current}]") is { Length: > 0 } name ? name : null;
            uint? Minute(string key) => Number(job.GetValueOrDefault(key)) is { } m && m > 0 ? (uint)m : null;
            return new JobProgress(current + 1, stages, next, Minute($"destination_stop.arrival_time.planned[{current}]"),
                Minute("source_stop.departure_time.planned[0]"), Minute($"destination_stop.arrival_time.planned[{stages - 1}]"));
        }
        var city = job.GetValueOrDefault("destination_city") is { Length: > 0 } c ? c : null;
        var delivery = Number(job.GetValueOrDefault("delivery.time")) is { } t && t > 0 ? (uint)t : (uint?)null;
        return new JobProgress(1, 1, city, delivery, null, delivery);
    }

    /// <summary>"1 h 12 min" or "12 min", from game minutes.</summary>
    public static string Minutes(long minutes)
    {
        var m = Math.Abs(minutes);
        return m >= 60 ? string.Create(CultureInfo.InvariantCulture, $"{m / 60} h {m % 60:00} min") : string.Create(CultureInfo.InvariantCulture, $"{m} min");
    }

    /// <summary>
    /// Stages the game counted (bus.stages.count) but whose stops didn't come through: before 0.13.0 the
    /// plugin passed a job in 1 KB, so a long coach job lost its last stages.
    /// </summary>
    public static int MissingStages(IReadOnlyDictionary<string, string> job)
    {
        if (Number(job.GetValueOrDefault("bus.stages.count")) is not { } count) return 0;
        const string key = "destination_stop.name[";
        var recorded = job.Keys.Where(k => k.StartsWith(key, StringComparison.Ordinal))
            .Select(k => int.TryParse(k[key.Length..].TrimEnd(']'), out var i) ? i + 1 : 0)
            .DefaultIfEmpty(0).Max();
        return recorded == 0 ? 0 : Math.Max(0, (int)count - recorded);
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

    /// <summary>
    /// Whether two job configurations are the same job: the game sends a job again when it starts or a
    /// profile is loaded, and fills in arrival times as the job goes, so only what names the job counts
    /// (its stops or cities, companies, cargo, pay, planned distance and market).
    /// </summary>
    public static bool SameIdentity(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        static bool Names(string key) =>
            key is "income" or "planned_distance.km" or "bus_job.market" or "cargo" or "cargo.id"
                or "source_city" or "destination_city" or "source_company" or "destination_company"
            || key.StartsWith("source_stop.id", StringComparison.Ordinal)
            || key.StartsWith("destination_stop.id", StringComparison.Ordinal);
        var keys = a.Keys.Concat(b.Keys).Where(Names).Distinct().ToList();
        return keys.Count > 0 && keys.All(k => a.GetValueOrDefault(k) == b.GetValueOrDefault(k));
    }

    public static double? Km(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var km) && km > 0 ? km : null;

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
///
/// A job outlives the game and the app being closed. When the game starts, its plugin has sent no job yet
/// (its job_seq is 0), and when the app starts it carries on from the job it saved (<see cref="Restore"/>):
/// either way the job is held, <see cref="Waiting"/>, until the game sends one. The same job (by
/// <see cref="JobText.SameIdentity"/>) carries on the same summary; another job, or the game saying there's
/// none (or driving without one for a while), ends the held one.
/// </summary>
public sealed class JobTracker
{
    readonly object _lock = new();
    TripRecorder? _trip;
    JobKind _kind;
    Dictionary<string, string> _job = new();
    DateTime _started;
    uint? _gameStarted;
    (JobOutcome Outcome, string Id, Dictionary<string, string> Attributes)? _result;
    uint? _gameplaySeen;
    bool _waiting, _appClosed;
    DateTime? _noJobSince;

    /// <summary>Driving this long with no job, while one is held, means it's gone.</summary>
    static readonly TimeSpan NoJobGrace = TimeSpan.FromSeconds(10);

    /// <summary>Goes up whenever the job, its events or its state change: the app saves the job then.</summary>
    public int Revision { get; private set; }

    /// <summary>A job is held until the game (restarted, or the app reopened) sends it again.</summary>
    public bool Waiting
    {
        get { lock (_lock) return _trip is not null && _waiting; }
    }

    /// <summary>Carries on from a job saved while it was in progress, once the game sends it again.</summary>
    public void Restore(JobSummary saved)
    {
        lock (_lock)
        {
            if (_trip is not null || saved.Outcome != JobOutcome.InProgress || saved.Kind == JobKind.None) return;
            _trip = new TripRecorder();
            _trip.Resume(saved.Trip);
            _kind = saved.Kind;
            _job = new Dictionary<string, string>(saved.Job);
            _started = saved.Started;
            _gameStarted = saved.GameStarted;
            _result = null;
            _waiting = _appClosed = true;
            _noJobSince = null;
            Revision++;
        }
    }

    /// <summary>The job being driven, or null.</summary>
    public JobSummary? Current()
    {
        lock (_lock)
        {
            return _trip?.Snapshot() is { } trip
                ? new JobSummary(_started, null, _kind, JobOutcome.InProgress, _job, _result?.Attributes, _result?.Id, trip, _appClosed, _gameStarted)
                : null;
        }
    }

    /// <summary>When the job being driven started (real time), or null: cheap, for telling one job from the next.</summary>
    public DateTime? StartedAt
    {
        get { lock (_lock) return _trip is null ? null : _started; }
    }

    /// <summary>Takes an event off the job being driven.</summary>
    public bool RemoveEvent(DateTime started, DrivingEventKind kind)
    {
        lock (_lock)
        {
            if (_trip?.Remove(started, kind) != true) return false;
            Revision++;
            return true;
        }
    }

    public bool Active
    {
        get { lock (_lock) return _trip is not null; }
    }

    /// <summary>
    /// One reader step. <paramref name="raw"/> is the plugin's frame even while paused (null when the game
    /// isn't running); <paramref name="speedMs"/> is null while paused. Returns the job that just
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
                    if (_trip is not null && f.JobSeq == 0)
                    {
                        // The game has just started: it hasn't sent the job yet.
                        if (!_waiting) Revision++;
                        _waiting = true;
                    }
                    else if (_trip is not null && _waiting)
                    {
                        if (kind != JobKind.None && kind == _kind && JobText.SameIdentity(job, _job))
                        {
                            _waiting = false;
                            _noJobSince = null;
                            _job = job;
                            Revision++;
                        }
                        else if (kind != JobKind.None)
                        {
                            finished = Finish(now);
                            Begin(now, kind, job, f.LocalGameMinute);
                        }
                        else if (speedMs is not null && (_noJobSince ??= now) is var since && now - since >= NoJobGrace)
                        {
                            finished = Finish(now); // driving with no job: it was finished or given up meanwhile
                        }
                    }
                    else
                    {
                        var changed = kind != _kind || !SameJob(job, _job);
                        if (_trip is not null && (kind == JobKind.None || kind != _kind || (changed && _result is not null)))
                            finished = Finish(now);
                        if (_trip is null && kind != JobKind.None)
                            Begin(now, kind, job, f.LocalGameMinute);
                        else if (_trip is not null)
                            _job = job; // the same job, updated (e.g. cargo loaded)
                    }
                }
            }
            // A held job isn't being driven: it may not even be loaded.
            if (_waiting) return finished;
            var newsList = news as IReadOnlyCollection<DrivingEvent> ?? news.ToList();
            if (_trip is not null && newsList.Count > 0) Revision++;
            _trip?.Step(now, speedMs, coach, newsList, scale);
            return finished;
        }
    }

    static bool SameJob(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    void Begin(DateTime now, JobKind kind, Dictionary<string, string> job, uint? gameMinute)
    {
        _gameStarted = gameMinute;
        _trip = new TripRecorder();
        _trip.Start(now);
        _kind = kind;
        _job = job;
        _started = now;
        _result = null;
        _waiting = _appClosed = false;
        _noJobSince = null;
        Revision++;
    }

    JobSummary? Finish(DateTime now)
    {
        var trip = _trip!;
        trip.Stop(now);
        _trip = null;
        var summary = trip.Snapshot()!;
        // With a gap in the driving, the game's own distance for the job is the better one, and the
        // driving time is stretched to match.
        if (_appClosed && JobText.Km(_result?.Attributes.GetValueOrDefault("distance.km")) is { } km && km > summary.DistanceKm * 1.02)
            summary = summary with
            {
                DrivingTime = summary.DistanceKm >= 0.1 ? summary.DrivingTime * (km / summary.DistanceKm) : summary.DrivingTime,
                DistanceKm = km,
            };
        var job = new JobSummary(_started, now, _kind, _result?.Outcome ?? JobOutcome.Ended, _job, _result?.Attributes, _result?.Id,
            summary, _appClosed, _gameStarted);
        _result = null;
        _waiting = _appClosed = false;
        _noJobSince = null;
        Revision++;
        // A job that was never driven (a profile loaded and left) isn't worth a summary. Coach and truck
        // jobs both come back; the app files each in its own log (JobSummary.IsCoach).
        var driven = summary.DrivingTime > TimeSpan.Zero || job.Outcome != JobOutcome.Ended;
        return driven ? job : null;
    }
}

/// <summary>The jobs' summaries, newest first: coach jobs in %APPDATA%\ets2u\coach_jobs.json, truck jobs in truck_jobs.json.</summary>
public static class JobLog
{
    public const int Keep = 200;

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FilePath => Path.Combine(AppDirs.Settings, "coach_jobs.json");
    public static string TruckFilePath => Path.Combine(AppDirs.Settings, "truck_jobs.json");

    /// <summary>The log a coach or a truck job goes in.</summary>
    public static string PathFor(bool truck) => truck ? TruckFilePath : FilePath;

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
            AppDirs.Log(Path.GetFileName(path) + ": " + ex.Message);
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

    static double? Km(string? value) => JobText.Km(value);

    /// <summary>The job in progress, saved so it outlives the app: %APPDATA%\ets2u\coach_job_current.json.</summary>
    public static string CurrentPath => Path.Combine(AppDirs.Settings, "coach_job_current.json");

    /// <summary>Saves the job in progress, or deletes the file when there's none.</summary>
    public static void SaveCurrent(JobSummary? job, string? path = null)
    {
        path ??= CurrentPath;
        if (job is null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(job, Json));
        File.Move(temp, path, overwrite: true);
    }

    public static JobSummary? LoadCurrent(string? path = null)
    {
        path ??= CurrentPath;
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<JobSummary>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            AppDirs.Log("coach_job_current.json: " + ex.Message);
            return null;
        }
    }

    public static void Save(IEnumerable<JobSummary> jobs, string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(jobs.Take(Keep).ToList(), Json));
        File.Move(temp, path, overwrite: true);
    }
}
