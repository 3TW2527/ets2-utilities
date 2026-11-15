using System.IO.Compression;
using System.Text;
using Ets2u.Core.Features;
using Ets2u.Core.Liveries;
using Ets2u.Core.Mods;
using Ets2u.Core.Paths;
using Ets2u.Core.Sii;

namespace Ets2u.Core.Tests;

public class CoachGLimitTests
{
    // The g-force part of the game's bus_economy_data.sii.
    const string Stock =
        "SiiNunit\n{\nbus_economy_data : bus_economy.data\n{\n" +
        "\tsatisfaction_acceleration_limits[]: (6.0, 6.0, 6.0)\n" +
        "\tsatisfaction_acceleration_penalties[]: (0.09, 0.18)\n" +
        "\tsatisfaction_acceleration_limits[]: (1000.0, 1000.0, 11.0)\n" +
        "\tsatisfaction_acceleration_penalties[]: (0.6, 0.9)\n" +
        "\tsatisfaction_late_penalty: (0.05, 0.13)\n" +
        "\tfailure_acceleration_limit: (-1.0, -1.0, 22.0)\n" +
        "}\n}\n";

    static (SiiDocument Doc, SiiUnit Unit) Build(CoachGLimits? limits, CoachFactor ignored = CoachFactor.None)
    {
        var doc = SiiDocument.Parse(CoachComfort.Build(Encoding.UTF8.GetBytes(Stock), ignored, false, limits));
        return (doc, doc.FindUnit("bus_economy_data")!);
    }

    [Fact]
    public void StockLimitsWriteTheStockFile()
    {
        var stock = Encoding.UTF8.GetBytes(Stock);
        Assert.True(CoachGLimits.Game.IsStock);
        Assert.False(CoachGLimits.Stock.IsStock); // the Stock preset adds harsh limits the game lacks
        Assert.Equal(stock, CoachComfort.Build(stock, CoachFactor.None, false, CoachGLimits.Game));
        // As typed back from the page (two decimals in g), it's still the stock file.
        var typed = new CoachGLimits { Cornering = 0.61, Vertical = 0.61, Braking = 0.61, HarshBraking = 1.12, Failure = 2.24 };
        Assert.True(typed.IsStock);
        Assert.Equal(stock, CoachComfort.Build(stock, CoachFactor.None, false, typed));
    }

    [Fact]
    public void LimitsGoIntoBothTiersInMetresPerSecondSquared()
    {
        var limits = new CoachGLimits { Cornering = 1.0, Vertical = 0.9, Braking = 1.2, HarshCornering = 1.5, Failure = 2.5 };
        var (doc, unit) = Build(limits);
        Assert.Equal(["(9.8, 8.8, 11.8)", "(14.7, 1000.0, 1000.0)"], doc.GetValues(unit, CoachComfort.LimitsKey));
        Assert.Equal("(-1.0, -1.0, 24.5)", doc.GetValue(unit, CoachComfort.FailureLimitKey));
        // Penalties stay: the limits only move where they start.
        Assert.Equal(["(0.09, 0.18)", "(0.6, 0.9)"], doc.GetValues(unit, "satisfaction_acceleration_penalties[]"));
    }

    [Fact]
    public void JobFailureSwitchWinsOverTheLimit()
    {
        var (doc, unit) = Build(CoachGLimits.Relaxed, CoachFactor.JobFailure);
        Assert.Equal("(-1.0, -1.0, -1.0)", doc.GetValue(unit, CoachComfort.FailureLimitKey));
    }

    [Fact]
    public void NoFailureLimitMeansNever()
    {
        var (doc, unit) = Build(CoachGLimits.Forgiving with { Failure = null });
        Assert.Equal("(-1.0, -1.0, -1.0)", doc.GetValue(unit, CoachComfort.FailureLimitKey));
    }

