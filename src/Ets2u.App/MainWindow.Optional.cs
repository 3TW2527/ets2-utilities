using System.ComponentModel;
using System.IO;
using System.Windows;
using Ets2u.Core.Liveries;
using Ets2u.Core.Mods;
using Ets2u.Core.Safety;
using Ets2u.Core.Save;

namespace Ets2u.App;

/// <summary>The "Make optional" page: copies of other mods with mp_mod_optional added, for Convoy.</summary>
public partial class MainWindow
{
    sealed class OptionalRow(OptionalModEntry entry, string file) : INotifyPropertyChanged
    {
        public OptionalModEntry Entry { get; } = entry;

        public bool Included
        {
            get => Entry.Source.Included;
            set => Entry.Source.Included = value;
        }

        public string Name
        {
            get => Entry.Source.Name;
            set => Entry.Source.Name = value.Trim().Length == 0 ? Entry.Source.Name : value.Trim();
        }

        public string Where => Entry.Source.Where;
        public string File { get; } = file;

        string _state = "Checking…";
        public string State
        {
            get => _state;
            set { _state = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    sealed record OptionalWorkshopRow(WorkshopItem Item, string Name, string Author, string Id, bool Optional, string Note);

    List<OptionalRow> _optionalRows = new();
    IReadOnlyList<OptionalWorkshopRow> _optionalWorkshopRows = [];
    bool _optionalBusy;

    void RefreshOptional()
    {
        OptionalHeader.Info =
            "Convoy only lets players without a mod join when the mod is marked optional (mp_mod_optional in its manifest.sii). " +
            "This makes a copy of a mod that isn't, with only that changed, under its own file name in your mod folder " +
            $"(ets2u_opt_<id>_{_app.Settings.ModTag}.scs). The original is left as it is, so Steam never overwrites the copy. " +
            "It suits mods that add things only you use, like engines, gearboxes, tires or sounds.";
        _optionalRows = _app.Settings.OptionalMods.Mods.Select(m => new OptionalRow(m, _app.OptionalSpec(m).FileName)).ToList();
        OptionalGrid.ItemsSource = _optionalRows;
        OptionalBuildButton.IsEnabled = !_optionalBusy;
        CheckOptional();
    }

    /// <summary>Works out each row's state off the UI thread (it reads the originals and the profiles).</summary>
    async void CheckOptional()
    {
        var rows = _optionalRows;
        var paths = _app.Paths;
        var profiles = _app.Profiles;
        var app = _app;
        var states = await Task.Run(() => rows.Select(r =>
        {
            var entry = r.Entry;
            var (package, problem) = LiveryPack.Resolve(entry.Source, paths);
            if (!entry.Source.Included)
                return app.OptionalExists(entry) ? "Off: saving removes the copy." : "Off.";
            if (package is null) return (app.OptionalExists(entry) ? "The copy stays, but " : "") + problem;
            var state = !app.OptionalExists(entry) ? "Not built yet."
                : app.OptionalOutdated(entry) ? "The original changed: build again."
                : "Ready.";
            if (state == "Ready." && ModStatus.ActiveIn(profiles, app.OptionalSpec(entry)) is { Count: 0 })
                state = "Ready. Enable it in Mod Manager.";
            var original = OptionalCopy.OriginalActiveIn(profiles, entry.Source, paths);
            if (original.Count > 0) state += $" Switch the original off in Mod Manager ({string.Join(", ", original)}).";
            return state;
        }).ToList());
        if (!ReferenceEquals(rows, _optionalRows)) return;
        for (var i = 0; i < rows.Count; i++) rows[i].State = states[i];
        var on = rows.Count(r => r.Included);
        OptionalStatus.Text = rows.Count == 0 ? "No mods yet: add one below."
            : rows.Any(r => r.Included && _app.OptionalOutdated(r.Entry)) ? "Some copies need building."
            : $"{on} optional cop{(on == 1 ? "y" : "ies")} up to date.";
        RefreshModsPage();
    }

    void SaveOptionalPage()
    {
        OptionalGrid.CommitEdit();
        OptionalGrid.CommitEdit();
        _app.Settings.OptionalMods.Mods = _optionalRows.Select(r => r.Entry).ToList();
        _app.Settings.Save();
    }

    void AddOptional(LiverySource source)
    {
        SaveOptionalPage();
        var mods = _app.Settings.OptionalMods.Mods;
        if (mods.Any(m => m.Source.Key == source.Key))
        {
            _app.Status($"{source.Name} is in the list already.");
            return;
        }
        var spec = OptionalCopy.Spec(source, _app.Settings.ModTag ?? "");
        if (mods.Any(m => _app.OptionalSpec(m).FileName.Equals(spec.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            _app.Status($"Another mod in the list would get the same file name ({spec.FileName}). Rename one of the files first.");
            return;
        }
        mods.Add(new OptionalModEntry { Source = source });
        _app.Settings.Save();
        _app.Status($"Added {source.Name}. Press \"Save and build copies\" to make its optional copy.");
        RefreshOptional();
        LoadOptionalWorkshopList();
    }

    void OptionalAddFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick mods to make optional",
            Filter = "ETS2 mods (*.scs;*.zip)|*.scs;*.zip|All files (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = Directory.Exists(_app.Paths.ModDir) ? _app.Paths.ModDir : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames) AddOptionalLocal(file);
    }

    void OptionalAddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Pick an unpacked mod (the folder with manifest.sii)" };
        if (dialog.ShowDialog(this) == true) AddOptionalLocal(dialog.FolderName);
    }

    void AddOptionalLocal(string path)
    {
        if (OptionalCopy.IsAppMod(path))
        {
            _app.Status("That's one of this app's own mods, which are optional already.");
            return;
        }
        var (name, author) = Workshop.ReadManifest(path);
        AddOptional(new LiverySource
        {
            Kind = LiverySourceKind.Local, Path = Path.GetFullPath(path),
            Name = name ?? Path.GetFileNameWithoutExtension(path), Author = author,
        });
    }

    OptionalRow? SelectedOptional => OptionalGrid.SelectedItem as OptionalRow;

    void OptionalRemove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedOptional is not { } row) return;
        try
        {
            var message = _app.RemoveOptional(row.Entry);
            _optionalRows.Remove(row);
            SaveOptionalPage();
            _app.Status((message.Length > 0 ? message + " " : "") + $"Forgot {row.Name}." +
                        (message.Length > 0 ? " Switch the original back on in Mod Manager if you still want it." : ""));
        }
        catch (Exception ex) when (ex is GameRunningException or IOException or UnauthorizedAccessException)
        {
            _app.Status(ex.Message);
        }
        RefreshOptional();
        LoadOptionalWorkshopList();
    }

