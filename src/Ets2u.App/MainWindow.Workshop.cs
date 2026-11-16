using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ets2u.Core;
using Ets2u.Core.Liveries;

namespace Ets2u.App;

/// <summary>
/// Livery pack page: Workshop items added by link. The app reads the item from Steam first, then downloads
/// it itself with DepotDownloader, so nobody has to subscribe (and leave a stray mod in Mod Manager).
/// </summary>
public partial class MainWindow
{
    WorkshopDetails? _checked;
    string? _checkedAuthor;
    CancellationTokenSource? _downloadCts;
    SteamQrWindow? _qrWindow;

    void RefreshSteamAccount()
    {
        var account = _app.Settings.LiveryPack.SteamAccount;
        SteamAccountText.Text = account is null
            ? "Steam: not signed in yet. The first download shows a QR code to scan with the Steam mobile app; no password is typed into this app."
            : $"Steam: downloads use the sign-in DepotDownloader remembers for {account}.";
        SteamSignOutButton.Visibility = account is null ? Visibility.Collapsed : Visibility.Visible;
    }

    void SteamSignOut_Click(object sender, RoutedEventArgs e)
    {
        _app.Settings.LiveryPack.SteamAccount = null;
        _app.Settings.Save();
        RefreshSteamAccount();
        _app.Status("The app forgot the Steam sign-in; the next download asks for a QR code again. To end DepotDownloader's " +
                    "session too, remove it under Authorized devices in your Steam account settings.");
    }

    // ---------- checking a link ----------

