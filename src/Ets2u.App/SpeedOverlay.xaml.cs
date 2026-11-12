using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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
    const int GwlExStyle = -20;
    const int WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExLayered = 0x80000, WsExNoActivate = 0x8000000;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    readonly TelemetryReader _reader = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    AppSettings _settings;
    uint _lastSequence;
    DateTime _lastChange = DateTime.MinValue;

    public SpeedOverlay(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        SourceInitialized += (_, _) => MakeClickThrough();
        SizeChanged += (_, _) => Place();
        _timer.Tick += (_, _) => Tick();
        Closed += (_, _) => { _timer.Stop(); _reader.Dispose(); };
        Apply(settings);
        _timer.Start();
    }

    public void Apply(AppSettings settings)
    {
        _settings = settings;
        Scale.ScaleX = Scale.ScaleY = Math.Clamp(settings.OverlayScale, 0.6, 3);
        Place();
    }

    void MakeClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate);
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
        var frame = _reader.Read();
        if (frame is not { } f)
        {
            _reader.Reset();
            Hide();
            return;
        }

        // The plugin bumps Sequence every simulated frame; if it stops, the game has closed or is loading.
        if (f.Sequence != _lastSequence)
        {
            _lastSequence = f.Sequence;
            _lastChange = DateTime.UtcNow;
        }
        var live = DateTime.UtcNow - _lastChange < TimeSpan.FromSeconds(2) && f.Paused == 0;
        if (!live)
        {
            if (DateTime.UtcNow - _lastChange > TimeSpan.FromSeconds(10)) _reader.Reset();
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
