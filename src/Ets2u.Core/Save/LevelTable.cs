using System.Globalization;
using Ets2u.Core.Sii;

namespace Ets2u.Core.Save;

/// <summary>
/// XP needed per level, from def/economy_data_xp.sii (trucks) or def/bus_economy_data_xp.sii (coaches).
/// Levels past the end of the table cost the table's last step.
/// </summary>
public sealed class LevelTable(IReadOnlyList<long> steps)
{
    public const string TruckPath = "def/economy_data_xp.sii";
    public const string CoachPath = "def/bus_economy_data_xp.sii";

    public IReadOnlyList<long> Steps { get; } = steps.Count > 0 ? steps : throw new ArgumentException("Empty level table.");

    public static LevelTable Parse(byte[] sii)
    {
        var doc = SiiDocument.Parse(sii);
        var unit = doc.FindUnit("xp_level_data") ?? throw new FormatException("No xp_level_data unit.");
        var steps = new List<long>();
        for (var i = 0; doc.GetValue(unit, $"level_xp[{i}]") is { } raw; i++)
            steps.Add(long.Parse(raw, CultureInfo.InvariantCulture));
        return new LevelTable(steps);
    }

    long Step(int level) => level < Steps.Count ? Steps[level] : Steps[^1];

    /// <summary>Total XP needed to reach <paramref name="level"/> from level 0.</summary>
    public long XpForLevel(int level)
    {
        long total = 0;
        for (var l = 0; l < level; l++) total += Step(l);
        return total;
    }

    public int LevelForXp(long xp)
    {
        var level = 0;
        long total = 0;
        while (total + Step(level) <= xp && level < 10_000) total += Step(level++);
        return level;
    }
}
