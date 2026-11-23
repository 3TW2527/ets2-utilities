using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Ets2u.Core.Features;
using Ets2u.Core.Liveries;
using Ets2u.Core.Mods;
using Ets2u.Core.Telemetry;

namespace Ets2u.App;

/// <summary>The "Start here" overview, the menu's on/off dots and the coach g-force limits.</summary>
public partial class MainWindow
{
    void InitPages()
    {
        GPresetBox.ItemsSource = CoachGLimits.Presets.Select(p => p.Name).Append("Custom").ToList();
        foreach (var (box, names) in new[]
                 {
                     (CoachSourceBox, (System.Collections.IEnumerable)SourceNames(coach: true)), (TruckSourceBox, SourceNames(coach: false)),
                     (CoachRelaxBox, RelaxNames), (TruckRelaxBox, RelaxNames),
                 })
        {
            box.ItemsSource = names;
            box.DisplayMemberPath = "Value";
            box.SelectedValuePath = "Key";
        }
        BusColorBox.ItemsSource = Enum.GetValues<DisplayColor>();
    }

    string DescribePlugin()
    {
        var install = _app.Paths.InstallDir;
        return PluginInstaller.FindSource() is null ? "The plugin file (ets2u_telemetry.dll) isn't next to the app."
            : !PluginInstaller.IsInstalled(install) ? "Plugin not installed yet: install it below or on the Start here page."
            : PluginInstaller.IsCurrent(install) ? "Plugin installed. ETS2 shows an \"SDK plugins\" notice when it starts; that's this plugin."
            : "An older plugin is installed: install it again, or these features won't work.";
    }

    // ---------- Start here ----------

    readonly Dictionary<TabItem, string> _navNames = new();

    /// <summary>
    /// Puts a dot before each page in the menu: green when its feature is on, hollow when off, amber when
    /// it's on but needs a plugin that isn't installed (or is out of date). Pages without a switch get none.
    /// </summary>
    void RefreshNav()
    {
        var s = _app.Settings;
        var pluginOk = PluginInstaller.IsCurrent(_app.Paths.InstallDir);
        var states = new Dictionary<TabItem, (bool On, bool Plugin)>
        {
            [NoDamageTab] = (s.NoDamage, false),
            [EarningsTab] = (s.IncomeMultiplier != 1 || s.XpMultiplier != 1, false),
            [CoachTab] = (s.CoachLimits is not null || s.CoachIgnored != default || s.CoachFullSatisfaction, false),
            [HudTab] = (s.MapPositionEnabled, false),
            [BusDisplayTab] = (s.BusDisplay.Enabled, false),
            [LiveryTab] = (s.LiveryPack.Enabled, false),
            [OptionalTab] = (s.OptionalMods.Mods.Any(m => m.Source.Included), false),
            [OverlayTab] = (s.SpeedOverlay, true),
            [AlertsTab] = (s.CoachAlerts || s.TruckAlerts, true),
            [TripTab] = (s.RecordCoachJobs, true),
        };
        foreach (var tab in Nav.Items.OfType<TabItem>().Where(t => t.Tag as string != "group"))
        {
            if (!_navNames.TryGetValue(tab, out var name)) _navNames[tab] = name = tab.Header as string ?? "";
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            string? tip = null;
            if (states.TryGetValue(tab, out var state))
            {
                var warn = state.On && state.Plugin && !pluginOk;
                if (warn) dot.Fill = Ui.Brush(0xE0, 0x9A, 0x1A);
                else if (state.On) dot.Fill = Ui.Brush(0x2E, 0xA0, 0x5B);
                else { dot.Stroke = Ui.Brush(0xA0, 0xA8, 0xB4); dot.StrokeThickness = 1.2; }
                tip = warn ? "On, but the plugin isn't installed or is out of date" : state.On ? "On" : "Off";
            }
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(dot);
            header.Children.Add(new TextBlock { Text = name });
            tab.Header = header;
            tab.ToolTip = tip;
        }
        StepPickStatus.Text = states.Values.Count(x => x.On) switch
        {
            0 => "Nothing is switched on yet.",
            1 => "1 feature is on (green dot).",
            var n => $"{n} features are on (green dots).",
        };
    }

