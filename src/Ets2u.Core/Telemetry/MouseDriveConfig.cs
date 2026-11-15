using System.Globalization;
using System.Text;

namespace Ets2u.Core.Telemetry;

/// <summary>
/// The plugin's mouse driving settings, as an ini file it reads at start and re-reads within a
/// second of a change, so they apply while the game runs and without this app open.
/// </summary>
public static class MouseDriveConfig
{
    public static string FilePath => Path.Combine(AppDirs.Settings, "mouse_drive.ini");

    public static string Format(AppSettings s)
    {
        var (toggle, vk) = s.MouseToggle switch
        {
            MouseToggle.MiddleButton => ("middle", 0),
            MouseToggle.Insert => ("key", 0x2D),
            _ => ("key", 0x77), // F8: not bound by the game
        };
        var c = CultureInfo.InvariantCulture;
        return new StringBuilder()
            .Append("; Written by ETS2 Utilities; read by ets2u_telemetry.dll. Change these in the app.\r\n")
            .Append("[mouse_drive]\r\n")
            .Append(c, $"enabled={(s.MouseDrive ? 1 : 0)}\r\n")
            .Append(c, $"toggle={toggle}\r\n")
            .Append(c, $"toggle_vk={vk}\r\n")
            .Append(c, $"sensitivity={Math.Clamp(s.MouseSensitivity, 0.2, 5):0.##}\r\n")
            .Append(c, $"pedal_strength={Math.Clamp(s.MousePedalStrength, 0.25, 4):0.##}\r\n")
            .Append("pedal_deadzone=0.05\r\n")
            .Append("smooth=1\r\n")
            .ToString();
    }

    public static void Write(AppSettings s, string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = Format(s);
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    public static string ToggleName(MouseToggle t) => t switch
    {
        MouseToggle.MiddleButton => "middle mouse button",
        MouseToggle.Insert => "Insert",
        _ => "F8",
    };
}
