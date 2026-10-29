using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NekoDex.Core.Enums;
using NekoDex.Core.Models;
using AppSettings = MangaLibraryApp.AppSettings;
using GalleryDl = MangaLibraryApp.GalleryDl;
using TagDownloadProgress = MangaLibraryApp.TagDownloadProgress;
using TagNames = MangaLibraryApp.TagNames;
using WebSearchViewModel = MangaLibraryApp.WebSearchViewModel;

namespace NekoDex.Services.Scrapers
{
    /// <summary>
    /// HTML-Katalog auf <c>https://simplyhentai.org/</c>. Es gibt hier keine API v3.
    /// Ein Tag geht auf die Slug-Seite (<c>full color</c> → <c>/tag/full-color/</c>).
    /// Mehrere Tags verknüpft <c>/search/?q=</c> mit den Namen, ohne Anführungszeichen.
    /// gallery-dl bekommt <c>https://simplyhentai.org/g/{id}/</c>.
    /// </summary>
    public sealed class SimplyHentaiProvider : IScraperProvider, ITagCatalogSource, ISearchPageHint
    {
        public const string Site = "simplyhentai";

        private const string Root = "https://simplyhentai.org/";
        private const int FullPageCap = 80;

        private static readonly (string Path, string Kind)[] TagRoots =
        {
            ("tags", "tag"),
            ("artists", "artist"),
            ("characters", "character"),
            ("parodies", "parody"),
            ("groups", "group"),
            ("category", "category"),
        };

