using System.Diagnostics;

namespace Ets2u.Core.Safety;

public sealed class GameRunningException()
    : InvalidOperationException("ETS2 is running. Close the game before making changes.");

/// <summary>Every file the app changes goes through here: game-running check, backup, atomic replace.</summary>
public sealed class SafeWriter(BackupService backups, Func<bool>? isGameRunning = null)
{
    readonly Func<bool> _isGameRunning = isGameRunning ?? IsGameRunning;

    public static bool IsGameRunning() => Process.GetProcessesByName("eurotrucks2").Length > 0;

    /// <summary>
    /// Backs up every existing target, then writes each file and removes each path in <paramref name="delete"/>.
    /// Nothing is touched if the game is running.
    /// </summary>
    public Backup Write(string reason, IReadOnlyDictionary<string, byte[]> files, IReadOnlyCollection<string>? delete = null)
    {
        if (_isGameRunning()) throw new GameRunningException();
        delete ??= Array.Empty<string>();
        var backup = backups.Create(reason, [.. files.Keys, .. delete]);
        foreach (var path in delete)
            File.Delete(path);
        foreach (var (path, bytes) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".ets2u-tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        return backup;
    }
}
