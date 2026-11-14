using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ets2u.Core.Extract;

namespace Ets2u.Core.Info;

/// <summary>A country the installed game has cities in. The fuel price is in euros per litre.</summary>
public sealed record CountryInfo(string Token, string Name, string LocalName, string Code, decimal? FuelPrice, int CityCount);

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
        return new GameInfo(countryList, cityList);
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
