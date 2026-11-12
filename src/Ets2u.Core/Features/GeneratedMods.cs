using Ets2u.Core.Extract;
using Ets2u.Core.Mods;

namespace Ets2u.Core.Features;

/// <summary>Builds every generated mod from the game's own files and the user's settings.</summary>
public static class GeneratedMods
{
    public static IReadOnlyList<ModSpec> All => [NoDamage.Mod, Earnings.Mod, CoachComfort.Mod, HudMod.Mod];

    /// <summary>
    /// The mod files to write, keyed by file name. A mod the user never switched on is left out,
    /// so the app doesn't create mods nobody asked for; once created, "off" ships stock files.
    /// </summary>
    public static Dictionary<string, byte[]> Build(IGameFiles game, AppSettings s, Func<ModSpec, bool> exists)
    {
        var mods = new Dictionary<string, byte[]>();
        if (s.NoDamage || exists(NoDamage.Mod))
            mods[NoDamage.Mod.FileName] = NoDamage.BuildMod(game.Read(NoDamage.GamePath), s.NoDamage);
        if (s.IncomeMultiplier != 1 || s.XpMultiplier != 1 || exists(Earnings.Mod))
            mods[Earnings.Mod.FileName] = Earnings.BuildMod(game.Read(Earnings.GamePath), s.IncomeMultiplier, s.XpMultiplier);
        if (s.CoachNoPenalties || s.CoachFullSatisfaction || exists(CoachComfort.Mod))
            mods[CoachComfort.Mod.FileName] = CoachComfort.BuildMod(game.Read(CoachComfort.GamePath), s.CoachNoPenalties, s.CoachFullSatisfaction);
        if (s.MapPositionEnabled || exists(HudMod.Mod))
            mods[HudMod.Mod.FileName] = HudMod.BuildMod(game, s);
        return mods;
    }
}
