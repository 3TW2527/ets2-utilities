using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ets2u.Core;
using Ets2u.Core.Features;

namespace Ets2u.App;

/// <summary>The coach destination display page.</summary>
public partial class MainWindow
{
    /// <summary>Saved display texts, one per line ("route | text"). Kept out of settings.json: it can be huge.</summary>
    static string BusListPath => Path.Combine(AppDirs.Settings, "destination_texts.txt");

    const int BusListShown = 1000;

    void RefreshBusDisplay()
    {
        var d = _app.Settings.BusDisplay;
        BusDisplayBox.IsChecked = d.Enabled;
        BusRouteBox.Text = d.Route;
        BusTextBox.Text = d.Text;
        BusColorBox.SelectedItem = d.Color;
        BusSizeSlider.Value = d.Size;
        var hasDlc = _app.Game?.TryRead(BusDisplay.GamePath) is not null;
        BusDisplayStatus.Text = !hasDlc && _app.Game is not null
            ? "The display's template wasn't found in the game's files: this needs the coaches DLC."
            : DescribeMod(BusDisplay.Mod);
        UpdateBusPreview();
        if (BusList.ItemsSource is null) FilterBusList();
    }

    BusDisplaySettings ReadBusDisplay() => new()
    {
        Enabled = BusDisplayBox.IsChecked == true,
        Route = BusRouteBox.Text,
        Text = BusTextBox.Text,
        Color = BusColorBox.SelectedItem is DisplayColor c ? c : DisplayColor.Amber,
        Size = Math.Round(BusSizeSlider.Value, 2),
    };

    void BusDisplay_Changed(object sender, RoutedEventArgs e) => UpdateBusPreview();

    void BusSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateBusPreview();

