using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Ets2u.Core;
using Ets2u.Core.Telemetry;

namespace Ets2u.App;

/// <summary>
/// The alerts as a GreenRoad-style light bar: green, yellow and red lamps in a row, green on the left.
/// It shows two things. An event flashes its lamp (yellow or red) while it's on and for the same few
/// seconds after it as a card. Between events one lamp stays lit, steady, for how the recent driving has
/// gone: the safety score of the last 15, 30 or 60 minutes of driving (<see cref="DriveStatus"/>), so too
/// many events keep it yellow or red until they roll out of that window. Click-through; hidden while the
/// game is paused or closed, and in a vehicle whose alerts are off.
/// </summary>
public partial class TrafficLightOverlay : Window
{
    static readonly Brush GreenOn = Frozen(0x2E, 0xC4, 0x5B), YellowOn = Frozen(0xF2, 0xB7, 0x05), RedOn = Frozen(0xE0, 0x3A, 0x2F);
    static readonly Brush GreenOff = Frozen(0x2E, 0xC4, 0x5B, 0x38), YellowOff = Frozen(0xF2, 0xB7, 0x05, 0x38), RedOff = Frozen(0xE0, 0x3A, 0x2F, 0x38);

    readonly TelemetryHub _hub;
    /// <summary>A flashing lamp is on this long, then off this long.</summary>
    static readonly TimeSpan FlashHalf = TimeSpan.FromMilliseconds(250);

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    AppSettings _settings;
    (AlertLevel? Level, bool On)? _shown;

    public TrafficLightOverlay(AppSettings settings, TelemetryHub hub)
    {
        InitializeComponent();
        _settings = settings;
        _hub = hub;
        SourceInitialized += (_, _) => OverlayWindow.MakeClickThrough(this);
        SizeChanged += (_, _) => Place();
        _timer.Tick += (_, _) => Tick();
        Closed += (_, _) => _timer.Stop();
        Apply(settings);
        _timer.Start();
    }

    public void Apply(AppSettings settings)
    {
        _settings = settings;
        Scale.ScaleX = Scale.ScaleY = Math.Clamp(settings.TrafficLightScale, 0.6, 3);
        Place();
    }

    static SolidColorBrush Frozen(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    void Place()
    {
        var screenW = SystemParameters.PrimaryScreenWidth;
        var screenH = SystemParameters.PrimaryScreenHeight;
        const double margin = 24;
        var w = ActualWidth;
        var h = ActualHeight;
        (Left, Top) = _settings.TrafficLightCorner switch
        {
            OverlayCorner.TopLeft => (margin, margin),
            OverlayCorner.TopRight => (screenW - w - margin, margin),
            OverlayCorner.BottomLeft => (margin, screenH - h - margin * 4),
            OverlayCorner.BottomRight => (screenW - w - margin, screenH - h - margin * 4),
            _ => ((screenW - w) / 2, margin),
        };
    }

    void Tick()
    {
        var (frame, coach) = _hub.Current;
        if (frame is null || !(coach ? _settings.CoachAlerts : _settings.TruckAlerts))
        {
            if (IsVisible) Hide();
            _shown = null;
            return;
        }
        var alerts = _hub.Alerts;
        var light = AlertLight.Show(alerts.Select(a => a.Level).ToList(), _hub.Score.Level);
        // A flash starts lit, from the latest event, so each new one is seen at once.
        var on = !light.Flashing ||
                 (long)((DateTime.UtcNow - alerts.Max(a => a.Started)) / FlashHalf) % 2 == 0;
        if (_shown != (light.Level, on))
        {
            // Only on a change, so a steady light redraws nothing. Null is green.
            GreenLamp.Fill = on && light.Level is null ? GreenOn : GreenOff;
            YellowLamp.Fill = on && light.Level == AlertLevel.Yellow ? YellowOn : YellowOff;
            RedLamp.Fill = on && light.Level == AlertLevel.Red ? RedOn : RedOff;
            _shown = (light.Level, on);
        }
        if (!IsVisible) Show();
    }
}
