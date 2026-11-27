using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Ets2u.Core.Telemetry;

namespace Ets2u.App;

/// <summary>
/// The Driving view: a page to leave open on another screen while driving. Big figures, little text: the
/// traffic light's reading, speed and limit, the job and its schedule, and the events and score of the job
/// (or trip) as tables. F5 starts the view's counts afresh; the job's own summary keeps everything.
/// </summary>
public partial class MainWindow
{
    static readonly Brush DriveGreen = Ui.Brush(0x2E, 0xC4, 0x5B), DriveYellow = Ui.Brush(0xF2, 0xB7, 0x05), DriveRed = Ui.Brush(0xE0, 0x3A, 0x2F);
    static readonly Brush DriveGreenOff = Ui.Brush(0x2E, 0xC4, 0x5B, 0x30), DriveYellowOff = Ui.Brush(0xF2, 0xB7, 0x05, 0x30),
        DriveRedOff = Ui.Brush(0xE0, 0x3A, 0x2F, 0x30);
    static readonly Brush DriveText = Ui.Brush(0xE8, 0xED, 0xF2), DriveMuted = Ui.Brush(0x8B, 0x98, 0xA6), DriveCard = Ui.Brush(0x1C, 0x23, 0x2B);
    static readonly Brush DriveZero = Ui.Brush(0x4A, 0x55, 0x61), DriveRule = Ui.Brush(0x2A, 0x33, 0x3D);

    DispatcherTimer? _driveTimer;
    /// <summary>Each kind's yellow and red count in the events table; speeding has no yellow cell (it's red only).</summary>
    readonly Dictionary<DrivingEventKind, (TextBlock? Yellow, TextBlock Red)> _driveCounts = new();

    void InitDriving()
    {
        BuildDriveEventsTable();

        // F5, wherever the app is: start the Driving view's counts and the traffic light afresh.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.F5 || Keyboard.Modifiers != ModifierKeys.None) return;
            e.Handled = true;
            ConfirmResetDriving();
        };

