using System.Diagnostics;
using Ets2u.Core.Extract;
using Ets2u.Core.Paths;
using Xunit.Abstractions;

namespace Ets2u.Core.Tests;

public class CityHashTests
{
    [Fact]
    public void EmptyStringIsK2() => Assert.Equal(0x9ae16a3b2f90404fUL, CityHash.Hash64(""));
}

/// <summary>
/// Compares the native archive reader with files SCS's own extractor produced earlier on this PC
/// (kept in %LOCALAPPDATA%\ets2u\game\&lt;version&gt;\&lt;archive&gt;). Each test covers a different
/// CityHash length branch through its path. Needs ETS2U_LOCAL=1; skips if those files are gone.
/// </summary>
public class HashFsLocalTests(ITestOutputHelper output)
{
    static bool Enabled => Environment.GetEnvironmentVariable("ETS2U_LOCAL") == "1";

    [Theory]
    [InlineData("def", "def/damage_data.sii")]
    [InlineData("def", "def/economy_data.sii")]
    [InlineData("def", "def/economy_data_xp.sii")]
    [InlineData("def", "def/bus_economy_data_xp.sii")]
    [InlineData("def", "def/bus_economy_data.sii")]
    [InlineData("base_share", "ui/adviser.sii")]
    [InlineData("dlc_iveco_sway", "ui/dashboard/iveco_sway_ic10_gps_mph.sii")]
    public void ReadsTheSameBytesAsTheOfficialExtractor(string archive, string gamePath)
    {
        var paths = GamePaths.Detect();
        if (!Enabled || paths.InstallDir is null) return;
        var extracted = Path.Combine(AppDirs.GameCache, paths.GameVersion!, archive, gamePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(extracted)) return;

        var timer = Stopwatch.StartNew();
        using var scs = new HashFsArchive(Path.Combine(paths.InstallDir, archive + ".scs"));
        var bytes = scs.Read(gamePath);
        output.WriteLine($"{archive}.scs: {scs.EntryCount} entries, {gamePath} ({bytes?.Length} bytes) in {timer.ElapsedMilliseconds} ms");
        Assert.Equal(File.ReadAllBytes(extracted), bytes);
    }
}
