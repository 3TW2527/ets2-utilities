using System.IO;
using Ets2u.Core;
using Ets2u.Core.Extract;
using Ets2u.Core.Features;
using Ets2u.Core.Info;
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
        if (string.IsNullOrEmpty(Settings.ModTag))
        {
            Settings.ModTag = ModTag.Create(Paths.CloudProfileRoots);
            Settings.Save();
        }
    }

    public bool GameRunning => SafeWriter.IsGameRunning();

    static string AppVersion => typeof(AppServices).Assembly.GetName().Version?.ToString() ?? "";

    public void Refresh()
    {
        Paths = GamePaths.Detect();
        Profiles = ProfileLocator.Find(Paths);
        (Game as IDisposable)?.Dispose();
        Game = Paths.InstallDir is null ? null : new GameArchives(Paths.InstallDir);
        _levelTables.Clear();
        _infoLoaded = false;
        Changed?.Invoke();
    }

    bool _infoLoaded;
    GameInfo? _info;

    /// <summary>Why <see cref="Info"/> is null although the game was found.</summary>
    public string? InfoProblem { get; private set; }

    /// <summary>The installed game's countries and cities, read once per refresh; null if they can't be read.</summary>
    public GameInfo? Info
    {
        get
        {
            if (_infoLoaded) return _info;
            _infoLoaded = true;
            _info = null;
            InfoProblem = null;
            if (Paths.InstallDir is null) return null;
            try { _info = GameInfo.FromInstall(Paths.InstallDir); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                InfoProblem = "Couldn't read the game's archives: " + ex.Message;
                AppDirs.Log(InfoProblem);
            }
            return _info;
        }
    }

    public void Status(string message)
    {
        AppDirs.Log(message);
        StatusChanged?.Invoke(message);
    }

    /// <summary>The mod as this player's file is named.</summary>
    public ModSpec Mine(ModSpec mod) => GeneratedMods.ForPlayer(mod, Settings);

    /// <summary>Whether the mod was ever created here, under this player's name or an older one.</summary>
    public bool ModExists(ModSpec mod) =>
        File.Exists(Path.Combine(Paths.ModDir, Mine(mod).FileName)) || OldModFiles(mod).Any();

    /// <summary>
    /// Copies of the mod under other names: the untagged name v0.4 used, or another tag (settings reset).
    /// Left in place they'd load next to the current copy.
    /// </summary>
    IEnumerable<string> OldModFiles(ModSpec mod)
    {
        if (!Directory.Exists(Paths.ModDir)) return [];
        var current = Mine(mod).FileName;
        return Directory.EnumerateFiles(Paths.ModDir, $"{mod.Id}*.scs")
            .Where(f => Path.GetFileName(f) is var name && name != current
                        && (name == mod.LegacyFileName || System.Text.RegularExpressions.Regex.IsMatch(name, $@"^{mod.Id}_[0-9a-f]{{6}}\.scs$")))
            .ToList();
    }

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

        var old = GeneratedMods.All.SelectMany(OldModFiles).ToList();

        Settings.ModsBuiltForGameStamp = Paths.VersionStamp;
        Settings.ModsBuiltByApp = AppVersion;
        Settings.Save();
        if (changed.Count == 0 && old.Count == 0) return "No changes.";

        Writer.Write("Rebuild mods: " + string.Join(", ", changed.Keys.Select(Path.GetFileName)), changed, old);
        Changed?.Invoke();
        var message = changed.Count == 0 ? "" : $"Updated {string.Join(", ", changed.Keys.Select(Path.GetFileName))}. ";
        if (old.Count > 0)
            message += $"Removed the old {string.Join(", ", old.Select(Path.GetFileName))}: the mods have new file names, " +
                       "so enable them again once in Mod Manager. ";
        return message + "Restart ETS2 to apply.";
    }

    /// <summary>
    /// After a game update the mods still hold the old version's files. Rebuild them from the new
    /// files on start, when there are mods to rebuild and the game isn't running. The same after an
    /// app update (new mod contents, e.g. pictures), and for mods still under an old file name
    /// (v0.4 and earlier), which move to this player's names.
    /// </summary>
    public string? RebuildIfGameUpdated()
    {
        if (Paths.VersionStamp is null) return null;
        var gameUpdated = Settings.ModsBuiltForGameStamp != Paths.VersionStamp;
        var renamed = GeneratedMods.All.Any(m => OldModFiles(m).Any());
        var appUpdated = Settings.ModsBuiltByApp != AppVersion;
        if (!gameUpdated && !renamed && !appUpdated) return null;
        if (!GeneratedMods.All.Any(ModExists))
        {
            Settings.ModsBuiltForGameStamp = Paths.VersionStamp;
            Settings.ModsBuiltByApp = AppVersion;
            Settings.Save();
            return null;
        }
        if (GameRunning)
            return gameUpdated
                ? "ETS2 was updated. Close the game and reopen this app to update the mods."
                : "This version of the app updates your mods. Close the game and reopen this app to update them.";
        return (gameUpdated ? $"ETS2 was updated to {Paths.GameVersion}. " : "") + ApplyMods();
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
