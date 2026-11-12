using System.Globalization;
using System.Text;
using Ets2u.Core.Paths;
using Ets2u.Core.Safety;
using Ets2u.Core.Sii;

namespace Ets2u.Core.Save;

public sealed record SaveValues(long Money, long TruckXp, long? CoachXp);

public enum EditMode { Set, Add }

/// <summary>What the user typed. A null field means "leave this value as it is".</summary>
public sealed record SaveEdit(EditMode Mode, long? Money, long? TruckXp, long? CoachXp)
{
    /// <summary>The values the save will hold after this edit.</summary>
    public SaveValues ApplyTo(SaveValues now) => Mode == EditMode.Set
        ? new SaveValues(Money ?? now.Money, TruckXp ?? now.TruckXp, now.CoachXp is null ? null : CoachXp ?? now.CoachXp)
        : new SaveValues(now.Money + (Money ?? 0), now.TruckXp + (TruckXp ?? 0), now.CoachXp + (CoachXp ?? 0));

    /// <summary>"1,000,000", "1 000 000" or "" (= unchanged).</summary>
    public static long? ParseField(string text, string what)
    {
        var clean = new string(text.Where(c => !char.IsWhiteSpace(c) && c != ',' && c != '_').ToArray());
        if (clean.Length == 0) return null;
        return long.TryParse(clean, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
            ? n : throw new FormatException($"{what} must be a whole number.");
    }
}

/// <param name="Problem">Why the save can't be edited, when it can't.</param>
public sealed record SaveState(SaveSlot Slot, SiiFormat Format, SaveValues? Values, string? Problem = null)
{
    public bool Editable => Values is not null && Problem is null;
}

/// <summary>
/// Reads and writes money, truck XP and coach XP in binary saves (the game's default) and text saves.
/// Coaches share the truck bank (bank.money_account) but level up on their own counter
/// (economy.bus_experience_points). In a binary save all three are fixed-size numbers, so they're
/// overwritten in place and every other byte stays as the game wrote it.
/// </summary>
public static class SaveEditor
{
    public const string MoneyKey = "money_account";
    public const string TruckXpKey = "experience_points";
    public const string CoachXpKey = "bus_experience_points";

    public const long MaxMoney = 2_000_000_000;

    static readonly (string, string)[] BinaryFields =
        [("bank", MoneyKey), ("economy", TruckXpKey), ("economy", CoachXpKey)];

    public static SaveState Read(SaveSlot slot)
    {
        var plain = SiiCodec.Decode(File.ReadAllBytes(slot.GamePath));
        var format = SiiCodec.Detect(plain);
        try
        {
            return format switch
            {
                SiiFormat.Text => new SaveState(slot, format, ReadValues(SiiDocument.Parse(plain))),
                SiiFormat.Binary => new SaveState(slot, format, ValuesOf(FindBinary(plain))),
                _ => new SaveState(slot, format, null, "This file isn't a save the app recognises."),
            };
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
        {
            return new SaveState(slot, format, null, ex.Message);
        }
    }

    /// <summary>The money and XP fields of a binary save, keyed by field name. Throws if one is missing.</summary>
    static Dictionary<string, BsiiField> FindBinary(byte[] plain)
    {
        var fields = BsiiReader.Find(plain, BinaryFields);
        // One bank and one economy record; take the first of each field to be safe.
        var byName = fields.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First());
        if (!byName.ContainsKey(MoneyKey) || !byName.ContainsKey(TruckXpKey))
            throw new InvalidDataException("Money or XP wasn't found in this save.");
        return byName;
    }

    static SaveValues ValuesOf(Dictionary<string, BsiiField> f) => new(
        Convert.ToInt64(f[MoneyKey].Value),
        Convert.ToInt64(f[TruckXpKey].Value),
        f.TryGetValue(CoachXpKey, out var coach) ? Convert.ToInt64(coach.Value) : null);

    /// <summary>Money and truck XP from info.sii, which stays text even when game.sii is binary. Coach XP isn't in it.</summary>
    public static SaveValues? ReadSummary(SaveSlot slot)
    {
        try
        {
            var info = SiiDocument.Parse(SiiCodec.Decode(File.ReadAllBytes(slot.InfoPath)));
            var money = Number(info, "save_container", "info_money_account");
            var xp = Number(info, "save_container", "info_players_experience");
            return money is null || xp is null ? null : new SaveValues(money.Value, xp.Value, null);
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException)
        {
            return null;
        }
    }

    public static SaveValues? ReadValues(SiiDocument game)
    {
        var money = Number(game, "bank", MoneyKey);
        var truckXp = Number(game, "economy", TruckXpKey);
        if (money is null || truckXp is null) return null;
        return new SaveValues(money.Value, truckXp.Value, Number(game, "economy", CoachXpKey));
    }

