using System.Text;
using Ets2u.Core.Paths;
using Ets2u.Core.Safety;
using Ets2u.Core.Save;

namespace Ets2u.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("ets2u-test-").FullName;
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

public class SiiCodecTests
{
    [Fact]
    public void EncodeThenDecodeRoundTrips()
    {
        var plain = Encoding.UTF8.GetBytes("SiiNunit\n{\n}\n");
        var wrapped = SiiCodec.Encode(plain);
        Assert.True(SiiCodec.IsEncrypted(wrapped));
        Assert.Equal(plain, SiiCodec.Decode(wrapped));
    }

    [Fact]
    public void PlainBytesPassThrough()
    {
        var plain = Encoding.UTF8.GetBytes("SiiNunit\n{\n}\n");
        Assert.Same(plain, SiiCodec.Decode(plain));
    }

    [Theory]
    [InlineData("SiiNunit", SiiFormat.Text)]
    [InlineData("BSII\u0003", SiiFormat.Binary)]
    [InlineData("other", SiiFormat.Unknown)]
    public void DetectsFormat(string start, SiiFormat expected) =>
        Assert.Equal(expected, SiiCodec.Detect(Encoding.ASCII.GetBytes(start)));
}

public class SafeWriterTests
{
    [Fact]
    public void BacksUpThenWrites()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "game.sii");
        File.WriteAllText(target, "old");
        var backups = new BackupService(Path.Combine(dir.Path, "backups"));

        var backup = new SafeWriter(backups, () => false)
            .Write("test", new Dictionary<string, byte[]> { [target] = "new"u8.ToArray() });

        Assert.Equal("new", File.ReadAllText(target));
        Assert.Single(backups.List());
        backups.Restore(backup);
        Assert.Equal("old", File.ReadAllText(target));

        // Restoring backed up "new" first, so the restore itself can be undone.
        var undo = backups.List().First();
        Assert.StartsWith("Before restoring", undo.Reason);
        backups.Restore(undo);
        Assert.Equal("new", File.ReadAllText(target));
    }

    [Fact]
    public void RefusesWhileGameRuns()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "game.sii");
        File.WriteAllText(target, "old");
        var writer = new SafeWriter(new BackupService(Path.Combine(dir.Path, "b")), () => true);

        Assert.Throws<GameRunningException>(() =>
            writer.Write("test", new Dictionary<string, byte[]> { [target] = "new"u8.ToArray() }));
        Assert.Equal("old", File.ReadAllText(target));
    }

    [Fact]
    public void KeepsOnlyNewestBackups()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "f.txt");
        File.WriteAllText(file, "x");
        var backups = new BackupService(Path.Combine(dir.Path, "b"), keep: 3);
        for (var i = 0; i < 5; i++)
        {
            backups.Create($"r{i}", file);
            Thread.Sleep(5);
        }
        Assert.Equal(["r4", "r3", "r2"], backups.List().Select(b => b.Reason));
    }
}

public class PathsTests
{
    [Theory]
    [InlineData("6B6D62", "kmb")]
    [InlineData("507265734243", "PresBC")]
    [InlineData("notHex", "notHex")]
    public void DecodesProfileFolder(string folder, string name) =>
        Assert.Equal(name, ProfileLocator.DecodeFolderName(folder));

    [Fact]
    public void ReadsLibraryFolders()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "steamapps"));
        File.WriteAllText(Path.Combine(dir.Path, "steamapps", "libraryfolders.vdf"),
            "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t}\n}\n");
        Assert.Equal([dir.Path, @"D:\SteamLibrary"], GamePaths.ReadLibraryFolders(dir.Path));
    }

    [Fact]
    public void ReadsVersionFromNewestBackupFolder()
    {
        using var dir = new TempDir();
        var older = Directory.CreateDirectory(Path.Combine(dir.Path, "steam_profiles(1.62.2.1s).bak"));
        older.LastWriteTimeUtc = DateTime.UtcNow.AddDays(-1);
        Directory.CreateDirectory(Path.Combine(dir.Path, "steam_profiles(1.62.3.2s).bak"));
        Assert.Equal("1.62.3.2s", GamePaths.ReadGameVersion(dir.Path));
    }
}
