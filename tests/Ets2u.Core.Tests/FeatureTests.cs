using System.Text;
using Ets2u.Core.Features;
using Ets2u.Core.Mods;
using Ets2u.Core.Paths;
using Ets2u.Core.Safety;
using Ets2u.Core.Save;
using Ets2u.Core.Sii;

namespace Ets2u.Core.Tests;

public class NoDamageTests
{
    // Same shape as the game's damage_data.sii (hand-written, shortened).
    const string Stock =
        "SiiNunit\n{\nvehicle_damage_data : .damage_data\n{\n" +
        "\ttruck_damage_coef:\t0.0007\t\t# per hit\n" +
        "\ttruck_to_trailer_dmg: \t0.2\n" +
        "\tside_damage_factor:\t6.0\n" +
        "\tcabin_damage_ratio:\t0.8\n" +
        "\tengine_wear:\t\t2e-6\n" +
        "\twheel_wear_unfixable:\t\t2e-5\n" +
        "}\n}\n";

    [Fact]
    public void OnZeroesEveryDamageValue()
    {
        var doc = SiiDocument.Parse(NoDamage.Build(Encoding.UTF8.GetBytes(Stock), enabled: true));
        foreach (var key in new[] { "truck_damage_coef", "truck_to_trailer_dmg", "side_damage_factor", "cabin_damage_ratio", "engine_wear", "wheel_wear_unfixable" })
            Assert.Equal("0.0", doc.GetValue("vehicle_damage_data", key));
        Assert.Contains("# per hit", doc.ToString());
    }

    [Fact]
    public void OffIsTheStockFile()
    {
        var stock = Encoding.UTF8.GetBytes(Stock);
        Assert.Equal(stock, NoDamage.Build(stock, enabled: false));
    }

    [Fact]
    public void ModStateReadsBack()
    {
        var stock = Encoding.UTF8.GetBytes(Stock);
        Assert.True(NoDamage.ReadState(NoDamage.BuildMod(stock, true)));
        Assert.False(NoDamage.ReadState(NoDamage.BuildMod(stock, false)));
    }
}

public class EarningsTests
{
    const string Stock =
        "SiiNunit\n{\neconomy_data : economy.data.storage\n{\n" +
        "\tfixed_revenue: 600\t# in Euro\n" +
        "\trevenue_coef_per_km: 0.9\n" +
        "\tcargo_market_revenue_coef_per_km: 1.0\n" +
        "\texp_cargo_delivery: \t1.0\n" +
        "\texp_free_roam: \t\t0.5\n" +
        "\texp_road_discovery:\t0.8\n" +
        "\tcabin_wear: 0.0\n" +
        "}\n}\n";

    [Fact]
    public void MultipliesIncomeAndXpOnly()
    {
        var doc = SiiDocument.Parse(Earnings.Build(Encoding.UTF8.GetBytes(Stock), income: 10, xp: 3));
        Assert.Equal("6000", doc.GetValue("economy_data", "fixed_revenue"));
        Assert.Equal("9", doc.GetValue("economy_data", "revenue_coef_per_km"));
        Assert.Equal("10", doc.GetValue("economy_data", "cargo_market_revenue_coef_per_km"));
        Assert.Equal("3", doc.GetValue("economy_data", "exp_cargo_delivery"));
        Assert.Equal("1.5", doc.GetValue("economy_data", "exp_free_roam"));
        Assert.Equal("2.4", doc.GetValue("economy_data", "exp_road_discovery"));
        Assert.Equal("0.0", doc.GetValue("economy_data", "cabin_wear"));
    }

    [Fact]
    public void OneTimesIsStock()
    {
        var stock = Encoding.UTF8.GetBytes(Stock);
        Assert.Equal(stock, Earnings.Build(stock, 1, 1));
    }

    [Fact]
    public void ClampsToFifty()
    {
        var doc = SiiDocument.Parse(Earnings.Build(Encoding.UTF8.GetBytes(Stock), income: 999, xp: 1));
        Assert.Equal("30000", doc.GetValue("economy_data", "fixed_revenue"));
    }
}

