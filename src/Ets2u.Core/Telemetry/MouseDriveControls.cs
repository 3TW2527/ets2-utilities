using System.Text.RegularExpressions;

namespace Ets2u.Core.Telemetry;

/// <summary>
/// What mouse driving changes in a profile's controls.sii, and how it puts it back.
/// <para>
/// The camera's mixes are gated by the plugin: <c>camlr</c> and <c>camud</c> take the mouse's
/// movement times <c>(1 - semantical.camlr?0)</c> / <c>(1 - semantical.camud?0)</c>. The plugin's
/// device reports 1 on those inputs while the mouse steers, so the camera holds still, and 0 when
/// mouse driving is off, while the right button is held (looking round) or in menus: then the
/// mouse looks round as usual. Steering, keyboard steering and the game's own mouse steering option
/// are left as they are.
/// </para>
/// <para>
/// v0.7 previews switched the game's mouse steering mode on (<c>c_mousesteer 1</c>) and muted its
/// <c>msteering</c> mix; in that mode nothing else steers, so <see cref="Apply"/> puts those back.
/// </para>
/// </summary>
public static class MouseDriveControls
{
    public const string FileName = "controls.sii";

    const string MuteSuffix = " * 0";

    static readonly Regex MouseSteer = new(@"(?<pre>""constant c_mousesteer )(?<value>[-0-9.]+)(?<post>"")", RegexOptions.Compiled);
    static readonly Regex Steering = new(@"(?<pre>""mix msteering `)(?<expr>[^`]*)(?<post>`"")", RegexOptions.Compiled);
    static readonly Regex Camera = new(@"(?<pre>""mix (?<name>camlr|camud) `)(?<expr>[^`]*)(?<post>`"")", RegexOptions.Compiled);

    static string Gate(string name) => $" * (1 - semantical.{name}?0)";

    /// <summary>The profile's own values, or null when the file doesn't have the camera lines.</summary>
    public static ControlsOriginal? Read(string text)
    {
        string? lr = null, ud = null;
        foreach (Match m in Camera.Matches(text))
            if (m.Groups["name"].Value == "camlr") lr = m.Groups["expr"].Value;
            else ud = m.Groups["expr"].Value;
        if (lr is null || ud is null) return null;
        return new ControlsOriginal(
            MouseSteer.Match(text) is { Success: true } c ? c.Groups["value"].Value : null,
            Steering.Match(text) is { Success: true } s ? s.Groups["expr"].Value : null,
            lr, ud);
    }

    /// <summary>The camera is gated and nothing of the v0.7 previews' change is left.</summary>
    public static bool IsApplied(string text) =>
        Read(text) is { } c
        && c.CamLr!.EndsWith(Gate("camlr"), StringComparison.Ordinal)
        && c.CamUd!.EndsWith(Gate("camud"), StringComparison.Ordinal)
        && !LegacyApplied(c);

    static bool LegacyApplied(ControlsOriginal c) => c.MouseSteeringMix?.EndsWith(MuteSuffix, StringComparison.Ordinal) == true;

    /// <summary>
    /// The text with the camera gated. <paramref name="recorded"/> is what the profile had before the
    /// v0.7 previews changed it, if known.
    /// </summary>
    public static string Apply(string text, ControlsOriginal? recorded = null)
    {
        text = UndoLegacy(text, recorded);
        return Camera.Replace(text, m =>
        {
            var name = m.Groups["name"].Value;
            var expr = m.Groups["expr"].Value;
            if (expr.EndsWith(Gate(name), StringComparison.Ordinal)) return m.Value;
            // "-mouse.rel_position.x?0 * c_msens - semantical.camlr?0": the semantical part now gates the mouse part.
            var mouse = Regex.Replace(expr, $@"\s*[-+]\s*semantical\.{name}\?0\s*$", "");
            return m.Groups["pre"].Value + "(" + mouse + ")" + Gate(name) + m.Groups["post"].Value;
        });
    }

    /// <summary>The text with the profile's own values back (as a fresh profile has them when they weren't recorded).</summary>
    public static string Restore(string text, ControlsOriginal? recorded)
    {
        text = UndoLegacy(text, recorded);
        return Camera.Replace(text, m =>
        {
            var name = m.Groups["name"].Value;
            var own = name == "camlr" ? recorded?.CamLr : recorded?.CamUd;
            if (own is not null && !own.EndsWith(Gate(name), StringComparison.Ordinal))
                return m.Groups["pre"].Value + own + m.Groups["post"].Value;
            var expr = m.Groups["expr"].Value;
            if (!expr.EndsWith(Gate(name), StringComparison.Ordinal) || !expr.StartsWith('(')) return m.Value;
            var mouse = expr[1..^(Gate(name).Length + 1)];
            return m.Groups["pre"].Value + mouse + $" - semantical.{name}?0" + m.Groups["post"].Value;
        });
    }

    /// <summary>Puts back what the v0.7 previews changed: the game's mouse steering mode and its msteering mix.</summary>
    static string UndoLegacy(string text, ControlsOriginal? recorded)
    {
        if (Steering.Match(text) is not { Success: true } s || !s.Groups["expr"].Value.EndsWith(MuteSuffix, StringComparison.Ordinal))
            return text;
        var mode = recorded?.MouseSteering is { } v && !recorded.MouseSteeringMix!.EndsWith(MuteSuffix, StringComparison.Ordinal) ? v : "0.000000";
        text = MouseSteer.Replace(text, m => m.Groups["pre"].Value + mode + m.Groups["post"].Value);
        return Steering.Replace(text, m =>
        {
            var expr = m.Groups["expr"].Value;
            var own = recorded?.MouseSteeringMix is { } r && !r.EndsWith(MuteSuffix, StringComparison.Ordinal) ? r
                : expr.StartsWith('(') ? expr[1..^(MuteSuffix.Length + 1)] : expr;
            return m.Groups["pre"].Value + own + m.Groups["post"].Value;
        });
    }
}

/// <summary>A profile's own values for what mouse driving changes (or changed), kept to put them back.</summary>
public sealed record ControlsOriginal(string? MouseSteering, string? MouseSteeringMix, string? CamLr = null, string? CamUd = null);
