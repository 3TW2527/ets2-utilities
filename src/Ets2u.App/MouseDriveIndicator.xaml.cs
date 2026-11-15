using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Ets2u.Core.Telemetry;

namespace Ets2u.App;

/// <summary>
/// While mouse driving is on the game's cursor is gone, so this shows where the mouse holds the
/// wheel and pedals, as OMSI's steering cross does: across is the wheel, up the throttle, down the
/// brake. Dimmed while the mouse is held (looking round). Redrawn only when something changed, as
/// a layered window repaints in software.
/// </summary>
public partial class MouseDriveIndicator : Window
{
    const double PadWidth = 180, PadHeight = 140;

    readonly LiveTelemetry _telemetry = new();
    readonly DispatcherTimer _timer;
    double _x = double.NaN, _y = double.NaN, _opacity = -1;
    bool _shown;

    public MouseDriveIndicator()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => OverlayWindow.MakeClickThrough(this);
        SizeChanged += (_, _) => Place();
        // Background priority: never ahead of the other overlays' input and rendering.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Tick();
        Closed += (_, _) => { _timer.Stop(); _telemetry.Dispose(); };
        // Opened once and kept: it is shown and hidden by its opacity, not by creating and destroying
        // a window each time the mouse driving is switched.
        Opacity = 0;
        ShowActivated = false;
        Show();
        _timer.Start();
    }

    void Place()
    {
        Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
        Top = SystemParameters.PrimaryScreenHeight - ActualHeight - 120;
    }

    void Tick()
    {
        if (_telemetry.Read() is not { MouseState: not MouseDriveState.Off } f)
        {
            if (_shown) { _shown = false; Opacity = 0; _opacity = -1; }
            return;
        }
        _shown = true;

        // The pad's middle is the wheel straight and both pedals up.
        var steer = Math.Clamp(f.MouseSteer, -1, 1);
        var pedal = Math.Clamp(f.MouseBrake, 0, 1) - Math.Clamp(f.MouseThrottle, 0, 1);
        var x = Math.Round((steer + 1) / 2 * PadWidth);
        var y = Math.Round((pedal + 1) / 2 * PadHeight);
        var opacity = f.MouseState == MouseDriveState.Held ? 0.45 : 1;

        if (x != _x || y != _y)
        {
            _x = x; _y = y;
            Canvas.SetLeft(CursorH, x - CursorH.Width / 2);
            Canvas.SetTop(CursorH, y - CursorH.Height / 2);
            Canvas.SetLeft(CursorV, x - CursorV.Width / 2);
            Canvas.SetTop(CursorV, y - CursorV.Height / 2);
        }
        if (opacity != _opacity)
        {
            _opacity = opacity;
            Opacity = opacity;
        }
    }
}
