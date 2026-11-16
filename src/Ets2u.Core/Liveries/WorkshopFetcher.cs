namespace Ets2u.Core.Liveries;

/// <summary>
/// Downloads Workshop items into <see cref="WorkshopCache"/> with DepotDownloader, one at a time:
/// installs DepotDownloader when there's none, uses the remembered Steam login, and falls back to a QR
/// sign-in when that login is missing or no longer valid (unless <see cref="AllowSignIn"/> is off).
/// Call it from the UI thread: settings are changed where it resumes.
/// </summary>
public sealed class WorkshopFetcher(LiveryPackSettings settings, Action save)
{
    static readonly SemaphoreSlim Gate = new(1, 1);

    public DownloadEvents Events { get; init; } = new();
    public Action<string>? Status { get; init; }

    /// <summary>Off for the checks on start: a QR code is only shown when the user asked for a download.</summary>
    public bool AllowSignIn { get; init; } = true;

    public async Task<DownloadResult> FetchAsync(string id, string name, long timeUpdated, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var exe = DepotDownloaderTool.Find();
            if (exe is null)
            {
                Status?.Invoke($"Installing DepotDownloader {DepotDownloaderTool.Version} (about 33 MB, once)…");
                exe = await DepotDownloaderTool.InstallAsync(Events.Progress, ct);
            }

            var account = settings.SteamAccount ?? DepotDownloaderTool.SavedAccounts().FirstOrDefault();
            if (account is null && !AllowSignIn)
                return new DownloadResult(DownloadOutcome.NeedsSignIn, "Sign in to Steam to download it.");

            Status?.Invoke(account is null ? $"Sign in to Steam to download {name}…" : $"Downloading {name}…");
            var dir = WorkshopCache.NewDownloadDir(id);
            var result = await DepotDownloaderTool.DownloadAsync(exe, id, dir, account, Events, ct);
            if (result.Outcome == DownloadOutcome.NeedsSignIn && account is not null)
            {
                settings.SteamAccount = null;
                save();
                if (AllowSignIn)
                {
                    Status?.Invoke($"Sign in to Steam again to download {name}…");
                    dir = WorkshopCache.NewDownloadDir(id);
                    result = await DepotDownloaderTool.DownloadAsync(exe, id, dir, null, Events, ct);
                    account = null;
                }
            }

            if (result.Outcome != DownloadOutcome.Done)
            {
                WorkshopCache.Discard(dir);
                return result;
            }

            WorkshopCache.Commit(id, dir);
            settings.SteamAccount = result.Account ?? account ?? DepotDownloaderTool.SavedAccounts().FirstOrDefault();
            foreach (var source in settings.Sources.Where(s => s.Kind == LiverySourceKind.Workshop && s.WorkshopId == id))
                source.DownloadedUpdated = timeUpdated;
            save();
            return result;
        }
        finally
        {
            Gate.Release();
        }
    }
}
