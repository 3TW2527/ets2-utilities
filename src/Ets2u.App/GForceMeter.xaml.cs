using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ets2u.Core;
using Ets2u.Core.Telemetry;

namespace Ets2u.App;

/// <summary>
/// A floating readout of the g-force the alerts see, for choosing thresholds: now and the peak in each
/// direction, coloured by the current vehicle's yellow and red thresholds, and a friction circle with the
/// last few seconds as a trail. Same reading as the alerts (the plugin's, smoothed over ~0.3 s).
/// Drag it anywhere; it stays there. Never takes focus from the game.
/// </summary>
public partial class GForceMeter : Window
{
    static readonly Brush Normal = Frozen(0xFF, 0xFF, 0xFF), Yellow = Frozen(0xF2, 0xB7, 0x05), Red = Frozen(0xE0, 0x3A, 0x2F);
    static readonly TimeSpan TrailLength = TimeSpan.FromSeconds(3);

    readonly TelemetryHub _hub;
    // Redrawn ten times a second; the reader samples at 50 Hz, so the peaks miss nothing.
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly Queue<(DateTime At, double Right, double Forward)> _trail = new();
    AppSettings _settings;
    GPeaks _peaks;
    string? _footer;

    public GForceMeter(AppSettings settings, TelemetryHub hub)
    {
        InitializeComponent();
        _settings = settings;
        _hub = hub;
        SourceInitialized += (_, _) => OverlayWindow.MakeNoActivate(this);
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) is not null) return;
            DragMove();
            _settings.GForceMeterLeft = Left;
            _settings.GForceMeterTop = Top;
            _settings.Save();
        };
        _timer.Tick += (_, _) => Tick();
        Closed += (_, _) => _timer.Stop();
        Left = settings.GForceMeterLeft ?? 24;
        Top = settings.GForceMeterTop ?? SystemParameters.PrimaryScreenHeight * 0.35;
        ShowValues(0, 0, DrivingThresholds.Coach);
        _timer.Start();
        Show();
    }

    public void Apply(AppSettings settings) => _settings = settings;

    static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var at = d; at is not null; at = VisualTreeHelper.GetParent(at))
            if (at is T t) return t;
        return null;
    }

    static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        _hub.ResetPeaks();
        _peaks = default;
    }

    void Tick()
    {
        var now = DateTime.UtcNow;
        var (frame, coach) = _hub.Current;
        if (frame is not { } f)
        {
            SetFooter("Waiting for the game (needs the plugin, and the game unpaused).");
            _trail.Clear();
            return;
        }
        var t = _hub.ThresholdsFor(coach);
        double forward = f.AccelForwardG, right = f.AccelRightG;
        _peaks = _hub.Peaks;
        _trail.Enqueue((now, right, forward));
        while (_trail.Count > 0 && now - _trail.Peek().At > TrailLength) _trail.Dequeue();

        var heading = coach ? "G-force · coach thresholds" : "G-force · truck thresholds";
        if (Heading.Text != heading) Heading.Text = heading;
        SetFooter($"Yellow / red: accelerating {G(t.AccelYellow)} / {G(t.AccelRed)}, braking {G(t.BrakeYellow)} / {G(t.BrakeRed)}, " +
                  $"cornering {G(t.CornerYellow)} / {G(t.CornerRed)} g");
        ShowValues(forward, right, t);
    }

    void SetFooter(string text)
    {
        if (_footer == text) return;
        _footer = text;
        Footer.Text = text;
    }

    static string G(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    static Brush Level(double g, double yellow, double red) => g >= red ? Red : g >= yellow ? Yellow : Normal;

    void ShowValues(double forward, double right, DrivingThresholds t)
    {
        static void Set(TextBlock box, double g, double yellow, double red)
        {
            var text = G(Math.Max(g, 0));
            if (box.Text != text) box.Text = text;
            var brush = Level(g, yellow, red);
            if (box.Foreground != brush) box.Foreground = brush;
        }
        Set(AccelNow, forward, t.AccelYellow, t.AccelRed);
        Set(BrakeNow, -forward, t.BrakeYellow, t.BrakeRed);
        Set(LeftNow, -right, t.CornerYellow, t.CornerRed);
        Set(RightNow, right, t.CornerYellow, t.CornerRed);
        Set(AccelPeak, _peaks.Accel, t.AccelYellow, t.AccelRed);
        Set(BrakePeak, _peaks.Brake, t.BrakeYellow, t.BrakeRed);
        Set(LeftPeak, _peaks.Left, t.CornerYellow, t.CornerRed);
        Set(RightPeak, _peaks.Right, t.CornerYellow, t.CornerRed);

        // The plot reaches a little past red, or past the reading if it's beyond that.
        var size = Plot.Width;
        var c = size / 2;
        var reach = new[] { t.AccelRed, t.BrakeRed, t.CornerRed, Math.Abs(forward), Math.Abs(right) }.Max() * 1.15;
        var scale = (c - 6) / Math.Max(reach, 0.1);
        Ring(RedRing, t.CornerRed, (t.AccelRed + t.BrakeRed) / 2);
        Ring(YellowRing, t.CornerYellow, (t.AccelYellow + t.BrakeYellow) / 2);
        Trail.Points = new PointCollection(_trail.Select(p => new Point(c + p.Right * scale, c - p.Forward * scale)));
        Canvas.SetLeft(Dot, c + right * scale - Dot.Width / 2);
        Canvas.SetTop(Dot, c - forward * scale - Dot.Height / 2);
        var level = new[]
        {
            Level(forward, t.AccelYellow, t.AccelRed), Level(-forward, t.BrakeYellow, t.BrakeRed),
            Level(Math.Abs(right), t.CornerYellow, t.CornerRed),
        };
        Dot.Fill = level.Contains(Red) ? Red : level.Contains(Yellow) ? Yellow : Normal;

        void Ring(System.Windows.Shapes.Ellipse ring, double across, double along)
        {
            ring.Width = 2 * across * scale;
            ring.Height = 2 * along * scale;
            Canvas.SetLeft(ring, c - across * scale);
            Canvas.SetTop(ring, c - along * scale);
        }
    }
}