    /// <summary>
    /// Writes the new values into game.sii, plus info.sii (load-screen summary) and profile.sii
    /// (profile-screen XP). A binary game.sii is patched in place and written back unencrypted,
    /// still binary; info.sii and profile.sii are written as plain text. The game reads all of these.
    /// </summary>
    public static Backup Write(Profile profile, SaveSlot slot, SaveValues values, SafeWriter writer)
    {
        if (values.Money < 0 || values.Money > MaxMoney) throw new ArgumentOutOfRangeException(nameof(values), "Money must be 0 to 2,000,000,000.");
        if (values.TruckXp < 0 || values.CoachXp < 0) throw new ArgumentOutOfRangeException(nameof(values), "XP can't be negative.");
        if (values.TruckXp > uint.MaxValue || values.CoachXp > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(values), "XP must be at most 4,294,967,295.");

        var plain = SiiCodec.Decode(File.ReadAllBytes(slot.GamePath));
        var files = new Dictionary<string, byte[]> { [slot.GamePath] = SiiCodec.Detect(plain) switch
        {
            SiiFormat.Text => PatchText(plain, values),
            SiiFormat.Binary => PatchBinary(plain, values),
            _ => throw new InvalidDataException("This file isn't a save the app recognises."),
        } };

        if (File.Exists(slot.InfoPath))
        {
            var info = SiiDocument.Parse(SiiCodec.Decode(File.ReadAllBytes(slot.InfoPath)));
            info.SetValue("save_container", "info_money_account", Format(values.Money));
            info.SetValue("save_container", "info_players_experience", Format(values.TruckXp));
            files[slot.InfoPath] = info.ToBytes();
        }

        if (File.Exists(profile.ProfileSiiPath))
        {
            var prof = SiiDocument.Parse(SiiCodec.Decode(File.ReadAllBytes(profile.ProfileSiiPath)));
            prof.SetValue("user_profile", "cached_experience", Format(values.TruckXp));
            files[profile.ProfileSiiPath] = prof.ToBytes();
        }

        return writer.Write($"Money and XP for {profile.Name} / {slot.Name}", files);
    }

    static byte[] PatchText(byte[] plain, SaveValues values)
    {
        var game = SiiDocument.Parse(plain);
        Set(game, "bank", MoneyKey, values.Money);
        Set(game, "economy", TruckXpKey, values.TruckXp);
        if (values.CoachXp is { } coach) Set(game, "economy", CoachXpKey, coach);
        return game.ToBytes();
    }

    static byte[] PatchBinary(byte[] plain, SaveValues values)
    {
        var fields = FindBinary(plain);
        var patched = (byte[])plain.Clone();
        BsiiReader.Patch(patched, fields[MoneyKey], values.Money);
        BsiiReader.Patch(patched, fields[TruckXpKey], values.TruckXp);
        if (values.CoachXp is { } coach && fields.TryGetValue(CoachXpKey, out var coachField))
            BsiiReader.Patch(patched, coachField, coach);
        // Read it back: the walk must still succeed and show exactly the new values.
        if (ValuesOf(FindBinary(patched)) != values with { CoachXp = fields.ContainsKey(CoachXpKey) ? values.CoachXp : null })
            throw new InvalidDataException("The patched save didn't read back as expected; nothing was written.");
        return patched;
    }

    static long? Number(SiiDocument doc, string unitType, string key)
    {
        // Prefer the expected unit, but fall back to any unit holding the key.
        var raw = doc.GetValue(unitType, key)
                  ?? doc.Units.Select(u => doc.GetValue(u, key)).FirstOrDefault(v => v is not null);
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    static void Set(SiiDocument doc, string unitType, string key, long value)
    {
        if (doc.SetValue(unitType, key, Format(value))) return;
        var unit = doc.Units.FirstOrDefault(u => doc.GetValue(u, key) is not null)
                   ?? throw new InvalidDataException($"{key} not found in the save.");
        doc.SetValue(unit, key, Format(value));
    }

    static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Turns text saves on or off in config.cfg (uset g_save_format "2" = text).</summary>
public static class SaveFormatSetting
{
    const string Key = "g_save_format";
    public const string Text = "2";

    public static string? Read(string configPath)
    {
        if (!File.Exists(configPath)) return null;
        foreach (var line in File.ReadLines(configPath))
        {
            var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[0] == "uset" && parts[1] == Key) return parts[2].Trim('"');
        }
        return null;
    }

    public static byte[] WithValue(byte[] config, string value)
    {
        var text = Encoding.UTF8.GetString(config);
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split(newline).ToList();
        var line = $"uset {Key} \"{value}\"";
        var i = lines.FindIndex(l => l.TrimStart().StartsWith($"uset {Key} ", StringComparison.Ordinal));
        if (i >= 0) lines[i] = line;
        else lines.Insert(lines.Count > 0 && lines[^1] == "" ? lines.Count - 1 : lines.Count, line);
        return Encoding.UTF8.GetBytes(string.Join(newline, lines));
    }
}
