using System.Text.Json;

namespace Ets2u.Core.Safety;

public sealed record BackupFile(string OriginalPath, string StoredName);

public sealed record Backup(string Dir, DateTime CreatedUtc, string Reason, IReadOnlyList<BackupFile> Files);

/// <summary>Copies files aside before the app changes them, and puts them back on request.</summary>
public sealed class BackupService(string root, int keep = 20)
{
    const string ManifestName = "backup.json";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Root { get; } = root;

    public Backup Create(string reason, params string[] paths)
    {
        var created = DateTime.UtcNow;
        var dir = Path.Combine(Root, created.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(dir);

        var files = new List<BackupFile>();
        foreach (var (path, i) in paths.Where(File.Exists).Select((p, i) => (p, i)))
        {
            var stored = $"{i:D2}_{Path.GetFileName(path)}";
            File.Copy(path, Path.Combine(dir, stored));
            files.Add(new BackupFile(Path.GetFullPath(path), stored));
        }

        var backup = new Backup(dir, created, reason, files);
        File.WriteAllText(Path.Combine(dir, ManifestName), JsonSerializer.Serialize(backup, Json));
        Prune();
        return backup;
    }

    public IReadOnlyList<Backup> List()
    {
        if (!Directory.Exists(Root)) return Array.Empty<Backup>();
        return Directory.EnumerateDirectories(Root)
            .Select(d => Path.Combine(d, ManifestName))
            .Where(File.Exists)
            .Select(m => JsonSerializer.Deserialize<Backup>(File.ReadAllText(m))!)
            .OrderByDescending(b => b.CreatedUtc)
            .ToList();
    }

    /// <summary>Puts a backup's files back. The current files are backed up first, so a restore can be undone too.</summary>
    public void Restore(Backup backup)
    {
        // Read first: creating the undo backup may prune this one if it's the oldest.
        var contents = backup.Files.Select(f => (f.OriginalPath, Bytes: File.ReadAllBytes(Path.Combine(backup.Dir, f.StoredName)))).ToList();
        Create($"Before restoring {backup.CreatedUtc.ToLocalTime():dd MMM HH:mm}",
            backup.Files.Select(f => f.OriginalPath).ToArray());
        foreach (var (path, bytes) in contents)
            File.WriteAllBytes(path, bytes);
    }

    void Prune()
    {
        foreach (var old in List().Skip(keep))
            Directory.Delete(old.Dir, recursive: true);
    }
}
