namespace Ets2u.Core.Extract;

/// <summary>Read access to the game's own files by game path, e.g. "def/damage_data.sii".</summary>
public interface IGameFiles
{
    /// <summary>The file's bytes, or null if the game doesn't have it.</summary>
    byte[]? TryRead(string gamePath);
}

public static class GameFilesExtensions
{
    public static byte[] Read(this IGameFiles files, string gamePath) =>
        files.TryRead(gamePath) ?? throw new FileNotFoundException($"{gamePath} wasn't found in the game's archives.");
}

/// <summary>
/// Reads game files straight out of the installed .scs archives, the way the game layers them
/// (a DLC overrides the base game). Only the archives that hold files the app needs are opened,
/// and only their indexes are read, so a lookup takes milliseconds and nothing is extracted.
/// </summary>
public sealed class GameArchives(string installDir) : IGameFiles, IDisposable
{
    /// <summary>Lookup order: first archive that has the file wins.</summary>
    public static readonly string[] Archives = ["dlc_bus", "def", "base_share", "core", "base"];

    readonly Dictionary<string, HashFsArchive?> _open = new();

    public byte[]? TryRead(string gamePath)
    {
        foreach (var name in Archives)
            if (Open(name)?.Read(gamePath) is { } bytes) return bytes;
        return null;
    }

    HashFsArchive? Open(string name)
    {
        if (_open.TryGetValue(name, out var archive)) return archive;
        var path = Path.Combine(installDir, name + ".scs");
        archive = File.Exists(path) && HashFsArchive.IsHashFsV2(path) ? new HashFsArchive(path) : null;
        _open[name] = archive;
        return archive;
    }

    public void Dispose()
    {
        foreach (var archive in _open.Values) archive?.Dispose();
        _open.Clear();
    }
}

/// <summary>Files held in memory, for tests.</summary>
public sealed class InMemoryGameFiles(IReadOnlyDictionary<string, byte[]> files) : IGameFiles
{
    public byte[]? TryRead(string gamePath) => files.GetValueOrDefault(gamePath.TrimStart('/'));
}
