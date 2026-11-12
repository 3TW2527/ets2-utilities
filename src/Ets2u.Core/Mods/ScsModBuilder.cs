using System.IO.Compression;
using System.Text;

namespace Ets2u.Core.Mods;

/// <summary>One mod the app generates into the game's mod folder.</summary>
/// <param name="Id">Base file name, e.g. "ets2u_coach".</param>
/// <param name="PlayerTag">
/// Per-player suffix (see <see cref="ModTag"/>). The game identifies a local mod by its file name, so two
/// players' copies, built from different settings, count as two different optional mods in Convoy
/// instead of one mod in two versions. It never changes with settings, so the mod stays enabled.
/// </param>
public sealed record ModSpec(string Id, string DisplayName, string Category, string Description, string PlayerTag = "")
{
    public string FileName => PlayerTag.Length == 0 ? $"{Id}.scs" : $"{Id}_{PlayerTag}.scs";
    public string PackageName => Path.GetFileNameWithoutExtension(FileName);

    /// <summary>The untagged name v0.4 and earlier used.</summary>
    public string LegacyFileName => $"{Id}.scs";

    public ModSpec ForPlayer(string tag) => this with { PlayerTag = tag };
}

/// <summary>Packs files into a .scs mod (a zip with manifest.sii at its root).</summary>
public static class ScsModBuilder
{
    public const string Version = "1.0";
    public const string Author = "ETS2 Utilities";

    /// <summary>
    /// The manifest's unit name. SII name parts are at most 12 characters (a longer one made the game
    /// skip the whole manifest, so the mod showed with no name), and the game takes the mod's identity
    /// from the file name, so one fixed short name serves every mod.
    /// </summary>
    public const string ManifestUnit = ".ets2u_mod";

    public static string Manifest(ModSpec spec, string compatibleVersions = "1.62.*") =>
        "SiiNunit\n{\n" +
        $"mod_package : {ManifestUnit}\n{{\n" +
        $"\tpackage_version: \"{Version}\"\n" +
        $"\tdisplay_name: \"{spec.DisplayName}\"\n" +
        $"\tauthor: \"{Author}\"\n" +
        $"\tcategory[]: \"{spec.Category}\"\n" +
        $"\tcompatible_versions[]: \"{compatibleVersions}\"\n" +
        "\tdescription_file: \"description.txt\"\n" +
        (Icon(spec) is null ? "" : $"\ticon: \"{IconFile}\"\n") +
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
            if (Icon(spec) is { } icon) Add(zip, IconFile, icon);
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

    /// <summary>The picture Mod Manager shows (276x162 JPG), stored in every mod under this name.</summary>
    public const string IconFile = "mod_icon.jpg";

    /// <summary>The mod's picture, embedded in this assembly as icons/&lt;id&gt;.jpg, or null if it has none.</summary>
    public static byte[]? Icon(ModSpec spec)
    {
        using var stream = typeof(ScsModBuilder).Assembly.GetManifestResourceStream($"icons/{spec.Id}.jpg");
        if (stream is null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
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