    void WorkshopIdBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) WorkshopCheck_Click(sender, e);
    }

    async void WorkshopCheck_Click(object sender, RoutedEventArgs e)
    {
        if (Workshop.ParseId(WorkshopIdBox.Text) is not { } id)
        {
            _app.Status("Paste a Workshop link (…filedetails/?id=123456789) or the number itself.");
            return;
        }
        WorkshopCheckButton.IsEnabled = false;
        _app.Status($"Asking Steam about Workshop item {id}…");
        try
        {
            var details = await WorkshopApi.GetDetailsAsync(id);
            var author = details.CreatorId is { } creator ? await WorkshopApi.GetPersonaNameAsync(creator) : null;
            ShowChecked(details, author);
            _app.Status(details.Problem ?? $"Found {details.Title}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _app.Status("Couldn't reach Steam: " + ex.Message);
        }
        finally
        {
            WorkshopCheckButton.IsEnabled = true;
        }
    }

    void ShowChecked(WorkshopDetails d, string? author)
    {
        _checked = d;
        _checkedAuthor = author;
        var subscribed = Workshop.FindItemDir(_app.Paths, d.Id) is not null;
        var inPack = _app.Settings.LiveryPack.Sources.Any(s => s.Kind == LiverySourceKind.Workshop && s.WorkshopId == d.Id);

        WorkshopCheckTitle.Text = d.Title;
        var info = new List<string>();
        if (author is not null) info.Add("by " + author);
        info.Add("Workshop " + d.Id);
        if (d.FileSize > 0) info.Add($"{d.FileSize / 1048576.0:0.0} MB");
        if (d.TimeUpdated > 0) info.Add("updated " + DateTimeOffset.FromUnixTimeSeconds(d.TimeUpdated).LocalDateTime.ToString("d MMM yyyy"));
        if (d.Tags.Count > 0) info.Add("tags: " + string.Join(", ", d.Tags));
        WorkshopCheckInfo.Text = string.Join(" · ", info);

        var lines = new List<string>();
        if (d.Problem is { } problem) lines.Add("✖ " + problem);
        else
        {
            lines.Add("✔ An item for Euro Truck Simulator 2.");
            lines.Add(d.TaggedPaintJob
                ? "✔ Tagged as a paint job (livery)."
                : "⚠ Not tagged as a paint job, so it may not be a livery. The paint job count shows once it's added.");
            if (subscribed) lines.Add("You're subscribed to it, so Steam's copy is used and nothing is downloaded.");
            if (inPack) lines.Add("It's already in the pack.");
            else lines.Add("Before adding it, check on its Workshop page that the creator allows it to be repacked.");
        }
        WorkshopCheckResult.Text = string.Join("\n", lines);
        WorkshopCheckResult.Foreground = d.Problem is null ? SystemColors.ControlTextBrush : Brushes.Firebrick;

        WorkshopAddCheckedButton.Content = subscribed ? "Add" : "Download and add";
        WorkshopAddCheckedButton.IsEnabled = d.Problem is null && !inPack && _downloadCts is null;

        WorkshopPreview.Source = null;
        WorkshopPreview.Visibility = Visibility.Collapsed;
        if (d.PreviewUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            try
            {
                WorkshopPreview.Source = new BitmapImage(uri);
                WorkshopPreview.Visibility = Visibility.Visible;
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException) { }
        }
        WorkshopCheckPanel.Visibility = Visibility.Visible;
    }

    void WorkshopCheckPage_Click(object sender, RoutedEventArgs e)
    {
        if (_checked is { } d) OpenUrl(Workshop.PageUrl(d.Id));
    }

    void WorkshopCheckCancel_Click(object sender, RoutedEventArgs e)
    {
        WorkshopCheckPanel.Visibility = Visibility.Collapsed;
        _checked = null;
    }

    async void WorkshopAddChecked_Click(object sender, RoutedEventArgs e)
    {
        if (_checked is not { Problem: null } d) return;
        WorkshopCheckPanel.Visibility = Visibility.Collapsed;
        _checked = null;
        var source = new LiverySource
        {
            Kind = LiverySourceKind.Workshop, WorkshopId = d.Id, Name = d.Title, Author = _checkedAuthor,
        };
        if (Workshop.FindItemDir(_app.Paths, d.Id) is null)
        {
            var done = await DownloadWorkshop([(d.Id, d.Title, d.TimeUpdated)], allowSignIn: true);
            if (!done.Contains(d.Id)) return; // DownloadWorkshop said why
            source.DownloadedUpdated = d.TimeUpdated;
        }
        WorkshopIdBox.Text = "";
        AddLiverySource(source);
    }

    // ---------- downloading ----------

    async void WorkshopDownloadMissing_Click(object sender, RoutedEventArgs e)
    {
        SaveLiveryPage();
        if (!await UpdateWorkshopSources(allowSignIn: true, reportNothing: true)) return;
        RefreshLiveries();
    }

    void WorkshopDownloadCancel_Click(object sender, RoutedEventArgs e) => _downloadCts?.Cancel();

    /// <summary>
    /// Downloads the pack's Workshop items that aren't subscribed and are missing or updated on the
    /// Workshop. True if anything was downloaded.
    /// </summary>
    async Task<bool> UpdateWorkshopSources(bool allowSignIn, bool reportNothing = false)
    {
        var paths = _app.Paths;
        var sources = _app.Settings.LiveryPack.Sources
            .Where(s => s is { Kind: LiverySourceKind.Workshop, WorkshopId: not null } && Workshop.FindItemDir(paths, s.WorkshopId) is null)
            .ToList();
        if (sources.Count == 0)
        {
            if (reportNothing) _app.Status("Every Workshop item in the list is on this PC.");
            return false;
        }

        Dictionary<string, WorkshopDetails> details;
        try
        {
            details = (await WorkshopApi.GetDetailsAsync(sources.Select(s => s.WorkshopId!).Distinct().ToList()))
                .ToDictionary(d => d.Id);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            AppDirs.Log("Workshop update check: " + ex.Message);
            if (reportNothing) _app.Status("Couldn't reach Steam to check the Workshop items: " + ex.Message);
            return false;
        }

        var todo = sources
            .Where(s => WorkshopCache.NeedsDownload(s, paths, details.GetValueOrDefault(s.WorkshopId!)))
            // An item gone from the Workshop keeps the copy the app already has.
            .Where(s => WorkshopCache.Find(s.WorkshopId!) is null || details.GetValueOrDefault(s.WorkshopId!)?.Problem is null)
            .DistinctBy(s => s.WorkshopId)
            .Select(s => (s.WorkshopId!, s.Name, details.GetValueOrDefault(s.WorkshopId!)?.TimeUpdated ?? 0L))
            .ToList();
        if (todo.Count == 0)
        {
            if (reportNothing) _app.Status("The Workshop items in the list are up to date.");
            return false;
        }
        return (await DownloadWorkshop(todo, allowSignIn)).Count > 0;
    }

    /// <summary>Downloads the items one by one with a progress bar; returns the IDs that finished.</summary>
    async Task<HashSet<string>> DownloadWorkshop(IReadOnlyList<(string Id, string Name, long Updated)> items, bool allowSignIn)
    {
        var done = new HashSet<string>();
        if (_downloadCts is not null)
        {
            _app.Status("A Workshop download is already running.");
            return done;
        }
        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;
        WorkshopDownloadPanel.Visibility = Visibility.Visible;
        WorkshopDownloadMissingButton.IsEnabled = false;
        WorkshopProgress.Value = 0;

        var fetcher = new WorkshopFetcher(_app.Settings.LiveryPack, () => _app.Settings.Save())
        {
            AllowSignIn = allowSignIn,
            Status = text => Dispatcher.BeginInvoke(() =>
            {
                WorkshopDownloadText.Text = text;
                _app.Status(text);
            }),
            Events = new DownloadEvents
            {
                Progress = p => Dispatcher.BeginInvoke(() => WorkshopProgress.Value = p),
                QrCode = grid => Dispatcher.BeginInvoke(() => ShowQr(grid)),
            },
        };

        var problems = new List<string>();
        try
        {
            foreach (var (id, name, updated) in items)
            {
                WorkshopProgress.Value = 0;
                var result = await fetcher.FetchAsync(id, name, updated, ct);
                if (result.Outcome == DownloadOutcome.Done)
                {
                    done.Add(id);
                    continue;
                }
                problems.Add($"{name}: {result.Message}");
                // These would fail the same way for every other item.
                if (result.Outcome is DownloadOutcome.Cancelled or DownloadOutcome.NeedsSignIn or DownloadOutcome.NotOwned) break;
            }
        }
        catch (OperationCanceledException)
        {
            problems.Add("Cancelled.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                                       or UnauthorizedAccessException or Win32Exception)
        {
            problems.Add(ex.Message);
            AppDirs.Log("Workshop download: " + ex);
        }
        finally
        {
            _downloadCts.Dispose();
            _downloadCts = null;
            ShowQr(null);
            WorkshopDownloadPanel.Visibility = Visibility.Collapsed;
            WorkshopDownloadMissingButton.IsEnabled = true;
            RefreshSteamAccount();
        }

        var message = done.Count > 0 ? $"Downloaded {done.Count} Workshop item(s)." : "";
        if (problems.Count > 0)
        {
            message += (message.Length > 0 ? " " : "") + "Not downloaded: " + string.Join("; ", problems);
            if (problems.Any(p => p.Contains("Sign in", StringComparison.OrdinalIgnoreCase)) && !allowSignIn)
                message += " Press \"Download missing\" on the Livery pack page to sign in.";
        }
        _app.Status(message);
        return done;
    }

    /// <summary>On start: fetch updated Workshop items (without asking to sign in), then rebuild the pack if needed.</summary>
    async void StartLiveries()
    {
        if (_app.Settings.LiveryPack.Enabled)
        {
            try
            {
                await UpdateWorkshopSources(allowSignIn: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                AppDirs.Log("Workshop update on start: " + ex.Message);
            }
        }
        RunLiveryBuild(progress => _app.RebuildLiveryPackIfChanged(progress), quietWhenNull: true);
    }

    void ShowQr(bool[,]? grid)
    {
        if (grid is null)
        {
            _qrWindow?.CloseQuietly();
            _qrWindow = null;
            return;
        }
        if (_qrWindow is null)
        {
            _qrWindow = new SteamQrWindow(this, () => _downloadCts?.Cancel());
            _qrWindow.Show();
        }
        _qrWindow.SetCode(grid);
    }
}

/// <summary>The Steam sign-in QR code DepotDownloader asks for, shown in the app.</summary>
sealed class SteamQrWindow : Window
{
    readonly Image _image = new() { Width = 280, Height = 280, Stretch = Stretch.Fill };
    readonly Action _cancel;
    bool _closingQuietly;

    public SteamQrWindow(Window owner, Action cancel)
    {
        _cancel = cancel;
        Owner = owner;
        Title = "Sign in to Steam";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);

        var cancelButton = new Button { Content = "Cancel", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        cancelButton.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Width = 320,
            Children =
            {
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
                    Text = "To download Workshop items without subscribing, sign in to Steam once: open the Steam app on your " +
                           "phone, go to Steam Guard, tap \"Scan a QR code\" and scan this.",
                },
                new Border { Background = Brushes.White, Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Center, Child = _image },
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Foreground = Brushes.DimGray,
                    Text = "The sign-in is made and remembered by DepotDownloader, which does the downloading; this app never " +
                           "sees your password. The code changes by itself every so often.",
                },
                cancelButton,
            },
        };
        Closing += (_, _) =>
        {
            if (!_closingQuietly) _cancel();
        };
    }

    public void SetCode(bool[,] grid)
    {
        int n = grid.GetLength(0), m = grid.GetLength(1);
        var pixels = new byte[n * m];
        for (var y = 0; y < n; y++)
            for (var x = 0; x < m; x++)
                pixels[y * m + x] = grid[y, x] ? (byte)0 : (byte)255;
        _image.Source = BitmapSource.Create(m, n, 96, 96, PixelFormats.Gray8, null, pixels, m);
    }

    public void CloseQuietly()
    {
        _closingQuietly = true;
        Close();
    }
}
