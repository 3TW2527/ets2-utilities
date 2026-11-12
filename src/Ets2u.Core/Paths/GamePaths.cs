using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Ets2u.Core.Paths;

/// <summary>Where ETS2 and its user data live on this PC.</summary>
public sealed record GamePaths(
    string? InstallDir,
    string DocumentsDir,
    string? SteamDir,
    IReadOnlyList<string> CloudProfileRoots,
    string? GameVersion)
{
    public const string SteamAppId = "227300";

    public string ModDir => Path.Combine(DocumentsDir, "mod");
    public string ConfigPath => Path.Combine(DocumentsDir, "config.cfg");
    public string LocalProfilesDir => Path.Combine(DocumentsDir, "profiles");
    public string SteamProfilesDir => Path.Combine(DocumentsDir, "steam_profiles");
    public string GameLogPath => Path.Combine(DocumentsDir, "game.log.txt");

    /// <summary>Changes whenever the game is updated; used to invalidate the extracted-file cache.</summary>
    public DateTime? VersionStamp =>
        InstallDir is null ? null : File.GetLastWriteTimeUtc(Path.Combine(InstallDir, "version.scs"));

    public static GamePaths Detect()
    {
        var documents = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Euro Truck Simulator 2");
        var steam = FindSteamDir();
        var libraries = steam is null ? new List<string>() : ReadLibraryFolders(steam);
        var install = libraries
            .Select(lib => Path.Combine(lib, "steamapps", "common", "Euro Truck Simulator 2"))
            .FirstOrDefault(dir => File.Exists(Path.Combine(dir, "base.scs")));

        return new GamePaths(install, documents, steam, FindCloudProfileRoots(steam), ReadGameVersion(documents));
    }

    static string? FindSteamDir()
    {
        if (OperatingSystem.IsWindows()
            && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string reg
            && Directory.Exists(reg))
            return Path.GetFullPath(reg);
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(fallback) ? fallback : null;
    }

    /// <summary>Library roots listed in steamapps/libraryfolders.vdf, Steam's own folder first.</summary>
    public static List<string> ReadLibraryFolders(string steamDir)
    {
        var result = new List<string> { steamDir };
        var vdf = Path.Combine(steamDir, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return result;
        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
        {
            var path = m.Groups[1].Value.Replace(@"\\", @"\");
            if (!result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);
        }
        return result;
    }

    /// <summary>Steam Cloud profile folders: userdata/&lt;account&gt;/227300/remote/profiles.</summary>
    static List<string> FindCloudProfileRoots(string? steamDir)
    {
        var userdata = steamDir is null ? null : Path.Combine(steamDir, "userdata");
        if (userdata is null || !Directory.Exists(userdata)) return new();
        return Directory.EnumerateDirectories(userdata)
            .Select(account => Path.Combine(account, SteamAppId, "remote", "profiles"))
            .Where(Directory.Exists)
            .ToList();
    }

    /// <summary>
    /// The game writes "steam_profiles(1.62.3.2s).bak" each time its version changes,
    /// so the newest such folder names the current version.
    /// </summary>
    public static string? ReadGameVersion(string documentsDir)
    {
        if (!Directory.Exists(documentsDir)) return null;
        return Directory.EnumerateDirectories(documentsDir, "*profiles(*).bak")
            .Select(dir => new DirectoryInfo(dir))
            .OrderByDescending(dir => dir.LastWriteTimeUtc)
            .Select(dir => Regex.Match(dir.Name, @"\(([\d.]+s?)\)").Groups[1].Value)
            .FirstOrDefault(v => v.Length > 0);
    }
}
