using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Ets2u.Core.Telemetry;

namespace Ets2u.App;

/// <summary>Points the trip event's chevron down when closed and up when open.</summary>
public sealed class ChevronAngleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? 180.0 : 0.0;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>The trip summary's icons and colours, GreenRoad style: a white glyph on a yellow or red disc.</summary>
static class TripLook
{
    public static readonly Brush Yellow = Frozen(0xF5, 0xA6, 0x0A), Red = Frozen(0xDC, 0x3A, 0x3F),
        Green = Frozen(0x2E, 0x9E, 0x5B), Grey = Frozen(0xB4, 0xBC, 0xC6);

    static readonly Dictionary<DrivingEventKind, Geometry> Icons = new()
    {
        // A brake disc between two arcs.
        [DrivingEventKind.Braking] = Parse("M 12,6 A 6,6 0 1 1 12,18 A 6,6 0 1 1 12,6 M 5,4 A 11,11 0 0 0 5,20 M 19,4 A 11,11 0 0 1 19,20"),
        // A curved arrow, turning right.
        [DrivingEventKind.Cornering] = Parse("M 3,20 C 3,11 8,9 20,9 M 15,4 L 20,9 L 15,14"),
        // A forward arrow with speed lines.
        [DrivingEventKind.Acceleration] = Parse("M 8,12 L 22,12 M 17,7 L 22,12 L 17,17 M 2,8 L 6,8 M 0,12 L 4,12 M 2,16 L 6,16"),
        // A speedometer with its needle near the top of the dial.
        [DrivingEventKind.Speeding] = Parse("M 3,18 A 10,10 0 1 1 21,18 M 12,15 L 17,8"),
    };

    public static Geometry Icon(DrivingEventKind kind) => Icons[kind];

    public static Brush Fill(AlertLevel level) => level == AlertLevel.Red ? Red : Yellow;

