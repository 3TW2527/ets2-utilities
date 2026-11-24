using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Ets2u.Core.Telemetry;

namespace Ets2u.App;

/// <summary>
/// The Driving view: a page to leave open on another screen while driving. Big figures, little text: the
/// traffic light's reading, speed and limit, the job, and the trip's or job's events and score.
/// </summary>
public partial class MainWindow
{
    static readonly Brush DriveGreen = Ui.Brush(0x2E, 0xC4, 0x5B), DriveYellow = Ui.Brush(0xF2, 0xB7, 0x05), DriveRed = Ui.Brush(0xE0, 0x3A, 0x2F);
    static readonly Brush DriveGreenOff = Ui.Brush(0x2E, 0xC4, 0x5B, 0x30), DriveYellowOff = Ui.Brush(0xF2, 0xB7, 0x05, 0x30),
        DriveRedOff = Ui.Brush(0xE0, 0x3A, 0x2F, 0x30);
    static readonly Brush DriveText = Ui.Brush(0xE8, 0xED, 0xF2), DriveMuted = Ui.Brush(0x8B, 0x98, 0xA6), DriveCard = Ui.Brush(0x1C, 0x23, 0x2B);

    DispatcherTimer? _driveTimer;
    readonly Dictionary<DrivingEventKind, (TextBlock Yellow, TextBlock Red)> _driveCounts = new();
    TextBlock? _driveScore, _driveScoreLabel;

