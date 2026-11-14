using System.Buffers.Binary;
using System.IO.Compression;

namespace Ets2u.Core.Extract;

/// <summary>
/// Reads single files out of an SCS HashFS v2 archive (the .scs format since game 1.50),
/// so the app can fetch the few game files it needs in well under a second instead of
/// unpacking whole multi-gigabyte archives. Plain files only; textures aren't needed.
/// Format facts as documented by the Archive::SCS project (metacpan.org/pod/Archive::SCS::HashFS2).
/// </summary>
public sealed class HashFsArchive : IGameArchive, IDisposable
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    readonly record struct Entry(long Offset, int StoredSize, int Size, bool Zlib, bool IsDirectory);

    const int HeaderSize = 0x34;

    readonly FileStream _file;
    readonly Dictionary<ulong, Entry> _entries = new();

    public string Path { get; }

    public HashFsArchive(string path)
    {
        Path = path;
        _file = File.OpenRead(path);
        try { ReadIndex(); }
        catch { _file.Dispose(); throw; }
    }

    public static bool IsHashFsV2(string path)
    {
        Span<byte> head = stackalloc byte[6];
        using var f = File.OpenRead(path);
        return f.Read(head) == 6 && head.StartsWith("SCS#"u8) && head[4] == 2 && head[5] == 0;
    }

    void ReadIndex()
    {
        var header = new byte[HeaderSize];
        _file.ReadExactly(header);
        var h = header.AsSpan();
        if (!h.StartsWith("SCS#"u8)) throw new InvalidDataException($"{Path} is not an SCS archive.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(h[4..]) != 2) throw new InvalidDataException($"{Path} isn't HashFS v2.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(h[6..]) != 0) throw new InvalidDataException($"{Path} uses a hash salt.");
        if (!h[8..12].SequenceEqual("CITY"u8)) throw new InvalidDataException($"{Path} uses an unknown hash method.");

        var entryCount = BinaryPrimitives.ReadInt32LittleEndian(h[12..]);
        var entriesStored = BinaryPrimitives.ReadInt32LittleEndian(h[16..]);
        var metaWords = BinaryPrimitives.ReadInt32LittleEndian(h[20..]);
        var metaStored = BinaryPrimitives.ReadInt32LittleEndian(h[24..]);
        var entriesStart = BinaryPrimitives.ReadInt64LittleEndian(h[28..]);
        var metaStart = BinaryPrimitives.ReadInt64LittleEndian(h[36..]);

        var index = Inflate(ReadAt(entriesStart, entriesStored), entryCount * 16);
        var meta = Inflate(ReadAt(metaStart, metaStored), metaWords * 4);

        for (var i = 0; i < entryCount; i++)
        {
            var e = index.AsSpan(i * 16, 16);
            var hash = BinaryPrimitives.ReadUInt64LittleEndian(e);
            var metaIndex = BinaryPrimitives.ReadInt32LittleEndian(e[8..]);
            var parts = BinaryPrimitives.ReadUInt16LittleEndian(e[12..]);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(e[14..]);

            // Each part is a 4-byte pointer (24-bit word offset + kind); the data part has kind & 0x80.
            for (var k = 0; k < parts; k++)
            {
                var p = meta.AsSpan((metaIndex + k) * 4, 4);
                var kind = p[3];
                if ((kind & 0x80) == 0) continue;
                var at = (p[0] | p[1] << 8 | p[2] << 16) * 4;
                var d = meta.AsSpan(at, 16);
                var stored = d[0] | d[1] << 8 | d[2] << 16;
                var size = d[4] | d[5] << 8 | d[6] << 16;
                var compression = d[3] & 0xF0;
                var offset = (long)BinaryPrimitives.ReadUInt32LittleEndian(d[12..]) * 16;
                _entries[hash] = new Entry(offset, stored, size, compression == 0x10, (flags & 1) != 0);
                break;
            }
        }
    }

    byte[] ReadAt(long offset, int count)
    {
        var buffer = new byte[count];
        _file.Position = offset;
        _file.ReadExactly(buffer);
        return buffer;
    }

    static byte[] Inflate(byte[] data, int expected)
    {
        using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        var output = new byte[expected];
        z.ReadExactly(output);
        return output;
    }

    static ulong Key(string gamePath) => CityHash.Hash64(gamePath.Trim('/').ToLowerInvariant());

    public bool Contains(string gamePath) => _entries.TryGetValue(Key(gamePath), out var e) && !e.IsDirectory;

    /// <summary>The file's bytes, or null if this archive doesn't hold it.</summary>
    public byte[]? Read(string gamePath)
    {
        if (!_entries.TryGetValue(Key(gamePath), out var e) || e.IsDirectory) return null;
        var stored = ReadAt(e.Offset, e.StoredSize);
        return e.Zlib ? Inflate(stored, e.Size) : stored;
    }

    /// <summary>
    /// Names of the files directly in a directory (subdirectories left out), or null if this archive
    /// doesn't have the directory. A listing is a count, one length byte per name, then the names;
    /// subdirectory names start with '/'.
    /// </summary>
    public IReadOnlyList<string>? ListFiles(string gameDir)
    {
        if (!_entries.TryGetValue(Key(gameDir), out var e) || !e.IsDirectory) return null;
        var stored = ReadAt(e.Offset, e.StoredSize);
        var data = (e.Zlib ? Inflate(stored, e.Size) : stored).AsSpan();
        var count = BinaryPrimitives.ReadInt32LittleEndian(data);
        var names = new List<string>(count);
        var at = 4 + count;
        for (var i = 0; i < count; i++)
        {
            var name = System.Text.Encoding.UTF8.GetString(data.Slice(at, data[4 + i]));
            at += data[4 + i];
            if (!name.StartsWith('/')) names.Add(name);
        }
        return names;
    }

    public int EntryCount => _entries.Count;

    public void Dispose() => _file.Dispose();
}
