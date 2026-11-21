using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ets2u.Core.Extract;

namespace Ets2u.Core.Info;

/// <summary>A country the installed game has cities in. The fuel price is in euros per litre.</summary>
public sealed record CountryInfo(string Token, string Name, string LocalName, string Code, decimal? FuelPrice, int CityCount);

/// <summary>
/// A currency the game's options offer (def/economy_data.sii): <see cref="Ratio"/> of it to the euro, and the
/// signs the game writes before (<see cref="Prefix"/>) and after (<see cref="Suffix"/>) an amount.
/// </summary>
public sealed record CurrencyInfo(string Code, decimal Ratio, string Prefix, string Suffix)
{
    public static readonly CurrencyInfo Euro = new("EUR", 1, "€", "");

    /// <summary>A euro amount in this currency, the way the game writes it ("1.234 €"-style signs, e.g. "£1.271", "45.22 Kč").</summary>
    public string Format(decimal euros)
    {
        var value = euros * Ratio;
        var digits = Math.Abs(value) < 10 ? "0.000" : Math.Abs(value) < 1000 ? "0.00" : "#,0";
        return Prefix + value.ToString(digits, CultureInfo.InvariantCulture) + Suffix;
    }

    /// <summary>"EUR (€)", "CZK (Kč)".</summary>
    public string Label => (Prefix + Suffix).Trim() is { Length: > 0 } sign && sign != Code ? $"{Code} ({sign})" : Code;

    public override string ToString() => Label;
}

/// <summary>
/// A city of the installed game. <see cref="LocalName"/> is the game's own "city_name"
/// (e.g. "Köln"), which the console's goto command takes; <see cref="Name"/> is the English one.
/// </summary>
public sealed record CityInfo(string Token, string Name, string LocalName, string CountryToken, string Country, string Source)
{
    public string GotoCommand => "goto " + LocalName;
}

/// <summary>
/// Countries and cities as the installed game defines them. Cities come from each archive's own
/// "def/city*.sii" list, so a map DLC that isn't installed contributes none; countries without a
/// city are left out (the base game defines every country, owned or not).
/// </summary>
public sealed record GameInfo(IReadOnlyList<CountryInfo> Countries, IReadOnlyList<CityInfo> Cities)
{
    /// <summary>The currencies the game's options offer, the default (euro) first.</summary>
    public IReadOnlyList<CurrencyInfo> Currencies { get; init; } = [CurrencyInfo.Euro];

    public const string BaseArchive = "def";
    const string EnglishNames = "locale/en_gb/localization.sui";

    static readonly Regex Include = new("^\\s*@include\\s+\"(?<path>[^\"]+)\"", RegexOptions.Multiline);
    static readonly Regex CityUnit = Unit(@"city_data\s*:\s*city\.(?<token>\w+)");
    static readonly Regex CountryUnit = Unit(@"country_data\s*:\s*country\.data\.(?<token>\w+)");
    static readonly Regex Translation = new("key\\[\\]:\\s*\"(?<key>[^\"]*)\"\\s*val\\[\\]:\\s*\"(?<val>[^\"]*)\"");

    static Regex Unit(string header) =>
        new(@"^\s*" + header + @"\s*\{(?<body>.*?)^\s*\}", RegexOptions.Multiline | RegexOptions.Singleline);

    /// <summary>Reads the game's archives in <paramref name="installDir"/>: def.scs, every dlc_*.scs, and locale.scs for English names.</summary>
    public static GameInfo FromInstall(string installDir)
    {
        var open = new List<HashFsArchive>();
        try
        {
            HashFsArchive? Open(string path)
            {
                if (!File.Exists(path) || !HashFsArchive.IsHashFsV2(path)) return null;
                var archive = new HashFsArchive(path);
                open.Add(archive);
                return archive;
            }

            var paths = Directory.EnumerateFiles(installDir, "dlc_*.scs").Order(StringComparer.OrdinalIgnoreCase)
                .Prepend(Path.Combine(installDir, BaseArchive + ".scs"));
            var archives = paths.Select(Open).OfType<IGameArchive>().ToList();
            return Read(archives, Open(Path.Combine(installDir, "locale.scs")));
        }
        finally
        {
            foreach (var archive in open) archive.Dispose();
        }
    }

