using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ets2u.Core.Liveries;

public enum DownloadOutcome { Done, NeedsSignIn, NotOwned, Failed, Cancelled }

/// <param name="Account">The Steam account name DepotDownloader logged in as, when it said.</param>
public sealed record DownloadResult(DownloadOutcome Outcome, string Message, string? Account = null);

/// <summary>What a running download reports back.</summary>
public sealed class DownloadEvents
{
    /// <summary>A sign-in QR code to show (true = dark module), or null once it's no longer needed.</summary>
    public Action<bool[,]?>? QrCode { get; init; }

    /// <summary>0–100.</summary>
    public Action<double>? Progress { get; init; }

    public Action<string>? Line { get; init; }
}

/// <summary>
/// Runs DepotDownloader (github.com/SteamRE/DepotDownloader, GPL-2.0) as its own program to download
/// Workshop items without subscribing to them. It signs in to Steam as its own session, so the Steam
/// client doesn't need to run. The first sign-in is a QR code scanned with the Steam mobile app;
/// DepotDownloader then remembers the login, and the app never sees a password.
/// </summary>
public static class DepotDownloaderTool
{
    public const string Version = "3.4.0";
    public const string ZipUrl =
        "https://github.com/SteamRE/DepotDownloader/releases/download/DepotDownloader_3.4.0/DepotDownloader-windows-x64.zip";

    /// <summary>SHA-256 of <see cref="ZipUrl"/>: a download that doesn't match is thrown away.</summary>
    public const string ZipSha256 = "41c9e9f0df54b3ad02e67a11726756e5c73283bd7c2e1b04acfa5ae4c2ed3767";

    public const string ExeName = "DepotDownloader.exe";

    /// <summary>Where the app installs its own copy.</summary>
    public static string InstallDir => Path.Combine(AppDirs.Local, "DepotDownloader");

