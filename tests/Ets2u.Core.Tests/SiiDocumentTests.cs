using System.Text;
using Ets2u.Core.Sii;

namespace Ets2u.Core.Tests;

public class SiiDocumentTests
{
    // Hand-written in the shape of a text save; not copied from the game.
    const string Save =
        "SiiNunit\r\n" +
        "{\r\n" +
        "bank : _nameless.1f0.2a3b {\r\n" +
        " money_account: 2000\r\n" +
        " coinsurance_fixed: 0  # comment\r\n" +
        " loans: 0\r\n" +
        "}\r\n" +
        "\r\n" +
        "economy : _nameless.1f0.2a3c {\r\n" +
        " experience_points: 1234\r\n" +
        " bus_experience_points: 56\r\n" +
        " company_name: \"Has # inside\"  // trailing\r\n" +
        " stored_cities[]: berlin\r\n" +
        " stored_cities[]: paris\r\n" +
        "}\r\n" +
        "\r\n" +
        "}\r\n";

    // Def files put the brace on the next line and use tabs.
    const string Def =
        "SiiNunit\n" +
        "{\n" +
        "vehicle_damage_data : .damage_data\n" +
        "{\n" +
        "\t# damage dealt to truck and trailer\n" +
        "\n" +
        "\ttruck_damage_coef:\t0.0007\t\t# per hit\n" +
        "\tengine_wear:\t\t2e-6\n" +
        "}\n" +
        "}\n";

    [Theory]
    [InlineData(Save)]
    [InlineData(Def)]
    public void RoundTripIsByteIdentical(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Assert.Equal(bytes, SiiDocument.Parse(bytes).ToBytes());
    }

    [Fact]
    public void KeepsUtf8Bom()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Def)];
        Assert.Equal(bytes, SiiDocument.Parse(bytes).ToBytes());
    }

    [Fact]
    public void FindsUnitsAndValues()
    {
        var doc = SiiDocument.Parse(Save);
        Assert.Equal(["bank", "economy"], doc.Units.Select(u => u.Type));
        Assert.Equal("2000", doc.GetValue("bank", "money_account"));
        Assert.Equal("0", doc.GetValue("bank", "coinsurance_fixed"));
        Assert.Equal("56", doc.GetValue("economy", "bus_experience_points"));
        Assert.Equal("\"Has # inside\"", doc.GetValue("economy", "company_name"));
    }

    [Fact]
    public void ReadsDefStyleUnits()
    {
        var doc = SiiDocument.Parse(Def);
        Assert.Equal("0.0007", doc.GetValue("vehicle_damage_data", "truck_damage_coef"));
        Assert.Equal("2e-6", doc.GetValue("vehicle_damage_data", "engine_wear"));
    }

    [Fact]
    public void SetValueChangesOnlyThatValue()
    {
        var doc = SiiDocument.Parse(Save);
        Assert.True(doc.SetValue("bank", "money_account", "500000000"));
        Assert.True(doc.SetValue("bank", "coinsurance_fixed", "7"));
        Assert.True(doc.SetValue("economy", "company_name", "\"New\""));

        var expected = Save
            .Replace(" money_account: 2000", " money_account: 500000000")
            .Replace(" coinsurance_fixed: 0  # comment", " coinsurance_fixed: 7  # comment")
            .Replace("\"Has # inside\"  // trailing", "\"New\"  // trailing");
        Assert.Equal(expected, doc.ToString());
    }

    [Fact]
    public void SetValueKeepsTabsAndComment()
    {
        var doc = SiiDocument.Parse(Def);
        doc.SetValue("vehicle_damage_data", "truck_damage_coef", "0");
        Assert.Contains("\ttruck_damage_coef:\t0\t\t# per hit\n", doc.ToString());
    }

    [Fact]
    public void SetValueOnMissingKeyReturnsFalse()
    {
        var doc = SiiDocument.Parse(Save);
        Assert.False(doc.SetValue("bank", "nope", "1"));
        Assert.False(doc.SetValue("nope", "money_account", "1"));
        Assert.Equal(Save, doc.ToString());
    }

    [Fact]
    public void RejectsNonSii() =>
        Assert.Throws<FormatException>(() => SiiDocument.Parse("hello"));
}
