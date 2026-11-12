using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Ets2u.Core.Safety;

namespace Ets2u.Core.Telemetry;

/// <summary>Layout written by plugin/ets2u_telemetry.cpp. Keep the two in step.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct TelemetryFrame
{
    public const uint ExpectedMagic = 0x54553245; // "E2UT"
    public const uint ExpectedVersion = 1;

    public uint Magic;
    public uint Version;
    public uint Sequence;
    public uint Paused;
    public float SpeedMs;
    public float SpeedLimitMs;

    public readonly bool Valid => Magic == ExpectedMagic && Version == ExpectedVersion;
}

/// <summary>Reads the plugin's shared memory. Returns null when the game (or the plugin) isn't running.</summary>
public sealed class TelemetryReader : IDisposable
{
    public const string MapName = @"Local\Ets2uTelemetry";

    MemoryMappedFile? _map;
    MemoryMappedViewAccessor? _view;

    public TelemetryFrame? Read()
    {
        try
        {
            if (_view is null)
            {
                _map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
                _view = _map.CreateViewAccessor(0, Marshal.SizeOf<TelemetryFrame>(), MemoryMappedFileAccess.Read);
            }
            _view.Read(0, out TelemetryFrame frame);
            return frame.Valid ? frame : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Drop the mapping so a restarted game's new one is picked up.</summary>
    public void Reset()
    {
        _view?.Dispose();
        _map?.Dispose();
        _view = null;
        _map = null;
    }

    public void Dispose() => Reset();
}

public static class SpeedText
{
    const double KmhPerMs = 3.6;
    const double MphPerMs = 2.2369362920544;

    public static int Kmh(float speedMs) => (int)Math.Round(Math.Abs(speedMs) * KmhPerMs);
    public static int Mph(float speedMs) => (int)Math.Round(Math.Abs(speedMs) * MphPerMs);

    public static string Format(float speedMs) =>
        string.Create(CultureInfo.InvariantCulture, $"{Kmh(speedMs)} km/h · {Mph(speedMs)} mph");

    /// <summary>"Limit 80 km/h · 50 mph", or null when there's no limit.</summary>
    public static string? FormatLimit(float limitMs) =>
        limitMs <= 0.1f ? null : string.Create(CultureInfo.InvariantCulture, $"Limit {Kmh(limitMs)} km/h · {Mph(limitMs)} mph");
}

/// <summary>Copies the plugin DLL into the game's plugin folder, or removes it.</summary>
public static class PluginInstaller
{
    public const string FileName = "ets2u_telemetry.dll";

    public static string? TargetPath(string? installDir) =>
        installDir is null ? null : Path.Combine(installDir, "bin", "win_x64", "plugins", FileName);

    /// <summary>The built plugin shipped next to the app (plugin\ets2u_telemetry.dll) or in the repo (plugin\bin).</summary>
    public static string? FindSource() =>
        AppSettings.FindNearApp("plugin", FileName) ?? AppSettings.FindNearApp("plugin", "bin", FileName);

    public static bool IsInstalled(string? installDir) => TargetPath(installDir) is { } p && File.Exists(p);

    public static bool IsCurrent(string? installDir) =>
        TargetPath(installDir) is { } target && FindSource() is { } source && File.Exists(target)
        && File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(source));

    public static void Install(string installDir, SafeWriter writer)
    {
        var source = FindSource() ?? throw new FileNotFoundException("ets2u_telemetry.dll wasn't found next to the app.");
        writer.Write("Install speed plugin", new Dictionary<string, byte[]> { [TargetPath(installDir)!] = File.ReadAllBytes(source) });
    }

    public static void Remove(string installDir, BackupService backups)
    {
        if (SafeWriter.IsGameRunning()) throw new GameRunningException();
        var target = TargetPath(installDir)!;
        if (!File.Exists(target)) return;
        backups.Create("Remove speed plugin", target);
        File.Delete(target);
    }
}
