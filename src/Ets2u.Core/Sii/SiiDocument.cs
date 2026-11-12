using System.Text;
using System.Text.RegularExpressions;

namespace Ets2u.Core.Sii;

/// <summary>
/// A text SII file kept line by line, so a value can be changed without touching
/// any other byte (indentation, comments, ordering and line endings survive).
/// </summary>
public sealed class SiiDocument
{
    static readonly Regex UnitHeader = new(@"^\s*(?<type>[\w.]+)\s*:\s*(?<name>[\w.]+)\s*(?<brace>\{)?\s*$");
    static readonly Regex Entry = new(@"^(?<lead>\s*)(?<key>[\w]+(?:\[\d*\])?)(?<sep>\s*:\s*)(?<rest>.*?)(?<eol>\r?\n)?$");

    readonly List<string> _lines;
    readonly bool _bom;
    readonly List<SiiUnit> _units = new();

    SiiDocument(List<string> lines, bool bom)
    {
        _lines = lines;
        _bom = bom;
        Index();
    }

    public IReadOnlyList<SiiUnit> Units => _units;

    public static SiiDocument Parse(byte[] bytes)
    {
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        return Parse(text, bom);
    }

    public static SiiDocument Parse(string text, bool bom = false)
    {
        if (!text.TrimStart().StartsWith("SiiNunit", StringComparison.Ordinal))
            throw new FormatException("Not a text SII file (no SiiNunit header).");
        // Split after each '\n' so every line keeps its own ending.
        var lines = Regex.Split(text, "(?<=\n)").Where(l => l.Length > 0).ToList();
        return new SiiDocument(lines, bom);
    }

    public byte[] ToBytes()
    {
        var body = Encoding.UTF8.GetBytes(string.Concat(_lines));
        return _bom ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }

    public override string ToString() => string.Concat(_lines);

    public SiiUnit? FindUnit(string type) => _units.FirstOrDefault(u => u.Type == type);

    public IEnumerable<SiiUnit> FindUnits(string type) => _units.Where(u => u.Type == type);

    /// <summary>The raw value text of <paramref name="key"/> in the first unit of <paramref name="unitType"/>.</summary>
    public string? GetValue(string unitType, string key)
    {
        var unit = FindUnit(unitType);
        return unit is null ? null : GetValue(unit, key);
    }

    public string? GetValue(SiiUnit unit, string key) =>
        unit.Entries.TryGetValue(key, out var line) ? SplitEntry(_lines[line]).value : null;

    /// <summary>Replace a value in place. Returns false if the unit or key doesn't exist.</summary>
    public bool SetValue(string unitType, string key, string value)
    {
        var unit = FindUnit(unitType);
        return unit is not null && SetValue(unit, key, value);
    }

    public bool SetValue(SiiUnit unit, string key, string value) =>
        unit.Entries.TryGetValue(key, out var line) && SetLine(line, value);

    /// <summary>Every value of a key, in order (all elements of a "key[]" array).</summary>
    public IReadOnlyList<string> GetValues(SiiUnit unit, string key) =>
        unit.Occurrences.Where(o => o.Key == key).Select(o => SplitEntry(_lines[o.Line]).value).ToList();

    /// <summary>Rewrites every occurrence of a key (each element of a "key[]" array) from its old value.</summary>
    public int SetEach(SiiUnit unit, string key, Func<string, string> change)
    {
        var count = 0;
        foreach (var (k, line) in unit.Occurrences)
            if (k == key && SetLine(line, change(SplitEntry(_lines[line]).value))) count++;
        return count;
    }

    bool SetLine(int line, string value)
    {
        var m = Entry.Match(_lines[line]);
        var (_, tail) = SplitEntry(_lines[line]);
        _lines[line] = m.Groups["lead"].Value + m.Groups["key"].Value + m.Groups["sep"].Value
                       + value + tail + m.Groups["eol"].Value;
        return true;
    }

    /// <summary>Value text, and what follows it on the line (whitespace and any comment).</summary>
    static (string value, string tail) SplitEntry(string line)
    {
        var rest = Entry.Match(line).Groups["rest"].Value;
        var inQuotes = false;
        var cut = rest.Length;
        for (var i = 0; i < rest.Length; i++)
        {
            var c = rest[i];
            if (c == '"' && (i == 0 || rest[i - 1] != '\\')) inQuotes = !inQuotes;
            else if (!inQuotes && (c == '#' || (c == '/' && i + 1 < rest.Length && rest[i + 1] == '/')))
            {
                cut = i;
                break;
            }
        }
        var value = rest[..cut].TrimEnd();
        return (value, rest[value.Length..]);
    }

    void Index()
    {
        SiiUnit? current = null;
        SiiUnit? pending = null; // header seen, waiting for "{" on its own line
        for (var i = 0; i < _lines.Count; i++)
        {
            var text = _lines[i].TrimEnd('\r', '\n');
            var trimmed = text.Trim();
            if (current is null)
            {
                var header = UnitHeader.Match(text);
                if (header.Success && header.Groups["type"].Value != "SiiNunit")
                {
                    pending = new SiiUnit(header.Groups["type"].Value, header.Groups["name"].Value, i);
                    if (header.Groups["brace"].Success) { current = pending; pending = null; }
                }
                else if (trimmed == "{" && pending is not null) { current = pending; pending = null; }
                continue;
            }
            if (trimmed == "}")
            {
                _units.Add(current);
                current = null;
                continue;
            }
            if (trimmed.StartsWith('#') || trimmed.StartsWith("//") || trimmed.StartsWith('@')) continue;
            var entry = Entry.Match(_lines[i]);
            if (!entry.Success) continue;
            // Entries maps a key to its first line; "key[]:" lines repeat, so every line is also kept in order.
            current.Entries.TryAdd(entry.Groups["key"].Value, i);
            current.Occurrences.Add((entry.Groups["key"].Value, i));
        }
    }
}

public sealed class SiiUnit(string type, string name, int headerLine)
{
    public string Type { get; } = type;
    public string Name { get; } = name;
    public int HeaderLine { get; } = headerLine;

    /// <summary>Key to line index of its first occurrence; the key includes any [n] or [] suffix.</summary>
    internal Dictionary<string, int> Entries { get; } = new(StringComparer.Ordinal);

    /// <summary>Every entry line in file order, repeated "key[]" lines included.</summary>
    internal List<(string Key, int Line)> Occurrences { get; } = new();

    public IEnumerable<string> Keys => Entries.Keys;

    /// <summary>Keys in file order, once per line (a "key[]" array appears once per element).</summary>
    public IEnumerable<string> AllKeys => Occurrences.Select(o => o.Key);
}