        private static readonly Regex GalleryCard = new(
            """<a href="(?:https?://(?:www\.)?simplyhentai\.org)?/g/(?<id>\d+)/"[^>]*>.*?<img\b[^>]*\b(?:data-src|src)="(?<cover>https?://[^"]+)"[^>]*>.*?<div class="caption">(?<title>.*?)</div>""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex TagLink = new(
            """<a href="/(?<kind>tag|artist|character|parody|group|category|language)/(?<slug>[^"#?/]+)/?" class="tag"><span class="name">(?<name>.*?)</span><span class="count">(?<count>[^<]*)</span></a>""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex PageLink = new(
            """[?&]page=(?<page>\d+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly CookieContainer _cookies = new();
        private readonly HttpClient _http;
        private bool _useGalleryDlAgent;
        private bool _hasClearance;
        private bool _cookieRetried;
        private string? _blockMessage;
        private string _pageQuery = string.Empty;
        private bool _pageFallback;

        public bool HasFurtherPages { get; private set; }

        public SimplyHentaiProvider()
        {
            _http = SiteHttp.CreateBrowser(_cookies);
        }

        public SourceProvider Provider => SourceProvider.SimplyHentai;

        public string BaseUrl => Root;

        public bool SupportsTagSearch => true;

        public string SiteId => Site;

        public WebSearchViewModel? TagBlacklist { get; set; }

        public string? BlockStatus => _blockMessage;

        /// <summary>Gesetzte URL und Antwortgröße, wenn die Suche nichts liefert.</summary>
        public string? SearchDiagnostic { get; private set; }

        public void ApplyGalleryDlSession(IEnumerable<MangaLibraryApp.StoredHttpCookie>? stored)
        {
            _useGalleryDlAgent = false;
            _hasClearance = false;
            _cookieRetried = false;
            _blockMessage = null;
            var added = ApplySessionCookies(stored);
            foreach (var host in new[] { "simplyhentai.org", "www.simplyhentai.org" })
            {
                foreach (var path in GalleryDlCookieFiles(host))
                {
                    if (!File.Exists(path))
                        continue;
                    try
                    {
                        added += ApplySessionCookies(MangaLibraryApp.HttpCookieImport.Parse(File.ReadAllText(path), host));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Die Cookie-Datei bleibt optional.
                    }
                }
            }

            _useGalleryDlAgent = added > 0;
        }

        public string BuildSearchUrl(string query, int page)
        {
            var terms = ParseTerms(query);
            return terms.Count == 0 ? Root : ListingUrl(terms, Math.Max(1, page)).Primary;
        }

        public async Task<IEnumerable<GalleryResult>> SearchAsync(
            string query,
            int page,
            AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            _ = settings;
            _blockMessage = null;
            SearchDiagnostic = null;
            var terms = ParseTerms(query);
            if (terms.Count == 0)
            {
                NoteEmpty("SimplyHentai: keine Suchbegriffe nach der Slug-Bereinigung.");
                HasFurtherPages = false;
                return Array.Empty<GalleryResult>();
            }

            var current = Math.Max(1, page);
            if (current > GalleryPages.SafetyCap)
            {
                HasFurtherPages = false;
                return Array.Empty<GalleryResult>();
            }

            var (primary, fallback) = ListingUrl(terms, current);
            var useFallback = false;
            string? html = null;
            if (current > 1 && string.Equals(_pageQuery, query, StringComparison.Ordinal))
                useFallback = _pageFallback;
            else
            {
                try
                {
                    html = await GetHtmlAsync(primary, cancellationToken).ConfigureAwait(false);
                    if (ReadGalleries(html).Count == 0 && fallback is not null)
                        useFallback = true;
                }
                catch (HttpRequestException ex) when (IsNotFound(ex) && fallback is not null)
                {
                    useFallback = true;
                }
                catch (InvalidOperationException ex) when (IsAntiBot(ex.Message))
                {
                    NoteBlocked(primary);
                    HasFurtherPages = false;
                    return Array.Empty<GalleryResult>();
                }

                _pageQuery = query;
                _pageFallback = useFallback;
            }

            var url = useFallback ? fallback ?? primary : primary;
            try
            {
                if (useFallback || html is null)
                    html = await GetHtmlAsync(url, cancellationToken).ConfigureAwait(false);
                var hits = ReadGalleries(html);
                if (hits.Count == 0)
                    NoteEmpty("SimplyHentai: 0 Treffer. URL: " + url + ".");
                HasFurtherPages = GalleryPages.Continues(hits.Count, current, html, url);
                return hits;
            }
            catch (InvalidOperationException ex) when (IsAntiBot(ex.Message))
            {
                NoteBlocked(url);
                HasFurtherPages = false;
                return Array.Empty<GalleryResult>();
            }
        }

        public async Task<IReadOnlyList<CatalogTag>> GetOnlineTagsAsync(
            bool fullIndex = false,
            IProgress<TagDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var bag = new Dictionary<string, CatalogTag>(StringComparer.Ordinal);
            string? blocked = null;
            var cap = fullIndex ? FullPageCap : 1;
            foreach (var (path, kind) in TagRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await WalkRootAsync(bag, Root + path, kind, cap, progress, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    blocked = IsAntiBot(ex.Message)
                        ? "simplyhentai.org hat die Anfrage mit einer Anti-Bot-Seite beantwortet."
                        : ex.Message;
                    _blockMessage = blocked;
                }
                catch (HttpRequestException ex)
                {
                    blocked = ex.Message;
                    _blockMessage = blocked;
                }
            }

            if (bag.Count == 0 && blocked is not null)
                throw new InvalidOperationException(blocked);
            if (bag.Count == 0 && fullIndex)
                throw new InvalidOperationException(_blockMessage ?? "SimplyHentai lieferte keinen bereinigten Tagkatalog.");

            return bag.Values
                .OrderByDescending(tag => tag.Count)
                .ThenBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task WalkRootAsync(
            Dictionary<string, CatalogTag> bag,
            string root,
            string kind,
            int cap,
            IProgress<TagDownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            var pages = 1;
            for (var page = 1; page <= pages && page <= cap; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var url = page == 1 ? root : root + "?page=" + page.ToString();
                string html;
                try
                {
                    html = await GetHtmlAsync(url, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (ex.Message.Contains("404", StringComparison.Ordinal))
                {
                    return;
                }

                if (page == 1)
                    pages = HighestPage(html, page);
                var added = RememberTags(bag, html, kind);
                progress?.Report(new TagDownloadProgress(
                    page,
                    Math.Min(cap, pages),
                    "SimplyHentai: " + kind + " Seite " + page,
                    bag.Count));
                if (added == 0)
                    break;
                if (page < Math.Min(cap, pages))
                    await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            }
        }

        private List<GalleryResult> ReadGalleries(string html)
        {
            var list = new List<GalleryResult>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in GalleryCard.Matches(html))
            {
                var id = match.Groups["id"].Value;
                if (!seen.Add(id))
                    continue;

                var title = Clean(match.Groups["title"].Value);
                if (title.Length == 0)
                    continue;

                var tags = new List<string>();
                list.Add(new GalleryResult
                {
                    Title = title,
                    CoverUrl = WebUtility.HtmlDecode(match.Groups["cover"].Value),
                    GalleryUrl = Root + "g/" + id + "/",
                    GalleryId = id,
                    Tags = tags,
                    SourceSite = Site,
                });
            }

            if (TagBlacklist is { } filter)
                list.RemoveAll(item => !filter.Allows(item.Tags));
            return list;
        }

        private static int RememberTags(Dictionary<string, CatalogTag> bag, string html, string fallbackKind)
        {
            var before = bag.Count;
            foreach (Match match in TagLink.Matches(html))
            {
                var name = Clean(match.Groups["name"].Value);
                if (name.Length == 0)
                    continue;
                var kind = match.Groups["kind"].Value.ToLowerInvariant();
                if (kind.Length == 0)
                    kind = fallbackKind;
                var hasCount = TryCount(match.Groups["count"].Value, out var count);
                TagSanitizer.Add(bag, kind, name, hasCount ? count : 1, hasCount);
            }

            return bag.Count - before;
        }

        private static int HighestPage(string html, int current)
        {
            var highest = current;
            foreach (Match match in PageLink.Matches(html))
            {
                if (int.TryParse(match.Groups["page"].Value, out var page) && page > highest)
                    highest = page;
            }

            return highest;
        }

        private async Task<string> GetHtmlAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                return await DownloadAsync(url, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (IsAntiBot(ex.Message) && (_hasClearance || _useGalleryDlAgent) && !_cookieRetried)
            {
                _cookieRetried = true;
                _blockMessage = null;
                return await DownloadAsync(url, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<string> DownloadAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                return await SiteHttp.GetString(
                    _http,
                    url,
                    Root,
                    cancellationToken,
                    "text/html,application/xhtml+xml",
                    _useGalleryDlAgent ? GalleryDl.ChromeUserAgent : null).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("simplyhentai.org hat die Anfrage mit einer Anti-Bot-Seite beantwortet.");
            }
        }

        private int ApplySessionCookies(IEnumerable<MangaLibraryApp.StoredHttpCookie>? cookies)
        {
            if (cookies is null)
                return 0;

            var added = 0;
            foreach (var cookie in cookies)
            {
                var domain = (cookie.Domain ?? string.Empty).Trim().TrimStart('.');
                if (!IsSiteDomain(domain) || string.IsNullOrWhiteSpace(cookie.Name) || string.IsNullOrWhiteSpace(cookie.Value))
                    continue;
                if (cookie.Name.Equals("cf_clearance", StringComparison.OrdinalIgnoreCase))
                    _hasClearance = true;

                try
                {
                    _cookies.Add(new Cookie(
                        cookie.Name.Trim(),
                        cookie.Value,
                        string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path,
                        ".simplyhentai.org")
                    {
                        Secure = true,
                    });
                    added++;
                }
                catch (CookieException)
                {
                    // Dasselbe Cookie liegt schon in der Sitzung.
                }
            }

            return added;
        }

        private static List<string> Terms(string? query)
        {
            var text = (query ?? string.Empty).Trim();
            if (text.Length == 0)
                return new List<string>();
            if (text.Contains('\u001f'))
                return text.Split('\u001f', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            return new List<string> { text };
        }

        /// <summary><c>full color</c> → <c>full-color</c>. Buchstaben und Ziffern bleiben, der Rest wird ein Bindestrich.</summary>
        private static string ToSlug(string name)
        {
            var sb = new StringBuilder(name.Length);
            var dash = false;
            foreach (var ch in name.Trim().ToLowerInvariant())
            {
                if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
                {
                    sb.Append(ch);
                    dash = false;
                }
                else if (!dash && sb.Length > 0)
                {
                    sb.Append('-');
                    dash = true;
                }
            }

            if (dash && sb.Length > 0)
                sb.Length--;
            return sb.ToString();
        }

        private readonly record struct SearchTerm(string Segment, string Slug, string Name);

        private static List<SearchTerm> ParseTerms(string? query)
        {
            var terms = new List<SearchTerm>();
            foreach (var part in Terms(query))
            {
                var normalized = TagNames.Normalize(part);
                if (normalized.Length == 0)
                    continue;
                var (category, name) = TagNames.Split(normalized);
                if (name.Length == 0)
                    continue;
                var slug = ToSlug(name);
                if (slug.Length == 0)
                    continue;
                terms.Add(new SearchTerm(SegmentFor(category), slug, name));
            }

            return terms;
        }

        /// <summary>Die Site legt Tags, Künstler und Parodien in eigene Pfade. <c>female:</c> liegt dort unter <c>/tag/</c>.</summary>
        private static string SegmentFor(string category) => category switch
        {
            "artist" => "artist",
            "character" => "character",
            "parody" => "parody",
            "group" => "group",
            "language" => "language",
            "category" => "category",
            _ => "tag",
        };

        private static (string Primary, string? Fallback) ListingUrl(IReadOnlyList<SearchTerm> terms, int page)
        {
            var search = Root + "search/?q=" + Uri.EscapeDataString(string.Join(' ', terms.Select(term => term.Name)));
            if (page > 1)
                search += "&page=" + page.ToString(CultureInfo.InvariantCulture);
            if (terms.Count != 1)
                return (search, null);

            var slug = Root + terms[0].Segment + "/" + terms[0].Slug + "/";
            if (page > 1)
                slug += "?page=" + page.ToString(CultureInfo.InvariantCulture);
            return (slug, search);
        }

        private void NoteEmpty(string message)
        {
            SearchDiagnostic = message;
            Trace.WriteLine(message);
        }

        private void NoteBlocked(string url)
        {
            _blockMessage = "simplyhentai.org hat die Anfrage mit einer Anti-Bot-Seite beantwortet.";
            NoteEmpty("SimplyHentai: Anti-Bot. URL: " + url);
        }

        private static bool IsNotFound(HttpRequestException ex) =>
            ex.Message.Contains("404", StringComparison.Ordinal);

        private static bool TryCount(string raw, out int count)
        {
            var text = raw.Trim().Replace(",", string.Empty);
            var factor = 1d;
            if (text.EndsWith("K", StringComparison.OrdinalIgnoreCase))
            {
                factor = 1_000d;
                text = text[..^1];
            }
            else if (text.EndsWith("M", StringComparison.OrdinalIgnoreCase))
            {
                factor = 1_000_000d;
                text = text[..^1];
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                count = (int)Math.Round(value * factor);
                return count > 0;
            }

            count = 0;
            return false;
        }

        private static string Clean(string raw)
        {
            var text = WebUtility.HtmlDecode(raw).Replace('\n', ' ').Replace('\r', ' ').Trim();
            while (text.Contains("  ", StringComparison.Ordinal))
                text = text.Replace("  ", " ", StringComparison.Ordinal);
            return text;
        }

        private static bool IsSiteDomain(string domain) =>
            domain.Equals("simplyhentai.org", StringComparison.OrdinalIgnoreCase)
            || domain.EndsWith(".simplyhentai.org", StringComparison.OrdinalIgnoreCase);

        private static bool IsAntiBot(string message) =>
            message.Contains("Anti-Bot", StringComparison.Ordinal);

        private static IEnumerable<string> GalleryDlCookieFiles(string host)
        {
            var file = "cookies." + host.Replace('.', '_') + ".txt";
            yield return Path.Combine(Path.GetTempPath(), "MangaLibraryApp", file);
            var tools = Path.GetDirectoryName(MangaLibraryApp.GalleryDlService.ToolsPath);
            if (!string.IsNullOrEmpty(tools))
            {
                yield return Path.Combine(tools, file);
                yield return Path.Combine(tools, "cookies.txt");
            }

            var bundled = Path.GetDirectoryName(MangaLibraryApp.GalleryDlService.BundledPath);
            if (!string.IsNullOrEmpty(bundled))
            {
                yield return Path.Combine(bundled, file);
                yield return Path.Combine(bundled, "cookies.txt");
            }
        }
    }
}