    [Theory]
    [InlineData(0.6, 0.5)] // harsh below normal
    [InlineData(0.05, null)] // too small (there's no upper limit)
    public void RejectsLimitsThatMakeNoSense(double cornering, double? harshCornering) =>
        Assert.Throws<FormatException>(() =>
            (CoachGLimits.Stock with { Cornering = cornering, HarshCornering = harshCornering }).Validated());

    [Fact]
    public void PresetsAreValid()
    {
        foreach (var (_, limits) in CoachGLimits.Presets) limits.Validated();
    }

    [Fact]
    public void LimitsSurviveSettingsRoundTrip()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        new AppSettings { CoachLimits = CoachGLimits.Forgiving }.Save(path);
        Assert.Equal(CoachGLimits.Forgiving, AppSettings.Load(path).CoachLimits);
    }

    [Fact]
    public void CoachModIsBuiltForLimitsAlone()
    {
        var game = new Extract.InMemoryGameFiles(new Dictionary<string, byte[]> { [CoachComfort.GamePath] = Encoding.UTF8.GetBytes(Stock) });
        var s = new AppSettings { ModTag = "abc123", CoachLimits = CoachGLimits.Relaxed };
        Assert.Contains("ets2u_coach_abc123.scs", GeneratedMods.Build(game, s, _ => false).Keys);
        s.CoachLimits = CoachGLimits.Game;
        Assert.DoesNotContain("ets2u_coach_abc123.scs", GeneratedMods.Build(game, s, _ => false).Keys);
    }
}

public class BusDisplayTests
{
    // The game's ui/template/dashboard_text.dlc_bus.sii (dlc_bus.scs, 1.62).
    const string Stock =
        "SiiNunit\n{\n\nui::text_template : txt.dlc_bus.bus_panel\n{\n" +
        "\tname: \"Bus generic destination panel\"\n" +
        "\ttext: \"<offset vshift=-12><align hstyle=center vstyle=center><font face=/font/normal_bold.font xscale=5 yscale=5.5 spacing=1><color value=FF03B0FA><nowrap>%0</nowrap></font></align>\"\n" +
        "}\n\n}\n";

    static string Text(BusDisplaySettings s)
    {
        var doc = SiiDocument.Parse(BusDisplay.Build(Encoding.UTF8.GetBytes(Stock), s));
        return doc.GetValue(doc.Units.Single(), "text")!;
    }

    [Fact]
    public void ParsesNamespacedUnitTypes()
    {
        var doc = SiiDocument.Parse(Stock);
        Assert.Equal("ui::text_template", doc.Units.Single().Type);
        Assert.Equal(BusDisplay.TemplateName, doc.Units.Single().Name);
    }

    [Fact]
    public void FixedTextReplacesTheGamesDestination()
    {
        var text = Text(new BusDisplaySettings { Enabled = true, Route = "E23", Text = "Airport" });
        Assert.Contains("<nowrap>E23  Airport</nowrap>", text);
        Assert.DoesNotContain("%0", text);
        Assert.Contains("xscale=5 yscale=5.5", text);
        Assert.Contains("color value=FF03B0FA", text);
    }

    [Fact]
    public void DestinationTokenKeepsTheJobsDestination()
    {
        var text = Text(new BusDisplaySettings { Enabled = true, Text = "Express to {destination}", Color = DisplayColor.White, Size = 0.8 });
        Assert.Contains("<nowrap>Express to %0</nowrap>", text);
        Assert.Contains("xscale=4 yscale=4.4", text);
        Assert.Contains("color value=" + BusDisplay.ColorValue(DisplayColor.White), text);
    }

    [Fact]
    public void MarkupAndQuotesAreDropped()
    {
        var text = Text(new BusDisplaySettings { Enabled = true, Text = "A<b>\"B\"\\%C" });
        Assert.Contains("<nowrap>AbBC</nowrap>", text);
    }

    [Fact]
    public void OffIsStockAndTooLongIsRefused()
    {
        var stock = Encoding.UTF8.GetBytes(Stock);
        Assert.Equal(stock, BusDisplay.Build(stock, new BusDisplaySettings()));
        Assert.Throws<FormatException>(() => BusDisplay.Content("", new string('x', BusDisplay.MaxLength + 1)));
    }

