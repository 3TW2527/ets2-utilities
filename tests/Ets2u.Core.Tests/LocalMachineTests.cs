using System.Diagnostics;
using Ets2u.Core.Extract;
using Ets2u.Core.Features;
using Ets2u.Core.Paths;
using Ets2u.Core.Safety;
using Ets2u.Core.Save;
using Ets2u.Core.Sii;
using Ets2u.Core.Telemetry;
using Xunit.Abstractions;

namespace Ets2u.Core.Tests;

/// <summary>
/// Read-only checks against this PC's real game data. Run with ETS2U_LOCAL=1.
/// They never write to the game, the saves or the mod folder.
/// </summary>
public class LocalMachineTests(ITestOutputHelper output)
{
    static bool Enabled => Environment.GetEnvironmentVariable("ETS2U_LOCAL") == "1";

    [Fact]
    public void DetectsPathsProfilesAndDecodesSaves()
    {
        if (!Enabled) return;
        var paths = GamePaths.Detect();
        output.WriteLine($"install={paths.InstallDir} version={paths.GameVersion}");
        Assert.NotNull(paths.InstallDir);

        foreach (var profile in ProfileLocator.Find(paths))
        {
            var newest = profile.Saves().First();
            var game = SiiCodec.Decode(File.ReadAllBytes(newest.GamePath));
            var summary = SaveEditor.ReadSummary(newest);
            output.WriteLine($"{profile}: {newest.Name} game={SiiCodec.Detect(game)} {game.Length} bytes, " +
                             $"money={summary?.Money} xp={summary?.TruckXp}");
            Assert.NotEqual(SiiFormat.Unknown, SiiCodec.Detect(game));
        }
    }

    /// <summary>
    /// Every binary save on this PC: read money and XP, then edit a temp copy and check that only
    /// those bytes changed and the copy reads back.
    /// </summary>
    [Fact]
    public void EditsEveryRealBinarySaveInPlace()
    {
        if (!Enabled) return;
        var checkedSaves = 0;
        foreach (var profile in ProfileLocator.Find(GamePaths.Detect()))
        foreach (var slot in profile.Saves())
        {
            var state = SaveEditor.Read(slot);
            if (state.Format != SiiFormat.Binary) continue;
            Assert.True(state.Editable, $"{profile.Name}/{slot.Name}: {state.Problem}");
            // info.sii can legitimately disagree with game.sii after edits and play, so it's reported, not asserted.
            var summary = SaveEditor.ReadSummary(slot);
            if (summary is not null && (summary.Money != state.Values!.Money || summary.TruckXp != state.Values.TruckXp))
                output.WriteLine($"  note: {profile.Name}/{slot.Name} summary differs from the save (money {summary.Money} vs {state.Values!.Money})");

            using var dir = new TempDir();
            var copyDir = Path.Combine(dir.Path, profile.FolderName);
            var saveDir = Path.Combine(copyDir, "save", slot.Name);
            Directory.CreateDirectory(saveDir);
            File.Copy(slot.GamePath, Path.Combine(saveDir, "game.sii"));
            File.Copy(slot.InfoPath, Path.Combine(saveDir, "info.sii"));
            File.Copy(profile.ProfileSiiPath, Path.Combine(copyDir, "profile.sii"));
            var copy = new Profile(profile.FolderName, profile.Name, copyDir, profile.Storage);
            var copySlot = copy.Saves().Single();

            var target = new SaveValues(1_234_567, state.Values!.TruckXp + 1000, (state.Values.CoachXp ?? 0) + 500);
            SaveEditor.Write(copy, copySlot, target, new SafeWriter(new BackupService(Path.Combine(dir.Path, "b")), () => false));

            var before = SiiCodec.Decode(File.ReadAllBytes(slot.GamePath));
            var after = File.ReadAllBytes(copySlot.GamePath);
            Assert.Equal(before.Length, after.Length);
            Assert.InRange(Enumerable.Range(0, before.Length).Count(i => before[i] != after[i]), 1, 16);
            Assert.Equal(target, SaveEditor.Read(copySlot).Values);
            checkedSaves++;
        }
        output.WriteLine($"binary saves read, edited and re-read: {checkedSaves}");
        Assert.True(checkedSaves > 0);
    }

