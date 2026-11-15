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
    static readonly Brush Yellow = Frozen(0xF2, 0xB7, 0x05), Red = Frozen(0xE0, 0x3A, 0x2F);
    static readonly Brush CardBack = Frozen(0x18, 0x1C, 0x20, 0xE0), Detail = Frozen(0xD7, 0xDC, 0xE2);

    /// <summary>One card on screen, kept and updated in place while its event lasts.</summary>
    sealed record CardView(Border Card, Border Stripe, TextBlock Title, TextBlock Detail);

    readonly TelemetryHub _hub;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly Dictionary<(DrivingEventKind, DateTime), CardView> _cards = new();

    public DrivingAlertsOverlay(AppSettings settings, TelemetryHub hub)
    {
        InitializeComponent();
        _hub = hub;
        SourceInitialized += (_, _) => OverlayWindow.MakeClickThrough(this);
        SizeChanged += (_, _) => Place();
        _timer.Tick += (_, _) => Tick();
        Closed += (_, _) => _timer.Stop();
        _timer.Start();
    }

    /// <summary>Settings are read live by the telemetry reader; nothing to do here.</summary>
    public void Apply(AppSettings settings)
    {
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
        var shown = _hub.Alerts.OrderBy(e => e.Started).TakeLast(4).ToList();
        var keys = shown.Select(e => (e.Kind, e.Started)).ToHashSet();
        foreach (var gone in _cards.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            Cards.Children.Remove(_cards[gone].Card);
            _cards.Remove(gone);
        }
        if (shown.Count == 0)
        {
            if (IsVisible) Hide();
            return;
        }
        foreach (var e in shown)
        {
            if (!_cards.TryGetValue((e.Kind, e.Started), out var view))
            {
                view = NewCard(e);
                _cards[(e.Kind, e.Started)] = view;
                Cards.Children.Add(view.Card);
            }
            // Only what changed is set, so a steady card redraws nothing.
            var colour = e.Level == AlertLevel.Red ? Red : Yellow;
            if (view.Stripe.Background != colour) view.Stripe.Background = colour;
            if (view.Detail.Text != e.Detail) view.Detail.Text = e.Detail;
            // Fades out over the last second it is kept, in tenths so it changes at most ten times.
            var opacity = e.Ended is { } end ? Math.Round(Math.Clamp((TelemetryHub.Linger - (now - end)).TotalSeconds, 0, 1), 1) : 1;
            if (view.Card.Opacity != opacity) view.Card.Opacity = opacity;
        }
        if (!IsVisible) Show();
    }

    static CardView NewCard(AlertCard e)
    {
        var text = new StackPanel { Margin = new Thickness(12, 6, 16, 8) };
        var title = new TextBlock { Text = e.Title, Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.SemiBold };
        var detail = new TextBlock { Text = e.Detail, Foreground = Detail, FontSize = 14 };
        text.Children.Add(title);
        text.Children.Add(detail);

        var row = new DockPanel();
        var stripe = new Border { Width = 8, CornerRadius = new CornerRadius(8, 0, 0, 8) };
        DockPanel.SetDock(stripe, Dock.Left);
        row.Children.Add(stripe);
        row.Children.Add(text);
        var card = new Border { Background = CardBack, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 6), Child = row };
        return new CardView(card, stripe, title, detail);
    }
}
