using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Ets2u.Core.Extract;
using Ets2u.Core.Liveries;
using Ets2u.Core.Safety;

namespace Ets2u.App;

/// <summary>The livery pack page.</summary>
public partial class MainWindow
{
    /// <summary>One source in the grid. Name and Included edit the source in settings directly.</summary>
    sealed class LiveryRow(LiverySource source) : INotifyPropertyChanged
    {
        public LiverySource Source { get; } = source;

        public bool Included
        {
            get => Source.Included;
            set => Source.Included = value;
        }

        public string Name
        {
            get => Source.Name;
            set => Source.Name = value.Trim().Length == 0 ? Source.Name : value.Trim();
        }

        public string Author => Source.Author ?? "";
        public string Where => Source.Where;

        string _paintJobs = "…";
        public string PaintJobs
        {
            get => _paintJobs;
            set { _paintJobs = value; Notify(nameof(PaintJobs)); }
        }

        string _state = "Checking…";
        public string State
        {
            get => _state;
            set { _state = value; Notify(nameof(State)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    sealed record WorkshopRow(WorkshopItem Item, string Name, string Author, string Id, int PaintJobs, string Note);

    List<LiveryRow> _liveryRows = new();
    IReadOnlyList<WorkshopRow> _workshopRows = [];
    bool _liveryOutdated;
    bool _liveryBusy;

    void RefreshLiveries()
    {
        var s = _app.Settings.LiveryPack;
        LiveryIntro.Text =
            $"Mod: {_app.LiveryMod.FileName}. New coach liveries each come as their own mod; this packs the ones you choose into one " +
            "optional mod with your own file name, so Mod Manager shows one entry and your pack never clashes with another player's. " +
            "Local mods (.scs, .zip or a folder) and Steam Workshop items can both go in.";
        LiveryEnabledBox.IsChecked = s.Enabled;
        LiveryPermissionBox.IsChecked = s.WorkshopPermissionConfirmed;
        _liveryRows = s.Sources.Select(x => new LiveryRow(x)).ToList();
        LiverySourceGrid.ItemsSource = _liveryRows;
        LiveryBuildButton.IsEnabled = !_liveryBusy;
        CheckLiverySources();
    }

    /// <summary>Reads every source in the background: paint jobs, problems, and whether the pack is up to date.</summary>
    async void CheckLiverySources()
    {
        var rows = _liveryRows;
        var sources = rows.Select(r => r.Source).ToList();
        var paths = _app.Paths;
        var settings = _app.Settings.LiveryPack;
        try
        {
            var (plan, outdated, conflicts) = await Task.Run(() =>
            {
                using var game = paths.InstallDir is null ? null : new GameArchives(paths.InstallDir);
                // Every source, ticked or not, so the grid can show what each holds.
                var all = sources.Select(x => new LiverySource
                {
                    Kind = x.Kind, Path = x.Path, WorkshopId = x.WorkshopId, Name = x.Name, Author = x.Author, Included = true,
                }).ToList();
                var p = LiveryPack.Plan(all, paths, game);
                var changed = settings.Enabled && sources.Any(x => x.Included)
                              && LiveryPack.Fingerprint(sources, paths) != settings.BuiltFingerprint;
                // Clashes only count between the ticked sources, in their order.
                return (p, changed, LiveryPack.Plan(sources, paths).Conflicts);
            });
            if (!ReferenceEquals(rows, _liveryRows)) return; // refreshed meanwhile
            for (var i = 0; i < rows.Count; i++)
            {
                var r = plan.Sources[i];
                rows[i].PaintJobs = r.Problem is null ? r.PaintJobs.ToString() : "-";
                rows[i].State = r.Problem
                                ?? (r.PaintJobs == 0 ? "No paint jobs: this doesn't look like a livery mod." : "Ready")
                                + (r.Overrides.Count > 0 ? $". Also replaces {r.Overrides.Count} game file(s), e.g. {r.Overrides[0]}" : "");
                // A Workshop item added before Steam downloaded it gets its real name once it's there.
                if (r.Problem is null && rows[i].Source.Kind == LiverySourceKind.Workshop
                                      && rows[i].Source.Name == $"Workshop item {rows[i].Source.WorkshopId}" && r.Package is { } pkg)
                {
                    var (name, author) = Workshop.ReadManifest(pkg);
                    if (name is not null) rows[i].Source.Name = name;
                    rows[i].Source.Author ??= author;
                }
            }
            _liveryOutdated = outdated;
            LiveryStatus.Text = !settings.Enabled ? (_app.LiveryPackExists ? "Switched off: building removes the pack." : "Off.")
                : !_app.LiveryPackExists ? "Not built yet."
                : outdated ? "Something changed since the pack was built: build it again."
                : "The pack is up to date.";
            if (conflicts.Count > 0)
                LiveryStatus.Text += $" {conflicts.Count} file(s) clash; first: {conflicts[0]}";
            RefreshModsPage();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            LiveryStatus.Text = "Couldn't read the mods: " + ex.Message;
        }
    }

    /// <summary>Saves what's on the page (switch, ticks, names, order, confirmation) to settings.</summary>
    void SaveLiveryPage()
    {
        var s = _app.Settings.LiveryPack;
        LiverySourceGrid.CommitEdit();
        LiverySourceGrid.CommitEdit();
        s.Enabled = LiveryEnabledBox.IsChecked == true;
        s.WorkshopPermissionConfirmed = LiveryPermissionBox.IsChecked == true;
        s.Sources = _liveryRows.Select(r => r.Source).ToList();
        _app.Settings.Save();
    }

    void AddLiverySource(LiverySource source)
    {
        SaveLiveryPage();
        var s = _app.Settings.LiveryPack;
        if (s.Sources.Any(x => x.Key == source.Key))
        {
            _app.Status($"{source.Name} is already in the pack.");
            return;
        }
        s.Sources.Add(source);
        if (source.Kind == LiverySourceKind.Workshop)
            s.WorkshopPermissionConfirmed = false; // a new creator: check again
        _app.Settings.Save();
        _app.Status($"Added {source.Name}." + (source.Kind == LiverySourceKind.Workshop
            ? " Check that its creator allows repacking, then tick the confirmation below." : ""));
        RefreshLiveries();
        LoadWorkshopList();
    }

    void LiveryAddFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add livery mods",
            Filter = "ETS2 mods (*.scs;*.zip)|*.scs;*.zip|All files (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = Directory.Exists(_app.Paths.ModDir) ? _app.Paths.ModDir : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames) AddLocal(file);
    }

    void LiveryAddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Add an unpacked livery mod (the folder with manifest.sii)" };
        if (dialog.ShowDialog(this) == true) AddLocal(dialog.FolderName);
    }

    void AddLocal(string path)
    {
        if (Path.GetFileName(path).Equals(_app.LiveryMod.FileName, StringComparison.OrdinalIgnoreCase))
        {
            _app.Status("That's the pack itself.");
            return;
        }
        var (name, author) = Workshop.ReadManifest(path);
        AddLiverySource(new LiverySource
        {
            Kind = LiverySourceKind.Local, Path = Path.GetFullPath(path),
            Name = name ?? Path.GetFileNameWithoutExtension(path), Author = author,
        });
    }

    LiveryRow? SelectedLivery => LiverySourceGrid.SelectedItem as LiveryRow;

    void MoveLivery(int by)
    {
        if (SelectedLivery is not { } row) return;
        var i = _liveryRows.IndexOf(row);
        var j = i + by;
        if (j < 0 || j >= _liveryRows.Count) return;
        (_liveryRows[i], _liveryRows[j]) = (_liveryRows[j], _liveryRows[i]);
        SaveLiveryPage();
        RefreshLiveries();
        LiverySourceGrid.SelectedIndex = j;
    }

    void LiveryUp_Click(object sender, RoutedEventArgs e) => MoveLivery(-1);

    void LiveryDown_Click(object sender, RoutedEventArgs e) => MoveLivery(1);

    void LiveryRemove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLivery is not { } row) return;
        _liveryRows.Remove(row);
        SaveLiveryPage();
        _app.Status($"Removed {row.Name} from the list. Build the pack again to take it out.");
        RefreshLiveries();
        LoadWorkshopList();
    }

