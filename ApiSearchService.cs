using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MangaLibraryApp;

/// <summary>Ein Treffer der direkten Web-API-Suche (ohne gallery-dl beim Raster).</summary>
public sealed record OnlineSearchResult(
    string Title,
    string? CoverUrl,
    string GalleryUrl,
    IReadOnlyList<string> Tags,
    string SourceSite,
    string? GalleryId = null,
    int? PageCount = null,
    IReadOnlyList<(string Tag, int Count)>? TagHarvest = null,
    IReadOnlyList<string>? PageUrls = null);

internal sealed class ApiSearchException : Exception
{
    public bool IsBlocked { get; }

    public ApiSearchException(string message, bool blocked = false) : base(message)
    {
        IsBlocked = blocked;
    }
}

/// <summary>
/// Direkte HTTP-Suche nach NHApp / NClientV3: CookieContainer, Browser-Header, nhentai API v2 dann v1.
/// Bei HTTP 403 / Cloudflare: FlareSolverr (optional) und gallery-dl als interner Bypass-Scraper.
/// </summary>
internal sealed class ApiSearchService : IDisposable
{
    /// <summary>Wie viele Ergebnisseiten <see cref="SearchAllPagesAsync"/> höchstens hintereinander lädt.</summary>
    public const int DefaultMaxPages = 5;

    /// <summary>Hitomi liefert keinen Seiten-JSON; eine UI-Seite umfasst so viele Galerien.</summary>
    public const int HitomiPageSize = 25;

    public const string BlockedHint =
        "Session-Cookie (cf_clearance, csrftoken, xres) in den Einstellungen hinterlegen oder gallery-dl Fallback nutzen.";

    private const string ChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private const string SecChUa = "\"Google Chrome\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"";