    void RefreshSteps()
    {
        var s = _app.Settings;
        StepApplyStatus.Text = _app.Paths.InstallDir is null ? "ETS2 wasn't found: check that it's installed through Steam."
            : _app.GameRunning ? "ETS2 is running: close it to apply."
            : GeneratedMods.All.Any(_app.ModExists) && s.ModsBuiltForGameStamp != _app.Paths.VersionStamp
                ? "The mods were built for an older game version: press Rebuild all mods."
                : "ETS2 is closed: ready to apply.";

        var install = _app.Paths.InstallDir;
        var plugin = PluginInstaller.FindSource() is null ? "Plugin file missing next to the app."
            : !PluginInstaller.IsInstalled(install) ? "Plugin: not installed."
            : !PluginInstaller.IsCurrent(install) ? "Plugin: out of date, install it again."
            : "Plugin: installed.";
        var mods = GeneratedMods.All.Where(_app.ModExists).Select(_app.Mine)
            .Concat(_app.LiveryPackExists ? [_app.LiveryMod] : [])
            .Concat(s.OptionalMods.Mods.Where(m => m.Source.Included && _app.OptionalExists(m)).Select(_app.OptionalSpec))
            .ToList();
        var notEnabled = mods.Count(m => ModStatus.ActiveIn(_app.Profiles, m).Count == 0);
        StepGameStatus.Text = plugin + " " + (mods.Count == 0 ? "No mods made yet."
            : notEnabled == 0 ? "Every mod is enabled in a profile."
            : notEnabled == 1 ? "1 mod isn't enabled in Mod Manager yet."
            : $"{notEnabled} mods aren't enabled in Mod Manager yet.");
        StartInstallPluginButton.Visibility = PluginInstaller.IsCurrent(install) ? Visibility.Collapsed : Visibility.Visible;
        StartInstallPluginButton.Content = PluginInstaller.IsInstalled(install) ? "Reinstall plugin" : "Install plugin";
    }

    sealed record ModRow(string Name, string Kind, string File, string State, TabItem Page);

    void RefreshModsPage()
    {
        List<ModRow> rows =
        [
            ModRowFor(NoDamage.Mod, "No damage", NoDamageTab),
            ModRowFor(Earnings.Mod, "Bigger earnings", EarningsTab),
            ModRowFor(CoachComfort.Mod, "Coach passengers", CoachTab),
            ModRowFor(HudMod.Mod, "HUD", HudTab),
            ModRowFor(BusDisplay.Mod, "Destination display", BusDisplayTab),
            LiveryPackRow(),
            .. OptionalRows(),
            new("Telemetry plugin", "Plugin", PluginInstaller.FileName, DescribePlugin() + " Used by: speed overlay, driving alerts, traffic light, trip and coach job summaries.", OverlayTab),
        ];
        ModsGrid.ItemsSource = rows;
        RefreshNav();
        RefreshSteps();
    }

    ModRow ModRowFor(ModSpec mod, string name, TabItem page)
    {
        var state = DescribeMod(mod);
        if (_app.ModExists(mod) && _app.Settings.ModsBuiltForGameStamp != _app.Paths.VersionStamp)
            state = "Built for an older game version: press Rebuild all mods. " + state;
        return new ModRow(name, "Mod", _app.Mine(mod).FileName, state, page);
    }

    ModRow LiveryPackRow()
    {
        var s = _app.Settings.LiveryPack;
        var file = _app.LiveryMod.FileName;
        if (!s.Enabled) return new ModRow("Livery pack", "Mod", file, _app.LiveryPackExists ? "Switched off; still in the mod folder." : "Off (optional).", LiveryTab);
        var count = s.Sources.Count(x => x.Included);
        string state;
        if (!_app.LiveryPackExists) state = $"Not built yet ({count} mod(s) chosen).";
        else
        {
            var active = ModStatus.ActiveIn(_app.Profiles, _app.LiveryMod);
            state = $"{count} mod(s). " + (active.Count == 0 ? "Not enabled in any profile yet." : "Enabled in: " + string.Join(", ", active) + ".");
            if (_liveryOutdated) state = "Sources changed since it was built. " + state;
        }
        return new ModRow("Livery pack", "Mod", file, state, LiveryTab);
    }

