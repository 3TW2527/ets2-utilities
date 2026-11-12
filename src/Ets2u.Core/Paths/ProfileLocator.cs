using System.Text;

namespace Ets2u.Core.Paths;

public enum ProfileStorage { SteamCloud, Local }

public sealed record SaveSlot(string Name, string Dir, DateTime LastWriteUtc)
{
    public string GamePath => System.IO.Path.Combine(Dir, "game.sii");
    public string InfoPath => System.IO.Path.Combine(Dir, "info.sii");
}

public sealed record Profile(string FolderName, string Name, string Dir, ProfileStorage Storage)
{
    public string ProfileSiiPath => Path.Combine(Dir, "profile.sii");

    /// <summary>Save folders that hold a game.sii, newest first.</summary>
    public IReadOnlyList<SaveSlot> Saves()
    {
        var saveRoot = Path.Combine(Dir, "save");
        if (!Directory.Exists(saveRoot)) return Array.Empty<SaveSlot>();
        return Directory.EnumerateDirectories(saveRoot)
            .Where(d => File.Exists(Path.Combine(d, "game.sii")))
            .Select(d => new SaveSlot(Path.GetFileName(d), d, File.GetLastWriteTimeUtc(Path.Combine(d, "game.sii"))))
            .OrderByDescending(s => s.LastWriteUtc)
            .ToList();
    }

    public override string ToString() => $"{Name} ({(Storage == ProfileStorage.SteamCloud ? "Steam Cloud" : "local")})";
}

public static class ProfileLocator
{
    public static IReadOnlyList<Profile> Find(GamePaths paths)
    {
        var found = new List<Profile>();
        foreach (var root in paths.CloudProfileRoots)
            found.AddRange(Scan(root, ProfileStorage.SteamCloud));
        // Steam Cloud profiles also leave a stub in Documents/steam_profiles (settings only, no saves); skip those.
        found.AddRange(Scan(paths.LocalProfilesDir, ProfileStorage.Local));
        return found;
    }

    static IEnumerable<Profile> Scan(string root, ProfileStorage storage)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!File.Exists(Path.Combine(dir, "profile.sii"))) continue;
            var folder = Path.GetFileName(dir);
            yield return new Profile(folder, DecodeFolderName(folder), dir, storage);
        }
    }

    /// <summary>Profile folders are the profile name as UTF-8 hex, e.g. 6B6D62 = "kmb".</summary>
    public static string DecodeFolderName(string folder)
    {
        if (folder.Length % 2 != 0) return folder;
        try { return Encoding.UTF8.GetString(Convert.FromHexString(folder)); }
        catch (FormatException) { return folder; }
    }
}