    private static readonly Regex EhGallery = new(@"e-hentai\.org/g/(\d+)/([0-9a-f]{8,})/", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NhentaiListingTag = new(
        @"href\s*=\s*[""']/(?<type>artist|character|parody|group|tag|language|category)/(?<slug>[^""'/]+)/[""'][^>]*>\s*<span\s+class\s*=\s*[""']name[""']\s*>(?<name>[^<]+)</span>\s*<span\s+class\s*=\s*[""']count[""']\s*>(?<count>[^<]+)</span>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NhentaiPageLink = new(@"[?&]page=(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HitomiIndexLink = new(
        @"href\s*=\s*[""']/(?<type>tag|artist|character|series|group|language)/(?<slug>[^""']+?)-all\.html[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly CookieContainer _cookies = new();
    private readonly object _cookieGate = new();
    private readonly SemaphoreSlim _hitomiIndexGate = new(1, 1);
    private List<long>? _hitomiIndexAll;
    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _http;
    private string _userAgent = ChromeUa;
    private bool _disposed;

    public ApiSearchService()
    {
        try
        {
            _handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(15),
                MaxConnectionsPerServer = 8,
                UseCookies = true,
                CookieContainer = _cookies,
            };

            _http = new HttpClient(_handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(90) };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("HttpClient für die Websuche konnte nicht erzeugt werden: " + ex.Message, ex);
        }
    }

    /// <summary>Hinweis für die Statusleiste (Fallback), kein Fehler.</summary>
    public Action<string>? Notice { get; set; }

    public int? LastTotal { get; private set; }
    public int? LastTotalPages { get; private set; }
    public bool LastHasMore { get; private set; }
    public int LastPage { get; private set; } = 1;

    /// <summary>Warnungen für das Log (Cloudflare, 429, Fallback).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Neu erfasste Cookies (z. B. nach FlareSolverr) zum Speichern.</summary>
    public Action<IReadOnlyList<StoredHttpCookie>>? CookiesCaptured { get; set; }

    public string? FlareSolverrUrl { get; set; }

    public GalleryDlCommand? GalleryDl { get; set; }

    public string? ExtraArgs { get; set; }

    public string? WorkingDirectory { get; set; }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _hitomiIndexGate.Dispose();
        _http.Dispose();
    }

    /// <summary>Ersetzt den Cookie-Jar (Suwayomi-Style Session-Persistenz).</summary>
    public void LoadCookies(IEnumerable<StoredHttpCookie> cookies)
    {
        lock (_cookieGate)
        {
            foreach (Cookie existing in _cookies.GetAllCookies())
                existing.Expired = true;

            foreach (var item in cookies)
                AddCookie(item);
        }
    }

    /// <summary>
    /// Vollständiger Tag-Index einer Quelle: nhentai über NClientV3-<c>tags.json</c> plus Listings,
    /// andere Seiten über HTML-Verzeichnisse.
    /// </summary>
    public async Task<IReadOnlyList<(string Tag, int Count)>> DownloadSiteTagIndexAsync(
        string site,
        IProgress<TagDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        site = SourceCatalog.Normalize(site);
        return site switch
        {
            "nhentai" => await DownloadNhentaiTagsAsync(progress, cancellationToken),
            "hitomi" => await DownloadHitomiTagsAsync(progress, cancellationToken),
            "ehentai" => await DownloadEhentaiTagsAsync(progress, cancellationToken),
            _ => throw new ArgumentException("Unbekannte Website: " + site),
        };
    }

    public async Task<IReadOnlyList<OnlineSearchResult>> SearchAsync(
        string site,
        IReadOnlyList<string> tags,
        string? extraQuery,
        CancellationToken cancellationToken)
    {
        var list = new List<OnlineSearchResult>();
        await foreach (var hit in SearchStreamAsync(site, tags, extraQuery, page: 1, cancellationToken))
            list.Add(hit);
        return list;
    }

    /// <summary>Liefert Treffer einer Ergebnisseite. Seite 1 ist die erste Seite.</summary>
    public async IAsyncEnumerable<OnlineSearchResult> SearchStreamAsync(
        string site,
        IReadOnlyList<string> tags,
        string? extraQuery,
        int page,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        site = SourceCatalog.Normalize(site);
        page = Math.Max(1, page);
        if (site == "exhentai")
            throw new ArgumentException("ExHentai ist vorübergehend nicht auswählbar. Bitte E-Hentai verwenden.");
        var query = BuildQuery(site, tags, extraQuery);
        if (query.Length == 0)
            throw new ArgumentException("Mindestens ein Tag oder Suchwort ist nötig.");

        LastPage = page;
        IReadOnlyList<OnlineSearchResult>? direct = null;
        ApiSearchException? blocked = null;
        var fallbackSite = UsesGalleryDlFallback(site);

        try
        {
            direct = await SearchDirectAsync(site, query, tags, extraQuery, page, cancellationToken);
        }
        catch (ApiSearchException ex) when (ex.IsBlocked || fallbackSite)
        {
            blocked = ex;
            Log?.Invoke("Direkte API " + (ex.IsBlocked ? "blockiert" : "fehlgeschlagen") + ": " + ex.Message);
        }
        catch (ApiSearchException)
        {
            throw;
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && fallbackSite)
        {
            blocked = new ApiSearchException("Zeitüberschreitung bei der API-Anfrage: " + ex.Message, blocked: true);
            Log?.Invoke(blocked.Message);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ApiSearchException("Zeitüberschreitung bei der API-Anfrage: " + ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex) when (fallbackSite)
        {
            blocked = new ApiSearchException(DescribeHttp(ex), blocked: true);
            Log?.Invoke(blocked.Message);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiSearchException(DescribeHttp(ex));
        }

        var sparse = site is not "hitomi" && fallbackSite && direct is not null && IsSparse(direct);
        if (direct is { Count: > 0 } && !sparse)
        {
            foreach (var hit in direct)
                yield return hit;
            yield break;
        }

        if (blocked is null && !fallbackSite && direct is not null)
        {
            foreach (var hit in direct)
                yield return hit;
            yield break;
        }

        if (blocked is not null && !string.IsNullOrWhiteSpace(FlareSolverrUrl))
        {
            var root = SourceCatalog.Find(site)?.Root ?? "https://nhentai.net/";
            Log?.Invoke("FlareSolverr: Challenge für " + root + " …");
            try
            {
                if (await TryFlareSolverrAsync(root, cancellationToken))
                    direct = await SearchDirectAsync(site, query, tags, extraQuery, page, cancellationToken);
            }
            catch (Exception ex) when (ex is ApiSearchException or HttpRequestException or JsonException or TaskCanceledException)
            {
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    throw;
                Log?.Invoke("FlareSolverr fehlgeschlagen: " + ex.Message);
            }

            if (direct is { Count: > 0 } && !IsSparse(direct))
            {
                foreach (var hit in direct)
                    yield return hit;
                yield break;
            }
        }

        if (page > 1 && blocked is null && direct is not null && direct.Count == 0)
        {
            LastHasMore = false;
            yield break;
        }

        if (site == "hitomi" && direct is not { Count: > 0 })
        {
            Notice?.Invoke("Hitomi: Wechsel auf den Nozomi-Index und galleries/{id}.js, ohne die Cloudflare-Hauptseite.");
            try
            {
                direct = await SearchHitomiIndexPageAsync(page, cancellationToken);
            }
            catch (Exception ex) when (ex is ApiSearchException or HttpRequestException or JsonException)
            {
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    throw;
                Log?.Invoke("Hitomi-Nozomi: " + ex.Message);
            }

            if (direct is { Count: > 0 })
            {
                foreach (var hit in direct)
                    yield return hit;
                yield break;
            }
        }

        Log?.Invoke("Fallback: gallery-dl -j/--dump-json (gallery-dl kennt kein --json) für " + site + ".");
        IReadOnlyList<OnlineSearchResult>? fallback = null;
        GalleryDlException? dumpError = null;
        try
        {
            fallback = await SearchViaGalleryDlAsync(site, query, page, cancellationToken);
        }
        catch (GalleryDlException ex)
        {
            dumpError = ex;
        }

        if (dumpError is not null)
        {
            if (direct is { Count: > 0 })
            {
                Log?.Invoke("gallery-dl Fallback fehlgeschlagen (" + dumpError.Message + ") – verwende unvollständige API-Treffer.");
                foreach (var hit in direct)
                    yield return hit;
                yield break;
            }

            throw new ApiSearchException(
                (blocked?.Message ?? "Zugriff blockiert (HTTP 403 / Cloudflare).") + " gallery-dl: " + dumpError.Message + " " + BlockedHint,
                blocked: true);
        }

        if (fallback is null || fallback.Count == 0)
        {
            if (direct is { Count: > 0 })
            {
                foreach (var hit in direct)
                    yield return hit;
                yield break;
            }

            throw new ApiSearchException(
                (blocked?.Message ?? "Zugriff blockiert (HTTP 403 / Cloudflare).") + " " + BlockedHint,
                blocked: true);
        }

        foreach (var hit in fallback)
            yield return hit;
    }

    /// <summary>
    /// Lädt bis zu <paramref name="maxPages"/> Ergebnisseiten nacheinander (Standard 5).
    /// Die UI lädt Seiten einzeln nach; diese Methode ist für einen kompletten Durchlauf.
    /// </summary>
    public async IAsyncEnumerable<OnlineSearchResult> SearchAllPagesAsync(
        string site,
        string query,
        int maxPages = DefaultMaxPages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(maxPages, 1, 40);
        for (var page = 1; page <= limit; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = new List<OnlineSearchResult>();
            await foreach (var hit in SearchStreamAsync(site, Array.Empty<string>(), query, page, cancellationToken))
                batch.Add(hit);

            if (batch.Count == 0)
                yield break;

            foreach (var hit in batch)
                yield return hit;

            if (!LastHasMore)
                yield break;
        }
    }

    private static bool UsesGalleryDlFallback(string site) =>
        site is "hitomi" or "ehentai";

    /// <summary>Unvollständig: die Mehrheit der Treffer hat keine Tags (HTML-Scrape ohne Metadaten).</summary>
    private static bool IsSparse(IReadOnlyList<OnlineSearchResult> hits) =>
        hits.Count > 0 && hits.Count(h => h.Tags.Count == 0) * 2 >= hits.Count;

    private Task<IReadOnlyList<OnlineSearchResult>> SearchDirectAsync(
        string site,
        string query,
        IReadOnlyList<string> tags,
        string? extraQuery,
        int page,
        CancellationToken cancellationToken) =>
        site switch
        {
            "nhentai" => SearchNhentaiAsync(query, page, cancellationToken),
            "ehentai" => SearchEHentaiAsync(query, page, cancellationToken),
            "hitomi" => SearchHitomiAsync(tags, extraQuery, page, cancellationToken),
            _ => throw new ArgumentException("Unbekannte Website: " + site),
        };

    // ── nhentai REST (NClientV3: v2, Fallback v1) ───────────────────────

    private async Task<IReadOnlyList<OnlineSearchResult>> SearchNhentaiAsync(string query, int page, CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(query);
        var endpoints = new[]
        {
            ("https://nhentai.net/api/v2/search?query=" + encoded + "&page=" + page, "API v2"),
            ("https://nhentai.net/api/galleries/search?query=" + encoded + "&page=" + page, "API v1"),
        };

        ApiSearchException? lastBlocked = null;
        foreach (var (url, name) in endpoints)
        {
            string json;
            try
            {
                json = await GetStringAsync(url, "https://nhentai.net/", ct);
            }
            catch (ApiSearchException ex) when (ex.IsBlocked)
            {
                lastBlocked = ex;
                Log?.Invoke("nhentai " + name + " blockiert – nächste Schnittstelle.");
                continue;
            }

            var (hits, totalPages, total) = ParseNhentaiSearch(json, name);
            if (hits.Count > 0)
            {
                RememberPage(page, hits.Count, totalPages, total);
                Log?.Invoke("nhentai " + name + " Seite " + page + ": " + hits.Count + " Galerien.");
                return hits;
            }

            Log?.Invoke("nhentai " + name + " Seite " + page + " lieferte keine Galerien.");
        }

        if (page > 1)
        {
            RememberPage(page, 0, LastTotalPages, LastTotal);
            LastHasMore = false;
            return Array.Empty<OnlineSearchResult>();
        }

        throw lastBlocked ?? new ApiSearchException("nhentai hat keine Suchergebnisse geliefert.", blocked: lastBlocked is not null);
    }

    private static (List<OnlineSearchResult> Hits, int? TotalPages, int? Total) ParseNhentaiSearch(string json, string name)
    {
        using var document = ParseJson(json, "nhentai " + name);
        var root = document.RootElement;
        var hits = new List<OnlineSearchResult>();
        if (!root.TryGetProperty("result", out var list) || list.ValueKind != JsonValueKind.Array)
            return (hits, ReadInt(root, "num_pages"), ReadCount(root));

        foreach (var item in list.EnumerateArray())
        {
            var gallery = NhentaiGallery.TryParse(item);
            if (gallery is null)
                continue;
            hits.Add(gallery.ToResult());
        }

        return (hits, ReadInt(root, "num_pages"), ReadCount(root));
    }

    // ── E-Hentai: HTML-Suche + offizielles JSON-RPC gdata ───────────────

    private async Task<IReadOnlyList<OnlineSearchResult>> SearchEHentaiAsync(string query, int page, CancellationToken ct)
    {
        const string host = "https://e-hentai.org/";
        var searchUrl = host + "?f_search=" + Uri.EscapeDataString(query) + "&inline_set=dm_l&page=" + Math.Max(0, page - 1);

        var html = await GetStringAsync(searchUrl, host, ct);
        var pairs = ReadEhGalleryPairs(html);

        if (pairs.Count == 0 && html.Contains("No hits found", StringComparison.OrdinalIgnoreCase))
        {
            RememberPage(page, 0, LastTotalPages, LastTotal);
            LastHasMore = false;
            return Array.Empty<OnlineSearchResult>();
        }

        if (pairs.Count == 0)
        {
            if (LooksLikeCloudflare(html))
                throw new ApiSearchException("E-Hentai blockiert die Anfrage (Cloudflare). " + BlockedHint, blocked: true);

            if (page > 1)
            {
                RememberPage(page, 0, LastTotalPages, LastTotal);
                LastHasMore = false;
                return Array.Empty<OnlineSearchResult>();
            }

            throw new ApiSearchException("Die E-Hentai-Suche lieferte keine Galerie-IDs.");
        }

        var hits = await FetchEhentaiGdataAsync(pairs, ct);
        var total = ReadEhTotal(html);
        int? totalPages = total is int count && hits.Count > 0
            ? (int)Math.Ceiling(count / (double)Math.Max(25, hits.Count))
            : null;
        RememberPage(page, hits.Count, totalPages, total);
        Log?.Invoke("E-Hentai gdata Seite " + page + ": " + hits.Count + " Galerien.");
        return hits;
    }

    /// <summary>
    /// Offizielle Schnittstelle <c>https://api.e-hentai.org/api.php</c>, <c>method: "gdata"</c>.
    /// Tags kommen als <c>namespace:name</c> (artist, character, group, language, female, male, …).
    /// </summary>
    private async Task<List<OnlineSearchResult>> FetchEhentaiGdataAsync(
        IReadOnlyList<(string Gid, string Token)> pairs,
        CancellationToken ct)
    {
        var payload = "{\"method\":\"gdata\",\"namespace\":1,\"gidlist\":[" +
                      string.Join(',', pairs.Select(p => "[" + p.Gid + ",\"" + p.Token + "\"]")) + "]}";
        var json = await PostJsonAsync("https://api.e-hentai.org/api.php", payload, "https://e-hentai.org/", ct);
        using var document = ParseJson(json, "E-Hentai gdata");
        var hits = new List<OnlineSearchResult>();
        if (!document.RootElement.TryGetProperty("gmetadata", out var meta) || meta.ValueKind != JsonValueKind.Array)
            throw new ApiSearchException("Die E-Hentai-API (gdata) hat kein gültiges JSON geliefert.");

        const string galleryHost = "https://e-hentai.org/g/";
        foreach (var item in meta.EnumerateArray())
        {
            var gid = ReadNumber(item, "gid");
            var token = ReadString(item, "token");
            if (gid is null || token is null)
                continue;

            var title = ReadString(item, "title") ?? ReadString(item, "title_jpn") ?? ("#" + gid);
            var thumb = ReadString(item, "thumb");
            int? pages = int.TryParse(ReadString(item, "filecount"), out var n) ? n : null;
            var harvest = ReadEhentaiTags(item);
            var tags = harvest.Select(t => t.Tag).ToList();
            hits.Add(new OnlineSearchResult(title, thumb, galleryHost + gid + "/" + token + "/", tags, "ehentai", gid, pages, harvest));
        }

        return hits;
    }

    private static List<(string Gid, string Token)> ReadEhGalleryPairs(string html)
    {
        var pairs = new List<(string Gid, string Token)>();
        var seen = new HashSet<string>();
        foreach (Match match in EhGallery.Matches(html))
        {
            var gid = match.Groups[1].Value;
            if (!seen.Add(gid))
                continue;
            pairs.Add((gid, match.Groups[2].Value));
        }

        return pairs;
    }

    private static List<(string Tag, int Count)> ReadEhentaiTags(JsonElement item)
    {
        var harvest = new List<(string Tag, int Count)>();
        if (!item.TryGetProperty("tags", out var tagEl) || tagEl.ValueKind != JsonValueKind.Array)
            return harvest;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in tagEl.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.String || t.GetString() is not { Length: > 0 } raw)
                continue;
            var formatted = FormatEhentaiTag(raw);
            if (formatted.Length == 0 || !seen.Add(formatted))
                continue;
            harvest.Add((formatted, 1));
        }

        return harvest;
    }

    /// <summary>
    /// <c>female:big breasts</c> bleibt <c>female:big breasts</c>.
    /// female, male, artist, character, group, language und weitere E-Hentai-Namespaces werden nicht abgeschnitten.
    /// </summary>
    private static string FormatEhentaiTag(string raw)
    {
        var text = raw.Trim().Replace('_', ' ');
        var colon = text.IndexOf(':');
        if (colon <= 0)
            return TagNames.Normalize(text);

        var ns = text[..colon].Trim().ToLowerInvariant();
        var name = TagNames.CleanName(text[(colon + 1)..]);
        if (name.Length == 0)
            return string.Empty;

        if (ns is "tag" or "tags" or "genre" or "genres" or "content" or "contents")
            return name;

        var category = TagNames.CanonicalCategory(ns) ?? ns;
        return category.Length == 0 ? name : TagNames.Format(category, name);
    }

    // ── Hitomi: Nozomi-Index + galleries/{id}.js ────────────────────────

    private async Task<IReadOnlyList<OnlineSearchResult>> SearchHitomiAsync(
        IReadOnlyList<string> tags,
        string? extraQuery,
        int page,
        CancellationToken ct)
    {
        var keys = tags.Select(TagNames.Normalize).Where(t => t.Length > 0).ToList();
        if (!string.IsNullOrWhiteSpace(extraQuery))
            keys.Add(TagNames.Normalize(extraQuery) is { Length: > 0 } n ? n : extraQuery.Trim());

        if (keys.Count == 0)
            throw new ArgumentException("Mindestens ein Tag oder Suchwort ist nötig.");

        HashSet<int>? ids = null;
        var anyBlocked = false;
        foreach (var key in keys)
        {
            byte[] bytes;
            try
            {
                bytes = await GetHitomiBytesAsync(HitomiTagNozomiUrls(key), "https://hitomi.la/", ct);
            }
            catch (ApiSearchException ex)
            {
                anyBlocked |= ex.IsBlocked;
                Log?.Invoke("Hitomi-Index " + key + ": " + ex.Message);
                continue;
            }

            var index = ReadNozomiIds(bytes);
            if (ids is null)
                ids = index;
            else
                ids.IntersectWith(index);
        }

        if (ids is null || ids.Count == 0)
        {
            if (anyBlocked)
            {
                Notice?.Invoke("Hitomi-Tag-Index blockiert (HTTP 403 / Cloudflare). Wechsel auf index-all.nozomi.");
                return await SearchHitomiIndexPageAsync(page, ct);
            }

            if (page > 1)
            {
                LastHasMore = false;
                return Array.Empty<OnlineSearchResult>();
            }

            throw new ApiSearchException("Hitomi hat zu diesen Tags keine Galerie-IDs geliefert.");
        }

        var all = ids.Select(id => (long)id).ToList();
        return await LoadHitomiPageAsync(all, page, ct);
    }

    /// <summary>
    /// Lädt <c>https://ltn.hitomi.la/index-all.nozomi</c> und liest die Galerie-IDs
    /// als 4-Byte-Big-Endian-Int32. Die Liste bleibt für weitere Seiten im Speicher.
    /// </summary>
    private async Task<IReadOnlyList<long>> FetchHitomiNozomiIndexAsync(CancellationToken ct)
    {
        if (_hitomiIndexAll is { Count: > 0 })
            return _hitomiIndexAll;

        await _hitomiIndexGate.WaitAsync(ct);
        try
        {
            if (_hitomiIndexAll is { Count: > 0 })
                return _hitomiIndexAll;

            var bytes = await GetHitomiBytesAsync(HitomiIndexUrls(), "https://hitomi.la/", ct);
            var ids = ParseNozomiIds(bytes);
            if (ids.Count == 0)
                throw new ApiSearchException("Hitomi index-all.nozomi enthielt keine Galerie-IDs.");

            _hitomiIndexAll = ids;
            Log?.Invoke("Hitomi index-all.nozomi: " + ids.Count + " Galerie-IDs.");
            return ids;
        }
        finally
        {
            _hitomiIndexGate.Release();
        }
    }

    /// <summary>Seite 1 = Index 0 bis 24, jede weitere Seite die nächsten <see cref="HitomiPageSize"/> IDs.</summary>
    private static List<long> SliceHitomiPage(IReadOnlyList<long> ids, int page)
    {
        var start = Math.Max(0, page - 1) * HitomiPageSize;
        if (start >= ids.Count)
            return new List<long>();

        var count = Math.Min(HitomiPageSize, ids.Count - start);
        var slice = new List<long>(count);
        for (var i = 0; i < count; i++)
            slice.Add(ids[start + i]);
        return slice;
    }

    private async Task<IReadOnlyList<OnlineSearchResult>> SearchHitomiIndexPageAsync(int page, CancellationToken ct)
    {
        IReadOnlyList<long> all;
        try
        {
            all = await FetchHitomiNozomiIndexAsync(ct);
        }
        catch (ApiSearchException ex) when (ex.IsBlocked)
        {
            throw new ApiSearchException("Hitomi index-all.nozomi blockiert (Cloudflare). " + BlockedHint, blocked: true);
        }

        Log?.Invoke("Hitomi-Fallback index-all.nozomi Seite " + page + " (neueste Galerien, ohne Tag-Filter).");
        return await LoadHitomiPageAsync(all, page, ct);
    }

    private async Task<IReadOnlyList<OnlineSearchResult>> LoadHitomiPageAsync(
        IReadOnlyList<long> all,
        int page,
        CancellationToken ct)
    {
        var totalPages = (int)Math.Ceiling(all.Count / (double)HitomiPageSize);
        var take = SliceHitomiPage(all, page);
        RememberPage(page, take.Count, totalPages, all.Count);
        if (take.Count == 0)
        {
            LastHasMore = false;
            return Array.Empty<OnlineSearchResult>();
        }

        var hits = new OnlineSearchResult?[take.Count];
        using var gate = new SemaphoreSlim(4);
        var tasks = take.Select(async (id, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                hits[index] = await FetchHitomiGalleryAsync(id, ct);
            }
            catch (Exception ex) when (ex is ApiSearchException or HttpRequestException or JsonException)
            {
                Log?.Invoke("Hitomi-Galerie " + id + ": " + ex.Message);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return hits.Where(h => h is not null).Select(h => h!).ToList();
    }

    private async Task<OnlineSearchResult?> FetchHitomiGalleryAsync(long id, CancellationToken ct)
    {
        var text = await GetHitomiTextAsync(HitomiGalleryScriptUrls(id), "https://hitomi.la/", ct);
        var json = text.Trim();
        var eq = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (eq < 0 || end <= eq)
            return null;

        using var document = JsonDocument.Parse(json[eq..(end + 1)]);
        var root = document.RootElement;
        var title = ReadString(root, "title") ?? ReadString(root, "japanese_title") ?? ("#" + id);

        string? cover = null;
        if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in files.EnumerateArray())
            {
                var hash = ReadString(file, "hash");
                if (hash is { Length: >= 3 })
                {
                    cover = HitomiCoverUrl(hash);
                    break;
                }
            }
        }

        var tagNames = new List<string>();
        var harvest = new List<(string Tag, int Count)>();
        if (root.TryGetProperty("tags", out var tagEl) && tagEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tagEl.EnumerateArray())
            {
                var name = ReadString(t, "tag");
                if (string.IsNullOrEmpty(name))
                    continue;
                var category = t.TryGetProperty("female", out _) ? "female"
                    : t.TryGetProperty("male", out _) ? "male"
                    : string.Empty;
                var formatted = TagNames.Format(category, TagNames.CleanName(name));
                if (formatted.Length > 0)
                {
                    tagNames.Add(formatted);
                    harvest.Add((formatted, 1));
                }
            }
        }

        var language = ReadString(root, "language");
        if (!string.IsNullOrEmpty(language))
        {
            var formatted = TagNames.Format("language", TagNames.CleanName(language));
            tagNames.Add(formatted);
            harvest.Add((formatted, 1));
        }

        return new OnlineSearchResult(title, cover, $"https://hitomi.la/galleries/{id}.html", tagNames, "hitomi", id.ToString(), null, harvest);
    }