    /// <summary>One row per optional copy (none when the page was never used).</summary>
    IEnumerable<ModRow> OptionalRows() => _app.Settings.OptionalMods.Mods.Select(m =>
    {
        var spec = _app.OptionalSpec(m);
        string state;
        if (!m.Source.Included) state = _app.OptionalExists(m) ? "Switched off; still in the mod folder." : "Off.";
        else if (!_app.OptionalExists(m)) state = "Not built yet.";
        else
        {
            var active = ModStatus.ActiveIn(_app.Profiles, spec);
            state = (_app.OptionalOutdated(m) ? "The original changed since it was built. " : "") +
                    (active.Count == 0 ? "Not enabled in any profile yet." : "Enabled in: " + string.Join(", ", active) + ".");
        }
        return new ModRow("Optional: " + m.Source.Name, "Mod", spec.FileName, state, OptionalTab);
    });

    void ModsGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ModsGrid.SelectedItem is ModRow row) Nav.SelectedItem = row.Page;
    }

    void RebuildAll_Click(object sender, RoutedEventArgs e)
    {
        Run(_app.ApplyMods);
        if (_app.Settings.LiveryPack.Enabled) RunLiveryBuild(progress => _app.BuildLiveryPack(progress));
        if (_app.Settings.OptionalMods.Mods.Count > 0)
            RunOptionalBuild(progress => _app.SyncOptional(progress) ?? "The optional copies are up to date.");
    }

    void OpenModFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_app.Paths.ModDir);
        Process.Start(new ProcessStartInfo("explorer.exe", _app.Paths.ModDir) { UseShellExecute = true });
    }

    // ---------- Coach g-force limits ----------

    bool _showingLimits;

    void RefreshCoachLimits()
    {
        var s = _app.Settings;
        ShowLimits(s.CoachLimits ?? CoachGLimits.Stock);
        GLimitsNote.Text = s.CoachIgnored.HasFlag(CoachFactor.DrivingStyle)
            ? "Driving style is switched off below, so g-force never lowers satisfaction and these limits don't matter."
            : s.CoachLimits is null
                ? "The coach mod doesn't change the limits yet: the game's own apply (harsh for braking only). Apply to use the ones above."
                : "";
    }

    static string G(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    static string G(double? v) => v is { } x ? G(x) : "";

    void ShowLimits(CoachGLimits l)
    {
        _showingLimits = true;
        GCornerBox.Text = G(l.Cornering);
        GVerticalBox.Text = G(l.Vertical);
        GBrakeBox.Text = G(l.Braking);
        GCornerHarshBox.Text = G(l.HarshCornering);
        GVerticalHarshBox.Text = G(l.HarshVertical);
        GBrakeHarshBox.Text = G(l.HarshBraking);
        GFailureBox.Text = G(l.Failure);
        var preset = CoachGLimits.Presets.Select((p, i) => (p, i)).FirstOrDefault(x => x.p.Limits.Rounded() == l.Rounded());
        GPresetBox.SelectedIndex = preset.p.Name is null ? CoachGLimits.Presets.Count : preset.i;
        _showingLimits = false;
    }

    void GPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_showingLimits || GPresetBox.SelectedIndex < 0 || GPresetBox.SelectedIndex >= CoachGLimits.Presets.Count) return;
        ShowLimits(CoachGLimits.Presets[GPresetBox.SelectedIndex].Limits);
    }

    CoachGLimits ReadCoachLimits()
    {
        static double Req(TextBox box, string name) => Opt(box, name) ?? throw new FormatException($"{name}: enter a g-force, such as 0.85.");
        static double? Opt(TextBox box, string name)
        {
            var t = box.Text.Trim().Replace(',', '.');
            if (t.Length == 0) return null;
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v
                : throw new FormatException($"{name}: \"{box.Text}\" isn't a number.");
        }
        return new CoachGLimits
        {
            Cornering = Req(GCornerBox, "Cornering"),
            Vertical = Req(GVerticalBox, "Bumps and crests"),
            Braking = Req(GBrakeBox, "Accelerating and braking"),
            HarshCornering = Req(GCornerHarshBox, "Harsh cornering"),
            HarshVertical = Req(GVerticalHarshBox, "Harsh bumps"),
            HarshBraking = Req(GBrakeHarshBox, "Harsh braking"),
            Failure = Opt(GFailureBox, "Job failure"),
        }.Validated();
    }
}
