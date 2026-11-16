using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ets2u.Core.Liveries;

/// <summary>What Steam says about a Workshop item, read before anything is downloaded.</summary>
/// <param name="Found">Steam knows the item and it's visible to everyone.</param>
/// <param name="AppId">The game the item is for (227300 is ETS2).</param>
/// <param name="Downloadable">It has content to download (a collection or a deleted item has none).</param>
/// <param name="TimeUpdated">When it last changed, in Unix seconds; a newer value means download it again.</param>
public sealed record WorkshopDetails(
    string Id, bool Found, string Title, string? CreatorId, long AppId, bool Downloadable, long FileSize,
    long TimeUpdated, IReadOnlyList<string> Tags, string? PreviewUrl, bool Banned)
{
    public bool ForEts2 => AppId.ToString() == Workshop.AppId;

    /// <summary>The creator tagged it as a paint job, which is how liveries are listed on the Workshop.</summary>
    public bool TaggedPaintJob => Tags.Any(t => t.Replace(" ", "").StartsWith("Paintjob", StringComparison.OrdinalIgnoreCase));

    /// <summary>Why it can't go in the pack, or null if it can.</summary>
    public string? Problem =>
        !Found ? "Steam doesn't know this item, or it's private or deleted."
        : Banned ? "Steam has banned this item."
        : !ForEts2 ? $"This item is for another game (Steam app {AppId}), not Euro Truck Simulator 2."
        : !Downloadable ? "This item has nothing to download (it may be a collection: add its items one by one)."
        : null;
}

/// <summary>
/// Steam's public Web API: no key or login is needed to read a Workshop item's title, size, tags and
/// update time, or a creator's display name.
/// </summary>
public static class WorkshopApi
{
    const string DetailsUrl = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";

    static readonly Lazy<HttpClient> SharedClient = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ets2-utilities");
        return client;
    });

    public static HttpClient Http => SharedClient.Value;

    public static async Task<IReadOnlyList<WorkshopDetails>> GetDetailsAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        var form = new List<KeyValuePair<string, string>> { new("itemcount", ids.Count.ToString()) };
        form.AddRange(ids.Select((id, i) => new KeyValuePair<string, string>($"publishedfileids[{i}]", id)));
        using var response = await Http.PostAsync(DetailsUrl, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return ParseDetails(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public static async Task<WorkshopDetails> GetDetailsAsync(string id, CancellationToken ct = default) =>
        (await GetDetailsAsync([id], ct).ConfigureAwait(false)).FirstOrDefault()
        ?? throw new HttpRequestException("Steam sent no details for item " + id);

    /// <summary>Reads GetPublishedFileDetails' answer.</summary>
    public static IReadOnlyList<WorkshopDetails> ParseDetails(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var response)
            || !response.TryGetProperty("publishedfiledetails", out var items)) return [];
        var list = new List<WorkshopDetails>();
        foreach (var item in items.EnumerateArray())
        {
            var id = Str(item, "publishedfileid") ?? "";
            var found = Long(item, "result") == 1 && Long(item, "visibility") == 0;
            var tags = item.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => Str(x, "tag")).OfType<string>().ToList()
                : new List<string>();
            var hasContent = Str(item, "hcontent_file") is { } h && h != "0" || !string.IsNullOrEmpty(Str(item, "file_url"));
            list.Add(new WorkshopDetails(
                id, found, Str(item, "title") is { Length: > 0 } title ? title : $"Workshop item {id}", Str(item, "creator"),
                Long(item, "consumer_app_id"), found && hasContent, Long(item, "file_size"), Long(item, "time_updated"),
                tags, Str(item, "preview_url"), Long(item, "banned") != 0));
        }
        return list;
    }

    /// <summary>A Steam user's display name from their public profile, or null.</summary>
    public static async Task<string?> GetPersonaNameAsync(string steamId64, CancellationToken ct = default)
    {
        if (!Regex.IsMatch(steamId64, @"^\d{17}$")) return null;
        try
        {
            var xml = await Http.GetStringAsync($"https://steamcommunity.com/profiles/{steamId64}/?xml=1", ct).ConfigureAwait(false);
            return ParsePersonaName(xml);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public static string? ParsePersonaName(string xml)
    {
        var m = Regex.Match(xml, @"<steamID>(?:<!\[CDATA\[(.*?)\]\]>|([^<]*))</steamID>", RegexOptions.Singleline);
        var name = m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim() : null;
        return string.IsNullOrEmpty(name) ? null : name;
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    static long Long(JsonElement e, string name) => long.TryParse(Str(e, name), out var n) ? n : 0;
}
