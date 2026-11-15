using System.Text.RegularExpressions;
using Ets2u.Core.Paths;
using Ets2u.Core.Sii;

namespace Ets2u.Core.Liveries;

/// <summary>One ETS2 Steam Workshop item this PC is subscribed to (downloaded by Steam).</summary>
/// <param name="Id">The item's Workshop ID.</param>
/// <param name="Dir">steamapps\workshop\content\227300\&lt;id&gt;.</param>
/// <param name="Package">The folder or .scs file the game would load for the current version, or null if none fits.</param>
public sealed record WorkshopItem(string Id, string Dir, string? Package, string Name, string? Author)
{
    public string PageUrl => Workshop.PageUrl(Id);
}

/// <summary>Finds subscribed Workshop items and picks the version of each that the game would load.</summary>
public static class Workshop
{
    public const string AppId = GamePaths.SteamAppId;

    public static string PageUrl(string id) => $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}";

    /// <summary>Opens the item in the Steam client, where it can be subscribed to.</summary>
    public static string SteamClientUrl(string id) => $"steam://url/CommunityFilePage/{id}";

    /// <summary>The Workshop ID in a pasted link or a bare number, or null.</summary>
    public static string? ParseId(string input)
    {
        var t = input.Trim();
        var m = Regex.Match(t, @"[?&]id=(\d{5,})");
        if (m.Success) return m.Groups[1].Value;
        return Regex.IsMatch(t, @"^\d{5,}$") ? t : null;
    }

    /// <summary>Every Steam library's ETS2 Workshop folder.</summary>
    public static IReadOnlyList<string> ContentDirs(GamePaths paths) =>
        (paths.SteamDir is null ? [] : GamePaths.ReadLibraryFolders(paths.SteamDir))
        .Select(lib => Path.Combine(lib, "steamapps", "workshop", "content", AppId))
        .Where(Directory.Exists)
        .ToList();

    public static string? FindItemDir(GamePaths paths, string id) =>
        ContentDirs(paths).Select(d => Path.Combine(d, id)).FirstOrDefault(Directory.Exists);

    public static IReadOnlyList<WorkshopItem> List(GamePaths paths) =>
        ContentDirs(paths)
            .SelectMany(Directory.EnumerateDirectories)
            .Select(dir => Describe(dir, paths.GameVersion))
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public static WorkshopItem Describe(string dir, string? gameVersion)
    {
        var id = Path.GetFileName(dir);
        var package = PickPackage(dir, gameVersion);
        var (name, author) = package is null ? (null, null) : ReadManifest(package);
        // Steam keeps item titles online only; the manifest, package name or description has to do.
        return new WorkshopItem(id, dir, package, name ?? $"Workshop item {id}", author);
    }

    /// <summary>
    /// Which of the item's packages the game loads: versions.sii lists them, each for some game versions
    /// (compatible_versions[], e.g. "1.62.*") or for any version. Informational packages are never loaded.
    /// An item without versions.sii is its own package.
    /// </summary>
    public static string? PickPackage(string dir, string? gameVersion)
    {
        var versions = Path.Combine(dir, "versions.sii");
        if (!File.Exists(versions)) return LooksLikeMod(dir) ? dir : SingleArchive(dir);

        var candidates = new List<(string Name, IReadOnlyList<string> Versions)>();
        try
        {
            var doc = SiiDocument.Parse(File.ReadAllBytes(versions));
            foreach (var unit in doc.FindUnits("package_version_info"))
            {
                if (doc.GetValue(unit, "informational")?.Trim() == "true") continue;
                var name = Unquote(doc.GetValue(unit, "package_name"));
                if (string.IsNullOrEmpty(name)) continue;
                candidates.Add((name, doc.GetValues(unit, "compatible_versions[]").Select(v => Unquote(v) ?? "").ToList()));
            }
        }
        catch (FormatException)
        {
            return null;
        }

        string? Resolve(string name) =>
            Directory.Exists(Path.Combine(dir, name)) ? Path.Combine(dir, name)
            : File.Exists(Path.Combine(dir, name + ".scs")) ? Path.Combine(dir, name + ".scs")
            : null;

        var version = Short(gameVersion);
        var matching = version is null ? null
            : candidates.FirstOrDefault(c => c.Versions.Any(v => VersionMatches(v, version))).Name;
        var any = candidates.FirstOrDefault(c => c.Versions.Count == 0).Name;
        return (matching is null ? null : Resolve(matching))
               ?? (any is null ? null : Resolve(any))
               ?? (version is null ? candidates.Select(c => Resolve(c.Name)).FirstOrDefault(p => p is not null) : null);
    }

    /// <summary>"1.62.3.2s" → "1.62.3.2".</summary>
    static string? Short(string? v) => v is null ? null : Regex.Match(v, @"^[\d.]+").Value.TrimEnd('.') is { Length: > 0 } s ? s : null;

    /// <summary>"1.62.*" matches 1.62.x; "1.62.3" matches exactly that and its sub-versions.</summary>
    public static bool VersionMatches(string pattern, string version)
    {
        var p = pattern.Trim().TrimEnd('*').TrimEnd('.');
        return version == p || version.StartsWith(p + ".", StringComparison.Ordinal);
    }

    static bool LooksLikeMod(string dir) => File.Exists(Path.Combine(dir, "manifest.sii")) || Directory.Exists(Path.Combine(dir, "def"));

    static string? SingleArchive(string dir)
    {
        var archives = Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".scs", StringComparison.OrdinalIgnoreCase)
                                                              || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        return archives.Count == 1 ? archives[0] : null;
    }

    /// <summary>The mod's display name and author from its manifest.sii, when it has them.</summary>
    public static (string? Name, string? Author) ReadManifest(string package)
    {
        try
        {
            using var content = ModContent.Load(package);
            if (!content.Files.Contains("manifest.sii")) return (null, null);
            var doc = SiiDocument.Parse(content.Read("manifest.sii"));
            var unit = doc.FindUnit("mod_package");
            if (unit is null) return (null, null);
            var name = Unquote(doc.GetValue(unit, "display_name"));
            if (string.IsNullOrWhiteSpace(name)) name = PackageName(package);
            if (string.IsNullOrWhiteSpace(name) && Unquote(doc.GetValue(unit, "description_file")) is { Length: > 0 } descFile
                && content.Files.FirstOrDefault(f => f.Equals(descFile, StringComparison.OrdinalIgnoreCase)) is { } found)
                name = TitleLine(System.Text.Encoding.UTF8.GetString(content.Read(found)));
            return (string.IsNullOrWhiteSpace(name) ? null : name, Unquote(doc.GetValue(unit, "author")));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    /// <summary>The package's folder or file name, unless it's a generic one like "universal".</summary>
    static string? PackageName(string package)
    {
        var name = Path.GetFileNameWithoutExtension(package.TrimEnd('\\', '/'));
        return name.ToLowerInvariant() is "universal" or "latest" or "content" or "mod" or "files" || Regex.IsMatch(name, @"^[\d_.]+$")
            ? null
            : name.Replace('_', ' ');
    }

    /// <summary>
    /// The first line of a description that reads like a title: Workshop formatting ([b], [color]) removed,
    /// and lines of only symbols or ending in ':' ("Vehicles supported:") skipped.
    /// </summary>
    public static string? TitleLine(string description)
    {
        foreach (var raw in description.Split('\n'))
        {
            var line = Regex.Replace(raw, @"\[/?[a-zA-Z0-9=#]*\]", "").Trim().TrimStart('﻿');
            if (line.Length < 3 || line.EndsWith(':') || !line.Any(char.IsLetter)) continue;
            return line.Length > 60 ? line[..60] + "…" : line;
        }
        return null;
    }

    static string? Unquote(string? v)
    {
        if (v is null) return null;
        var t = v.Trim();
        return t.Length >= 2 && t[0] == '"' && t[^1] == '"' ? t[1..^1] : t;
    }
}
