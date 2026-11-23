using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Ets2u.App;

/// <summary>
/// A small circled "i": hover it to read what a setting does. The text shows at once and stays while
/// the mouse is on it, so the pages keep only what you must see.
/// </summary>
public sealed class InfoTip : Border
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(InfoTip), new PropertyMetadata("", (d, _) => ((InfoTip)d).Update()));

    readonly TextBlock _tipText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 440 };

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public InfoTip()
    {
        Width = Height = 16;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1.2);
        BorderBrush = Ui.Brush(0x6A, 0x7A, 0x8C);
        Background = Brushes.Transparent;
        Margin = new Thickness(6, 0, 0, 0);
        VerticalAlignment = VerticalAlignment.Center;
        Cursor = Cursors.Help;
        Child = new TextBlock
        {
            Text = "i", FontFamily = new FontFamily("Georgia"), FontWeight = FontWeights.Bold, FontSize = 11,
            Foreground = BorderBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0),
        };
        ToolTip = new ToolTip { Content = _tipText, Padding = new Thickness(10, 8, 10, 8) };
        ToolTipService.SetInitialShowDelay(this, 0);
        ToolTipService.SetShowDuration(this, int.MaxValue);
        Update();
    }

    void Update()
    {
        _tipText.Text = Text;
        Visibility = string.IsNullOrWhiteSpace(Text) ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>
/// The top of every page: its title (with an optional info tip), one subtitle line saying what it's for,
/// and small chips for what it needs ("Mod", "Plugin", "Game closed"...).
/// </summary>
public sealed class PageHeader : StackPanel
{
    public static readonly DependencyProperty TitleProperty = Register(nameof(Title));
    public static readonly DependencyProperty SubtitleProperty = Register(nameof(Subtitle));
    /// <summary>What the page needs, separated by "|", e.g. "Mod|Game closed".</summary>
    public static readonly DependencyProperty NeedsProperty = Register(nameof(Needs));
    public static readonly DependencyProperty InfoProperty = Register(nameof(Info));

    static DependencyProperty Register(string name) => DependencyProperty.Register(
        name, typeof(string), typeof(PageHeader), new PropertyMetadata("", (d, _) => ((PageHeader)d).Build()));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public string Needs { get => (string)GetValue(NeedsProperty); set => SetValue(NeedsProperty, value); }
    public string Info { get => (string)GetValue(InfoProperty); set => SetValue(InfoProperty, value); }

    public PageHeader()
    {
        Margin = new Thickness(0, 0, 0, 8);
        Build();
    }

    void Build()
    {
        Children.Clear();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = Title, FontSize = 20, FontWeight = FontWeights.SemiBold });
        title.Children.Add(new InfoTip { Text = Info, Margin = new Thickness(8, 4, 0, 0) });
        Children.Add(title);
        if (!string.IsNullOrWhiteSpace(Subtitle))
            Children.Add(new TextBlock
            {
                Text = Subtitle, FontSize = 14, Foreground = Ui.Brush(0x4A, 0x55, 0x63), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });
        var needs = (Needs ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (needs.Length == 0) return;
        var chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        chips.Children.Add(new TextBlock
        {
            Text = "Needs", Foreground = Ui.Brush(0x6A, 0x72, 0x80), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 4),
        });
        foreach (var need in needs) chips.Children.Add(Ui.Chip(need));
        Children.Add(chips);
    }
}

/// <summary>A section heading inside a page, with an optional info tip beside it.</summary>
public sealed class Heading : StackPanel
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Heading), new PropertyMetadata("", (d, _) => ((Heading)d).Build()));
    public static readonly DependencyProperty InfoProperty = DependencyProperty.Register(
        nameof(Info), typeof(string), typeof(Heading), new PropertyMetadata("", (d, _) => ((Heading)d).Build()));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Info { get => (string)GetValue(InfoProperty); set => SetValue(InfoProperty, value); }

    public Heading()
    {
        Orientation = Orientation.Horizontal;
        Margin = new Thickness(0, 16, 0, 6);
        Build();
    }

    void Build()
    {
        Children.Clear();
        Children.Add(new TextBlock { Text = Text, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Children.Add(new InfoTip { Text = Info, Margin = new Thickness(8, 1, 0, 0) });
    }
}

static class Ui
{
    public static SolidColorBrush Brush(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public static Border Chip(string text) => new()
    {
        Background = Brush(0xE8, 0xF0, 0xF8),
        BorderBrush = Brush(0xC9, 0xD8, 0xE8),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(8, 1, 8, 2),
        Margin = new Thickness(0, 0, 6, 4),
        Child = new TextBlock { Text = text, FontSize = 11.5, Foreground = Brush(0x1F, 0x4E, 0x79) },
    };
}
