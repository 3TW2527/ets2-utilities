using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Ets2u.Core.Liveries;
using Ets2u.Core.Paths;
using Ets2u.Core.Save;

namespace Ets2u.Core.Mods;

/// <summary>One mod the player wants an optional copy of.</summary>
public sealed class OptionalModEntry
{
    /// <summary>Where the original is: a Workshop item, or a local file or folder (Included = make the copy).</summary>
    public LiverySource Source { get; set; } = new();

    /// <summary>What the copy was last built from (see <see cref="OptionalCopy.Fingerprint"/>); a change means it's rebuilt.</summary>
    public string? BuiltFingerprint { get; set; }
}

public sealed class OptionalModsSettings
{
    public List<OptionalModEntry> Mods { get; set; } = new();
}

/// <summary>
/// Makes a copy of another mod that Convoy treats as optional: every file as it is, and the manifest with
/// <c>mp_mod_optional: true</c> added. The copy goes into the mod folder under its own file name
/// (ets2u_opt_&lt;id&gt;_&lt;tag&gt;.scs), so Steam never puts the original back over it and it never clashes with
/// another player's copy. The original stays where it is; it should be switched off in Mod Manager.
/// </summary>
public static class OptionalCopy
{
    /// <summary>How the copy is written; bump it when that changes, so existing copies are rebuilt.</summary>
    public const int Format = 1;

    public const string Prefix = "ets2u_opt_";

    public const string Suffix = " (optional)";

    /// <summary>The part of the file name that names the original: its Workshop ID, or its file or folder name.</summary>
    public static string Slug(LiverySource source)
    {
        if (source.Kind == LiverySourceKind.Workshop) return source.WorkshopId ?? "unknown";
        var name = Path.GetFileNameWithoutExtension((source.Path ?? "").TrimEnd('\\', '/'));
        var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        if (slug.Length > 32) slug = slug[..32].TrimEnd('_');
        return slug.Length == 0 ? "mod" : slug;
    }

    /// <summary>The copy as this player's file is named.</summary>
    public static ModSpec Spec(LiverySource source, string tag) =>
        new(Prefix + Slug(source), source.Name + Suffix, "", "", tag);

    /// <summary>The app's own mods aren't copied: they're optional already.</summary>
    public static bool IsAppMod(string path) =>
        Path.GetFileName(path.TrimEnd('\\', '/')).StartsWith("ets2u_", StringComparison.OrdinalIgnoreCase);

    static readonly Regex Header = new(@"^\s*mod_package\s*:\s*[\w.]+\s*(\{)?\s*$");
    static readonly Regex OptionalLine = new(@"^\s*mp_mod_optional\s*:");
    static readonly Regex NameLine = new(@"^(\s*)display_name\s*:(.*)$");

    /// <summary>Whether a manifest already marks its mod optional.</summary>
    public static bool IsOptional(string manifest) =>
        Lines(manifest).Any(l => OptionalLine.IsMatch(l) && Regex.IsMatch(l, @":\s*true\b", RegexOptions.IgnoreCase));

    static IEnumerable<string> Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    /// <summary>
    /// The manifest with <c>mp_mod_optional: true</c> (any existing value replaced) and "(optional)" after
    /// its display name, so Mod Manager tells the copy from the original. A manifest without a display
    /// name gets <paramref name="fallbackName"/>.
    /// </summary>
    public static string PatchManifest(string manifest, string fallbackName)
    {
        var lines = Lines(manifest.TrimStart('﻿')).ToList();
        var header = lines.FindIndex(l => Header.IsMatch(l));
        if (header < 0) throw new InvalidDataException("The mod's manifest.sii has no mod_package unit.");
        var open = header;
        if (!Header.Match(lines[header]).Groups[1].Success)
        {
            open = lines.FindIndex(header + 1, l => l.Trim().Length > 0);
            if (open < 0 || lines[open].Trim() != "{") throw new InvalidDataException("The mod's manifest.sii couldn't be read.");
        }
        var close = lines.FindIndex(open + 1, l => l.Trim() == "}");
        if (close < 0) throw new InvalidDataException("The mod's manifest.sii couldn't be read.");

        var body = lines.GetRange(open + 1, close - open - 1).Where(l => !OptionalLine.IsMatch(l)).ToList();
        var named = false;
        for (var i = 0; i < body.Count; i++)
        {
            if (NameLine.Match(body[i]) is not { Success: true } m) continue;
            var value = m.Groups[2].Value.Trim();
            var quoted = Regex.Match(value, "^\"(.*)\"");
            var name = quoted.Success ? quoted.Groups[1].Value : value;
            if (!name.EndsWith(Suffix, StringComparison.Ordinal)) name += Suffix;
            body[i] = $"{m.Groups[1].Value}display_name: \"{name}\"";
            named = true;
        }
        if (!named) body.Insert(0, $"\tdisplay_name: \"{Escape(fallbackName)}{Suffix}\"");
        // Lets you join Convoy sessions whose host and players don't have this mod (the host must allow optional mods).
        body.Add("\tmp_mod_optional: true");

        var result = lines.Take(open + 1).Concat(body).Concat(lines.Skip(close));
        return string.Join("\n", result);
    }

