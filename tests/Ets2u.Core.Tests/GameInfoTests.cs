using System.Text;
using Ets2u.Core.Extract;
using Ets2u.Core.Info;
using Ets2u.Core.Paths;
using Xunit.Abstractions;

namespace Ets2u.Core.Tests;

public class GameInfoTests
{
    sealed class FakeArchive(string name, Dictionary<string, string> files) : IGameArchive
    {
        public string Name => name;

        public IReadOnlyList<string>? ListFiles(string gameDir)
        {
            var names = files.Keys.Where(f => f.StartsWith(gameDir + "/") && !f[(gameDir.Length + 1)..].Contains('/'))
                .Select(f => f[(gameDir.Length + 1)..]).ToList();
            return names.Count == 0 ? null : names;
        }

        public byte[]? Read(string gamePath) => files.TryGetValue(gamePath, out var text) ? Encoding.UTF8.GetBytes(text) : null;
    }

    static string Country(string token, string name, string fuel) => $$"""

        country_data : country.data.{{token}}
        {
        	country_id: 5
        	name: "{{name}}"
        	name_localized: "@@{{token}}@@"
        	country_code: "X"
        	fuel_price: {{fuel}}	# per litre
        }
        """;

    static string City(string token, string name, string country) => $$"""

        city_data : city.{{token}}
        {
        	city_name: "{{name}}"
        	city_name_localized: "@@{{token}}@@"
        	country: {{country}}
        	license_plate[]: .{{token}}.lp.car
        }

        license_plate_data : .{{token}}.lp.car
        {
        	type: car
        }
        """;

    static readonly FakeArchive Def = new("def", new()
    {
        ["def/country.sii"] = "﻿SiiNunit\n{\n@include \"country/germany.sui\"\n@include \"country/france.sui\"\n@include \"country/iceland.sui\"\n}\n",
        ["def/country.custom.sii"] = "SiiNunit\n{\n@include \"/custom/country.sii\"\n}\n",
        ["def/country/germany.sui"] = Country("germany", "Deutschland", "2.244"),
        ["def/country/france.sui"] = Country("france", "France", "2.1"),
        ["def/country/iceland.sui"] = Country("iceland", "Ísland", "2.9"),
        ["def/city.sii"] = "SiiNunit\n{\n@include \"city/koln.sui\"\n@include \"city/paris.sui\"\n}\n",
        ["def/city/koln.sui"] = City("koln", "Köln", "germany"),
        ["def/city/paris.sui"] = City("paris", "Paris", "france"),
        ["def/city_other.sii"] = "SiiNunit\n{\n@include \"city/ghost.sui\"\n}\n",
        ["def/city/ghost.sui"] = City("ghost", "Ghost", "germany"),
    });

    static readonly FakeArchive DlcFr = new("dlc_fr", new()
    {
        ["def/city.dlc_fr.sii"] = "SiiNunit\n{\n@include \"/def/city/lehavre.dlc_fr.sui\"\n}\n",
        ["def/city/lehavre.dlc_fr.sui"] = City("lehavre", "Le Havre", "france"),
    });

    static readonly FakeArchive Locale = new("locale", new()
    {
        ["locale/en_gb/localization.sui"] =
            "\tkey[]: \"germany\"\n\tval[]: \"Germany\"\n\n\tkey[]: \"koln\"\n\tval[]: \"Cologne\"\n\n\tkey[]: \"iceland\"\n\tval[]: \"Iceland\"\n",
    });

    [Fact]
    public void ListsCitiesFromEveryArchiveWithEnglishAndGameNames()
    {
        var info = GameInfo.Read([Def, DlcFr], Locale);

        Assert.Equal(["koln", "lehavre", "paris"], info.Cities.Select(c => c.Token).Order());
        var koln = info.Cities.Single(c => c.Token == "koln");
        Assert.Equal(("Cologne", "Köln", "Germany", "def", "goto Köln"),
            (koln.Name, koln.LocalName, koln.Country, koln.Source, koln.GotoCommand));
        var havre = info.Cities.Single(c => c.Token == "lehavre");
        Assert.Equal(("Le Havre", "France", "dlc_fr"), (havre.Name, havre.Country, havre.Source));
    }

    [Fact]
    public void ListsFuelPricesOnlyForCountriesThatHaveCities()
    {
        var info = GameInfo.Read([Def, DlcFr], Locale);

        Assert.Equal(["france", "germany"], info.Countries.Select(c => c.Token).Order());
        var germany = info.Countries.Single(c => c.Token == "germany");
        Assert.Equal(("Germany", "Deutschland", 2.244m, 1), (germany.Name, germany.LocalName, germany.FuelPrice, germany.CityCount));
        Assert.Equal(2, info.Countries.Single(c => c.Token == "france").CityCount);
    }

    [Fact]
    public void WithoutTheDlcArchiveItsCitiesAreGone()
    {
        var info = GameInfo.Read([Def]);

        Assert.DoesNotContain(info.Cities, c => c.Token == "lehavre");
        Assert.Equal("Köln", info.Cities.Single(c => c.Token == "koln").Name); // no locale: the game's own name
    }
}

/// <summary>Reads the real install. Needs ETS2U_LOCAL=1.</summary>
public class GameInfoLocalTests(ITestOutputHelper output)
{
    [Fact]
    public void ReadsTheInstalledGame()
    {
        var paths = GamePaths.Detect();
        if (Environment.GetEnvironmentVariable("ETS2U_LOCAL") != "1" || paths.InstallDir is null) return;

        var info = GameInfo.FromInstall(paths.InstallDir);
        foreach (var c in info.Countries) output.WriteLine($"{c.Name,-24} {c.FuelPrice,6} {c.CityCount,3} cities");
        output.WriteLine($"{info.Cities.Count} cities; no English name: " +
                         string.Join(", ", info.Cities.Where(c => c.Name == c.LocalName).Select(c => c.LocalName)));
        foreach (var g in info.Cities.GroupBy(c => c.Source)) output.WriteLine($"{g.Key}: {g.Count()}");

        Assert.Contains(info.Cities, c => c is { Token: "berlin", Country: "Germany" });
        Assert.All(info.Countries, c => Assert.True(c.FuelPrice > 0, c.Name));
        Assert.All(info.Cities, c => Assert.False(string.IsNullOrEmpty(c.Country), c.Token));
    }
}
