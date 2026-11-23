using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Ets2u.Core;
using Ets2u.Core.Liveries;
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
        Nav.SelectedItem = ModsPageTab;
        _app.StatusChanged += message => Dispatcher.Invoke(() => StatusText.Text = message);
        _app.Changed += () => Dispatcher.Invoke(RefreshAll);
        Loaded += (_, _) =>
        {
            InitPages();
            // Coach jobs are summarised in the background, so the reader starts with the app.
            if (_app.Settings.RecordCoachJobs) _ = Hub;
            RefreshAll();
            UpdateOverlay();
            // MOUSE DRIVING DISABLED:
            // try { MouseDriveConfig.Write(_app.Settings); }
            // catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppDirs.Log("mouse_drive.ini: " + ex.Message); }
            Run(() => _app.RebuildIfGameUpdated() ?? "Ready");
            // Liveries are big: download Workshop updates, then check the pack's sources off the UI thread.
            StartLiveries();
            LoadWorkshopList();
            StartOptional();
            LoadOptionalWorkshopList();
            // The game can reset controls.sii when its control options change: set them up again.
            // if (_app.MouseControlsNeeded && !_app.GameRunning) Run(() => _app.SetMouseControls(true) + "Mouse driving controls set up again.");
        };
        Activated += (_, _) => UpdateBanner();
        Closed += (_, _) =>
        {
            _downloadCts?.Cancel();
            DepotDownloaderTool.StopRunning();
            _overlay?.Close();
            _alerts?.Close();
            _light?.Close();
            _gMeter?.Close();
            _hub?.Dispose();
            // _mouseIndicator?.Close();
        };
    }

    // ---------- shared ----------

    void RefreshAll()
    {
        _loading = true;
        RefreshSetup();
        RefreshGameplay();
        RefreshProfiles();
        RefreshHud();
        RefreshDriving();
        RefreshTrip();
        RefreshBusDisplay();
        RefreshLiveries();
        RefreshOptional();
        RefreshInfo();
        RefreshBackups();
        RefreshModsPage();
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
        RefreshCoachLimits();
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

    // Each page saves only its own controls, then every mod is rebuilt (only changed files are written).

    void ApplyNoDamage_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        _app.Settings.NoDamage = NoDamageBox.IsChecked == true;
        return _app.ApplyMods();
    });

    void ApplyEarnings_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        _app.Settings.IncomeMultiplier = Math.Round(IncomeSlider.Value);
        _app.Settings.XpMultiplier = Math.Round(XpSlider.Value);
        return _app.ApplyMods();
    });

    void ApplyCoach_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var s = _app.Settings;
        var limits = ReadCoachLimits();
        var ignored = CoachFactorBoxes.Where(b => b.IsChecked == true)
            .Aggregate(CoachFactor.None, (all, b) => all | Enum.Parse<CoachFactor>((string)b.Tag));
        if (CoachJobFailureBox.IsChecked == true) ignored |= CoachFactor.JobFailure;
        s.CoachIgnored = ignored;
        s.CoachFullSatisfaction = CoachFullBox.IsChecked == true;
        s.CoachLimits = limits.IsStock ? null : limits;
        var result = _app.ApplyMods();
        UpdateOverlay();
        return result;
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
                ? $"{(_save.Format == Core.Save.SiiFormat.Binary ? "Binary" : "Text")} save: ready to edit. " +
                  $"Money up to {_save.MaxMoney.ToString("N0", CultureInfo.CurrentCulture)}" +
                  (_save.MoneyIs64Bit ? " (the save stores money as a 64-bit number)." : ".")
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
            (after.Money < 0 || after.Money > _save!.MaxMoney ? "  (money out of range)" : ""));
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
        PluginStatus.Text = DescribePlugin();
        var canInstall = source is not null && install is not null;
        InstallPluginButton.IsEnabled = InstallPluginButton2.IsEnabled = canInstall && !PluginInstaller.IsCurrent(install);
        // "Reinstall" always works: it also repairs a plugin file that was damaged or replaced.
        ReinstallPluginButton.IsEnabled = canInstall;
        ReinstallPluginButton.Content = PluginInstaller.IsInstalled(install) ? "Reinstall plugin" : "Install plugin";
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
        return "Plugin installed (speed overlay and driving alerts). Start ETS2 and accept the SDK plugins notice.";
    });

    void RemovePlugin_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        PluginInstaller.Remove(_app.Paths.InstallDir ?? throw new InvalidOperationException("ETS2 wasn't found."), _app.Backups);
        return "Speed plugin removed.";
    });

    SpeedOverlay? _overlay;

    static readonly int[] LightMinutes = [30, 60, 120];
    static readonly KeyValuePair<LightStrictness, string>[] StrictnessNames =
    [
        new(LightStrictness.Realistic, "Realistic: yellow from 2 points, red from 4"),
        new(LightStrictness.Normal, "Normal: yellow from 4 points, red from 8"),
        new(LightStrictness.Relaxed, "Relaxed: yellow from 8 points, red from 16"),
    ];

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
        var s = _app.Settings;
        if (s.SpeedOverlay)
        {
            _overlay ??= new SpeedOverlay(s, Hub);
            _overlay.Apply(s);
        }
        else
        {
            _overlay?.Close();
            _overlay = null;
        }

        if ((s.CoachAlerts || s.TruckAlerts) && s.AlertCards)
        {
            _alerts ??= new DrivingAlertsOverlay(s, Hub);
            _alerts.Apply(s);
        }
        else
        {
            _alerts?.Close();
            _alerts = null;
        }

        if ((s.CoachAlerts || s.TruckAlerts) && s.TrafficLight)
        {
            _light ??= new TrafficLightOverlay(s, Hub);
            _light.Apply(s);
        }
        else
        {
            _light?.Close();
            _light = null;
        }

        if (s.GForceMeter)
        {
            _gMeter ??= new GForceMeter(s, Hub);
            _gMeter.Apply(s);
        }
        else
        {
            _gMeter?.Close();
            _gMeter = null;
        }

        // MOUSE DRIVING DISABLED:
        // if (s.MouseDrive && s.MouseIndicator)
        // {
        //     _mouseIndicator ??= new MouseDriveIndicator();
        // }
        // else
        // {
        //     _mouseIndicator?.Close();
        //     _mouseIndicator = null;
        // }
    }

    // ---------- Driving ----------

    DrivingAlertsOverlay? _alerts;
    TrafficLightOverlay? _light;
    GForceMeter? _gMeter;
    TelemetryHub? _hub;

    /// <summary>One reader of the plugin for every overlay, started when the first one opens.</summary>
    TelemetryHub Hub
    {
        get
        {
            if (_hub is null)
            {
                _hub = new TelemetryHub(_app.Settings, _app.Paths.InstallDir);
                _hub.JobFinished += OnJobFinished;
            }
            return _hub;
        }
    }

    void GMeter_Click(object sender, RoutedEventArgs e)
    {
        _app.Settings.GForceMeter = GMeterBox.IsChecked == true;
        _app.Settings.Save();
        UpdateOverlay();
        _app.Status(_app.Settings.GForceMeter ? "G-force meter on. Drag it where you like." : "G-force meter off.");
    }
    // MouseDriveIndicator? _mouseIndicator;

    // static readonly Dictionary<MouseToggle, string> ToggleNames = new()
    // {
    //     [MouseToggle.F8] = "F8",
    //     [MouseToggle.Insert] = "Insert",
    //     [MouseToggle.MiddleButton] = "Middle mouse button",
    // };

    void RefreshDriving()
    {
        var s = _app.Settings;
        var install = _app.Paths.InstallDir;
        DrivingPluginStatus.Text = DescribePlugin();

        // MOUSE DRIVING DISABLED:
        // MouseDriveBox.IsChecked = s.MouseDrive;
        // MouseToggleBox.ItemsSource = ToggleNames;
        // MouseToggleBox.DisplayMemberPath = "Value";
        // MouseToggleBox.SelectedValuePath = "Key";
        // MouseToggleBox.SelectedValue = s.MouseToggle;
        // MouseSensSlider.Value = s.MouseSensitivity;
        // MousePedalSlider.Value = s.MousePedalStrength;
        // MouseIndicatorBox.IsChecked = s.MouseIndicator;
        // var (ready, total) = _app.MouseControlsState();
        // MouseDriveStatus.Text = !s.MouseDrive ? "Mouse driving off."
        //     : total == 0 ? "No profile's controls.sii was found, so the camera will still follow the mouse. Start ETS2 once with your profile, close it, and press Apply mouse driving."
        //     : ready < total ? $"{total - ready} of {total} profile(s) still have controls that fight the mouse (the game may have reset them). Close the game and press Apply mouse driving."
        //     : $"Mouse driving ready: press {MouseDriveConfig.ToggleName(s.MouseToggle)} while driving to switch it on or off.";

        CoachAlertsBox.IsChecked = s.CoachAlerts;
        TruckAlertsBox.IsChecked = s.TruckAlerts;
        GMeterBox.IsChecked = s.GForceMeter;
        CoachSourceBox.SelectedValue = s.CoachAlertSource;
        CoachRelaxBox.SelectedValue = s.CoachAlertRelax;
        TruckSourceBox.SelectedValue = s.TruckAlertSource;
        TruckRelaxBox.SelectedValue = s.TruckAlertRelax;
        _customDraft = (s.CoachThresholds, s.TruckThresholds);
        _shownSource = (s.CoachAlertSource, s.TruckAlertSource);
        ShowThresholds(s.CoachThresholds, s.TruckThresholds);
        ShowAlertSources();
        SpeedingBox.IsChecked = s.SpeedingAlerts;
        AlertCardsBox.IsChecked = s.AlertCards;
        TrafficLightBox.IsChecked = s.TrafficLight;
        TrafficLightCornerBox.ItemsSource = CornerNames;
        TrafficLightCornerBox.DisplayMemberPath = "Value";
        TrafficLightCornerBox.SelectedValuePath = "Key";
        TrafficLightCornerBox.SelectedValue = s.TrafficLightCorner;
        TrafficLightScaleSlider.Value = s.TrafficLightScale;
        TrafficLightMinutesBox.ItemsSource = LightMinutes;
        TrafficLightMinutesBox.SelectedItem = LightMinutes.Contains(s.TrafficLightGameMinutes) ? s.TrafficLightGameMinutes : 60;
        TrafficLightStrictnessBox.ItemsSource = StrictnessNames;
        TrafficLightStrictnessBox.DisplayMemberPath = "Value";
        TrafficLightStrictnessBox.SelectedValuePath = "Key";
        TrafficLightStrictnessBox.SelectedValue = s.TrafficLightStrictness;
        AlertsStatus.Text = DescribeAlerts(s);
    }

    static string DescribeAlerts(AppSettings s) => (s.CoachAlerts, s.TruckAlerts) switch
    {
        (true, true) => "Alerts on in coaches and trucks. They appear while you drive, and hide while the game is paused or closed.",
        (true, false) => "Alerts on in coaches only.",
        (false, true) => "Alerts on in trucks only.",
        _ => "Alerts off.",
    };

    (TextBox Yellow, TextBox Red, string Name)[] ThresholdBoxes(bool coach) => coach
        ? [(CoachAccelYellowBox, CoachAccelRedBox, "Coach acceleration"), (CoachBrakeYellowBox, CoachBrakeRedBox, "Coach braking"),
           (CoachCornerYellowBox, CoachCornerRedBox, "Coach cornering")]
        : [(TruckAccelYellowBox, TruckAccelRedBox, "Truck acceleration"), (TruckBrakeYellowBox, TruckBrakeRedBox, "Truck braking"),
           (TruckCornerYellowBox, TruckCornerRedBox, "Truck cornering")];

    void ShowThresholds(DrivingThresholds coach, DrivingThresholds truck)
    {
        static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        foreach (var (t, isCoach) in new[] { (coach, true), (truck, false) })
        {
            var b = ThresholdBoxes(isCoach);
            (b[0].Yellow.Text, b[0].Red.Text) = (F(t.AccelYellow), F(t.AccelRed));
            (b[1].Yellow.Text, b[1].Red.Text) = (F(t.BrakeYellow), F(t.BrakeRed));
            (b[2].Yellow.Text, b[2].Red.Text) = (F(t.CornerYellow), F(t.CornerRed));
        }
    }

    DrivingThresholds ReadThresholds(bool coach)
    {
        var b = ThresholdBoxes(coach);
        return new DrivingThresholds
        {
            AccelYellow = ReadG(b[0].Yellow, b[0].Name + ", yellow"), AccelRed = ReadG(b[0].Red, b[0].Name + ", red"),
            BrakeYellow = ReadG(b[1].Yellow, b[1].Name + ", yellow"), BrakeRed = ReadG(b[1].Red, b[1].Name + ", red"),
            CornerYellow = ReadG(b[2].Yellow, b[2].Name + ", yellow"), CornerRed = ReadG(b[2].Red, b[2].Name + ", red"),
        }.Normalised();
    }

    static double ReadG(TextBox box, string name) =>
        double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0.05
            ? v
            : throw new FormatException($"{name}: enter a g-force of at least 0.05, such as 0.35.");

    // MOUSE DRIVING DISABLED:
    // void ApplyMouseDrive_Click(object sender, RoutedEventArgs e) => Run(() =>
    // {
    //     var s = _app.Settings;
    //     s.MouseDrive = MouseDriveBox.IsChecked == true;
    //     s.MouseToggle = MouseToggleBox.SelectedValue is MouseToggle t ? t : MouseToggle.F8;
    //     s.MouseSensitivity = Math.Round(MouseSensSlider.Value, 1);
    //     s.MousePedalStrength = Math.Round(MousePedalSlider.Value, 1);
    //     s.MouseIndicator = MouseIndicatorBox.IsChecked == true;
    //     s.Save();
    //     MouseDriveConfig.Write(s);
    //     UpdateOverlay();
    //     var controls = _app.SetMouseControls(s.MouseDrive);
    //     return controls + (s.MouseDrive
    //         ? $"Mouse driving on. In the game, press {MouseDriveConfig.ToggleName(s.MouseToggle)} to start steering with the mouse."
    //         : "Mouse driving off.");
    // });

    void ApplyAlerts_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var s = _app.Settings;
        s.CoachAlertSource = Source(coach: true);
        s.CoachAlertRelax = Relax(coach: true);
        s.TruckAlertSource = Source(coach: false);
        s.TruckAlertRelax = Relax(coach: false);
        // Only custom numbers are kept from the boxes; the others are worked out from their source.
        if (s.CoachAlertSource == AlertSource.Custom) s.CoachThresholds = ReadThresholds(coach: true);
        if (s.TruckAlertSource == AlertSource.Custom) s.TruckThresholds = ReadThresholds(coach: false);
        s.CoachAlerts = CoachAlertsBox.IsChecked == true;
        s.TruckAlerts = TruckAlertsBox.IsChecked == true;
        s.SpeedingAlerts = SpeedingBox.IsChecked == true;
        s.AlertCards = AlertCardsBox.IsChecked == true;
        s.TrafficLight = TrafficLightBox.IsChecked == true;
        s.TrafficLightCorner = TrafficLightCornerBox.SelectedValue is OverlayCorner corner ? corner : OverlayCorner.TopCenter;
        s.TrafficLightScale = Math.Round(TrafficLightScaleSlider.Value, 1);
        s.TrafficLightGameMinutes = TrafficLightMinutesBox.SelectedItem is int minutes ? minutes : 60;
        s.TrafficLightStrictness = TrafficLightStrictnessBox.SelectedValue is LightStrictness strictness ? strictness : LightStrictness.Normal;
        s.Save();
        UpdateOverlay();
        return DescribeAlerts(s);
    });

    /// <summary>Both vehicles back on their defaults, not relaxed.</summary>
    void DefaultThresholds_Click(object sender, RoutedEventArgs e)
    {
        CoachSourceBox.SelectedValue = TruckSourceBox.SelectedValue = AlertSource.Default;
        CoachRelaxBox.SelectedValue = TruckRelaxBox.SelectedValue = AlertRelax.None;
        ShowAlertSources();
    }

    static Dictionary<AlertSource, string> SourceNames(bool coach) => new()
    {
        [AlertSource.Default] = coach ? "Defaults (0.35 / 0.7 g)" : "Defaults (0.4 / 0.8 g)",
        [AlertSource.CoachMod] = "Coach mod's passenger limits",
        [AlertSource.Custom] = "My own numbers (below)",
    };

    static readonly Dictionary<AlertRelax, string> RelaxNames = new()
    {
        [AlertRelax.None] = "As they are",
        [AlertRelax.Plus10] = "10% looser",
        [AlertRelax.Plus20] = "20% looser",
    };

    AlertSource Source(bool coach) =>
        (coach ? CoachSourceBox : TruckSourceBox).SelectedValue is AlertSource s ? s : AlertSource.Default;

    AlertRelax Relax(bool coach) =>
        (coach ? CoachRelaxBox : TruckRelaxBox).SelectedValue is AlertRelax r ? r : AlertRelax.None;

    /// <summary>The saved custom numbers, used when the boxes can't be read.</summary>
    (DrivingThresholds Coach, DrivingThresholds Truck) _customDraft = (DrivingThresholds.Coach, DrivingThresholds.Truck);
    (AlertSource Coach, AlertSource Truck) _shownSource;

    void AlertSource_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading && IsLoaded) ShowAlertSources();
    }

    /// <summary>
    /// Custom columns can be typed in; the others show, greyed out, the numbers their source gives. Switching
    /// to custom starts from the numbers that were showing, so a source can be fine-tuned.
    /// </summary>
    void ShowAlertSources()
    {
        static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        var s = _app.Settings;
        foreach (var coach in new[] { true, false })
        {
            var source = Source(coach);
            var was = coach ? _shownSource.Coach : _shownSource.Truck;
            var boxes = ThresholdBoxes(coach);
            DrivingThresholds shown;
            if (source != AlertSource.Custom) shown = Resolved(coach, source);
            // Staying custom (or opening the page on it): what's in the boxes. Just switched to custom:
            // start from the numbers that were showing.
            else if (was == AlertSource.Custom) shown = ReadOrNull(coach) ?? (coach ? _customDraft.Coach : _customDraft.Truck);
            else shown = Resolved(coach, was);
            foreach (var (yellow, red, _) in boxes) yellow.IsEnabled = red.IsEnabled = source == AlertSource.Custom;
            (boxes[0].Yellow.Text, boxes[0].Red.Text) = (F(shown.AccelYellow), F(shown.AccelRed));
            (boxes[1].Yellow.Text, boxes[1].Red.Text) = (F(shown.BrakeYellow), F(shown.BrakeRed));
            (boxes[2].Yellow.Text, boxes[2].Red.Text) = (F(shown.CornerYellow), F(shown.CornerRed));
            (coach ? CoachRelaxBox : TruckRelaxBox).IsEnabled = source != AlertSource.Custom;
            _shownSource = coach ? (source, _shownSource.Truck) : (_shownSource.Coach, source);
        }
        var fromMod = Source(true) == AlertSource.CoachMod || Source(false) == AlertSource.CoachMod;
        CoachThresholdNote.Text = !fromMod ? ""
            : s.CoachLimits is null ? "The coach mod doesn't change the passengers' limits yet, so its Stock ones are used (Coach passengers page)."
            : "Coach mod numbers come from the Coach passengers page; change them there.";

        DrivingThresholds Resolved(bool coach, AlertSource source) => DrivingThresholds.Resolve(
            source, Relax(coach), coach ? DrivingThresholds.Coach : DrivingThresholds.Truck,
            coach ? _customDraft.Coach : _customDraft.Truck, s.AlertLimits);
    }

    DrivingThresholds? ReadOrNull(bool coach)
    {
        try { return ReadThresholds(coach); }
        catch (FormatException) { return null; }
    }

    // ---------- Game Information ----------

    sealed record CityRow(CityInfo City, string Name, string LocalName, string Country, string Source);

    IReadOnlyList<CityRow> _cities = Array.Empty<CityRow>();

    void RefreshInfo()
    {
        var info = _app.Info;
        var currencies = info?.Currencies ?? [CurrencyInfo.Euro];
        FuelCurrencyBox.ItemsSource = currencies;
        FuelCurrencyBox.SelectedItem = currencies.FirstOrDefault(c => c.Code == _app.Settings.FuelCurrency) ?? currencies[0];
        ShowFuelPrices();
        _cities = (info?.Cities ?? Array.Empty<CityInfo>())
            .Select(c => new CityRow(c, c.Name, c.LocalName, c.Country,
                c.Source == GameInfo.BaseArchive ? "Base game" : c.Source))
            .ToList();
        FuelNote.Text = info is null
            ? _app.InfoProblem ?? "ETS2 wasn't found."
            : "Base price in each country, from the game's files, in any currency the game's options offer (at the game's own exchange rates). Countries of map DLCs this PC doesn't have are left out.";
        CityNote.Text = info is null ? ""
            : "Double-click a city (or press Ctrl+C) to copy its name as the game's console expects it, e.g. " +
              "\"goto Köln\". After goto moves the camera, press Ctrl+F9 to bring the truck there.";
        FilterCities();
    }

    sealed record FuelRow(string Name, decimal? Price, string PriceText, int CityCount);

    void ShowFuelPrices()
    {
        var currency = FuelCurrencyBox.SelectedItem as CurrencyInfo ?? CurrencyInfo.Euro;
        FuelPriceColumn.Header = $"{currency.Code} per litre";
        FuelGrid.ItemsSource = _app.Info?.Countries
            .Select(c => new FuelRow(c.Name, c.FuelPrice * currency.Ratio, c.FuelPrice is { } p ? currency.Format(p) : "", c.CityCount))
            .ToList();
    }

    void FuelCurrency_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FuelCurrencyBox.SelectedItem is not CurrencyInfo currency) return;
        ShowFuelPrices();
        if (_app.Settings.FuelCurrency == currency.Code) return;
        _app.Settings.FuelCurrency = currency.Code;
        _app.Settings.Save();
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
        var owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _app.Status(ClipboardText.TrySet(owner, copied)
            ? $"Copied \"{copied}\"."
            : "The clipboard is in use by another program. Try again.");
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
