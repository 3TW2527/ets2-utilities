using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ets2u.Core.Update;

/// <summary>A release newer than the running app, from GitHub's latest release.</summary>
/// <param name="Version">Its version, e.g. 0.14.1.</param>
/// <param name="Page">The release's page, for "What's new".</param>
/// <param name="AssetUrl">The win-x64 zip to download.</param>
/// <param name="Size">The zip's size in bytes.</param>
/// <param name="Sha256">The zip's SHA-256 (upper-case hex), from GitHub's digest of the file or the release notes.</param>
public sealed record UpdateInfo(Version Version, string Page, string AssetUrl, long Size, string Sha256)
{
    public string VersionText => Updater.Text(Version);
}

/// <summary>
/// Updates the app from its GitHub releases: finds a newer release, downloads its zip and checks its SHA-256,
/// unpacks it next to the app's data, then the new copy of the app (run from there with
/// <see cref="FinishArg"/>) waits for this one to close, copies itself over it and starts it again.
/// </summary>
public static class Updater
{
    public const string Repository = "3TW2527/ets2-utilities";
    public const string LatestUrl = $"https://api.github.com/repos/{Repository}/releases/latest";
    const string DownloadPrefix = $"https://github.com/{Repository}/releases/download/";

    /// <summary>Run by the new copy: <c>--finish-update "target folder" pid</c>.</summary>
    public const string FinishArg = "--finish-update";

    /// <summary>Passed to the updated app when it's started again.</summary>
    public const string UpdatedArg = "--updated";

    /// <summary>For trying the update without a release: a JSON file in GitHub's format, whose asset may be a local zip.</summary>
    public const string FeedVariable = "ETS2U_UPDATE_FEED";

    public static string Root => Path.Combine(AppDirs.Local, "update");

    static readonly Regex AssetName = new(@"^ets2-utilities-v[\d.]+-win-x64\.zip$", RegexOptions.IgnoreCase);
    static readonly Regex NotesSha = new(@"SHA-256[^`\n]*`([0-9A-Fa-f]{64})`");

    /// <summary>The app's version (ets2u.exe's) as three parts, 0.14.0, as releases are tagged.</summary>
    public static Version Current
    {
        get
        {
            var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string Text(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

    /// <summary>
    /// The release in GitHub's "latest release" JSON, if it's newer than <paramref name="current"/> and has a
    /// win-x64 zip with a known SHA-256; null otherwise. Drafts and pre-releases are ignored.
    /// </summary>
    public static UpdateInfo? Parse(string json, Version current, bool localAssets = false)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (Bool(r, "draft") || Bool(r, "prerelease")) return null;
        var tag = Str(r, "tag_name")?.TrimStart('v', 'V');
        if (tag is null || !Version.TryParse(tag, out var parsed)) return null;
        var version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        if (version <= current) return null;
        if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = Str(a, "name");
            var url = Str(a, "browser_download_url");
            if (name is null || url is null || !AssetName.IsMatch(name)) continue;
            if (!localAssets && !url.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var digest = Str(a, "digest") is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? d[7..].ToUpperInvariant() : null;
            var noted = Str(r, "body") is { } body && NotesSha.Match(body) is { Success: true } m ? m.Groups[1].Value.ToUpperInvariant() : null;
            // Both should agree; a release whose notes and file disagree isn't installed.
            if (digest is not null && noted is not null && digest != noted) return null;
            var sha = digest ?? noted;
            if (sha is null || sha.Length != 64) return null;
            var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
            return new UpdateInfo(version, Str(r, "html_url") ?? $"https://github.com/{Repository}/releases", url, size, sha);
        }
        return null;
    }

    static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"ets2u-updater/{Text(Current)}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>A newer release, or null when there's none (or it can't be told).</summary>
    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, CancellationToken ct = default)
    {
        if (Environment.GetEnvironmentVariable(FeedVariable) is { Length: > 0 } feed)
            return Parse(await File.ReadAllTextAsync(feed, ct), Current, localAssets: true);
        using var response = await http.GetAsync(LatestUrl, ct);
        if (!response.IsSuccessStatusCode) return null;
        return Parse(await response.Content.ReadAsStringAsync(ct), Current);
    }

    /// <summary>
    /// Downloads the release's zip, checks its SHA-256 and unpacks it; returns the folder with the new
    /// ets2u.exe in it. <paramref name="progress"/> gets 0..1.
    /// </summary>
    public static async Task<string> DownloadAsync(HttpClient http, UpdateInfo update, IProgress<double>? progress = null,
                                                   CancellationToken ct = default, string? root = null)
    {
        var dir = Path.Combine(root ?? Root, update.VersionText);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, "download.zip");
        var uri = new Uri(update.AssetUrl);
        if (uri.IsFile)
        {
            File.Copy(uri.LocalPath, zip);
            progress?.Report(1);
        }
        else
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? update.Size;
            await using var from = await response.Content.ReadAsStreamAsync(ct);
            await using var to = File.Create(zip);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await from.ReadAsync(buffer, ct)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
            }
        }

        var sha = Sha256(zip);
        if (sha != update.Sha256)
            throw new InvalidDataException($"The download doesn't match the release's SHA-256 ({sha[..12]}… instead of {update.Sha256[..12]}…), so it wasn't installed.");
        var app = Path.Combine(dir, "app");
        ZipFile.ExtractToDirectory(zip, app);
        File.Delete(zip);
        if (!File.Exists(Path.Combine(app, "ets2u.exe")))
            throw new InvalidDataException("The release's zip has no ets2u.exe in it, so it wasn't installed.");
        return app;
    }

    public static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f));
    }

    /// <summary>Whether the app can replace its own files in <paramref name="dir"/> (not, say, under Program Files).</summary>
    public static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".ets2u-update-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Starts the new copy, which finishes the update once this process has closed.</summary>
    public static void Start(string staged, string target)
    {
        var info = new ProcessStartInfo(Path.Combine(staged, "ets2u.exe")) { UseShellExecute = false, WorkingDirectory = staged };
        info.ArgumentList.Add(FinishArg);
        info.ArgumentList.Add(target);
        info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Process.Start(info);
    }

    /// <summary>
    /// Run by the new copy: waits for the old app to close, copies the new files over it and starts it.
    /// Returns what went wrong, or null.
    /// </summary>
    public static string? Finish(string staged, string target, int pid, TimeSpan? wait = null)
    {
        try
        {
            try
            {
                using var old = Process.GetProcessById(pid);
                old.WaitForExit(wait ?? TimeSpan.FromSeconds(60));
            }
            catch (ArgumentException) { } // already closed
            CopyOver(staged, target);
            AppDirs.Log($"Updated the app in {target} to {Text(Current)}.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppDirs.Log("Update: " + ex);
            return ex.Message;
        }
    }

    /// <summary>Copies every file in <paramref name="from"/> over <paramref name="to"/>, retrying a file that's still in use.</summary>
    public static void CopyOver(string from, string to, int attempts = 40)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            for (var i = 1; ; i++)
            {
                try
                {
                    File.Copy(file, dest, overwrite: true);
                    break;
                }
                catch (IOException) when (i < attempts)
                {
                    Thread.Sleep(500);
                }
            }
        }
    }

    /// <summary>Deletes what an update left behind, unless the app is running from it.</summary>
    public static void CleanUp()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            var here = Path.GetFullPath(AppContext.BaseDirectory);
            if (here.StartsWith(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase)) return;
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppDirs.Log("Cleaning up the update: " + ex.Message);
        }
    }
}
