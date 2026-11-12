using System.IO.Compression;
using System.Text;

namespace Ets2u.Core.Mods;

/// <summary>One mod the app generates into the game's mod folder.</summary>
public sealed record ModSpec(string FileName, string DisplayName, string Category, string Description)
{
    public string PackageName => Path.GetFileNameWithoutExtension(FileName);
}

/// <summary>Packs files into a .scs mod (a zip with manifest.sii at its root).</summary>
public static class ScsModBuilder
{
    public const string Version = "1.0";
    public const string Author = "ETS2 Utilities";

    public static string Manifest(ModSpec spec, string compatibleVersions = "1.62.*") =>
        "SiiNunit\n{\n" +
        $"mod_package : .{spec.PackageName}\n{{\n" +
        $"\tpackage_version: \"{Version}\"\n" +
        $"\tdisplay_name: \"{spec.DisplayName}\"\n" +
        $"\tauthor: \"{Author}\"\n" +
        $"\tcategory[]: \"{spec.Category}\"\n" +
        $"\tcompatible_versions[]: \"{compatibleVersions}\"\n" +
        "\tdescription_file: \"description.txt\"\n" +
        // Lets you join Convoy sessions whose host and players don't have this mod (the host must allow optional mods).
        "\tmp_mod_optional: true\n" +
        "}\n}\n";

    /// <summary>Builds the archive in memory. Keys are paths inside the mod, e.g. "def/damage_data.sii".</summary>
    public static byte[] Build(ModSpec spec, IReadOnlyDictionary<string, byte[]> files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "manifest.sii", Encoding.UTF8.GetBytes(Manifest(spec)));
            Add(zip, "description.txt", Encoding.UTF8.GetBytes(spec.Description));
            foreach (var (path, bytes) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
                Add(zip, path.Replace('\\', '/').TrimStart('/'), bytes);
        }
        return stream.ToArray();
    }

    static void Add(ZipArchive zip, string path, byte[] bytes)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        // Fixed timestamp so rebuilding the same content gives the same bytes.
        entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var s = entry.Open();
        s.Write(bytes);
    }

    /// <summary>Reads a file back out of a built mod (used by tests and the status screen).</summary>
    public static byte[]? Read(byte[] scs, string path)
    {
        using var zip = new ZipArchive(new MemoryStream(scs), ZipArchiveMode.Read);
        var entry = zip.GetEntry(path);
        if (entry is null) return null;
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