    /// <summary>
    /// A copy to use: one installed with winget first (it may already be signed in), then one shipped
    /// beside the app, then the app's own, then any on PATH.
    /// </summary>
    public static string? Find()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>();
        var winget = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
            candidates.AddRange(Directory.EnumerateDirectories(winget, "SteamRE.DepotDownloader*").Select(d => Path.Combine(d, ExeName)));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "DepotDownloader", ExeName));
        candidates.Add(Path.Combine(InstallDir, ExeName));
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), ExeName)));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Downloads the pinned release into <see cref="InstallDir"/>, checking its hash.</summary>
    public static async Task<string> InstallAsync(Action<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(AppDirs.Local);
        var zipPath = Path.Combine(AppDirs.Local, "DepotDownloader.zip.tmp");
        try
        {
            using (var response = await WorkshopApi.Http.GetAsync(ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var from = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var to = File.Create(zipPath);
                var buffer = new byte[81920];
                long done = 0;
                int n;
                while ((n = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await to.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (total > 0) progress?.Invoke(done * 100.0 / total);
                }
            }
            await using (var check = File.OpenRead(zipPath))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false));
                if (!hash.Equals(ZipSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The DepotDownloader download didn't match the expected file, so it wasn't installed.");
            }
            if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, true);
            ZipFile.ExtractToDirectory(zipPath, InstallDir);
            var exe = Path.Combine(InstallDir, ExeName);
            if (!File.Exists(exe)) throw new InvalidDataException("The DepotDownloader download had no " + ExeName + ".");
            return exe;
        }
        finally
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
        }
    }

    /// <summary>
    /// Downloads Workshop item <paramref name="itemId"/> into <paramref name="targetDir"/>. With an
    /// <paramref name="account"/> it uses DepotDownloader's remembered login for it; without one it signs in
    /// with a QR code (passed to <see cref="DownloadEvents.QrCode"/>) and remembers that login.
    /// </summary>
    public static async Task<DownloadResult> DownloadAsync(string exe, string itemId, string targetDir, string? account,
                                                           DownloadEvents events, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ConsoleEncoding(),
            StandardErrorEncoding = ConsoleEncoding(),
            WorkingDirectory = Directory.CreateDirectory(AppDirs.Local).FullName,
        };
        foreach (var arg in Arguments(itemId, targetDir, account)) psi.ArgumentList.Add(arg);

        var parser = new OutputParser(events);
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var lockObj = new object();
        void OnLine(string? line)
        {
            if (line is null) return;
            bool stop;
            lock (lockObj) stop = parser.Feed(line);
            if (stop) Kill(process);
        }
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.Start();
        _running = process;
        // Nothing is ever typed in: a prompt for a password or code ends the run (see OutputParser).
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            return new DownloadResult(DownloadOutcome.Cancelled, "Cancelled.");
        }
        finally
        {
            Interlocked.CompareExchange(ref _running, null, process);
            events.QrCode?.Invoke(null);
        }
        process.WaitForExit(); // flush the output events

        lock (lockObj) return parser.Result(process.ExitCode, HasContent(targetDir));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern uint GetOEMCP();

    /// <summary>
    /// What DepotDownloader writes in: its hidden console uses the system's OEM code page (950 on a
    /// Traditional Chinese Windows, where "█" is two bytes), not UTF-8.
    /// </summary>
    public static Encoding ConsoleEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            var cp = OperatingSystem.IsWindows() ? (int)GetOEMCP() : 65001;
            return cp == 65001 ? new UTF8Encoding(false) : Encoding.GetEncoding(cp);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or EntryPointNotFoundException or DllNotFoundException)
        {
            return new UTF8Encoding(false);
        }
    }

    static Process? _running;

    /// <summary>Stops a download that's running, e.g. when the app closes.</summary>
    public static void StopRunning()
    {
        if (Volatile.Read(ref _running) is { } p) Kill(p);
    }

    public static IReadOnlyList<string> Arguments(string itemId, string targetDir, string? account)
    {
        var args = new List<string> { "-app", Workshop.AppId, "-pubfile", itemId, "-dir", targetDir, "-remember-password" };
        if (account is null) args.Add("-qr");
        else args.AddRange(["-username", account]);
        return args;
    }

    /// <summary>Anything downloaded besides DepotDownloader's own bookkeeping folder.</summary>
    public static bool HasContent(string dir) =>
        Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir)
            .Any(e => !Path.GetFileName(e).Equals(".DepotDownloader", StringComparison.OrdinalIgnoreCase));

    static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Reads DepotDownloader's console output line by line.</summary>
    public sealed class OutputParser(DownloadEvents events)
    {
        const string QrHeader = "Use the Steam Mobile App to sign in with this QR code:";

        readonly List<string> _qrLines = new();
        bool _inQr;
        string? _account;
        string? _problem;
        DownloadOutcome? _outcome;
        string _last = "";

        public string? Account => _account;

        /// <summary>Takes one line; true means stop the program (it's waiting for input that never comes).</summary>
        public bool Feed(string line)
        {
            events.Line?.Invoke(line);
            if (_inQr)
            {
                if (IsQrRow(line) && (_qrLines.Count == 0 || line.Length == _qrLines[0].Length))
                {
                    _qrLines.Add(line);
                    // DepotDownloader prints nothing more while it waits for the scan, so the code has to be
                    // shown as soon as its last row is in: it's square, each module 2 characters wide.
                    if (_qrLines.Count >= 21 && line.Length == _qrLines.Count * 2)
                    {
                        _inQr = false;
                        if (ParseQr(_qrLines) is { } done) events.QrCode?.Invoke(done);
                    }
                    return false;
                }
                _inQr = false;
                if (ParseQr(_qrLines) is { } grid) events.QrCode?.Invoke(grid);
            }
            if (line.Contains(QrHeader, StringComparison.Ordinal))
            {
                _inQr = true;
                _qrLines.Clear();
                return false;
            }
            if (!string.IsNullOrWhiteSpace(line)) _last = line.Trim();

            if (Regex.Match(line, @"-username (\S+) -remember-password") is { Success: true } next) _account = next.Groups[1].Value;
            if (Regex.Match(line, @"Logging '([^']+)' into Steam3") is { Success: true } logging) _account ??= logging.Groups[1].Value;
            if (line.Contains("Got ", StringComparison.Ordinal) && line.Contains("licenses for account", StringComparison.Ordinal))
                events.QrCode?.Invoke(null); // signed in

            if (Regex.Match(line, @"^\s*(\d+(?:[.,]\d+)?)%\s") is { Success: true } pct
                && double.TryParse(pct.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var p))
                events.Progress?.Invoke(Math.Clamp(p, 0, 100));

            if (line.Contains("Enter account password", StringComparison.Ordinal)
                || line.Contains("Access token was rejected", StringComparison.Ordinal)
                || line.Contains("has stored credentials", StringComparison.Ordinal))
            {
                _outcome = DownloadOutcome.NeedsSignIn;
                _problem = "The saved Steam sign-in isn't valid any more.";
                return true;
            }
            if (line.Contains("2 factor auth code", StringComparison.Ordinal)
                || line.Contains("authentication code sent to your email", StringComparison.Ordinal))
            {
                _outcome = DownloadOutcome.NeedsSignIn;
                _problem = "Steam asked for a Steam Guard code.";
                return true;
            }
            if (line.Contains("is not available from this account", StringComparison.Ordinal))
            {
                _outcome = DownloadOutcome.NotOwned;
                _problem = "This Steam account doesn't own Euro Truck Simulator 2.";
            }
            if (line.Contains("Unable to login to Steam3", StringComparison.Ordinal) || line.Contains("Failed to authenticate", StringComparison.Ordinal))
                _problem ??= line.Trim();
            if (line.Contains("Unable to locate manifest", StringComparison.Ordinal) || line.Contains("unsupported file type", StringComparison.Ordinal))
                _problem ??= "Steam has nothing to download for this item.";
            return false;
        }

        public DownloadResult Result(int exitCode, bool hasContent)
        {
            if (_outcome is DownloadOutcome.NeedsSignIn or DownloadOutcome.NotOwned)
                return new DownloadResult(_outcome.Value, _problem!, _account);
            if (exitCode == 0 && hasContent) return new DownloadResult(DownloadOutcome.Done, "Downloaded.", _account);
            return new DownloadResult(DownloadOutcome.Failed, _problem ?? (_last.Length > 0 ? _last : $"DepotDownloader stopped (code {exitCode})."), _account);
        }

        /// <summary>A row of a QR code drawn in text: blocks and spaces only.</summary>
        static bool IsQrRow(string line) =>
            line.Length >= 20 && line.All(c => c is ' ' or '█' or '▀' or '▄' or '�' or '?' || c > 0x7F && !char.IsLetterOrDigit(c));

        /// <summary>
        /// The QR code's modules. Each row is one module tall and the code is square, so each module is
        /// line length ÷ row count characters wide (2 for "██").
        /// </summary>
        public static bool[,]? ParseQr(IReadOnlyList<string> rows)
        {
            var n = rows.Count;
            if (n < 21 || rows[0].Length % n != 0) return null;
            var w = rows[0].Length / n;
            var grid = new bool[n, n];
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                    grid[y, x] = rows[y][x * w] != ' ';
            return grid;
        }
    }

    // ---------- the remembered logins ----------

    /// <summary>
    /// Account names DepotDownloader has a remembered login for. It keeps them in .NET isolated storage
    /// (%LocalAppData%\IsolatedStorage\…\account.config: deflated protobuf, field 4 = account → token).
    /// Only the names are read. Newest file first.
    /// </summary>
    public static IReadOnlyList<string> SavedAccounts()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IsolatedStorage");
        if (!Directory.Exists(root)) return [];
        try
        {
            return Directory.EnumerateFiles(root, "account.config", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .SelectMany(f =>
                {
                    try { return ReadAccountNames(File.ReadAllBytes(f)); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return []; }
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static IReadOnlyList<string> ReadAccountNames(byte[] deflated)
    {
        using var input = new DeflateStream(new MemoryStream(deflated), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        input.CopyTo(raw);
        var names = new List<string>();
        foreach (var (field, value) in Fields(raw.ToArray()))
        {
            if (field != 4 || value is null) continue;
            // A map entry: field 1 = key.
            foreach (var (f, v) in Fields(value))
                if (f == 1 && v is not null) names.Add(Encoding.UTF8.GetString(v));
        }
        return names;
    }

    /// <summary>A protobuf message's fields; length-delimited ones with their bytes, others with null.</summary>
    static IEnumerable<(int Field, byte[]? Value)> Fields(byte[] data)
    {
        var i = 0;
        ulong Varint()
        {
            ulong v = 0;
            for (var shift = 0; ; shift += 7)
            {
                if (i >= data.Length || shift > 63) throw new InvalidDataException("Bad protobuf varint.");
                var b = data[i++];
                v |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80) return v;
            }
        }
        while (i < data.Length)
        {
            var key = Varint();
            var field = (int)(key >> 3);
            switch (key & 7)
            {
                case 0: Varint(); yield return (field, null); break;
                case 1: i += 8; yield return (field, null); break;
                case 5: i += 4; yield return (field, null); break;
                case 2:
                    var len = (int)Varint();
                    if (len < 0 || i + len > data.Length) throw new InvalidDataException("Bad protobuf length.");
                    yield return (field, data[i..(i + len)]);
                    i += len;
                    break;
                default: throw new InvalidDataException("Unknown protobuf wire type.");
            }
        }
    }
}