    private static string HitomiNozomiUrl(string tag)
    {
        var urls = HitomiTagNozomiUrls(tag);
        return urls.Count > 0 ? urls[0] : "https://ltn.hitomi.la/index-all.nozomi";
    }

    /// <summary>
    /// Zuerst <c>ltn.hitomi.la</c>, danach das aktuelle CDN <c>ltn.gold-usergeneratedcontent.net</c>.
    /// </summary>
    private static List<string> HitomiTagNozomiUrls(string tag)
    {
        var (category, name) = TagNames.Split(tag);
        if (name.Length == 0)
            name = TagNames.CleanName(tag);
        if (category == "parody")
            category = "series";

        var area = category.Length == 0 ? "tag" : category;
        var slug = Uri.EscapeDataString(name.Replace(' ', '_'));
        return new List<string>
        {
            $"https://ltn.hitomi.la/{area}/{slug}-all.nozomi",
            $"https://ltn.hitomi.la/n/{area}/{slug}-all.nozomi",
            $"https://ltn.gold-usergeneratedcontent.net/n/{area}/{slug}-all.nozomi",
        };
    }

    private static List<string> HitomiIndexUrls() => new()
    {
        "https://ltn.hitomi.la/index-all.nozomi",
        "https://ltn.hitomi.la/n/index-all.nozomi",
        "https://ltn.gold-usergeneratedcontent.net/n/index-all.nozomi",
    };

