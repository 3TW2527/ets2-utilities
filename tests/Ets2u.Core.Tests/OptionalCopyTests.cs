using System.IO.Compression;
using System.Text;
using Ets2u.Core.Liveries;
using Ets2u.Core.Mods;
using Ets2u.Core.Paths;
using Ets2u.Core.Sii;
using Xunit.Abstractions;

namespace Ets2u.Core.Tests;

public class OptionalCopyTests(ITestOutputHelper output)
{
    // As the Volvo D8K-350 engine mod writes it: no display name, unit brace on the same line.
    const string NoName =
        "SiiNunit\n{\nmod_package : .d8k\n{\n\tpackage_version: \"1.5\"\n\tauthor: \"someone\"\n" +
        "\tcategory[]: \"tuning_parts\"\n\ticon: \"icon.jpg\"\n\tdescription_file: \"info.txt\"\n}\n}\n";

    static SiiUnit Unit(string manifest, out SiiDocument doc)
    {
        doc = SiiDocument.Parse(manifest);
        return doc.FindUnit("mod_package")!;
    }

    [Fact]
    public void AddsOptionalAndANameWhenThereIsNone()
    {
        var patched = OptionalCopy.PatchManifest(NoName, "Volvo D8K");
        var unit = Unit(patched, out var doc);
        Assert.Equal("true", doc.GetValue(unit, "mp_mod_optional"));
        Assert.Equal("\"Volvo D8K (optional)\"", doc.GetValue(unit, "display_name"));
        Assert.Equal("\"icon.jpg\"", doc.GetValue(unit, "icon"));
        Assert.Equal("\"1.5\"", doc.GetValue(unit, "package_version"));
        Assert.True(OptionalCopy.IsOptional(patched));
        Assert.False(OptionalCopy.IsOptional(NoName));
    }

    [Fact]
    public void ReplacesFalseKeepsTheNameAndIgnoresComments()
    {
        const string manifest =
            "\uFEFFSiiNunit\r\n{\r\nmod_package : .package_name\r\n{\r\n\t#display_name: \"Commented\"\r\n" +
            "\tdisplay_name: \"Coach tires\"   # trailing\r\n\tmp_mod_optional: false\r\n" +
            "\tcompatible_versions[]: \"1.62.*\"\r\n}\r\n}\r\n";
        var patched = OptionalCopy.PatchManifest(manifest, "fallback");
        Assert.Equal(1, patched.Split('\n').Count(l => l.Contains("mp_mod_optional")));
        var unit = Unit(patched, out var doc);
        Assert.Equal("true", doc.GetValue(unit, "mp_mod_optional"));
        Assert.Equal("\"Coach tires (optional)\"", doc.GetValue(unit, "display_name"));
        Assert.Contains("#display_name: \"Commented\"", patched);
        Assert.DoesNotContain("fallback", patched);
    }

    [Fact]
    public void PatchingTwiceChangesNothingMore()
    {
        var once = OptionalCopy.PatchManifest(NoName, "Mod");
        Assert.Equal(once, OptionalCopy.PatchManifest(once, "Mod"));
    }

    [Fact]
    public void HandlesTheBraceOnTheHeaderLine()
    {
        const string manifest = "SiiNunit\n{\nmod_package : .m {\n\tauthor: \"a\"\n}\n}\n";
        var unit = Unit(OptionalCopy.PatchManifest(manifest, "M"), out var doc);
        Assert.Equal("true", doc.GetValue(unit, "mp_mod_optional"));
    }

    [Fact]
    public void RejectsAManifestWithoutAModPackage() =>
        Assert.Throws<InvalidDataException>(() => OptionalCopy.PatchManifest("SiiNunit\n{\n}\n", "M"));

    [Fact]
    public void CopiesEveryFileAndPatchesTheManifest()
    {
        var dir = Directory.CreateTempSubdirectory("ets2u_opt_test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "manifest.sii"), NoName);
            File.WriteAllText(Path.Combine(dir, "info.txt"), "Volvo D8K-350 engine");
            Directory.CreateDirectory(Path.Combine(dir, "def", "vehicle"));
            File.WriteAllText(Path.Combine(dir, "def", "vehicle", "engine.sii"), "SiiNunit\n{\n}\n");

            using var ms = new MemoryStream();
            Assert.False(OptionalCopy.Write(ms, dir, "Volvo D8K"));
            ms.Position = 0;
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            Assert.Equal(["def/vehicle/engine.sii", "info.txt", "manifest.sii"], zip.Entries.Select(e => e.FullName).Order());
            using var reader = new StreamReader(zip.GetEntry("manifest.sii")!.Open());
            Assert.True(OptionalCopy.IsOptional(reader.ReadToEnd()));
            using var info = new StreamReader(zip.GetEntry("info.txt")!.Open());
            Assert.Equal("Volvo D8K-350 engine", info.ReadToEnd());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void NamesTheCopyAfterTheOriginal()
    {
        var workshop = new LiverySource { Kind = LiverySourceKind.Workshop, WorkshopId = "3839178263", Name = "D8K" };
        Assert.Equal("ets2u_opt_3839178263_ab12cd.scs", OptionalCopy.Spec(workshop, "ab12cd").FileName);
        var local = new LiverySource { Kind = LiverySourceKind.Local, Path = @"C:\mods\My Engine Pack v2.scs", Name = "x" };
        Assert.Equal("my_engine_pack_v2", OptionalCopy.Slug(local));
        Assert.Equal("mod_workshop_package.00000000E4D53617", OptionalCopy.WorkshopPackageName("3839178263"));
        Assert.True(OptionalCopy.IsAppMod(@"C:\x\ets2u_liveries_ab12cd.scs"));
        Assert.False(OptionalCopy.IsAppMod(@"C:\x\prestal.zip"));
    }

    /// <summary>The two mods this was made for, if they're subscribed on this PC (run with ETS2U_LOCAL=1). Writes only to memory.</summary>
    [Fact]
    public void MakesTheSubscribedPartsModsOptional()
    {
        if (Environment.GetEnvironmentVariable("ETS2U_LOCAL") != "1") return;
        var paths = GamePaths.Detect();
        foreach (var id in new[] { "3838579257", "3839178263" })
        {
            var dir = Workshop.FindItemDir(paths, id);
            if (dir is null) continue;
            var package = Workshop.PickPackage(dir, paths.GameVersion)!;
            var (name, _) = Workshop.ReadManifest(package);
            using var ms = new MemoryStream();
            Assert.False(OptionalCopy.Write(ms, package, name ?? id));
            ms.Position = 0;
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            using var reader = new StreamReader(zip.GetEntry("manifest.sii")!.Open(), Encoding.UTF8);
            var manifest = reader.ReadToEnd();
            output.WriteLine($"{id}: {zip.Entries.Count} files, {ms.Length / 1024} KB\n{manifest}");
            Assert.True(OptionalCopy.IsOptional(manifest));
            using var content = ModContent.Load(package);
            Assert.Equal(content.Files.Count, zip.Entries.Count);
            var active = OptionalCopy.OriginalActiveIn(ProfileLocator.Find(paths),
                new LiverySource { Kind = LiverySourceKind.Workshop, WorkshopId = id }, paths);
            output.WriteLine($"  original active in: {string.Join(", ", active)}");
        }
    }
}
