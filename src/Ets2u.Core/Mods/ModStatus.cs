using Ets2u.Core.Paths;
using Ets2u.Core.Save;
using Ets2u.Core.Sii;

namespace Ets2u.Core.Mods;

/// <summary>Whether a generated mod is present, and which profiles have it switched on in Mod Manager.</summary>
public static class ModStatus
{
    public static bool IsInstalled(GamePaths paths, ModSpec mod) => File.Exists(Path.Combine(paths.ModDir, mod.FileName));

    /// <summary>Profiles whose profile.sii lists the mod in active_mods (local mods appear as "name|display name").</summary>
    public static IReadOnlyList<string> ActiveIn(IEnumerable<Profile> profiles, ModSpec mod)
    {
        var active = new List<string>();
        foreach (var profile in profiles)
        {
            try
            {
                var doc = SiiDocument.Parse(SiiCodec.Decode(File.ReadAllBytes(profile.ProfileSiiPath)));
                var text = doc.ToString();
                if (text.Contains($"\"{mod.PackageName}|", StringComparison.Ordinal)) active.Add(profile.Name);
            }
            catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException)
            {
                // Unreadable profile: report it as not active.
            }
        }
        return active;
    }
}
