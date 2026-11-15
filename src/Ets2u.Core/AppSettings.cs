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

/// <summary>What switches mouse driving on and off in the game.</summary>
public enum MouseToggle { F8, Insert, MiddleButton }

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
    /// <summary>OMSI-style mouse driving, done by the plugin (it reads mouse_drive.ini).</summary>
    public bool MouseDrive { get; set; }
    public MouseToggle MouseToggle { get; set; } = MouseToggle.F8;
    /// <summary>1 = the window's width is full lock at walking pace (OMSI); 2 = half the width.</summary>
    public double MouseSensitivity { get; set; } = 1;
    public double MousePedalStrength { get; set; } = 1;
    public bool MouseIndicator { get; set; } = true;
    /// <summary>Each profile's own controls before mouse driving changed them, by profile folder.</summary>
    public Dictionary<string, Telemetry.ControlsOriginal> MouseControlsOriginals { get; set; } = new();

    /// <summary>GreenRoad-style alerts for harsh driving and speeding, in coaches and in trucks.</summary>
    public bool CoachAlerts { get; set; }
    public bool TruckAlerts { get; set; }
    public Telemetry.DrivingThresholds CoachThresholds { get; set; } = Telemetry.DrivingThresholds.Coach;
    public Telemetry.DrivingThresholds TruckThresholds { get; set; } = Telemetry.DrivingThresholds.Truck;
    public bool SpeedingAlerts { get; set; } = true;

    /// <summary>The floating live g-force meter, and where it was dragged to (null = the default place).</summary>
    public bool GForceMeter { get; set; }
    public double? GForceMeterLeft { get; set; }
    public double? GForceMeterTop { get; set; }
    /// <summary>Which defaults the saved thresholds came from; older ones are replaced on load.</summary>
    public int AlertDefaults { get; set; }
    public const int CurrentAlertDefaults = 6;

    /// <summary>v0.7 previews' single switch; read once and turned into <see cref="CoachAlerts"/> and <see cref="TruckAlerts"/>.</summary>
    public bool? DrivingAlerts { get; set; }

    /// <summary>Factors that no longer lower coach passenger satisfaction.</summary>
    public Features.CoachFactor CoachIgnored { get; set; }
    public bool CoachFullSatisfaction { get; set; }

    /// <summary>When passengers notice g-forces; null = the game's own limits.</summary>
    public Features.CoachGLimits? CoachLimits { get; set; }

    /// <summary>Where each vehicle's alert thresholds come from, and how much looser they are.</summary>
    public Telemetry.AlertSource CoachAlertSource { get; set; }
    public Telemetry.AlertRelax CoachAlertRelax { get; set; }
    public Telemetry.AlertSource TruckAlertSource { get; set; }
    public Telemetry.AlertRelax TruckAlertRelax { get; set; }

    /// <summary>v0.8 previews' sync switches; read once and turned into the sources above.</summary>
    public bool? CoachAlertSync { get; set; }
    public string? TruckAlertSync { get; set; }

    /// <summary>The coach limits the alerts sync to: the player's, or the stock ones (harsh in every direction).</summary>
    [JsonIgnore]
    public Features.CoachGLimits AlertLimits => CoachLimits ?? Features.CoachGLimits.Stock;

    /// <summary>The thresholds the alerts use in a coach.</summary>
    [JsonIgnore]
    public Telemetry.DrivingThresholds EffectiveCoachThresholds => Telemetry.DrivingThresholds.Resolve(
        CoachAlertSource, CoachAlertRelax, Telemetry.DrivingThresholds.Coach, CoachThresholds, AlertLimits);

    /// <summary>The thresholds the alerts use in a truck.</summary>
    [JsonIgnore]
    public Telemetry.DrivingThresholds EffectiveTruckThresholds => Telemetry.DrivingThresholds.Resolve(
        TruckAlertSource, TruckAlertRelax, Telemetry.DrivingThresholds.Truck, TruckThresholds, AlertLimits);

    /// <summary>Text on the coaches' front destination display.</summary>
    public Features.BusDisplaySettings BusDisplay { get; set; } = new();

    /// <summary>Liveries from several mods packed into one.</summary>
    public Liveries.LiveryPackSettings LiveryPack { get; set; } = new();

    /// <summary>v0.3's single "satisfaction never drops" switch; read once and turned into <see cref="CoachIgnored"/>.</summary>
    public bool? CoachNoPenalties { get; set; }

    /// <summary>Suffix on this player's generated mod file names; made once (see <see cref="Mods.ModTag"/>).</summary>
    public string? ModTag { get; set; }

    /// <summary>The game version the mods were last built from; a change means they're rebuilt on start.</summary>
    public DateTime? ModsBuiltForGameStamp { get; set; }

    /// <summary>The app version that last built the mods; a newer app rebuilds them on start.</summary>
    public string? ModsBuiltByApp { get; set; }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FilePath => Path.Combine(AppDirs.Settings, "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= FilePath;
        try
        {
            var settings = File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new() : new();
            if (settings.CoachNoPenalties == true) settings.CoachIgnored = Features.CoachFactor.All;
            settings.CoachNoPenalties = null;
            if (settings.DrivingAlerts == true) settings.CoachAlerts = settings.TruckAlerts = true;
            settings.DrivingAlerts = null;
            if (settings.AlertDefaults < 2)
            {
                // The first thresholds alerted on nearly every start and stop.
                settings.CoachThresholds = Telemetry.DrivingThresholds.Coach;
                settings.TruckThresholds = Telemetry.DrivingThresholds.Truck;
            }
            // v0.8: each vehicle picks a source. Thresholds still on a built-in default follow today's
            // default; ones the player typed become custom. The previews' sync switches carry over.
            if (settings.AlertDefaults < 6)
            {
                settings.CoachAlertSource = settings.CoachAlertSync == true ? Telemetry.AlertSource.CoachMod
                    : Telemetry.DrivingThresholds.IsBuiltIn(settings.CoachThresholds) ? Telemetry.AlertSource.Default
                    : Telemetry.AlertSource.Custom;
                var truckSync = settings.TruckAlertSync is "Exact" or "Plus10" or "Plus20";
                settings.TruckAlertSource = truckSync ? Telemetry.AlertSource.CoachMod
                    : Telemetry.DrivingThresholds.IsBuiltIn(settings.TruckThresholds) ? Telemetry.AlertSource.Default
                    : Telemetry.AlertSource.Custom;
                settings.TruckAlertRelax = settings.TruckAlertSync switch
                {
                    "Plus10" => Telemetry.AlertRelax.Plus10,
                    "Plus20" => Telemetry.AlertRelax.Plus20,
                    _ => Telemetry.AlertRelax.None,
                };
                if (settings.CoachAlertSource == Telemetry.AlertSource.Default) settings.CoachThresholds = Telemetry.DrivingThresholds.Coach;
                if (settings.TruckAlertSource == Telemetry.AlertSource.Default) settings.TruckThresholds = Telemetry.DrivingThresholds.Truck;
            }
            settings.CoachAlertSync = null;
            settings.TruckAlertSync = null;
            settings.AlertDefaults = CurrentAlertDefaults;
            return settings;
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