    /// <summary>
    /// Thumbnail auf dem aktuellen CDN: <c>webpsmalltn/{letztes}/{zwei}/{hash}.webp</c>
    /// unter <c>{a|b}tn.gold-usergeneratedcontent.net</c>. <c>smallbigtn</c> und <c>tn.hitomi.la</c>
    /// liefern hier 404 bzw. keinen Host. Der Loader setzt Referer und probiert a/b.
    /// </summary>
    internal static string HitomiCoverUrl(string hash)
    {
        var last = hash[^1];
        var two = hash.Substring(hash.Length - 3, 2);
        return $"https://atn.gold-usergeneratedcontent.net/webpsmalltn/{last}/{two}/{hash}.webp";
    }

    private static List<string> HitomiGalleryScriptUrls(long id) => new()
    {
        $"https://ltn.hitomi.la/galleries/{id}.js",
        $"https://ltn.gold-usergeneratedcontent.net/galleries/{id}.js",
    };

    /// <summary>
    /// gallery-dl 1.32 hat kein <c>--http-header</c>. Referer läuft über <c>-o extractor.hitomi.headers.Referer</c>,
    /// der User-Agent über <c>--user-agent</c>.
    /// </summary>
    private static IReadOnlyList<string> HitomiGalleryDlFlags() => new[]
    {
        "--user-agent", ChromeUa,
        "-o", "extractor.hitomi.headers.Referer=https://hitomi.la/",
    };