    void LiverySourcePage_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLivery?.Source is { Kind: LiverySourceKind.Workshop, WorkshopId: { } id }) OpenUrl(Workshop.PageUrl(id));
        else _app.Status("Pick a Workshop item in the list first.");
    }

    // ---------- Workshop ----------

    async void LoadWorkshopList()
    {
        var paths = _app.Paths;
        var inPack = _app.Settings.LiveryPack.Sources.Where(x => x.Kind == LiverySourceKind.Workshop)
            .Select(x => x.WorkshopId).ToHashSet();
        try
        {
            _workshopRows = await Task.Run(() => Workshop.List(paths).Select(item =>
            {
                var paintJobs = 0;
                string note;
                if (item.Package is null) note = "No version for this game";
                else
                {
                    try
                    {
                        using var content = ModContent.Load(item.Package);
                        paintJobs = content.Files.Count(LiveryPack.IsPaintJob);
                        note = "";
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        note = "Can't read: " + ex.Message;
                    }
                }
                if (inPack.Contains(item.Id)) note = "In the pack";
                return new WorkshopRow(item, item.Name, item.Author ?? "", item.Id, paintJobs, note);
            }).ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _workshopRows = [];
            _app.Status("Couldn't list the Workshop items: " + ex.Message);
        }
        FilterWorkshop();
    }

    void FilterWorkshop() =>
        WorkshopGrid.ItemsSource = WorkshopLiveriesOnlyBox.IsChecked == true
            ? _workshopRows.Where(r => r.PaintJobs > 0).ToList()
            : _workshopRows;

    void WorkshopFilter_Click(object sender, RoutedEventArgs e) => FilterWorkshop();

    void WorkshopRefresh_Click(object sender, RoutedEventArgs e)
    {
        LoadWorkshopList();
        CheckLiverySources();
    }

    void WorkshopAdd_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in WorkshopGrid.SelectedItems.OfType<WorkshopRow>().ToList())
            AddLiverySource(new LiverySource
            {
                Kind = LiverySourceKind.Workshop, WorkshopId = row.Id, Name = row.Name,
                Author = string.IsNullOrEmpty(row.Author) ? null : row.Author,
            });
    }

    void WorkshopPage_Click(object sender, RoutedEventArgs e)
    {
        if (WorkshopGrid.SelectedItem is WorkshopRow row) OpenUrl(row.Item.PageUrl);
    }

    /// <summary>
    /// A pasted link or ID. Steam downloads Workshop items for subscribers only, so one that isn't here yet
    /// opens in the Steam client to subscribe; it goes in the list anyway and is used once Steam has it.
    /// </summary>
    void WorkshopAddId_Click(object sender, RoutedEventArgs e)
    {
        if (Workshop.ParseId(WorkshopIdBox.Text) is not { } id)
        {
            _app.Status("Paste a Workshop link (…filedetails/?id=123456789) or the number itself.");
            return;
        }
        WorkshopIdBox.Text = "";
        if (Workshop.FindItemDir(_app.Paths, id) is { } dir)
        {
            var item = Workshop.Describe(dir, _app.Paths.GameVersion);
            AddLiverySource(new LiverySource { Kind = LiverySourceKind.Workshop, WorkshopId = id, Name = item.Name, Author = item.Author });
            return;
        }
        AddLiverySource(new LiverySource { Kind = LiverySourceKind.Workshop, WorkshopId = id, Name = $"Workshop item {id}" });
        OpenUrl(Workshop.SteamClientUrl(id));
        _app.Status($"Workshop item {id} isn't downloaded yet: subscribe to it in Steam (opened now), wait for the download, then press Refresh.");
    }

    void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _app.Status($"Couldn't open {url}: {ex.Message}");
        }
    }

    // ---------- building ----------

    void LiveryBuild_Click(object sender, RoutedEventArgs e)
    {
        SaveLiveryPage();
        var s = _app.Settings.LiveryPack;
        if (s.Enabled && s.Sources.Any(x => x.Included && x.Kind == LiverySourceKind.Workshop) && !s.WorkshopPermissionConfirmed)
        {
            MessageBox.Show(this,
                "Your pack includes Steam Workshop items. Check on each item's Workshop page that its creator allows it to be " +
                "edited and redistributed (or ask them), then tick the confirmation box above the Build button.",
                "Livery pack", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        RunLiveryBuild(progress => _app.BuildLiveryPack(progress));
    }

    /// <summary>Builds in the background with a progress bar; big packs take a few seconds.</summary>
    async void RunLiveryBuild(Func<Action<int, int>, string?> build, bool quietWhenNull = false)
    {
        if (_liveryBusy) return;
        _liveryBusy = true;
        LiveryBuildButton.IsEnabled = false;
        LiveryProgress.Value = 0;
        LiveryProgress.Visibility = Visibility.Visible;
        void Progress(int done, int total) => Dispatcher.BeginInvoke(() =>
        {
            LiveryProgress.Maximum = total;
            LiveryProgress.Value = done;
        });
        try
        {
            var message = await Task.Run(() => build(Progress));
            if (message is not null || !quietWhenNull) _app.Status(message ?? "Nothing to do.");
        }
        catch (Exception ex) when (ex is GameRunningException or InvalidOperationException or IOException
                                       or InvalidDataException or UnauthorizedAccessException or FormatException)
        {
            _app.Status(ex.Message);
            if (!quietWhenNull) MessageBox.Show(this, ex.Message, "Livery pack", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _liveryBusy = false;
            LiveryProgress.Visibility = Visibility.Collapsed;
            RefreshAll();
        }
    }
}
