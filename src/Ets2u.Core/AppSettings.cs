using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ets2u.Core;

/// <summary>Where the route advisor map goes when moved.</summary>
public enum MapSide
{
    /// <summary>Opposite of the game's default: still follows left- or right-hand drive.</summary>
    Swap,
    Left,
    Right,
}

public enum OverlayCorner { TopLeft, TopCenter, TopRight, BottomLeft, BottomRight }

/// <summary>The user's choices, saved to %APPDATA%\ets2u\settings.json.</summary>
public sealed class AppSettings
{
    public bool NoDamage { get; set; }
    public double IncomeMultiplier { get; set; } = 1;
    public double XpMultiplier { get; set; } = 1;
    public bool MapPositionEnabled { get; set; }
    public MapSide MapSide { get; set; } = MapSide.Swap;
    public bool SpeedOverlay { get; set; }
    public OverlayCorner OverlayCorner { get; set; } = OverlayCorner.TopRight;
    public double OverlayScale { get; set; } = 1;
    public bool OverlayShowLimit { get; set; } = true;
    public bool CoachNoPenalties { get; set; }
    public bool CoachFullSatisfaction { get; set; }

    /// <summary>The game version the mods were last built from; a change means they're rebuilt on start.</summary>
    public DateTime? ModsBuiltForGameStamp { get; set; }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FilePath => Path.Combine(AppDirs.Settings, "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= FilePath;
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>A file in &lt;folder&gt; next to the app or in one of its parent folders (covers dev builds).</summary>
    public static string? FindNearApp(params string[] relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, .. relative]);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