    static string Escape(string s) => s.Replace("\\", "").Replace("\"", "'");

    /// <summary>
    /// Writes the copy: the original's files as they are (its picture and description included) and the
    /// patched manifest. Returns whether the original was optional already.
    /// </summary>
    public static bool Write(Stream output, string package, string fallbackName, Action<int, int>? progress = null)
    {
        using var content = ModContent.Load(package);
        var manifestPath = content.Files.FirstOrDefault(f => f.Equals("manifest.sii", StringComparison.OrdinalIgnoreCase))
                           ?? throw new InvalidDataException("The mod has no manifest.sii, so it isn't a mod the game would load.");
        var raw = SiiCodec.Decode(content.Read(manifestPath));
        if (SiiCodec.Detect(raw) == SiiFormat.Binary)
            throw new InvalidDataException("The mod's manifest.sii is in binary form, which this app can't change.");
        var text = Encoding.UTF8.GetString(raw);
        var wasOptional = IsOptional(text);
        var manifest = Encoding.UTF8.GetBytes(PatchManifest(text, fallbackName));

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Add(zip, "manifest.sii", new MemoryStream(manifest));
        var files = content.Files.Where(f => f != manifestPath).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var done = 0;
        foreach (var file in files)
        {
            using (var from = content.Open(file)) Add(zip, file, from);
            progress?.Invoke(++done, files.Count);
        }
        return wasOptional;
    }

    static void Add(ZipArchive zip, string path, Stream from)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var to = entry.Open();
        from.CopyTo(to);
    }

    /// <summary>Changes when the original changes (Steam updated it, the file was replaced) or the copy's name does.</summary>
    public static string Fingerprint(LiverySource source, GamePaths paths) =>
        $"{Format}:{source.Name}:" + LiveryPack.Fingerprint([new LiverySource
        {
            Kind = source.Kind, Path = source.Path, WorkshopId = source.WorkshopId, Name = source.Name, Included = true,
        }], paths);

    /// <summary>
    /// How a Workshop mod is listed in a profile's active_mods: "mod_workshop_package.&lt;ID in 16 hex digits&gt;|name".
    /// </summary>
    public static string? WorkshopPackageName(string? workshopId) =>
        ulong.TryParse(workshopId, out var id) ? $"mod_workshop_package.{id:X16}" : null;

    /// <summary>The package name a local original is listed under in active_mods: its file name without extension.</summary>
    public static string? LocalPackageName(LiverySource source, GamePaths paths)
    {
        if (source.Kind != LiverySourceKind.Local || source.Path is not { } p) return null;
        var full = Path.GetFullPath(p).TrimEnd('\\', '/');
        var dir = Path.GetDirectoryName(full);
        return dir is not null && string.Equals(Path.GetFullPath(paths.ModDir).TrimEnd('\\', '/'), dir, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(full)
            : null; // not in the mod folder, so the game doesn't load it
    }

    /// <summary>Profiles that still have the original switched on, which would load it next to the copy.</summary>
    public static IReadOnlyList<string> OriginalActiveIn(IEnumerable<Profile> profiles, LiverySource source, GamePaths paths)
    {
        var package = source.Kind == LiverySourceKind.Workshop ? WorkshopPackageName(source.WorkshopId) : LocalPackageName(source, paths);
        return package is null ? [] : ModStatus.ActiveIn(profiles, package);
    }
}
