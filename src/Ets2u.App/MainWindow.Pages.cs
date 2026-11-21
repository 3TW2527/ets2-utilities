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

/// <summary>The "Mods &amp; plugins" overview and the coach g-force limits.</summary>
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
            : !PluginInstaller.IsInstalled(install) ? "Plugin not installed yet: install it below or on the Mods & plugins page."
            : PluginInstaller.IsCurrent(install) ? "Plugin installed. ETS2 shows an \"SDK plugins\" notice when it starts; that's this plugin."
            : "An older plugin is installed: install it again, or these features won't work.";
    }

    // ---------- Mods & plugins ----------

    sealed record ModRow(string Name, string Kind, string File, string State, TabItem Page);

    void RefreshModsPage()
    {
        List<ModRow> rows =
        [
            ModRowFor(NoDamage.Mod, "No damage", NoDamageTab),
            ModRowFor(Earnings.Mod, "Bigger earnings", EarningsTab),
            ModRowFor(CoachComfort.Mod, "Coach passengers", CoachTab),
            ModRowFor(HudMod.Mod, "Route advisor", HudTab),
            ModRowFor(BusDisplay.Mod, "Destination display", BusDisplayTab),
            LiveryPackRow(),
            .. OptionalRows(),
            new("Telemetry plugin", "Plugin", PluginInstaller.FileName, DescribePlugin() + " Used by: speed overlay, driving alerts, traffic light, trip and coach job summaries.", OverlayTab),
        ];
        ModsGrid.ItemsSource = rows;
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
