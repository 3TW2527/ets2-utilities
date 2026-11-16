using System.IO.Compression;
using System.Text;
using Ets2u.Core.Liveries;
using Ets2u.Core.Paths;

namespace Ets2u.Core.Tests;

public class WorkshopDownloadTests
{
    const string DetailsJson = """
        {"response":{"result":1,"resultcount":3,"publishedfiledetails":[
          {"publishedfileid":"3839519582","result":1,"creator":"76561198135332932","consumer_app_id":227300,
           "file_size":"22470488","file_url":"","hcontent_file":"3003690771580248544","preview_url":"https://images.steamusercontent.com/ugc/1/",
           "title":"Sun Bus HK Skin Pack","time_updated":1794664096,"visibility":0,"banned":0,
           "tags":[{"tag":"Paintjobs"},{"tag":"Scania"}]},
          {"publishedfileid":"111111111","result":1,"consumer_app_id":1066780,"hcontent_file":"5","title":"A train",
           "time_updated":5,"visibility":0,"banned":0,"tags":[]},
          {"publishedfileid":"222222222","result":9}
        ]}}
        """;

    [Fact]
    public void ReadsWorkshopDetails()
    {
        var items = WorkshopApi.ParseDetails(DetailsJson);
        Assert.Equal(3, items.Count);

        var livery = items[0];
        Assert.True(livery.Found);
        Assert.True(livery.ForEts2);
        Assert.True(livery.Downloadable);
        Assert.True(livery.TaggedPaintJob);
        Assert.Null(livery.Problem);
        Assert.Equal("Sun Bus HK Skin Pack", livery.Title);
        Assert.Equal(22470488, livery.FileSize);
        Assert.Equal(1794664096, livery.TimeUpdated);
        Assert.Equal("76561198135332932", livery.CreatorId);

        Assert.Contains("another game", items[1].Problem);
        Assert.False(items[2].Found);
        Assert.NotNull(items[2].Problem);
    }

    [Fact]
    public void ReadsPersonaName() =>
        Assert.Equal("Angus", WorkshopApi.ParsePersonaName("<profile><steamID64>1</steamID64><steamID><![CDATA[Angus]]></steamID></profile>"));

    [Fact]
    public void PassesTheRightArguments()
    {
        Assert.Equal(["-app", "227300", "-pubfile", "123456", "-dir", @"C:\x", "-remember-password", "-qr"],
            DepotDownloaderTool.Arguments("123456", @"C:\x", null));
        Assert.Equal(["-app", "227300", "-pubfile", "123456", "-dir", @"C:\x", "-remember-password", "-username", "me"],
            DepotDownloaderTool.Arguments("123456", @"C:\x", "me"));
    }

    /// <summary>A QR code as QRCoder's AsciiQRCode draws it: one line per module row, "██" or "  " per module.</summary>
    static List<string> QrText(bool[,] grid)
    {
        var rows = new List<string>();
        for (var y = 0; y < grid.GetLength(0); y++)
        {
            var sb = new StringBuilder();
            for (var x = 0; x < grid.GetLength(1); x++) sb.Append(grid[y, x] ? "██" : "  ");
            rows.Add(sb.ToString());
        }
        return rows;
    }

    static bool[,] SampleGrid(int n = 29)
    {
        var grid = new bool[n, n];
        for (var y = 4; y < n - 4; y++)
            for (var x = 4; x < n - 4; x++)
                grid[y, x] = (x * 7 + y * 3) % 5 < 2;
        return grid;
    }

    [Fact]
    public void ShowsTheQrCodeAsSoonAsItIsComplete()
    {
        var grid = SampleGrid();
        bool[,]? shown = null;
        var parser = new DepotDownloaderTool.OutputParser(new DownloadEvents { QrCode = g => shown = g ?? shown });

        parser.Feed("Connecting to Steam3... Done!");
        parser.Feed("Logging in with QR code...");
        parser.Feed("Use the Steam Mobile App to sign in with this QR code:");
        foreach (var row in QrText(grid)) parser.Feed(row);

        // Nothing more is printed while DepotDownloader waits for the scan.
        Assert.NotNull(shown);
        Assert.Equal(grid, shown);
    }

