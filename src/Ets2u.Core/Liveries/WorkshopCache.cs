namespace Ets2u.Core.Liveries;

/// <summary>
/// Workshop items the app downloaded itself (with DepotDownloader), without a Steam subscription:
/// %LocalAppData%\ets2u\workshop\&lt;id&gt;, laid out like Steam's own Workshop folder. Steam never
/// touches them, so the app keeps them up to date itself (see <see cref="NeedsDownload"/>).
/// </summary>
public static class WorkshopCache
{
    static string? _root;

    /// <summary>Settable for tests.</summary>
    public static string Root
    {
        get => _root ?? Path.Combine(AppDirs.Local, "workshop");
        set => _root = value;
    }

    public static string ItemDir(string id) => Path.Combine(Root, id);

    /// <summary>The item's folder, if it has been downloaded.</summary>
    public static string? Find(string id) => DepotDownloaderTool.HasContent(ItemDir(id)) ? ItemDir(id) : null;

    /// <summary>A fresh, empty folder to download into, beside the item's.</summary>
    public static string NewDownloadDir(string id)
    {
        var dir = ItemDir(id) + ".download";
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Puts a finished download in place of the item's folder. DepotDownloader's bookkeeping folder is
    /// removed first: everything left in the folder is the mod, and a pack takes every file in it.
    /// </summary>
    public static string Commit(string id, string downloadDir)
    {
        var meta = Path.Combine(downloadDir, ".DepotDownloader");
        if (Directory.Exists(meta)) Directory.Delete(meta, true);
        var dir = ItemDir(id);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.Move(downloadDir, dir);
        return dir;
    }

    public static void Discard(string downloadDir)
    {
        try
        {
            if (Directory.Exists(downloadDir)) Directory.Delete(downloadDir, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Removes a downloaded item no source uses any more.</summary>
    public static void Remove(string id)
    {
        try
        {
            if (Directory.Exists(ItemDir(id))) Directory.Delete(ItemDir(id), true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// A Workshop source the app has to download: not subscribed (Steam keeps subscribed items up to date
    /// itself), and either never downloaded or changed on the Workshop since.
    /// </summary>
    public static bool NeedsDownload(LiverySource source, Paths.GamePaths paths, WorkshopDetails? details)
    {
        if (source.Kind != LiverySourceKind.Workshop || source.WorkshopId is not { } id) return false;
        if (Workshop.FindItemDir(paths, id) is not null) return false;
        if (Find(id) is null) return true;
        return details is { Found: true } && details.TimeUpdated > (source.DownloadedUpdated ?? 0);
    }
}