    [Fact]
    public void ListReadsRouteAndText()
    {
        var list = BusDisplay.ReadList(new StringReader("# comment\n\nE23 | Airport\nTung Chung\n 1 |  Central  \n")).ToList();
        Assert.Equal([("E23", "Airport"), ("", "Tung Chung"), ("1", "Central")], list);
        Assert.Equal("E23 | Airport", BusDisplay.ListLine("E23", "Airport"));
    }

    [Fact]
    public void DisplayModHasItsPicture() => Assert.NotNull(ScsModBuilder.Icon(BusDisplay.Mod));
}

public class LiveryPackTests
{
    static GamePaths Paths(string docs) => new(null, docs, null, [], "1.62.3.3s");

    static string Zip(string dir, string name, Dictionary<string, string> files)
    {
        var path = Path.Combine(dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (p, text) in files)
        {
            using var s = zip.CreateEntry(p).Open();
            s.Write(Encoding.UTF8.GetBytes(text));
        }
        return path;
    }

    static string Folder(string dir, string name, Dictionary<string, string> files)
    {
        var root = Path.Combine(dir, name);
        foreach (var (p, text) in files)
        {
            var full = Path.Combine(root, p);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        return root;
    }

    [Theory]
    [InlineData("def/vehicle/bus/man.lions_24/paint_job/my_livery.sii", true)]
    [InlineData("def/vehicle/truck/scania.s_2016/paint_job/x.sii", true)]
    [InlineData("def/vehicle/bus/man.lions_24/paint_job/accessory/my_livery.sii", false)]
    [InlineData("vehicle/bus/upgrade/paintjob/man.lions_24/x.dds", false)]
    [InlineData("def/bus_economy_data.sii", false)]
    public void SpotsPaintJobs(string path, bool expected) => Assert.Equal(expected, LiveryPack.IsPaintJob(path));

    [Fact]
    public void MergesSourcesAndLeavesTheirMetadataOut()
    {
        using var dir = new TempDir();
        var a = Zip(dir.Path, "a.scs", new()
        {
            ["manifest.sii"] = "A", ["mod_icon.jpg"] = "A",
            ["def/vehicle/bus/man.lions_24/paint_job/a.sii"] = "a", ["skins/a.dds"] = "a",
            ["material/ui/shared.mat"] = "same",
        });
        var b = Folder(dir.Path, "b", new()
        {
            ["manifest.sii"] = "B",
            ["def/vehicle/bus/man.lions_24/paint_job/b.sii"] = "b",
            ["material/ui/shared.mat"] = "same",
            ["skins/a.dds"] = "different",
        });
        var sources = new List<LiverySource>
        {
            new() { Kind = LiverySourceKind.Local, Path = a, Name = "A", Author = "Ann" },
            new() { Kind = LiverySourceKind.Local, Path = b, Name = "B" },
        };
        var paths = Paths(dir.Path);
        var plan = LiveryPack.Plan(sources, paths);

        Assert.Equal(2, plan.PaintJobs);
        Assert.Equal(["def/vehicle/bus/man.lions_24/paint_job/a.sii", "def/vehicle/bus/man.lions_24/paint_job/b.sii",
                      "material/ui/shared.mat", "skins/a.dds"], plan.Files.Keys.Order().ToList());
        Assert.Single(plan.Conflicts); // shared.mat is identical, so only a.dds clashes
        Assert.Contains("skins/a.dds", plan.Conflicts[0]);
        Assert.Equal("A", plan.Files["skins/a.dds"].Name);

        using var ms = new MemoryStream();
        LiveryPack.Write(ms, LiveryPack.Mod.ForPlayer("abc123"), plan, paths);
        var scs = ms.ToArray();
        Assert.Equal("a", Encoding.UTF8.GetString(ScsModBuilder.Read(scs, "skins/a.dds")!));
        var manifest = Encoding.UTF8.GetString(ScsModBuilder.Read(scs, "manifest.sii")!);
        Assert.Contains("mp_mod_optional: true", manifest);
        Assert.Contains("category[]: \"paint_job\"", manifest);
        Assert.Contains("A by Ann", Encoding.UTF8.GetString(ScsModBuilder.Read(scs, "description.txt")!));
        Assert.NotNull(ScsModBuilder.Read(scs, ScsModBuilder.IconFile));
        Assert.Equal("ets2u_liveries_abc123.scs", LiveryPack.Mod.ForPlayer("abc123").FileName);
    }

    [Fact]
    public void FingerprintChangesWhenASourceChanges()
    {
        using var dir = new TempDir();
        var b = Folder(dir.Path, "b", new() { ["def/vehicle/bus/x/paint_job/b.sii"] = "b" });
        var sources = new List<LiverySource> { new() { Kind = LiverySourceKind.Local, Path = b, Name = "B" } };
        var paths = Paths(dir.Path);
        var before = LiveryPack.Fingerprint(sources, paths);
        Assert.Equal(before, LiveryPack.Fingerprint(sources, paths));

        File.WriteAllText(Path.Combine(b, "def/vehicle/bus/x/paint_job/b.sii"), "b, version 2");
        var after = LiveryPack.Fingerprint(sources, paths);
        Assert.NotEqual(before, after);

        sources[0].Included = false;
        Assert.NotEqual(after, LiveryPack.Fingerprint(sources, paths));
    }

    [Fact]
    public void MissingSourceIsReportedNotPacked()
    {
        using var dir = new TempDir();
        var sources = new List<LiverySource> { new() { Kind = LiverySourceKind.Local, Path = Path.Combine(dir.Path, "gone.scs"), Name = "Gone" } };
        var plan = LiveryPack.Plan(sources, Paths(dir.Path));
        Assert.Empty(plan.Files);
        Assert.NotNull(plan.Sources.Single().Problem);
        Assert.Single(LiveryPack.Missing(sources, Paths(dir.Path)));
    }

    [Fact]
    public void PicksTheWorkshopPackageForThisGameVersion()
    {
        using var dir = new TempDir();
        Folder(dir.Path, "universal", new() { ["manifest.sii"] = "x" });
        Folder(dir.Path, "old", new() { ["manifest.sii"] = "x" });
        Folder(dir.Path, "new", new() { ["manifest.sii"] = "x" });
        File.WriteAllText(Path.Combine(dir.Path, "versions.sii"),
            "SiiNunit\n{\npackage_version_info : .info {\n\tpackage_name: \"universal\"\n\tinformational: true\n}\n" +
            "package_version_info : .old\n{\n\tpackage_name: \"old\"\n\tcompatible_versions[]: \"1.61.*\"\n}\n" +
            "package_version_info : .new\n{\n\tpackage_name: \"new\"\n\tcompatible_versions[]: \"1.62.*\"\n}\n}\n");
        Assert.Equal(Path.Combine(dir.Path, "new"), Workshop.PickPackage(dir.Path, "1.62.3.3s"));
        Assert.Equal(Path.Combine(dir.Path, "old"), Workshop.PickPackage(dir.Path, "1.61.1.0s"));
        Assert.Null(Workshop.PickPackage(dir.Path, "1.63.0.1s")); // informational packages never load
    }

    [Theory]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=3839519582", "3839519582")]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?l=english&id=3839519582&searchtext=", "3839519582")]
    [InlineData(" 3839519582 ", "3839519582")]
    [InlineData("not a link", null)]
    public void ParsesWorkshopLinks(string input, string? id) => Assert.Equal(id, Workshop.ParseId(input));

    [Theory]
    [InlineData("[b]Sun Bus livery[/b]\n", "Sun Bus livery")]
    [InlineData("Vehicles supported:\n- MAN\n", "- MAN")]
    [InlineData("=====\n[color=#fff]Flixbus[/color]", "Flixbus")]
    public void TitleFromDescription(string description, string title) => Assert.Equal(title, Workshop.TitleLine(description));
}