    [Fact]
    public void BuildsEveryModStraightFromTheGameArchives()
    {
        var paths = GamePaths.Detect();
        if (!Enabled || paths.InstallDir is null) return;

        var timer = Stopwatch.StartNew();
        using var game = new GameArchives(paths.InstallDir);

        var zeroed = SiiDocument.Parse(NoDamage.Build(game.Read(NoDamage.GamePath), true));
        var unit = zeroed.FindUnit("vehicle_damage_data")!;
        var damageKeys = unit.Keys.Where(NoDamage.IsDamageKey).ToList();
        output.WriteLine($"damage keys zeroed: {damageKeys.Count} of {unit.Keys.Count()}");
        Assert.Equal(unit.Keys.Count(), damageKeys.Count); // 1.62's file holds only damage and wear values
        Assert.All(damageKeys, k => Assert.Equal("0.0", zeroed.GetValue(unit, k)));

        var truck = LevelTable.Parse(game.Read(LevelTable.TruckPath));
        var coach = LevelTable.Parse(game.Read(LevelTable.CoachPath));
        Assert.Equal(235_700, truck.XpForLevel(50));   // matches Save-Edit-Tool's level table
        Assert.Equal(575_700, coach.XpForLevel(100));

        var stockBus = SiiDocument.Parse(game.Read(CoachComfort.GamePath));
        Assert.Empty(CoachComfort.UnmappedPenalties(stockBus, stockBus.FindUnit("bus_economy_data")!)); // every penalty has a tick box
        var comfort = SiiDocument.Parse(CoachComfort.Build(game.Read(CoachComfort.GamePath), CoachFactor.All, true));
        var bus = comfort.FindUnit("bus_economy_data")!;
        foreach (var key in bus.AllKeys.Where(CoachComfort.IsPenaltyKey).Distinct())
            output.WriteLine($"  {key}: {string.Join(" | ", comfort.GetValues(bus, key))}");
        output.WriteLine($"  {CoachComfort.FailureLimitKey}: {comfort.GetValue(bus, CoachComfort.FailureLimitKey)}");
        output.WriteLine($"  {CoachComfort.NewPassengerKey}: {comfort.GetValue(bus, CoachComfort.NewPassengerKey)}");

        var all = GeneratedMods.Build(game, new AppSettings
        {
            NoDamage = true, IncomeMultiplier = 5, XpMultiplier = 5, MapPositionEnabled = true,
            CoachIgnored = CoachFactor.Lateness, CoachFullSatisfaction = true,
        }, _ => false);
        output.WriteLine($"all mods built in {timer.ElapsedMilliseconds} ms: " +
                         string.Join(", ", all.Select(m => $"{m.Key} {m.Value.Length} bytes")));
        Assert.Equal(4, all.Count);
    }

    [Fact]
    public void ReaderSeesWhatThePluginPublishes()
    {
        var host = AppSettings.FindNearApp("plugin", "bin", "test_host.exe");
        var dll = AppSettings.FindNearApp("plugin", "bin", PluginInstaller.FileName);
        if (!Enabled || host is null || dll is null) return;

        using var process = Process.Start(new ProcessStartInfo(host)
        {
            ArgumentList = { dll, "4000" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        while (process.StandardOutput.ReadLine() is { } line && !line.StartsWith("holding")) { }

        using var reader = new TelemetryReader();
        var frame = reader.Read();
        Assert.NotNull(frame);
        output.WriteLine($"plugin -> app: {SpeedText.Format(frame.Value.SpeedMs)}; {SpeedText.FormatLimit(frame.Value.SpeedLimitMs)}");
        Assert.Equal("90 km/h · 56 mph", SpeedText.Format(frame.Value.SpeedMs));
        Assert.Equal("Limit 80 km/h · 50 mph", SpeedText.FormatLimit(frame.Value.SpeedLimitMs));
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
