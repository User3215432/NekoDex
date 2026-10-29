using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NekoDex.Core.Enums;
using NekoDex.Core.Models;
using AppSettings = MangaLibraryApp.AppSettings;
using TagDownloadProgress = MangaLibraryApp.TagDownloadProgress;
using TagNames = MangaLibraryApp.TagNames;
using WebSearchViewModel = MangaLibraryApp.WebSearchViewModel;

namespace NekoDex.Services.Scrapers
{
    internal readonly record struct SiteTerm(string Kind, string Name, string Slug);

    /// <summary>Begriffe aus der Quellenleiste. Ein Chip bleibt ein Begriff, auch mit Leerzeichen.</summary>
    internal static class SiteTerms
    {
        public static List<SiteTerm> Parse(string? query, WebSearchViewModel? blacklist)
        {
            var raw = query ?? string.Empty;
            var pieces = raw.Contains('\u001f')
                ? raw.Split('\u001f', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<SiteTerm>();
            foreach (var piece in pieces)
            {
                if (piece.StartsWith('-'))
                    continue;

                var text = piece.Replace('_', ' ').Trim();
                var colon = text.IndexOf(':');
                var prefix = colon > 0 ? text[..colon].Trim() : string.Empty;
                var (category, name) = TagNames.Split(text);
                if (name.Length == 0)
                    name = text;
                if (name.Length == 0)
                    continue;
                if (blacklist is { } filter && !filter.Allows(new[] { name }))
                    continue;

                var slug = ToSlug(name);
                if (slug.Length == 0)
                    continue;
                var kind = category;
                if (prefix.Equals("genre", StringComparison.OrdinalIgnoreCase)
                    || prefix.Equals("genres", StringComparison.OrdinalIgnoreCase))
                    kind = "genre";
                else if (string.IsNullOrEmpty(kind))
                    kind = "tag";
                list.Add(new SiteTerm(kind, name, slug));
            }

            return list;
        }

        public static string ToSlug(string name)
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

            return sb.ToString().Trim('-');
        }

        public static string Segment(string kind) => kind.ToLowerInvariant() switch
        {
            "artist" or "author" => "artist",
            "character" => "character",
            "parody" or "series" or "magazine" => "parody",
            "group" or "circle" => "group",
            "language" => "language",
            "category" => "category",
            _ => "tag",
        };

        public static string ReadSegment(string kind) => kind.ToLowerInvariant() switch
        {
            "artist" or "author" => "artist",
            "character" => "character",
            "parody" or "series" => "parody",
            "group" or "circle" => "circle",
            "convention" => "convention",
            "genre" => "genre",
            _ => "tag",
        };

        public static string Clean(string raw)
        {
            var text = WebUtility.HtmlDecode(raw ?? string.Empty);
            text = Regex.Replace(text, "<[^>]+>", " ");
            return Regex.Replace(text, @"\s+", " ").Trim();
        }
    }

    internal static class HtmlCatalog
    {
        public static int HighestPage(string html, int current)
        {
            var highest = current;
            foreach (Match match in Regex.Matches(html, @"(?:[?&]page=|/page/)(\d+)"))
            {
                if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var page)
                    && page > highest
                    && page < 100_000)
                    highest = page;
            }

