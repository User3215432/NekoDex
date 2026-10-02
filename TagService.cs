using System.Text.Json;

namespace MangaLibraryApp;

/// <summary>Fortschritt beim Herunterladen des Online-Tag-Index.</summary>
public sealed record TagDownloadProgress(int Completed, int Total, string Status, int Collected);

/// <summary>
/// Online-Tag-Katalog für die Websuche (<c>online_site_tags</c>), nach NClientV3 vom Bibliotheks-Katalog getrennt.
/// Beim Wechsel der Ziel-Website kommen Autocomplete und Katalog nur von dieser Quelle
/// (nhentai: artist, character, parody, group, language, category, tag).
/// </summary>
internal sealed class TagService
{
    private readonly Database _db;

    public TagService(Database db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>Alle Online-Tags der gewählten Website, ohne künstliches Limit.</summary>
    public List<TagInfo> GetAll(string siteName, string? filter = null) =>
        _db.GetTagsBySite(siteName, filter, limit: null);

    public int Count(string siteName) => _db.CountTagsBySite(siteName);

    /// <summary>Häufigste Online-Tags (nur für kompakte Chip-Leisten; Katalog nutzt <see cref="GetAll"/>).</summary>
    public List<TagInfo> GetPopular(string siteName, int limit = 80, string? filter = null) =>
        _db.GetTagsBySite(siteName, filter, limit);

    /// <summary>
    /// Autovervollständigung aus <c>online_site_tags</c> der gewählten Quelle.
    /// <paramref name="limit"/> = <c>null</c> liefert alle Treffer (UI virtualisiert).
    /// </summary>
    public List<TagInfo> Suggest(string siteName, string? text, int? limit = null, IEnumerable<string>? exclude = null)
    {
        var skip = new HashSet<string>((exclude ?? Enumerable.Empty<string>()).Select(TagNames.Normalize), StringComparer.Ordinal);
        var found = _db.GetTagsBySite(siteName, text, limit is null ? null : limit.Value + skip.Count);
        if (skip.Count == 0)
            return limit is null || found.Count <= limit.Value ? found : found.GetRange(0, limit.Value);

        var result = new List<TagInfo>(limit ?? found.Count);
        foreach (var tag in found)
        {
            if (skip.Contains(tag.Name))
                continue;
            result.Add(tag);
            if (limit is int cap && result.Count >= cap)
                break;
        }

        return result;
    }

    /// <summary>
    /// Lädt den vollständigen Tag-Index der Quelle (NClientV3-Dump / Listings) und speichert ihn in <c>online_site_tags</c>.
    /// </summary>
    public async Task<int> DownloadAndSeedSiteTags(
        string siteName,
        ApiSearchService api,
        IProgress<TagDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        var site = SourceCatalog.Normalize(siteName);
        if (site == SourceCatalog.Other)
            throw new ArgumentException("Unbekannte Website.");

        progress?.Report(new TagDownloadProgress(0, 1, "Lade Tag-Index von " + SourceCatalog.DisplayName(site) + " …", 0));
        var tags = await api.DownloadSiteTagIndexAsync(site, progress, cancellationToken);
        if (tags.Count == 0)
            throw new InvalidOperationException("Die Quelle hat keine Tags geliefert. Session-Cookie prüfen oder später erneut versuchen.");

        progress?.Report(new TagDownloadProgress(1, 1, "Schreibe " + tags.Count.ToString("N0") + " Tags in die Datenbank …", tags.Count));
        var total = await Task.Run(() => _db.DownloadAndSeedSiteTags(site, tags), cancellationToken);
        progress?.Report(new TagDownloadProgress(1, 1, total.ToString("N0") + " Tags gespeichert.", total));
        return total;
    }

    public string ExportJson(string siteName)
    {
        var site = SourceCatalog.Normalize(siteName);
        var tags = _db.GetTagsBySite(site, filter: null, limit: null);
        var payload = new
        {
            site,
            tags = tags.Select(t =>
            {
                var (category, name) = TagNames.Split(t.Name);
                return new { category, name, count = t.MangaCount };
            }).ToList(),
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    public int ImportJson(string siteName, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var list = new List<(string Tag, int Count)>();

        JsonElement array = default;
        if (root.ValueKind == JsonValueKind.Array)
            array = root;
        else if (root.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            array = tagsEl;
        else
            throw new InvalidOperationException("JSON enthält kein Tag-Array.");

        foreach (var item in array.EnumerateArray())
        {
            string? name;
            string category;
            var count = 1;
            if (item.ValueKind == JsonValueKind.Object)
            {
                name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                category = item.TryGetProperty("category", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                if (item.TryGetProperty("count", out var cnt) && cnt.TryGetInt32(out var nCount))
                    count = nCount;
                if (string.IsNullOrWhiteSpace(name) && item.TryGetProperty("tag", out var tagEl))
                    name = tagEl.GetString();
            }
            else if (item.ValueKind == JsonValueKind.String)
            {
                name = item.GetString();
                category = string.Empty;
            }
            else
                continue;

            var formatted = string.IsNullOrWhiteSpace(category)
                ? TagNames.Normalize(name)
                : TagNames.Format(category, TagNames.CleanName(name));
            if (formatted.Length > 0)
                list.Add((formatted, Math.Max(1, count)));
        }

        if (list.Count == 0)
            throw new InvalidOperationException("Die Datei enthält keine verwertbaren Tags.");

        return _db.DownloadAndSeedSiteTags(siteName, list);
    }
}