    void UpdateBusPreview()
    {
        if (BusPreview is null || BusColorBox is null) return;
        var d = ReadBusDisplay();
        string content;
        try
        {
            content = BusDisplay.Content(d.Route, d.Text).Replace("%0", "Klagenfurt");
            BusDisplayNote.Text = BusDisplay.ShowsGameDestination(d)
                ? "{destination} is the job's destination (\"Klagenfurt\" in the preview). Without a job it's blank, and the rest of your text still shows."
                : "No {destination}: the display always shows this text, on a job or not.";
        }
        catch (FormatException ex)
        {
            content = "";
            BusDisplayNote.Text = ex.Message;
        }
        BusPreview.Text = content;
        var abgr = Convert.ToUInt32(BusDisplay.ColorValue(d.Color), 16);
        BusPreview.Foreground = new SolidColorBrush(Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16)));
        // The game's letters are 5 x 5.5 times its base font on a 512 x 85 panel: roughly 46 px tall.
        BusPreview.FontSize = 46 * d.Size;
    }

    void InsertDestination_Click(object sender, RoutedEventArgs e)
    {
        var at = BusTextBox.CaretIndex;
        BusTextBox.Text = BusTextBox.Text.Insert(at, BusDisplay.DestinationToken);
        BusTextBox.CaretIndex = at + BusDisplay.DestinationToken.Length;
        BusTextBox.Focus();
    }

    void ApplyBusDisplay_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var d = ReadBusDisplay();
        BusDisplay.Content(d.Route, d.Text); // reports text that won't fit before anything is written
        _app.Settings.BusDisplay = d;
        if (d.Enabled && _app.Game?.TryRead(BusDisplay.GamePath) is null)
            throw new InvalidOperationException("The display's template isn't in the game's files: the destination display needs the coaches DLC.");
        return _app.ApplyMods();
    });

    // ---------- saved texts ----------

    CancellationTokenSource? _busSearch;

    void BusListFilter_Changed(object sender, TextChangedEventArgs e) => FilterBusList();

    /// <summary>Searches the list file in the background: it may hold millions of lines.</summary>
    async void FilterBusList()
    {
        _busSearch?.Cancel();
        var cancel = _busSearch = new CancellationTokenSource();
        var find = BusListFilter.Text.Trim();
        try
        {
            var (shown, total) = await Task.Run(() =>
            {
                var found = new List<string>();
                var count = 0;
                if (!File.Exists(BusListPath)) return (found, 0);
                using var reader = new StreamReader(BusListPath, BusDisplay.ListEncoding);
                foreach (var (route, text) in BusDisplay.ReadList(reader))
                {
                    if ((count & 0xFFF) == 0) cancel.Token.ThrowIfCancellationRequested();
                    var line = BusDisplay.ListLine(route, text);
                    if (find.Length > 0 && !line.Contains(find, StringComparison.CurrentCultureIgnoreCase)) continue;
                    count++;
                    if (found.Count < BusListShown) found.Add(line);
                }
                return (found, count);
            }, cancel.Token);
            BusList.ItemsSource = shown;
            BusListCount.Text = !File.Exists(BusListPath) ? "No saved texts yet."
                : total > shown.Count ? $"{total:N0} match(es); showing the first {shown.Count:N0}. Type to narrow it down."
                : $"{total:N0} text(s).";
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException ex)
        {
            BusListCount.Text = "Couldn't read the list: " + ex.Message;
        }
    }

    void UseBusLine(string line)
    {
        var (route, text) = BusDisplay.ReadList(new StringReader(line)).FirstOrDefault();
        BusRouteBox.Text = route ?? "";
        BusTextBox.Text = text ?? "";
        BusDisplayBox.IsChecked = true;
        _app.Status("Picked \"" + line + "\". Press Apply to put it on the display.");
    }

    void BusList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => BusUseSelected_Click(sender, e);

    void BusUseSelected_Click(object sender, RoutedEventArgs e)
    {
        if (BusList.SelectedItem is string line) UseBusLine(line);
    }

    /// <summary>One line at random from the whole list (or from the matches), read in one pass.</summary>
    async void BusRandom_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(BusListPath)) return;
        var find = BusListFilter.Text.Trim();
        var picked = await Task.Run(() =>
        {
            string? choice = null;
            var seen = 0;
            using var reader = new StreamReader(BusListPath, BusDisplay.ListEncoding);
            foreach (var (route, text) in BusDisplay.ReadList(reader))
            {
                var line = BusDisplay.ListLine(route, text);
                if (find.Length > 0 && !line.Contains(find, StringComparison.CurrentCultureIgnoreCase)) continue;
                if (Random.Shared.Next(++seen) == 0) choice = line; // reservoir sampling
            }
            return choice;
        });
        if (picked is not null) UseBusLine(picked);
    }

    void BusSaveCurrent_Click(object sender, RoutedEventArgs e)
    {
        var d = ReadBusDisplay();
        if (d.Text.Trim().Length == 0 && d.Route.Trim().Length == 0) return;
        try
        {
            Directory.CreateDirectory(AppDirs.Settings);
            File.AppendAllText(BusListPath, BusDisplay.ListLine(d.Route.Trim(), d.Text.Trim()) + Environment.NewLine, BusDisplay.ListEncoding);
            _app.Status("Saved to the list.");
            FilterBusList();
        }
        catch (IOException ex)
        {
            _app.Status("Couldn't save to the list: " + ex.Message);
        }
    }

    void BusImport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import display texts (one per line: \"route | text\" or \"text\")",
            Filter = "Text files (*.txt;*.csv)|*.txt;*.csv|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        Run(() =>
        {
            Directory.CreateDirectory(AppDirs.Settings);
            var added = 0;
            using (var reader = new StreamReader(dialog.FileName, detectEncodingFromByteOrderMarks: true))
            using (var writer = new StreamWriter(BusListPath, append: true, BusDisplay.ListEncoding))
            {
                foreach (var (route, text) in BusDisplay.ReadList(reader))
                {
                    writer.WriteLine(BusDisplay.ListLine(route, text));
                    added++;
                }
            }
            return $"Imported {added:N0} text(s) into the list.";
        });
        FilterBusList();
    }

    void BusOpenList_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppDirs.Settings);
        if (!File.Exists(BusListPath))
            File.WriteAllText(BusListPath, "# One display text per line: \"route | text\" or just \"text\". {destination} = the job's destination." +
                                           Environment.NewLine, BusDisplay.ListEncoding);
        Process.Start(new ProcessStartInfo(BusListPath) { UseShellExecute = true });
    }
}
