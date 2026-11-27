using System.Diagnostics;
using System.IO;
using Ets2u.Core;
using Ets2u.Core.Extract;
using Ets2u.Core.Telemetry;

namespace Ets2u.App;

/// <summary>An alert as the overlay shows it: a copy, so the UI never reads what the reader thread is changing.</summary>
public sealed record AlertCard(DrivingEventKind Kind, DateTime Started, AlertLevel Level, string Title, string Detail, DateTime? Ended);

/// <summary>The traffic light's steady reading: points in the window against the strictness's thresholds.</summary>
public readonly record struct LightReading(int Points, int YellowFrom, int RedFrom, int WindowMinutes, AlertLevel? Level);

/// <summary>Largest g in each direction since the last reset.</summary>
public readonly record struct GPeaks(double Accel, double Brake, double Left, double Right);

/// <summary>
/// Reads the plugin's shared memory for every overlay, on its own thread, so the overlays (and the main
/// window) only draw. It samples at 50 Hz while the game runs, so the alerts and the peaks miss nothing,
/// and checks once a second while it doesn't. The overlays read the latest copy at their own, slower pace.
/// </summary>
public sealed class TelemetryHub : IDisposable
{
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(4);
    static readonly TimeSpan LivePeriod = TimeSpan.FromMilliseconds(20), IdlePeriod = TimeSpan.FromSeconds(1);

    readonly AppSettings _settings;
    readonly VehicleKind _vehicles;
    readonly GameArchives? _game;
    readonly LiveTelemetry _telemetry = new();
    readonly DrivingEventDetector _detector = new();
    readonly List<DrivingEvent> _events = new();
    readonly DriveStatus _status = new();

    /// <summary>The trip being captured for the Trip summary page (started and stopped by a button).</summary>
    public TripRecorder Trip { get; } = new();

    /// <summary>Splits the driving into jobs, each with its own trip summary.</summary>
    public JobTracker Jobs { get; } = new();

    /// <summary>
    /// What the Driving view counts: a copy of the job being driven (else the trip of Start/Stop, else the
    /// driving since the app opened), kept in step frame by frame. F5 starts it afresh (<see cref="ResetView"/>)
    /// without touching the job's or the trip's own summary; the next job or trip copies that one again.
    /// </summary>
    public TripRecorder View { get; } = new();

    /// <summary>When F5 last started the Driving view's counts afresh, while they still run from then; else null.</summary>
    public DateTime? ViewResetAt
    {
        get { lock (_lock) return _viewResetAt; }
    }

    /// <summary>Starts the Driving view's counts and the traffic light afresh (on the reader thread's next step).</summary>
    public void ResetView() => _resetView = true;

    /// <summary>A coach or truck job just ended (raised on the reader thread).</summary>
    public event Action<JobSummary>? JobFinished;
    volatile bool _resetView;
    DateTime? _viewResetAt;
    /// <summary>What <see cref="View"/> copies: the job's or the trip's start (and the trip's stop), or none.</summary>
    (int Kind, DateTime? Started, DateTime? Stopped) _viewSource = (-1, null, null);
    readonly Thread _thread;
    readonly object _lock = new();
    volatile bool _stop;

    TelemetryFrame? _frame;
    bool _coach;
    GPeaks _peaks;
    IReadOnlyList<AlertCard> _alerts = [];
    AlertLevel? _recent;
    LightReading _light;
    uint? _gameMinute, _localMinute;
    (float TimeS, float DistanceM) _nav;
    int _savedRevision;
    DateTime _savedAt;

    /// <summary>The job in progress is saved on every change, and every so often while it's driven.</summary>
    static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(30);

