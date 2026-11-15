using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Ets2u.Core;
using Ets2u.Core.Telemetry;

namespace Ets2u.App;

/// <summary>
/// GreenRoad-style alerts: a yellow or red card at the top of the screen for harsh acceleration,
/// braking or cornering and for speeding, kept a few seconds after the event is over.
/// Click-through; hidden while there's nothing to show.
/// </summary>
public partial class DrivingAlertsOverlay : Window
{
    static readonly TimeSpan Linger = TimeSpan.FromSeconds(4);
    static readonly Brush Yellow = Frozen(0xF2, 0xB7, 0x05), Red = Frozen(0xE0, 0x3A, 0x2F);
    static readonly Brush CardBack = Frozen(0x18, 0x1C, 0x20, 0xE0), Detail = Frozen(0xD7, 0xDC, 0xE2);

    readonly LiveTelemetry _telemetry = new();
    readonly DrivingEventDetector _detector = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly List<DrivingEvent> _shown = new();
    readonly VehicleKind _vehicles;
    AppSettings _settings;

    public DrivingAlertsOverlay(AppSettings settings, VehicleKind vehicles)
    {
        InitializeComponent();
        _settings = settings;
        _vehicles = vehicles;
        SourceInitialized += (_, _) => OverlayWindow.MakeClickThrough(this);
        SizeChanged += (_, _) => Place();
        _timer.Tick += (_, _) => Tick();
        Closed += (_, _) => { _timer.Stop(); _telemetry.Dispose(); };
        Apply(settings);
        _timer.Start();
    }

    public void Apply(AppSettings settings)
    {
        _settings = settings;
        _detector.Speeding = settings.SpeedingAlerts;
    }

    static SolidColorBrush Frozen(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    void Place()
    {
        Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
        Top = SystemParameters.PrimaryScreenHeight * 0.12;
    }

    void Tick()
    {
        var now = DateTime.UtcNow;
        var frame = _telemetry.Read();
        // Coach or truck: each has its own switch and thresholds.
        var coach = frame is { } v && _vehicles.IsCoach(v.TruckId);
        if (frame is { } f && (coach ? _settings.CoachAlerts : _settings.TruckAlerts))
        {
            _detector.Thresholds = coach ? _settings.EffectiveCoachThresholds : _settings.EffectiveTruckThresholds;
            foreach (var e in _detector.Update(now, f.AccelForwardG, f.AccelRightG, f.SpeedMs, f.SpeedLimitMs))
                if (!_shown.Contains(e)) _shown.Add(e);
        }
        else
        {
            _detector.Reset(now);
        }

        _shown.RemoveAll(e => e.Ended is { } end && now - end > Linger);
        if (_shown.Count == 0)
        {
            if (IsVisible) Hide();
            return;
        }

        Cards.Children.Clear();
        foreach (var e in _shown.OrderBy(e => e.Started).TakeLast(4))
            Cards.Children.Add(Card(e, now));
        if (!IsVisible) Show();
    }

    static Border Card(DrivingEvent e, DateTime now)
    {
        var colour = e.Level == AlertLevel.Red ? Red : Yellow;
        var text = new StackPanel { Margin = new Thickness(12, 6, 16, 8) };
        text.Children.Add(new TextBlock { Text = e.Title, Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = e.Detail, Foreground = Detail, FontSize = 14 });

        var row = new DockPanel();
        var stripe = new Border { Width = 8, Background = colour, CornerRadius = new CornerRadius(8, 0, 0, 8) };
        DockPanel.SetDock(stripe, Dock.Left);
        row.Children.Add(stripe);
        row.Children.Add(text);

        // Fades out over the last second it is kept.
        var opacity = e.Ended is { } end ? Math.Clamp((Linger - (now - end)).TotalSeconds, 0, 1) : 1;
        return new Border
        {
            Background = CardBack, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 6),
            Opacity = opacity, Child = row,
        };
    }
}
