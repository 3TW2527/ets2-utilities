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
    public const uint ExpectedVersion = 4;

    public uint Magic;
    public uint Version;
    public uint Sequence;
    public uint Paused;
    public float SpeedMs;
    public float SpeedLimitMs;
    /// <summary>Forward acceleration in g, smoothed by the plugin over ~0.3 s (negative when slowing).</summary>
    public float AccelForwardG;
    /// <summary>Acceleration towards the truck's right in g, smoothed the same.</summary>
    public float AccelRightG;
    public MouseDriveState MouseState;
    /// <summary>-1 full left .. 1 full right.</summary>
    public float MouseSteer;
    public float MouseThrottle;
    public float MouseBrake;
    /// <summary>The truck config's id, e.g. "vehicle.man.lions_24"; empty before a truck is loaded.</summary>
    public TruckIdBytes TruckIdRaw;
    /// <summary>Odd while the plugin is writing the job; read <see cref="JobInfo"/> only when it's even.</summary>
    public uint JobSeq;
    /// <summary>0 no job, 1 a truck job, 2 a bus job.</summary>
    public uint JobKind;
    public JobInfoBytes JobInfoRaw;
    /// <summary>Gameplay events so far (job delivered, cancelled...); the latest is in slot (count - 1) % 4.</summary>
    public uint GameplayCount;
    public GameplayBytes GameplayRaw;

    public readonly string TruckId
    {
        get
        {
            ReadOnlySpan<byte> bytes = TruckIdRaw;
            var end = bytes.IndexOf((byte)0);
            return System.Text.Encoding.ASCII.GetString(end < 0 ? bytes : bytes[..end]);
        }
    }

    public readonly bool Valid => Magic == ExpectedMagic && Version == ExpectedVersion;

    /// <summary>The job's attributes as "name=value" lines.</summary>
    public readonly string JobInfo
    {
        get
        {
            ReadOnlySpan<byte> bytes = JobInfoRaw;
            return Text(bytes);
        }
    }

    /// <summary>A gameplay event: its id on the first line, then its attributes as "name=value" lines.</summary>
    public readonly string Gameplay(uint slot)
    {
        ReadOnlySpan<byte> all = GameplayRaw;
        return Text(all.Slice((int)(slot % GameplaySlots) * GameplaySlotSize, GameplaySlotSize));
    }

    public const int GameplaySlots = 4, GameplaySlotSize = 512;

    static string Text(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }
}

public enum MouseDriveState : uint { Off, Steering, Held, NoFeed }

[System.Runtime.CompilerServices.InlineArray(64)]
public struct TruckIdBytes
{
    byte _first;
}

[System.Runtime.CompilerServices.InlineArray(1024)]
public struct JobInfoBytes
{
    byte _first;
}

[System.Runtime.CompilerServices.InlineArray(TelemetryFrame.GameplaySlots * TelemetryFrame.GameplaySlotSize)]
public struct GameplayBytes
{
    byte _first;
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
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or ArgumentOutOfRangeException or IOException)
        {
            // (an older plugin's smaller memory can't hold this layout)
            Reset();
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

/// <summary>
/// The plugin's frame while the game is running and not paused. The plugin bumps Sequence every
/// simulated frame; when it stops the game has closed or is loading.
/// </summary>
public sealed class LiveTelemetry : IDisposable
{
    readonly TelemetryReader _reader = new();
    uint _lastSequence;
    DateTime _lastChange = DateTime.MinValue;

    /// <summary>
    /// The plugin's last frame even while the game is paused (a job is delivered on a menu screen), or
    /// null when the game or the plugin isn't running.
    /// </summary>
    public TelemetryFrame? Raw { get; private set; }

    /// <summary>The frame, or null when the game isn't running, is paused, or the plugin is missing or older.</summary>
    public TelemetryFrame? Read()
    {
        var frame = _reader.Read();
        Raw = frame;
        if (frame is not { } f)
        {
            _reader.Reset();
            return null;
        }
        if (f.Sequence != _lastSequence)
        {
            _lastSequence = f.Sequence;
            _lastChange = DateTime.UtcNow;
        }
        if (DateTime.UtcNow - _lastChange < TimeSpan.FromSeconds(2) && f.Paused == 0) return f;
        if (DateTime.UtcNow - _lastChange > TimeSpan.FromSeconds(10)) _reader.Reset();
        return null;
    }

    public void Dispose() => _reader.Dispose();
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
