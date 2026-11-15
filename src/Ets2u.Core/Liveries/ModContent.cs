using System.IO.Compression;
using Ets2u.Core.Extract;

namespace Ets2u.Core.Liveries;

/// <summary>The files of one mod, whatever it's packed as: a folder, a zip .scs or a HashFS .scs.</summary>
public abstract class ModContent : IDisposable
{
    /// <summary>Every file, as a path inside the mod with forward slashes ("def/vehicle/...").</summary>
    public abstract IReadOnlyList<string> Files { get; }

    public abstract Stream Open(string path);

    public byte[] Read(string path)
    {
        using var s = Open(path);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public virtual void Dispose()
    {
    }

    /// <summary>Opens a mod folder, or a .scs / .zip file. Throws <see cref="InvalidDataException"/> for formats it can't read.</summary>
    public static ModContent Load(string path)
    {
        if (Directory.Exists(path)) return new FolderContent(path);
        if (!File.Exists(path)) throw new FileNotFoundException($"{path} doesn't exist.");
        Span<byte> head = stackalloc byte[6];
        using (var f = File.OpenRead(path))
            if (f.Read(head) < 4) throw new InvalidDataException($"{Path.GetFileName(path)} is empty.");
        if (head.StartsWith("PK"u8)) return new ZipContent(path);
        if (head.StartsWith("SCS#"u8))
        {
            if (!HashFsArchive.IsHashFsV2(path))
                throw new InvalidDataException($"{Path.GetFileName(path)} is an older or protected SCS archive this app can't read. Use the unpacked mod or a zip version.");
            return new HashFsContent(path);
        }
        throw new InvalidDataException($"{Path.GetFileName(path)} isn't a mod archive (zip or SCS).");
    }
}

sealed class FolderContent(string root) : ModContent
{
    readonly string _root = Path.GetFullPath(root);
    IReadOnlyList<string>? _files;

    public override IReadOnlyList<string> Files => _files ??= Directory
        .EnumerateFiles(_root, "*", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/'))
        .ToList();

    public override Stream Open(string path) => File.OpenRead(Path.Combine(_root, path));
}

sealed class ZipContent : ModContent
{
    readonly ZipArchive _zip;
    readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.Ordinal);

    public ZipContent(string path)
    {
        _zip = ZipFile.OpenRead(path);
        foreach (var e in _zip.Entries)
        {
            if (e.FullName.EndsWith('/')) continue; // directory entry
            _entries.TryAdd(e.FullName.Replace('\\', '/').TrimStart('/'), e);
        }
    }

    public override IReadOnlyList<string> Files => _entries.Keys.ToList();

    public override Stream Open(string path) => _entries[path].Open();

    public override void Dispose() => _zip.Dispose();
}

sealed class HashFsContent(string path) : ModContent
{
    readonly HashFsArchive _archive = new(path);
    IReadOnlyList<string>? _files;

    /// <summary>HashFS stores names only in directory listings, so the root listing is needed (mods have one).</summary>
    public override IReadOnlyList<string> Files => _files ??= _archive.EnumerateFiles().ToList() is { Count: > 0 } files
        ? files
        : throw new InvalidDataException($"{Path.GetFileName(path)} has no file listing, so its contents can't be found.");

    public override Stream Open(string p) =>
        new MemoryStream(_archive.Read(p) ?? throw new FileNotFoundException($"{p} isn't in {Path.GetFileName(path)}."));

    public override void Dispose() => _archive.Dispose();
}
