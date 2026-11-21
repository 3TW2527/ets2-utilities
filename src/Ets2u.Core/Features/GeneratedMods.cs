using Ets2u.Core.Extract;
using Ets2u.Core.Mods;

namespace Ets2u.Core.Features;

/// <summary>Builds every generated mod from the game's own files and the user's settings.</summary>
public static class GeneratedMods
{
    /// <summary>Mods built from the game's own files and the user's settings (the livery pack is built from other mods).</summary>
    public static IReadOnlyList<ModSpec> All => [NoDamage.Mod, Earnings.Mod, CoachComfort.Mod, HudMod.Mod, BusDisplay.Mod];

    /// <summary>The limits to write, or null to keep the game's.</summary>
    public static CoachGLimits? CoachLimits(AppSettings s) => s.CoachLimits is { IsStock: false } l ? l : null;

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
        var limits = CoachLimits(s);
        if (s.CoachIgnored != CoachFactor.None || s.CoachFullSatisfaction || limits is not null || exists(CoachComfort.Mod))
            mods[Name(CoachComfort.Mod)] = CoachComfort.BuildMod(game.Read(CoachComfort.GamePath), s.CoachIgnored, s.CoachFullSatisfaction, limits);
        if (s.MapPositionEnabled || s.HideGMeter || exists(HudMod.Mod))
            mods[Name(HudMod.Mod)] = HudMod.BuildMod(game, s);
        // The display template is in dlc_bus.scs, so this needs the coaches DLC.
        if ((s.BusDisplay.Enabled || exists(BusDisplay.Mod)) && game.TryRead(BusDisplay.GamePath) is { } template)
            mods[Name(BusDisplay.Mod)] = BusDisplay.BuildMod(template, s.BusDisplay);
        return mods;
    }
}
