using System.Diagnostics;
using System.IO;
using Ets2u.Core;
using Ets2u.Core.Extract;
using Ets2u.Core.Telemetry;

namespace Ets2u.App;

/// <summary>An alert as the overlay shows it: a copy, so the UI never reads what the reader thread is changing.</summary>
public sealed record AlertCard(DrivingEventKind Kind, DateTime Started, AlertLevel Level, string Title, string Detail, DateTime? Ended);

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

    /// <summary>A coach job just ended (raised on the reader thread).</summary>
    public event Action<JobSummary>? JobFinished;
    readonly Thread _thread;
    readonly object _lock = new();
    volatile bool _stop;

    TelemetryFrame? _frame;
    bool _coach;
    GPeaks _peaks;
    IReadOnlyList<AlertCard> _alerts = [];
    DriveScore _score;

    public TelemetryHub(AppSettings settings, string? installDir)
    {
        _settings = settings;
        // Its own reader of the game's files: the UI thread reads them too, and an archive isn't thread-safe.
        _game = installDir is null ? null : new GameArchives(installDir);
        _vehicles = new VehicleKind(() => _game);
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

    /// <summary>The recent driving's safety score, for the traffic light's steady colour.</summary>
    public DriveScore Score
    {
        get { lock (_lock) return _score; }
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
        var recordJobs = _settings.RecordCoachJobs;
        // A job's summary counts events like a trip does, alerts or not.
        var capturing = Trip.Capturing || (recordJobs && Jobs.Active);
        IReadOnlyList<DrivingEvent> news = [];
        List<DrivingEvent> shown = [];
        if (frame is { } f && (alerts || capturing))
        {
            // Speeding is left out everywhere (alerts, light, trips, jobs) while its switch is off.
            _detector.Speeding = _settings.SpeedingAlerts;
            _detector.Thresholds = ThresholdsFor(coach);
            news = _detector.Update(now, f.AccelForwardG, f.AccelRightG, f.SpeedMs, f.SpeedLimitMs);
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
        _status.Window = TimeSpan.FromMinutes(Math.Clamp(_settings.TrafficLightMinutes, 5, 120));
        _status.Step(now, alerts ? frame?.SpeedMs : null, scale, shown);
        Trip.Step(now, frame?.SpeedMs, coach, news, scale);
        if (recordJobs && Jobs.Step(now, raw, frame?.SpeedMs, coach, news, scale) is { } finished)
        {
            try { JobFinished?.Invoke(finished); }
            catch (Exception ex) { AppDirs.Log("Job summary: " + ex); }
        }
        _events.RemoveAll(e => e.Ended is { } end && now - end > Linger);
        var cards = _events.Count == 0
            ? []
            : _events.Select(e => new AlertCard(e.Kind, e.Started, e.Level, e.Title, e.Detail, e.Ended)).ToList();

        lock (_lock)
        {
            _frame = frame;
            _coach = coach;
            _alerts = cards;
            _score = _status.Current;
            if (frame is { } g)
                _peaks = new GPeaks(Math.Max(_peaks.Accel, g.AccelForwardG), Math.Max(_peaks.Brake, -g.AccelForwardG),
                    Math.Max(_peaks.Left, -g.AccelRightG), Math.Max(_peaks.Right, g.AccelRightG));
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(2));
        _telemetry.Dispose();
        _game?.Dispose();
    }
}