    void InitDriving()
    {
        foreach (var kind in TripKinds)
        {
            var yellow = Count(DriveYellow);
            var red = Count(DriveRed);
            var counts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            counts.Children.Add(yellow);
            counts.Children.Add(new TextBlock { Text = "·", FontSize = 30, Foreground = DriveMuted, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            counts.Children.Add(red);
            var icon = new Grid { Width = 34, Height = 34, Margin = new Thickness(0, 0, 10, 0) };
            icon.Children.Add(new Ellipse { Fill = Ui.Brush(0x2A, 0x33, 0x3D) });
            icon.Children.Add(new Path
            {
                Data = TripLook.Icon(kind), Stroke = DriveText, StrokeThickness = 1.6, Stretch = Stretch.Uniform, Margin = new Thickness(8),
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            });
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(icon);
            head.Children.Add(new TextBlock { Text = TripEventName(kind).ToUpperInvariant(), Foreground = DriveMuted, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var body = new StackPanel();
            body.Children.Add(head);
            body.Children.Add(counts);
            DriveCounts.Children.Add(new Border { Background = DriveCard, CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(6), Child = body });
            _driveCounts[kind] = (yellow, red);
        }

        _driveScore = new TextBlock { FontSize = 48, FontWeight = FontWeights.SemiBold, Foreground = DriveText, Margin = new Thickness(0, 2, 0, 0) };
        _driveScoreLabel = new TextBlock { Foreground = DriveMuted, FontSize = 13, FontWeight = FontWeights.SemiBold };
        var score = new StackPanel();
        score.Children.Add(new TextBlock { Text = "SAFETY SCORE", Foreground = DriveMuted, FontSize = 13, FontWeight = FontWeights.SemiBold });
        score.Children.Add(_driveScore);
        score.Children.Add(_driveScoreLabel);
        DriveCounts.Children.Add(new Border { Background = DriveCard, CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(6), Child = score });

        static TextBlock Count(Brush colour) => new() { FontSize = 46, FontWeight = FontWeights.SemiBold, Foreground = colour, Text = "0" };

        _driveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _driveTimer.Tick += (_, _) =>
        {
            if (DrivingTab.IsSelected && IsVisible && WindowState != WindowState.Minimized) ShowDriving();
        };
        _driveTimer.Start();
        Closed += (_, _) => _driveTimer.Stop();
    }

    static string TripEventName(DrivingEventKind kind) => kind switch
    {
        DrivingEventKind.Acceleration => "Acceleration",
        DrivingEventKind.Braking => "Braking",
        DrivingEventKind.Cornering => "Cornering",
        _ => "Speeding",
    };

    void ShowDriving()
    {
        var c = CultureInfo.InvariantCulture;
        var hub = Hub;
        var (frame, coach) = hub.Current;
        var clock = hub.GameMinute;
        var s = _app.Settings;
        var alertsOn = coach ? s.CoachAlerts : s.TruckAlerts;

        DriveClock.Text = clock is { } minute ? GameClock.Format(minute) : "";
        DriveState.Text = !PluginInstaller.IsInstalled(_app.Paths.InstallDir) ? "Plugin not installed: see Start here"
            : clock is null ? "Waiting for ETS2"
            : frame is null ? "Paused"
            : (coach ? "Coach" : "Truck") + (alertsOn ? "" : " · alerts off for this vehicle");

        // The traffic light: points in the window, against the strictness.
        var light = hub.Light;
        var level = alertsOn ? light.Level : null;
        DriveLampGreen.Fill = alertsOn && level is null ? DriveGreen : DriveGreenOff;
        DriveLampYellow.Fill = level == AlertLevel.Yellow ? DriveYellow : DriveYellowOff;
        DriveLampRed.Fill = level == AlertLevel.Red ? DriveRed : DriveRedOff;
        if (alertsOn && light.YellowFrom > 0)
        {
            DrivePoints.Text = light.Points.ToString(c);
            var next = light.Points < light.YellowFrom ? (light.YellowFrom, "yellow") : (light.RedFrom, "red");
            DrivePointsOf.Text = light.Points >= light.RedFrom ? "points · red" : $"/ {next.Item1} → {next.Item2}";
            DriveLightNote.Text = $"Last {light.WindowMinutes} game min · {s.TrafficLightStrictness}";
        }
        else
        {
            DrivePoints.Text = "–";
            DrivePointsOf.Text = "";
            DriveLightNote.Text = "Driving alerts are off";
        }

        // Speed and limit.
        if (frame is { } f)
        {
            DriveSpeed.Text = SpeedText.Kmh(f.SpeedMs).ToString(c);
            DriveSpeedMph.Text = $"{SpeedText.Mph(f.SpeedMs)} mph";
            var limit = f.SpeedLimitMs > 0.1f ? SpeedText.Kmh(f.SpeedLimitMs) : (int?)null;
            DriveLimitSign.Visibility = limit is null ? Visibility.Hidden : Visibility.Visible;
            DriveLimit.Text = limit?.ToString(c) ?? "";
            DriveSpeed.Foreground = limit is { } l && SpeedText.Kmh(f.SpeedMs) > l ? DriveRed : DriveText;
        }
        else
        {
            DriveSpeed.Text = "–";
            DriveSpeed.Foreground = DriveText;
            DriveSpeedMph.Text = "";
            DriveLimitSign.Visibility = Visibility.Hidden;
        }

        // The job being driven, else the trip being captured.
        var job = hub.Jobs.Current();
        var trip = job?.Trip ?? hub.Trip.Snapshot();
        if (job is not null)
        {
            var progress = JobText.Progress(job.Job);
            DriveJobLabel.Text = job.Kind == JobKind.Bus ? "COACH JOB" : "JOB";
            DriveRoute.Text = job.Route.Length > 0 ? job.Route : "Job";
            DriveStage.Text = progress.Stages > 1 ? $"Stop {progress.Stage} of {progress.Stages}" : "Delivering to";
            DriveNext.Text = progress.NextStop is { } stop ? "→ " + stop : "";
            var due = progress.DueText(hub.LocalGameMinute);
            DriveDue.Text = due ?? "";
            DriveDue.Foreground = due?.StartsWith("Late", StringComparison.Ordinal) == true ? DriveRed : DriveGreen;
        }
        else
        {
            DriveJobLabel.Text = trip is null ? "JOB" : trip.Capturing ? "TRIP · CAPTURING" : "TRIP · STOPPED";
            DriveRoute.Text = trip is null ? "No job" : "No job";
            DriveStage.Text = trip is null ? "Take a job, or start capturing on Trip summary." : "";
            DriveNext.Text = "";
            DriveDue.Text = "";
        }

        // Its events and score.
        foreach (var (kind, (yellow, red)) in _driveCounts)
        {
            yellow.Text = (trip?.Count(kind, AlertLevel.Yellow) ?? 0).ToString(c);
            red.Text = (trip?.Count(kind, AlertLevel.Red) ?? 0).ToString(c);
        }
        if (trip?.Score is { } score)
        {
            var band = TripSummary.Band(score);
            _driveScore!.Text = score.ToString("0", c);
            _driveScore.Foreground = band == AlertLevel.Red ? DriveRed : band == AlertLevel.Yellow ? DriveYellow : DriveGreen;
            _driveScoreLabel!.Text = band == AlertLevel.Red ? "RED" : band == AlertLevel.Yellow ? "YELLOW" : "GREEN";
        }
        else
        {
            _driveScore!.Text = "–";
            _driveScore.Foreground = DriveText;
            _driveScoreLabel!.Text = trip is null ? "" : "after 10 game min";
        }

        DriveTotals.Text = trip is null ? "" : $"{trip.DrivingText} · {trip.DistanceText} · top {trip.TopKmh:0} km/h";
        var last = trip?.Events.MaxBy(e => e.Started);
        DriveLast.Text = last is null ? (trip is null ? "" : "No events yet")
            : $"Last: {last.Category} · {(last.Level == AlertLevel.Red ? "red" : "yellow")} · {last.Detail} · {Ago(DateTime.UtcNow - last.Started)}";
        DriveLast.Foreground = last is null ? DriveMuted : TripLook.Fill(last.Level);
    }

    static string Ago(TimeSpan t) =>
        t.TotalSeconds < 60 ? "just now" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min ago" : $"{(int)t.TotalHours} h ago";
}