            return highest;
        }

        public static async Task<int> Walk(
            Func<string, CancellationToken, Task<string>> fetch,
            Func<int, string> urlFor,
            Func<string, int> harvest,
            int cap,
            string label,
            IProgress<TagDownloadProgress>? progress,
            Func<int> stored,
            CancellationToken cancellationToken)
        {
            var total = 0;
            var pages = 1;
            for (var page = 1; page <= pages && page <= cap; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string html;
                try
                {
                    html = await fetch(urlFor(page), cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (ex.Message.Contains("404", StringComparison.Ordinal))
                {
                    return total;
                }

                if (page == 1)
                    pages = HighestPage(html, 1);
                var fresh = harvest(html);
                total += fresh;
                progress?.Report(new TagDownloadProgress(
                    page,
                    Math.Min(cap, pages),
                    label + " Seite " + page.ToString(CultureInfo.InvariantCulture),
                    stored()));
                if (fresh == 0)
                    break;
                if (page < Math.Min(cap, pages))
                    await Task.Delay(80, cancellationToken).ConfigureAwait(false);
            }

            return total;
        }
    }

    /// <summary>
    /// Entscheidet, ob nach einer einzelnen Suchseite noch eine weitere Seite existieren kann.
    /// Die Suche selbst lädt immer nur diese eine Seite.
    /// </summary>
    internal static class GalleryPages
    {
        public const int SafetyCap = 250;
        public const int FullPageHint = 15;

        public static bool Continues(int count, int page, string? html = null, string? pageUrl = null)
        {
            if (count <= 0 || page >= SafetyCap)
                return false;
            if (html is not null && pageUrl is not null && ListingLastPage(html, pageUrl, page) > page)
                return true;
            return count >= FullPageHint;
        }

        public static int ListingLastPage(string html, string pageUrl, int current)
        {
            var marker = Marker(pageUrl);
            var highest = current;
            foreach (Match match in Regex.Matches(html, """href="([^"]+)""", RegexOptions.IgnoreCase))
            {
                var href = WebUtility.HtmlDecode(match.Groups[1].Value);
                if (marker.Length > 0 && href.IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                var pageNo = Regex.Match(href, @"(?:[?&]page=|/page/)(\d+)");
                if (pageNo.Success
                    && int.TryParse(pageNo.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var page)
                    && page > highest
                    && page < 100_000)
                    highest = page;
            }

            var titled = Regex.Match(html, @"Page\s+\d+\s+of\s+([\d,]+)", RegexOptions.IgnoreCase);
            if (titled.Success
                && int.TryParse(titled.Groups[1].Value.Replace(",", string.Empty), NumberStyles.None, CultureInfo.InvariantCulture, out var of)
                && of > highest
                && of < 100_000)
                highest = of;
            return highest;
        }

        private static string Marker(string pageUrl)
        {
            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
                return string.Empty;
            var path = uri.AbsolutePath;
            var cut = Regex.Match(path, @"/page/\d+/?$", RegexOptions.IgnoreCase);
            if (cut.Success)
                path = path[..cut.Index];
            if (path.Length == 0)
                path = "/";
            if (!path.EndsWith('/'))
                path += "/";
            return path;
        }
    }

    /// <summary>
    /// Next.js-Katalog auf <c>https://hentairun.com/</c>.
    /// Ein Tag: <c>/tag/full-color/</c> und <c>/tag/full-color/page/2/</c>.
    /// Mehrere Tags: <c>/search/?q=</c> mit <c>&amp;page=</c>.
    /// Karten: <c>/gallery/{id}/</c>, Cover <c>m*.hentairun.com/.../thumb.jpg</c>.
    /// </summary>
    public sealed class HentaiRunProvider : IScraperProvider, ITagCatalogSource, ISearchPageHint
    {
        public const string Site = "hentairun";
        private const string Root = "https://hentairun.com/";
        private const int TagPageCap = 800;
        private const int OtherPageCap = 500;

        public bool HasFurtherPages { get; private set; }

        private static readonly (string Path, string Kind)[] OtherRoots =
        {
            ("artists", "artist"),
            ("characters", "character"),
            ("parodies", "parody"),
            ("groups", "group"),
        };

        private static readonly Regex GalleryCard = new(
            """href="(?:https://hentairun\.com)?/gallery/(?<id>\d+)/"[\s\S]{0,900}?alt="(?<title>[^"]*)"[\s\S]{0,400}?src="(?<cover>https://m\d+\.hentairun\.com/[^"]+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex IndexCard = new(
            """aria-label="(?<name>[^"]+)"[^>]*href="/(?<kind>tag|artist|character|parody|group)/(?<slug>[^"#?/]+)/""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex IndexText = new(
            """href="/(?<kind>tag|artist|character|parody|group)/(?<slug>[^"#?/]+)/+"[^>]*>(?<name>[^<]+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly HttpClient _http = SiteHttp.Create();

        public SourceProvider Provider => SourceProvider.HentaiRun;
        public string BaseUrl => Root;
        public bool SupportsTagSearch => true;
        public string SiteId => Site;
        public WebSearchViewModel? TagBlacklist { get; set; }

        public string BuildSearchUrl(string query, int page)
        {
            var terms = SiteTerms.Parse(query, TagBlacklist);
            if (terms.Count == 0)
                return Root;
            if (terms.Count == 1 && terms[0].Slug.Length > 0)
            {
                var url = Root + SiteTerms.Segment(terms[0].Kind) + "/" + terms[0].Slug + "/";
                if (page > 1)
                    url += "page/" + page.ToString(CultureInfo.InvariantCulture) + "/";
                return url;
            }

            var search = Root + "search/?q=" + Uri.EscapeDataString(string.Join(' ', terms.ConvertAll(term => term.Name)));
            if (page > 1)
                search += "&page=" + page.ToString(CultureInfo.InvariantCulture);
            return search;
        }

        public async Task<IEnumerable<GalleryResult>> SearchAsync(string query, int page, AppSettings settings, CancellationToken cancellationToken = default)
        {
            _ = settings;
            var terms = SiteTerms.Parse(query, TagBlacklist);
            var current = Math.Max(1, page);
            if (terms.Count == 0 || current > GalleryPages.SafetyCap)
            {
                HasFurtherPages = false;
                return Array.Empty<GalleryResult>();
            }

            var url = BuildSearchUrl(query, current);
            var html = await GetAsync(url, cancellationToken).ConfigureAwait(false);
            var hits = ReadGalleries(html);
            HasFurtherPages = GalleryPages.Continues(hits.Count, current, html, url);
            return hits;
        }

        public async Task<IReadOnlyList<CatalogTag>> GetOnlineTagsAsync(bool fullIndex = false, IProgress<TagDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var bag = new Dictionary<string, CatalogTag>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? blocked = null;

            async Task WalkOne(string path, string kind, int cap, string label)
            {
                try
                {
                    await HtmlCatalog.Walk(
                        GetAsync,
                        page => PageUrl(path, page),
                        html => Harvest(bag, seen, html, kind),
                        cap,
                        label,
                        progress,
                        () => bag.Count,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    blocked = ex.Message;
                }
                catch (HttpRequestException ex)
                {
                    blocked = ex.Message;
                }
            }

            await WalkOne("tags", "tag", TagPageCap, "HentaiRun: Tags").ConfigureAwait(false);
            await WalkOne("tags/popular", "tag", TagPageCap, "HentaiRun: Tags beliebt").ConfigureAwait(false);
            foreach (var letter in "abcdefghijklmnopqrstuvwxyz")
                await WalkOne("tags/" + letter, "tag", 1, "HentaiRun: " + letter).ConfigureAwait(false);

            var otherCap = fullIndex ? OtherPageCap : 1;
            foreach (var (path, kind) in OtherRoots)
                await WalkOne(path, kind, otherCap, "HentaiRun: " + kind).ConfigureAwait(false);

            if (bag.Count == 0 && blocked is not null)
                throw new InvalidOperationException(blocked);
            if (bag.Count == 0)
                throw new InvalidOperationException("HentaiRun lieferte keinen bereinigten Tagkatalog.");

            return bag.Values
                .OrderByDescending(tag => tag.Count)
                .ThenBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static int Harvest(Dictionary<string, CatalogTag> bag, HashSet<string> seen, string html, string fallbackKind)
        {
            var fresh = 0;
            fresh += Take(bag, seen, IndexCard.Matches(html), fallbackKind);
            fresh += Take(bag, seen, IndexText.Matches(html), fallbackKind);
            return fresh;
        }

        private static int Take(Dictionary<string, CatalogTag> bag, HashSet<string> seen, MatchCollection matches, string fallbackKind)
        {
            var fresh = 0;
            foreach (Match match in matches)
            {
                var slug = match.Groups["slug"].Value.Trim().Trim('/');
                var name = SiteTerms.Clean(match.Groups["name"].Value);
                if (slug.Length == 0 || name.Length == 0)
                    continue;
                if (name.Equals("latest", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("popular", StringComparison.OrdinalIgnoreCase))
                    continue;
                var kind = match.Groups["kind"].Value.ToLowerInvariant();
                if (kind.Length == 0)
                    kind = fallbackKind;
                if (!seen.Add(kind + "\0" + slug))
                    continue;
                fresh++;
                TagSanitizer.Add(bag, kind, name, 1, hasCount: false, slug);
            }

            return fresh;
        }

        private static string PageUrl(string path, int page) =>
            page <= 1 ? Root + path + "/" : Root + path + "/page/" + page.ToString(CultureInfo.InvariantCulture) + "/";

        private Task<string> GetAsync(string url, CancellationToken cancellationToken) =>
            SiteHttp.GetString(_http, url, Root, cancellationToken);

        private List<GalleryResult> ReadGalleries(string html)
        {
            var list = new List<GalleryResult>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in GalleryCard.Matches(html))
            {
                var id = match.Groups["id"].Value;
                if (!seen.Add(id))
                    continue;
                var title = SiteTerms.Clean(match.Groups["title"].Value);
                if (title.Length == 0)
                    continue;
                list.Add(new GalleryResult
                {
                    Title = title,
                    CoverUrl = WebUtility.HtmlDecode(match.Groups["cover"].Value),
                    GalleryUrl = Root + "gallery/" + id + "/",
                    GalleryId = id,
                    SourceSite = Site,
                });
            }

            return list;
        }
    }

    /// <summary>
    /// PHP-Katalog auf <c>https://hentaiclap.com/</c>.
    /// Suche über das Formular <c>/search/?key=</c> und <c>&amp;page=</c>.
    /// <c>/tag/anal/</c> antwortet mit 404, die Tagliste liegt unter <c>/tags/popular/?page=</c>.
    /// Karten: <c>data-src</c> auf <c>m*.hentaiclap.com</c>, Titel in <c>h2</c>, Galerie <c>/gallery/{id}/</c>.
    /// </summary>
    public sealed class HentaiClapProvider : IScraperProvider, ITagCatalogSource, ISearchPageHint
    {
        public const string Site = "hentaiclap";
        private const string Root = "https://hentaiclap.com/";
        private const int FullPageCap = 800;

        public bool HasFurtherPages { get; private set; }

        private static readonly (string Path, string Kind)[] Roots =
        {
            ("tags", "tag"),
            ("artists", "artist"),
            ("characters", "character"),
            ("parodies", "parody"),
            ("groups", "group"),
        };

        private static readonly Regex GalleryCard = new(
            """href="/gallery/(?<id>\d+)/"[\s\S]{0,500}?data-src="(?<cover>https://[^"]+)"[\s\S]{0,2500}?href="/gallery/\k<id>/"><h2>(?<title>[^<]*)</h2>""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex IndexLink = new(
            """href="/(?<kind>tag|artist|character|parody|group|language|category)/(?<slug>[^"#?/]+)/"><h2>(?<name>[^<]+)</h2>""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly HttpClient _http = SiteHttp.Create();

        public SourceProvider Provider => SourceProvider.HentaiClap;
        public string BaseUrl => Root;
        public bool SupportsTagSearch => true;
        public string SiteId => Site;
        public WebSearchViewModel? TagBlacklist { get; set; }

        public string BuildSearchUrl(string query, int page)
        {
            var terms = SiteTerms.Parse(query, TagBlacklist);
            if (terms.Count == 0)
                return Root;
            var url = Root + "search/?key=" + Uri.EscapeDataString(string.Join(' ', terms.ConvertAll(term => term.Name)));
            if (page > 1)
                url += "&page=" + page.ToString(CultureInfo.InvariantCulture);
            return url;
        }

        public async Task<IEnumerable<GalleryResult>> SearchAsync(string query, int page, AppSettings settings, CancellationToken cancellationToken = default)
        {
            _ = settings;
            var terms = SiteTerms.Parse(query, TagBlacklist);
            if (terms.Count == 0)
            {
                HasFurtherPages = false;
                return Array.Empty<GalleryResult>();
            }

            var current = Math.Max(1, page);
            if (current > GalleryPages.SafetyCap)
            {
                HasFurtherPages = false;
                return Array.Empty<GalleryResult>();
            }

            var url = BuildSearchUrl(query, current);
            var html = await GetAsync(url, cancellationToken).ConfigureAwait(false);
            var hits = ReadGalleries(html);
            HasFurtherPages = GalleryPages.Continues(hits.Count, current, html, url);
            return hits;
        }

        public Task<IReadOnlyList<CatalogTag>> GetOnlineTagsAsync(bool fullIndex = false, IProgress<TagDownloadProgress>? progress = null, CancellationToken cancellationToken = default) =>
            CatalogSeed.Collect(Roots, fullIndex ? FullPageCap : 1, hasCount: false, IndexLink, PageUrl, GetAsync, "HentaiClap", progress, cancellationToken);

        private static string PageUrl(string path, int page)
        {
            var url = Root + path + "/";
            if (page > 1)
                url += "?page=" + page.ToString(CultureInfo.InvariantCulture);
            return url;
        }

        private Task<string> GetAsync(string url, CancellationToken cancellationToken) =>
            SiteHttp.GetString(_http, url, Root, cancellationToken);

        private List<GalleryResult> ReadGalleries(string html)
        {
            var list = new List<GalleryResult>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in GalleryCard.Matches(html))
            {
                var id = match.Groups["id"].Value;
                if (!seen.Add(id))
                    continue;
                var title = SiteTerms.Clean(match.Groups["title"].Value);
                if (title.Length == 0)
                    continue;
                list.Add(new GalleryResult
                {
                    Title = title,
                    CoverUrl = WebUtility.HtmlDecode(match.Groups["cover"].Value),
                    GalleryUrl = Root + "gallery/" + id + "/",
                    GalleryId = id,
                    SourceSite = Site,
                });
            }

            return list;
        }
    }

    /// <summary>
    /// Eigenes Theme auf <c>https://hentaienvy.com/</c>.
    /// Ein Tag: <c>/tag/{slug}/</c> mit <c>?page=</c>. Mehrere Tags: <c>/search/?key=</c>.
    /// Karten: <c>hnv-gallery-card__cover</c>, Cover <c>m*.hentaienvy.com</c>, Galerie <c>/gallery/{id}/</c>.
    /// Die Tagliste <c>/tags/popular/</c> trägt Namen und Badge-Zahl.
    /// </summary>
    public sealed class HentaiEnvyProvider : IScraperProvider, ITagCatalogSource, ISearchPageHint
    {
        public const string Site = "hentaienvy";
        private const string Root = "https://hentaienvy.com/";
        private const int FullPageCap = 800;

        public bool HasFurtherPages { get; private set; }

        private static readonly (string Path, string Kind)[] Roots =
        {
            ("tags", "tag"),
            ("artists", "artist"),
            ("characters", "character"),
            ("parodies", "parody"),
            ("groups", "group"),
        };

        private static readonly Regex GalleryCard = new(
            """<a class="hnv-gallery-card__cover" href="/gallery/(?<id>\d+)/"[^>]*>\s*<img src="(?<cover>https://[^"]+)"[\s\S]{0,1500}?<h2 class="hnv-gallery-card__title"><a href="/gallery/\k<id>/">(?<title>[^<]*)</a>""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex IndexLink = new(
            """href="/(?<kind>tag|artist|character|parody|group|language|category)/(?<slug>[^"#?/]+)/"[^>]*>\s*<span class="hnv-legacy-taxonomy-item__name">(?<name>[^<]*)</span>\s*<span class="hnv-legacy-taxonomy-item__badge">(?<count>\d+)</span>""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly HttpClient _http = SiteHttp.Create();

        public SourceProvider Provider => SourceProvider.HentaiEnvy;
        public string BaseUrl => Root;
        public bool SupportsTagSearch => true;
        public string SiteId => Site;
        public WebSearchViewModel? TagBlacklist { get; set; }

        public string BuildSearchUrl(string query, int page)
        {
            var terms = SiteTerms.Parse(query, TagBlacklist);
            if (terms.Count == 0)
                return Root;
            if (terms.Count == 1 && terms[0].Slug.Length > 0)
            {
                var url = Root + SiteTerms.Segment(terms[0].Kind) + "/" + terms[0].Slug + "/";
                if (page > 1)
                    url += "?page=" + page.ToString(CultureInfo.InvariantCulture);
                return url;
            }

            var search = Root + "search/?key=" + Uri.EscapeDataString(string.Join(' ', terms.ConvertAll(term => term.Name)));
            if (page > 1)
                search += "&page=" + page.ToString(CultureInfo.InvariantCulture);
            return search;
        }

        public async Task<IEnumerable<GalleryResult>> SearchAsync(string query, int page, AppSettings settings, CancellationToken cancellationToken = default)
        {
            _ = settings;
            var terms = SiteTerms.Parse(query, TagBlacklist);
            if (terms.Count == 0)
            {
                HasFurtherPages = false;
                return Array.Empty<GalleryResult>();
            }

            var current = Math.Max(1, page);
            if (current > GalleryPages.SafetyCap)
            {
                HasFurtherPages = false;
                return Array.Empty<GalleryResult>();
            }

            var url = BuildSearchUrl(query, current);
            var html = await GetAsync(url, cancellationToken).ConfigureAwait(false);
            var hits = ReadGalleries(html);
            HasFurtherPages = GalleryPages.Continues(hits.Count, current, html, url);
            return hits;
        }

        public Task<IReadOnlyList<CatalogTag>> GetOnlineTagsAsync(bool fullIndex = false, IProgress<TagDownloadProgress>? progress = null, CancellationToken cancellationToken = default) =>
            CatalogSeed.Collect(Roots, fullIndex ? FullPageCap : 1, hasCount: true, IndexLink, PageUrl, GetAsync, "HentaiEnvy", progress, cancellationToken);

        private static string PageUrl(string path, int page)
        {
            var url = Root + path + "/";
            if (page > 1)
                url += "?page=" + page.ToString(CultureInfo.InvariantCulture);
            return url;
        }

        private Task<string> GetAsync(string url, CancellationToken cancellationToken) =>
            SiteHttp.GetString(_http, url, Root, cancellationToken);

        private List<GalleryResult> ReadGalleries(string html)
        {
            var list = new List<GalleryResult>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in GalleryCard.Matches(html))
            {
                var id = match.Groups["id"].Value;
                if (!seen.Add(id))
                    continue;
                var title = SiteTerms.Clean(match.Groups["title"].Value);
                if (title.Length == 0)
                    continue;
                list.Add(new GalleryResult
                {
                    Title = title,
                    CoverUrl = WebUtility.HtmlDecode(match.Groups["cover"].Value),
                    GalleryUrl = Root + "gallery/" + id + "/",
                    GalleryId = id,
                    SourceSite = Site,
                });
            }

            return list;
        }
    }



    /// <summary>Seitenweise Taglisten. Zuerst <c>/popular/</c>, danach das alphabetische Verzeichnis, wenn Popular nichts liefert.</summary>
    internal static class CatalogSeed
    {
        public static async Task<IReadOnlyList<CatalogTag>> Collect(
            (string Path, string Kind)[] roots,
            int cap,
            bool hasCount,
            Regex links,
            Func<string, int, string> pageUrl,
            Func<string, CancellationToken, Task<string>> fetch,
            string label,
            IProgress<TagDownloadProgress>? progress,
            CancellationToken cancellationToken,
            bool preferPopular = true)
        {
            var bag = new Dictionary<string, CatalogTag>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? blocked = null;
            foreach (var (path, kind) in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var popular = preferPopular ? path + "/popular" : path;
                    var found = await HtmlCatalog.Walk(
                        fetch,
                        page => pageUrl(popular, page),
                        html => Remember(bag, seen, html, kind, hasCount, links),
                        cap,
                        label + ": " + kind,
                        progress,
                        () => bag.Count,
                        cancellationToken).ConfigureAwait(false);
                    if (found == 0 && preferPopular)
                    {
                        await HtmlCatalog.Walk(
                            fetch,
                            page => pageUrl(path, page),
                            html => Remember(bag, seen, html, kind, hasCount, links),
                            cap,
                            label + ": " + kind,
                            progress,
                            () => bag.Count,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (InvalidOperationException ex)
                {
                    blocked = ex.Message;
                }
                catch (HttpRequestException ex)
                {
                    blocked = ex.Message;
                }
            }

            if (bag.Count == 0 && blocked is not null)
                throw new InvalidOperationException(blocked);
            if (bag.Count == 0 && cap > 1)
                throw new InvalidOperationException(label + " lieferte keinen bereinigten Tagkatalog.");

            return bag.Values
                .OrderByDescending(tag => tag.Count)
                .ThenBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static int Remember(Dictionary<string, CatalogTag> bag, HashSet<string> seen, string html, string fallbackKind, bool hasCount, Regex links)
        {
            var fresh = 0;
            foreach (Match match in links.Matches(html))
            {
                var slug = match.Groups["slug"].Value.Trim().Trim('/');
                if (slug.Length == 0 || slug.Contains("page", StringComparison.OrdinalIgnoreCase))
                    continue;
                var name = SiteTerms.Clean(match.Groups["name"].Value);
                if (name.Length == 0)
                    continue;
                var kind = match.Groups["kind"].Value.ToLowerInvariant();
                if (kind is "circle")
                    kind = "group";
                if (kind.Length == 0)
                    kind = fallbackKind;
                if (!seen.Add(kind + "\0" + slug))
                    continue;
                fresh++;
                var count = 1;
                var numbered = hasCount && match.Groups["count"].Success && TryCount(match.Groups["count"].Value, out count);
                TagSanitizer.Add(bag, kind, name, numbered ? count : 1, numbered, slug);
            }

            return fresh;
        }

        private static bool TryCount(string raw, out int count)
        {
            var digits = new string(raw.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out count) && count > 0;
        }
    }
}