    private async Task<byte[]> GetHitomiBytesAsync(IReadOnlyList<string> urls, string referer, CancellationToken ct)
    {
        ApiSearchException? last = null;
        foreach (var url in urls)
        {
            try
            {
                return await GetBytesAsync(url, referer, ct);
            }
            catch (ApiSearchException ex)
            {
                last = ex;
                Log?.Invoke("Hitomi " + url + ": " + ex.Message);
            }
        }

        throw last ?? new ApiSearchException("Hitomi-Index nicht erreichbar.", blocked: true);
    }

    private async Task<string> GetHitomiTextAsync(IReadOnlyList<string> urls, string referer, CancellationToken ct)
    {
        var bytes = await GetHitomiBytesAsync(urls, referer, ct);
        return Encoding.UTF8.GetString(bytes);
    }

    private static HashSet<int> ReadNozomiIds(byte[] data)
    {
        var ids = new HashSet<int>();
        foreach (var id in ParseNozomiIds(data))
        {
            if (id <= int.MaxValue)
                ids.Add((int)id);
        }

        return ids;
    }

    /// <summary>Nozomi-Datei: aufeinanderfolgende Int32, Big-Endian, neueste ID zuerst.</summary>
    private static List<long> ParseNozomiIds(byte[] data)
    {
        var ids = new List<long>(data.Length / 4);
        for (var i = 0; i + 4 <= data.Length; i += 4)
        {
            var id = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(i, 4));
            if (id > 0)
                ids.Add(id);
        }