    [Fact]
    public void QrCodeSurvivesTheChineseConsoleCodePage()
    {
        // On Traditional Chinese Windows the hidden console writes code page 950, where "█" is two bytes.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var big5 = Encoding.GetEncoding(950);
        var grid = SampleGrid();
        var decoded = QrText(grid).Select(r => big5.GetString(big5.GetBytes(r))).ToList();
        Assert.Equal(grid, DepotDownloaderTool.OutputParser.ParseQr(decoded));
    }

    [Theory]
    [InlineData("Enter account password for \"me\": ", DownloadOutcome.NeedsSignIn)]
    [InlineData("Access token was rejected (Expired).", DownloadOutcome.NeedsSignIn)]
    [InlineData("Please enter your 2 factor auth code from your authenticator app: ", DownloadOutcome.NeedsSignIn)]
    [InlineData("App 227300 (Euro Truck Simulator 2) is not available from this account.", DownloadOutcome.NotOwned)]
    public void SpotsSignInProblems(string line, DownloadOutcome outcome)
    {
        var parser = new DepotDownloaderTool.OutputParser(new DownloadEvents());
        parser.Feed(line);
        Assert.Equal(outcome, parser.Result(1, false).Outcome);
    }

    [Fact]
    public void ReportsProgressAndTheAccount()
    {
        var progress = new List<double>();
        var parser = new DepotDownloaderTool.OutputParser(new DownloadEvents { Progress = progress.Add });
        parser.Feed("Success! Next time you can login with -username some_user -remember-password instead of -qr.");
        parser.Feed(" 12.50% C:\\x\\universal.scs");
        parser.Feed("100.00% C:\\x\\versions.sii");
        parser.Feed("Total downloaded: 100 bytes (200 bytes uncompressed) from 1 depots");
        Assert.Equal([12.5, 100], progress);
        var result = parser.Result(0, true);
        Assert.Equal(DownloadOutcome.Done, result.Outcome);
        Assert.Equal("some_user", result.Account);
        Assert.Equal(DownloadOutcome.Failed, parser.Result(0, false).Outcome); // exit 0 but nothing arrived
    }

    [Fact]
    public void ReadsRememberedAccountNames()
    {
        static byte[] Field(int number, byte[] value) =>
            [(byte)(number << 3 | 2), (byte)value.Length, .. value];
        var entry = Field(1, Encoding.UTF8.GetBytes("my_account")).Concat(Field(2, Encoding.UTF8.GetBytes("token"))).ToArray();
        var penalty = Field(1, Encoding.UTF8.GetBytes("cm.example")).Concat(new byte[] { 0x10, 0x05 }).ToArray();
        var message = Field(2, penalty).Concat(Field(4, entry)).ToArray();

        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionMode.Compress, leaveOpen: true)) deflate.Write(message);
        Assert.Equal(["my_account"], DepotDownloaderTool.ReadAccountNames(packed.ToArray()));
    }

    [Fact]
    public void DownloadsOnlyUnsubscribedItemsThatAreMissingOrUpdated()
    {
        using var dir = new TempDir();
        WorkshopCache.Root = Path.Combine(dir.Path, "cache");
        var paths = new GamePaths(null, dir.Path, null, [], "1.62.3.3s");
        var source = new LiverySource { Kind = LiverySourceKind.Workshop, WorkshopId = "3839519582", DownloadedUpdated = 100 };
        WorkshopDetails Details(long updated) => new("3839519582", true, "x", null, 227300, true, 1, updated, [], null, false);

        Assert.True(WorkshopCache.NeedsDownload(source, paths, Details(100))); // never downloaded

        var download = WorkshopCache.NewDownloadDir("3839519582");
        Directory.CreateDirectory(Path.Combine(download, ".DepotDownloader", "staging"));
        File.WriteAllText(Path.Combine(download, "versions.sii"), "SiiNunit {}");
        var committed = WorkshopCache.Commit("3839519582", download);
        Assert.False(Directory.Exists(Path.Combine(committed, ".DepotDownloader"))); // never ends up in the pack
        Assert.Equal(committed, Workshop.FindAnyItemDir(paths, "3839519582"));

        Assert.False(WorkshopCache.NeedsDownload(source, paths, Details(100)));
        Assert.True(WorkshopCache.NeedsDownload(source, paths, Details(200)));
        Assert.False(WorkshopCache.NeedsDownload(new LiverySource { Kind = LiverySourceKind.Local, Path = "x" }, paths, null));

        WorkshopCache.Remove("3839519582");
        Assert.Null(WorkshopCache.Find("3839519582"));
    }
}
