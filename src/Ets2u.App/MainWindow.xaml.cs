using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Ets2u.Core;
using Ets2u.Core.Features;
using Ets2u.Core.Info;
using Ets2u.Core.Mods;
using Ets2u.Core.Paths;
using Ets2u.Core.Safety;
using Ets2u.Core.Save;
using Ets2u.Core.Telemetry;

namespace Ets2u.App;

public partial class MainWindow : Window
{
    sealed record Row(string Label, string Value);

    sealed record ProfileRow(string Name, string Storage, int SaveCount, string NewestSave, string Format);

    sealed record SaveItem(SaveSlot Slot, string Label);

    sealed record BackupRow(Backup Backup, string When, string Reason, string Files);

    readonly AppServices _app = new();
    SaveState? _save;
    bool _loading;

    public MainWindow()
    {
        InitializeComponent();
        _app.StatusChanged += message => Dispatcher.Invoke(() => StatusText.Text = message);
        _app.Changed += () => Dispatcher.Invoke(RefreshAll);
        Loaded += (_, _) =>
        {
            RefreshAll();
            UpdateOverlay();
            Run(() => _app.RebuildIfGameUpdated() ?? "Ready");
        };
        Activated += (_, _) => UpdateBanner();
        Closed += (_, _) => _overlay?.Close();
    }

    // ---------- shared ----------

    void RefreshAll()
    {
        _loading = true;
        RefreshSetup();
        RefreshGameplay();
        RefreshProfiles();
        RefreshHud();
        RefreshInfo();
        RefreshBackups();
        UpdateBanner();
        _loading = false;
    }

    void UpdateBanner()
    {
        string? message = null;
        if (_app.GameRunning) message = "ETS2 is running. Close the game to make changes.";
        else if (_app.Paths.InstallDir is null) message = "ETS2 wasn't found. Check that it's installed through Steam.";
        Banner.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        BannerText.Text = message ?? "";
    }

