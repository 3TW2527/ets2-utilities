using System.Text;
using Ets2u.Core.Features;
using Ets2u.Core.Sii;
using Ets2u.Core.Telemetry;

namespace Ets2u.Core.Tests;

public class HudModTests
{
    // Same shape as ui/adviser.sii (hand-written, shortened).
    const string Stock =
        "SiiNunit\n{\n" +
        "adviser_hud_configuration : .adv_hud_r\n{\n" +
        " window_keep_aspect_standard: right\n window_keep_aspect_uk: left\n" +
        " standard_positions: 1\n standard_positions[0]: 50\n uk_positions: 1\n uk_positions[0]: 60\n" +
        " standard_layout_setup_command: 1\n standard_layout_setup_command[0]: \"align_x=right, align_y=bottom, reverse=false\"\n" +
        " uk_layout_setup_command: 1\n uk_layout_setup_command[0]: \"align_x=left, align_y=bottom, reverse=false\"\n" +
        " widget_types[0]: minimap\n}\n\n" +
        "adviser_hud_configuration : .adv_hud_l\n{\n" +
        " window_keep_aspect_standard: left\n window_keep_aspect_uk: right\n" +
        " standard_layout_setup_command: 1\n standard_layout_setup_command[0]: \"align_x=left, align_y=center, reverse=false\"\n" +
        " uk_layout_setup_command: 1\n uk_layout_setup_command[0]: \"align_x=right, align_y=center, reverse=true\"\n" +
        "}\n\n" +
        "adviser_hud_configuration : .adv_hud_c\n{\n window_keep_aspect_standard: center\n window_keep_aspect_uk: center\n}\n" +
        "}\n";

    static (string Std, string Uk) Aspect(SiiDocument doc, string block)
    {
        var unit = doc.Units.Single(u => u.Name == block);
        return (doc.GetValue(unit, "window_keep_aspect_standard")!, doc.GetValue(unit, "window_keep_aspect_uk")!);
    }

    static SiiDocument Build(MapSide side) => SiiDocument.Parse(HudMod.Build(Encoding.UTF8.GetBytes(Stock), side));

    [Fact]
    public void SwapMovesMapAndTachometerToTheOtherSide()
    {
        var doc = Build(MapSide.Swap);
        Assert.Equal(("left", "right"), Aspect(doc, ".adv_hud_r"));
        Assert.Equal(("right", "left"), Aspect(doc, ".adv_hud_l"));
        var map = doc.Units.Single(u => u.Name == ".adv_hud_r");
        Assert.Equal("60", doc.GetValue(map, "standard_positions[0]"));
        Assert.Equal("\"align_x=left, align_y=bottom, reverse=false\"", doc.GetValue(map, "standard_layout_setup_command[0]"));
        Assert.Equal("1", doc.GetValue(map, "standard_positions"));
    }

    [Fact]
    public void AlwaysLeftPinsMapLeftAndTachometerRight()
    {
        var doc = Build(MapSide.Left);
        Assert.Equal(("left", "left"), Aspect(doc, ".adv_hud_r"));
        Assert.Equal(("right", "right"), Aspect(doc, ".adv_hud_l"));
    }

    [Fact]
    public void AlwaysRightPinsMapRightAndTachometerLeft()
    {
        var doc = Build(MapSide.Right);
        Assert.Equal(("right", "right"), Aspect(doc, ".adv_hud_r"));
        Assert.Equal(("left", "left"), Aspect(doc, ".adv_hud_l"));
    }

    [Fact]
    public void LeavesCentreBlockAlone()
    {
        var doc = Build(MapSide.Swap);
        Assert.Equal(("center", "center"), Aspect(doc, ".adv_hud_c"));
    }
}

public class SpeedTextTests
{
    [Theory]
    [InlineData(0f, "0 km/h · 0 mph")]
    [InlineData(25f, "90 km/h · 56 mph")]
    [InlineData(-5f, "18 km/h · 11 mph")]
    [InlineData(22.352f, "80 km/h · 50 mph")]
    public void FormatsBothUnits(float ms, string expected) => Assert.Equal(expected, SpeedText.Format(ms));

    [Fact]
    public void NoLimitGivesNull() => Assert.Null(SpeedText.FormatLimit(0));

    [Fact]
    public void FormatsLimit() => Assert.Equal("Limit 80 km/h · 50 mph", SpeedText.FormatLimit(80 / 3.6f));

    [Fact]
    public void FrameLayoutMatchesPlugin() =>
        Assert.Equal(24, System.Runtime.InteropServices.Marshal.SizeOf<TelemetryFrame>());
}