    /// <summary>Base archive first; a later archive's definition of the same city or country replaces an earlier one.</summary>
    public static GameInfo Read(IReadOnlyList<IGameArchive> archives, IGameArchive? locale = null)
    {
        var english = new Dictionary<string, string>(StringComparer.Ordinal);
        if (locale?.Read(EnglishNames) is { } names)
            foreach (Match m in Translation.Matches(Decode(names)))
                english.TryAdd(m.Groups["key"].Value, m.Groups["val"].Value);

        string Localize(string body, string key, string fallback) =>
            Value(body, key) is { } tag && english.TryGetValue(tag.Trim('@'), out var name) && name.Length > 0 ? name : fallback;

        var countries = new Dictionary<string, (string Name, string Local, string Code, decimal? Fuel)>();
        foreach (var (_, token, body) in Units(archives, "country", CountryUnit))
        {
            var local = Value(body, "name") ?? token;
            var fuel = decimal.TryParse(Value(body, "fuel_price"), NumberStyles.Float, CultureInfo.InvariantCulture, out var price)
                ? price : (decimal?)null;
            countries[token] = (Localize(body, "name_localized", local), local, Value(body, "country_code") ?? "", fuel);
        }

        var cities = new Dictionary<string, CityInfo>();
        foreach (var (archive, token, body) in Units(archives, "city", CityUnit))
        {
            var local = Value(body, "city_name") ?? token;
            var country = Value(body, "country") ?? "";
            cities[token] = new CityInfo(token, Localize(body, "city_name_localized", local), local, country,
                countries.TryGetValue(country, out var c) ? c.Name : country, archive);
        }

        var cityList = cities.Values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var countryList = countries
            .Select(c => new CountryInfo(c.Key, c.Value.Name, c.Value.Local, c.Value.Code, c.Value.Fuel,
                cityList.Count(city => city.CountryToken == c.Key)))
            .Where(c => c.CityCount > 0)
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new GameInfo(countryList, cityList) { Currencies = ReadCurrencies(archives) };
    }

    const string EconomyData = "def/economy_data.sii";

    /// <summary>
    /// The currency_code[] / currency_ratio[] / currency_sign1..3[] lists in economy_data.sii; a later archive's
    /// list replaces an earlier one. The game marks a few codes with "*" (e.g. "BGN*"); that's left off.
    /// </summary>
    static IReadOnlyList<CurrencyInfo> ReadCurrencies(IReadOnlyList<IGameArchive> archives)
    {
        IReadOnlyList<CurrencyInfo> found = [CurrencyInfo.Euro];
        foreach (var archive in archives)
        {
            if (archive.Read(EconomyData) is not { } bytes) continue;
            var text = Decode(bytes);
            List<string> List(string key) => Regex.Matches(text,
                    @"^\s*" + Regex.Escape(key) + @"\[\]\s*:\s*(?:""(?<v>[^""]*)""|(?<v>[^\s#]+))", RegexOptions.Multiline)
                .Select(m => m.Groups["v"].Value).ToList();
            var codes = List("currency_code");
            if (codes.Count == 0) continue;
            var ratios = List("currency_ratio");
            var (sign1, sign2, sign3) = (List("currency_sign1"), List("currency_sign2"), List("currency_sign3"));
            static string At(List<string> list, int i) => i < list.Count ? list[i] : "";
            found = codes
                .Select((code, i) => (code, ratio: decimal.TryParse(At(ratios, i), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0, i))
                .Where(c => c.ratio > 0 && c.code.Trim('*').Length > 0)
                .Select(c => new CurrencyInfo(c.code.Trim('*'), c.ratio, At(sign1, c.i) + At(sign2, c.i), At(sign3, c.i)))
                .ToList();
            if (found.Count == 0) found = [CurrencyInfo.Euro];
        }
        return found;
    }

    /// <summary>
    /// Every unit reachable from an archive's "def/&lt;list&gt;.sii" and "def/&lt;list&gt;.*.sii" files, which
    /// hold one @include per city or country.
    /// </summary>
    static IEnumerable<(string Archive, string Token, string Body)> Units(IReadOnlyList<IGameArchive> archives, string list, Regex unit)
    {
        foreach (var archive in archives)
        {
            var lists = (archive.ListFiles("def") ?? [])
                .Where(f => f.EndsWith(".sii", StringComparison.OrdinalIgnoreCase)
                            && (f.Equals(list + ".sii", StringComparison.OrdinalIgnoreCase)
                                || f.StartsWith(list + ".", StringComparison.OrdinalIgnoreCase)));
            foreach (var file in lists)
            {
                if (archive.Read("def/" + file) is not { } bytes) continue;
                var text = Decode(bytes);
                var parts = Include.Matches(text)
                    .Select(m => m.Groups["path"].Value)
                    .Select(p => p.StartsWith('/') ? p.TrimStart('/') : "def/" + p)
                    .Select(p => archive.Read(p) ?? archives.Select(a => a.Read(p)).FirstOrDefault(b => b is not null))
                    .OfType<byte[]>()
                    .Select(Decode)
                    .Prepend(text);
                foreach (var part in parts)
                foreach (Match m in unit.Matches(part))
                    yield return (archive.Name, m.Groups["token"].Value, m.Groups["body"].Value);
            }
        }
    }

    /// <summary>A top-level attribute of a unit body, without quotes or a trailing comment.</summary>
    static string? Value(string body, string key)
    {
        var m = Regex.Match(body, @"^\s*" + Regex.Escape(key) + "\\s*:\\s*(?:\"(?<v>[^\"]*)\"|(?<v>[^\\s#]+))", RegexOptions.Multiline);
        return m.Success ? m.Groups["v"].Value : null;
    }

    static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimStart('﻿');
}