    static Geometry Parse(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>One row of the safety events list. Kept between refreshes so an opened row stays open.</summary>
public sealed class TripEventRow : INotifyPropertyChanged
{
    public sealed record Line(string Label, string Value);

    public event PropertyChangedEventHandler? PropertyChanged;

    public TripEventRow(TripEvent e) => Update(e, DateTime.UtcNow);

    public DrivingEventKind Kind { get; private set; }
    public DateTime Started { get; private set; }
    public string Category { get; private set; } = "";
    public string Variant { get; private set; } = "";
    public string Time { get; private set; } = "";
    public Brush Fill { get; private set; } = TripLook.Yellow;
    public Geometry Icon { get; private set; } = Geometry.Empty;
    public IReadOnlyList<Line> Details { get; private set; } = [];
    public bool Expanded { get; set; }

    public void Update(TripEvent e, DateTime now)
    {
        Kind = e.Kind;
        Started = e.Started;
        Category = e.Category;
        Variant = e.Variant;
        Time = e.Started.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        Fill = TripLook.Fill(e.Level);
        Icon = TripLook.Icon(e.Kind);
        var c = CultureInfo.InvariantCulture;
        var lasted = e.Duration(now).TotalSeconds;
        Details =
        [
            new("Level", e.Level == AlertLevel.Red ? "Red" : "Yellow"),
            new("Started at", e.Started.ToLocalTime().ToString("HH:mm:ss", c)),
            new("Lasted", e.Ended is null ? string.Create(c, $"{lasted:0.0} s so far") : string.Create(c, $"{lasted:0.0} s")),
            e.Kind == DrivingEventKind.Speeding
                ? new("Top speed", e.Detail)
                : new("Peak g-force", e.Detail),
            new("Speed", string.Create(c, $"{e.StartKmh:0} km/h at the start, {e.EndKmh:0} km/h at the end")),
            new("Vehicle", e.Coach ? "Coach" : "Truck"),
        ];
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}

public partial class MainWindow
{
    sealed record TripCountRow(string Category, Brush Fill, Geometry Icon, string Counts);

    static readonly DrivingEventKind[] TripKinds =
        [DrivingEventKind.Acceleration, DrivingEventKind.Braking, DrivingEventKind.Cornering, DrivingEventKind.Speeding];

    readonly ObservableCollection<TripEventRow> _tripRows = new();
    DispatcherTimer? _tripTimer;

    /// <summary>What the page shows: the trip of Start/Stop, the coach job being driven, or a saved one.</summary>
    sealed record TripSource(string Label, int Job)
    {
        public const int Manual = -2, CurrentJob = -1;
        public override string ToString() => Label;
    }

    List<JobSummary>? _jobs;
    string _sourcesKey = "";
    bool _settingSources;

    List<JobSummary> Jobs => _jobs ??= JobLog.Load();

    TripSource Selected => TripSourceBox.SelectedItem as TripSource ?? new TripSource("", TripSource.Manual);

    /// <summary>Rebuilds the Show list when the jobs change, keeping what was picked.</summary>
    void RefreshTripSources()
    {
        var current = _hub?.Jobs.Current();
        var key = $"{Jobs.Count}:{Jobs.FirstOrDefault()?.Started.Ticks}:{current?.Started.Ticks}";
        if (key == _sourcesKey) return;
        _sourcesKey = key;
        var picked = Selected;
        var c = CultureInfo.InvariantCulture;
        var items = new List<TripSource> { new("Trip (Start / Stop capturing)", TripSource.Manual) };
        if (current is not null)
            items.Add(new($"Coach job in progress, since {current.Started.ToLocalTime():HH:mm}" +
                          (current.Route.Length > 0 ? " · " + current.Route : ""), TripSource.CurrentJob));
        items.AddRange(Jobs.Select((j, i) => new TripSource(string.Create(c,
            $"{j.Started.ToLocalTime():d MMM HH:mm} · {(j.Route.Length > 0 ? j.Route + " · " : "")}{JobText.Describe(j.Outcome, null)}{(j.Trip.Score is { } s ? " · score " + Math.Round(s).ToString(c) : "")}"), i)));
        _settingSources = true;
        TripSourceBox.ItemsSource = items;
        // A job that was in progress and just ended is now the newest saved one.
        TripSourceBox.SelectedItem = picked.Job == TripSource.CurrentJob && current is null && items.Count > 1
            ? items.FirstOrDefault(i => i.Job == 0)
            : items.FirstOrDefault(i => i.Label == picked.Label) ?? items[0];
        _settingSources = false;
        _tripRows.Clear();
    }

    DateTime? _jobSeen;

    /// <summary>
    /// When a coach job starts, says so and shows it, unless a trip is being captured by hand (that stays
    /// on screen). Runs every second, whichever page is open.
    /// </summary>
    void NoticeJobStart()
    {
        var current = _hub?.Jobs.Current();
        if (current?.Started == _jobSeen) return;
        _jobSeen = current?.Started;
        if (current is null) return;
        RefreshTripSources();
        var showing = Selected.Job == TripSource.Manual && _hub?.Trip.Capturing == true ? null
            : (TripSourceBox.ItemsSource as IEnumerable<TripSource>)?.FirstOrDefault(s => s.Job == TripSource.CurrentJob);
        if (showing is not null) TripSourceBox.SelectedItem = showing;
        _app.Status("Coach job started" + (current.Route.Length > 0 ? $" ({current.Route})" : "") +
                    ": its summary is being kept (Trip summary page).");
    }

    void TripSource_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || _settingSources) return;
        _tripRows.Clear();
        ShowTrip();
    }

    void RecordJobs_Click(object sender, RoutedEventArgs e)
    {
        _app.Settings.RecordCoachJobs = RecordJobsBox.IsChecked == true;
        _app.Settings.Save();
        if (_app.Settings.RecordCoachJobs) _ = Hub;
        _app.Status(_app.Settings.RecordCoachJobs ? "Coach jobs will get a summary each." : "Coach jobs aren't summarised any more.");
    }

    /// <summary>Saves a finished coach job (called on the reader thread).</summary>
    void OnJobFinished(JobSummary job) => Dispatcher.BeginInvoke(() =>
    {
        Jobs.Insert(0, job);
        try
        {
            JobLog.Save(Jobs);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _app.Status("Couldn't save the coach job's summary: " + ex.Message);
            return;
        }
        _app.Status($"Coach job {JobText.Describe(job.Outcome, job.Result).ToLowerInvariant()}" +
                    (job.Route.Length > 0 ? $" ({job.Route})" : "") + ". Its summary is on the Trip summary page.");
        RefreshTripSources();
        if (TripTab.IsSelected) ShowTrip();
    });

    void ForgetJob_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.Job < 0 || Selected.Job >= Jobs.Count) return;
        Jobs.RemoveAt(Selected.Job);
        JobLog.Save(Jobs);
        _sourcesKey = "";
        RefreshTripSources();
        ShowTrip();
    }

    static readonly Dictionary<TripSort, string> SortNames = new()
    {
        [TripSort.Time] = "Time", [TripSort.Type] = "Type", [TripSort.Level] = "Level", [TripSort.Duration] = "How long",
    };

    static readonly Dictionary<bool, string> SortDirections = new() { [false] = "Ascending", [true] = "Descending" };

    void TripSort_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || _settingSort) return;
        var s = _app.Settings;
        s.TripSort = TripSortBox.SelectedValue is TripSort by ? by : TripSort.Time;
        s.TripSortDescending = TripSortDirectionBox.SelectedValue is true;
        s.Save();
        ShowTrip();
    }

    bool _settingSort;

    void RemoveTripEvent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TripEventRow row) return;
        var removed = Selected.Job switch
        {
            TripSource.Manual => _hub?.Trip.Remove(row.Started, row.Kind) == true,
            TripSource.CurrentJob => _hub?.Jobs.RemoveEvent(row.Started, row.Kind) == true,
            var i when i >= 0 && i < Jobs.Count => RemoveSaved(i, row),
            _ => false,
        };
        if (!removed) return;
        _app.Status($"Removed the {row.Category.ToLowerInvariant()} event at {row.Started.ToLocalTime():HH:mm:ss}; the score no longer counts it.");
        _sourcesKey = ""; // the Show list has each job's score
        ShowTrip();
    }

    bool RemoveSaved(int index, TripEventRow row)
    {
        var job = Jobs[index];
        if (!job.Trip.Events.Any(ev => ev.Started == row.Started && ev.Kind == row.Kind)) return false;
        Jobs[index] = job with { Trip = job.Trip.Without(row.Started, row.Kind) };
        try
        {
            JobLog.Save(Jobs);
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Jobs[index] = job;
            _app.Status("Couldn't save the coach job's summary: " + ex.Message);
            return false;
        }
    }

    void RefreshTrip()
    {
        if (_tripTimer is null)
        {
            TripEvents.ItemsSource = _tripRows;
            // Once a second while the page is shown; the reader thread captures at 50 Hz either way.
            _tripTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tripTimer.Tick += (_, _) =>
            {
                NoticeJobStart();
                if (TripTab.IsSelected && IsVisible) ShowTrip();
            };
            _tripTimer.Start();
            Closed += (_, _) => _tripTimer.Stop();
        }
        TripPluginStatus.Text = DescribePlugin();
        RecordJobsBox.IsChecked = _app.Settings.RecordCoachJobs;
        _settingSort = true;
        TripSortBox.ItemsSource = SortNames;
        TripSortBox.DisplayMemberPath = "Value";
        TripSortBox.SelectedValuePath = "Key";
        TripSortBox.SelectedValue = _app.Settings.TripSort;
        TripSortDirectionBox.ItemsSource = SortDirections;
        TripSortDirectionBox.DisplayMemberPath = "Value";
        TripSortDirectionBox.SelectedValuePath = "Key";
        TripSortDirectionBox.SelectedValue = _app.Settings.TripSortDescending;
        _settingSort = false;
        RefreshTripSources();
        ShowTrip();
    }

    void StartTrip_Click(object sender, RoutedEventArgs e)
    {
        if (_hub?.Trip.Snapshot() is { Events.Count: > 0 } last && !last.Capturing
            && MessageBox.Show(this, "Start a new trip? The last trip's summary will be cleared.", "ETS2 Utilities",
                   MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        Hub.Trip.Start(DateTime.UtcNow);
        _tripRows.Clear();
        _app.Status("Capturing a trip. Drive, then press Stop capturing.");
        ShowTrip();
    }

    void StopTrip_Click(object sender, RoutedEventArgs e)
    {
        _hub?.Trip.Stop(DateTime.UtcNow);
        _app.Status("Trip captured.");
        ShowTrip();
    }

    /// <summary>The job shown, when the page shows one.</summary>
    JobSummary? ShownJob => Selected.Job switch
    {
        TripSource.CurrentJob => _hub?.Jobs.Current(),
        >= 0 when Selected.Job < Jobs.Count => Jobs[Selected.Job],
        _ => null,
    };

    void CopyTrip_Click(object sender, RoutedEventArgs e)
    {
        var text = Selected.Job == TripSource.Manual ? _hub?.Trip.Snapshot()?.ToText() : ShownJob?.ToText();
        if (text is null) return;
        _app.Status(ClipboardText.TrySet(new System.Windows.Interop.WindowInteropHelper(this).Handle, text) ? "Summary copied." : "The clipboard is busy; try again.");
    }

    void ShowTrip()
    {
        RefreshTripSources();
        var manual = Selected.Job == TripSource.Manual;
        var job = manual ? null : ShownJob;
        var trip = manual ? _hub?.Trip.Snapshot() : job?.Trip;
        var now = DateTime.UtcNow;
        StartTripButton.Visibility = StopTripButton.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        ForgetJobButton.Visibility = Selected.Job >= 0 ? Visibility.Visible : Visibility.Collapsed;
        StartTripButton.IsEnabled = trip is not { Capturing: true };
        StopTripButton.IsEnabled = trip is { Capturing: true };
        CopyTripButton.IsEnabled = trip is not null;
        TripTotals.Visibility = trip is null ? Visibility.Collapsed : Visibility.Visible;
        TripJobDetails.Visibility = job is null ? Visibility.Collapsed : Visibility.Visible;
        if (job is not null)
        {
            // A few words about the job; its result is in the status line above.
            TripJobDetails.Text = JobText.Facts(job.Job);
            TripJobDetails.Visibility = TripJobDetails.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        if (trip is null)
        {
            TripStatus.Text = manual ? "No trip captured yet." : "No coach job in progress.";
            TripNoEventsText.Text = manual ? "Press Start capturing, then drive." : "";
            TripNoEvents.Visibility = Visibility.Visible;
            _tripRows.Clear();
            return;
        }

        var c = CultureInfo.InvariantCulture;
        var started = trip.Started.ToLocalTime();
        TripStatus.Text = job is not null
            ? (job.Outcome == JobOutcome.InProgress
                ? $"Coach job since {started:HH:mm}{(job.Route.Length > 0 ? ", " + job.Route : "")}."
                : $"Coach job from {started:d MMM HH:mm} to {job.Ended!.Value.ToLocalTime():HH:mm}{(job.Route.Length > 0 ? ", " + job.Route : "")}: " +
                  JobText.Describe(job.Outcome, job.Result) + ".")
            : trip.Capturing
            ? (_hub!.Current.Frame is null
                ? $"Capturing since {started:HH:mm}. Waiting for the game (needs the plugin, and the game unpaused)."
                : $"Capturing since {started:HH:mm}.")
            : $"Trip from {started:HH:mm} to {trip.Stopped!.Value.ToLocalTime():HH:mm}.";
        var vehicle = (trip.InCoach, trip.InTruck) switch
        {
            (true, true) => " · coach and truck",
            (true, false) => " · coach",
            (false, true) => " · truck",
            _ => "",
        };
        TripTotalsText.Text = string.Create(c, $"{trip.DrivingText} · {trip.DistanceText} · top speed {trip.TopKmh:0} km/h{vehicle}");

        if (trip.Score is { } score)
        {
            var band = TripSummary.Band(score);
            TripScoreText.Text = Math.Round(score).ToString("0", c);
            TripScoreBadge.Background = band is { } b ? TripLook.Fill(b) : TripLook.Green;
            TripScoreLabel.Text = "Safety score";
            TripScoreBadge.ToolTip = "Events per 10 hours of driving in game time (as the game's clock and job screens count it), " +
                                     "a red event counting as two. Green under 20, yellow under 40, red from 40.";
        }
        else
        {
            TripScoreText.Text = "–";
            TripScoreBadge.Background = TripLook.Grey;
            TripScoreLabel.Text = "Score after 10 min";
            TripScoreBadge.ToolTip = null;
        }

        TripCounts.ItemsSource = TripKinds.Select(kind =>
        {
            int yellow = trip.Count(kind, AlertLevel.Yellow), red = trip.Count(kind, AlertLevel.Red);
            var counts = kind == DrivingEventKind.Speeding ? $"{red}"
                : yellow + red == 0 ? "0" : $"{yellow} yellow · {red} red";
            var fill = red > 0 ? TripLook.Red : yellow > 0 ? TripLook.Yellow : TripLook.Green;
            return new TripCountRow(kind switch
            {
                DrivingEventKind.Acceleration => "Acceleration",
                DrivingEventKind.Braking => "Braking",
                DrivingEventKind.Cornering => "Cornering",
                _ => "Speeding",
            }, fill, TripLook.Icon(kind), counts);
        }).ToList();

        // Rows are kept by event, so an opened row stays open however the list is sorted.
        var s = _app.Settings;
        var ordered = TripEventOrder.Sort(trip.Events, s.TripSort, s.TripSortDescending, now);
        var kept = _tripRows.ToDictionary(r => (r.Started, r.Kind));
        var rows = ordered.Select(ev =>
        {
            if (kept.TryGetValue((ev.Started, ev.Kind), out var row)) row.Update(ev, now);
            else row = new TripEventRow(ev);
            return row;
        }).ToList();
        if (!rows.SequenceEqual(_tripRows))
        {
            _tripRows.Clear();
            foreach (var row in rows) _tripRows.Add(row);
        }

        TripNoEventsText.Text = trip.Capturing ? "No safety events yet. Keep it smooth." : job is not null ? "No safety events on this job." : "No safety events on this trip.";
        TripNoEvents.Visibility = trip.Events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
