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
    readonly LiveTelemetry _telemetry = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    AppSettings _settings;

    public SpeedOverlay(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
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
        Scale.ScaleX = Scale.ScaleY = Math.Clamp(settings.OverlayScale, 0.6, 3);
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
        if (_telemetry.Read() is not { } f)
        {
            Hide();
            return;
        }

        SpeedLine.Text = SpeedText.Format(f.SpeedMs);
        var limit = _settings.OverlayShowLimit ? SpeedText.FormatLimit(f.SpeedLimitMs) : null;
        LimitLine.Text = limit ?? "";
        LimitLine.Visibility = limit is null ? Visibility.Collapsed : Visibility.Visible;
        if (!IsVisible) Show();
    }
}