    void OptionalPage_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedOptional?.Entry.Source is { Kind: LiverySourceKind.Workshop, WorkshopId: { } id }) OpenUrl(Workshop.PageUrl(id));
        else _app.Status("Pick a Workshop item in the list first.");
    }

    // ---------- subscribed Workshop items ----------

    async void LoadOptionalWorkshopList()
    {
        var paths = _app.Paths;
        var added = _app.Settings.OptionalMods.Mods.Where(m => m.Source.Kind == LiverySourceKind.Workshop)
            .Select(m => m.Source.WorkshopId).ToHashSet();
        try
        {
            _optionalWorkshopRows = await Task.Run(() => Workshop.List(paths).Select(item =>
            {
                var optional = false;
                string note;
                if (item.Package is null) note = "No version for this game";
                else
                {
                    try
                    {
                        using var content = ModContent.Load(item.Package);
                        var manifest = content.Files.FirstOrDefault(f => f.Equals("manifest.sii", StringComparison.OrdinalIgnoreCase));
                        optional = manifest is not null
                                   && OptionalCopy.IsOptional(System.Text.Encoding.UTF8.GetString(SiiCodec.Decode(content.Read(manifest))));
                        note = optional ? "Optional already" : "";
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        note = "Can't read: " + ex.Message;
                    }
                }
                if (added.Contains(item.Id)) note = "In the list";
                return new OptionalWorkshopRow(item, item.Name, item.Author ?? "", item.Id, optional, note);
            }).ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _optionalWorkshopRows = [];
            _app.Status("Couldn't list the Workshop items: " + ex.Message);
        }
        FilterOptionalWorkshop();
    }

    void FilterOptionalWorkshop() =>
        OptionalWorkshopGrid.ItemsSource = OptionalNotOptionalOnlyBox.IsChecked == true
            ? _optionalWorkshopRows.Where(r => !r.Optional).ToList()
            : _optionalWorkshopRows;

    void OptionalFilter_Click(object sender, RoutedEventArgs e) => FilterOptionalWorkshop();

    void OptionalRefresh_Click(object sender, RoutedEventArgs e)
    {
        LoadOptionalWorkshopList();
        CheckOptional();
    }

    async void OptionalWorkshopAdd_Click(object sender, RoutedEventArgs e)
    {
        var rows = OptionalWorkshopGrid.SelectedItems.OfType<OptionalWorkshopRow>().ToList();
        foreach (var row in rows.Where(r => r.Optional)) _app.Status($"{row.Name} is optional already: use it as it is.");
        rows = rows.Where(r => !r.Optional).ToList();
        if (rows.Count == 0) return;
        // Steam has the item's real title; the manifest often has none, and a description's first line
        // can be anything ("English").
        var titles = new Dictionary<string, string>();
        try
        {
            foreach (var d in await WorkshopApi.GetDetailsAsync(rows.Select(r => r.Id).ToList()))
                if (d.Found && !string.IsNullOrWhiteSpace(d.Title)) titles[d.Id] = d.Title.Trim();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Ets2u.Core.AppDirs.Log("Workshop titles: " + ex.Message);
        }
        foreach (var row in rows)
            AddOptional(new LiverySource
            {
                Kind = LiverySourceKind.Workshop, WorkshopId = row.Id, Name = titles.GetValueOrDefault(row.Id, row.Name),
                Author = string.IsNullOrEmpty(row.Author) ? null : row.Author,
            });
    }

    void OptionalWorkshopPage_Click(object sender, RoutedEventArgs e)
    {
        if (OptionalWorkshopGrid.SelectedItem is OptionalWorkshopRow row) OpenUrl(row.Item.PageUrl);
    }

    // ---------- building ----------

    void OptionalBuild_Click(object sender, RoutedEventArgs e)
    {
        SaveOptionalPage();
        RunOptionalBuild(progress => _app.SyncOptional(progress) ?? "The optional copies are up to date.");
    }

    async void RunOptionalBuild(Func<Action<int, int>, string?> build, bool quietWhenNull = false)
    {
        if (_optionalBusy) return;
        _optionalBusy = true;
        OptionalBuildButton.IsEnabled = false;
        OptionalProgress.Value = 0;
        OptionalProgress.Visibility = Visibility.Visible;
        void Progress(int done, int total) => Dispatcher.BeginInvoke(() =>
        {
            OptionalProgress.Maximum = total;
            OptionalProgress.Value = done;
        });
        try
        {
            var message = await Task.Run(() => build(Progress));
            if (message is not null || !quietWhenNull) _app.Status(message ?? "Nothing to do.");
        }
        catch (Exception ex) when (ex is GameRunningException or InvalidOperationException or IOException
                                       or InvalidDataException or UnauthorizedAccessException)
        {
            _app.Status(ex.Message);
            if (!quietWhenNull) MessageBox.Show(this, ex.Message, "Make optional", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _optionalBusy = false;
            OptionalProgress.Visibility = Visibility.Collapsed;
            RefreshAll();
        }
    }

    /// <summary>On start: rebuild copies whose original Steam updated.</summary>
    void StartOptional() => RunOptionalBuild(progress => _app.SyncOptional(progress, onStart: true), quietWhenNull: true);
}
