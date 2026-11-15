using System.IO;
using Ets2u.Core;
using Ets2u.Core.Extract;
using Ets2u.Core.Features;
using Ets2u.Core.Info;
using Ets2u.Core.Liveries;
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

    // ---------- Livery pack ----------

    public ModSpec LiveryMod => Mine(LiveryPack.Mod);

    public string LiveryPackPath => Path.Combine(Paths.ModDir, LiveryMod.FileName);

    public bool LiveryPackExists => File.Exists(LiveryPackPath);

    /// <summary>The pack is on, has sources, and they changed since it was built (or it was never built).</summary>
    public bool LiveryPackOutdated(out IReadOnlyList<(LiverySource Source, string Problem)> missing)
    {
        var s = Settings.LiveryPack;
        missing = [];
        if (!s.Enabled || !s.Sources.Any(x => x.Included)) return false;
        missing = LiveryPack.Missing(s.Sources, Paths);
        return !LiveryPackExists || LiveryPack.Fingerprint(s.Sources, Paths) != s.BuiltFingerprint;
    }

    /// <summary>
    /// Builds the pack from the included sources, or removes it when switched off. The pack is written
    /// beside its final name and then swapped in. It isn't backed up: it's made entirely from the source
    /// mods, which stay where they are, and copies of a pack of big textures would fill the backups.
    /// </summary>
    public string BuildLiveryPack(Action<int, int>? progress = null)
    {
        if (GameRunning) throw new GameRunningException();
        var s = Settings.LiveryPack;
        var path = LiveryPackPath;
        if (!s.Enabled || !s.Sources.Any(x => x.Included))
        {
            if (!File.Exists(path)) return "The livery pack is off.";
            File.Delete(path);
            s.BuiltFingerprint = null;
            Settings.Save();
            Changed?.Invoke();
            return $"Removed {Path.GetFileName(path)}.";
        }
        if (s.Sources.Any(x => x.Included && x.Kind == LiverySourceKind.Workshop) && !s.WorkshopPermissionConfirmed)
            throw new InvalidOperationException("Confirm that the creators of the Workshop items allow editing and redistribution before building.");

        var fingerprint = LiveryPack.Fingerprint(s.Sources, Paths);
        // Its own reader: this runs off the UI thread, which may be reading the game's files too.
        using var game = Paths.InstallDir is null ? null : new GameArchives(Paths.InstallDir);
        var plan = LiveryPack.Plan(s.Sources, Paths, game);
        if (plan.Files.Count == 0)
            throw new InvalidOperationException("None of the included mods could be read: " +
                                                string.Join("; ", plan.Sources.Select(r => $"{r.Source.Name}: {r.Problem}")));

        Directory.CreateDirectory(Paths.ModDir);
        var temp = path + ".ets2u-tmp";
        try
        {
            using (var file = File.Create(temp))
                LiveryPack.Write(file, LiveryMod, plan, Paths, progress);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        s.BuiltFingerprint = fingerprint;
        Settings.Save();
        Changed?.Invoke();

        var used = plan.Sources.Count(r => r.Problem is null);
        var message = $"Built {Path.GetFileName(path)}: {plan.PaintJobs} paint job(s) from {used} mod(s), " +
                      $"{new FileInfo(path).Length / 1048576.0:0} MB.";
        var skipped = plan.Sources.Where(r => r.Problem is not null).ToList();
        if (skipped.Count > 0) message += " Left out: " + string.Join("; ", skipped.Select(r => $"{r.Source.Name} ({r.Problem})")) + ".";
        if (plan.Conflicts.Count > 0) message += $" {plan.Conflicts.Count} file(s) clash between mods; the one higher in the list is used.";
        return message + " Restart ETS2 to apply.";
    }

    /// <summary>
    /// On start: rebuild the pack when a source changed (Steam updated a Workshop item, a local mod was
    /// replaced). A source that's gone stops the rebuild, so a pack never silently loses liveries.
    /// </summary>
    public string? RebuildLiveryPackIfChanged(Action<int, int>? progress = null)
    {
        if (!LiveryPackOutdated(out var missing)) return null;
        if (missing.Count > 0)
            return "Livery pack: can't update, " + string.Join("; ", missing.Select(m => $"{m.Source.Name}: {m.Problem}")) +
                   ". Fix or untick it on the Livery pack page.";
        if (GameRunning) return "A livery mod in your pack was updated. Close the game and reopen this app to update the pack.";
        return "A livery mod in your pack changed. " + BuildLiveryPack(progress);
    }

    /// <summary>Profiles whose controls.sii is set up for mouse driving, and those that aren't.</summary>
    public (int Ready, int Total) MouseControlsState()
    {
        int ready = 0, total = 0;
        foreach (var path in ControlsFiles())
        {
            total++;
            if (Core.Telemetry.MouseDriveControls.IsApplied(ReadControls(path))) ready++;
        }
        return (ready, total);
    }

    /// <summary>
    /// Every profile's controls.sii. A Steam Cloud profile's saves live in Steam's userdata folder,
    /// but its controls stay on this PC: Documents\Euro Truck Simulator 2\steam_profiles\&lt;id&gt;.
    /// </summary>
    IEnumerable<string> ControlsFiles()
    {
        var dirs = Profiles.SelectMany(p => new[]
            {
                p.Dir,
                Path.Combine(Paths.SteamProfilesDir, p.FolderName),
                Path.Combine(Paths.LocalProfilesDir, p.FolderName),
            })
            .Concat(Directory.Exists(Paths.SteamProfilesDir) ? Directory.EnumerateDirectories(Paths.SteamProfilesDir) : [])
            .Concat(Directory.Exists(Paths.LocalProfilesDir) ? Directory.EnumerateDirectories(Paths.LocalProfilesDir) : []);
        return dirs.Select(d => Path.GetFullPath(Path.Combine(d, Core.Telemetry.MouseDriveControls.FileName)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToList();
    }

    /// <summary>Mouse driving is on but a profile's controls aren't set up for it (the game can reset them).</summary>
    public bool MouseControlsNeeded => Settings.MouseDrive && MouseControlsState() is var (ready, total) && ready < total;

    static string ReadControls(string path) =>
        System.Text.Encoding.UTF8.GetString(Core.Save.SiiCodec.Decode(File.ReadAllBytes(path)));

    /// <summary>
    /// Sets every profile's camera up to hold still while the mouse steers (and puts back what an
    /// earlier version changed), or puts each profile's own values back.
    /// </summary>
    public string SetMouseControls(bool on)
    {
        var files = new Dictionary<string, byte[]>();
        foreach (var path in ControlsFiles())
        {
            var text = ReadControls(path);
            string changed;
            if (on)
            {
                if (Core.Telemetry.MouseDriveControls.IsApplied(text)) continue;
                var recorded = Settings.MouseControlsOriginals.GetValueOrDefault(path);
                // The profile's own values: the file with anything an earlier version changed put back.
                if (Core.Telemetry.MouseDriveControls.Read(Core.Telemetry.MouseDriveControls.Restore(text, recorded)) is { } own)
                    Settings.MouseControlsOriginals[path] = own;
                changed = Core.Telemetry.MouseDriveControls.Apply(text, recorded);
            }
            else
            {
                if (!Core.Telemetry.MouseDriveControls.IsApplied(text)) continue;
                Settings.MouseControlsOriginals.Remove(path, out var original);
                changed = Core.Telemetry.MouseDriveControls.Restore(text, original);
            }
            if (changed != text) files[path] = System.Text.Encoding.UTF8.GetBytes(changed);
        }
        if (files.Count == 0) return "";
        Writer.Write(on ? "Set controls up for mouse driving" : "Put controls back after mouse driving", files);
        Settings.Save();
        return on
            ? $"Set up the camera of {files.Count} profile(s) to hold still while the mouse steers (it looks round as usual otherwise). "
            : $"Put back the camera settings of {files.Count} profile(s). ";
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
