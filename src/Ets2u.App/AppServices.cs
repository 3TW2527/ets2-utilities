using System.IO;
using Ets2u.Core;
using Ets2u.Core.Extract;
using Ets2u.Core.Features;
using Ets2u.Core.Mods;
using Ets2u.Core.Paths;
using Ets2u.Core.Safety;
using Ets2u.Core.Save;

namespace Ets2u.App;

/// <summary>State shared by every tab: paths, settings, profiles, game files, and the one place files get written.</summary>
public sealed class AppServices
{
    public AppSettings Settings { get; } = AppSettings.Load();
    public BackupService Backups { get; } = new(AppDirs.Backups);
    public SafeWriter Writer { get; }
    public GamePaths Paths { get; private set; } = GamePaths.Detect();
    public IReadOnlyList<Profile> Profiles { get; private set; } = Array.Empty<Profile>();

    /// <summary>The game's own files, read straight from its archives (nothing extracted).</summary>
    public IGameFiles? Game { get; private set; }

    public event Action? Changed;
    public event Action<string>? StatusChanged;

    public AppServices()
    {
        Writer = new SafeWriter(Backups);
        Refresh();
    }

    public bool GameRunning => SafeWriter.IsGameRunning();

    public void Refresh()
    {
        Paths = GamePaths.Detect();
        Profiles = ProfileLocator.Find(Paths);
        (Game as IDisposable)?.Dispose();
        Game = Paths.InstallDir is null ? null : new GameArchives(Paths.InstallDir);
        _levelTables.Clear();
        Changed?.Invoke();
    }

    public void Status(string message)
    {
        AppDirs.Log(message);
        StatusChanged?.Invoke(message);
    }

    public bool ModExists(ModSpec mod) => File.Exists(Path.Combine(Paths.ModDir, mod.FileName));

    /// <summary>Saves settings, rebuilds every generated mod and writes the ones that changed.</summary>
    public string ApplyMods()
    {
        Settings.Save();
        var game = Game ?? throw new InvalidOperationException("ETS2 wasn't found, so its files can't be read.");

        var built = GeneratedMods.Build(game, Settings, ModExists);
        var changed = built
            .Where(m => !File.Exists(Path.Combine(Paths.ModDir, m.Key))
                        || !File.ReadAllBytes(Path.Combine(Paths.ModDir, m.Key)).SequenceEqual(m.Value))
            .ToDictionary(m => Path.Combine(Paths.ModDir, m.Key), m => m.Value);

        Settings.ModsBuiltForGameStamp = Paths.VersionStamp;
        Settings.Save();
        if (changed.Count == 0) return "No changes.";

        Writer.Write("Rebuild mods: " + string.Join(", ", changed.Keys.Select(Path.GetFileName)), changed);
        Changed?.Invoke();
        return $"Updated {string.Join(", ", changed.Keys.Select(Path.GetFileName))}. Restart ETS2 to apply.";
    }

    /// <summary>
    /// After a game update the mods still hold the old version's files. Rebuild them from the new
    /// files on start, when there are mods to rebuild and the game isn't running.
    /// </summary>
    public string? RebuildIfGameUpdated()
    {
        if (Paths.VersionStamp is null || Settings.ModsBuiltForGameStamp == Paths.VersionStamp) return null;
        if (!GeneratedMods.All.Any(ModExists))
        {
            Settings.ModsBuiltForGameStamp = Paths.VersionStamp;
            Settings.Save();
            return null;
        }
        if (GameRunning) return "ETS2 was updated. Close the game and reopen this app to update the mods.";
        return $"ETS2 was updated to {Paths.GameVersion}. " + ApplyMods();
    }

    readonly Dictionary<string, LevelTable?> _levelTables = new();

    public LevelTable? LevelTable(string gamePath)
    {
        if (!_levelTables.TryGetValue(gamePath, out var table))
        {
            table = Game?.TryRead(gamePath) is { } bytes ? Core.Save.LevelTable.Parse(bytes) : null;
            _levelTables[gamePath] = table;
        }
        return table;
    }
}