        _driveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _driveTimer.Tick += (_, _) =>
        {
            if (DrivingTab.IsSelected && IsVisible && WindowState != WindowState.Minimized) ShowDriving();
        };
        _driveTimer.Start();
        Closed += (_, _) => _driveTimer.Stop();
    }

    void ConfirmResetDriving()
    {
        if (MessageBox.Show(this,
                "Clear the recent events?\n\nThe Driving view's event counts and safety score, and the traffic light, start again from 0. " +
                "The job's and the trip's own summaries (Trip summary page) keep every event.",
                "ETS2 Utilities", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        Hub.ResetView();
        _app.Status("Recent events cleared: the Driving view counts from now. The job's summary keeps everything.");
        if (DrivingTab.IsSelected) Dispatcher.BeginInvoke(ShowDriving, DispatcherPriority.Background);
    }

    /// <summary>A column per kind of event (its icon and name on top), a row each for yellow and red.</summary>
    void BuildDriveEventsTable()
    {
        var table = DriveEventsTable;
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        foreach (var _ in TripKinds) table.ColumnDefinitions.Add(new ColumnDefinition());
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(78) });
        table.RowDefinitions.Add(new RowDefinition());
        table.RowDefinitions.Add(new RowDefinition());

        // Rules under the head and between the two levels.
        for (var row = 0; row < 2; row++)
            Place(new Border { BorderBrush = DriveRule, BorderThickness = new Thickness(0, 0, 0, 1) }, row, 0, TripKinds.Length + 1);

        Place(LevelHead("YELLOW", DriveYellow), 1, 0);
        Place(LevelHead("RED", DriveRed), 2, 0);

        for (var i = 0; i < TripKinds.Length; i++)
        {
            var kind = TripKinds[i];
            var icon = new Grid { Width = 38, Height = 38, HorizontalAlignment = HorizontalAlignment.Center };
            icon.Children.Add(new Ellipse { Fill = Ui.Brush(0x2A, 0x33, 0x3D) });
            icon.Children.Add(new Path
            {
                Data = TripLook.Icon(kind), Stroke = DriveText, StrokeThickness = 1.6, Stretch = Stretch.Uniform, Margin = new Thickness(9),
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            });
            var head = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            head.Children.Add(icon);
            head.Children.Add(new TextBlock
            {
                Text = TripEventName(kind).ToUpperInvariant(), Foreground = DriveMuted, FontSize = 13, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
            });
            Place(head, 0, i + 1);

            // Speeding is red from the start: no yellow cell, just a dash.
            TextBlock? yellow = null;
            if (kind == DrivingEventKind.Speeding)
                Place(new TextBlock
                {
                    Text = "–", FontSize = 40, Foreground = DriveZero, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Speeding has no yellow level: it's red from the start.",
                }, 1, i + 1);
            else
                Place(yellow = Count(), 1, i + 1);
            var red = Count();
            Place(red, 2, i + 1);
            _driveCounts[kind] = (yellow, red);
        }

        void Place(UIElement element, int row, int column, int span = 1)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            Grid.SetColumnSpan(element, span);
            table.Children.Add(element);
        }

        static TextBlock Count() => new()
        {
            FontSize = 46, FontWeight = FontWeights.SemiBold, Foreground = DriveZero, Text = "0",
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };

        static StackPanel LevelHead(string text, Brush colour)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            head.Children.Add(new Ellipse { Width = 14, Height = 14, Fill = colour, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new TextBlock { Text = text, Foreground = DriveMuted, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            return head;
        }
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
        var local = hub.LocalGameMinute;
        var s = _app.Settings;
        var alertsOn = coach ? s.CoachAlerts : s.TruckAlerts;

        DriveClock.Text = clock is { } minute ? GameClock.Format(minute) : "";
        DriveState.Text = !PluginInstaller.IsInstalled(_app.Paths.InstallDir) ? "Plugin not installed: see Start here"
            : clock is null ? "Waiting for ETS2"
            : frame is null ? "Paused"
            : (coach ? "Coach" : "Truck") + (alertsOn ? "" : " · alerts off for this vehicle (events still counted)");

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

        // The job being driven and its schedule.
        var job = hub.Jobs.Current();
        var manual = hub.Trip.Bounds;
        if (job is not null)
        {
            var progress = JobText.Progress(job.Job);
            DriveJobLabel.Text = job.Noun.ToUpperInvariant() + (progress.Stages > 1 ? $" · STOP {progress.Stage} OF {progress.Stages}" : "");
            DriveRoute.Text = job.Route.Length > 0 ? job.Route : job.Noun;
            DriveStage.Visibility = Visibility.Collapsed;
            DriveJobTable.Visibility = Visibility.Visible;

            // The schedule's times are on the local economy's clock; shown on the clock the game shows (a convoy's).
            var shift = clock is { } shown && local is { } own ? (long)shown - own : 0;
            string At(uint? minute) => minute is { } m ? GameClock.Format((uint)Math.Max(1, m + shift)) : "–";

            DriveSchedStart.Text = At(progress.Start ?? job.GameStarted);
            DriveSchedEnd.Text = At(progress.End);
            DriveNext.Text = progress.NextStop ?? "–";
            var due = progress.DueText(local);
            DriveDue.Text = progress.Due is null ? "–" : due is null ? At(progress.Due) : $"{At(progress.Due)} · {LowerFirst(due)}";
            DriveDue.Foreground = due?.StartsWith("Late", StringComparison.Ordinal) == true ? DriveRed : due is null ? DriveText : DriveGreen;

            // Only where the game's own figure is to the next stop: its route advisor is routed to the last one.
            var (navTime, navDistance) = hub.Navigation;
            var eta = progress.Eta(local, navTime, navDistance);
            ShowEta(eta is not null);
            if (eta is { } arrives)
            {
                DriveEta.Text = $"{At(arrives)} · in {JobText.Minutes(arrives - local!.Value)}";
                DriveEta.Foreground = progress.Due is { } d && arrives > d ? DriveRed : DriveGreen;
            }
        }
        else
        {
            DriveJobLabel.Text = "JOB";
            DriveRoute.Text = "No job";
            DriveStage.Text = manual is { Stopped: null } ? "Capturing a trip (Trip summary page)." : "Take a job, or start capturing on Trip summary.";
            DriveStage.Visibility = Visibility.Visible;
            DriveJobTable.Visibility = Visibility.Collapsed;
        }

        // The events and figures the view counts: the job's or trip's, or since F5.
        var trip = hub.View.Snapshot();
        var resetAt = hub.ViewResetAt;
        var what = job is not null ? "THIS " + job.Noun.ToUpperInvariant()
            : manual is { Stopped: null } ? "THIS TRIP"
            : manual is not null ? "LAST TRIP · STOPPED"
            : "SINCE THE APP OPENED";
        DriveSummaryLabel.Text = resetAt is { } at ? $"{what} · SINCE CLEAR AT {at.ToLocalTime():HH:mm}" : what;

        foreach (var (kind, (yellow, red)) in _driveCounts)
        {
            if (yellow is not null) SetCount(yellow, trip?.Count(kind, AlertLevel.Yellow) ?? 0, DriveYellow);
            SetCount(red, trip?.Count(kind, AlertLevel.Red) ?? 0, DriveRed);
        }

        // From the first minute: a trip under an hour counts as an hour (F5 brings it back to 0).
        if (trip?.Score is { } value)
        {
            var band = TripSummary.Band(value);
            DriveScore.Text = value.ToString("0", c);
            // Its colour is the band: no word for it.
            DriveScore.Foreground = band == AlertLevel.Red ? DriveRed : band == AlertLevel.Yellow ? DriveYellow : DriveGreen;
            DriveScoreLabel.Text = "";
        }
        else
        {
            DriveScore.Text = "–";
            DriveScore.Foreground = DriveText;
            DriveScoreLabel.Text = "";
        }

        if (trip is null)
        {
            DriveTime.Text = DriveDistance.Text = DriveTop.Text = "–";
            DriveLast.Text = "";
            return;
        }
        DriveTime.Text = TripSummary.FormatDuration(trip.DrivingTime) +
                         (trip.RealDrivingTime is { } real && real != trip.DrivingTime ? $" · {TripSummary.FormatDuration(real)} real" : "");
        DriveDistance.Text = trip.DistanceText;
        DriveTop.Text = string.Create(c, $"{trip.TopKmh:0} km/h");
        var last = trip.Events.MaxBy(e => e.Started);
        DriveLast.Text = last is null ? "None yet"
            : $"{last.Category} · {last.Detail} · {Ago(DateTime.UtcNow - last.Started)}"; // its colour says yellow or red
        DriveLast.Foreground = last is null ? DriveMuted : TripLook.Fill(last.Level);

        static void SetCount(TextBlock cell, int count, Brush colour)
        {
            cell.Text = count.ToString(CultureInfo.InvariantCulture);
            cell.Foreground = count == 0 ? DriveZero : colour;
        }
    }

    void ShowEta(bool show)
    {
        DriveEta.Visibility = DriveEtaLabel.Visibility = DriveEtaRule.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    static string LowerFirst(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    static string Ago(TimeSpan t) =>
        t.TotalSeconds < 60 ? "just now" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min ago" : $"{(int)t.TotalHours} h ago";
}