public class CoachComfortTests
{
    // Same shape as def/bus_economy_data.sii (hand-written, shortened).
    const string Stock =
        "SiiNunit\n{\nbus_economy_data : bus_economy.data\n{\n" +
        "\tsatisfaction_new_passenger: (0.45, 0.8)\n" +
        "\tsatisfaction_new_passenger_core_wear_penalty: (0.04, 0.08)\n" +
        "\tsatisfaction_new_passenger_core_wear_reward: (0.02, 0.04)\n" +
        "\tsatisfaction_acceleration_limits[]: (6.0, 6.0, 6.0)\n" +
        "\tsatisfaction_acceleration_penalties[]: (0.09, 0.18)\n" +
        "\tsatisfaction_acceleration_limits[]: (1000.0, 1000.0, 11.0)\n" +
        "\tsatisfaction_acceleration_penalties[]: (0.6, 0.9)\n" +
        "\tsatisfaction_crash_penalties[]: (0.05, 0.1)\n" +
        "\tsatisfaction_speed_penalty: (0.09, 0.18)\n" +
        "\tsatisfaction_on_time_reward: (0.06, 0.18)\n" +
        "\tfailure_acceleration_limit: (-1.0, -1.0, 22.0)\n" +
        "}\n}\n";

    static SiiDocument Build(bool noPenalties, bool full) =>
        SiiDocument.Parse(CoachComfort.Build(Encoding.UTF8.GetBytes(Stock), noPenalties, full));

    [Fact]
    public void ZeroesEveryPenaltyIncludingRepeatedArrayLines()
    {
        var doc = Build(true, false);
        var unit = doc.FindUnit("bus_economy_data")!;
        Assert.Equal(["(0.0, 0.0)", "(0.0, 0.0)"], doc.GetValues(unit, "satisfaction_acceleration_penalties[]"));
        Assert.Equal("(0.0, 0.0)", doc.GetValue(unit, "satisfaction_crash_penalties[]"));
        Assert.Equal("(0.0, 0.0)", doc.GetValue(unit, "satisfaction_speed_penalty"));
        Assert.Equal("(0.0, 0.0)", doc.GetValue(unit, "satisfaction_new_passenger_core_wear_penalty"));
        Assert.Equal("(-1.0, -1.0, -1.0)", doc.GetValue(unit, CoachComfort.FailureLimitKey));
        // Rewards, limits and the boarding value stay.
        Assert.Equal("(0.06, 0.18)", doc.GetValue(unit, "satisfaction_on_time_reward"));
        Assert.Equal(["(6.0, 6.0, 6.0)", "(1000.0, 1000.0, 11.0)"], doc.GetValues(unit, "satisfaction_acceleration_limits[]"));
        Assert.Equal("(0.45, 0.8)", doc.GetValue(unit, CoachComfort.NewPassengerKey));
    }

    [Theory]
    [InlineData("satisfaction_speed_penalty", true)]
    [InlineData("satisfaction_crash_penalties[]", true)]
    [InlineData("satisfaction_new_passenger_core_wear_penalty", true)]
    [InlineData("satisfaction_no_penalty_reward", false)]
    [InlineData("satisfaction_no_penalty_stage_reward", false)]
    [InlineData("satisfaction_no_penalty_reward_distance_km", false)]
    [InlineData("satisfaction_penalty_cooldown_time_s", false)]
    [InlineData("satisfaction_acceleration_limits[]", false)]
    public void PicksOnlyPenalties(string key, bool penalty) => Assert.Equal(penalty, CoachComfort.IsPenaltyKey(key));

    [Fact]
    public void FullSatisfactionOnlyChangesBoarding()
    {
        var doc = Build(false, true);
        var unit = doc.FindUnit("bus_economy_data")!;
        Assert.Equal("(1.0, 1.0)", doc.GetValue(unit, CoachComfort.NewPassengerKey));
        Assert.Equal("(0.09, 0.18)", doc.GetValue(unit, "satisfaction_speed_penalty"));
    }

    [Fact]
    public void BothOffIsStock()
    {
        var stock = Encoding.UTF8.GetBytes(Stock);
        Assert.Equal(stock, CoachComfort.Build(stock, false, false));
    }
}

public class ModBuilderTests
{
    [Fact]
    public void ManifestMarksModsOptionalForConvoy() =>
        Assert.Contains("mp_mod_optional: true", ScsModBuilder.Manifest(NoDamage.Mod));

    [Fact]
    public void ArchiveHasManifestAndFiles()
    {
        var scs = ScsModBuilder.Build(NoDamage.Mod, new Dictionary<string, byte[]> { ["def/damage_data.sii"] = "x"u8.ToArray() });
        var manifest = Encoding.UTF8.GetString(ScsModBuilder.Read(scs, "manifest.sii")!);
        Assert.Contains("mod_package : .ets2u_nodamage", manifest);
        Assert.Contains("display_name: \"ETS2 Utilities: No damage\"", manifest);
        SiiDocument.Parse(manifest);
        Assert.Equal("x"u8.ToArray(), ScsModBuilder.Read(scs, "def/damage_data.sii"));
    }