        return ids;
    }

    // ── gallery-dl Bypass ───────────────────────────────────────────────

    private async Task<IReadOnlyList<OnlineSearchResult>> SearchViaGalleryDlAsync(
        string site,
        string query,
        int page,
        CancellationToken ct)
    {
        if (GalleryDl is null)
            throw new ApiSearchException("gallery-dl ist nicht eingerichtet. " + BlockedHint, blocked: true);

        var url = BuildSearchUrl(site, query, page);
        var domain = CookieDomainFor(site);
        var cookieFile = WriteNetscapeCookieFile(domain);
        Log?.Invoke("gallery-dl -j/--dump-json --range 1 --page-range " + page + " " + url);
        Notice?.Invoke(SourceCatalog.DisplayName(site) + "-Fallback Seite " + page + ": gallery-dl liest " + url);

        var json = await MangaLibraryApp.GalleryDl.DumpJsonAsync(
            GalleryDl, url, ExtraArgs, WorkingDirectory, ct, cookieFile, page.ToString(CultureInfo.InvariantCulture),
            site == "hitomi" ? HitomiGalleryDlFlags() : null);
        var hits = ParseGalleryDlDump(json, site);
        RememberPage(page, hits.Count, null, null);
        return hits;
    }

    internal static string BuildSearchUrl(string site, string query, int page = 1)
    {
        var q = Uri.EscapeDataString(query);
        var pageQuery = page > 1 ? "&page=" + page : string.Empty;
        return site switch
        {
            "nhentai" => "https://nhentai.net/search/?q=" + q + pageQuery,
            "ehentai" => "https://e-hentai.org/?f_search=" + q + "&page=" + Math.Max(0, page - 1),
            "hitomi" => "https://hitomi.la/search.html?" + q,
            _ => throw new ArgumentException("Unbekannte Website: " + site),
        };
    }

    private static string CookieDomainFor(string site) =>
        SourceCatalog.Find(site)?.Domains[0] ?? "nhentai.net";

    private List<OnlineSearchResult> ParseGalleryDlDump(string json, string site)
    {
        var hits = new List<OnlineSearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        json = json.Trim();
        if (json.Length == 0)
            return hits;

        try
        {
            using var document = JsonDocument.Parse(json);
            CollectDumpHits(document.RootElement, site, hits, seen);
        }
        catch (JsonException)
        {
            foreach (var slice in SplitJsonObjects(json))
            {
                try
                {
                    using var document = JsonDocument.Parse(slice);
                    CollectDumpHits(document.RootElement, site, hits, seen);
                }
                catch (JsonException)
                {
                    // nächstes Fragment
                }
            }
        }

        return hits;
    }

    private static void CollectDumpHits(JsonElement el, string site, List<OnlineSearchResult> hits, HashSet<string> seen)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Array:
                if (el.GetArrayLength() >= 2 && el[1].ValueKind == JsonValueKind.Object)
                {
                    var tupleHit = HitFromDumpObject(el[1], site, el.GetArrayLength() >= 3 && el[2].ValueKind == JsonValueKind.String ? el[2].GetString() : null);
                    if (tupleHit is not null && seen.Add(tupleHit.GalleryUrl))
                        hits.Add(tupleHit);
                    break;
                }

                foreach (var child in el.EnumerateArray())
                    CollectDumpHits(child, site, hits, seen);
                break;

            case JsonValueKind.Object:
                var hit = HitFromDumpObject(el, site, null);
                if (hit is not null && seen.Add(hit.GalleryUrl))
                    hits.Add(hit);
                break;
        }
    }

    private static OnlineSearchResult? HitFromDumpObject(JsonElement obj, string site, string? imageUrl)
    {
        var id = ReadNumber(obj, "gallery_id") ?? ReadNumber(obj, "id") ?? ReadNumber(obj, "gid");
        var title = ReadString(obj, "title")
                    ?? ReadString(obj, "fulltitle")
                    ?? ReadNestedString(obj, "title", "pretty")
                    ?? ReadString(obj, "manga")
                    ?? (id is null ? null : "#" + id);
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(id))
            return null;

        var url = ReadString(obj, "gallery_url")
                  ?? ReadString(obj, "webpage_url")
                  ?? ReadString(obj, "url");
        if (string.IsNullOrWhiteSpace(url) || LooksLikeImageUrl(url))
            url = GalleryUrlFromId(site, id, url);
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var cover = ReadString(obj, "thumbnail")
                    ?? ReadString(obj, "thumbnail_url")
                    ?? ReadString(obj, "cover_url")
                    ?? ReadString(obj, "cover")
                    ?? ReadString(obj, "thumb")
                    ?? ReadString(obj, "image")
                    ?? (LooksLikeImageUrl(imageUrl) ? imageUrl : null);

        var tags = ReadDumpTags(obj, site);
        var harvest = tags.Select(t => (t, 1)).ToList();
        int? pages = null;
        if (obj.TryGetProperty("count", out var c) && c.TryGetInt32(out var n))
            pages = n;
        else if (int.TryParse(ReadNumber(obj, "num_pages"), out var np))
            pages = np;

        return new OnlineSearchResult(title ?? url, cover, url, tags, site, id, pages, harvest);
    }

    private static List<string> ReadDumpTags(JsonElement obj, string? site = null)
    {
        var ehentai = site is "ehentai" or "exhentai";
        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? formatted)
        {
            if (string.IsNullOrWhiteSpace(formatted) || !seen.Add(formatted))
                return;
            tags.Add(formatted);
        }

        string Format(string? type, string raw)
        {
            if (ehentai)
                return string.IsNullOrWhiteSpace(type) ? FormatEhentaiTag(raw) : FormatEhentaiTag(type + ":" + raw);
            return string.IsNullOrWhiteSpace(type)
                ? TagNames.Normalize(raw)
                : TagNames.Format(NhentaiTagTypes.DatabaseCategory(type), TagNames.CleanName(raw));
        }

        void FromElement(JsonElement el, string? type)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String:
                    if (el.GetString() is { Length: > 0 } s)
                        Add(Format(type, s));
                    break;
                case JsonValueKind.Array:
                    foreach (var child in el.EnumerateArray())
                        FromElement(child, type);
                    break;
                case JsonValueKind.Object:
                    var name = ReadString(el, "name") ?? ReadString(el, "tag") ?? ReadString(el, "title");
                    var kind = ReadString(el, "namespace") ?? ReadString(el, "type") ?? ReadString(el, "category") ?? type ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(name))
                        Add(Format(kind, name));
                    break;
            }
        }

        foreach (var key in new[]
                 {
                     "tags", "tag",
                     "artist", "artists", "tags_artist", "tags_artists",
                     "parody", "parodies", "series", "tags_parody", "tags_series",
                     "character", "characters", "tags_character", "tags_characters",
                     "group", "groups", "circle", "circles", "tags_group", "tags_groups",
                     "language", "languages", "tags_language", "tags_languages",
                     "female", "tags_female", "male", "tags_male",
                     "mixed", "tags_mixed", "other", "tags_other",
                     "cosplayer", "tags_cosplayer", "reclass", "tags_reclass",
                     "temp", "tags_temp", "location", "tags_location",
                     "category", "categories", "content", "contents",
                 })
        {
            if (!obj.TryGetProperty(key, out var el))
                continue;
            FromElement(el, DumpTagType(key));
        }

        return tags;
    }

    /// <summary><c>tags_female</c> → <c>female</c>; das Sammelfeld <c>tags</c> trägt den Namespace schon im Wert.</summary>
    private static string? DumpTagType(string key)
    {
        if (key is "tags" or "tag")
            return null;
        var name = key.StartsWith("tags_", StringComparison.Ordinal) ? key["tags_".Length..] : key;
        return name switch
        {
            "artists" => "artist",
            "parodies" or "series" => "parody",
            "characters" => "character",
            "groups" or "circles" => "group",
            "languages" => "language",
            "categories" or "content" or "contents" => "category",
            _ => name,
        };
    }

    private static string? GalleryUrlFromId(string site, string? id, string? fallback)
    {
        if (id is { Length: > 0 })
        {
            return site switch
            {
                "nhentai" => "https://nhentai.net/g/" + id + "/",
                "hitomi" => "https://hitomi.la/galleries/" + id + ".html",
                "ehentai" => fallback,
                _ => fallback,
            };
        }

        return fallback;
    }

    private static bool LooksLikeImageUrl(string? url) =>
        url is not null
        && (url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".gif", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> SplitJsonObjects(string text)
    {
        var depth = 0;
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{')
            {
                if (depth == 0)
                    start = i;
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    yield return text[start..(i + 1)];
                    start = -1;
                }
            }
        }
    }

    // ── FlareSolverr ────────────────────────────────────────────────────

    private async Task<bool> TryFlareSolverrAsync(string pageUrl, CancellationToken ct)
    {
        var endpoint = FlareSolverrUrl!.Trim().TrimEnd('/');
        if (!endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            endpoint += "/v1";

        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["cmd"] = "request.get",
            ["url"] = pageUrl,
            ["maxTimeout"] = 60000,
        });

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(75) };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (!root.TryGetProperty("status", out var status) || status.GetString() is not "ok")
        {
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : text;
            Log?.Invoke("FlareSolverr: " + TrimForLog(message ?? "Fehler"));
            return false;
        }

        if (!root.TryGetProperty("solution", out var solution))
            return false;

        if (solution.TryGetProperty("userAgent", out var ua) && ua.GetString() is { Length: > 0 } agent)
            _userAgent = agent;

        var captured = new List<StoredHttpCookie>();
        if (solution.TryGetProperty("cookies", out var cookies) && cookies.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in cookies.EnumerateArray())
            {
                var name = c.TryGetProperty("name", out var n) ? n.GetString() : null;
                var value = c.TryGetProperty("value", out var v) ? v.GetString() : null;
                var domain = c.TryGetProperty("domain", out var d) ? d.GetString() : null;
                var path = c.TryGetProperty("path", out var p) ? p.GetString() : "/";
                if (string.IsNullOrEmpty(name) || value is null || string.IsNullOrEmpty(domain))
                    continue;
                var stored = new StoredHttpCookie(HttpCookieImport.NormalizeDomain(domain), name, value, path ?? "/");
                captured.Add(stored);
                AddCookie(stored);
            }
        }

        if (captured.Count > 0)
        {
            CookiesCaptured?.Invoke(captured);
            Log?.Invoke("FlareSolverr: " + captured.Count + " Cookies übernommen (u. a. cf_clearance).");
        }

        return captured.Count > 0;
    }

    // ── Tag-Index (NClientV3 / NHApp) ───────────────────────────────────

    private const string NhentaiTagDumpUrl =
        "https://raw.githubusercontent.com/maxwai/NClientV3/master/data/tags.json";

    private static readonly (string Path, string Type)[] NhentaiListingRoots =
    {
        ("artists", "artist"),
        ("characters", "character"),
        ("parodies", "parody"),
        ("groups", "group"),
        ("tags", "tag"),
        ("language", "language"),
        ("category", "category"),
    };

    private async Task<IReadOnlyList<(string Tag, int Count)>> DownloadNhentaiTagsAsync(
        IProgress<TagDownloadProgress>? progress,
        CancellationToken ct)
    {
        var bag = new Dictionary<string, int>(StringComparer.Ordinal);

        progress?.Report(new TagDownloadProgress(0, 3, "Lade NClientV3-Tag-Dump …", 0));
        try
        {
            var json = await GetStringAsync(NhentaiTagDumpUrl, "https://github.com/maxwai/NClientV3", ct);
            MergeNhentaiDump(json, bag);
            Log?.Invoke("NClientV3-Dump: " + bag.Count + " Tags.");
        }
        catch (Exception ex) when (ex is ApiSearchException or HttpRequestException or JsonException)
        {
            Log?.Invoke("NClientV3-Dump nicht erreichbar: " + ex.Message);
        }

        progress?.Report(new TagDownloadProgress(1, 3, "Lese nhentai-Tag-Listings …", bag.Count));
        var listing = 0;
        foreach (var (path, type) in NhentaiListingRoots)
        {
            ct.ThrowIfCancellationRequested();
            listing++;
            progress?.Report(new TagDownloadProgress(
                1, 3, "Listing " + type + " (" + listing + "/" + NhentaiListingRoots.Length + ") …", bag.Count));
            await ScrapeNhentaiListingAsync(path, type, bag, ct);
        }

        if (bag.Count == 0)
            throw new ApiSearchException("nhentai-Tag-Index leer. " + BlockedHint, blocked: true);

        progress?.Report(new TagDownloadProgress(3, 3, bag.Count.ToString("N0") + " Tags geladen.", bag.Count));
        return bag.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static void MergeNhentaiDump(string json, Dictionary<string, int> bag)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 4)
                continue;

            var name = row[1].ValueKind == JsonValueKind.String ? row[1].GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var count = 1;
            if (row[2].TryGetInt32(out var n))
                count = Math.Max(1, n);
            else if (row[2].ValueKind == JsonValueKind.String && int.TryParse(row[2].GetString(), out var parsed))
                count = Math.Max(1, parsed);

            var typeId = row[3].TryGetInt32(out var id) ? id : 3;
            var category = typeId switch
            {
                1 => NhentaiTagTypes.Parody,
                2 => NhentaiTagTypes.Character,
                4 => NhentaiTagTypes.Artist,
                5 => NhentaiTagTypes.Group,
                6 => NhentaiTagTypes.Language,
                7 => NhentaiTagTypes.Category,
                _ => string.Empty,
            };
            var formatted = TagNames.Format(NhentaiTagTypes.DatabaseCategory(category.Length == 0 ? "tag" : category), TagNames.CleanName(name));
            if (formatted.Length == 0)
                continue;
            if (!bag.TryGetValue(formatted, out var existing) || count > existing)
                bag[formatted] = count;
        }
    }

    private async Task ScrapeNhentaiListingAsync(string path, string type, Dictionary<string, int> bag, CancellationToken ct)
    {
        var maxPage = 1;
        for (var page = 1; page <= maxPage && page <= 80; page++)
        {
            var url = "https://nhentai.net/" + path + "/?page=" + page;
            string? html;
            try
            {
                html = await TryGetStringAsync(url, "https://nhentai.net/", ct);
            }
            catch (ApiSearchException)
            {
                break;
            }

            if (string.IsNullOrEmpty(html))
                break;

            var added = 0;
            foreach (Match match in NhentaiListingTag.Matches(html))
            {
                var tagType = NhentaiTagTypes.Canonical(match.Groups["type"].Value);
                var name = WebUtility.HtmlDecode(match.Groups["name"].Value);
                var formatted = TagNames.Format(NhentaiTagTypes.DatabaseCategory(tagType), TagNames.CleanName(name));
                if (formatted.Length == 0)
                    continue;
                var count = ParseCompactCount(match.Groups["count"].Value);
                if (!bag.TryGetValue(formatted, out var existing) || count > existing)
                    bag[formatted] = count;
                added++;
            }

            foreach (Match pageMatch in NhentaiPageLink.Matches(html))
            {
                if (int.TryParse(pageMatch.Groups[1].Value, out var n) && n > maxPage)
                    maxPage = Math.Min(n, 80);
            }

            if (added == 0)
                break;
        }
    }

    private async Task<IReadOnlyList<(string Tag, int Count)>> DownloadHitomiTagsAsync(
        IProgress<TagDownloadProgress>? progress,
        CancellationToken ct)
    {
        var bag = new Dictionary<string, int>(StringComparer.Ordinal);
        var letters = "123abcdefghijklmnopqrstuvwxyz".Select(c => c.ToString()).ToArray();
        var kinds = new[] { "alltags", "allartists", "allcharacters", "allseries", "allgroups" };
        var total = kinds.Length * letters.Length;
        var done = 0;

        foreach (var kind in kinds)
        {
            foreach (var letter in letters)
            {
                ct.ThrowIfCancellationRequested();
                done++;
                progress?.Report(new TagDownloadProgress(done, total, "Hitomi " + kind + "-" + letter + " …", bag.Count));
                var url = "https://hitomi.la/" + kind + "-" + letter + ".html";
                var html = await TryGetStringAsync(url, "https://hitomi.la/", ct);
                if (string.IsNullOrEmpty(html))
                    continue;

                foreach (Match match in HitomiIndexLink.Matches(html))
                {
                    var type = match.Groups["type"].Value.ToLowerInvariant();
                    var slug = WebUtility.UrlDecode(match.Groups["slug"].Value).Replace('-', ' ');
                    var category = type switch
                    {
                        "artist" => "artist",
                        "character" => "character",
                        "series" => "parody",
                        "group" => "group",
                        "language" => "language",
                        _ => string.Empty,
                    };
                    var formatted = TagNames.Format(category, TagNames.CleanName(slug));
                    if (formatted.Length == 0)
                        continue;
                    bag.TryAdd(formatted, 1);
                }
            }
        }

        return bag.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static readonly Regex EhTagHref = new(
        @"/tag/([^""'#/?\s]+):([^""'#/?\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EhTagTitle = new(
        @"title\s*=\s*[""']([a-z][a-z0-9]{1,20}):([^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private async Task<IReadOnlyList<(string Tag, int Count)>> DownloadEhentaiTagsAsync(
        IProgress<TagDownloadProgress>? progress,
        CancellationToken ct)
    {
        const string host = "https://e-hentai.org/";
        const int pages = 4;
        var bag = new Dictionary<string, int>(StringComparer.Ordinal);
        var pairs = new List<(string Gid, string Token)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var page = 0; page < pages; page++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new TagDownloadProgress(page, pages + 1, "E-Hentai-Liste Seite " + (page + 1) + " …", bag.Count));
            string html;
            try
            {
                html = await GetStringAsync(host + "?page=" + page, host, ct);
            }
            catch (ApiSearchException ex)
            {
                Log?.Invoke("E-Hentai Tag-Liste Seite " + (page + 1) + ": " + ex.Message);
                if (page == 0 && bag.Count == 0)
                    throw;
                break;
            }

            foreach (var pair in ReadEhGalleryPairs(html))
            {
                if (seen.Add(pair.Gid))
                    pairs.Add(pair);
            }

            CollectEhentaiTags(html, bag);
        }

        for (var offset = 0; offset < pairs.Count; offset += 25)
        {
            ct.ThrowIfCancellationRequested();
            var batch = pairs.Skip(offset).Take(25).ToList();
            progress?.Report(new TagDownloadProgress(pages, pages + 1, "E-Hentai gdata " + (offset + batch.Count) + "/" + pairs.Count + " …", bag.Count));
            try
            {
                foreach (var hit in await FetchEhentaiGdataAsync(batch, ct))
                {
                    if (hit.TagHarvest is null)
                        continue;
                    foreach (var (tag, count) in hit.TagHarvest)
                        RememberEhTag(bag, tag, count);
                }
            }
            catch (Exception ex) when (ex is ApiSearchException or HttpRequestException or JsonException)
            {
                Log?.Invoke("E-Hentai Tag-Index gdata: " + ex.Message);
            }
        }

        progress?.Report(new TagDownloadProgress(pages + 1, pages + 1, "E-Hentai-Index fertig", bag.Count));
        if (bag.Count == 0)
            throw new ApiSearchException("E-Hentai-Tag-Index leer. " + BlockedHint, blocked: true);
        return bag.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static void CollectEhentaiTags(string html, Dictionary<string, int> bag)
    {
        foreach (Match match in EhTagHref.Matches(html))
        {
            var formatted = FormatEhentaiTag(
                WebUtility.UrlDecode(match.Groups[1].Value) + ":" +
                WebUtility.UrlDecode(match.Groups[2].Value).Replace('+', ' '));
            RememberEhTag(bag, formatted, 1);
        }

        foreach (Match match in EhTagTitle.Matches(html))
        {
            var formatted = FormatEhentaiTag(match.Groups[1].Value + ":" + WebUtility.HtmlDecode(match.Groups[2].Value));
            RememberEhTag(bag, formatted, 1);
        }
    }

    private static void RememberEhTag(Dictionary<string, int> bag, string formatted, int count)
    {
        if (formatted.Length == 0 || count <= 0)
            return;
        bag[formatted] = Math.Max(bag.GetValueOrDefault(formatted), count);
    }

    private static int ParseCompactCount(string raw)
    {
        var text = raw.Trim().Replace(",", "");
        var multiplier = 1;
        if (text.EndsWith("K", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1_000;
            text = text[..^1];
        }
        else if (text.EndsWith("M", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1_000_000;
            text = text[..^1];
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Max(1, (int)Math.Round(value * multiplier))
            : 1;
    }

    private async Task<string?> TryGetStringAsync(string url, string referer, CancellationToken ct)
    {
        try
        {
            return await GetStringAsync(url, referer, ct);
        }
        catch (ApiSearchException)
        {
            return null;
        }
    }

    // ── HTTP ────────────────────────────────────────────────────────────

    private async Task<string> GetStringAsync(string url, string referer, CancellationToken ct)
    {
        var bytes = await SendAsync(HttpMethod.Get, url, referer, body: null, ct);
        return Encoding.UTF8.GetString(bytes);
    }

    private async Task<byte[]> GetBytesAsync(string url, string referer, CancellationToken ct) =>
        await SendAsync(HttpMethod.Get, url, referer, body: null, ct);

    private async Task<string> PostJsonAsync(string url, string json, string referer, CancellationToken ct)
    {
        var bytes = await SendAsync(HttpMethod.Post, url, referer, json, ct);
        return Encoding.UTF8.GetString(bytes);
    }

    private async Task<byte[]> SendAsync(HttpMethod method, string url, string referer, string? body, CancellationToken ct)
    {
        var wantsJson = url.Contains("/api/", StringComparison.OrdinalIgnoreCase)
                        || url.Contains("api.", StringComparison.OrdinalIgnoreCase)
                        || body is not null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            ApplyBrowserHeaders(request, referer, wantsJson);
            if (body is not null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new ApiSearchException(DescribeHttp(ex));
            }

            using (response)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                var text = Encoding.UTF8.GetString(bytes);

                if ((int)response.StatusCode == 429)
                {
                    Log?.Invoke("HTTP 429 Rate-Limit von " + url + " – warte und versuche erneut.");
                    await Task.Delay(1500 * (attempt + 1), ct);
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable
                    || LooksLikeCloudflare(text))
                {
                    throw new ApiSearchException(
                        "Zugriff blockiert (HTTP " + (int)response.StatusCode + ", Cloudflare Access Denied). " + BlockedHint,
                        blocked: true);
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new ApiSearchException(
                        "HTTP " + (int)response.StatusCode + " von " + new Uri(url).Host +
                        (string.IsNullOrWhiteSpace(text) ? "." : ": " + TrimForLog(text)));
                }

                return bytes;
            }
        }

        throw new ApiSearchException("Die Anfrage wurde nach einem Rate-Limit nicht beantwortet.");
    }

    private void ApplyBrowserHeaders(HttpRequestMessage request, string referer, bool wantsJson)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.TryAddWithoutValidation("Accept", wantsJson
            ? "application/json, text/javascript, */*;q=0.8"
            : "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation("Sec-Ch-Ua", SecChUa);
        request.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Mobile", "?0");
        request.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Platform", "\"Windows\"");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", wantsJson ? "empty" : "document");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", wantsJson ? "cors" : "navigate");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        if (!wantsJson)
            request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        if (Uri.TryCreate(referer, UriKind.Absolute, out var uri))
        {
            request.Headers.Referrer = uri;
            request.Headers.TryAddWithoutValidation("Origin", uri.GetLeftPart(UriPartial.Authority));
        }
    }

    private void AddCookie(StoredHttpCookie item)
    {
        var domain = HttpCookieImport.NormalizeDomain(item.Domain);
        if (domain.Length == 0 || item.Name.Length == 0)
            return;
        try
        {
            var cookie = new Cookie(item.Name, item.Value ?? string.Empty, string.IsNullOrEmpty(item.Path) ? "/" : item.Path, domain)
            {
                Secure = true,
            };
            _cookies.Add(new Uri("https://" + domain + "/"), cookie);
        }
        catch (CookieException ex)
        {
            Log?.Invoke("Cookie " + item.Name + " @" + domain + " ungültig: " + ex.Message);
        }
    }

    private string? WriteNetscapeCookieFile(string domain)
    {
        try
        {
            List<string> lines;
            lock (_cookieGate)
            {
                lines = new List<string> { "# Netscape HTTP Cookie File" };
                foreach (Cookie cookie in _cookies.GetAllCookies())
                {
                    var host = HttpCookieImport.NormalizeDomain(cookie.Domain);
                    if (host.Length == 0)
                        continue;
                    if (host != domain && !host.EndsWith("." + domain, StringComparison.Ordinal)
                        && domain != host && !domain.EndsWith("." + host, StringComparison.Ordinal))
                        continue;
                    var flag = "." + host;
                    var secure = cookie.Secure ? "TRUE" : "FALSE";
                    var exp = cookie.Expires == DateTime.MinValue
                        ? "0"
                        : new DateTimeOffset(DateTime.SpecifyKind(cookie.Expires, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString();
                    lines.Add($"{flag}\tTRUE\t{cookie.Path}\t{secure}\t{exp}\t{cookie.Name}\t{cookie.Value}");
                }
            }

            if (lines.Count <= 1)
                return null;

            var directory = Path.Combine(Path.GetTempPath(), "MangaLibraryApp");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "cookies." + domain.Replace('.', '_') + ".txt");
            File.WriteAllLines(path, lines);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke("Cookie-Datei für gallery-dl: " + ex.Message);
            return null;
        }
    }

    private static JsonDocument ParseJson(string text, string name)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is not ('{' or '['))
        {
            if (LooksLikeCloudflare(text))
                throw new ApiSearchException(name + " hat HTML statt JSON geliefert (Cloudflare). " + BlockedHint, blocked: true);
            throw new ApiSearchException(name + " hat keine JSON-Antwort geliefert.");
        }

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            if (LooksLikeCloudflare(text))
                throw new ApiSearchException(name + " hat HTML statt JSON geliefert (Cloudflare). " + BlockedHint, blocked: true);
            throw new ApiSearchException("Ungültiges JSON von " + name + ": " + ex.Message);
        }
    }

    private static bool LooksLikeCloudflare(string text) =>
        text.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase)
        || text.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Attention Required! | Cloudflare", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Access denied", StringComparison.OrdinalIgnoreCase);

    private static string DescribeHttp(HttpRequestException ex) =>
        ex.StatusCode is { } code
            ? $"HTTP {(int)code}: die API ist nicht erreichbar."
            : "Keine Verbindung zur API: " + ex.Message;

    private static string TrimForLog(string text)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= 160 ? flat : flat[..157] + "…";
    }

    private static string BuildQuery(string site, IReadOnlyList<string> tags, string? extra)
    {
        var parts = new List<string>();
        foreach (var tag in tags)
        {
            var formatted = FormatTag(site, tag);
            if (formatted.Length > 0)
                parts.Add(formatted);
        }

        if (!string.IsNullOrWhiteSpace(extra))
            parts.Add(extra.Trim());

        return string.Join(' ', parts);
    }

    private static string FormatTag(string site, string raw)
    {
        var tag = TagNames.Normalize(raw);
        if (tag.Length == 0)
            return string.Empty;

        var (category, name) = TagNames.Split(tag);
        return site switch
        {
            "nhentai" => FormatNhentaiQueryTag(category, name),
            "ehentai" or "exhentai" => category.Length == 0
                ? Quote(name)
                : category + ":" + Quote(name),
            "hitomi" => category.Length == 0 ? name : category + ":" + name,
            _ => name,
        };
    }

    /// <summary>NClientV3 <c>Tag.toQueryTag</c>: <c>artist:"name"</c>, UND über Leerzeichen.</summary>
    private static string FormatNhentaiQueryTag(string category, string name)
    {
        var quoted = name.Contains(' ', StringComparison.Ordinal) ? "\"" + name + "\"" : name;
        return category.Length == 0 ? quoted : category + ":" + quoted;
    }

    private static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? "\"" + value + "\"" : value;

    private void RememberPage(int page, int hitCount, int? totalPages, int? total)
    {
        LastPage = page;
        if (total is not null)
            LastTotal = total;
        if (totalPages is not null)
            LastTotalPages = totalPages;
        LastHasMore = hitCount > 0 && (totalPages is int pages ? page < pages : hitCount >= 15);
    }

    private static int? ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el))
            return null;
        if (el.TryGetInt32(out var n))
            return n;
        return el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var parsed) ? parsed : null;
    }

    private static int? ReadCount(JsonElement root)
    {
        foreach (var name in new[] { "num_results", "total", "total_count", "result_count" })
        {
            var value = ReadInt(root, name);
            if (value is > 0)
                return value;
        }

        return null;
    }

    private static readonly Regex EhTotal = new(@"of\s+([\d,]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static int? ReadEhTotal(string html)
    {
        var match = EhTotal.Match(html);
        if (!match.Success)
            return null;
        var digits = match.Groups[1].Value.Replace(",", string.Empty);
        return int.TryParse(digits, out var n) ? n : null;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static string? ReadNestedString(JsonElement root, string parent, string child) =>
        root.TryGetProperty(parent, out var p) && p.ValueKind == JsonValueKind.Object ? ReadString(p, child) : null;

    private static string? ReadNumber(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.String => el.GetString(),
            _ => null,
        };
    }
}
