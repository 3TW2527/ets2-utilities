using Ets2u.Core.Extract;
using Ets2u.Core.Mods;

namespace Ets2u.Core.Features;

/// <summary>Builds every generated mod from the game's own files and the user's settings.</summary>
public static class GeneratedMods
{
    public static IReadOnlyList<ModSpec> All => [NoDamage.Mod, Earnings.Mod, CoachComfort.Mod, HudMod.Mod];

    /// <summary>The mod as this player's files are named (see <see cref="ModSpec.PlayerTag"/>).</summary>
    public static ModSpec ForPlayer(ModSpec mod, AppSettings s) => mod.ForPlayer(s.ModTag ?? "");

    /// <summary>
    /// The mod files to write, keyed by file name. A mod the user never switched on is left out,
    /// so the app doesn't create mods nobody asked for; once created, "off" ships stock files.
    /// <paramref name="exists"/> is asked about the untagged mod and should also count an old-named file.
    /// </summary>
    public static Dictionary<string, byte[]> Build(IGameFiles game, AppSettings s, Func<ModSpec, bool> exists)
    {
        var mods = new Dictionary<string, byte[]>();
        string Name(ModSpec mod) => ForPlayer(mod, s).FileName;
        if (s.NoDamage || exists(NoDamage.Mod))
            mods[Name(NoDamage.Mod)] = NoDamage.BuildMod(game.Read(NoDamage.GamePath), s.NoDamage);
        if (s.IncomeMultiplier != 1 || s.XpMultiplier != 1 || exists(Earnings.Mod))
            mods[Name(Earnings.Mod)] = Earnings.BuildMod(game.Read(Earnings.GamePath), s.IncomeMultiplier, s.XpMultiplier);
        if (s.CoachIgnored != CoachFactor.None || s.CoachFullSatisfaction || exists(CoachComfort.Mod))
            mods[Name(CoachComfort.Mod)] = CoachComfort.BuildMod(game.Read(CoachComfort.GamePath), s.CoachIgnored, s.CoachFullSatisfaction);
        if (s.MapPositionEnabled || exists(HudMod.Mod))
            mods[Name(HudMod.Mod)] = HudMod.BuildMod(game, s);
        return mods;
    }
}
