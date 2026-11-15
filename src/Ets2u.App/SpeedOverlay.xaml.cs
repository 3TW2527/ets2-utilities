using System.Windows;
using System.Windows.Threading;
using Ets2u.Core;
using Ets2u.Core.Telemetry;

namespace Ets2u.App;

/// <summary>
/// A click-through, always-on-top readout of the truck's speed in km/h and mph, fed by the
/// ets2u_telemetry plugin. Hidden while the game is paused, in menus or not running.
/// Visible over the game in borderless or windowed mode; exclusive fullscreen may hide it.
/// </summary>
public partial class SpeedOverlay : Window
{
    readonly TelemetryHub _hub;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    AppSettings _settings;

    public SpeedOverlay(AppSettings settings, TelemetryHub hub)
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
        Scale.ScaleX = Scale.ScaleY = Math.Clamp(settings.OverlayScale, 0.6, 3);
        // As wide as the widest reading from the start: a window that resizes as the digits change is
        // rebuilt by Windows each time, which is what made the overlay stutter.
        SpeedLine.MinWidth = Widest(SpeedLine, SpeedText.Format(88.8f));
        LimitLine.MinWidth = Widest(LimitLine, SpeedText.FormatLimit(88.8f)!);
        Place();
    }

    void Place()
    {
        var area = SystemParameters.WorkArea;
        var screenW = SystemParameters.PrimaryScreenWidth;
        var screenH = SystemParameters.PrimaryScreenHeight;
        const double margin = 24;
        var w = ActualWidth;
        var h = ActualHeight;
        (Left, Top) = _settings.OverlayCorner switch
        {
            OverlayCorner.TopLeft => (margin, margin),
            OverlayCorner.TopCenter => ((screenW - w) / 2, margin),
            OverlayCorner.BottomLeft => (margin, screenH - h - margin * 4),
            OverlayCorner.BottomRight => (screenW - w - margin, screenH - h - margin * 4),
            _ => (Math.Max(area.Left, screenW - w - margin), margin),
        };
    }

    void Tick()
    {
        if (_hub.Current.Frame is not { } f)
        {
            if (IsVisible) Hide();
            return;
        }

        // Only what changed is set, so an unchanged speed redraws nothing.
        SetText(SpeedLine, SpeedText.Format(f.SpeedMs));
        var limit = _settings.OverlayShowLimit ? SpeedText.FormatLimit(f.SpeedLimitMs) : null;
        SetText(LimitLine, limit ?? "");
        var visibility = limit is null ? Visibility.Collapsed : Visibility.Visible;
        if (LimitLine.Visibility != visibility) LimitLine.Visibility = visibility;
        if (!IsVisible) Show();
    }

    static double Widest(System.Windows.Controls.TextBlock box, string sample)
    {
        var text = new System.Windows.Media.FormattedText(sample, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new System.Windows.Media.Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch),
            box.FontSize, System.Windows.Media.Brushes.White, 1.0);
        return Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 2;
    }

    static void SetText(System.Windows.Controls.TextBlock box, string text)
    {
        if (box.Text != text) box.Text = text;
    }
}