    [Fact]
    public void SameInputGivesSameBytes()
    {
        var files = new Dictionary<string, byte[]> { ["def/a.sii"] = "a"u8.ToArray() };
        Assert.Equal(ScsModBuilder.Build(NoDamage.Mod, files), ScsModBuilder.Build(NoDamage.Mod, files));
    }
}

public class SaveEditorTests
{
    const string Game =
        "SiiNunit\r\n{\r\n" +
        "bank : _nameless.1 {\r\n money_account: 2000\r\n loans: 0\r\n}\r\n" +
        "economy : _nameless.2 {\r\n experience_points: 100\r\n bus_experience_points: 50\r\n}\r\n" +
        "}\r\n";

    const string Info = "SiiNunit\n{\nsave_container : _nameless.3 {\n info_players_experience: 100\n info_money_account: 2000\n}\n\n}\n";
    const string ProfileSii = "SiiNunit\n{\nuser_profile : _nameless.4 {\n cached_experience: 100\n profile_name: kmb\n}\n\n}\n";

    static (Profile, SaveSlot, TempDir) MakeProfile(bool encrypted, string game = Game) =>
        MakeProfile(encrypted, Encoding.UTF8.GetBytes(game));

    static (Profile, SaveSlot, TempDir) MakeProfile(bool encrypted, byte[] game)
    {
        var dir = new TempDir();
        var profileDir = Path.Combine(dir.Path, "6B6D62");
        var saveDir = Path.Combine(profileDir, "save", "autosave");
        Directory.CreateDirectory(saveDir);
        byte[] Wrap(string s) => encrypted ? SiiCodec.Encode(Encoding.UTF8.GetBytes(s)) : Encoding.UTF8.GetBytes(s);
        File.WriteAllBytes(Path.Combine(saveDir, "game.sii"), encrypted ? SiiCodec.Encode(game) : game);
        File.WriteAllBytes(Path.Combine(saveDir, "info.sii"), Wrap(Info));
        File.WriteAllBytes(Path.Combine(profileDir, "profile.sii"), Wrap(ProfileSii));
        var profile = new Profile("6B6D62", "kmb", profileDir, ProfileStorage.SteamCloud);
        return (profile, profile.Saves().Single(), dir);
    }

    [Fact]
    public void ReadsMoneyTruckXpAndCoachXp()
    {
        var (_, slot, dir) = MakeProfile(encrypted: true);
        using (dir)
        {
            var state = SaveEditor.Read(slot);
            Assert.True(state.Editable);
            Assert.Equal(new SaveValues(2000, 100, 50), state.Values);
        }
    }

    [Fact]
    public void WritesAllThreeFilesAsTextAndBacksUp()
    {
        var (profile, slot, dir) = MakeProfile(encrypted: true);
        using (dir)
        {
            var backups = new BackupService(Path.Combine(dir.Path, "backups"));
            SaveEditor.Write(profile, slot, new SaveValues(1_000_000, 300_000, 120_000), new SafeWriter(backups, () => false));

            Assert.Equal(new SaveValues(1_000_000, 300_000, 120_000), SaveEditor.Read(slot).Values);
            var info = SiiDocument.Parse(File.ReadAllBytes(slot.InfoPath));
            Assert.Equal("1000000", info.GetValue("save_container", "info_money_account"));
            Assert.Equal("300000", info.GetValue("save_container", "info_players_experience"));
            var prof = SiiDocument.Parse(File.ReadAllBytes(profile.ProfileSiiPath));
            Assert.Equal("300000", prof.GetValue("user_profile", "cached_experience"));
            Assert.Contains("\r\n loans: 0\r\n", File.ReadAllText(slot.GamePath));
            Assert.Equal(3, backups.List().Single().Files.Count);
        }
    }

    [Fact]
    public void ReadsABinarySave()
    {
        var (_, slot, dir) = MakeProfile(encrypted: true, BinarySave.Build(2000, 100, 50));
        using (dir)
        {
            var state = SaveEditor.Read(slot);
            Assert.Equal(SiiFormat.Binary, state.Format);
            Assert.True(state.Editable);
            Assert.Equal(new SaveValues(2000, 100, 50), state.Values);
        }
    }