    /// <summary>Runs a change and reports the result or the reason it failed.</summary>
    void Run(Func<string> action)
    {
        try
        {
            _app.Status(action());
        }
        catch (Exception ex) when (ex is GameRunningException or InvalidOperationException or IOException
                                       or FormatException or ArgumentException or UnauthorizedAccessException
                                       or InvalidDataException)
        {
            _app.Status(ex.Message);
            MessageBox.Show(this, ex.Message, "ETS2 Utilities", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshAll();
    }

    // ---------- Setup ----------

    void RefreshSetup()
    {
        var p = _app.Paths;
        PathRows.ItemsSource = new[]
        {
            new Row("Game install", p.InstallDir ?? "Not found"),
            new Row("Game version", p.GameVersion ?? "Unknown"),
            new Row("Documents folder", p.DocumentsDir),
            new Row("Mod folder", p.ModDir),
            new Row("Steam Cloud profiles", p.CloudProfileRoots.Count == 0 ? "None" : string.Join("\n", p.CloudProfileRoots)),
            new Row("Game files", _app.Game is null ? "Not available" : "Read directly from the game's archives when needed (nothing to extract)"),
            new Row("App data", AppDirs.Local),
        };

        ProfileGrid.ItemsSource = _app.Profiles.Select(DescribeProfile).ToList();
    }

    static ProfileRow DescribeProfile(Profile profile)
    {
        var saves = profile.Saves();
        var newest = saves.FirstOrDefault();
        string format;
        try
        {
            var state = newest is null ? null : SaveEditor.Read(newest);
            format = state is null ? "-"
                : state.Problem is { } problem ? "Can't edit: " + problem
                : state.Format == Core.Save.SiiFormat.Binary ? "Binary: editable" : "Text: editable";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException)
        {
            format = "Unreadable: " + ex.Message;
        }
        return new ProfileRow(profile.Name, profile.Storage == ProfileStorage.SteamCloud ? "Steam Cloud" : "Local",
            saves.Count, newest is null ? "-" : $"{newest.Name}, {newest.LastWriteUtc.ToLocalTime():dd MMM HH:mm}", format);
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => _app.Refresh();

    // ---------- Gameplay ----------

    void RefreshGameplay()
    {
        var s = _app.Settings;
        NoDamageBox.IsChecked = s.NoDamage;
        IncomeSlider.Value = s.IncomeMultiplier;
        XpSlider.Value = s.XpMultiplier;
        UpdateSliderLabels();
        foreach (var box in CoachFactorBoxes)
            box.IsChecked = s.CoachIgnored.HasFlag(Enum.Parse<CoachFactor>((string)box.Tag));
        CoachJobFailureBox.IsChecked = s.CoachIgnored.HasFlag(CoachFactor.JobFailure);
        CoachFullBox.IsChecked = s.CoachFullSatisfaction;
        NoDamageStatus.Text = DescribeMod(NoDamage.Mod);
        EarningsStatus.Text = DescribeMod(Earnings.Mod);
        CoachStatus.Text = DescribeMod(CoachComfort.Mod);
    }

    string DescribeMod(ModSpec mod)
    {
        if (!_app.ModExists(mod)) return "Mod not created yet.";
        mod = _app.Mine(mod);
        var active = ModStatus.ActiveIn(_app.Profiles, mod);
        return active.Count == 0
            ? $"{mod.FileName} is in the mod folder, but not enabled in any profile yet. Enable \"{mod.DisplayName}\" in Mod Manager."
            : $"{mod.FileName} is enabled in: {string.Join(", ", active)}.";
    }

    void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSliderLabels();

    void UpdateSliderLabels()
    {
        if (IncomeLabel is null || XpLabel is null) return;
        IncomeLabel.Text = $"x{IncomeSlider.Value:0}";
        XpLabel.Text = $"x{XpSlider.Value:0}";
    }

    void ApplyGameplay_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        _app.Settings.NoDamage = NoDamageBox.IsChecked == true;
        _app.Settings.IncomeMultiplier = Math.Round(IncomeSlider.Value);
        _app.Settings.XpMultiplier = Math.Round(XpSlider.Value);
        var ignored = CoachFactorBoxes.Where(b => b.IsChecked == true)
            .Aggregate(CoachFactor.None, (all, b) => all | Enum.Parse<CoachFactor>((string)b.Tag));
        if (CoachJobFailureBox.IsChecked == true) ignored |= CoachFactor.JobFailure;
        _app.Settings.CoachIgnored = ignored;
        _app.Settings.CoachFullSatisfaction = CoachFullBox.IsChecked == true;
        return _app.ApplyMods();
    });

    IEnumerable<CheckBox> CoachFactorBoxes => CoachFactorPanel.Children.OfType<CheckBox>();

    void CoachAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var box in CoachFactorBoxes) box.IsChecked = true;
    }

    void CoachNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var box in CoachFactorBoxes) box.IsChecked = false;
    }

    // ---------- Money & XP ----------

    void RefreshProfiles()
    {
        var selected = (ProfileBox.SelectedItem as Profile)?.Dir;
        ProfileBox.ItemsSource = _app.Profiles;
        ProfileBox.SelectedItem = _app.Profiles.FirstOrDefault(p => p.Dir == selected) ?? _app.Profiles.FirstOrDefault();
        LoadSaves();
    }

    void ProfileBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) LoadSaves();
    }

    void LoadSaves()
    {
        var profile = ProfileBox.SelectedItem as Profile;
        var saves = profile?.Saves() ?? Array.Empty<SaveSlot>();
        SaveBox.ItemsSource = saves.Select(s => new SaveItem(s, $"{s.Name}  ({s.LastWriteUtc.ToLocalTime():dd MMM HH:mm})")).ToList();
        SaveBox.SelectedIndex = saves.Count > 0 ? 0 : -1;
        LoadSave();
    }

    void SaveBox_Changed(object sender, SelectionChangedEventArgs e) => LoadSave();

    void LoadSave()
    {
        _save = null;
        if (SaveBox.SelectedItem is SaveItem item)
        {
            try { _save = SaveEditor.Read(item.Slot); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException)
            {
                SaveStateText.Text = "Couldn't read this save: " + ex.Message;
            }
        }

        var v = _save?.Values;
        // Binary saves can't be read, but their info.sii (load-screen summary) is text.
        var summary = v is null && SaveBox.SelectedItem is SaveItem s ? SaveEditor.ReadSummary(s.Slot) : null;
        MoneyNow.Text = (v?.Money ?? summary?.Money)?.ToString("N0", CultureInfo.CurrentCulture) ?? "-";
        TruckXpNow.Text = (v?.TruckXp ?? summary?.TruckXp) is { } txp ? DescribeXp(txp, LevelTable.TruckPath) : "-";
        CoachXpNow.Text = v?.CoachXp is { } c ? DescribeXp(c, LevelTable.CoachPath) : "-";

        if (_save is not null)
            SaveStateText.Text = _save.Editable
                ? $"{(_save.Format == Core.Save.SiiFormat.Binary ? "Binary" : "Text")} save: ready to edit."
                : "This save can't be edited: " + _save.Problem;
        EditGrid.IsEnabled = ApplySaveButton.IsEnabled = _save?.Editable == true;
        CoachXpBox.IsEnabled = v?.CoachXp is not null;
        FillBoxes();
    }

    /// <summary>Set mode starts from the current values; Add mode starts empty (nothing added).</summary>
    void FillBoxes()
    {
        var v = _save?.Values;
        var set = AddMode.IsChecked != true;
        MoneyBox.Text = set ? v?.Money.ToString(CultureInfo.InvariantCulture) ?? "" : "";
        TruckXpBox.Text = set ? v?.TruckXp.ToString(CultureInfo.InvariantCulture) ?? "" : "";
        CoachXpBox.Text = set ? v?.CoachXp?.ToString(CultureInfo.InvariantCulture) ?? "" : "";
        ModeHint.Text = set
            ? "Each box holds the value the save will have. Leave a box empty to keep the current value."
            : "Each box holds an amount to add (use a minus sign to take away). Empty means add nothing.";
        UpdatePreview();
    }

    void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) FillBoxes();
    }

    string DescribeXp(long xp, string tablePath)
    {
        var level = _app.LevelTable(tablePath)?.LevelForXp(xp);
        return xp.ToString("N0", CultureInfo.CurrentCulture) + (level is null ? "" : $"  (level {level})");
    }

    void XpBox_Changed(object sender, TextChangedEventArgs e) => UpdatePreview();

    /// <summary>What the edit would leave in the save, or why it can't be applied.</summary>
    (SaveValues? Result, string? Error) PlanEdit()
    {
        if (_save?.Values is not { } now) return (null, null);
        try
        {
            var edit = new SaveEdit(AddMode.IsChecked == true ? EditMode.Add : EditMode.Set,
                SaveEdit.ParseField(MoneyBox.Text, "Money"),
                SaveEdit.ParseField(TruckXpBox.Text, "Truck XP"),
                SaveEdit.ParseField(CoachXpBox.Text, "Coach XP"));
            return (edit.ApplyTo(now), null);
        }
        catch (FormatException ex)
        {
            return (null, ex.Message);
        }
    }

    void UpdatePreview()
    {
        if (PreviewText is null) return;
        var (after, error) = PlanEdit();
        var truckTable = _app.LevelTable(LevelTable.TruckPath);
        var coachTable = _app.LevelTable(LevelTable.CoachPath);
        TruckXpLevel.Text = after is null || truckTable is null ? "" : $"→ level {truckTable.LevelForXp(after.TruckXp)}";
        CoachXpLevel.Text = after?.CoachXp is { } cx && coachTable is not null ? $"→ level {coachTable.LevelForXp(cx)}" : "";
        PreviewText.Text = error ?? (after is null ? "" :
            $"After Apply: money {after.Money:N0}, truck XP {after.TruckXp:N0}" +
            (after.CoachXp is { } c ? $", coach XP {c:N0}" : "") +
            (after.Money is < 0 or > SaveEditor.MaxMoney ? "  (money out of range)" : ""));
    }

    void Preset_Click(object sender, RoutedEventArgs e)
    {
        var parts = ((string)((Button)sender).Tag).Split(':');
        var n = long.Parse(parts[1], CultureInfo.InvariantCulture);
        // Presets are target values: switch to Set (which refills every box with today's values), then change one box.
        if (AddMode.IsChecked == true) SetMode.IsChecked = true;
        switch (parts[0])
        {
            case "money":
                MoneyBox.Text = n.ToString(CultureInfo.InvariantCulture);
                break;
            case "truck":
                TruckXpBox.Text = XpFor(LevelTable.TruckPath, (int)n);
                break;
            case "coach":
                if (CoachXpBox.IsEnabled) CoachXpBox.Text = XpFor(LevelTable.CoachPath, (int)n);
                break;
        }
    }

    string XpFor(string tablePath, int level) =>
        (_app.LevelTable(tablePath)?.XpForLevel(level) ?? 0).ToString(CultureInfo.InvariantCulture);

    void ApplySave_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (ProfileBox.SelectedItem is not Profile profile || _save is null || _save.Values is null)
            return "Pick a profile and a text save first.";
        var (target, error) = PlanEdit();
        if (target is null) throw new FormatException(error ?? "Nothing to apply.");

        SaveEditor.Write(profile, _save.Slot, target, _app.Writer);
        return $"Saved {profile.Name} / {_save.Slot.Name}: money {target.Money:N0}, truck XP {target.TruckXp:N0}" +
               (target.CoachXp is { } c ? $", coach XP {c:N0}" : "") + ". Load this save in the game.";
    });

    // ---------- HUD & Dash ----------

    void RefreshHud()
    {
        var s = _app.Settings;
        MapBox.IsChecked = s.MapPositionEnabled;
        MapSwap.IsChecked = s.MapSide == MapSide.Swap;
        MapLeft.IsChecked = s.MapSide == MapSide.Left;
        MapRight.IsChecked = s.MapSide == MapSide.Right;
        MapNote.Text = _app.Game is null ? "ETS2 wasn't found." : HudMod.DescribeMapSupport(_app.Game);
        HudStatus.Text = DescribeMod(HudMod.Mod);

        var install = _app.Paths.InstallDir;
        var source = PluginInstaller.FindSource();
        PluginStatus.Text = source is null ? "The plugin file (ets2u_telemetry.dll) isn't next to the app."
            : !PluginInstaller.IsInstalled(install) ? "Plugin not installed."
            : PluginInstaller.IsCurrent(install) ? "Plugin installed. ETS2 shows an \"SDK plugins\" notice when it starts; that's this plugin."
            : "An older plugin is installed. Install again to update it.";
        InstallPluginButton.IsEnabled = source is not null && install is not null && !PluginInstaller.IsCurrent(install);
        RemovePluginButton.IsEnabled = PluginInstaller.IsInstalled(install);

        OverlayBox.IsChecked = s.SpeedOverlay;
        CornerBox.ItemsSource = CornerNames;
        CornerBox.DisplayMemberPath = "Value";
        CornerBox.SelectedValuePath = "Key";
        CornerBox.SelectedValue = s.OverlayCorner;
        ScaleSlider.Value = s.OverlayScale;
        LimitBox.IsChecked = s.OverlayShowLimit;
        OverlayStatus.Text = s.SpeedOverlay
            ? "Overlay on. It appears once you're driving, and hides while the game is paused or closed."
            : "Overlay off.";
    }

    void ApplyHud_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        _app.Settings.MapPositionEnabled = MapBox.IsChecked == true;
        _app.Settings.MapSide = MapLeft.IsChecked == true ? MapSide.Left : MapRight.IsChecked == true ? MapSide.Right : MapSide.Swap;
        return _app.ApplyMods();
    });

    void InstallPlugin_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        PluginInstaller.Install(_app.Paths.InstallDir ?? throw new InvalidOperationException("ETS2 wasn't found."), _app.Writer);
        return "Speed plugin installed. Start ETS2 and accept the SDK plugins notice.";
    });

    void RemovePlugin_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        PluginInstaller.Remove(_app.Paths.InstallDir ?? throw new InvalidOperationException("ETS2 wasn't found."), _app.Backups);
        return "Speed plugin removed.";
    });

    SpeedOverlay? _overlay;

    static readonly Dictionary<OverlayCorner, string> CornerNames = new()
    {
        [OverlayCorner.TopLeft] = "Top left",
        [OverlayCorner.TopCenter] = "Top centre",
        [OverlayCorner.TopRight] = "Top right",
        [OverlayCorner.BottomLeft] = "Bottom left",
        [OverlayCorner.BottomRight] = "Bottom right",
    };

    void ApplyOverlay_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var s = _app.Settings;
        s.SpeedOverlay = OverlayBox.IsChecked == true;
        s.OverlayCorner = CornerBox.SelectedValue is OverlayCorner c ? c : OverlayCorner.TopRight;
        s.OverlayScale = Math.Round(ScaleSlider.Value, 1);
        s.OverlayShowLimit = LimitBox.IsChecked == true;
        s.Save();
        UpdateOverlay();
        return s.SpeedOverlay ? "Speed overlay on." : "Speed overlay off.";
    });

    void UpdateOverlay()
    {
        if (_app.Settings.SpeedOverlay)
        {
            _overlay ??= new SpeedOverlay(_app.Settings);
            _overlay.Apply(_app.Settings);
        }
        else
        {
            _overlay?.Close();
            _overlay = null;
        }
    }

    // ---------- Game Information ----------

    sealed record CityRow(CityInfo City, string Name, string LocalName, string Country, string Source);

    IReadOnlyList<CityRow> _cities = Array.Empty<CityRow>();

    void RefreshInfo()
    {
        var info = _app.Info;
        FuelGrid.ItemsSource = info?.Countries;
        _cities = (info?.Cities ?? Array.Empty<CityInfo>())
            .Select(c => new CityRow(c, c.Name, c.LocalName, c.Country,
                c.Source == GameInfo.BaseArchive ? "Base game" : c.Source))
            .ToList();
        FuelNote.Text = info is null
            ? _app.InfoProblem ?? "ETS2 wasn't found."
            : "Base price in each country, from the game's files. Countries of map DLCs this PC doesn't have are left out.";
        CityNote.Text = info is null ? ""
            : "Double-click a city (or press Ctrl+C) to copy its name as the game's console expects it, e.g. " +
              "\"goto Köln\". After goto moves the camera, press Ctrl+F9 to bring the truck there.";
        FilterCities();
    }

    void CityFilter_Changed(object sender, TextChangedEventArgs e) => FilterCities();

    void FilterCities()
    {
        var find = CityFilter.Text.Trim();
        var shown = find.Length == 0 ? _cities : _cities.Where(c =>
            new[] { c.Name, c.LocalName, c.Country, c.City.Token, c.Source }
                .Any(text => text.Contains(find, StringComparison.CurrentCultureIgnoreCase))).ToList();
        CityGrid.ItemsSource = shown;
        UpdateCopyButtons();
    }

    void CityGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCopyButtons();

    void UpdateCopyButtons() =>
        CopyCityButton.IsEnabled = CopyGotoButton.IsEnabled = CityGrid.SelectedItem is CityRow;

    void CityGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => CopyCity(c => c.LocalName);

    void CityGrid_Copy(object sender, System.Windows.Input.ExecutedRoutedEventArgs e) => CopyCity(c => c.LocalName);

    void CopyCity_Click(object sender, RoutedEventArgs e) => CopyCity(c => c.LocalName);

    void CopyGoto_Click(object sender, RoutedEventArgs e) => CopyCity(c => c.GotoCommand);

    void CopyCity(Func<CityInfo, string> text)
    {
        if (CityGrid.SelectedItem is not CityRow row) return;
        var copied = text(row.City);
        _app.Status(TrySetClipboard(copied)
            ? $"Copied \"{copied}\"."
            : "The clipboard is in use by another program. Try again.");
    }

    /// <summary>
    /// Windows often reports the clipboard as busy (a clipboard manager is reading it) although the
    /// text was set, so a failure only counts if the text isn't there afterwards.
    /// </summary>
    static bool TrySetClipboard(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                for (var check = 0; check < 10; check++)
                {
                    System.Threading.Thread.Sleep(50);
                    try
                    {
                        if (Clipboard.GetText() == text) return true;
                        break; // readable, but not our text: set it again
                    }
                    catch (System.Runtime.InteropServices.ExternalException)
                    {
                    }
                }
            }
        }
        return false;
    }

    // ---------- Backups ----------

    void RefreshBackups()
    {
        BackupGrid.ItemsSource = _app.Backups.List()
            .Select(b => new BackupRow(b, b.CreatedUtc.ToLocalTime().ToString("dd MMM yyyy HH:mm:ss"), b.Reason,
                string.Join(", ", b.Files.Select(f => Path.GetFileName(f.OriginalPath)))))
            .ToList();
    }

    void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (BackupGrid.SelectedItem is not BackupRow row) return;
        var files = string.Join("\n", row.Backup.Files.Select(f => f.OriginalPath));
        if (MessageBox.Show(this, $"Put these files back as they were on {row.When}?\n\n{files}", "Restore backup",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Run(() =>
        {
            if (_app.GameRunning) throw new GameRunningException();
            _app.Backups.Restore(row.Backup);
            return $"Restored the backup from {row.When}.";
        });
    }

    void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_app.Backups.Root);
        Process.Start(new ProcessStartInfo("explorer.exe", _app.Backups.Root) { UseShellExecute = true });
    }
}