    public TelemetryHub(AppSettings settings, string? installDir)
    {
        _settings = settings;
        // Its own reader of the game's files: the UI thread reads them too, and an archive isn't thread-safe.
        _game = installDir is null ? null : new GameArchives(installDir);
        _vehicles = new VehicleKind(() => _game);
        // The coach job that was in progress when the app last closed, held until the game sends it again.
        if (settings.RecordJobs && JobLog.LoadCurrent() is { } saved) Jobs.Restore(saved);
        _savedRevision = Jobs.Revision;
        _thread = new Thread(Run) { IsBackground = true, Name = "ets2u telemetry", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>The latest frame (null while the game isn't running or is paused) and whether it's a coach.</summary>
    public (TelemetryFrame? Frame, bool Coach) Current
    {
        get { lock (_lock) return (_frame, _coach); }
    }

    public GPeaks Peaks
    {
        get { lock (_lock) return _peaks; }
    }

    public void ResetPeaks()
    {
        lock (_lock) _peaks = default;
    }

    public IReadOnlyList<AlertCard> Alerts
    {
        get { lock (_lock) return _alerts; }
    }

    /// <summary>The band of the last few game minutes of driving's safety score (null is green), for the traffic light's steady colour.</summary>
    public AlertLevel? Recent
    {
        get { lock (_lock) return _recent; }
    }

    /// <summary>The light's points, thresholds and window, for the Driving view.</summary>
    public LightReading Light
    {
        get { lock (_lock) return _light; }
    }

    /// <summary>The game's clock (minutes, see <see cref="GameClock"/>), also while paused; null when it isn't running.</summary>
    public uint? GameMinute
    {
        get { lock (_lock) return _gameMinute; }
    }

    /// <summary>The local economy's clock, which job due times are on (differs from <see cref="GameMinute"/> in a convoy).</summary>
    public uint? LocalGameMinute
    {
        get { lock (_lock) return _localMinute; }
    }

    /// <summary>The route advisor's time (s) and distance (m) to where the GPS is routed; zeros when there's no route or no game.</summary>
    public (float TimeS, float DistanceM) Navigation
    {
        get { lock (_lock) return _nav; }
    }

    public DrivingThresholds ThresholdsFor(bool coach) =>
        coach ? _settings.EffectiveCoachThresholds : _settings.EffectiveTruckThresholds;

    void Run()
    {
        var clock = Stopwatch.StartNew();
        while (!_stop)
        {
            var started = clock.Elapsed;
            TelemetryFrame? frame;
            try
            {
                frame = _telemetry.Read();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                frame = null;
            }
            Step(frame, _telemetry.Raw, DateTime.UtcNow);
            var wait = (frame is null ? IdlePeriod : LivePeriod) - (clock.Elapsed - started);
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
        }
    }

    void Step(TelemetryFrame? frame, TelemetryFrame? raw, DateTime now)
    {
        var coach = frame is { } v && _vehicles.IsCoach(v.TruckId);
        var alerts = coach ? _settings.CoachAlerts : _settings.TruckAlerts;
        var recordJobs = _settings.RecordJobs;
        // Events are found whatever the alerts' switches say: the trip, the job and the Driving view count
        // them all; the switches only decide what's shown on screen and on the light.
        IReadOnlyList<DrivingEvent> news = [];
        List<DrivingEvent> shown = [];
        if (_resetView)
        {
            _resetView = false;
            _status.Clear();
            View.Start(now);
            lock (_lock) _viewResetAt = now;
        }
        if (frame is { } f)
        {
            // Speeding is left out everywhere (alerts, light, trips, jobs) while its switch is off.
            _detector.Speeding = _settings.SpeedingAlerts;
            _detector.Thresholds = ThresholdsFor(coach);
            news = _detector.Update(now, f.AccelForwardG, f.AccelRightG, f.SpeedMs, f.SpeedLimitMs, f.GameMinute, f.Scale);
            if (alerts)
                foreach (var e in news)
                    if (e.Kind != DrivingEventKind.Speeding || _settings.SpeedingAlerts)
                    {
                        shown.Add(e);
                        if (!_events.Contains(e)) _events.Add(e);
                    }
        }
        else
        {
            _detector.Reset(now);
        }
        var scale = (frame ?? raw)?.Scale ?? 1;
        // The light's score counts what the alerts show, and only while they're on for this vehicle.
        _status.WindowMinutes = Math.Clamp(_settings.TrafficLightGameMinutes, 5, 240);
        _status.Strictness = _settings.TrafficLightStrictness;
        _status.Step(now, alerts ? frame?.SpeedMs : null, scale, shown);
        Trip.Step(now, frame?.SpeedMs, coach, news, scale);
        if (recordJobs && Jobs.Step(now, raw, frame?.SpeedMs, coach, news, scale) is { } finished
            && (finished.IsCoach ? _settings.RecordCoachJobs : _settings.RecordTruckJobs))
        {
            try { JobFinished?.Invoke(finished); }
            catch (Exception ex) { AppDirs.Log("Job summary: " + ex); }
        }
        if (recordJobs) SaveJob(now, force: false);
        StepView(now, frame?.SpeedMs, coach, news, scale);
        _events.RemoveAll(e => e.Ended is { } end && now - end > Linger);
        var cards = _events.Count == 0
            ? []
            : _events.Select(e => new AlertCard(e.Kind, e.Started, e.Level, e.Title, e.Detail, e.Ended)).ToList();

        lock (_lock)
        {
            _frame = frame;
            _coach = coach;
            _alerts = cards;
            _recent = _status.Level;
            _light = new LightReading(_status.Points, _status.YellowFrom, _status.RedFrom, _status.WindowMinutes, _status.Level);
            _gameMinute = (frame ?? raw)?.GameMinute;
            _localMinute = (frame ?? raw)?.LocalGameMinute;
            _nav = (frame ?? raw) is { } n ? (n.NavTimeS, n.NavDistanceM) : default;
            if (frame is { } g)
                _peaks = new GPeaks(Math.Max(_peaks.Accel, g.AccelForwardG), Math.Max(_peaks.Brake, -g.AccelForwardG),
                    Math.Max(_peaks.Left, -g.AccelRightG), Math.Max(_peaks.Right, g.AccelRightG));
        }
    }

    /// <summary>
    /// Keeps <see cref="View"/> in step: when the job (or the trip) changes, it becomes a copy of it, dropping
    /// an F5 reset; otherwise it counts this step the same way they do.
    /// </summary>
    void StepView(DateTime now, float? speedMs, bool coach, IReadOnlyList<DrivingEvent> news, double scale)
    {
        var job = Jobs.StartedAt;
        var trip = Trip.Bounds;
        var source = job is not null ? (1, job, (DateTime?)null)
            : trip is { } t ? (2, t.Started, t.Stopped)
            : (0, (DateTime?)null, (DateTime?)null);
        if (source != _viewSource)
        {
            _viewSource = source;
            var copy = source.Item1 == 1 ? Jobs.Current()?.Trip : source.Item1 == 2 ? Trip.Snapshot() : null;
            if (copy is null) View.Start(now);
            else
            {
                View.Resume(copy);
                if (copy.Stopped is { } stopped) View.Stop(stopped);
            }
            lock (_lock) _viewResetAt = null;
            return;
        }
        // A held job isn't being driven (see JobTracker.Step): the view waits with it.
        if (source.Item1 == 1 && Jobs.Waiting) return;
        View.Step(now, speedMs, coach, news, scale);
    }

    /// <summary>Saves the job in progress (or deletes the file once there's none), so it outlives the app.</summary>
    void SaveJob(DateTime now, bool force)
    {
        var revision = Jobs.Revision;
        var due = revision != _savedRevision || (Jobs.Active && !Jobs.Waiting && now - _savedAt >= SaveEvery);
        if (!force && !due) return;
        try
        {
            JobLog.SaveCurrent(Jobs.Current());
            _savedRevision = revision;
            _savedAt = now;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _savedAt = now; // try again later, not every frame
            AppDirs.Log("Saving the job in progress: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(2));
        if (_settings.RecordJobs && !_thread.IsAlive) SaveJob(DateTime.UtcNow, force: true);
        _telemetry.Dispose();
        _game?.Dispose();
    }
}