    [Fact]
    public void PatchesABinarySaveInPlace()
    {
        var original = BinarySave.Build(2000, 100, 50);
        var (profile, slot, dir) = MakeProfile(encrypted: true, original);
        using (dir)
        {
            SaveEditor.Write(profile, slot, new SaveValues(1_000_000, 300_000, 120_000),
                new SafeWriter(new BackupService(Path.Combine(dir.Path, "b")), () => false));

            var written = File.ReadAllBytes(slot.GamePath);
            Assert.True(BsiiReader.IsBinary(written)); // still binary, now unencrypted
            Assert.Equal(original.Length, written.Length);
            var changed = Enumerable.Range(0, original.Length).Count(i => original[i] != written[i]);
            Assert.InRange(changed, 1, 16); // only the three numbers' bytes
            Assert.Equal(new SaveValues(1_000_000, 300_000, 120_000), SaveEditor.Read(slot).Values);
        }
    }

    [Fact]
    public void UnknownBinaryTypeIsReportedNotGuessed()
    {
        var (profile, slot, dir) = MakeProfile(encrypted: false, BinarySave.Build(2000, 100, 50, extraFieldType: 0x7F));
        using (dir)
        {
            var state = SaveEditor.Read(slot);
            Assert.False(state.Editable);
            Assert.Contains("0x7f", state.Problem);
            Assert.ThrowsAny<Exception>(() => SaveEditor.Write(profile, slot, new SaveValues(1, 1, 1),
                new SafeWriter(new BackupService(Path.Combine(dir.Path, "b")), () => false)));
        }
    }

    [Fact]
    public void BinaryXpMustFitIn32Bits()
    {
        var (profile, slot, dir) = MakeProfile(encrypted: false, BinarySave.Build(2000, 100, 50));
        using (dir)
            Assert.Throws<ArgumentOutOfRangeException>(() => SaveEditor.Write(profile, slot,
                new SaveValues(1, (long)uint.MaxValue + 1, 0), new SafeWriter(new BackupService(dir.Path), () => false)));
    }

    [Fact]
    public void RejectsMoneyOverCap()
    {
        var (profile, slot, dir) = MakeProfile(encrypted: false);
        using (dir)
            Assert.Throws<ArgumentOutOfRangeException>(() => SaveEditor.Write(profile, slot,
                new SaveValues(SaveEditor.MaxMoney + 1, 0, 0), new SafeWriter(new BackupService(dir.Path), () => false)));
    }

    [Fact]
    public void SaveFormatSettingReplacesOrAdds()
    {
        var cfg = Encoding.UTF8.GetBytes("# config\r\nuset g_save_format \"0\"\r\nuset g_console \"1\"\r\n");
        var updated = Encoding.UTF8.GetString(SaveFormatSetting.WithValue(cfg, "2"));
        Assert.Equal("# config\r\nuset g_save_format \"2\"\r\nuset g_console \"1\"\r\n", updated);

        var added = Encoding.UTF8.GetString(SaveFormatSetting.WithValue(Encoding.UTF8.GetBytes("uset a \"1\"\n"), "2"));
        Assert.Equal("uset a \"1\"\nuset g_save_format \"2\"\n", added);
    }

    static readonly SaveValues Now = new(1000, 500, 300);

    [Fact]
    public void SetKeepsEmptyFields() =>
        Assert.Equal(new SaveValues(5000, 500, 300), new SaveEdit(EditMode.Set, 5000, null, null).ApplyTo(Now));

    [Fact]
    public void AddAddsOnlyWhatWasTyped() =>
        Assert.Equal(new SaveValues(1500, 500, 290), new SaveEdit(EditMode.Add, 500, null, -10).ApplyTo(Now));

    [Fact]
    public void AddWithNothingTypedChangesNothing() =>
        Assert.Equal(Now, new SaveEdit(EditMode.Add, null, null, null).ApplyTo(Now));

    [Fact]
    public void CoachXpStaysAbsentWhenTheSaveHasNone() =>
        Assert.Null(new SaveEdit(EditMode.Set, null, null, 999).ApplyTo(Now with { CoachXp = null }).CoachXp);

    [Theory]
    [InlineData("1,000,000", 1_000_000L)]
    [InlineData(" 1 000 000 ", 1_000_000L)]
    [InlineData("-250", -250L)]
    [InlineData("", null)]
    public void ParsesFields(string text, long? expected) => Assert.Equal(expected, SaveEdit.ParseField(text, "x"));

    [Fact]
    public void RejectsNonNumbers() => Assert.Throws<FormatException>(() => SaveEdit.ParseField("12a", "Money"));

    [Fact]
    public void LevelTableMatchesGameSteps()
    {
        var table = new LevelTable([200, 500, 700]);
        Assert.Equal(0, table.XpForLevel(0));
        Assert.Equal(1400, table.XpForLevel(3));
        Assert.Equal(2100, table.XpForLevel(4));
        Assert.Equal(2, table.LevelForXp(700));
        Assert.Equal(3, table.LevelForXp(1400));
    }
}
