using System.IO.Compression;
using System.Text.Json;
using Ets2u.Core.Update;

namespace Ets2u.Core.Tests;

public class UpdaterTests
{
    const string Sha = "D5B4B60E1003BAF090952EFD52781ADF7A28C8691EFFE88F18D8B455C5E39766";
    static readonly Version Have = new(0, 14, 0);

    static string Release(string tag = "v0.14.1", string? digest = "sha256:" + Sha, string body = "", bool prerelease = false,
                          string name = "ets2-utilities-v0.14.1-win-x64.zip",
                          string url = "https://github.com/3TW2527/ets2-utilities/releases/download/v0.14.1/ets2-utilities-v0.14.1-win-x64.zip") =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag, draft = false, prerelease, body,
            html_url = "https://github.com/3TW2527/ets2-utilities/releases/tag/" + tag,
            assets = new object[]
            {
                new { name = "notes.txt", browser_download_url = "https://github.com/3TW2527/ets2-utilities/releases/download/x/notes.txt", size = 3 },
                new { name, browser_download_url = url, size = 66_000_000, digest },
            },
        });

    [Fact]
    public void ANewerReleaseWithItsZipIsAnUpdate()
    {
        var u = Assert.IsType<UpdateInfo>(Updater.Parse(Release(), Have));
        Assert.Equal("0.14.1", u.VersionText);
        Assert.Equal(Sha, u.Sha256);
        Assert.EndsWith("ets2-utilities-v0.14.1-win-x64.zip", u.AssetUrl);
        Assert.Equal(66_000_000, u.Size);
    }

    [Fact]
    public void TheSameOrAnOlderVersionIsNot()
    {
        Assert.Null(Updater.Parse(Release("v0.14.0"), Have));
        Assert.Null(Updater.Parse(Release("v0.13.1"), Have));
        // 0.14.0.0 (the assembly's) is the same as the tag's 0.14.0.
        Assert.Null(Updater.Parse(Release("v0.14.0"), new Version(0, 14, 0, 0) is var v ? new Version(v.Major, v.Minor, v.Build) : Have));
        Assert.NotNull(Updater.Parse(Release("v0.15.0"), Have));
        Assert.NotNull(Updater.Parse(Release("v1.0.0"), Have));
    }

    [Fact]
    public void PreReleasesAndOddTagsAreIgnored()
    {
        Assert.Null(Updater.Parse(Release(prerelease: true), Have));
        Assert.Null(Updater.Parse(Release("nightly"), Have));
    }

    [Fact]
    public void TheShaComesFromTheNotesWhenGitHubHasNoDigest()
    {
        var body = $"Install...\n\nSHA-256 of the zip: `{Sha.ToLowerInvariant()}`\n";
        Assert.Equal(Sha, Updater.Parse(Release(digest: null, body: body), Have)?.Sha256);
        // Without either it can't be checked, so it isn't offered.
        Assert.Null(Updater.Parse(Release(digest: null), Have));
        // Notes and file disagreeing: not installed.
        Assert.Null(Updater.Parse(Release(body: $"SHA-256 of the zip: `{new string('0', 64)}`"), Have));
    }

    [Fact]
    public void OnlyThisRepositorysZipIsTaken()
    {
        Assert.Null(Updater.Parse(Release(url: "https://example.com/ets2-utilities-v0.14.1-win-x64.zip"), Have));
        Assert.Null(Updater.Parse(Release(name: "something-else.zip"), Have));
    }

    [Fact]
    public async Task ADownloadIsCheckedAndUnpacked()
    {
        var dir = Directory.CreateTempSubdirectory("ets2u-update-test").FullName;
        try
        {
            var zip = Path.Combine(dir, "release.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(archive.CreateEntry("ets2u.exe").Open())) w.Write("new exe");
                using (var w = new StreamWriter(archive.CreateEntry("plugin/ets2u_telemetry.dll").Open())) w.Write("new plugin");
            }
            var good = new UpdateInfo(new Version(0, 14, 1), "page", new Uri(zip).AbsoluteUri, 0, Updater.Sha256(zip));
            using var http = Updater.CreateClient();
            var staged = await Updater.DownloadAsync(http, good, root: Path.Combine(dir, "update"));
            Assert.Equal("new plugin", File.ReadAllText(Path.Combine(staged, "plugin", "ets2u_telemetry.dll")));

            // The new files go over the old ones; others there are kept.
            var app = Path.Combine(dir, "app");
            Directory.CreateDirectory(Path.Combine(app, "plugin"));
            File.WriteAllText(Path.Combine(app, "ets2u.exe"), "old exe");
            File.WriteAllText(Path.Combine(app, "mine.txt"), "kept");
            Updater.CopyOver(staged, app);
            Assert.Equal("new exe", File.ReadAllText(Path.Combine(app, "ets2u.exe")));
            Assert.Equal("new plugin", File.ReadAllText(Path.Combine(app, "plugin", "ets2u_telemetry.dll")));
            Assert.Equal("kept", File.ReadAllText(Path.Combine(app, "mine.txt")));

            var tampered = good with { Sha256 = new string('A', 64) };
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Updater.DownloadAsync(http, tampered, root: Path.Combine(dir, "update")));
            Assert.Contains("SHA-256", ex.Message);
            Assert.False(Directory.Exists(Path.Combine(dir, "update", "0.14.1", "app")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
